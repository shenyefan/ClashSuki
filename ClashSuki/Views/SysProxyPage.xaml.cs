using ClashSuki.PrivilegedOperations;
using ClashSuki.Services;
using ClashSuki.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using System.ComponentModel;

namespace ClashSuki.Views;

public sealed partial class SysProxyPage : Page
{
    private bool _uwpLoopbackDialogShowing;
    private bool _uwpLoopbackSaving;
    private bool _updatingUwpLoopbackChecks;
    private IReadOnlyList<UwpLoopbackSelectionItem> _uwpLoopbackItems = [];
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
            _updatingUwpLoopbackChecks = true;
            try
            {
                _uwpLoopbackItems = apps
                    .Select(static app => new UwpLoopbackSelectionItem(app))
                    .ToArray();
                UwpLoopbackList.ItemsSource = _uwpLoopbackItems;
            }
            finally
            {
                _updatingUwpLoopbackChecks = false;
            }
            UpdateUwpLoopbackSelectionState();
            loadCts.Token.ThrowIfCancellationRequested();

            await viewModel.Runtime.Notifications.ShowDialogAsync(
                XamlRoot,
                UwpLoopbackDialog,
                "配置商店应用代理",
                LogSources.Network);
        }
        catch (OperationCanceledException) when (loadCts.IsCancellationRequested)
        {
            // Navigating away cancels native AppContainer enumeration quietly.
        }
        catch (Exception ex)
        {
            viewModel.Runtime.Notifications.Error(
                "商店应用列表读取失败",
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
        SetAllUwpLoopbackChecks(isChecked: true);
    }

    private void UwpLoopbackClearSelection_Click(object sender, RoutedEventArgs e)
    {
        SetAllUwpLoopbackChecks(isChecked: false);
    }

    private void UwpLoopbackCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (sender is CheckBox { Tag: UwpLoopbackSelectionItem item } checkBox)
        {
            item.IsSelected = checkBox.IsChecked is true;
        }

        if (!_updatingUwpLoopbackChecks)
        {
            UpdateUwpLoopbackSelectionState();
        }
    }

    private void SetAllUwpLoopbackChecks(bool isChecked)
    {
        _updatingUwpLoopbackChecks = true;
        try
        {
            foreach (var item in _uwpLoopbackItems)
            {
                item.IsSelected = isChecked;
            }
        }
        finally
        {
            _updatingUwpLoopbackChecks = false;
        }

        UpdateUwpLoopbackSelectionState();
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

        if (_uwpLoopbackItems.Count(static item => item.IsSelected) >
            LoopbackExemptionPolicy.MaxExemptionCount)
        {
            args.Cancel = true;
            ShowUwpLoopbackInfo(
                InfoBarSeverity.Warning,
                "所选应用过多",
                $"一次最多选择 {LoopbackExemptionPolicy.MaxExemptionCount} 个应用。");
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
            var selectedSids = _uwpLoopbackItems
                .Where(static item => item.IsSelected)
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
        var selectedCount = _uwpLoopbackItems.Count(static item => item.IsSelected);
        var totalCount = _uwpLoopbackItems.Count;
        var exceedsLimit = selectedCount > LoopbackExemptionPolicy.MaxExemptionCount;

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
                $"一次最多选择 {LoopbackExemptionPolicy.MaxExemptionCount} 个应用。");
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

public sealed class UwpLoopbackSelectionItem : INotifyPropertyChanged
{
    private bool _isSelected;

    public UwpLoopbackSelectionItem(UwpLoopbackApp app)
    {
        Sid = app.Sid;
        DisplayName = app.DisplayName;
        PackageFamilyName = app.PackageFamilyName;
        _isSelected = app.IsExempt;
    }

    public string Sid { get; }

    public string DisplayName { get; }

    public string PackageFamilyName { get; }

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value)
            {
                return;
            }

            _isSelected = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}
