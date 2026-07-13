using ClashSuki.Services;
using ClashSuki.Utilities;
using ClashSuki.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace ClashSuki.Views;

public sealed partial class OverridePage : Page
{
    public OverridePage()
    {
        PageBinding.Bind(this, vm => vm.OverrideVm);
        InitializeComponent();
        Loaded += OverridePage_Loaded;
    }

    private async void OverridePage_Loaded(object sender, RoutedEventArgs e)
    {
        if (DataContext is OverrideViewModel viewModel)
        {
            await viewModel.LoadAsync();
        }
    }

    private async void DnsOverrideSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (DataContext is OverrideViewModel viewModel &&
            sender is ToggleSwitch { IsLoaded: true } toggle &&
            toggle.IsOn != viewModel.DnsOverrideEnabled)
        {
            await viewModel.SetDnsOverrideEnabledAsync(toggle.IsOn);
        }
    }

    private async void SnifferOverrideSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (DataContext is OverrideViewModel viewModel &&
            sender is ToggleSwitch { IsLoaded: true } toggle &&
            toggle.IsOn != viewModel.SnifferOverrideEnabled)
        {
            await viewModel.SetSnifferOverrideEnabledAsync(toggle.IsOn);
        }
    }

    private async void ImportFile_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not OverrideViewModel viewModel)
        {
            return;
        }

        var picker = new FileOpenPicker();
        picker.FileTypeFilter.Add(".yaml");
        picker.FileTypeFilter.Add(".yml");
        picker.FileTypeFilter.Add(".js");
        if (App.CurrentWindow is not null)
        {
            InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(App.CurrentWindow));
        }

        var file = await picker.PickSingleFileAsync();
        if (file is null)
        {
            return;
        }

        await viewModel.ImportFileAsync(file.Path);
    }

    private async void NewYaml_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is OverrideViewModel viewModel)
        {
            await viewModel.NewYamlCommand.ExecuteAsync(null);
        }
    }

    private async void NewJs_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is OverrideViewModel viewModel)
        {
            await viewModel.NewJsCommand.ExecuteAsync(null);
        }
    }

    private async void RefreshRemote_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is OverrideViewModel viewModel &&
            GetItem(sender) is { } item)
        {
            await viewModel.RefreshRemoteCommand.ExecuteAsync(item);
        }
    }

    private async void EditInfo_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not OverrideViewModel viewModel ||
            GetItem(sender) is not { } item)
        {
            return;
        }

        viewModel.BeginEditInfo(item);
        await viewModel.Runtime.Notifications.ShowDialogAsync(
            XamlRoot,
            EditInfoDialog,
            "编辑覆写信息",
            LogSources.Override);
    }

    private async void EditFile_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not OverrideViewModel viewModel ||
            GetItem(sender) is not { } item)
        {
            return;
        }

        await viewModel.BeginEditAsync(item);
        await viewModel.Runtime.Notifications.ShowDialogAsync(
            XamlRoot,
            EditDialog,
            "编辑覆写文件",
            LogSources.Override);
    }

    private async void OpenFile_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is OverrideViewModel viewModel &&
            GetItem(sender) is { } item)
        {
            await viewModel.OpenFileCommand.ExecuteAsync(item);
        }
    }

    private async void ViewLog_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not OverrideViewModel viewModel ||
            GetItem(sender) is not { } item)
        {
            return;
        }

        await viewModel.BeginViewLogAsync(item);
        await viewModel.Runtime.Notifications.ShowDialogAsync(
            XamlRoot,
            LogDialog,
            "查看覆写日志",
            LogSources.Override);
    }

    private async void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not OverrideViewModel viewModel ||
            GetItem(sender) is not { } item)
        {
            return;
        }

        if (!await viewModel.Runtime.Notifications.ConfirmAsync(
                XamlRoot,
                "删除覆写",
                $"确定删除覆写项「{item.Name}」吗？此操作无法撤销。",
                "删除",
                "取消",
                LogSources.Override))
        {
            return;
        }

        await viewModel.DeleteCommand.ExecuteAsync(item);
    }

    private async void EnabledSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (DataContext is OverrideViewModel viewModel &&
            sender is ToggleSwitch { IsLoaded: true } toggle &&
            toggle.DataContext is OverrideItemViewModel item)
        {
            item.Enabled = toggle.IsOn;
            await viewModel.ToggleEnabledCommand.ExecuteAsync(item);
        }
    }

    private async void EditDialog_PrimaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        if (DataContext is not OverrideViewModel viewModel)
        {
            return;
        }

        var deferral = args.GetDeferral();
        try
        {
            args.Cancel = !await viewModel.SaveEditAsync();
        }
        finally
        {
            deferral.Complete();
        }
    }

    private async void EditInfoDialog_PrimaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        if (DataContext is not OverrideViewModel viewModel)
        {
            return;
        }

        var deferral = args.GetDeferral();
        try
        {
            args.Cancel = !await viewModel.SaveInfoAsync();
        }
        finally
        {
            deferral.Complete();
        }
    }

    private static OverrideItemViewModel? GetItem(object sender) =>
        sender is FrameworkElement { Tag: OverrideItemViewModel tagItem } ? tagItem :
        sender is FrameworkElement { DataContext: OverrideItemViewModel contextItem } ? contextItem :
        null;
}
