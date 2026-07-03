using System.IO;

namespace ClashSuki.Services;

public static class AppPaths
{
    public static string DataRoot { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "ClashSuki");

    public static string CoreDirectory { get; } = Path.Combine(DataRoot, "core");
    public static string ConfigDirectory { get; } = Path.Combine(DataRoot, "config");
    public static string LogDirectory { get; } = Path.Combine(DataRoot, "logs");

    public static string ManagedCorePath { get; } = Path.Combine(CoreDirectory, "mihomo.exe");
    public static string RuntimeConfigPath { get; } = Path.Combine(ConfigDirectory, "mihomo-runtime.yaml");
    public static string BaseConfigPath { get; } = Path.Combine(ConfigDirectory, "config-base.yaml");
    public static string SettingsPath { get; } = Path.Combine(DataRoot, "app-settings.json");

    public static async Task BootstrapAsync(CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(CoreDirectory);
        Directory.CreateDirectory(ConfigDirectory);
        Directory.CreateDirectory(LogDirectory);

        await EnsureTemplateConfigAsync(cancellationToken);
        EnsureManagedCore();
    }

    private static async Task EnsureTemplateConfigAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(BaseConfigPath))
        {
            await File.WriteAllTextAsync(BaseConfigPath, DefaultBaseConfig, cancellationToken);
        }

        if (File.Exists(RuntimeConfigPath))
        {
            return;
        }

        var baseConfig = await File.ReadAllTextAsync(BaseConfigPath, cancellationToken);
        await File.WriteAllTextAsync(
            RuntimeConfigPath,
            YamlConfigService.EnsureGlobalConfig(baseConfig),
            cancellationToken);
    }

    private static void EnsureManagedCore()
    {
        if (File.Exists(ManagedCorePath))
        {
            return;
        }

        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "Assets", "Core", "mihomo.exe"),
            Path.Combine(AppContext.BaseDirectory, "mihomo.exe")
        };

        var bundledCore = candidates.FirstOrDefault(File.Exists);
        if (bundledCore is not null)
        {
            File.Copy(bundledCore, ManagedCorePath, overwrite: true);
        }
    }

    private const string DefaultBaseConfig = """
        mixed-port: 7890
        allow-lan: false
        mode: rule
        log-level: info
        ipv6: false
        secret: ""
        external-controller: ""
        external-ui: ""
        unified-delay: true
        tcp-concurrent: true

        tun:
          enable: false
          stack: system
          dns-hijack:
            - any:53
          auto-route: true
          auto-detect-interface: true

        dns:
          enable: true
          ipv6: false
          enhanced-mode: fake-ip
          fake-ip-range: 198.18.0.0/15
          fake-ip-filter:
            - "*.lan"
            - localhost.ptlogin2.qq.com
          nameserver:
            - 114.114.114.114
            - 8.8.8.8
          fallback:
            - tls://1.1.1.1
            - tls://8.8.4.4
          fallback-filter:
            geoip: true
            geoip-code: CN
        """;
}
