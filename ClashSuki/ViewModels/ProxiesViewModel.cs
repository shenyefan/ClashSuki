using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ClashSuki.Services;
using ClashSuki.Stores;

namespace ClashSuki.ViewModels;

public sealed partial class ProxiesViewModel : ObservableObject
{
    private readonly AppCoordinator _coordinator;
    private bool _syncingPreferences;
    private bool _preferencesSavePending;
    private bool _isSavingPreferences;

    [ObservableProperty] private int sortIndex;
    [ObservableProperty] private bool sortDescending;
    [ObservableProperty] private string displayMode = "simple";
    [ObservableProperty] private bool isDirectMode;
    [ObservableProperty] private string filterText = "";

    public ProxiesViewModel(AppCoordinator coordinator)
    {
        _coordinator = coordinator;
        Proxies = coordinator.Proxies;
        Runtime = coordinator.Runtime;
        Groups = coordinator.Proxies.Groups;
        Groups.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(GroupCount));
            OnPropertyChanged(nameof(ShowProxyGroups));
            OnPropertyChanged(nameof(ShowEmptyProxyGroups));
            OnPropertyChanged(nameof(ShowNoFilterResults));
        };
        Runtime.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(RuntimeStore.CurrentMode))
            {
                IsDirectMode = Runtime.CurrentMode.Equals("direct", StringComparison.OrdinalIgnoreCase);
            }
        };
        IsDirectMode = Runtime.CurrentMode.Equals("direct", StringComparison.OrdinalIgnoreCase);
    }

    public ProxyStore Proxies { get; }
    public RuntimeStore Runtime { get; }
    public ObservableCollection<ProxyGroupItemViewModel> Groups { get; }
    public int GroupCount => Groups.Count;
    public bool ShowProxyGroups => GroupCount > 0 && !IsDirectMode;
    public bool ShowNoFilterResults => !IsDirectMode
                                       && !string.IsNullOrWhiteSpace(FilterText)
                                       && GroupCount == 0
                                       && Proxies.TotalGroupCount > 0;
    public bool ShowEmptyProxyGroups => GroupCount == 0 && !IsDirectMode && !ShowNoFilterResults;
    public bool IsFullDisplay => DisplayMode.Equals("full", StringComparison.OrdinalIgnoreCase);
    public string SortDirectionGlyph => SortDescending ? "\uE74B" : "\uE74A";
    public string DisplayModeLabel => IsFullDisplay ? "完整模式" : "简洁模式";

    partial void OnSortIndexChanged(int value)
    {
        Proxies.SetSortMode(SortModeFromIndex(value));
        if (!_syncingPreferences)
        {
            _ = SavePreferencesAsync();
        }
    }

    partial void OnSortDescendingChanged(bool value)
    {
        Proxies.SetSortDescending(value);
        OnPropertyChanged(nameof(SortDirectionGlyph));
        if (!_syncingPreferences)
        {
            _ = SavePreferencesAsync();
        }
    }

    partial void OnIsDirectModeChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowProxyGroups));
        OnPropertyChanged(nameof(ShowEmptyProxyGroups));
        OnPropertyChanged(nameof(ShowNoFilterResults));
    }

    partial void OnFilterTextChanged(string value)
    {
        Proxies.SetFilterText(value);
        OnPropertyChanged(nameof(ShowProxyGroups));
        OnPropertyChanged(nameof(ShowEmptyProxyGroups));
        OnPropertyChanged(nameof(ShowNoFilterResults));
    }

    partial void OnDisplayModeChanged(string value)
    {
        OnPropertyChanged(nameof(IsFullDisplay));
        OnPropertyChanged(nameof(DisplayModeLabel));
        if (!_syncingPreferences)
        {
            _ = SavePreferencesAsync();
        }
    }

    [RelayCommand]
    private async Task SelectNodeAsync(NodeItemViewModel? node)
    {
        if (node is null)
        {
            return;
        }

        var group = Proxies.FindGroup(node.GroupName);
        if (group is null || !group.CanSwitch || node.IsSelected)
        {
            return;
        }

        await _coordinator.SelectNodeAsync(node.GroupName, node.Name);
    }

    [RelayCommand]
    private async Task TestGroupDelayAsync(ProxyGroupItemViewModel? group)
    {
        if (group is null || group.IsGroupDelayRunning)
        {
            return;
        }

        if (group.FilteredNodes.Count == 0)
        {
            group.IsExpanded = true;
            group.RefreshFiltered();
            if (group.FilteredNodes.Count == 0)
            {
                return;
            }
        }

        await _coordinator.TestGroupDelayAsync(group.Name);
    }

    [RelayCommand]
    private async Task TestNodeDelayAsync(NodeItemViewModel? node)
    {
        if (node is null || node.IsDelayLoading)
        {
            return;
        }

        node.IsTesting = true;
        try
        {
            await _coordinator.TestNodeDelayAsync(node.GroupName, node.Name);
        }
        finally
        {
            node.IsTesting = false;
        }
    }

    [RelayCommand]
    private async Task UnfixGroupAsync(ProxyGroupItemViewModel? group)
    {
        if (group is null || string.IsNullOrWhiteSpace(group.FixedNode))
        {
            return;
        }

        await _coordinator.UnfixProxyAsync(group.Name);
    }

    [RelayCommand]
    private async Task RefreshAsync() => await _coordinator.RefreshProxiesNowAsync();

    [RelayCommand]
    private void ToggleSortDirection() => SortDescending = !SortDescending;

    [RelayCommand]
    private void ToggleDisplayMode() =>
        DisplayMode = IsFullDisplay ? "simple" : "full";

    public async Task LoadPreferencesAsync()
    {
        try
        {
            var settings = await AppSettingsService.LoadAsync();
            _syncingPreferences = true;
            try
            {
                SortIndex = SortIndexFromMode(settings.ProxyDisplayOrder);
                SortDescending = settings.ProxySortDescending;
                DisplayMode = string.IsNullOrWhiteSpace(settings.ProxyDisplayMode) ? "simple" : settings.ProxyDisplayMode;
                Proxies.SetSortMode(SortModeFromIndex(SortIndex));
                Proxies.SetSortDescending(SortDescending);

                foreach (var group in Groups)
                {
                    if (settings.GroupExpandState.TryGetValue(group.Name, out var expanded))
                    {
                        group.IsExpanded = expanded;
                    }
                }
            }
            finally
            {
                _syncingPreferences = false;
            }
        }
        catch (Exception ex)
        {
            Runtime.Notifications.Warning(
                "代理组偏好加载失败",
                source: LogSources.Proxy,
                exception: ex);
        }
    }

    public async Task SaveGroupExpandStatesAsync(IEnumerable<ProxyGroupItemViewModel> groups)
    {
        var states = groups.ToDictionary(
            group => group.Name,
            group => group.IsExpanded,
            StringComparer.OrdinalIgnoreCase);
        await AppSettingsService.PatchAsync(settings =>
        {
            foreach (var (name, expanded) in states)
            {
                settings.GroupExpandState[name] = expanded;
            }
        });
    }

    private async Task SavePreferencesAsync()
    {
        _preferencesSavePending = true;
        if (_isSavingPreferences)
        {
            return;
        }

        _isSavingPreferences = true;
        try
        {
            while (_preferencesSavePending)
            {
                _preferencesSavePending = false;
                var order = SortModeFromIndex(SortIndex);
                var descending = SortDescending;
                var displayMode = DisplayMode;
                await AppSettingsService.PatchAsync(settings =>
                {
                    settings.ProxyDisplayOrder = order;
                    settings.ProxySortDescending = descending;
                    settings.ProxyDisplayMode = displayMode;
                });
            }
        }
        catch (Exception ex)
        {
            _preferencesSavePending = false;
            Runtime.Notifications.Warning(
                "代理组偏好保存失败",
                source: LogSources.Proxy,
                exception: ex);
        }
        finally
        {
            _isSavingPreferences = false;
        }
    }

    private static string SortModeFromIndex(int index) => index switch
    {
        1 => "delay",
        2 => "name",
        _ => "default"
    };

    private static int SortIndexFromMode(string? mode) => mode switch
    {
        "delay" => 1,
        "name" => 2,
        _ => 0
    };
}
