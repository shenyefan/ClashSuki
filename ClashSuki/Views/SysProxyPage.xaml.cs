using ClashSuki.ServiceContract;
using ClashSuki.Services;
using ClashSuki.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;

namespace ClashSuki.Views;

public sealed partial class SysProxyPage : Page
{
    private bool _uwpLoopbackDialogShowing;
    private bool _uwpLoopbackSaving;
    private bool _updatingUwpLoopbackSelection;
    private CancellationTokenSource? _uwpLoopbackLoadCts;

    public SysProxyPage()
    {
        PageBinding.Bind(this, vm => vm.Dashboard);
        InitializeComponent();
        Loaded += SysProxyPage_Loaded;
        Unloaded += SysProxyPage_Unloaded;
    }

    private async void SysProxyPage_Loaded(object sender, RoutedEventArgs e)
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

    private async void ResetSystemProxySettings_Click(object sender, RoutedEventArgs e)
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

    private async void SysProxy_Toggled(object sender, RoutedEventArgs e)
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

    private void SysProxyPage_Unloaded(object sender, RoutedEventArgs e)
    {
        _uwpLoopbackLoadCts?.Cancel();
    }

    private async void OpenUwpLoopback_Click(object sender, RoutedEventArgs e)
    {
        if (_uwpLoopbackDialogShowing || DataContext is not DashboardViewModel viewModel)
        {
            return;
        }

        _uwpLoopbackDialogShowing = true;
        var loadCts = new CancellationTokenSource();
        _uwpLoopbackLoadCts = loadCts;
        SetUwpLoopbackLoadingState(isLoading: true);
        try
        {
            var apps = await UwpLoopbackToolService.GetAppsAsync(loadCts.Token);
            loadCts.Token.ThrowIfCancellationRequested();

            UwpLoopbackInfoBar.IsOpen = false;
            _updatingUwpLoopbackSelection = true;
            try
            {
                UwpLoopbackList.ItemsSource = apps;
                UwpLoopbackList.SelectedItems.Clear();
                foreach (var app in apps.Where(static app => app.IsExempt))
                {
                    UwpLoopbackList.SelectedItems.Add(app);
                }
            }
            finally
            {
                _updatingUwpLoopbackSelection = false;
            }
            UpdateUwpLoopbackSelectionState();
            loadCts.Token.ThrowIfCancellationRequested();

            await viewModel.Runtime.Notifications.ShowDialogAsync(
                XamlRoot,
                UwpLoopbackDialog,
                "配置 UWP 应用代理",
                LogSources.Network);
        }
        catch (OperationCanceledException) when (loadCts.IsCancellationRequested)
        {
            // Navigating away cancels native AppContainer enumeration quietly.
        }
        catch (Exception ex)
        {
            viewModel.Runtime.Notifications.Error(
                "UWP 应用列表读取失败",
                source: LogSources.Network,
                exception: ex);
        }
        finally
        {
            if (ReferenceEquals(_uwpLoopbackLoadCts, loadCts))
            {
                _uwpLoopbackLoadCts = null;
            }

            loadCts.Dispose();
            _uwpLoopbackDialogShowing = false;
            SetUwpLoopbackLoadingState(isLoading: false);
        }
    }

    private void UwpLoopbackSelectAll_Click(object sender, RoutedEventArgs e)
    {
        UwpLoopbackList.SelectAll();
    }

    private void UwpLoopbackClearSelection_Click(object sender, RoutedEventArgs e)
    {
        if (UwpLoopbackList.Items.Count > 0)
        {
            UwpLoopbackList.DeselectRange(
                new ItemIndexRange(0, checked((uint)UwpLoopbackList.Items.Count)));
        }
    }

