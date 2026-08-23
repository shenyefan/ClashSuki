using ClashSuki.Services;
using ClashSuki.Utilities;
using ClashSuki.ViewModels;
using Microsoft.UI.Xaml.Controls;

namespace ClashSuki.Views;

public sealed partial class SettingsPage : Page
{
    public SettingsPage()
    {
        PageBinding.Bind(this, vm => vm.SettingsVm);
        InitializeComponent();
        Loaded += SettingsPage_Loaded;
    }

    private async void SettingsPage_Loaded(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        if (DataContext is not SettingsViewModel vm)
        {
            return;
        }

        await Task.Yield();
        try
        {
            await vm.LoadAsync();
        }
        catch (Exception ex)
        {
            vm.Runtime.Notifications.Error(
                "设置加载失败",
                source: LogSources.Settings,
                exception: ex);
        }
    }

    private async void ResetSettings_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        if (DataContext is not SettingsViewModel vm)
        {
            return;
        }

        if (!await vm.Runtime.Notifications.ConfirmAsync(
                XamlRoot,
                "恢复默认设置",
                "将恢复所有应用设置为默认值并立即保存。",
                "恢复并保存",
                "取消",
                LogSources.Settings))
        {
            return;
        }

        await vm.ResetSettingsCommand.ExecuteAsync(null);
    }

    private async void RestoreLatestBackup_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        if (DataContext is not SettingsViewModel vm)
        {
            return;
        }

        if (!await vm.Runtime.Notifications.ConfirmAsync(
                XamlRoot,
                "恢复备份",
                "确定恢复最新备份吗？当前数据将被覆盖，建议先创建新备份。",
                "恢复",
                "取消",
                LogSources.Settings))
        {
            return;
        }

        await vm.RestoreLatestBackupCommand.ExecuteAsync(null);
    }

    private async void ResetSoftware_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        if (DataContext is not SettingsViewModel vm)
        {
            return;
        }

        if (!await vm.Runtime.Notifications.ConfirmAsync(
                XamlRoot,
                "重置应用",
                "将重置所有应用设置，操作前会自动创建备份。确定继续吗？",
                "重置",
                "取消",
                LogSources.Settings))
        {
            return;
        }

        await vm.ResetSoftwareCommand.ExecuteAsync(null);
    }
}
