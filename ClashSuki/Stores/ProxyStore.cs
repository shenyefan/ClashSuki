using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using ClashSuki.Models;
using ClashSuki.Utilities;
using ClashSuki.ViewModels;

namespace ClashSuki.Stores;

public sealed partial class ProxyStore : ObservableObject
{
    private static readonly HashSet<string> ProxyGroupTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "Selector", "URLTest", "url-test", "Fallback", "LoadBalance", "load-balance",
        "Relay", "Smart", "SmartGroup"
    };

    private readonly Dictionary<string, ProxyGroupItemViewModel> _groups = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ProviderItemViewModel> _providers = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<ProxyGroupItemViewModel> _orderedGroups = [];
    private bool _globalOnly;
    private string _sortMode = "default";
    private bool _sortDescending;
    private string _filterText = "";

    public ObservableCollection<ProxyGroupItemViewModel> Groups { get; } = [];
    public ObservableCollection<ProviderItemViewModel> Providers { get; } = [];
    public int TotalGroupCount => _groups.Count;

    public void SetFilterText(string value)
    {
        var normalized = value.Trim();
        if (string.Equals(_filterText, normalized, StringComparison.Ordinal))
        {
            return;
        }

        _filterText = normalized;
        foreach (var group in _groups.Values)
        {
            group.SearchText = _filterText;
        }

        ApplyVisibleGroups(_orderedGroups);
    }

    public void SetGlobalOnly(bool value)
    {
        if (_globalOnly == value) return;
        _globalOnly = value;
        ApplyVisibleGroups(_orderedGroups);
    }

    public void SetSortMode(string value)
    {
        var normalized = value is "delay" or "name" ? value : "default";
        if (string.Equals(_sortMode, normalized, StringComparison.Ordinal))
        {
            return;
        }

        _sortMode = normalized;
        ResetNodeSorting();
        ApplyVisibleGroups(_orderedGroups);
    }

    public void SetSortDescending(bool value)
    {
        if (_sortDescending == value)
        {
            return;
        }

        _sortDescending = value;
        ResetNodeSorting();
        ApplyVisibleGroups(_orderedGroups);
    }

    public void ApplyProxyGroups(ProxyGroupsResponse response, IReadOnlyList<string>? order = null)
    {
        var desired = new List<ProxyGroupItemViewModel>();
        var proxies = response.Proxies ?? new Dictionary<string, ProxyGroupDto>(StringComparer.OrdinalIgnoreCase);
        var delayMap = proxies.ToDictionary(p => p.Key, p => p.Value.LatestDelay);

        foreach (var pair in proxies)
        {
            var dto = pair.Value;
            if (!ProxyGroupTypes.Contains(dto.Type ?? ""))
            {
                continue;
            }

            var name = dto.Name ?? pair.Key;
            var all = dto.All ?? [];
            var current = dto.Now ?? all.FirstOrDefault() ?? "-";
            if (!_groups.TryGetValue(name, out var group) || group.Type != (dto.Type ?? ""))
            {
                group = new ProxyGroupItemViewModel
                {
                    Name = name,
                    Type = dto.Type ?? "",
                    Hidden = dto.Hidden ?? false
                };
                _groups[name] = group;
            }

            group.CurrentNode = current;
            group.NodeCount = all.Length;
            delayMap.TryGetValue(current, out var currentNodeDelay);
            group.Delay = dto.LatestDelay ?? currentNodeDelay;
            group.FixedNode = dto.Fixed ?? "";
            UpdateGroupIconKey(group, dto.Icon);
            group.TimeoutMs = dto.Timeout ?? 0;
            group.TestUrl = string.IsNullOrWhiteSpace(dto.TestUrl)
                ? "https://www.gstatic.com/generate_204"
                : dto.TestUrl!;

            if (group.Nodes.Count == all.Length && group.Nodes.Select(n => n.Name).SequenceEqual(all))
            {
                foreach (var node in group.Nodes)
                {
                    ApplyNodeMetadata(node, name, proxies, delayMap, current, group.FixedNode);
                }

                group.RefreshFiltered();
            }
            else
            {
                var existingNodes = group.Nodes.ToDictionary(
                    node => node.Name,
                    StringComparer.OrdinalIgnoreCase);
                var desiredNodes = new List<NodeItemViewModel>(all.Length);
                foreach (var nodeName in all)
                {
                    delayMap.TryGetValue(nodeName, out var delay);
                    if (!existingNodes.TryGetValue(nodeName, out var node))
                    {
                        node = new NodeItemViewModel
                        {
                            Name = nodeName,
                            GroupName = name
                        };
                    }

                    node.Delay = delay;
                    node.IsSelected = nodeName.Equals(current, StringComparison.OrdinalIgnoreCase);
                    ApplyNodeMetadata(node, name, proxies, delayMap, current, group.FixedNode);
                    desiredNodes.Add(node);
                }

                CollectionSync.Sync(group.Nodes, desiredNodes);
                group.RefreshFiltered();
            }

            desired.Add(group);
        }

        if (order is { Count: > 0 })
        {
            var orderIndex = order
                .Select((name, idx) => (name, idx))
                .ToDictionary(x => x.name, x => x.idx, StringComparer.OrdinalIgnoreCase);
            desired.Sort((a, b) =>
            {
                var ai = orderIndex.TryGetValue(a.Name, out var aIdx) ? aIdx : int.MaxValue;
                var bi = orderIndex.TryGetValue(b.Name, out var bIdx) ? bIdx : int.MaxValue;
                return ai.CompareTo(bi);
            });
        }

        foreach (var removed in _groups.Keys.Except(desired.Select(g => g.Name), StringComparer.OrdinalIgnoreCase).ToList())
        {
            _groups.Remove(removed);
        }

        foreach (var group in desired)
        {
            if (!string.Equals(group.SearchText, _filterText, StringComparison.Ordinal))
            {
                group.SearchText = _filterText;
            }
        }

        _orderedGroups.Clear();
        _orderedGroups.AddRange(desired);
        ApplyVisibleGroups(_orderedGroups);
        ProxyIconLoader.ScheduleAfterListUpdated(desired);
    }

    public void ApplyProviders(ProviderSummary summary)
    {
        var providers = summary.Providers ??
                        new Dictionary<string, ProviderDetailDto>(StringComparer.OrdinalIgnoreCase);
        var desired = new List<ProviderItemViewModel>(providers.Count);
        foreach (var pair in providers.OrderBy(p => p.Key, StringComparer.CurrentCultureIgnoreCase))
        {
            var detail = pair.Value;
            var used = (detail.SubscriptionInfo?.Upload ?? 0) + (detail.SubscriptionInfo?.Download ?? 0);
            var total = detail.SubscriptionInfo?.Total ?? 0;
            var name = detail.Name ?? pair.Key;
            if (!_providers.TryGetValue(name, out var item))
            {
                item = new ProviderItemViewModel { Name = name };
                _providers[name] = item;
            }

            item.Type = detail.VehicleType ?? detail.Type ?? "";
            item.Behavior = detail.Behavior ?? "";
            item.RuleCount = detail.RuleCount ?? 0;
            item.UsedText = total > 0
                ? $"{Utilities.Formatters.FormatBytes(used)} / {Utilities.Formatters.FormatBytes(total)}"
                : "";
            item.UpdatedText = detail.UpdatedAt ?? "";
            item.ExpireText = detail.SubscriptionInfo?.Expire is { } exp
                ? DateTimeOffset.FromUnixTimeSeconds(exp).LocalDateTime.ToString("yyyy/MM/dd")
                : "";
            desired.Add(item);
        }

        foreach (var removed in _providers.Keys
                     .Except(desired.Select(item => item.Name), StringComparer.OrdinalIgnoreCase)
                     .ToList())
        {
            _providers.Remove(removed);
        }

        CollectionSync.Sync(Providers, desired);
    }

    public ProxyGroupItemViewModel? FindGroup(string name) =>
        _groups.TryGetValue(name, out var group) ? group : null;

    private void ApplyVisibleGroups(IReadOnlyList<ProxyGroupItemViewModel> desired)
    {
        IEnumerable<ProxyGroupItemViewModel> visible = _globalOnly
            ? desired.Where(g => g.Name.Equals("GLOBAL", StringComparison.OrdinalIgnoreCase))
            : desired;

        if (!string.IsNullOrWhiteSpace(_filterText))
        {
            visible = visible.Where(g =>
                g.Name.Contains(_filterText, StringComparison.OrdinalIgnoreCase)
                || g.FilteredNodes.Count > 0);
        }

        var sorted = _sortMode switch
        {
            "delay" => SortGroupsByDelay(visible),
            "name" => visible
                .OrderBy(group => group.DisplayName, StringComparer.CurrentCultureIgnoreCase)
                .ToList(),
            _ => visible.ToList()
        };

        if (_sortDescending && _sortMode != "delay")
        {
            sorted.Reverse();
        }

        CollectionSync.Sync(Groups, sorted);
    }

    private void ResetNodeSorting()
    {
        foreach (var group in _groups.Values)
        {
            group.SortMode = "default";
            group.SortDescending = false;
        }
    }

    private List<ProxyGroupItemViewModel> SortGroupsByDelay(
        IEnumerable<ProxyGroupItemViewModel> groups)
    {
        var list = groups.ToList();
        var available = _sortDescending
            ? list.Where(group => group.Delay is > 0).OrderByDescending(group => group.Delay)
            : list.Where(group => group.Delay is > 0).OrderBy(group => group.Delay);

        return available
            .Concat(list.Where(group => group.Delay is not > 0))
            .ToList();
    }

    private static void ApplyNodeMetadata(
        NodeItemViewModel node,
        string groupName,
        IReadOnlyDictionary<string, ProxyGroupDto> proxies,
        Dictionary<string, int?> delayMap,
        string current,
        string fixedNode)
    {
        delayMap.TryGetValue(node.Name, out var delay);
        node.Delay = delay;
        node.IsSelected = node.Name.Equals(current, StringComparison.OrdinalIgnoreCase);
        node.IsFixed = !string.IsNullOrWhiteSpace(fixedNode)
                       && node.Name.Equals(fixedNode, StringComparison.OrdinalIgnoreCase);

        if (proxies.TryGetValue(node.Name, out var proxyDto))
        {
            node.ProxyType = proxyDto.Type ?? "";
            node.IsNestedGroup = proxyDto.IsGroup;
        }
    }

    private static void UpdateGroupIconKey(ProxyGroupItemViewModel group, string? icon)
    {
        var normalized = icon?.Trim() ?? "";
        if (string.Equals(group.Icon, normalized, StringComparison.Ordinal))
        {
            return;
        }

        group.Icon = normalized;
        group.IconUri = null;
    }
}
