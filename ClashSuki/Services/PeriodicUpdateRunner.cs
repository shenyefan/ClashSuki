namespace ClashSuki.Services;

internal sealed class PeriodicUpdateRunner(
    Func<CancellationToken, Task> cycle,
    string logSource,
    string cycleFailureMessage,
    string stopTimeoutMessage) : IAsyncDisposable
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMinutes(1);
    private CancellationTokenSource? _cts;
    private Task? _loopTask;

    public void Start(CancellationToken appToken)
    {
        if (_loopTask is not null)
        {
            return;
        }

        _cts = CancellationTokenSource.CreateLinkedTokenSource(appToken);
        _loopTask = RunAsync(_cts.Token);
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
                DiagnosticLog.WriteAppException(logSource, ex, stopTimeoutMessage, "WARN");
            }
        }

        _cts.Dispose();
        _cts = null;
        _loopTask = null;
    }

    private async Task RunAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(PollInterval, token);
                await cycle(token);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                DiagnosticLog.WriteAppException(logSource, ex, cycleFailureMessage, "WARN");
            }
        }
    }
}
