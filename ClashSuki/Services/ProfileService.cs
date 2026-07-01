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
    /// <paramref name="log"/> 为可选日志回调，签名为 (level, message)。
    /// </summary>
    public async Task<ProfileItem> DownloadAsync(
        ProfileItem profile,
        int? mixedPort,
        CancellationToken cancellationToken = default,
        Action<string, string>? log = null)
    {
        var url = profile.Url
                  ?? throw new ArgumentException("订阅 URL 不能为空。");

        if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("订阅 URL 必须以 http:// 或 https:// 开头。");
        }

        log?.Invoke("INFO", $"订阅下载开始；名称={profile.Name}");
        log?.Invoke("INFO", $"订阅下载地址；主机={FormatUrlHostForLog(url)}");
        var appSettings = await AppSettingsService.LoadAsync(cancellationToken);
        var effectiveUserAgent = EffectiveUserAgent(profile, appSettings);
        log?.Invoke("INFO", $"订阅下载参数；User-Agent={effectiveUserAgent}");

        // 三级代理回退：直连 → 本地 mixed 代理 → 失败
        var (content, headers) = await TryDownloadWithFallbackAsync(profile, mixedPort, cancellationToken, log);
        content = await DecryptAgeContentIfNeededAsync(content, profile.AgeSecretKey, cancellationToken, log);

        log?.Invoke("INFO", $"订阅下载完成；内容大小={content.Length:N0} 字节");

        // 解析 subscription-userinfo
        var extra = ParseSubscriptionInfo(headers);
        if (extra is not null)
        {
            log?.Invoke("INFO", $"订阅流量信息；已用={FormatBytes(extra.Used)}；总计={FormatBytes(extra.Total)}" +
                                 (extra.Expire.HasValue
                                     ? $"；到期={DateTimeOffset.FromUnixTimeSeconds(extra.Expire.Value).LocalDateTime:yyyy-MM-dd}"
                                     : "；到期=永不过期"));
        }
        else
        {
            log?.Invoke("INFO", "订阅未返回流量信息；服务器没有 subscription-userinfo 响应头。");
        }

        // 基础 YAML 校验（必须有 proxies 或 proxy-providers）
        log?.Invoke("INFO", "正在校验订阅配置格式。");
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
        log?.Invoke("INFO", $"订阅配置已保存；路径={filePath}");

        // 更新元数据
        profile.Updated = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        profile.Extra = extra;

        return profile;
    }

    /// <summary>将指定 profile 的配置文件（注入全局配置后）写为 mihomo 的运行配置并触发重载。</summary>
    public async Task ActivateAsync(
        ProfileItem profile,
        MihomoApiClient api,
        MihomoCoreManager core,
        CancellationToken cancellationToken = default,
        Action<string, string>? log = null,
        bool useHotReload = true,
        bool closeConnectionsBeforeHotReload = false)
    {
        if (string.IsNullOrWhiteSpace(profile.File))
        {
            throw new InvalidOperationException($"配置项 [{profile.Name}] 没有关联的本地文件。");
        }

        var srcPath = Path.Combine(ProfilesDir, profile.File);
        if (!File.Exists(srcPath))
        {
            throw new FileNotFoundException($"配置文件不存在：{srcPath}");
        }

        // 读取订阅 YAML，与 base 模板合并（全局配置来自 base，代理内容来自订阅）
        var rawYaml = await File.ReadAllTextAsync(srcPath, cancellationToken);
        string mergedYaml;

        if (File.Exists(AppPaths.BaseConfigPath))
        {
            var baseYaml = await File.ReadAllTextAsync(AppPaths.BaseConfigPath, cancellationToken);
            mergedYaml = MergeWithBase(rawYaml, baseYaml);
            log?.Invoke("INFO", $"订阅已与基础模板合并；路径={AppPaths.BaseConfigPath}");
        }
        else
        {
            // 没有 base 时回退到简单注入
            mergedYaml = InjectGlobalConfig(rawYaml);
            log?.Invoke("INFO", "未找到基础模板，正在注入默认全局配置。");
        }

        var tempPath = Path.Combine(Path.GetDirectoryName(AppPaths.ConfigPath)!, "mihomo.profile.tmp.yaml");
        Directory.CreateDirectory(Path.GetDirectoryName(AppPaths.ConfigPath)!);
        await File.WriteAllTextAsync(tempPath, mergedYaml, cancellationToken);
        try
        {
            log?.Invoke("INFO", "正在校验合并后的订阅配置。");
            await core.ValidateConfigAsync(tempPath, cancellationToken);
            log?.Invoke("INFO", "订阅配置校验通过。");
        }
        finally
        {
            try
            {
                if (File.Exists(tempPath))
                {
                    File.Delete(tempPath);
                }
            }
            catch (Exception ex)
            {
                log?.Invoke("WARN", $"删除订阅临时配置失败：{ex.Message}");
            }
        }

        var snapshot = await ConfigFileSnapshot.CaptureAsync(
            [AppPaths.ConfigPath, AppPaths.RuntimeConfigPath],
            cancellationToken);
        var previousConfig = snapshot.GetContent(AppPaths.ConfigPath);
        var previousRuntime = snapshot.GetContent(AppPaths.RuntimeConfigPath);
        var coreWasRunning = core.RunMode != CoreRunMode.NotRunning || core.IsRunning;
        var previousTunEnabled = previousConfig is not null &&
                                 YamlConfigService.IsTunEnabled(previousConfig);

        try
        {
            log?.Invoke("INFO", $"正在写入订阅主配置；路径={AppPaths.ConfigPath}");
            Directory.CreateDirectory(Path.GetDirectoryName(AppPaths.ConfigPath)!);
            await File.WriteAllTextAsync(AppPaths.ConfigPath, mergedYaml, cancellationToken);
            log?.Invoke("INFO", "订阅配置文件写入完成。");

            await MihomoControllerEndpoint.ApplyPolicyAsync(cancellationToken);
            await MihomoControllerEndpoint.PrepareRuntimeConfigForCoreAsync(cancellationToken);
            log?.Invoke("INFO", "订阅外部控制策略已同步。");

            var requireTun = await YamlConfigService.IsTunEnabledAsync(AppPaths.ConfigPath, cancellationToken);

            // Core 正在运行（含 service mode）：热重载；未运行：直接启动
            if (core.RunMode != CoreRunMode.NotRunning && useHotReload)
            {
                log?.Invoke("INFO", "订阅激活：内核运行中，正在尝试热重载。");
                try
                {
                    if (closeConnectionsBeforeHotReload)
                    {
                        await api.CloseAllConnectionsAsync(cancellationToken);
                        log?.Invoke("INFO", "订阅激活：已关闭现有连接。");
                    }

                    await api.ReloadConfigAsync(AppPaths.RuntimeConfigPath, cancellationToken);
                    log?.Invoke("INFO", "订阅激活：热重载成功。");
                }
                catch (Exception ex)
                {
                    log?.Invoke("WARN", $"订阅热重载失败，正在重启内核：{ex.Message}");
                    await core.RestartAsync(requireTun, cancellationToken);
                    log?.Invoke("INFO", "订阅激活：内核重启完成。");
                }
            }
            else if (core.RunMode != CoreRunMode.NotRunning)
            {
                log?.Invoke("INFO", "订阅激活：热重载已关闭，正在重启内核。");
                await core.RestartAsync(requireTun, cancellationToken);
                log?.Invoke("INFO", "订阅激活：内核重启完成。");
            }
            else
            {
                log?.Invoke("INFO", "订阅激活：内核未运行，正在启动。");
                await core.EnsureStartedAsync(requireTun, cancellationToken);
                log?.Invoke("INFO", "订阅激活：内核启动完成。");
            }
        }
        catch (Exception ex)
        {
            log?.Invoke("WARN", $"订阅激活失败，正在恢复之前的配置：{ex.Message}");
            await snapshot.RestoreAsync();

            if (coreWasRunning && previousRuntime is not null)
            {
                try
                {
                    await api.ReloadConfigAsync(AppPaths.RuntimeConfigPath, CancellationToken.None);
                }
                catch (Exception restoreEx)
                {
                    log?.Invoke("WARN", $"恢复订阅配置的热重载失败，正在重启内核：{restoreEx.Message}");
                    await core.RestartAsync(previousTunEnabled, CancellationToken.None);
                }
            }

            throw;
        }
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
        CancellationToken cancellationToken,
        Action<string, string>? log = null)
    {
        var url = profile.Url ?? throw new ArgumentException("订阅 URL 不能为空。");
        var settings = await AppSettingsService.LoadAsync(cancellationToken);
        var timeout = TimeSpan.FromSeconds(Math.Max(1, settings.SubscriptionTimeout));
        var userAgent = EffectiveUserAgent(profile, settings);
        var useProxy = settings.ProfileUseProxy;

        // 1. 直连（复用 _directClient，禁用系统代理）
        if (!useProxy)
        {
            log?.Invoke("INFO", "正在直连下载订阅。");
            try
            {
                var result = await FetchWithClientAsync(_directClient, url, userAgent, profile.AuthToken, timeout, cancellationToken);
                log?.Invoke("INFO", "订阅直连下载成功。");
                return result;
            }
            catch (Exception ex) when (mixedPort.HasValue)
            {
                log?.Invoke("WARN", $"订阅直连下载失败，正在通过本地代理重试；端口={mixedPort}；{ex.Message}");
            }
            catch (Exception ex)
            {
                log?.Invoke("ERROR", $"订阅下载失败，没有可用的代理回退：{ex.Message}");
                throw;
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
        var proxyResult = await FetchWithClientAsync(proxyClient, url, userAgent, profile.AuthToken, timeout, cancellationToken);
        log?.Invoke("INFO", "订阅代理下载成功。");
        return proxyResult;
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
        CancellationToken cancellationToken,
        Action<string, string>? log = null)
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
                log?.Invoke("INFO", "订阅 Age 加密内容已解密。");
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

    private static string FormatBytes(long bytes)
    {
        if (bytes < 1024)         return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
        if (bytes < 1024L * 1024 * 1024) return $"{bytes / 1024.0 / 1024:F2} MB";
        return $"{bytes / 1024.0 / 1024 / 1024:F2} GB";
    }

    private static string FormatUrlHostForLog(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return "无效地址";
        }

        return uri.GetComponents(UriComponents.SchemeAndServer, UriFormat.Unescaped);
    }

    // ──────────────────────────────────────────────
    // Config 合并（对应 Clash Verge 的 config merge 逻辑）
    // ──────────────────────────────────────────────

    // 全局配置键：来自 base 模板，不受订阅 YAML 影响
    private static readonly HashSet<string> GlobalKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "port", "socks-port", "mixed-port", "redir-port", "tproxy-port",
        "allow-lan", "bind-address", "mode", "log-level", "ipv6",
        "lan-allowed-ips", "lan-disallowed-ips", "authentication", "skip-auth-prefixes",
        "external-controller", "external-ui", "secret",
        "tun", "dns", "hosts", "sniffer",
        "geox-url", "geodata-mode", "geodata-loader", "geo-auto-update", "geo-update-interval",
        "profile", "experimental", "unified-delay", "tcp-concurrent",
        "find-process-mode", "global-client-fingerprint"
    };

    // 内容键：来自订阅 YAML，订阅说了算
    private static readonly HashSet<string> ContentKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "proxies", "proxy-providers", "proxy-groups", "rule-providers", "rules"
    };

    /// <summary>
    /// 将订阅 YAML（内容）与 base 模板（全局配置）合并，生成最终的 mihomo 运行配置。
    /// 全局字段取自 base，代理/组/规则取自订阅，同时强制清空 secret。
    /// </summary>
    internal static string MergeWithBase(string subscriptionYaml, string baseYaml)
    {
        return YamlConfigService.MergeWithBase(subscriptionYaml, baseYaml);
    }

    /// <summary>
    /// 兼容旧调用：仅在订阅缺少全局字段时注入默认值（并清空 secret）。
    /// 完整合并请用 MergeWithBase。
    /// </summary>
    internal static string InjectGlobalConfig(string yaml)
    {
        return YamlConfigService.EnsureGlobalConfig(yaml);
    }

    /// <summary>
    /// 修改 YAML 中 tun 块的 enable 值；没有 tun 块时追加一个完整默认块。
    /// 用于把 TUN 开关持久化到 base 模板和运行配置（重启后保持状态）。
    /// </summary>
    internal static string SetTunEnabled(string yaml, bool enabled)
    {
        return YamlConfigService.SetTunEnabled(yaml, enabled);
    }

    /// <summary>把 TUN 开关写入 base 模板与当前运行配置文件。</summary>
    public static async Task PersistTunSettingAsync(bool enabled, CancellationToken cancellationToken = default)
    {
        await YamlConfigService.PersistTunSettingAsync(enabled, cancellationToken);
    }

    public static async Task PersistTunSettingAsync(bool enabled, bool enableDns, CancellationToken cancellationToken = default)
    {
        await YamlConfigService.PersistTunSettingAsync(enabled, enableDns, cancellationToken);
    }

    /// <summary>
    /// 将 YAML 文本按顶层 key 拆分成字典。
    /// 键：顶层字段名（如 "proxies"、"mixed-port"）；值：该字段含子内容的完整文本块。
    /// </summary>
    private static Dictionary<string, string> SplitSections(string yaml)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var lines  = yaml.Replace("\r\n", "\n").Split('\n');

        string? currentKey  = null;
        System.Text.StringBuilder? currentBody = null;

        void Flush()
        {
            if (currentKey is not null && currentBody is not null)
                result[currentKey] = currentBody.ToString().TrimEnd();
        }

        foreach (var line in lines)
        {
            // 顶层 key：从列 0 开始，不是空行、注释、列表项
            if (line.Length > 0 && line[0] != ' ' && line[0] != '\t'
                && line[0] != '#' && line[0] != '-')
            {
                var colon = line.IndexOf(':');
                if (colon > 0)
                {
                    Flush();
                    // 检查 YAML anchor（如 `pr: &a1 {`），忽略 anchor 本身，只取 key
                    currentKey  = line[..colon].Trim();
                    currentBody = new System.Text.StringBuilder();
                    currentBody.AppendLine(line);
                    continue;
                }
            }
            currentBody?.AppendLine(line);
        }
        Flush();
        return result;
    }
}

// ──────────────────────────────────────────────
// File 扩展：支持 overwrite 参数的 CopyAsync
// ──────────────────────────────────────────────
file static class FileEx
{
    public static async Task CopyAsync(string src, string dest, bool overwrite, CancellationToken ct)
    {
        await using var srcStream  = File.OpenRead(src);
        await using var destStream = new FileStream(dest, overwrite ? FileMode.Create : FileMode.CreateNew,
                                                    FileAccess.Write, FileShare.None,
                                                    bufferSize: 81920, useAsync: true);
        await srcStream.CopyToAsync(destStream, ct);
    }
}
