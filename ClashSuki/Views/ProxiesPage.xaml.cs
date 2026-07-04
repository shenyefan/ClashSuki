using ClashSuki.Services;
using ClashSuki.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace ClashSuki.Views;

public sealed partial class ProxiesPage : Page
{
    public ProxiesPage()
    {
        PageBinding.Bind(this, vm => vm.ProxiesVm);
        InitializeComponent();
        Unloaded += Page_Unloaded;
    }

    private async void Page_Loaded(object sender, RoutedEventArgs e)
    {
        if (DataContext is ProxiesViewModel viewModel)
        {
            await viewModel.LoadPreferencesAsync();
        }
    }

    private async void Page_Unloaded(object sender, RoutedEventArgs e)
    {
        if (DataContext is not ProxiesViewModel viewModel)
        {
            return;
        }

        try
        {
            await viewModel.SaveGroupExpandStatesAsync(viewModel.Groups);
        }
        catch (Exception ex)
        {
            viewModel.Runtime.Notifications.Warning(
                "代理组展开状态保存失败",
                source: LogSources.Proxy,
                exception: ex);
        }
    }

    private async void NodeCard_Tapped(object sender, TappedRoutedEventArgs e)
    {
        if (DataContext is not ProxiesViewModel viewModel ||
            FindNodeItem(sender as DependencyObject) is not { } node)
        {
            return;
        }

        e.Handled = true;
        await viewModel.SelectNodeCommand.ExecuteAsync(node);
    }

    private async void DelayButton_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not ProxiesViewModel viewModel ||
            FindNodeItem(sender as DependencyObject) is not { } node)
        {
            return;
        }

        await viewModel.TestNodeDelayCommand.ExecuteAsync(node);
    }

    private async void UnfixNode_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not ProxiesViewModel viewModel ||
            sender is not FrameworkElement element)
        {
            return;
        }

        var groupName = element.Tag as string;
        var group = string.IsNullOrWhiteSpace(groupName)
            ? null
            : viewModel.Proxies.FindGroup(groupName);
        if (group is null)
        {
            return;
        }

        await viewModel.UnfixGroupCommand.ExecuteAsync(group);
    }

    private static NodeItemViewModel? FindNodeItem(DependencyObject? source)
    {
        while (source is not null)
        {
            if (source is FrameworkElement { DataContext: NodeItemViewModel node })
            {
                return node;
            }

            source = VisualTreeHelper.GetParent(source);
        }

        return null;
    }
}
