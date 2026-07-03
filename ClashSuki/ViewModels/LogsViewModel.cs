using System.Collections.ObjectModel;
using System.Collections.Specialized;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ClashSuki.Services;
using ClashSuki.Stores;
using ClashSuki.Utilities;

namespace ClashSuki.ViewModels;

public sealed partial class LogsViewModel : ObservableObject
{
    private readonly AppCoordinator _coordinator;

    [ObservableProperty] private string searchText = "";
    [ObservableProperty] private int levelFilterIndex;
    [ObservableProperty] private bool isPaused;
    [ObservableProperty] private bool showMihomo;

    public LogsViewModel(AppCoordinator coordinator)
    {
        _coordinator = coordinator;
        Logs = coordinator.Logs;
        Logs.AppItems.CollectionChanged += (_, e) => OnSourceChanged(e, isApp: true);
        Logs.MihomoItems.CollectionChanged += (_, e) => OnSourceChanged(e, isApp: false);
        RebuildFiltered(isApp: true);
        RebuildFiltered(isApp: false);
    }

    public LogStore Logs { get; }
    public RuntimeStore Runtime => _coordinator.Runtime;
    public ObservableCollection<LogItemViewModel> FilteredAppLogs { get; } = [];
    public ObservableCollection<LogItemViewModel> FilteredMihomoLogs { get; } = [];
    public int FilteredAppLogCount => FilteredAppLogs.Count;
    public int FilteredMihomoLogCount => FilteredMihomoLogs.Count;
    public ObservableCollection<LogItemViewModel> CurrentFilteredLogs => ShowMihomo ? FilteredMihomoLogs : FilteredAppLogs;
    public int CurrentFilteredLogCount => ShowMihomo ? FilteredMihomoLogCount : FilteredAppLogCount;
    public string EmptyLogText => string.IsNullOrWhiteSpace(SearchText) && LevelFilterIndex == 0
        ? "暂无日志"
        : "没有匹配当前筛选条件的日志";
    public string AutoRefreshText => IsPaused ? "已暂停" : "自动刷新";
    public string AutoRefreshGlyph => IsPaused ? "\uE769" : "\uE72C";

    partial void OnSearchTextChanged(string value)
    {
        OnPropertyChanged(nameof(EmptyLogText));
        RebuildFiltered(isApp: true);
        RebuildFiltered(isApp: false);
        OnPropertyChanged(nameof(CurrentFilteredLogCount));
    }

    partial void OnLevelFilterIndexChanged(int value)
    {
        OnPropertyChanged(nameof(EmptyLogText));
        RebuildFiltered(isApp: true);
        RebuildFiltered(isApp: false);
        OnPropertyChanged(nameof(CurrentFilteredLogCount));
    }

    partial void OnIsPausedChanged(bool value)
    {
        Logs.IsPaused = value;
        OnPropertyChanged(nameof(AutoRefreshText));
        OnPropertyChanged(nameof(AutoRefreshGlyph));
    }

    partial void OnShowMihomoChanged(bool value)
    {
        OnPropertyChanged(nameof(CurrentFilteredLogs));
        OnPropertyChanged(nameof(CurrentFilteredLogCount));
    }

    private string LevelFilter => LevelFilterIndex switch
    {
        1 => "DEBUG",
        2 => "INFO",
        3 => "WARN",
        4 => "ERROR",
        _ => ""
    };

    private void OnSourceChanged(NotifyCollectionChangedEventArgs e, bool isApp)
    {
        var target = isApp ? FilteredAppLogs : FilteredMihomoLogs;

        switch (e.Action)
        {
            case NotifyCollectionChangedAction.Add:
                if (e.NewItems is null)
                {
                    break;
                }

                foreach (LogItemViewModel item in e.NewItems)
                {
                    if (!MatchesFilter(item))
                    {
                        continue;
                    }

                    target.Insert(0, item);
                }

                NotifyFilteredCount(isApp);
                break;

            case NotifyCollectionChangedAction.Remove:
                if (e.OldItems is null)
                {
                    break;
                }

                foreach (LogItemViewModel item in e.OldItems)
                {
                    var index = target.IndexOf(item);
                    if (index >= 0)
                    {
                        target.RemoveAt(index);
                    }
                }

                NotifyFilteredCount(isApp);
                break;

            case NotifyCollectionChangedAction.Reset:
                target.Clear();
                NotifyFilteredCount(isApp);
                break;

            default:
                RebuildFiltered(isApp);
                break;
        }
    }

    private void RebuildFiltered(bool isApp)
    {
        var source = isApp ? Logs.AppItems : Logs.MihomoItems;
        var target = isApp ? FilteredAppLogs : FilteredMihomoLogs;
        var desired = source.Where(MatchesFilter).Reverse().ToList();
        CollectionSync.Sync(target, desired);
        NotifyFilteredCount(isApp);
    }

    private bool MatchesFilter(LogItemViewModel item)
    {
        var query = SearchText.Trim();
        var level = LevelFilter;
        return (string.IsNullOrWhiteSpace(level) || item.Level.Contains(level, StringComparison.OrdinalIgnoreCase))
               && (string.IsNullOrWhiteSpace(query) ||
                   item.Message.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                   item.Details.Contains(query, StringComparison.OrdinalIgnoreCase));
    }

    private void NotifyFilteredCount(bool isApp)
    {
        OnPropertyChanged(isApp ? nameof(FilteredAppLogCount) : nameof(FilteredMihomoLogCount));
        if (isApp == !ShowMihomo || !isApp && ShowMihomo)
        {
            OnPropertyChanged(nameof(CurrentFilteredLogCount));
        }
    }

    [RelayCommand]
    private void ClearLogs() => Logs.Clear();

    [RelayCommand]
    private void ToggleAutoRefresh() => IsPaused = !IsPaused;
}
