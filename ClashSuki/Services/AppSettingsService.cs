using System.Text.Json;
using System.Text.Json.Serialization;
using ClashSuki.Utilities;

namespace ClashSuki.Services;

public sealed class AppSettings
{
    [JsonPropertyName("system_proxy_enabled")]
    public bool SystemProxyEnabled { get; set; }

    [JsonPropertyName("system_proxy_bypass")]
    public string SystemProxyBypass { get; set; } = WindowsSystemProxyService.DefaultBypass;

    [JsonPropertyName("system_proxy_host")]
    public string SystemProxyHost { get; set; } = "127.0.0.1";

    [JsonPropertyName("system_proxy_mode")]
    public string SystemProxyMode { get; set; } = "manual";

    [JsonPropertyName("system_proxy_pac_script")]
    public string SystemProxyPacScript { get; set; } = WindowsSystemProxyService.DefaultPacScript;

    [JsonPropertyName("theme")]
    public string Theme { get; set; } = "system";

    [JsonPropertyName("backdrop")]
    public string Backdrop { get; set; } = "mica";

    [JsonPropertyName("close_to_tray")]
    public bool CloseToTray { get; set; } = true;

    [JsonPropertyName("auto_run")]
    public bool AutoRun { get; set; }

    [JsonPropertyName("silent_start")]
    public bool SilentStart { get; set; }

    [JsonPropertyName("proxy_display_mode")]
    public string ProxyDisplayMode { get; set; } = "simple";

    [JsonPropertyName("proxy_display_order")]
    public string ProxyDisplayOrder { get; set; } = "default";

    [JsonPropertyName("proxy_sort_descending")]
    public bool ProxySortDescending { get; set; }

    [JsonPropertyName("auto_close_connection")]
    public bool AutoCloseConnection { get; set; } = true;

    [JsonPropertyName("hot_reload_profile_auto_close_connection")]
    public bool HotReloadProfileAutoCloseConnection { get; set; }

    [JsonPropertyName("use_hot_reload_profile")]
    public bool UseHotReloadProfile { get; set; } = true;

    [JsonPropertyName("test_profile_on_start")]
    public bool TestProfileOnStart { get; set; } = true;

    [JsonPropertyName("group_expand_state")]
    public Dictionary<string, bool> GroupExpandState { get; set; } = new();

    [JsonPropertyName("github_proxy")]
    public string GitHubProxy { get; set; } = "";

    [JsonPropertyName("env_type")]
    public string EnvType { get; set; } = "powershell";

    [JsonPropertyName("user_agent")]
    public string UserAgent { get; set; } = "clash.meta";

    [JsonPropertyName("subscription_timeout")]
    public int SubscriptionTimeout { get; set; } = 30;

    [JsonPropertyName("profile_use_proxy")]
    public bool ProfileUseProxy { get; set; }

    [JsonPropertyName("delay_test_url")]
    public string DelayTestUrl { get; set; } = "https://www.gstatic.com/generate_204";

    [JsonPropertyName("delay_test_concurrency")]
    public int DelayTestConcurrency { get; set; } = 10;

    [JsonPropertyName("delay_test_timeout")]
    public int DelayTestTimeout { get; set; } = 5000;

    [JsonPropertyName("sync_runtime_config_to_gist")]
    public bool SyncRuntimeConfigToGist { get; set; }

    [JsonPropertyName("gist_age_encrypt")]
    public bool GistAgeEncrypt { get; set; }

    [JsonPropertyName("gist_age_recipient")]
    public string GistAgeRecipient { get; set; } = "";

    [JsonPropertyName("gist_age_secret_key")]
    public string GistAgeSecretKey { get; set; } = "";

    [JsonPropertyName("github_token")]
    public string GitHubToken { get; set; } = "";

    [JsonPropertyName("gist_id")]
    public string GistId { get; set; } = "";

    [JsonPropertyName("mihomo_cpu_priority")]
    public string MihomoCpuPriority { get; set; } = "normal";

    [JsonPropertyName("diff_work_dir")]
    public bool DiffWorkDir { get; set; }

    [JsonPropertyName("pause_ssid")]
    [JsonConverter(typeof(StringListJsonConverter))]
    public List<string> PauseSsids { get; set; } = [];

    [JsonPropertyName("disable_dns_on_pause_ssid")]
    public bool DisableDnsOnPauseSsid { get; set; }

    [JsonPropertyName("max_log_days")]
    public int MaxLogDays { get; set; } = 7;

    [JsonPropertyName("max_log_file_size_mb")]
    public int MaxLogFileSizeMb { get; set; } = 10;

    [JsonPropertyName("web_ui_panels")]
    public List<WebUiPanelSetting> WebUiPanels { get; set; } = WebUiPanelSetting.CreateDefaults();

    [JsonPropertyName("core_release_channel")]
    public string CoreReleaseChannel { get; set; } = "latest";

    [JsonPropertyName("core_specific_version")]
    public string CoreSpecificVersion { get; set; } = "";

    [JsonPropertyName("enable_external_controller")]
    public bool EnableExternalController { get; set; }

    [JsonPropertyName("external_controller_address")]
    public string ExternalControllerAddress { get; set; } = MihomoControllerEndpoint.DefaultHttpAddress;
}

