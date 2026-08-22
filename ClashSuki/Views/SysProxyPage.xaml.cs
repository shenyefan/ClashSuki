using ClashSuki.Services;
using ClashSuki.Utilities;
using ClashSuki.ViewModels;
using Microsoft.UI.Xaml.Controls;

namespace ClashSuki.Views;

public sealed partial class SysProxyPage : Page
{
    private bool _uwpLoopbackDialogShowing;

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
                "系统代理设置加载失败",
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
                "系统代理切换失败",
                source: LogSources.SystemProxy,
                exception: ex);
            toggle.IsOn = viewModel.Runtime.IsSystemProxyEnabled;
        }
        finally
        {
            toggle.IsEnabled = true;
        }
    }

    private async void OpenUwpLoopback_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        if (_uwpLoopbackDialogShowing || DataContext is not DashboardViewModel viewModel)
        {
            return;
        }

        UwpLoopbackButton.IsLoading = true;
        try
        {
            var apps = await UwpLoopbackToolService.GetAppsAsync();
            UwpLoopbackList.ItemsSource = apps;
            UwpLoopbackList.SelectedItems.Clear();
            foreach (var app in apps.Where(static app => app.IsExempt))
            {
                UwpLoopbackList.SelectedItems.Add(app);
            }

            _uwpLoopbackDialogShowing = true;
            await viewModel.Runtime.Notifications.ShowDialogAsync(
                XamlRoot,
                UwpLoopbackDialog,
                "配置 UWP 应用回环",
                LogSources.Network);
        }
        catch (Exception ex)
        {
            viewModel.Runtime.Notifications.Error(
                "读取 UWP 应用回环配置失败",
                source: LogSources.Network,
                exception: ex);
        }
        finally
        {
            _uwpLoopbackDialogShowing = false;
            UwpLoopbackButton.IsLoading = false;
        }
    }

    private async void UwpLoopbackDialog_PrimaryButtonClick(
        ContentDialog sender,
        ContentDialogButtonClickEventArgs args)
    {
        var deferral = args.GetDeferral();
        args.Cancel = true;
        sender.IsPrimaryButtonEnabled = false;
        try
        {
            var selectedSids = UwpLoopbackList.SelectedItems
                .OfType<UwpLoopbackApp>()
                .Select(static app => app.Sid)
                .ToArray();
            await UwpLoopbackToolService.SetExemptionsAsync(selectedSids);
            args.Cancel = false;

            if (DataContext is DashboardViewModel viewModel)
            {
                viewModel.Runtime.Notifications.Success(
                    $"已保存 {selectedSids.Length} 个 UWP 应用回环豁免",
                    source: LogSources.Network);
            }
        }
        catch (Exception ex)
        {
            if (DataContext is DashboardViewModel viewModel)
            {
                viewModel.Runtime.Notifications.Error(
                    "保存 UWP 应用回环配置失败",
                    source: LogSources.Network,
                    exception: ex);
            }
        }
        finally
        {
            sender.IsPrimaryButtonEnabled = true;
            deferral.Complete();
        }
    }
}
