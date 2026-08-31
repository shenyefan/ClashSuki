using System.Diagnostics;
using System.IO;
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
    // ── subscription-userinfo 头解析正则 ──
    private static readonly Regex SubInfoRegex = new(
        @"(\w+)=(\d+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private readonly RemoteResourceFetchService _fetch = new();

    public void Dispose()
    {
        _fetch.Dispose();
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
        var fetchResult = await _fetch.FetchWithHeadersAsync(
            url,
            RemoteFetchRequest.Create(profile.UserAgent, profile.AuthToken),
            mixedPort,
            cancellationToken);
        var content = fetchResult.Content;
        var headers = fetchResult.Headers;
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
        if (ShouldUpdateAutomaticName(profile))
        {
            profile.Name = InferRemoteName(headers, profile.Url, profile.File, profile.Uid);
            profile.NameCustomized = false;
        }

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
        var settings = await AppSettingsService.LoadAsync(cancellationToken);
        var excludedRootKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!settings.DnsOverrideEnabled)
        {
            excludedRootKeys.Add("dns");
            excludedRootKeys.Add("hosts");
        }

        if (!settings.SnifferOverrideEnabled)
        {
            excludedRootKeys.Add("sniffer");
        }

        return YamlConfigService.MergeWithBase(profileYaml, baseYaml, excludedRootKeys);
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

        var encryptedInputPath = Path.Combine(
            Path.GetTempPath(),
            $"clashsuki-age-{Guid.NewGuid():N}.age");
        try
        {
            // age can read identities from stdin. Keep private keys off disk and only
            // stage the already-encrypted subscription so stdin remains available.
            await File.WriteAllTextAsync(encryptedInputPath, content, cancellationToken);
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
            process.StartInfo.ArgumentList.Add("-");
            process.StartInfo.ArgumentList.Add(encryptedInputPath);

            try
            {
                process.Start();
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException("检测到 Age 加密订阅，但未找到 Age 解密工具，无法解密。", ex);
            }

            using var cancellationRegistration =
                ProcessCancellation.TerminateOnCancellation(process, cancellationToken);
            var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
            try
            {
                var identityText = string.Join(Environment.NewLine, identities) + Environment.NewLine;
                await process.StandardInput.WriteAsync(identityText.AsMemory(), cancellationToken);
            }
            finally
            {
                process.StandardInput.Close();
            }

            await process.WaitForExitAsync(cancellationToken);

            var output = await outputTask;
            var error = await errorTask;
            if (process.ExitCode == 0)
            {
                return output;
            }

            throw new InvalidDataException($"Age 订阅解密失败：{error.Trim()}");
        }
        finally
        {
            if (File.Exists(encryptedInputPath))
            {
                File.Delete(encryptedInputPath);
            }
        }
    }

    private static bool IsAgeArmored(string content) =>
        content.TrimStart('\uFEFF').TrimStart()
            .StartsWith("-----BEGIN AGE ENCRYPTED FILE-----", StringComparison.Ordinal);

    private static string ResolveAgeExecutablePath()
    {
        var bundled = Path.Combine(AppPaths.AssetsDirectory, "Age", "age.exe");
        return File.Exists(bundled)
            ? bundled
            : throw new FileNotFoundException("未找到内置 age.exe。", bundled);
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

    private static string? TryParseContentDispositionFileName(IReadOnlyDictionary<string, string> headers)
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

    private static bool ShouldUpdateAutomaticName(ProfileItem profile)
    {
        if (profile.NameCustomized.HasValue)
        {
            return !profile.NameCustomized.Value;
        }

        var name = profile.Name.Trim();
        if (string.IsNullOrWhiteSpace(name) ||
            name is "未命名" or "远程订阅" ||
            string.Equals(name, profile.Uid, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (Uri.TryCreate(profile.Url, UriKind.Absolute, out var uri) &&
            string.Equals(name, uri.Host, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return !string.IsNullOrWhiteSpace(profile.File) &&
               string.Equals(
                   name,
                   Path.GetFileNameWithoutExtension(profile.File),
                   StringComparison.OrdinalIgnoreCase);
    }

    private static string InferRemoteName(
        IReadOnlyDictionary<string, string> headers,
        string? url,
        string? fileName,
        string uid)
    {
        if (headers.TryGetValue("profile-title", out var title) &&
            TryDecodeProfileTitle(title) is { Length: > 0 } decodedTitle)
        {
            return decodedTitle;
        }

        var dispositionFile = TryParseContentDispositionFileName(headers);
        var dispositionName = Path.GetFileNameWithoutExtension(dispositionFile);
        if (!string.IsNullOrWhiteSpace(dispositionName))
        {
            return dispositionName.Trim();
        }

        if (Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
            !string.IsNullOrWhiteSpace(uri.Host))
        {
            return uri.Host;
        }

        var localName = Path.GetFileNameWithoutExtension(fileName);
        return string.IsNullOrWhiteSpace(localName) ||
               string.Equals(localName, uid, StringComparison.OrdinalIgnoreCase)
            ? "远程订阅"
            : localName.Trim();
    }

    private static string? TryDecodeProfileTitle(string value)
    {
        var title = value.Trim().Trim('"', '\'');
        if (string.IsNullOrWhiteSpace(title))
        {
            return null;
        }

        var encodedWord = Regex.Match(
            title,
            @"^=\?utf-8\?b\?(?<value>[^?]+)\?=$",
            RegexOptions.IgnoreCase);
        if (encodedWord.Success &&
            TryDecodeBase64Text(encodedWord.Groups["value"].Value) is { } mimeDecoded)
        {
            return mimeDecoded;
        }

        if (title.StartsWith("base64:", StringComparison.OrdinalIgnoreCase) &&
            TryDecodeBase64Text(title[7..]) is { } prefixedDecoded)
        {
            return prefixedDecoded;
        }

        var uriDecoded = Uri.UnescapeDataString(title.Replace("+", " ", StringComparison.Ordinal));
        return TryDecodeBase64Text(uriDecoded) ?? uriDecoded.Trim();
    }

    private static string? TryDecodeBase64Text(string value)
    {
        var normalized = value.Trim().Replace('-', '+').Replace('_', '/');
        if (normalized.Length < 8 || normalized.Any(char.IsWhiteSpace))
        {
            return null;
        }

        normalized = normalized.PadRight((normalized.Length + 3) / 4 * 4, '=');
        try
        {
            var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(normalized)).Trim();
            return string.IsNullOrWhiteSpace(decoded) ||
                   decoded.Contains('\uFFFD') ||
                   decoded.Any(char.IsControl)
                ? null
                : decoded;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    internal static string NormalizeProfileFileName(string? fileName, string uid)
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
    private static ProfileExtra? ParseSubscriptionInfo(IReadOnlyDictionary<string, string> headers)
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
