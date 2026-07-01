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
    /// 按应用设置同步 external-controller；pipe 与 Verge 一致写入 YAML + CLI 双通道。
    /// </summary>
    public static async Task<bool> ApplyPolicyAsync(CancellationToken cancellationToken = default)
    {
        var appSettings = await AppSettingsService.LoadAsync(cancellationToken);
        var configuredAddress = ResolveStoredHttpAddress(appSettings);
        var effectiveController = ResolvePersistedExternalController(
            appSettings.EnableExternalController,
            configuredAddress);

        var currentController = "";
        if (File.Exists(AppPaths.ConfigPath))
        {
            var current = await YamlConfigService.LoadCoreSettingsAsync(AppPaths.ConfigPath, cancellationToken);
            currentController = current.ExternalController ?? "";
        }

        var changed = !string.Equals(
            currentController.Trim(),
            effectiveController.Trim(),
            StringComparison.OrdinalIgnoreCase);

        await YamlConfigService.PersistPatchAsync(new Dictionary<string, object?>
        {
            ["external-controller"] = effectiveController,
            ["external-controller-pipe"] = PipePath,
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

        var sourcePath = File.Exists(AppPaths.ConfigPath)
            ? AppPaths.ConfigPath
            : AppPaths.BaseConfigPath;
        if (!File.Exists(sourcePath))
        {
            throw new FileNotFoundException("找不到 mihomo 配置文件。", sourcePath);
        }

        var appSettings = await AppSettingsService.LoadAsync(cancellationToken);
        var httpController = ResolvePersistedExternalController(
            appSettings.EnableExternalController,
            ResolveStoredHttpAddress(appSettings));

        var patch = new Dictionary<string, object?>
        {
            ["external-controller-pipe"] = PipePath,
        };
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

        var runtimeYaml = await YamlConfigService.BuildPatchedConfigAsync(sourcePath, patch, cancellationToken);
        var removeKeys = new List<string>(RuntimeOnlyRemovedKeys);
        if (string.IsNullOrWhiteSpace(httpController))
        {
            removeKeys.Add("external-controller");
        }

        runtimeYaml = YamlConfigService.RemoveRootKeys(runtimeYaml, removeKeys);

        Directory.CreateDirectory(Path.GetDirectoryName(AppPaths.RuntimeConfigPath)!);
        await File.WriteAllTextAsync(AppPaths.RuntimeConfigPath, runtimeYaml, cancellationToken);
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
