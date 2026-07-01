using ClashSuki.Models;

namespace ClashSuki.Services;

/// <summary>
/// 按每条远程订阅的 auto_update / interval 定时拉取更新。
/// </summary>
public sealed class ProfileAutoUpdateService : IAsyncDisposable
{
    private const int DefaultIntervalMinutes = 1440;
    private static readonly TimeSpan PollInterval = TimeSpan.FromMinutes(1);

    private readonly ProfileService _profileService;
    private readonly Func<string, CancellationToken, Task> _updateProfileAsync;
    private readonly Action<string, string>? _log;
    private readonly SemaphoreSlim _updateLock = new(1, 1);

    private CancellationTokenSource? _cts;
    private Task? _loopTask;

    public ProfileAutoUpdateService(
        ProfileService profileService,
        Func<string, CancellationToken, Task> updateProfileAsync,
        Action<string, string>? log = null)
    {
        _profileService = profileService;
        _updateProfileAsync = updateProfileAsync;
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
                await CheckDueProfilesAsync(token);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _log?.Invoke("WARN", $"订阅自动更新检查失败：{ex.Message}");
            }
        }
    }

    private async Task CheckDueProfilesAsync(CancellationToken token)
    {
        var config = await _profileService.LoadAsync(token);
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        foreach (var profile in config.Items)
        {
            if (!profile.AutoUpdate ||
                !string.Equals(profile.Type, "remote", StringComparison.OrdinalIgnoreCase) ||
                string.IsNullOrWhiteSpace(profile.Url))
            {
                continue;
            }

            var intervalMinutes = profile.Interval is > 0 ? profile.Interval.Value : DefaultIntervalMinutes;
            var lastUpdated = profile.Updated ?? 0;
            if (now - lastUpdated < intervalMinutes * 60L)
            {
                continue;
            }

            await _updateLock.WaitAsync(token);
            try
            {
                _log?.Invoke("INFO", $"订阅自动更新开始；名称={profile.Name}");
                await _updateProfileAsync(profile.Uid, token);
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
                _log?.Invoke("WARN", $"订阅自动更新停止超时：{ex.GetType().Name}");
            }
        }

        _cts.Dispose();
        _cts = null;
        _loopTask = null;
        _updateLock.Dispose();
    }
}
