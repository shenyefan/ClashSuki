using ClashSuki.Models;

namespace ClashSuki.Stores;

public sealed record TrafficSample(DateTimeOffset Timestamp, long Upload, long Download);

public sealed record TrafficRanking(string Label, long Upload, long Download)
{
    public long Total => Upload + Download;
}

public sealed class TrafficStatisticsStore
{
    private const int MaxSamples = 60;
    private const int RankingLimit = 8;

    private readonly DateTimeOffset _startedAt = DateTimeOffset.Now;
    private readonly Queue<TrafficSample> _samples = new();
    private readonly Dictionary<string, ConnectionTraffic> _lastConnections = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TrafficAccumulator> _proxyTraffic = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, TrafficAccumulator> _domainTraffic = new(StringComparer.OrdinalIgnoreCase);
    private long _lastUploadTotal;
    private long _lastDownloadTotal;

    public event Action<TrafficSample>? TrafficSampled;
    public event Action? RankingsChanged;

    public IReadOnlyList<TrafficSample> Samples => _samples.ToArray();

    public IReadOnlyList<TrafficRanking> ProxyRankings => CreateRankings(_proxyTraffic);

    public IReadOnlyList<TrafficRanking> DomainRankings => CreateRankings(_domainTraffic);

    public void ApplyTraffic(long upload, long download)
    {
        var sample = new TrafficSample(
            DateTimeOffset.Now,
            Math.Max(0, upload),
            Math.Max(0, download));

        _samples.Enqueue(sample);
        while (_samples.Count > MaxSamples)
        {
            _samples.Dequeue();
        }

        TrafficSampled?.Invoke(sample);
    }

    public void ApplyConnections(ConnectionsSnapshot snapshot)
    {
        var uploadTotal = Math.Max(0, snapshot.UploadTotal ?? 0);
        var downloadTotal = Math.Max(0, snapshot.DownloadTotal ?? 0);
        if (uploadTotal < _lastUploadTotal || downloadTotal < _lastDownloadTotal)
        {
            _lastConnections.Clear();
        }

        _lastUploadTotal = uploadTotal;
        _lastDownloadTotal = downloadTotal;

        var activeIds = new HashSet<string>(StringComparer.Ordinal);
        var changed = false;
        foreach (var connection in snapshot.Connections ?? [])
        {
            if (string.IsNullOrWhiteSpace(connection.Id))
            {
                continue;
            }

            var id = connection.Id;
            activeIds.Add(id);
            var upload = Math.Max(0, connection.Upload ?? 0);
            var download = Math.Max(0, connection.Download ?? 0);
            var hasPrevious = _lastConnections.TryGetValue(id, out var previous);
            _lastConnections[id] = new ConnectionTraffic(upload, download);

            var shouldCountInitial = !hasPrevious && StartedAfterStatistics(connection.Start);
            var uploadDelta = hasPrevious
                ? Math.Max(0, upload - previous.Upload)
                : shouldCountInitial ? upload : 0;
            var downloadDelta = hasPrevious
                ? Math.Max(0, download - previous.Download)
                : shouldCountInitial ? download : 0;
            if (uploadDelta == 0 && downloadDelta == 0)
            {
                continue;
            }

            AddTraffic(
                _domainTraffic,
                connection.Metadata?.Host,
                connection.Metadata?.DestinationIP,
                "未知域名",
                uploadDelta,
                downloadDelta);
            AddTraffic(
                _proxyTraffic,
                connection.Chains?.FirstOrDefault(),
                null,
                "DIRECT",
                uploadDelta,
                downloadDelta);
            changed = true;
        }

        foreach (var id in _lastConnections.Keys.Where(id => !activeIds.Contains(id)).ToArray())
        {
            _lastConnections.Remove(id);
        }

        if (changed)
        {
            RankingsChanged?.Invoke();
        }
    }

    private bool StartedAfterStatistics(string? start)
    {
        return DateTimeOffset.TryParse(start, out var startedAt) && startedAt >= _startedAt;
    }

    private static void AddTraffic(
        IDictionary<string, TrafficAccumulator> target,
        string? preferredLabel,
        string? fallbackLabel,
        string defaultLabel,
        long upload,
        long download)
    {
        var label = string.IsNullOrWhiteSpace(preferredLabel)
            ? string.IsNullOrWhiteSpace(fallbackLabel) ? defaultLabel : fallbackLabel.Trim()
            : preferredLabel.Trim();

        if (!target.TryGetValue(label, out var accumulator))
        {
            accumulator = new TrafficAccumulator();
            target[label] = accumulator;
        }

        accumulator.Upload += upload;
        accumulator.Download += download;
    }

    private static IReadOnlyList<TrafficRanking> CreateRankings(
        IReadOnlyDictionary<string, TrafficAccumulator> source)
    {
        return source
            .Select(pair => new TrafficRanking(pair.Key, pair.Value.Upload, pair.Value.Download))
            .OrderByDescending(item => item.Total)
            .ThenBy(item => item.Label, StringComparer.OrdinalIgnoreCase)
            .Take(RankingLimit)
            .ToArray();
    }

    private readonly record struct ConnectionTraffic(long Upload, long Download);

    private sealed class TrafficAccumulator
    {
        public long Upload { get; set; }
        public long Download { get; set; }
    }
}
