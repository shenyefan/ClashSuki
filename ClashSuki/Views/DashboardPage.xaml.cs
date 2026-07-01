using ClashSuki.Services;
using Microsoft.UI.Xaml.Controls;
using ClashSuki.ViewModels;

namespace ClashSuki.Views;

public sealed partial class DashboardPage : Page
{
    public DashboardPage()
    {
        PageBinding.Bind(this, vm => vm.Dashboard);
        InitializeComponent();
    }

    private async void SystemProxy_Toggled(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        if (DataContext is not DashboardViewModel viewModel ||
            sender is not ToggleSwitch toggle ||
            toggle.IsOn == viewModel.Runtime.IsSystemProxyEnabled)
        {
            return;
        }

        try
        {
            await viewModel.SetSystemProxyAsync(toggle.IsOn);
        }
        catch (Exception ex)
        {
            viewModel.Runtime.Notifications.Error(
                $"系统代理切换失败：{ex.Message}",
                source: LogSources.SystemProxy,
                exception: ex);
            toggle.IsOn = viewModel.Runtime.IsSystemProxyEnabled;
        }
    }

    private async void Tun_Toggled(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        if (DataContext is not DashboardViewModel viewModel ||
            sender is not ToggleSwitch toggle ||
            toggle.IsOn == viewModel.Runtime.IsTunEnabled)
        {
            return;
        }

        try
        {
            await viewModel.SetTunAsync(toggle.IsOn);
        }
        catch (Exception ex)
        {
            viewModel.Runtime.Notifications.Error(
                $"虚拟网卡切换失败：{ex.Message}",
                source: LogSources.Tun,
                exception: ex);
            toggle.IsOn = viewModel.Runtime.IsTunEnabled;
        }
    }

}
