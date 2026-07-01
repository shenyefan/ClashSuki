using ClashSuki.Services;
using ClashSuki.Utilities;
using ClashSuki.ViewModels;
using Microsoft.UI.Xaml.Controls;

namespace ClashSuki.Views;

public sealed partial class SnifferPage : Page
{
    public SnifferPage()
    {
        PageBinding.Bind(this, vm => vm.SnifferVm);
        InitializeComponent();
        Loaded += SnifferPage_Loaded;
    }

    private async void SnifferPage_Loaded(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        if (DataContext is not SnifferViewModel viewModel)
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
                $"嗅探配置加载失败：{ex.Message}",
                source: LogSources.Sniffer,
                exception: ex);
        }
    }

    private async void Reset_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        if (DataContext is not SnifferViewModel viewModel)
        {
            return;
        }

        if (!await viewModel.Runtime.Notifications.ConfirmAsync(
                XamlRoot,
                "恢复默认嗅探设置",
                "将恢复为默认值并保存到配置，随后触发热重载。",
                "恢复并保存",
                "取消",
                LogSources.Sniffer))
        {
            return;
        }

        await viewModel.ResetAndSaveCommand.ExecuteAsync(null);
    }
}
