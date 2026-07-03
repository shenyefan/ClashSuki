using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ClashSuki.Services;
using ClashSuki.Stores;
using ClashSuki.Utilities;
using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text;

namespace ClashSuki.ViewModels;

public sealed partial class CoreSettingsViewModel : ObservableObject
{
    private readonly AppCoordinator _coordinator;

    [ObservableProperty] private bool ipv6;
    [ObservableProperty] private bool unifiedDelay = true;
    [ObservableProperty] private bool tcpConcurrent = true;
    [ObservableProperty] private int logLevelIndex = 3;
    [ObservableProperty] private int findProcessIndex = 1;
    [ObservableProperty] private double mixedPort = 7890;
    [ObservableProperty] private double socksPort = 7891;
    [ObservableProperty] private double httpPort = 7892;
    [ObservableProperty] private double redirPort;
    [ObservableProperty] private double tproxyPort;
    [ObservableProperty] private bool enableExternalController;
    [ObservableProperty] private string externalController = MihomoControllerEndpoint.DefaultHttpAddress;
    [ObservableProperty] private string secret = "";
    [ObservableProperty] private bool allowLan;
    [ObservableProperty] private string lanAllowedIps = "";
    [ObservableProperty] private string lanDisallowedIps = "";
    [ObservableProperty] private string authentication = "";
    [ObservableProperty] private string skipAuthPrefixes = "127.0.0.1/8\n::1/128";
    [ObservableProperty] private bool storeSelected = true;
    [ObservableProperty] private bool storeFakeIp = true;
    [ObservableProperty] private double maxLogDays = 7;
    [ObservableProperty] private double maxLogFileSizeMb = 10;
    [ObservableProperty] private string newWebUiName = "";
    [ObservableProperty] private string newWebUiUrl = "";
    [ObservableProperty] private int coreReleaseIndex;
    [ObservableProperty] private string coreSpecificVersion = "";
    [ObservableProperty] private bool isCoreVersionsLoading;

    public CoreSettingsViewModel(AppCoordinator coordinator)
    {
        _coordinator = coordinator;
        Runtime = coordinator.Runtime;
    }

    public RuntimeStore Runtime { get; }
    public ObservableCollection<WebUiPanelViewModel> WebUiPanels { get; } = [];
    public ObservableCollection<string> CoreVersions { get; } = [];
    public bool IsSpecificCoreSelected => CoreReleaseIndex == 3;

    partial void OnCoreReleaseIndexChanged(int value)
    {
        OnPropertyChanged(nameof(IsSpecificCoreSelected));
        if (value == 3)
        {
            _ = LoadSpecificCoreVersionsAsync(forceRefresh: false);
        }
    }

    public async Task LoadAsync()
    {
        var settings = await _coordinator.LoadCoreSettingsAsync();
        var appSettings = await AppSettingsService.LoadAsync();
        EnableExternalController = appSettings.EnableExternalController;
        Ipv6 = settings.Ipv6;
        UnifiedDelay = settings.UnifiedDelay;
        TcpConcurrent = settings.TcpConcurrent;
        LogLevelIndex = settings.LogLevel.ToLowerInvariant() switch
        {
            "silent" => 0,
            "error" => 1,
            "warning" => 2,
            "debug" => 4,
            _ => 3
        };
        FindProcessIndex = settings.FindProcessMode.ToLowerInvariant() switch
        {
            "strict" => 1,
            "always" => 2,
            _ => 0
        };
        MixedPort = settings.MixedPort;
        SocksPort = settings.SocksPort;
        HttpPort = settings.HttpPort;
        RedirPort = settings.RedirPort;
        TproxyPort = settings.TproxyPort;
        ExternalController = string.IsNullOrWhiteSpace(appSettings.ExternalControllerAddress)
            ? (string.IsNullOrWhiteSpace(settings.ExternalController)
                ? MihomoControllerEndpoint.DefaultHttpAddress
                : settings.ExternalController)
            : appSettings.ExternalControllerAddress;
        Secret = settings.Secret;
        AllowLan = settings.AllowLan;
        LanAllowedIps = ConfigTextCodec.FormatLines(settings.LanAllowedIps);
        LanDisallowedIps = ConfigTextCodec.FormatLines(settings.LanDisallowedIps);
        Authentication = ConfigTextCodec.FormatLines(settings.Authentication);
        SkipAuthPrefixes = ConfigTextCodec.FormatLines(settings.SkipAuthPrefixes);
        StoreSelected = settings.StoreSelected;
        StoreFakeIp = settings.StoreFakeIp;
        MaxLogDays = appSettings.MaxLogDays;
        MaxLogFileSizeMb = appSettings.MaxLogFileSizeMb;
        CoreReleaseIndex = appSettings.CoreReleaseChannel.ToLowerInvariant() switch
        {
            "preview" => 1,
            "smart" => 2,
            "specific" => 3,
            _ => 0
        };
        CoreSpecificVersion = appSettings.CoreSpecificVersion;
        WebUiPanels.Clear();
        var panels = appSettings.WebUiPanels is { Count: > 0 }
            ? appSettings.WebUiPanels
            : WebUiPanelSetting.CreateDefaults();
        foreach (var panel in panels)
        {
            WebUiPanels.Add(new WebUiPanelViewModel { Name = panel.Name, Url = panel.Url });
        }
    }

