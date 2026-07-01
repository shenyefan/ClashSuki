using ClashSuki.Services;
using ClashSuki.Utilities;
using ClashSuki.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace ClashSuki.Views;

public sealed partial class ConnectionsPage : Page
{
    public ConnectionsPage()
    {
        PageBinding.Bind(this, vm => vm.ConnectionsVm);
        InitializeComponent();
    }

    private void ConnectionViewSelector_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        if (DataContext is ConnectionsViewModel viewModel)
        {
            viewModel.SetShowClosed(sender.SelectedItem == ClosedConnectionsItem);
        }
    }

    private void ConnectionsListView_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        args.ItemContainer.HorizontalContentAlignment = HorizontalAlignment.Stretch;
    }

    private async void CloseAllConnections_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not ConnectionsViewModel viewModel)
        {
            return;
        }

        if (!await viewModel.Runtime.Notifications.ConfirmAsync(
                XamlRoot,
                "关闭全部连接",
                "确定关闭所有活动连接吗？",
                "关闭全部",
                "取消",
                LogSources.Connection))
        {
            return;
        }

        await viewModel.CloseAllConnectionsCommand.ExecuteAsync(null);
    }
}
