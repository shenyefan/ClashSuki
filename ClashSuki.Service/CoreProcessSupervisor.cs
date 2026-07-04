using System.Diagnostics;

namespace ClashSuki.Service;

internal sealed class CoreProcessSupervisor(ILogger<CoreProcessSupervisor> logger) : IAsyncDisposable
{
    private static readonly TimeSpan ProcessExitTimeout = TimeSpan.FromSeconds(5);
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private Process? _process;
    private EventHandler? _exitedHandler;
    private bool _disposed;

    public async Task<CoreProcessStatus> GetStatusAsync(CancellationToken cancellationToken)
    {
        await _lifecycleGate.WaitAsync(cancellationToken);
        try
        {
            var running = _process is { HasExited: false };
            return new CoreProcessStatus(running, running ? _process!.Id : null);
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public async Task StartAsync(CoreLaunchOptions options, CancellationToken cancellationToken)
    {
        await _lifecycleGate.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            await StopLockedAsync(cancellationToken);
            await KillOrphanCoresAsync(options.CorePath, cancellationToken);

            logger.LogInformation(
                "正在启动内核，内核路径: {CorePath}，配置路径: {ConfigPath}，控制管道: {PipePath}",
                options.CorePath,
                options.ConfigPath,
                options.ControlPipePath);

            var startInfo = new ProcessStartInfo
            {
                FileName = options.CorePath,
                WorkingDirectory = options.ConfigDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            startInfo.ArgumentList.Add("-d");
            startInfo.ArgumentList.Add(options.ConfigDirectory);
            startInfo.ArgumentList.Add("-f");
            startInfo.ArgumentList.Add(options.ConfigPath);
            startInfo.ArgumentList.Add("-ext-ctl-pipe");
            startInfo.ArgumentList.Add(options.ControlPipePath);
            ClearProxyEnvironment(startInfo);

            var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
            process.OutputDataReceived += OnCoreOutput;
            process.ErrorDataReceived += OnCoreOutput;
            var exitedHandler = new EventHandler((_, _) => _ = HandleCoreExitedAsync(process));
            process.Exited += exitedHandler;

            try
            {
                if (!process.Start())
                {
                    throw new InvalidOperationException("无法启动 mihomo 内核进程");
                }

                _process = process;
                _exitedHandler = exitedHandler;
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
                logger.LogInformation("内核进程已启动，进程标识: {Pid}", process.Id);
            }
            catch
            {
                process.Exited -= exitedHandler;
                DetachOutputAndDispose(process);
                throw;
            }
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await _lifecycleGate.WaitAsync(cancellationToken);
        try
        {
            await StopLockedAsync(cancellationToken);
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _lifecycleGate.WaitAsync();
        try
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            await StopLockedAsync(CancellationToken.None);
        }
        finally
        {
            _lifecycleGate.Release();
            _lifecycleGate.Dispose();
        }
    }

    private async Task StopLockedAsync(CancellationToken cancellationToken)
    {
        var process = _process;
        if (process is null)
        {
            return;
        }

        _process = null;
        if (_exitedHandler is not null)
        {
            process.Exited -= _exitedHandler;
            _exitedHandler = null;
        }

        try
        {
            TryCancelOutputRead(process);
            if (!process.HasExited)
            {
                logger.LogInformation("正在停止内核进程，进程标识: {Pid}", process.Id);
                process.Kill(entireProcessTree: true);
                await WaitForExitAsync(process, ProcessExitTimeout, cancellationToken);
            }
        }
        finally
        {
            DetachOutputAndDispose(process);
        }
    }

    private async Task HandleCoreExitedAsync(Process process)
    {
        try
        {
            await _lifecycleGate.WaitAsync();
            try
            {
                if (!ReferenceEquals(_process, process))
                {
                    return;
                }

                _process = null;
                _exitedHandler = null;
            }
            finally
            {
                _lifecycleGate.Release();
            }

            logger.LogWarning("内核进程已退出，退出代码: {ExitCode}", SafeExitCode(process));
            DetachOutputAndDispose(process);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "处理内核退出事件失败");
        }
    }

    private void OnCoreOutput(object sender, DataReceivedEventArgs args)
    {
        if (!string.IsNullOrWhiteSpace(args.Data))
        {
            logger.LogDebug("mihomo：{Message}", args.Data);
        }
    }

    private async Task KillOrphanCoresAsync(string corePath, CancellationToken cancellationToken)
    {
        var expectedPath = Path.GetFullPath(corePath);
        foreach (var process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(corePath)))
        {
            var matchesManagedCore = false;
            try
            {
                if (ReferenceEquals(process, _process) || process.Id == Environment.ProcessId)
                {
                    continue;
                }

                var actualPath = process.MainModule?.FileName;
                matchesManagedCore = string.Equals(
                    Path.GetFullPath(actualPath ?? string.Empty),
                    expectedPath,
                    StringComparison.OrdinalIgnoreCase);
                if (!matchesManagedCore)
                {
                    continue;
                }

                logger.LogWarning("正在停止遗留的受管内核进程，进程标识: {Pid}", process.Id);
                process.Kill(entireProcessTree: true);
                await WaitForExitAsync(process, ProcessExitTimeout, cancellationToken);
            }
            catch (Exception ex)
            {
                if (matchesManagedCore)
                {
                    throw new InvalidOperationException($"停止遗留的受管内核进程 {process.Id} 失败。", ex);
                }

                logger.LogWarning(ex, "检查疑似遗留的内核进程失败，进程标识: {Pid}", process.Id);
            }
            finally
            {
                process.Dispose();
            }
        }
    }

    private static async Task WaitForExitAsync(
        Process process,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        try
        {
            await process.WaitForExitAsync(cancellationToken).WaitAsync(timeout, cancellationToken);
        }
        catch (System.TimeoutException ex)
        {
            throw new System.TimeoutException($"等待进程 {process.Id} 停止超时。", ex);
        }
    }

    private void TryCancelOutputRead(Process process)
    {
        try
        {
            process.CancelOutputRead();
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "取消内核标准输出读取失败");
        }

        try
        {
            process.CancelErrorRead();
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "取消内核标准错误读取失败");
        }
    }

    private void DetachOutputAndDispose(Process process)
    {
        process.OutputDataReceived -= OnCoreOutput;
        process.ErrorDataReceived -= OnCoreOutput;
        process.Dispose();
    }

    private string SafeExitCode(Process process)
    {
        try
        {
            return process.ExitCode.ToString();
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "读取内核退出代码失败");
            return "未知";
        }
    }

    private static void ClearProxyEnvironment(ProcessStartInfo startInfo)
    {
        foreach (var name in new[]
                 {
                     "HTTP_PROXY", "HTTPS_PROXY", "ALL_PROXY", "NO_PROXY",
                     "http_proxy", "https_proxy", "all_proxy", "no_proxy"
                 })
        {
            startInfo.Environment.Remove(name);
        }
    }
}

internal readonly record struct CoreProcessStatus(bool Running, int? ProcessId);
