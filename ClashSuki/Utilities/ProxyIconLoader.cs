using System.Collections.Concurrent;
using ClashSuki.ViewModels;
using Microsoft.UI.Dispatching;

namespace ClashSuki.Utilities;

public static class ProxyIconLoader
{
    private static DispatcherQueue? _uiQueue;
    private static readonly ConcurrentDictionary<string, byte> LoadsInFlight = new(StringComparer.Ordinal);

    public static void Initialize(DispatcherQueue uiQueue) => _uiQueue = uiQueue;

    /// <summary>列表更新完成后调用：先填本地缓存，再后台拉远程图标。</summary>
    public static void ScheduleAfterListUpdated(IEnumerable<ProxyGroupItemViewModel> groups)
    {
        var pending = groups
            .Where(group => !string.IsNullOrWhiteSpace(group.Icon) && group.IconUri is null)
            .ToList();
        if (pending.Count == 0)
        {
            return;
        }

        EnqueueUi(DispatcherQueuePriority.Low, () =>
        {
            foreach (var group in pending)
            {
                if (group.IconUri is not null)
                {
                    continue;
                }

                if (ProxyIconProvider.TryGetCachedUri(group.Icon) is { } cached)
                {
                    group.IconUri = cached;
                    continue;
                }

                ScheduleDownload(group);
            }
        });
    }

    public static async Task RefreshAsync(
        IEnumerable<ProxyGroupItemViewModel> groups,
        CancellationToken cancellationToken = default)
    {
        var targets = groups
            .Where(group => !string.IsNullOrWhiteSpace(group.Icon))
            .GroupBy(group => group.Icon.Trim(), StringComparer.Ordinal)
            .ToList();
        if (targets.Count == 0)
        {
            return;
        }

        foreach (var group in targets.SelectMany(group => group))
        {
            group.IconUri = null;
        }

        var refreshed = await Task.WhenAll(targets.Select(async group =>
        {
            var iconKey = group.Key;
            var uri = await ProxyIconProvider.RefreshIconUriAsync(iconKey, cancellationToken)
                .ConfigureAwait(false);
            return (IconKey: iconKey, Uri: uri, Groups: group.ToArray());
        })).ConfigureAwait(false);

        EnqueueUi(DispatcherQueuePriority.Normal, () =>
        {
            foreach (var result in refreshed)
            {
                foreach (var group in result.Groups)
                {
                    if (string.Equals(group.Icon, result.IconKey, StringComparison.Ordinal))
                    {
                        group.IconUri = result.Uri;
                    }
                }
            }
        });
    }

    private static void ScheduleDownload(ProxyGroupItemViewModel group)
    {
        var iconKey = group.Icon.Trim();
        if (string.IsNullOrWhiteSpace(iconKey))
        {
            return;
        }

        var loadKey = $"{group.Name}\0{iconKey}";
        if (!LoadsInFlight.TryAdd(loadKey, 0))
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                var iconUri = await ProxyIconProvider.GetIconUriAsync(iconKey).ConfigureAwait(false);
                if (iconUri is null || !string.Equals(group.Icon, iconKey, StringComparison.Ordinal))
                {
                    return;
                }

                EnqueueUi(DispatcherQueuePriority.Low, () =>
                {
                    if (string.Equals(group.Icon, iconKey, StringComparison.Ordinal))
                    {
                        group.IconUri = iconUri;
                    }
                });
            }
            finally
            {
                LoadsInFlight.TryRemove(loadKey, out _);
            }
        });
    }

    private static void EnqueueUi(DispatcherQueuePriority priority, Action action)
    {
        _uiQueue?.TryEnqueue(priority, () => action());
    }
}
