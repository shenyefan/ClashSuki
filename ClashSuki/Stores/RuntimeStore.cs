using CommunityToolkit.Mvvm.ComponentModel;
using ClashSuki.Models;
using ClashSuki.Services;
using ClashSuki.Utilities;
using Microsoft.UI.Xaml.Controls;

namespace ClashSuki.Stores;

public sealed partial class RuntimeStore : ObservableObject
{
    [ObservableProperty] private string connectionText = "检查中";
    [ObservableProperty] private string coreStatusText = "内核启动中";
    [ObservableProperty] private string tunServiceStatusText = "正在检查服务";
    [ObservableProperty] private string tunFirewallDescription = "正在检查服务";
    [ObservableProperty] private string uwpLoopbackDescription = "正在检查服务";
    [ObservableProperty] private string mihomoVersion = "未知";
    [ObservableProperty] private string currentMode = "rule";
    [ObservableProperty] private bool isSystemProxyEnabled;
    [ObservableProperty] private bool isTunEnabled;
    [ObservableProperty] private bool isTunToggleAvailable;
    [ObservableProperty] private bool showTunServiceRepair;
    [ObservableProperty] private bool isTunServiceInstalled;
    [ObservableProperty] private bool isTunServiceReady;
    [ObservableProperty] private string tunServiceInstallActionText = "安装服务";
    [ObservableProperty] private bool showTunServiceInstallAction;
    [ObservableProperty] private bool showTunServiceRepairAction;
    [ObservableProperty] private bool showTunServiceStopAction;
    [ObservableProperty] private bool isAllowLan;
    [ObservableProperty] private string mixedPortText = "mixed --";
    [ObservableProperty] private string apiPortText = "api 9090";
    [ObservableProperty] private string uploadText = "0 B/s";
    [ObservableProperty] private string downloadText = "0 B/s";
    [ObservableProperty] private string uploadTotalText = "0 B";
    [ObservableProperty] private string downloadTotalText = "0 B";
    [ObservableProperty] private string memoryText = "0 MB";
    [ObservableProperty] private string notificationTitle = "";
    [ObservableProperty] private string notificationMessage = "";
    [ObservableProperty] private InfoBarSeverity notificationSeverity = InfoBarSeverity.Informational;
    [ObservableProperty] private long notificationId;
    private IAppNotificationService? _notifications;

    public int MixedPortNumber { get; private set; } = 7890;
    public bool IsRuleMode => CurrentMode.Equals("rule", StringComparison.OrdinalIgnoreCase);
    public bool IsGlobalMode => CurrentMode.Equals("global", StringComparison.OrdinalIgnoreCase);
    public bool IsDirectMode => CurrentMode.Equals("direct", StringComparison.OrdinalIgnoreCase);

    partial void OnCurrentModeChanged(string value)
    {
        OnPropertyChanged(nameof(IsRuleMode));
        OnPropertyChanged(nameof(IsGlobalMode));
        OnPropertyChanged(nameof(IsDirectMode));
    }

    public IAppNotificationService Notifications =>
        _notifications ?? throw new InvalidOperationException("通知服务尚未初始化。");

    internal void AttachNotificationService(IAppNotificationService notifications)
    {
        ArgumentNullException.ThrowIfNull(notifications);
        if (_notifications is not null)
        {
            throw new InvalidOperationException("通知服务不能重复初始化。");
        }

        _notifications = notifications;
    }

    internal void PublishNotification(string title, string message, InfoBarSeverity severity)
    {
        NotificationTitle = title;
        NotificationMessage = message;
        NotificationSeverity = severity;
        if (!string.IsNullOrWhiteSpace(message))
        {
            NotificationId++;
        }
    }

    public void SyncSystemProxyEnabled(bool enabled)
    {
        if (IsSystemProxyEnabled != enabled)
        {
            IsSystemProxyEnabled = enabled;
        }
    }

    public void SyncTunEnabled(bool enabled)
    {
        if (IsTunEnabled != enabled)
        {
            IsTunEnabled = enabled;
        }
    }

    public void SyncAllowLan(bool enabled)
    {
        if (IsAllowLan != enabled)
        {
            IsAllowLan = enabled;
        }
    }

