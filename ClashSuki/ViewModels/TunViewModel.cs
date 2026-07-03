using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ClashSuki.Services;
using ClashSuki.Stores;
using ClashSuki.Utilities;
using Microsoft.UI.Xaml;

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
    private async Task RepairServiceAsync()
    {
        try
        {
            await _coordinator.RepairServiceAsync();
            if (Application.Current is App app)
            {
                await app.ShutdownAsync();
            }

            Application.Current.Exit();
        }
        catch (Exception ex)
        {
            Runtime.Notifications.Error(
                $"服务修复失败：{ex.Message}",
                source: LogSources.Service,
                exception: ex);
        }
    }

    [RelayCommand]
    private async Task StopServiceAsync()
    {
        try
        {
            await _coordinator.StopServiceAsync();
            Runtime.Notifications.Success(
                "服务已停止。",
                source: LogSources.Service,
                writeLog: false);
        }
        catch (Exception ex)
        {
            Runtime.Notifications.Error(
                $"服务停止失败：{ex.Message}",
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
        Mtu = settings.Mtu.ToString(System.Globalization.CultureInfo.InvariantCulture);
        DeviceName = settings.DeviceName;
        DnsHijack = ConfigTextCodec.FormatLines(settings.DnsHijack);
        RouteExcludeAddress = ConfigTextCodec.FormatLines(settings.RouteExcludeAddress);
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
            if (!int.TryParse(Mtu, out var mtuValue) || mtuValue <= 0)
            {
                throw new InvalidOperationException("MTU 必须是大于 0 的数字。");
            }

            await _coordinator.SaveTunSettingsAsync(new YamlConfigService.TunSectionSettings(
                stack,
                AutoRoute,
                AutoDetectInterface,
                StrictRoute,
                mtuValue,
                DeviceName.Trim(),
                ConfigTextCodec.ParseLines(DnsHijack),
                ConfigTextCodec.ParseLines(RouteExcludeAddress)));

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
                source: LogSources.Tun,
                writeLog: false);
        }
        catch (Exception ex)
        {
            Runtime.Notifications.Error(
                $"防火墙规则配置失败：{ex.Message}",
                source: LogSources.Network,
                exception: ex);
        }
    }

}
