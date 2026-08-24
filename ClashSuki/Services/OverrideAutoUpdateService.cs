namespace ClashSuki.Services;

public sealed class OverrideAutoUpdateService : IAsyncDisposable
{
    private const int DefaultIntervalMinutes = 1440;
    private readonly OverrideService _overrideService;
    private readonly Func<string, CancellationToken, Task> _refreshOverrideAsync;
    private readonly PeriodicUpdateRunner _runner;

    public OverrideAutoUpdateService(
        OverrideService overrideService,
        Func<string, CancellationToken, Task> refreshOverrideAsync)
    {
        _overrideService = overrideService;
        _refreshOverrideAsync = refreshOverrideAsync;
        _runner = new PeriodicUpdateRunner(
            CheckDueOverridesAsync,
            LogSources.Override,
            "覆写自动更新检查失败",
            "覆写自动更新停止超时");
    }

    public void Start(CancellationToken appToken) => _runner.Start(appToken);

    public ValueTask DisposeAsync() => _runner.DisposeAsync();

    private async Task CheckDueOverridesAsync(CancellationToken token)
    {
        var config = await _overrideService.LoadAsync(token);
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        foreach (var entry in config.Items)
        {
            if (!entry.AutoUpdate ||
                !entry.Type.Equals("remote", StringComparison.OrdinalIgnoreCase) ||
                string.IsNullOrWhiteSpace(entry.Url))
            {
                continue;
            }

            var intervalMinutes = entry.Interval is > 0 ? entry.Interval.Value : DefaultIntervalMinutes;
            if (now - entry.UpdatedAt.ToUnixTimeSeconds() >= intervalMinutes * 60L)
            {
                await _refreshOverrideAsync(entry.Id, token);
            }
        }
    }
}
