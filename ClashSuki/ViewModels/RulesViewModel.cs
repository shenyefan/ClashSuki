using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ClashSuki.Models;
using ClashSuki.Services;
using ClashSuki.Stores;
using ClashSuki.Utilities;

namespace ClashSuki.ViewModels;

public sealed partial class RulesViewModel : ObservableObject
{
    private readonly AppCoordinator _coordinator;
    private readonly List<RuleItemViewModel> _allRules = [];

    [ObservableProperty] private bool isRefreshing;
    [ObservableProperty] private string searchText = "";

    public RulesViewModel(AppCoordinator coordinator)
    {
        _coordinator = coordinator;
        Rules = coordinator.Rules;
        Rules.RulesApplied += () => RefreshRules(Rules.Rules.ToList());
    }

    public RuleStore Rules { get; }
    public RuntimeStore Runtime => _coordinator.Runtime;
    public ObservableCollection<RuleItemViewModel> FilteredRules { get; } = [];
    public int FilteredRuleCount => FilteredRules.Count;
    public string EmptyText => string.IsNullOrWhiteSpace(SearchText) ? "暂无分流规则" : "没有匹配当前筛选条件的规则";

    partial void OnSearchTextChanged(string value)
    {
        OnPropertyChanged(nameof(EmptyText));
        ApplyFilter();
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        if (IsRefreshing)
        {
            return;
        }

        IsRefreshing = true;
        var startedAt = DateTimeOffset.UtcNow;

        try
        {
            await _coordinator.RefreshRulesNowAsync();
        }
        finally
        {
            var elapsed = DateTimeOffset.UtcNow - startedAt;
            var remaining = TimeSpan.FromMilliseconds(350) - elapsed;
            if (remaining > TimeSpan.Zero)
            {
                await Task.Delay(remaining);
            }

            IsRefreshing = false;
        }
    }

    public async Task SetRuleEnabledAsync(RuleItemViewModel rule, bool enabled)
    {
        if (rule.IsUpdating || rule.IsSyncing || rule.IsEnabled == enabled)
        {
            return;
        }

        var previous = rule.IsEnabled;
        rule.IsUpdating = true;
        rule.SetEnabledFromCore(enabled);

        try
        {
            await _coordinator.SetRuleDisabledAsync(rule.RuleIndex, !enabled);
        }
        catch (Exception ex)
        {
            rule.SetEnabledFromCore(previous);
            Runtime.Notifications.Error(
                $"规则状态更新失败：{ex.Message}",
                source: LogSources.Rule,
                exception: ex);
        }
        finally
        {
            rule.IsUpdating = false;
        }
    }

    public void RefreshRules(List<RuleDto> rules)
    {
        var existing = _allRules.ToDictionary(rule => rule.RuleIndex);
        var desired = new List<RuleItemViewModel>(rules.Count);

        for (var i = 0; i < rules.Count; i++)
        {
            var rule = rules[i];
            var index = rule.Index ?? i;

            if (!existing.TryGetValue(index, out var item))
            {
                item = new RuleItemViewModel { RuleIndex = index };
            }

            item.Apply(rule, i + 1, i == rules.Count - 1);
            desired.Add(item);
        }

        CollectionSync.Sync(_allRules, desired);
        ApplyFilter();
    }

    private void ApplyFilter()
    {
        var query = SearchText.Trim();
        var source = string.IsNullOrWhiteSpace(query)
            ? _allRules
            : _allRules.Where(rule =>
                rule.Type.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                rule.Payload.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                rule.DisplayPayload.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                rule.Proxy.Contains(query, StringComparison.OrdinalIgnoreCase)).ToList();

        CollectionSync.Sync(FilteredRules, source);
        OnPropertyChanged(nameof(FilteredRuleCount));
    }
}

public sealed partial class RuleItemViewModel : ObservableObject
{
    [ObservableProperty] private bool isEnabled = true;
    [ObservableProperty] private bool isUpdating;
    [ObservableProperty] private int displayIndex;
    [ObservableProperty] private string type = "";
    [ObservableProperty] private string payload = "";
    [ObservableProperty] private string proxy = "";
    [ObservableProperty] private long size;
    [ObservableProperty] private int hitCount;
    [ObservableProperty] private int missCount;
    [ObservableProperty] private string hitAt = "";
    [ObservableProperty] private string missAt = "";
    [ObservableProperty] private bool hasRuntimeStats;
    [ObservableProperty] private bool isFallbackRule;

    public required int RuleIndex { get; init; }
    public bool IsSyncing { get; private set; }

