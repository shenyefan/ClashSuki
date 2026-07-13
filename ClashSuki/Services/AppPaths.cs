using System.IO;

namespace ClashSuki.Services;

public static class AppPaths
{
    private static readonly string[] GeoDataFileNames =
    [
        "Country.mmdb",
        "geoip.dat",
        "geosite.dat"
    ];

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
        // Always heal the directory layout. The process may outlive a cleanup or an
        // incomplete first-run initialization even after the one-time bootstrap ran.
        EnsureDirectories();

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

            EnsureDirectories();

            await EnsureTemplateConfigAsync(cancellationToken);
            EnsureManagedCore();
            EnsureGeoDataFiles(DataRoot);
            Volatile.Write(ref _bootstrapped, 1);
        }
        finally
        {
            BootstrapLock.Release();
        }
    }

    public static void EnsureDirectories()
    {
        Directory.CreateDirectory(DataRoot);
        Directory.CreateDirectory(CoreDirectory);
        Directory.CreateDirectory(ConfigDirectory);
        Directory.CreateDirectory(LogDirectory);
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

    public static void EnsureGeoDataFiles(string targetDirectory)
    {
        targetDirectory = Path.GetFullPath(targetDirectory);
        Directory.CreateDirectory(targetDirectory);

        foreach (var fileName in GeoDataFileNames)
        {
            var candidates = new[]
            {
                Path.Combine(DataRoot, fileName),
                Path.Combine(AppContext.BaseDirectory, "Assets", "GeoData", fileName),
                Path.Combine(AppContext.BaseDirectory, fileName)
            };

            var destination = Path.Combine(targetDirectory, fileName);
            var source = candidates.FirstOrDefault(candidate =>
                File.Exists(candidate) &&
                !string.Equals(
                    Path.GetFullPath(candidate),
                    Path.GetFullPath(destination),
                    StringComparison.OrdinalIgnoreCase));

            if (source is null)
            {
                continue;
            }

            if (File.Exists(destination) &&
                File.GetLastWriteTimeUtc(destination) >= File.GetLastWriteTimeUtc(source))
            {
                continue;
            }

            var temporaryPath = $"{destination}.{Guid.NewGuid():N}.tmp";
            try
            {
                File.Copy(source, temporaryPath, overwrite: true);
                File.Move(temporaryPath, destination, overwrite: true);
            }
            finally
            {
                if (File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }
            }
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
            geoip: false
            geoip-code: CN
        """;
}
