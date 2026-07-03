using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ClashSuki.Models;

namespace ClashSuki.Services;

/// <summary>
/// 订阅配置文件管理服务，对应 Clash Verge 的 PrfItem + profile.rs 逻辑。
/// 负责：增删改查配置项、远程下载（含流量解析）、切换激活配置。
/// </summary>
public sealed class ProfileService : IDisposable
{
    // ── 默认 User-Agent，与 mihomo core 保持一致 ──
    private const string DefaultUserAgent = "clash.meta";

    // ── subscription-userinfo 头解析正则 ──
    private static readonly Regex SubInfoRegex = new(
        @"(\w+)=(\d+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    // 直连用 HttpClient（禁用系统代理）
    private readonly HttpClient _directClient = new(
        new HttpClientHandler { UseProxy = false }, disposeHandler: true)
    {
        Timeout = TimeSpan.FromSeconds(30)
    };

    public void Dispose()
    {
        _directClient.Dispose();
    }

    // ──────────────────────────────────────────────
    // 数据目录布局
    // ──────────────────────────────────────────────
    private static string ProfilesDir => Path.Combine(AppPaths.DataRoot, "profiles");
    private static string ProfilesConfigPath => Path.Combine(ProfilesDir, "profiles.json");

    // ──────────────────────────────────────────────
    // 读取 & 保存
    // ──────────────────────────────────────────────

    public async Task<ProfilesConfig> LoadAsync(CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(ProfilesDir);

        if (!File.Exists(ProfilesConfigPath))
        {
            return new ProfilesConfig();
        }

        await using var stream = File.OpenRead(ProfilesConfigPath);
        return await JsonSerializer.DeserializeAsync<ProfilesConfig>(stream, JsonOpts, cancellationToken)
               ?? new ProfilesConfig();
    }

    public async Task SaveAsync(ProfilesConfig config, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(ProfilesDir);
        var json = JsonSerializer.Serialize(config, JsonOpts);
        await File.WriteAllTextAsync(ProfilesConfigPath, json, cancellationToken);
    }

    // ──────────────────────────────────────────────
    // 下载远程订阅
    // ──────────────────────────────────────────────

    /// <summary>
    /// 从 URL 下载订阅配置，解析 subscription-userinfo 头获取流量信息，
    /// 保存到 profiles 目录，返回更新后的 ProfileItem。
    /// </summary>
    public async Task<ProfileItem> DownloadAsync(
        ProfileItem profile,
        int? mixedPort,
        CancellationToken cancellationToken = default)
    {
        var url = profile.Url
                  ?? throw new ArgumentException("订阅 URL 不能为空。");

        if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("订阅 URL 必须以 http:// 或 https:// 开头。");
        }

        // 三级代理回退：直连 → 本地 mixed 代理 → 失败
        var (content, headers) = await TryDownloadWithFallbackAsync(profile, mixedPort, cancellationToken);
        content = await DecryptAgeContentIfNeededAsync(content, profile.AgeSecretKey, cancellationToken);

        // 解析 subscription-userinfo
        var extra = ParseSubscriptionInfo(headers);

        // 基础 YAML 校验（必须有 proxies 或 proxy-providers）
        if (!content.Contains("proxies:", StringComparison.OrdinalIgnoreCase) &&
            !content.Contains("proxy-providers:", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("下载内容不是有效的 Clash 配置：缺少 proxies 或 proxy-providers。");
        }

        // 保存配置文件
        Directory.CreateDirectory(ProfilesDir);
        var filename = string.IsNullOrWhiteSpace(profile.File)
            ? TryParseContentDispositionFileName(headers) ?? $"{profile.Uid}.yaml"
            : profile.File.Trim();
        profile.File = NormalizeProfileFileName(filename, profile.Uid);
        var filePath = Path.Combine(ProfilesDir, profile.File);
        await File.WriteAllTextAsync(filePath, content, cancellationToken);

        // 更新元数据
        profile.Updated = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        profile.Extra = extra;

        return profile;
    }

    /// <summary>将指定 profile 的配置文件（注入全局配置后）写为 mihomo 的运行配置并触发重载。</summary>
    public async Task<string> BuildRuntimeYamlAsync(
        ProfileItem profile,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(profile.File))
        {
            throw new InvalidOperationException($"配置项 [{profile.Name}] 没有关联的本地文件。");
        }

        var profilePath = Path.Combine(ProfilesDir, profile.File);
        if (!File.Exists(profilePath))
        {
            throw new FileNotFoundException("订阅配置文件不存在。", profilePath);
        }

        var profileYaml = await File.ReadAllTextAsync(profilePath, cancellationToken);
        if (!File.Exists(AppPaths.BaseConfigPath))
        {
            return YamlConfigService.EnsureGlobalConfig(profileYaml);
        }

        var baseYaml = await File.ReadAllTextAsync(AppPaths.BaseConfigPath, cancellationToken);
        return YamlConfigService.MergeWithBase(profileYaml, baseYaml);
    }

