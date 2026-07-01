using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ClashSuki.Services;
using ClashSuki.Stores;

namespace ClashSuki.ViewModels;

public sealed partial class TunViewModel : ObservableObject
{
    private readonly AppCoordinator _coordinator;

    [ObservableProperty] private int stackIndex;
    [ObservableProperty] private bool autoRoute = true;
    [ObservableProperty] private bool autoDetectInterface = true;
    [ObservableProperty] private bool strictRoute;
    [ObservableProperty] private string mtu = "9000";
    [ObservableProperty] private string deviceName = "";
    [ObservableProperty] private string dnsHijack = "any:53";
    [ObservableProperty] private string routeExcludeAddress = "";

    public TunViewModel(AppCoordinator coordinator)
    {
        _coordinator = coordinator;
        Runtime = coordinator.Runtime;
    }

    public RuntimeStore Runtime { get; }

    public async Task SetTunAsync(bool enabled) => await _coordinator.SetTunAsync(enabled);

    [RelayCommand]
    private async Task InstallServiceAsync()
    {
        try
        {
            await _coordinator.InstallServiceAsync();
        }
        catch (Exception ex)
        {
            Runtime.Notifications.Error(
                $"服务安装失败：{ex.Message}",
                source: LogSources.Service,
                exception: ex);
        }
    }

    [RelayCommand]
    private async Task UninstallServiceAsync()
    {
        try
        {
            await _coordinator.UninstallServiceAsync();
            Runtime.Notifications.Success("服务已卸载。", source: LogSources.Service);
        }
        catch (Exception ex)
        {
            Runtime.Notifications.Error(
                $"服务卸载失败：{ex.Message}",
                source: LogSources.Service,
                exception: ex);
        }
    }

    public async Task LoadAsync()
    {
        var settings = await _coordinator.LoadTunSettingsAsync();
        StackIndex = settings.Stack.ToLowerInvariant() switch
        {
            "gvisor" => 0,
            "system" => 2,
            _ => 1
        };
        AutoRoute = settings.AutoRoute;
        AutoDetectInterface = settings.AutoDetectInterface;
        StrictRoute = settings.StrictRoute;
        Mtu = settings.Mtu;
        DeviceName = settings.DeviceName;
        DnsHijack = settings.DnsHijack;
        RouteExcludeAddress = settings.RouteExcludeAddress;
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        try
        {
            var stack = StackIndex switch
            {
                0 => "gVisor",
                2 => "system",
                _ => "mixed"
            };
            var tun = new Dictionary<string, object?>
            {
                ["stack"] = stack,
                ["auto-route"] = AutoRoute,
                ["auto-detect-interface"] = AutoDetectInterface,
                ["strict-route"] = StrictRoute,
                ["dns-hijack"] = SplitLines(DnsHijack)
            };
            if (int.TryParse(Mtu, out var mtuVal) && mtuVal > 0)
            {
                tun["mtu"] = mtuVal;
            }
            else
            {
                throw new InvalidOperationException("MTU 必须是大于 0 的数字。");
            }

            tun["device-name"] = DeviceName.Trim();
            tun["route-exclude-address"] = SplitLines(RouteExcludeAddress);

            await _coordinator.SaveTunSettingsAsync(
                new Dictionary<string, object?> { ["tun"] = tun });

            Runtime.Notifications.Success(
                "虚拟网卡配置已保存。",
                source: LogSources.Tun);
        }
        catch (Exception ex)
        {
            Runtime.Notifications.Error(
                $"虚拟网卡配置保存失败：{ex.Message}",
                source: LogSources.Tun,
                exception: ex);
            await LoadAsync();
        }
    }

    [RelayCommand]
    public void ApplyDefaults()
    {
        StackIndex = 0;
        AutoRoute = true;
        AutoDetectInterface = true;
        StrictRoute = false;
        Mtu = "9000";
        DeviceName = "";
        DnsHijack = "any:53";
        RouteExcludeAddress = "";
    }

    [RelayCommand]
    public async Task ResetAndSaveAsync()
    {
        ApplyDefaults();
        await SaveAsync();
    }

    [RelayCommand]
    private async Task SetupFirewallAsync()
    {
        try
        {
            await _coordinator.SetupTunFirewallAsync();
            Runtime.Notifications.Success(
                "防火墙规则已重置。",
                source: LogSources.Tun);
        }
        catch (OperationCanceledException)
        {
            Runtime.Notifications.Info(
                "已取消防火墙规则配置。",
                source: LogSources.Tun);
        }
        catch (Exception ex)
        {
            Runtime.Notifications.Error(
                $"防火墙规则配置失败：{ex.Message}",
                source: LogSources.Network,
                exception: ex);
        }
    }

    private static string[] SplitLines(string text) =>
        text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