    private string LogLevel => LogLevelIndex switch
    {
        0 => "silent",
        1 => "error",
        2 => "warning",
        3 => "info",
        4 => "debug",
        _ => "info"
    };

    private string FindProcess => FindProcessIndex switch
    {
        0 => "off",
        1 => "strict",
        2 => "always",
        _ => "off"
    };

    [RelayCommand]
    private async Task RestartCoreAsync() => await _coordinator.RestartCoreAsync();

    [RelayCommand]
    private async Task ApplyCoreReleaseAsync()
    {
        try
        {
            var kind = CoreReleaseKind;
            await _coordinator.ApplyCoreReleaseAsync(kind, CoreSpecificVersion);
            Runtime.Notifications.Success(
                $"已应用 {FormatCoreReleaseLabel(kind)} 内核并启动。",
                source: LogSources.Core);
        }
        catch (OperationCanceledException)
        {
            Runtime.Notifications.Warning(
                "已取消管理员权限请求，内核未替换。",
                source: LogSources.Core,
                writeLog: false);
        }
        catch (Exception ex)
        {
            Runtime.Notifications.Error(
                $"内核应用失败：{ex.Message}",
                source: LogSources.Core,
                exception: ex);
        }
    }

    [RelayCommand]
    private async Task RefreshCoreVersionsAsync() => await LoadSpecificCoreVersionsAsync(forceRefresh: true);

    [RelayCommand]
    private async Task ValidateConfigAsync()
    {
        try
        {
            await _coordinator.ValidateCurrentConfigAsync();
            Runtime.Notifications.Success("配置校验通过。", source: LogSources.Core);
        }
        catch (Exception ex)
        {
            Runtime.Notifications.Error(
                $"配置校验失败：{ex.Message}",
                source: LogSources.Core,
                exception: ex);
        }
    }

    [RelayCommand]
    private async Task UpdateGeoAsync()
    {
        try
        {
            await _coordinator.UpdateGeoAsync();
            Runtime.Notifications.Success("GeoData 已更新。", source: LogSources.Resource);
        }
        catch (Exception ex)
        {
            Runtime.Notifications.Error(
                $"GeoData 更新失败：{ex.Message}",
                source: LogSources.Resource,
                exception: ex);
        }
    }

    [RelayCommand]
    private async Task FlushFakeIpAsync()
    {
        try
        {
            await _coordinator.FlushFakeIpAsync();
            Runtime.Notifications.Success("Fake-IP 缓存已清空。", source: LogSources.Dns);
        }
        catch (Exception ex)
        {
            Runtime.Notifications.Error(
                $"Fake-IP 缓存清理失败：{ex.Message}",
                source: LogSources.Dns,
                exception: ex);
        }
    }

    [RelayCommand]
    private async Task OpenCoreDirectoryAsync() => await _coordinator.OpenCoreDirectoryAsync();

    [RelayCommand]
    private async Task OpenConfigDirectoryAsync() => await _coordinator.OpenConfigDirectoryAsync();

