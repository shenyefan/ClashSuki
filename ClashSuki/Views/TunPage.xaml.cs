using ClashSuki.Services;
using ClashSuki.Utilities;
using ClashSuki.ViewModels;
using Microsoft.UI.Xaml.Controls;

namespace ClashSuki.Views;

public sealed partial class TunPage : Page
{
    public TunPage()
    {
        PageBinding.Bind(this, vm => vm.TunVm);
        InitializeComponent();
        Loaded += TunPage_Loaded;
    }

    private async void TunPage_Loaded(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        if (DataContext is not TunViewModel viewModel)
        {
            return;
        }

        await Task.Yield();
        try
        {
            await viewModel.LoadAsync();
        }
        catch (Exception ex)
        {
            viewModel.Runtime.Notifications.Error(
                $"虚拟网卡配置加载失败：{ex.Message}",
                source: LogSources.Tun,
                exception: ex);
        }
    }

    private async void Reset_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        if (DataContext is not TunViewModel viewModel)
        {
            return;
        }

        if (!await viewModel.Runtime.Notifications.ConfirmAsync(
                XamlRoot,
                "恢复默认虚拟网卡设置",
                "将恢复为默认值并保存到配置。",
                "恢复并保存",
                "取消",
                LogSources.Tun))
        {
            return;
        }

        await viewModel.ResetAndSaveCommand.ExecuteAsync(null);
    }

    private async void UninstallService_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        if (DataContext is not TunViewModel viewModel)
        {
            return;
        }

        if (!await viewModel.Runtime.Notifications.ConfirmAsync(
                XamlRoot,
                "卸载服务",
                "确定卸载 ClashSuki 虚拟网卡服务吗？卸载后需重新安装才能使用 TUN 模式。",
                "卸载",
                "取消",
                LogSources.Service))
        {
            return;
        }

        await viewModel.UninstallServiceCommand.ExecuteAsync(null);
    }

    private async void Tun_Toggled(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        if (DataContext is not TunViewModel viewModel ||
            sender is not ToggleSwitch toggle ||
            toggle.IsOn == viewModel.Runtime.IsTunEnabled)
        {
            return;
        }

        try
        {
            toggle.IsEnabled = false;
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
        finally
        {
            toggle.IsEnabled = true;
        }
    }
}
