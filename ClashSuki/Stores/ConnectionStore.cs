using System.Collections.ObjectModel;
using System.ComponentModel;
using ClashSuki.Models;
using ClashSuki.Utilities;
using ClashSuki.ViewModels;

namespace ClashSuki.Stores;

public sealed class ConnectionStore : INotifyPropertyChanged
{
    private readonly Dictionary<string, (long Upload, long Download)> _previous = [];
    private readonly BoundedObservableCollection<ConnectionItemViewModel> _closed = new(200);
    private int _visibleVersion;
    private List<ConnectionItemViewModel> _cachedVisibleActive = [];
    private List<ConnectionItemViewModel> _cachedVisibleClosed = [];

    public ObservableCollection<ConnectionItemViewModel> Active { get; } = [];
    public ObservableCollection<ConnectionItemViewModel> Visible { get; } = [];

    public int ActiveCount => Active.Count;
    public int ClosedCount => _closed.Count;
    public int VisibleCount => Visible.Count;
    public bool ShowClosed { get; set; }
    public string FilterText { get; set; } = "";
    public string SortKey { get; set; } = "updated";
    public bool SortDescending { get; set; } = true;

    public event PropertyChangedEventHandler? PropertyChanged;

    public void Apply(ConnectionsSnapshot snapshot)
    {
        var desired = new List<ConnectionItemViewModel>();
        var byId = Active.ToDictionary(c => c.Id);
        var activeIds = new HashSet<string>();
        var hadClosedChanges = false;

        foreach (var dto in snapshot.Connections ?? [])
        {
            var id = dto.Id ?? "";
            if (string.IsNullOrWhiteSpace(id))
            {
                continue;
            }

            activeIds.Add(id);
            if (!byId.TryGetValue(id, out var vm))
            {
                vm = new ConnectionItemViewModel
                {
                    Id = id,
                    Host = string.IsNullOrWhiteSpace(dto.Metadata?.Host) ? dto.Metadata?.DestinationIP ?? "--" : dto.Metadata.Host!,
                    Port = dto.Metadata?.DestinationPort ?? "",
                    Network = dto.Metadata?.Network ?? "--",
                    Rule = dto.Rule ?? "--",
                    RulePayload = dto.RulePayload ?? "",
                    Chain = dto.Chains is { Length: > 0 } ? string.Join(" / ", dto.Chains) : "--",
                    ProcessText = string.IsNullOrWhiteSpace(dto.Metadata?.Process)
                        ? Path.GetFileName(dto.Metadata?.ProcessPath ?? "")
                        : dto.Metadata!.Process!,
                    ProcessPath = dto.Metadata?.ProcessPath ?? ""
                };
            }

            UpdateProcessIcon(vm, dto.Metadata?.ProcessPath);

            var upload = dto.Upload ?? 0;
            var download = dto.Download ?? 0;
            if (_previous.TryGetValue(id, out var prev))
            {
                vm.UpSpeedText = Formatters.FormatSpeed(Math.Max(0, upload - prev.Upload));
                vm.DownSpeedText = Formatters.FormatSpeed(Math.Max(0, download - prev.Download));
            }

            vm.UploadText = Formatters.FormatBytes(upload);
            vm.DownloadText = Formatters.FormatBytes(download);
            vm.UploadBytes = upload;
            vm.DownloadBytes = download;
            vm.StartText = RelativeTime(dto.Start);
            vm.StartTime = TryParseTime(dto.Start);
            vm.LastSeenAt = DateTimeOffset.Now;
            vm.IsClosed = false;
            _previous[id] = (upload, download);
            desired.Add(vm);
        }

        foreach (var old in Active.Where(c => !activeIds.Contains(c.Id)).ToList())
        {
            old.IsClosed = true;
            old.ClosedAt = DateTimeOffset.Now;
            _closed.InsertNewestFirst(old);
            _previous.Remove(old.Id);
            hadClosedChanges = true;
        }

        var previousActiveIds = Active.Select(c => c.Id).ToHashSet();
        var nextActiveIds = desired.Select(c => c.Id).ToHashSet();
        var structureChanged = hadClosedChanges
                               || previousActiveIds.Count != nextActiveIds.Count
                               || !previousActiveIds.SetEquals(nextActiveIds);

        CollectionSync.Sync(Active, desired);
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ActiveCount)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ClosedCount)));

        var requiresDynamicOrder = SortKey is "updated" or "upload" or "download";
        if (structureChanged || !string.IsNullOrWhiteSpace(FilterText) || requiresDynamicOrder)
        {
            RebuildVisibleCaches();
            ApplyVisibleList();
        }
    }

    public void ApplyFilter()
    {
        Interlocked.Increment(ref _visibleVersion);
        RebuildVisibleCaches();
        ApplyVisibleList();
    }

    public void ApplyVisibleTabSwitch()
    {
        ApplyVisibleList();
    }

    public async Task ApplyFilterAsync(CancellationToken cancellationToken = default)
    {
        var version = Interlocked.Increment(ref _visibleVersion);
        var active = Active.ToArray();
        var closed = _closed.ToArray();
        var query = FilterText.Trim();
        var sortKey = SortKey;
        var sortDescending = SortDescending;
        var result = await Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            return (
                Active: BuildVisibleList(active, query, sortKey, sortDescending, showClosed: false),
                Closed: BuildVisibleList(closed, query, sortKey, sortDescending, showClosed: true));
        }, cancellationToken);

        if (version != _visibleVersion || cancellationToken.IsCancellationRequested)
        {
            return;
        }

        _cachedVisibleActive = result.Active;
        _cachedVisibleClosed = result.Closed;
        ApplyVisibleList();
    }

    private void RebuildVisibleCaches()
    {
        _cachedVisibleActive = BuildVisibleList(showClosed: false);
        _cachedVisibleClosed = BuildVisibleList(showClosed: true);
    }

    private void ApplyVisibleList()
    {
        var desired = ShowClosed ? _cachedVisibleClosed : _cachedVisibleActive;
        CollectionSync.Sync(Visible, desired);
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(VisibleCount)));
    }

    private List<ConnectionItemViewModel> BuildVisibleList(bool showClosed)
    {
        IEnumerable<ConnectionItemViewModel> source = showClosed ? _closed : Active;
        return BuildVisibleList(
            source,
            FilterText.Trim(),
            SortKey,
            SortDescending,
            showClosed);
    }

    private static List<ConnectionItemViewModel> BuildVisibleList(
        IEnumerable<ConnectionItemViewModel> source,
        string query,
        string sortKey,
        bool sortDescending,
        bool showClosed)
    {
        if (!string.IsNullOrWhiteSpace(query))
        {
            source = source.Where(c => c.SearchText.Contains(query, StringComparison.OrdinalIgnoreCase));
        }

        return Sort(source, sortKey, sortDescending, showClosed).ToList();
    }

    private static IEnumerable<ConnectionItemViewModel> Sort(
        IEnumerable<ConnectionItemViewModel> source,
        string sortKey,
        bool sortDescending,
        bool showClosed)
    {
        Func<ConnectionItemViewModel, object> selector = sortKey switch
        {
            "host" => c => c.HostDisplay,
            "rule" => c => c.RuleDisplay,
            "process" => c => c.ProcessText,
            "upload" => c => c.UploadBytes,
            "download" => c => c.DownloadBytes,
            "started" => c => c.StartTime,
            _ => showClosed ? c => c.ClosedAt : c => c.LastSeenAt
        };

        return sortDescending
            ? source.OrderByDescending(selector)
            : source.OrderBy(selector);
    }

    private static string RelativeTime(string? value)
    {
        if (!DateTimeOffset.TryParse(value, out var date)) return "--";
        var span = DateTimeOffset.Now - date;
        if (span.TotalSeconds < 60) return "刚刚";
        if (span.TotalHours < 1) return $"{(int)span.TotalMinutes}分钟前";
        if (span.TotalDays < 1) return $"{(int)span.TotalHours}小时前";
        return $"{(int)span.TotalDays}天前";
    }

    private static DateTimeOffset TryParseTime(string? value) =>
        DateTimeOffset.TryParse(value, out var date) ? date : DateTimeOffset.MinValue;

    private static void UpdateProcessIcon(ConnectionItemViewModel vm, string? processPath)
    {
        processPath = processPath ?? "";
        if (string.Equals(vm.ProcessPath, processPath, StringComparison.OrdinalIgnoreCase)
            && (vm.ProcessIconUri is not null || string.IsNullOrWhiteSpace(processPath)))
        {
            return;
        }

        if (!string.Equals(vm.ProcessPath, processPath, StringComparison.OrdinalIgnoreCase))
        {
            vm.ProcessPath = processPath;
            vm.ProcessIconUri = null;
        }

        if (string.IsNullOrWhiteSpace(processPath))
        {
            return;
        }

        var dispatcher = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        _ = LoadProcessIconAsync(vm, processPath, dispatcher);
    }

    private static async Task LoadProcessIconAsync(
        ConnectionItemViewModel vm,
        string processPath,
        Microsoft.UI.Dispatching.DispatcherQueue? dispatcher)
    {
        var icon = await ProcessIconProvider.GetIconAsync(processPath);
        if (icon is null)
        {
            return;
        }

        void Apply()
        {
            if (string.Equals(vm.ProcessPath, processPath, StringComparison.OrdinalIgnoreCase))
            {
                vm.ProcessIconUri = icon;
            }
        }

        if (dispatcher is null || dispatcher.HasThreadAccess)
        {
            Apply();
            return;
        }

        dispatcher.TryEnqueue(Apply);
    }
}