    [RelayCommand]
    private async Task OpenWebUiAsync(WebUiPanelViewModel? panel)
    {
        if (panel is null || string.IsNullOrWhiteSpace(panel.Url))
        {
            return;
        }

        await _coordinator.OpenWebUiAsync(panel.Url);
    }

    [RelayCommand]
    private async Task ToggleAllowLanAsync() => await _coordinator.ToggleAllowLanAsync();

    public async Task SetAllowLanAsync(bool enabled) => await _coordinator.SetAllowLanAsync(enabled);

    [RelayCommand]
    private async Task SaveConfigAsync()
    {
        if (EnableExternalController && string.IsNullOrWhiteSpace(Secret))
        {
            Runtime.Notifications.Warning(
                "开启外部控制时请设置 Secret。",
                source: LogSources.Core,
                writeLog: false);
            return;
        }

        var previousAppSettings = await AppSettingsService.LoadAsync();
        try
        {
            await _coordinator.SaveCoreAppSettingsAsync(
                NormalizePositiveInt(MaxLogDays),
                NormalizePositiveInt(MaxLogFileSizeMb),
                WebUiPanels.Select(panel => new WebUiPanelSetting { Name = panel.Name, Url = panel.Url }).ToList(),
                EnableExternalController);
            await _coordinator.SaveCoreDownloadSettingsAsync(
                CoreReleaseKind,
                CoreSpecificVersion);
            await _coordinator.SaveCoreSettingsAsync(new YamlConfigService.CoreSectionSettings(
                Ipv6,
                UnifiedDelay,
                TcpConcurrent,
                LogLevel,
                FindProcess,
                NormalizePort(MixedPort),
                NormalizePort(SocksPort),
                NormalizePort(HttpPort),
                NormalizePort(RedirPort),
                NormalizePort(TproxyPort),
                NormalizeController(ExternalController),
                Secret.Trim(),
                AllowLan,
                ConfigTextCodec.ParseLines(LanAllowedIps),
                ConfigTextCodec.ParseLines(LanDisallowedIps),
                ConfigTextCodec.ParseLines(Authentication),
                ConfigTextCodec.ParseLines(SkipAuthPrefixes),
                StoreSelected,
                StoreFakeIp), EnableExternalController);
            Runtime.Notifications.Success(
                "核心配置已保存，内核已重启。",
                source: LogSources.Core);
        }
        catch (Exception ex)
        {
            try
            {
                await AppSettingsService.SaveAsync(previousAppSettings);
            }
            catch (Exception rollbackEx)
            {
                DiagnosticLog.WriteAppException("CORE-SETTINGS-ROLLBACK", rollbackEx);
            }

            Runtime.Notifications.Error(
                $"核心配置保存失败：{ex.Message}",
                source: LogSources.Core,
                exception: ex);
            await LoadAsync();
        }
    }

    [RelayCommand]
    private async Task ResetConfigAsync()
    {
        Ipv6 = false;
        UnifiedDelay = true;
        TcpConcurrent = true;
        LogLevelIndex = 3;
        FindProcessIndex = 1;
        MixedPort = 7890;
        SocksPort = 7891;
        HttpPort = 7892;
        RedirPort = 0;
        TproxyPort = 0;
        EnableExternalController = false;
        ExternalController = MihomoControllerEndpoint.DefaultHttpAddress;
        Secret = "";
        AllowLan = false;
        LanAllowedIps = "";
        LanDisallowedIps = "";
        Authentication = "";
        SkipAuthPrefixes = "127.0.0.1/8\n::1/128";
        StoreSelected = true;
        StoreFakeIp = true;
        MaxLogDays = 7;
        MaxLogFileSizeMb = 10;
        CoreReleaseIndex = 0;
        CoreSpecificVersion = "";
        WebUiPanels.Clear();
        foreach (var panel in WebUiPanelSetting.CreateDefaults())
        {
            WebUiPanels.Add(new WebUiPanelViewModel { Name = panel.Name, Url = panel.Url });
        }

        await SaveConfigAsync();
    }