    public string IndexText => $"#{DisplayIndex}";
    public string DisplayPayload => IsFallbackRule || string.IsNullOrWhiteSpace(Payload) ? "FINAL" : Payload;
    public string SizeText => Size > 0 ? $"{Size:N0} 条" : "";
    public string DetailText => string.Join(" · ", new[] { IndexText, Type, Proxy, SizeText }.Where(s => !string.IsNullOrWhiteSpace(s)));
    public bool HasSize => Size > 0;
    public int TotalCount => HitCount + MissCount;
    public string HitSummary => HasRuntimeStats ? $"{HitCount}/{TotalCount}" : "";
    public string HitRateText => HasRuntimeStats && TotalCount > 0 ? $"{HitCount * 100.0 / TotalCount:0.0}%" : "0.0%";
    public string HitTimeText => HasRuntimeStats ? FormatRelativeTime(string.IsNullOrWhiteSpace(HitAt) ? MissAt : HitAt) : "";
    public double DisabledOpacity => IsEnabled ? 1.0 : 0.55;

    public void Apply(RuleDto rule, int displayIndex, bool isLast)
    {
        DisplayIndex = displayIndex;
        Type = rule.Type ?? "--";
        Payload = rule.Payload ?? "";
        Proxy = rule.Proxy ?? "--";
        Size = rule.Size ?? 0;
        HitCount = rule.Extra?.HitCount ?? 0;
        MissCount = rule.Extra?.MissCount ?? 0;
        HitAt = rule.Extra?.HitAt ?? "";
        MissAt = rule.Extra?.MissAt ?? "";
        HasRuntimeStats = rule.Extra is not null;
        IsFallbackRule = isLast && (string.IsNullOrWhiteSpace(rule.Payload) || string.Equals(rule.Type, "MATCH", StringComparison.OrdinalIgnoreCase));
        if (!IsUpdating)
        {
            SetEnabledFromCore(rule.Extra is null || !rule.Extra.Disabled);
        }
    }

    partial void OnDisplayIndexChanged(int value)
    {
        OnPropertyChanged(nameof(IndexText));
        OnPropertyChanged(nameof(DetailText));
    }

    partial void OnTypeChanged(string value) => OnPropertyChanged(nameof(DetailText));
    partial void OnPayloadChanged(string value) => OnPropertyChanged(nameof(DisplayPayload));
    partial void OnProxyChanged(string value) => OnPropertyChanged(nameof(DetailText));
    partial void OnIsFallbackRuleChanged(bool value) => OnPropertyChanged(nameof(DisplayPayload));
    partial void OnSizeChanged(long value)
    {
        OnPropertyChanged(nameof(SizeText));
        OnPropertyChanged(nameof(HasSize));
        OnPropertyChanged(nameof(DetailText));
    }

    partial void OnHitCountChanged(int value)
    {
        OnPropertyChanged(nameof(TotalCount));
        OnPropertyChanged(nameof(HitSummary));
        OnPropertyChanged(nameof(HitRateText));
    }

    partial void OnMissCountChanged(int value)
    {
        OnPropertyChanged(nameof(TotalCount));
        OnPropertyChanged(nameof(HitSummary));
        OnPropertyChanged(nameof(HitRateText));
    }

    partial void OnHitAtChanged(string value) => OnPropertyChanged(nameof(HitTimeText));
    partial void OnMissAtChanged(string value) => OnPropertyChanged(nameof(HitTimeText));
    partial void OnHasRuntimeStatsChanged(bool value)
    {
        OnPropertyChanged(nameof(HitSummary));
        OnPropertyChanged(nameof(HitRateText));
        OnPropertyChanged(nameof(HitTimeText));
    }

    partial void OnIsEnabledChanged(bool value) => OnPropertyChanged(nameof(DisabledOpacity));

    public void SetEnabledFromCore(bool value)
    {
        IsSyncing = true;
        try
        {
            IsEnabled = value;
        }
        finally
        {
            IsSyncing = false;
        }
    }

    private static string FormatRelativeTime(string value)
    {
        if (!DateTimeOffset.TryParse(value, out var time) || time == DateTimeOffset.UnixEpoch)
        {
            return "未命中规则";
        }

        var span = DateTimeOffset.Now - time;
        if (span.TotalSeconds < 60) return "最近命中于 几秒前";
        if (span.TotalMinutes < 60) return $"最近命中于 {(int)span.TotalMinutes} 分钟前";
        if (span.TotalHours < 24) return $"最近命中于 {(int)span.TotalHours} 小时前";
        return $"最近命中于 {(int)span.TotalDays} 天前";
    }
}
