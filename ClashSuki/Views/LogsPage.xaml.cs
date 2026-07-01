using ClashSuki.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using ClashSuki.ViewModels;

namespace ClashSuki.Views;

public sealed partial class LogsPage : Page
{
    public LogsPage()
    {
        PageBinding.Bind(this, vm => vm.LogsVm);
        InitializeComponent();
    }

    private void LogSourceSelector_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        if (DataContext is LogsViewModel viewModel)
        {
            viewModel.ShowMihomo = sender.SelectedItem == MihomoLogsItem;
        }
    }

    private void LogItem_Tapped(object sender, TappedRoutedEventArgs e)
    {
        if (sender is not Microsoft.UI.Xaml.FrameworkElement { DataContext: LogItemViewModel item })
        {
            return;
        }

        var package = new DataPackage();
        package.SetText(item.DisplayText);
        Clipboard.SetContent(package);
        if (DataContext is LogsViewModel viewModel)
        {
            viewModel.Runtime.Notifications.Success(
                "已复制这一行日志。",
                "复制成功",
                LogSources.Application);
        }
    }

    private void LogDetailsButton_Tapped(object sender, TappedRoutedEventArgs e) =>
        e.Handled = true;

    private async void LogDetails_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: LogItemViewModel item } ||
            !item.HasDetails ||
            DataContext is not LogsViewModel viewModel)
        {
            return;
        }

        var dialog = new ContentDialog
        {
            Title = $"{item.Level} · {item.Source}",
            CloseButtonText = "关闭",
            DefaultButton = ContentDialogButton.Close,
            Content = new ScrollViewer
            {
                Width = 700,
                MaxHeight = 480,
                HorizontalScrollMode = ScrollMode.Disabled,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                VerticalScrollMode = ScrollMode.Auto,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Content = new TextBlock
                {
                    Text = item.Details,
                    FontFamily = new FontFamily("Consolas"),
                    IsTextSelectionEnabled = true,
                    TextWrapping = TextWrapping.Wrap
                }
            }
        };
        dialog.Resources["ContentDialogMinWidth"] = 760d;
        dialog.Resources["ContentDialogMaxWidth"] = 760d;

        await viewModel.Runtime.Notifications.ShowDialogAsync(
            XamlRoot,
            dialog,
            "查看日志详情",
            LogSources.Application);
    }
}
