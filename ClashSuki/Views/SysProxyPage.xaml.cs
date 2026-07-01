using ClashSuki.Services;
using ClashSuki.Utilities;
using ClashSuki.ViewModels;
using Microsoft.UI.Xaml.Controls;

namespace ClashSuki.Views;

public sealed partial class SysProxyPage : Page
{
    public SysProxyPage()
    {
        PageBinding.Bind(this, vm => vm.Dashboard);
        InitializeComponent();
        Loaded += SysProxyPage_Loaded;
    }

    private async void SysProxyPage_Loaded(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        if (DataContext is not DashboardViewModel viewModel)
        {
            return;
        }

        await Task.Yield();
        try
        {
            await viewModel.LoadSystemProxySettingsAsync();
        }
        catch (Exception ex)
        {
            viewModel.Runtime.Notifications.Error(
                $"系统代理设置加载失败：{ex.Message}",
                source: LogSources.SystemProxy,
                exception: ex);
        }
    }

    private async void ResetSystemProxySettings_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        if (DataContext is not DashboardViewModel viewModel)
        {
            return;
        }

        if (!await viewModel.Runtime.Notifications.ConfirmAsync(
                XamlRoot,
                "重置系统代理设置",
                "将恢复为默认值并立即保存。",
                "重置并保存",
                "取消",
                LogSources.SystemProxy))
        {
            return;
        }

        await viewModel.ResetSystemProxySettingsCommand.ExecuteAsync(null);
    }

    private async void SysProxy_Toggled(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        if (DataContext is not DashboardViewModel viewModel ||
            sender is not ToggleSwitch toggle ||
            toggle.IsOn == viewModel.Runtime.IsSystemProxyEnabled)
        {
            return;
        }

        try
        {
            toggle.IsEnabled = false;
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
        finally
        {
            toggle.IsEnabled = true;
        }
    }
}