    private void UwpLoopbackList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_updatingUwpLoopbackSelection)
        {
            UpdateUwpLoopbackSelectionState();
        }
    }

    private async void UwpLoopbackDialog_PrimaryButtonClick(
        ContentDialog sender,
        ContentDialogButtonClickEventArgs args)
    {
        if (_uwpLoopbackSaving)
        {
            args.Cancel = true;
            return;
        }

        if (UwpLoopbackList.SelectedItems.Count > ServiceProtocol.MaxLoopbackExemptionCount)
        {
            args.Cancel = true;
            ShowUwpLoopbackInfo(
                InfoBarSeverity.Warning,
                "所选应用过多",
                $"一次最多选择 {ServiceProtocol.MaxLoopbackExemptionCount} 个应用。");
            return;
        }

        var deferral = args.GetDeferral();
        args.Cancel = true;
        _uwpLoopbackSaving = true;
        sender.IsPrimaryButtonEnabled = false;
        UwpLoopbackList.IsEnabled = false;
        UwpLoopbackSelectAllButton.IsEnabled = false;
        UwpLoopbackClearSelectionButton.IsEnabled = false;
        UwpLoopbackSavingRing.IsActive = true;
        UwpLoopbackSavingRing.Visibility = Visibility.Visible;
        ShowUwpLoopbackInfo(
            InfoBarSeverity.Informational,
            "正在保存",
            "正在更新应用回环权限，请稍候。");
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
                    $"已允许 {selectedSids.Length} 个应用访问本机代理",
                    source: LogSources.Network);
            }
        }
        catch (Exception ex)
        {
            ShowUwpLoopbackInfo(
                InfoBarSeverity.Error,
                "保存失败",
                ex.Message);
            DiagnosticLog.WriteAppException(
                LogSources.Network,
                ex,
                "保存 UWP 应用回环配置失败");
        }
        finally
        {
            _uwpLoopbackSaving = false;
            UwpLoopbackSavingRing.IsActive = false;
            UwpLoopbackSavingRing.Visibility = Visibility.Collapsed;
            UwpLoopbackList.IsEnabled = true;
            UpdateUwpLoopbackSelectionState();
            deferral.Complete();
        }
    }

    private void UwpLoopbackDialog_Closing(
        ContentDialog sender,
        ContentDialogClosingEventArgs args)
    {
        if (_uwpLoopbackSaving)
        {
            args.Cancel = true;
        }
    }

    private void SetUwpLoopbackLoadingState(bool isLoading)
    {
        UwpLoopbackCard.IsClickEnabled = !isLoading;
        UwpLoopbackCard.IsActionIconVisible = !isLoading;
        UwpLoopbackLoadingRing.IsActive = isLoading;
        UwpLoopbackLoadingRing.Visibility = isLoading
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void UpdateUwpLoopbackSelectionState()
    {
        var selectedCount = UwpLoopbackList.SelectedItems.Count;
        var totalCount = UwpLoopbackList.Items.Count;
        var exceedsLimit = selectedCount > ServiceProtocol.MaxLoopbackExemptionCount;

        var selectionSummary = $"已选择 {selectedCount} 个，共 {totalCount} 个";
        if (!string.Equals(UwpLoopbackSelectionSummary.Text, selectionSummary, StringComparison.Ordinal))
        {
            UwpLoopbackSelectionSummary.Text = selectionSummary;
            var peer = FrameworkElementAutomationPeer.FromElement(UwpLoopbackSelectionSummary) ??
                       FrameworkElementAutomationPeer.CreatePeerForElement(UwpLoopbackSelectionSummary);
            peer?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
        }

        UwpLoopbackDialog.IsPrimaryButtonEnabled = !exceedsLimit;
        UwpLoopbackSelectAllButton.IsEnabled = selectedCount < totalCount;
        UwpLoopbackClearSelectionButton.IsEnabled = selectedCount > 0;

        if (exceedsLimit)
        {
            ShowUwpLoopbackInfo(
                InfoBarSeverity.Warning,
                "所选应用过多",
                $"一次最多选择 {ServiceProtocol.MaxLoopbackExemptionCount} 个应用。");
        }
        else if (UwpLoopbackInfoBar.IsOpen &&
                 UwpLoopbackInfoBar.Severity == InfoBarSeverity.Warning)
        {
            UwpLoopbackInfoBar.IsOpen = false;
        }
    }

    private void ShowUwpLoopbackInfo(
        InfoBarSeverity severity,
        string title,
        string message)
    {
        UwpLoopbackInfoBar.Severity = severity;
        UwpLoopbackInfoBar.Title = title;
        UwpLoopbackInfoBar.Message = message;
        UwpLoopbackInfoBar.IsOpen = true;
    }
}
