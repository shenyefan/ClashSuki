using System.Collections.ObjectModel;
using ClashSuki.Models;
using ClashSuki.Services;
using ClashSuki.Utilities;
using ClashSuki.ViewModels;

namespace ClashSuki.Stores;

public sealed class RuleStore
{
    public ObservableCollection<RuleDto> Rules { get; } = [];
    public ObservableCollection<RuleProviderItemViewModel> Providers { get; } = [];

    public int RuleCount => Rules.Count;
    public event Action? RulesApplied;

    public void Apply(
        RulesResponse? rules,
        RuleProviderSummary? providers,
        IReadOnlyDictionary<string, YamlConfigService.RuleProviderConfigInfo>? providerConfigs = null)
    {
        CollectionSync.Sync(Rules, rules?.Rules ?? []);

        ApplyProviders(providers, providerConfigs);

        RulesApplied?.Invoke();
    }

    private void ApplyProviders(
        RuleProviderSummary? summary,
        IReadOnlyDictionary<string, YamlConfigService.RuleProviderConfigInfo>? providerConfigs)
    {
        var desired = (summary?.Providers ?? [])
            .OrderBy(pair => pair.Value.VehicleType is "File" ? 0 : 1)
            .ThenBy(pair => pair.Key, StringComparer.CurrentCultureIgnoreCase)
            .Select(pair =>
            {
                var detail = pair.Value;
                var name = detail.Name ?? pair.Key;
                YamlConfigService.RuleProviderConfigInfo? config = null;
                providerConfigs?.TryGetValue(name, out config);
                var item = Providers.FirstOrDefault(provider =>
                    provider.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) ?? new RuleProviderItemViewModel();

                item.Name = name;
                item.Type = detail.Type ?? "";
                item.VehicleType = detail.VehicleType ?? config?.VehicleType ?? detail.Type ?? "";
                item.Behavior = detail.Behavior ?? config?.Behavior ?? "";
                item.Format = detail.Format ?? config?.Format ?? "";
                item.Path = config?.Path ?? "";
                item.Payload = config?.Payload ?? "";
                item.RuleCount = detail.RuleCount ?? 0;
                item.UpdatedText = FormatRelativeTime(detail.UpdatedAt);
                return item;
            })
            .ToList();

        CollectionSync.Sync(Providers, desired);
    }

    private static string FormatRelativeTime(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            !DateTimeOffset.TryParse(value, out var time) ||
            time == DateTimeOffset.UnixEpoch)
        {
            return "未更新";
        }

        var span = DateTimeOffset.Now - time;
        if (span.TotalSeconds < 60) return "刚刚更新";
        if (span.TotalMinutes < 60) return $"{(int)span.TotalMinutes} 分钟前";
        if (span.TotalHours < 24) return $"{(int)span.TotalHours} 小时前";
        if (span.TotalDays < 30) return $"{(int)span.TotalDays} 天前";
        return time.LocalDateTime.ToString("yyyy/MM/dd");
    }
}
