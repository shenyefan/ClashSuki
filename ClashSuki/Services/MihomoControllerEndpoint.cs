namespace ClashSuki.Services;

public static class MihomoControllerEndpoint
{
    public const string PipeName = "clashsuki-mihomo";
    public const string PipePath = @"\\.\pipe\clashsuki-mihomo";
    public const string DefaultHttpAddress = "127.0.0.1:9090";

    private static readonly string[] RuntimeOnlyRemovedKeys =
    [
        "external-controller-unix",
        "external-controller-tls"
    ];

    /// <summary>解析写入 YAML 的 HTTP 外部控制地址（仅影响对外暴露，不影响本应用 pipe 通信）。</summary>
    public static string ResolveHttpAddress(string? configured) =>
        string.IsNullOrWhiteSpace(configured) ? DefaultHttpAddress : configured.Trim();

    public static string ResolvePersistedExternalController(bool enabled, string? configuredAddress) =>
        enabled ? ResolveHttpAddress(configuredAddress) : "";

    /// <summary>
    /// 按应用设置同步持久化的 HTTP external-controller。
    /// pipe 是运行时实现细节，只在启动内核前写入 Runtime。
    /// </summary>
    public static async Task<bool> ApplyPolicyAsync(CancellationToken cancellationToken = default)
    {
        var appSettings = await AppSettingsService.LoadAsync(cancellationToken);
        var configuredAddress = ResolveStoredHttpAddress(appSettings);
        var effectiveController = ResolvePersistedExternalController(
            appSettings.EnableExternalController,
            configuredAddress);

        var currentController = "";
        if (File.Exists(AppPaths.RuntimeConfigPath))
        {
            var current = await YamlConfigService.LoadCoreSettingsAsync(AppPaths.RuntimeConfigPath, cancellationToken);
            currentController = current.ExternalController ?? "";
        }

        var changed = !string.Equals(
            currentController.Trim(),
            effectiveController.Trim(),
            StringComparison.OrdinalIgnoreCase);

        await YamlConfigService.PersistBasePatchAsync(new Dictionary<string, object?>
        {
            ["external-controller"] = effectiveController
        }, cancellationToken);

        return changed;
    }

    /// <summary>
    /// 生成内核实际加载的运行时配置：HTTP 按开关暴露；pipe 写入 YAML（与 -ext-ctl-pipe 一致）。
    /// </summary>
    public static async Task PrepareRuntimeConfigForCoreAsync(
        CancellationToken cancellationToken = default,
        bool? tunEnabledOverride = null)
    {
        await AppPaths.BootstrapAsync(cancellationToken);
        await ApplyPolicyAsync(cancellationToken);

        if (!File.Exists(AppPaths.RuntimeConfigPath))
        {
            throw new FileNotFoundException("找不到 mihomo 运行时配置文件。", AppPaths.RuntimeConfigPath);
        }

        await PrepareConfigFileForCoreAsync(
            AppPaths.RuntimeConfigPath,
            cancellationToken,
            tunEnabledOverride);
    }

    public static async Task PrepareConfigFileForCoreAsync(
        string configPath,
        CancellationToken cancellationToken = default,
        bool? tunEnabledOverride = null)
    {
        if (!File.Exists(configPath))
        {
            throw new FileNotFoundException("找不到待处理的 mihomo 配置文件。", configPath);
        }

        var appSettings = await AppSettingsService.LoadAsync(cancellationToken);
        var httpController = ResolvePersistedExternalController(
            appSettings.EnableExternalController,
            ResolveStoredHttpAddress(appSettings));

        var patch = new Dictionary<string, object?>
        {
            ["external-controller-pipe"] = PipePath,
        };
        if (File.Exists(AppPaths.BaseConfigPath))
        {
            var baseSettings = await YamlConfigService.LoadCoreSettingsAsync(
                AppPaths.BaseConfigPath,
                cancellationToken);
            patch["secret"] = baseSettings.Secret;
        }

        if (tunEnabledOverride.HasValue)
        {
            patch["tun"] = new Dictionary<string, object?>
            {
                ["enable"] = tunEnabledOverride.Value
            };
        }

        if (string.IsNullOrWhiteSpace(httpController))
        {
            patch["external-controller"] = "";
        }
        else
        {
            patch["external-controller"] = httpController;
        }

        var runtimeYaml = await YamlConfigService.BuildPatchedConfigAsync(configPath, patch, cancellationToken);
        var removeKeys = new List<string>(RuntimeOnlyRemovedKeys);
        if (string.IsNullOrWhiteSpace(httpController))
        {
            removeKeys.Add("external-controller");
        }

        runtimeYaml = YamlConfigService.RemoveRootKeys(runtimeYaml, removeKeys);

        await File.WriteAllTextAsync(configPath, runtimeYaml, cancellationToken);
    }

    private static string ResolveStoredHttpAddress(AppSettings appSettings)
    {
        if (!string.IsNullOrWhiteSpace(appSettings.ExternalControllerAddress))
        {
            return appSettings.ExternalControllerAddress;
        }

        return DefaultHttpAddress;
    }
}
