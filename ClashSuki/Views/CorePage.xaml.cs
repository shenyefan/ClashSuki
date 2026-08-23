using ClashSuki.Services;
using ClashSuki.Utilities;
using ClashSuki.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace ClashSuki.Views;

public sealed partial class CorePage : Page
{
    public CorePage()
    {
        PageBinding.Bind(this, vm => vm.CoreSettings);
        InitializeComponent();
        Loaded += CorePage_Loaded;
    }

    private async void CorePage_Loaded(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        if (DataContext is not CoreSettingsViewModel viewModel)
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
                "内核配置加载失败",
                source: LogSources.Core,
                exception: ex);
        }
    }

    private async void ResetConfig_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        if (DataContext is not CoreSettingsViewModel viewModel)
        {
            return;
        }

        if (!await viewModel.Runtime.Notifications.ConfirmAsync(
                XamlRoot,
                "重置内核配置",
                "将恢复内核配置为默认值并保存，随后重启内核。",
                "重置并保存",
                "取消",
                LogSources.Core))
        {
            return;
        }

        await viewModel.ResetConfigCommand.ExecuteAsync(null);
    }

    private async void ApplyCoreRelease_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not CoreSettingsViewModel viewModel)
        {
            return;
        }

        if (viewModel.CoreReleaseIndex == 3 && string.IsNullOrWhiteSpace(viewModel.CoreSpecificVersion))
        {
            viewModel.Runtime.Notifications.Warning(
                "请先选择指定版本",
                source: LogSources.Core,
                writeLog: false);
            return;
        }

        var hasService = PackagedServiceController.IsInstalled();
        var message = hasService
            ? "将下载并替换当前内核，然后重新启动。已安装 ClashSuki 服务，将先停止当前内核。替换内核文件时可能需要管理员权限，若弹出 UAC 请选择「是」。"
            : "将下载并替换当前内核，然后重新启动。若内核文件被占用或写入失败，可能会弹出 UAC 以请求管理员权限。";

        if (!await viewModel.Runtime.Notifications.ConfirmAsync(
                XamlRoot,
                "应用内核",
                message,
                "应用",
                "取消",
                LogSources.Core))
        {
            return;
        }

        await viewModel.ApplyCoreReleaseCommand.ExecuteAsync(null);
    }

    private async void DeleteWebUiPanel_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        if (DataContext is not CoreSettingsViewModel viewModel ||
            sender is not FrameworkElement { Tag: WebUiPanelViewModel panel })
        {
            return;
        }

        if (!await viewModel.Runtime.Notifications.ConfirmAsync(
                XamlRoot,
                "删除 WebUI",
                $"确定删除 WebUI 面板「{panel.Name}」吗？",
                "删除",
                "取消",
                LogSources.Core))
        {
            return;
        }

        viewModel.DeleteWebUiPanelCommand.Execute(panel);
    }
}
