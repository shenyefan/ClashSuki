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
                "系统代理切换失败",
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
                "虚拟网卡切换失败",
                source: LogSources.Tun,
                exception: ex);
            toggle.IsOn = viewModel.Runtime.IsTunEnabled;
        }
    }

    private async void RepairService_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        if (DataContext is not DashboardViewModel viewModel)
        {
            return;
        }

        if (!await viewModel.Runtime.Notifications.ConfirmAsync(
                XamlRoot,
                "修复服务",
                "程序将停止服务并退出，重新注册应用包和服务后自动启动。是否继续？",
                "修复并重启",
                "取消",
                LogSources.Service))
        {
            return;
        }

        await viewModel.RepairServiceCommand.ExecuteAsync(null);
    }

}
