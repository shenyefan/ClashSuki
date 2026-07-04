using ClashSuki.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
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
                "已复制这一行日志",
                "复制成功",
                LogSources.Application,
                writeLog: false);
        }
    }

    private void LogDetailsButton_Tapped(object sender, TappedRoutedEventArgs e) =>
        e.Handled = true;

    private void LogDetails_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: LogItemViewModel item } ||
            !item.HasDetails)
        {
            return;
        }

        item.IsDetailsExpanded = !item.IsDetailsExpanded;
    }
}
