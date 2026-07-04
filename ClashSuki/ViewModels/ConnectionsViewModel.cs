using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.ComponentModel;
using ClashSuki.Services;
using ClashSuki.Stores;

namespace ClashSuki.ViewModels;

public sealed partial class ConnectionsViewModel : ObservableObject
{
    private readonly AppCoordinator _coordinator;
    private CancellationTokenSource? _filterCts;

    [ObservableProperty] private string filterText = "";
    [ObservableProperty] private bool showClosed;
    [ObservableProperty] private int sortIndex;
    [ObservableProperty] private bool sortDescending = true;

    public ConnectionsViewModel(AppCoordinator coordinator)
    {
        _coordinator = coordinator;
        Connections = coordinator.Connections;
        Runtime = coordinator.Runtime;
        Connections.PropertyChanged += Connections_PropertyChanged;
    }

    public ConnectionStore Connections { get; }
    public RuntimeStore Runtime { get; }
    public string ActiveTabText => $"活动中 {Connections.ActiveCount}";
    public string ClosedTabText => $"已关闭 {Connections.ClosedCount}";
    public string SortDirectionGlyph => SortDescending ? "\uE74B" : "\uE74A";
    public string EmptyTitle => ShowClosed ? "暂无已关闭连接" : "暂无活动连接";
    public string EmptyDescription => string.IsNullOrWhiteSpace(FilterText) ? "连接数据会在 mihomo 返回后自动刷新。" : "没有匹配当前筛选条件的连接。";

    partial void OnFilterTextChanged(string value)
    {
        Connections.FilterText = value;
        QueueApplyFilter();
        OnPropertyChanged(nameof(EmptyDescription));
    }

    partial void OnShowClosedChanged(bool value)
    {
        Connections.ShowClosed = value;
        Connections.ApplyVisibleTabSwitch();
        OnPropertyChanged(nameof(EmptyTitle));
        OnPropertyChanged(nameof(EmptyDescription));
    }

    partial void OnSortIndexChanged(int value)
    {
        Connections.SortKey = value switch
        {
            1 => "host",
            2 => "process",
            3 => "rule",
            4 => "upload",
            5 => "download",
            6 => "started",
            _ => "updated"
        };
        QueueApplyFilter();
    }

    partial void OnSortDescendingChanged(bool value)
    {
        Connections.SortDescending = value;
        QueueApplyFilter();
        OnPropertyChanged(nameof(SortDirectionGlyph));
    }

    public void SetShowClosed(bool value)
    {
        if (ShowClosed != value)
        {
            ShowClosed = value;
        }
    }

    public void RefreshCountLabels()
    {
        OnPropertyChanged(nameof(ActiveTabText));
        OnPropertyChanged(nameof(ClosedTabText));
    }

    private void QueueApplyFilter()
    {
        _filterCts?.Cancel();
        var cts = new CancellationTokenSource();
        _filterCts = cts;
        _ = ApplyFilterAsync(cts);
    }

    private async Task ApplyFilterAsync(CancellationTokenSource cts)
    {
        try
        {
            await Connections.ApplyFilterAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            // A newer tab/filter/sort request superseded this one.
        }
        catch (Exception ex)
        {
            Runtime.Notifications.Warning(
                "连接列表筛选失败，已保留当前列表",
                source: LogSources.Connection,
                exception: ex);
        }
        finally
        {
            if (ReferenceEquals(_filterCts, cts))
            {
                _filterCts = null;
            }

            cts.Dispose();
        }
    }

    [RelayCommand]
    private void ToggleSortDirection() => SortDescending = !SortDescending;

    [RelayCommand]
    private async Task CloseAllConnectionsAsync()
    {
        try
        {
            await _coordinator.CloseAllConnectionsAsync();
            Runtime.Notifications.Success(
                "全部活动连接已关闭",
                source: LogSources.Connection);
        }
        catch (Exception ex)
        {
            Runtime.Notifications.Error(
                "关闭全部连接失败",
                source: LogSources.Connection,
                exception: ex);
        }
    }

    [RelayCommand]
    private async Task CloseConnectionAsync(ConnectionItemViewModel? connection)
    {
        if (connection is not null && !connection.IsClosing)
        {
            connection.IsClosing = true;
            try
            {
                await _coordinator.CloseConnectionAsync(connection.Id);
            }
            catch (Exception ex)
            {
                Runtime.Notifications.Error(
                    "关闭连接失败",
                    source: LogSources.Connection,
                    exception: ex);
            }
            finally
            {
                connection.IsClosing = false;
            }
        }
    }

    private void Connections_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ConnectionStore.ActiveCount)
            or nameof(ConnectionStore.ClosedCount)
            or nameof(ConnectionStore.VisibleCount))
        {
            RefreshCountLabels();
        }
    }
}
