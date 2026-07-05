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

    /// <summary>
    /// 生成内核实际加载的运行时配置：保留 Base 中的 HTTP 控制器设置，
    /// 并注入仅供应用内部通信使用的 pipe。
    /// </summary>
    public static async Task PrepareRuntimeConfigForCoreAsync(
        CancellationToken cancellationToken = default)
    {
        await AppPaths.BootstrapAsync(cancellationToken);

        if (!File.Exists(AppPaths.RuntimeConfigPath))
        {
            throw new FileNotFoundException("找不到 mihomo 运行时配置文件。", AppPaths.RuntimeConfigPath);
        }

        await PrepareConfigFileForCoreAsync(
            AppPaths.RuntimeConfigPath,
            cancellationToken);
    }

    public static async Task PrepareConfigFileForCoreAsync(
        string configPath,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(configPath))
        {
            throw new FileNotFoundException("找不到待处理的 mihomo 配置文件。", configPath);
        }

        var patch = new Dictionary<string, object?>
        {
            ["external-controller-pipe"] = PipePath,
        };
        var baseSettings = await YamlConfigService.LoadCoreSettingsAsync(
            AppPaths.BaseConfigPath,
            cancellationToken);
        var httpController = baseSettings.ExternalController.Trim();
        patch["secret"] = baseSettings.Secret;
        if (!string.IsNullOrWhiteSpace(httpController))
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
}
