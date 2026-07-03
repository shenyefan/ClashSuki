namespace ClashSuki.Services;

/// <summary>
/// 按每条远程覆写的 auto_update / interval 定时拉取更新。
/// </summary>
public sealed class OverrideAutoUpdateService : IAsyncDisposable
{
    private const int DefaultIntervalMinutes = 1440;
    private static readonly TimeSpan PollInterval = TimeSpan.FromMinutes(1);

    private readonly OverrideService _overrideService;
    private readonly Func<string, CancellationToken, Task> _refreshOverrideAsync;
    private readonly Action<string, string>? _log;

    private readonly SemaphoreSlim _updateLock = new(1, 1);
    private CancellationTokenSource? _cts;
    private Task? _loopTask;

    public OverrideAutoUpdateService(
        OverrideService overrideService,
        Func<string, CancellationToken, Task> refreshOverrideAsync,
        Action<string, string>? log = null)
    {
        _overrideService = overrideService;
        _refreshOverrideAsync = refreshOverrideAsync;
        _log = log;
    }

    public void Start(CancellationToken appToken)
    {
        if (_loopTask is not null)
        {
            return;
        }

        _cts = CancellationTokenSource.CreateLinkedTokenSource(appToken);
        _loopTask = RunLoopAsync(_cts.Token);
    }

    private async Task RunLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(PollInterval, token);
                await CheckDueOverridesAsync(token);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _log?.Invoke("WARN", $"覆写自动更新检查失败：{ex.Message}");
            }
        }
    }

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
            var lastUpdated = entry.UpdatedAt.ToUnixTimeSeconds();
            if (now - lastUpdated < intervalMinutes * 60L)
            {
                continue;
            }

            await _updateLock.WaitAsync(token);
            try
            {
                await _refreshOverrideAsync(entry.Id, token);
            }
            finally
            {
                _updateLock.Release();
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_cts is null)
        {
            return;
        }

        await _cts.CancelAsync();
        if (_loopTask is not null)
        {
            try
            {
                await _loopTask.WaitAsync(TimeSpan.FromSeconds(3));
            }
            catch (Exception ex) when (ex is OperationCanceledException or TimeoutException)
            {
                _log?.Invoke("WARN", $"覆写自动更新停止超时：{ex.GetType().Name}");
            }
        }

        _cts.Dispose();
        _cts = null;
        _loopTask = null;
        _updateLock.Dispose();
    }
}