    [RelayCommand]
    private void RandomizeMixedPort() => MixedPort = RandomPort();

    [RelayCommand]
    private void RandomizeSocksPort() => SocksPort = RandomPort();

    [RelayCommand]
    private void RandomizeHttpPort() => HttpPort = RandomPort();

    [RelayCommand]
    private void GenerateSecret() => Secret = GenerateRandomSecret();

    [RelayCommand]
    private void AddWebUiPanel()
    {
        if (string.IsNullOrWhiteSpace(NewWebUiName) || string.IsNullOrWhiteSpace(NewWebUiUrl))
        {
            Runtime.Notifications.Warning(
                "WebUI 名称和地址不能为空。",
                source: LogSources.Core,
                writeLog: false);
            return;
        }

        WebUiPanels.Add(new WebUiPanelViewModel { Name = NewWebUiName.Trim(), Url = NewWebUiUrl.Trim() });
        NewWebUiName = "";
        NewWebUiUrl = "";
    }

    [RelayCommand]
    private void DeleteWebUiPanel(WebUiPanelViewModel? panel)
    {
        if (panel is not null)
        {
            WebUiPanels.Remove(panel);
        }
    }

    [RelayCommand]
    private void RestoreWebUiPanels()
    {
        WebUiPanels.Clear();
        foreach (var panel in WebUiPanelSetting.CreateDefaults())
        {
            WebUiPanels.Add(new WebUiPanelViewModel { Name = panel.Name, Url = panel.Url });
        }
    }

    private static int NormalizePort(double port) =>
        double.IsFinite(port) && port is >= 0 and <= 65535 ? (int)Math.Round(port) : 0;

    private static int NormalizePositiveInt(double value) =>
        double.IsFinite(value) && value > 0 ? (int)Math.Round(value) : 1;

    private static string NormalizeController(string value) =>
        string.IsNullOrWhiteSpace(value) ? MihomoControllerEndpoint.DefaultHttpAddress : value.Trim();

    private async Task LoadSpecificCoreVersionsAsync(bool forceRefresh)
    {
        if (IsCoreVersionsLoading)
        {
            return;
        }

        IsCoreVersionsLoading = true;
        try
        {
            var versions = await _coordinator.LoadCoreSpecificVersionsAsync(forceRefresh);
            CoreVersions.Clear();
            foreach (var version in versions)
            {
                CoreVersions.Add(version);
            }

            if (!string.IsNullOrWhiteSpace(CoreSpecificVersion) &&
                !CoreVersions.Contains(CoreSpecificVersion, StringComparer.OrdinalIgnoreCase))
            {
                CoreVersions.Insert(0, CoreSpecificVersion);
            }

            if (string.IsNullOrWhiteSpace(CoreSpecificVersion) && CoreVersions.Count > 0)
            {
                CoreSpecificVersion = CoreVersions[0];
            }
        }
        catch (Exception ex)
        {
            Runtime.Notifications.Error(
                $"版本列表获取失败：{ex.Message}",
                source: LogSources.Core,
                exception: ex);
        }
        finally
        {
            IsCoreVersionsLoading = false;
        }
    }

    private MihomoCoreReleaseKind CoreReleaseKind => CoreReleaseIndex switch
    {
        1 => MihomoCoreReleaseKind.Preview,
        2 => MihomoCoreReleaseKind.Smart,
        3 => MihomoCoreReleaseKind.Specific,
        _ => MihomoCoreReleaseKind.Latest
    };

    private static string FormatCoreReleaseLabel(MihomoCoreReleaseKind kind) => kind switch
    {
        MihomoCoreReleaseKind.Preview => "预览版",
        MihomoCoreReleaseKind.Smart => "Smart",
        MihomoCoreReleaseKind.Specific => "指定版本",
        _ => "最新版"
    };

    private static int RandomPort() => RandomNumberGenerator.GetInt32(1024, 65536);

    private static string GenerateRandomSecret()
    {
        const string chars = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";
        var builder = new StringBuilder(16);
        for (var i = 0; i < 16; i++)
        {
            builder.Append(chars[RandomNumberGenerator.GetInt32(chars.Length)]);
        }

        return builder.ToString();
    }
}