public sealed class StringListJsonConverter : JsonConverter<List<string>>
{
    public override List<string> Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
        {
            return [];
        }

        if (reader.TokenType == JsonTokenType.String)
        {
            return ConfigTextCodec.ParseLines(reader.GetString()).ToList();
        }

        if (reader.TokenType != JsonTokenType.StartArray)
        {
            throw new JsonException("列表设置必须是字符串或字符串数组。");
        }

        var values = new List<string>();
        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
        {
            if (reader.TokenType != JsonTokenType.String)
            {
                throw new JsonException("列表设置只能包含字符串。");
            }

            var value = reader.GetString()?.Trim();
            if (!string.IsNullOrWhiteSpace(value))
            {
                values.Add(value);
            }
        }

        return values;
    }

    public override void Write(
        Utf8JsonWriter writer,
        List<string> value,
        JsonSerializerOptions options)
    {
        writer.WriteStartArray();
        foreach (var item in value.Where(item => !string.IsNullOrWhiteSpace(item)))
        {
            writer.WriteStringValue(item.Trim());
        }
        writer.WriteEndArray();
    }
}

public sealed class WebUiPanelSetting
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("url")]
    public string Url { get; set; } = "";

    public static List<WebUiPanelSetting> CreateDefaults() =>
    [
        new()
        {
            Name = "MetaCubeXD",
            Url = "https://metacubex.github.io/metacubexd/#/setup?http=true&hostname=%host&port=%port&secret=%secret"
        },
        new()
        {
            Name = "YACD",
            Url = "https://yacd.metacubex.one/?hostname=%host&port=%port&secret=%secret"
        },
        new()
        {
            Name = "Zashboard",
            Url = "https://board.zash.run.place/#/setup?http=true&hostname=%host&port=%port&secret=%secret"
        }
    ];
}

public static class AppSettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    private static AppSettings? _cached;
    private static readonly SemaphoreSlim _lock = new(1, 1);

    public static async Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            return Clone(await LoadCoreAsync(cancellationToken));
        }
        finally
        {
            _lock.Release();
        }
    }

    public static async Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            await SaveCoreAsync(settings, cancellationToken);
        }
        finally
        {
            _lock.Release();
        }
    }

    public static async Task PatchAsync(Action<AppSettings> patch, CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            var settings = await LoadCoreAsync(cancellationToken);
            patch(settings);
            await SaveCoreAsync(settings, cancellationToken);
        }
        finally
        {
            _lock.Release();
        }
    }

    public static async Task SetSystemProxyEnabledAsync(bool enabled, CancellationToken cancellationToken = default)
    {
        await PatchAsync(s => s.SystemProxyEnabled = enabled, cancellationToken);
    }

    public static void InvalidateCache() => _cached = null;

    private static async Task<AppSettings> LoadCoreAsync(CancellationToken cancellationToken)
    {
        if (_cached is not null)
        {
            return _cached;
        }

        await AppPaths.BootstrapAsync(cancellationToken);
        if (!File.Exists(AppPaths.SettingsPath))
        {
            _cached = new AppSettings();
            return _cached;
        }

        await using var stream = File.OpenRead(AppPaths.SettingsPath);
        _cached = await JsonSerializer.DeserializeAsync<AppSettings>(stream, JsonOptions, cancellationToken)
                  ?? new AppSettings();
        await stream.DisposeAsync();

        if (TryMigrate(_cached))
        {
            await SaveCoreAsync(_cached, cancellationToken);
        }

        return _cached;
    }

    /// <summary>
    /// 一次性设置迁移。返回 true 表示有改动、需要回写。
    /// </summary>
    private static bool TryMigrate(AppSettings settings)
    {
        var changed = false;

        // PAC（auto）兼容性差，很多程序 / WinHTTP 不认 file:// PAC，会出现「开了代理但没走代理」。
        // party / verge 默认都用固定代理服务器（manual）。仅当用户未自定义 PAC 时迁移为 manual。
        if (string.Equals(settings.SystemProxyMode, "auto", StringComparison.OrdinalIgnoreCase) &&
            IsDefaultPacScript(settings.SystemProxyPacScript))
        {
            settings.SystemProxyMode = "manual";
            changed = true;
        }

        return changed;
    }

    private static bool IsDefaultPacScript(string? pacScript) =>
        string.IsNullOrWhiteSpace(pacScript) ||
        string.Equals(
            NormalizeScript(pacScript),
            NormalizeScript(WindowsSystemProxyService.DefaultPacScript),
            StringComparison.Ordinal);

    private static string NormalizeScript(string value) =>
        value.Replace("\r\n", "\n").Replace("\r", "\n").Trim();

    private static async Task SaveCoreAsync(AppSettings settings, CancellationToken cancellationToken)
    {
        await AppPaths.BootstrapAsync(cancellationToken);
        var json = JsonSerializer.Serialize(settings, JsonOptions);
        var tempPath = AppPaths.SettingsPath + ".tmp";
        await File.WriteAllTextAsync(tempPath, json, cancellationToken);
        File.Move(tempPath, AppPaths.SettingsPath, overwrite: true);
        _cached = Clone(settings);
    }

    private static AppSettings Clone(AppSettings settings) =>
        JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(settings, JsonOptions), JsonOptions)
        ?? new AppSettings();
}