    public void ApplyTunCapability(MihomoServiceStatus status)
    {
        var isPackaged = PackageIdentityService.IsPackaged;
        var needsServiceRecovery = status is MihomoServiceStatus.InstallRequired or
            MihomoServiceStatus.Unavailable;
        IsTunServiceInstalled = status != MihomoServiceStatus.InstallRequired;
        IsTunServiceReady = status == MihomoServiceStatus.Ready;
        TunServiceInstallActionText = status == MihomoServiceStatus.Unavailable
            ? "重新安装服务"
            : "安装服务";
        ShowTunServiceInstallAction = !isPackaged && needsServiceRecovery;
        ShowTunServiceRepairAction = isPackaged;
        ShowTunServiceStopAction = status == MihomoServiceStatus.Ready;
        TunServiceStatusText = status switch
        {
            MihomoServiceStatus.Ready => "服务已就绪",
            MihomoServiceStatus.Stopped => "服务将在需要时启动",
            MihomoServiceStatus.InstallRequired when isPackaged => "服务未注册，请修复应用",
            MihomoServiceStatus.InstallRequired => "服务尚未安装",
            MihomoServiceStatus.Unavailable when isPackaged => "服务不可用，请修复应用",
            MihomoServiceStatus.Unavailable => "服务不可用，请重新安装",
            _ => "服务状态未知"
        };

        var serviceUnavailableDescription = status switch
        {
            MihomoServiceStatus.InstallRequired when isPackaged => "服务未注册，请先修复应用",
            MihomoServiceStatus.InstallRequired => "安装服务后即可使用此功能",
            MihomoServiceStatus.Unavailable when isPackaged => "服务不可用，请先修复应用",
            MihomoServiceStatus.Unavailable => "服务不可用，请重新安装服务",
            _ => null
        };
        TunFirewallDescription = serviceUnavailableDescription ?? "重新创建 Windows 防火墙规则";
        UwpLoopbackDescription = serviceUnavailableDescription ??
            "允许所选 Microsoft Store 应用访问本机代理";

        IsTunToggleAvailable = status is MihomoServiceStatus.Ready or MihomoServiceStatus.Stopped;
        ShowTunServiceRepair = isPackaged && !IsTunToggleAvailable;
    }

    public void ApplyConnected(VersionInfo? version, ConfigSnapshot? config, CoreRunMode runMode, int? processId, bool syncTun = true)
    {
        ConnectionText = "已连接";

        if (version?.Version is { } v)
        {
            MihomoVersion = v;
        }

        CoreStatusText = runMode switch
        {
            CoreRunMode.Service => "服务模式",
            CoreRunMode.Sidecar => processId is { } pid ? $"子进程模式 · PID {pid}" : "子进程模式",
            _ => "未运行"
        };

        if (config is null)
        {
            return;
        }

        CurrentMode = config.Mode ?? "rule";
        if (syncTun)
        {
            SyncTunEnabled(config.Tun?.Enable ?? false);
        }
        SyncAllowLan(config.AllowLan ?? false);

        var port = new[] { config.MixedPort, config.Port }.FirstOrDefault(p => p is > 0);
        if (port is > 0)
        {
            MixedPortNumber = port.Value;
            MixedPortText = $"mixed {port}";
        }
        else
        {
            MixedPortNumber = 0;
            MixedPortText = "mixed 端口冲突";
        }

        ApiPortText = string.IsNullOrWhiteSpace(config.ExternalController)
            ? "api pipe"
            : $"api pipe · ext {ResolveControllerPort(config.ExternalController)}";
    }

    public void ApplyDisconnected(string? error = null)
    {
        ConnectionText = "已断开";
        CoreStatusText = "未运行";
        if (!string.IsNullOrWhiteSpace(error))
        {
            Notifications.Error(error, source: LogSources.Core);
        }
    }

    public void ApplyTraffic(long up, long down)
    {
        UploadText = Formatters.FormatSpeed(up);
        DownloadText = Formatters.FormatSpeed(down);
    }

    public void ApplyMemory(long inUse) => MemoryText = Formatters.FormatBytes(inUse);

    public void ApplyTotals(long? upTotal, long? downTotal)
    {
        if (upTotal.HasValue) UploadTotalText = Formatters.FormatBytes(upTotal.Value);
        if (downTotal.HasValue) DownloadTotalText = Formatters.FormatBytes(downTotal.Value);
    }

    private static string ResolveControllerPort(string? controller)
    {
        if (string.IsNullOrWhiteSpace(controller)) return "9090";
        var text = controller.Trim();
        var colon = text.LastIndexOf(':');
        return colon >= 0 && colon < text.Length - 1 ? text[(colon + 1)..] : "9090";
    }

}
