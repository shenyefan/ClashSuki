namespace ClashSuki.Services;

public sealed class ProfileAutoUpdateService : IAsyncDisposable
{
    private const int DefaultIntervalMinutes = 1440;
    private readonly ProfileService _profileService;
    private readonly Func<string, CancellationToken, Task> _updateProfileAsync;
    private readonly PeriodicUpdateRunner _runner;

    public ProfileAutoUpdateService(
        ProfileService profileService,
        Func<string, CancellationToken, Task> updateProfileAsync)
    {
        _profileService = profileService;
        _updateProfileAsync = updateProfileAsync;
        _runner = new PeriodicUpdateRunner(
            CheckDueProfilesAsync,
            LogSources.Subscription,
            "订阅自动更新检查失败",
            "订阅自动更新停止超时");
    }

    public void Start(CancellationToken appToken) => _runner.Start(appToken);

    public ValueTask DisposeAsync() => _runner.DisposeAsync();

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
            if (now - (profile.Updated ?? 0) >= intervalMinutes * 60L)
            {
                await _updateProfileAsync(profile.Uid, token);
            }
        }
    }
}
