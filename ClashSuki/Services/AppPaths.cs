using System.IO;

namespace ClashSuki.Services;

public static class AppPaths
{
    private static readonly SemaphoreSlim BootstrapLock = new(1, 1);
    private static int _bootstrapped;

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
        if (Volatile.Read(ref _bootstrapped) != 0)
        {
            return;
        }

        await BootstrapLock.WaitAsync(cancellationToken);
        try
        {
            if (_bootstrapped != 0)
            {
                return;
            }

            Directory.CreateDirectory(CoreDirectory);
            Directory.CreateDirectory(ConfigDirectory);
            Directory.CreateDirectory(LogDirectory);

            await EnsureTemplateConfigAsync(cancellationToken);
            EnsureManagedCore();
            Volatile.Write(ref _bootstrapped, 1);
        }
        finally
        {
            BootstrapLock.Release();
        }
    }

    private static async Task EnsureTemplateConfigAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(BaseConfigPath))
        {
            await File.WriteAllTextAsync(BaseConfigPath, DefaultBaseConfig, cancellationToken);
        }

        await YamlConfigService.NormalizeBaseFileAsync(BaseConfigPath, cancellationToken);

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