    public async Task<ProfileItem> ImportLocalAsync(
        ProfileItem profile,
        string content,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            content = "# ClashSuki local profile\nproxies: []\nproxy-groups: []\nrules: []\n";
        }

        Directory.CreateDirectory(ProfilesDir);
        profile.Type = "local";
        profile.File = NormalizeProfileFileName(profile.File, profile.Uid);
        var path = Path.Combine(ProfilesDir, profile.File);
        await File.WriteAllTextAsync(path, content, cancellationToken);
        profile.Updated = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        return profile;
    }

    /// <summary>删除 profile 及其关联的本地文件。</summary>
    public void Delete(ProfileItem profile)
    {
        if (!string.IsNullOrWhiteSpace(profile.File))
        {
            var path = Path.Combine(ProfilesDir, profile.File);
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    // ──────────────────────────────────────────────
    // 私有辅助
    // ──────────────────────────────────────────────

    private async Task<(string Content, Dictionary<string, string> Headers)> TryDownloadWithFallbackAsync(
        ProfileItem profile,
        int? mixedPort,
        CancellationToken cancellationToken)
    {
        var url = profile.Url ?? throw new ArgumentException("订阅 URL 不能为空。");
        var settings = await AppSettingsService.LoadAsync(cancellationToken);
        var timeout = TimeSpan.FromSeconds(Math.Max(1, settings.SubscriptionTimeout));
        var userAgent = EffectiveUserAgent(profile, settings);
        var useProxy = settings.ProfileUseProxy;

        // 1. 直连（复用 _directClient，禁用系统代理）
        if (!useProxy)
        {
            try
            {
                return await FetchWithClientAsync(
                    _directClient,
                    url,
                    userAgent,
                    profile.AuthToken,
                    timeout,
                    cancellationToken);
            }
            catch (Exception) when (mixedPort.HasValue)
            {
            }
        }

        if (!mixedPort.HasValue)
        {
            throw new InvalidOperationException("未获取到 mixed-port，无法通过代理下载订阅。");
        }

        using var proxyHandler = new HttpClientHandler
        {
            Proxy    = new System.Net.WebProxy($"http://127.0.0.1:{mixedPort}"),
            UseProxy = true
        };
        using var proxyClient = new HttpClient(proxyHandler, disposeHandler: false)
        {
            Timeout = timeout
        };
        return await FetchWithClientAsync(
            proxyClient,
            url,
            userAgent,
            profile.AuthToken,
            timeout,
            cancellationToken);
    }

    private static string EffectiveUserAgent(ProfileItem profile, AppSettings settings) =>
        !string.IsNullOrWhiteSpace(profile.UserAgent)
            ? profile.UserAgent.Trim()
            : string.IsNullOrWhiteSpace(settings.UserAgent)
                ? DefaultUserAgent
                : settings.UserAgent.Trim();

    private static async Task<(string Content, Dictionary<string, string> Headers)> FetchWithClientAsync(
        HttpClient client,
        string url,
        string? userAgent,
        string? authToken,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("User-Agent", userAgent ?? DefaultUserAgent);
        request.Headers.TryAddWithoutValidation("Accept", "*/*");
        request.Headers.TryAddWithoutValidation("Accept-Encoding", "identity");
        if (!string.IsNullOrWhiteSpace(authToken))
        {
            request.Headers.TryAddWithoutValidation("Authorization", authToken);
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);
        using var response = await client.SendAsync(request, timeoutCts.Token);
        response.EnsureSuccessStatusCode();

        var content = await response.Content.ReadAsStringAsync(cancellationToken);

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, values) in response.Headers)
            headers[key] = string.Join(", ", values);
        foreach (var (key, values) in response.Content.Headers)
            headers[key] = string.Join(", ", values);

        return (content, headers);
    }

    private static async Task<string> DecryptAgeContentIfNeededAsync(
        string content,
        string? ageSecretKey,
        CancellationToken cancellationToken)
    {
        if (!IsAgeArmored(content))
        {
            return content;
        }

        var identities = ParseAgeSecretKeys(ageSecretKey);
        if (identities.Count == 0)
        {
            throw new InvalidDataException("该订阅为 age 加密内容，需要填写有效的 age secret key。");
        }

        var keyPath = Path.Combine(Path.GetTempPath(), $"clashsuki-age-{Guid.NewGuid():N}.txt");
        try
        {
            await File.WriteAllTextAsync(keyPath, string.Join(Environment.NewLine, identities), cancellationToken);
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = ResolveAgeExecutablePath(),
                    UseShellExecute = false,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardInputEncoding = Encoding.UTF8,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8
                }
            };

            process.StartInfo.ArgumentList.Add("--decrypt");
            process.StartInfo.ArgumentList.Add("--identity");
            process.StartInfo.ArgumentList.Add(keyPath);

            try
            {
                process.Start();
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException("检测到 Age 加密订阅，但未找到 Age 解密工具，无法解密。", ex);
            }

            var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.StandardInput.WriteAsync(content.AsMemory(), cancellationToken);
            process.StandardInput.Close();
            await process.WaitForExitAsync(cancellationToken);

            var output = await outputTask;
            if (process.ExitCode == 0)
            {
                return output;
            }

            var error = await errorTask;
            throw new InvalidDataException($"Age 订阅解密失败：{error.Trim()}");
        }
        finally
        {
            if (File.Exists(keyPath))
            {
                File.Delete(keyPath);
            }
        }
    }

    private static bool IsAgeArmored(string content) =>
        content.TrimStart('\uFEFF').TrimStart()
            .StartsWith("-----BEGIN AGE ENCRYPTED FILE-----", StringComparison.Ordinal);

    private static string ResolveAgeExecutablePath()
    {
        var bundled = Path.Combine(AppContext.BaseDirectory, "Assets", "Age", "age.exe");
        return File.Exists(bundled) ? bundled : "age";
    }

    private static List<string> ParseAgeSecretKeys(string? ageSecretKey)
    {
        if (string.IsNullOrWhiteSpace(ageSecretKey))
        {
            return [];
        }

        return ageSecretKey
            .Split(new[] { '\r', '\n', '\t', ' ', ',' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(key => key.StartsWith("AGE-SECRET-KEY-1", StringComparison.OrdinalIgnoreCase) ||
                          key.StartsWith("AGE-SECRET-KEY-PQ-1", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string? TryParseContentDispositionFileName(Dictionary<string, string> headers)
    {
        if (!headers.TryGetValue("content-disposition", out var value))
        {
            return null;
        }

        var utf8Match = Regex.Match(value, "filename\\*=.*?''(?<name>[^;]+)", RegexOptions.IgnoreCase);
        if (utf8Match.Success)
        {
            return Uri.UnescapeDataString(utf8Match.Groups["name"].Value.Trim('"', '\''));
        }

        var match = Regex.Match(value, "filename=(?<name>[^;]+)", RegexOptions.IgnoreCase);
        return match.Success ? match.Groups["name"].Value.Trim('"', '\'') : null;
    }

    private static string NormalizeProfileFileName(string? fileName, string uid)
    {
        fileName = string.IsNullOrWhiteSpace(fileName) ? $"{uid}.yaml" : Path.GetFileName(fileName.Trim());
        foreach (var invalid in Path.GetInvalidFileNameChars())
        {
            fileName = fileName.Replace(invalid, '_');
        }

        var extension = Path.GetExtension(fileName);
        if (!extension.Equals(".yaml", StringComparison.OrdinalIgnoreCase) &&
            !extension.Equals(".yml", StringComparison.OrdinalIgnoreCase))
        {
            fileName += ".yaml";
        }

        return string.IsNullOrWhiteSpace(fileName) ? $"{uid}.yaml" : fileName;
    }

    /// <summary>
    /// 解析 subscription-userinfo 响应头。
    /// 格式：upload=1234567; download=2345678; total=10000000000; expire=1735689600
    /// </summary>
    private static ProfileExtra? ParseSubscriptionInfo(Dictionary<string, string> headers)
    {
        if (!headers.TryGetValue("subscription-userinfo", out var info))
        {
            return null;
        }

        var dict = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        foreach (Match m in SubInfoRegex.Matches(info))
        {
            if (long.TryParse(m.Groups[2].Value, out var value))
            {
                dict[m.Groups[1].Value] = value;
            }
        }

        if (dict.Count == 0) return null;

        return new ProfileExtra
        {
            Upload   = dict.GetValueOrDefault("upload"),
            Download = dict.GetValueOrDefault("download"),
            Total    = dict.GetValueOrDefault("total"),
            Expire   = dict.TryGetValue("expire", out var exp) ? exp : null
        };
    }

}
