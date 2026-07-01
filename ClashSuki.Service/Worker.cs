using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace ClashSuki.Service;

public sealed class Worker : BackgroundService
{
    private const string PipeName = "ClashSukiService";
    private const string CoreControlPipePath = @"\\.\pipe\clashsuki-mihomo";
    private const int ServiceProtocolVersion = 3;
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    private readonly ILogger<Worker> _logger;
    private readonly IHostApplicationLifetime _hostLifetime;
    private Process? _coreProcess;
    private EventHandler? _coreExitedHandler;
    private readonly Lock _lock = new();

    public Worker(ILogger<Worker> logger, IHostApplicationLifetime hostLifetime)
    {
        _logger = logger;
        _hostLifetime = hostLifetime;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("ClashSuki 服务已启动，正在监听命名管道 {PipeName}。", PipeName);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var pipeServer = NamedPipeFactory.CreateServer(PipeName);

                await pipeServer.WaitForConnectionAsync(stoppingToken);
                _logger.LogDebug("客户端已连接。");

                await HandleClientAsync(pipeServer, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "处理管道客户端时发生错误。");
                await Task.Delay(500, stoppingToken);
            }
        }

        try
        {
            StopCore();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "服务停止时关闭内核失败。");
        }
        _logger.LogInformation("ClashSuki 服务已停止。");
    }

    private async Task HandleClientAsync(NamedPipeServerStream pipe, CancellationToken ct)
    {
        try
        {
            using var reader = new StreamReader(pipe, Encoding.UTF8, leaveOpen: true);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));

            var line = await reader.ReadLineAsync(timeout.Token);
            if (string.IsNullOrWhiteSpace(line))
            {
                await WriteResponseAsync(pipe, false, "请求内容为空。", ct);
                return;
            }

            _logger.LogDebug("收到请求：{Line}", line);

            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            var command = root.TryGetProperty("command", out var cmdEl) ? cmdEl.GetString() : null;

            switch (command)
            {
                case "ping":
                    await WritePingResponseAsync(pipe, ct);
                    break;

                case "get_status":
                    await WriteStatusAsync(pipe, ct);
                    break;

                case "start_core":
                    await HandleStartCoreAsync(pipe, root, ct);
                    break;

                case "stop_core":
                    try
                    {
                        StopCore();
                        await WriteResponseAsync(pipe, true, null, ct);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "停止内核命令执行失败。");
                        await WriteResponseAsync(pipe, false, ex.Message, ct);
                    }
                    break;

                case "stop_service":
                    StopCore();
                    await WriteResponseAsync(pipe, true, null, ct);
                    _hostLifetime.StopApplication();
                    break;

                default:
                    await WriteResponseAsync(pipe, false, $"未知命令：{command}", ct);
                    break;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "处理客户端请求时发生错误。");
            try
            {
                await WriteResponseAsync(pipe, false, ex.Message, ct);
            }
            catch (Exception responseEx)
            {
                _logger.LogWarning(responseEx, "向客户端返回错误响应失败。");
            }
        }
    }

    private async Task HandleStartCoreAsync(NamedPipeServerStream pipe, JsonElement root, CancellationToken ct)
    {
        var corePath = root.TryGetProperty("core_path", out var cp) ? cp.GetString() : null;
        var configPath = root.TryGetProperty("config_path", out var cf) ? cf.GetString() : null;
        var configDir = root.TryGetProperty("config_dir", out var cd) ? cd.GetString() : null;
        var coreIpcPath = root.TryGetProperty("core_ipc_path", out var ipc) ? ipc.GetString() : null;

        if (string.IsNullOrWhiteSpace(corePath) || string.IsNullOrWhiteSpace(configPath))
        {
            await WriteResponseAsync(pipe, false, "必须提供内核路径和配置文件路径。", ct);
            return;
        }

        if (!File.Exists(corePath))
        {
            await WriteResponseAsync(pipe, false, $"找不到内核文件：{corePath}", ct);
            return;
        }

        try
        {
            StartCore(corePath, configPath, configDir ?? Path.GetDirectoryName(configPath)!, coreIpcPath);
            await WriteResponseAsync(pipe, true, null, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "启动内核命令执行失败。");
            await WriteResponseAsync(pipe, false, ex.Message, ct);
        }
    }

    private void StartCore(string corePath, string configPath, string configDir, string? coreIpcPath)
    {
        lock (_lock)
        {
            StopCoreLocked();
            KillOrphanCores(corePath);

            var pipePath = string.IsNullOrWhiteSpace(coreIpcPath) ? CoreControlPipePath : coreIpcPath;
            _logger.LogInformation(
                "正在启动内核；内核路径={CorePath}；配置路径={ConfigPath}；控制管道={PipePath}",
                corePath,
                configPath,
                pipePath);

            var startInfo = new ProcessStartInfo
            {
                FileName = corePath,
                WorkingDirectory = configDir,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            startInfo.ArgumentList.Add("-d");
            startInfo.ArgumentList.Add(configDir);
            startInfo.ArgumentList.Add("-f");
            startInfo.ArgumentList.Add(configPath);
            startInfo.ArgumentList.Add("-ext-ctl-pipe");
            startInfo.ArgumentList.Add(pipePath);
            ClearProxyEnvironment(startInfo);

            var process = new Process
            {
                StartInfo = startInfo,
                EnableRaisingEvents = true
            };
            process.OutputDataReceived += OnCoreOutput;
            process.ErrorDataReceived += OnCoreOutput;
            _coreExitedHandler = (_, _) => OnCoreExited(process);
            process.Exited += _coreExitedHandler;

            if (!process.Start())
            {
                process.Dispose();
                throw new InvalidOperationException("无法启动 mihomo 内核进程。");
            }

            _coreProcess = process;
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            _logger.LogInformation("内核进程已启动；进程标识={Pid}", process.Id);
        }
    }

    private void StopCore()
    {
        lock (_lock)
        {
            StopCoreLocked();
        }
    }

    private void StopCoreLocked()
    {
        var process = _coreProcess;
        if (process is null)
        {
            return;
        }

        _coreProcess = null;
        if (_coreExitedHandler is not null)
        {
            process.Exited -= _coreExitedHandler;
            _coreExitedHandler = null;
        }

        try
        {
            TryCancelOutputRead(process);
            if (!process.HasExited)
            {
                _logger.LogInformation("正在停止内核进程；进程标识={Pid}", process.Id);
                process.Kill(entireProcessTree: true);
                if (!process.WaitForExit(5000))
                {
                    throw new TimeoutException($"等待 mihomo 进程 {process.Id} 停止超时。");
                }
            }
        }
        finally
        {
            process.OutputDataReceived -= OnCoreOutput;
            process.ErrorDataReceived -= OnCoreOutput;
            process.Dispose();
        }
    }

    private void OnCoreExited(Process process)
    {
        var exitCode = SafeExitCode(process);
        lock (_lock)
        {
            if (ReferenceEquals(_coreProcess, process))
            {
                _coreProcess = null;
                _coreExitedHandler = null;
            }
        }

        _logger.LogWarning("内核进程已退出；退出代码={ExitCode}", exitCode);
        try
        {
            process.OutputDataReceived -= OnCoreOutput;
            process.ErrorDataReceived -= OnCoreOutput;
            process.Dispose();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "清理已退出的内核进程失败，进程可能已被并发释放。");
        }
    }

    private void OnCoreOutput(object sender, DataReceivedEventArgs args)
    {
        if (!string.IsNullOrWhiteSpace(args.Data))
        {
            _logger.LogDebug("mihomo：{Message}", args.Data);
        }
    }

    private void KillOrphanCores(string corePath)
    {
        var expectedPath = Path.GetFullPath(corePath);
        foreach (var process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(corePath)))
        {
            var matchesManagedCore = false;
            try
            {
                if (ReferenceEquals(process, _coreProcess) || process.Id == Environment.ProcessId)
                {
                    continue;
                }

                var actualPath = process.MainModule?.FileName;
                matchesManagedCore = string.Equals(
                        Path.GetFullPath(actualPath ?? ""),
                        expectedPath,
                        StringComparison.OrdinalIgnoreCase);
                if (!matchesManagedCore)
                {
                    continue;
                }

                _logger.LogWarning("正在停止遗留的受管内核进程；进程标识={Pid}", process.Id);
                process.Kill(entireProcessTree: true);
                if (!process.WaitForExit(5000))
                {
                    throw new TimeoutException($"停止遗留的 mihomo 进程 {process.Id} 超时。");
                }
            }
            catch (Exception ex)
            {
                if (matchesManagedCore)
                {
                    throw new InvalidOperationException(
                        $"停止遗留的受管内核进程 {process.Id} 失败。",
                        ex);
                }

                _logger.LogWarning(ex, "检查或停止疑似遗留的内核进程失败；进程标识={Pid}", process.Id);
            }
            finally
            {
                process.Dispose();
            }
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
            _logger.LogDebug(ex, "取消内核标准输出读取失败。");
        }

        try
        {
            process.CancelErrorRead();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "取消内核标准错误读取失败。");
        }
    }

    private string SafeExitCode(Process process)
    {
        try
        {
            return process.ExitCode.ToString();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "读取内核退出代码失败。");
            return "未知";
        }
    }

    private async Task WriteStatusAsync(PipeStream pipe, CancellationToken ct)
    {
        int? pid;
        bool running;
        lock (_lock)
        {
            running = _coreProcess is { HasExited: false };
            pid = running ? _coreProcess!.Id : null;
        }

        var payload = new { ok = true, core_running = running, core_pid = pid };
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload, JsonOptions) + "\n");
        await pipe.WriteAsync(bytes, ct);
        await pipe.FlushAsync(ct);
    }

    private static async Task WriteResponseAsync(PipeStream pipe, bool ok, string? error, CancellationToken ct)
    {
        var response = ok
            ? """{"ok":true}"""
            : $$"""{"ok":false,"error":{{JsonSerializer.Serialize(error ?? "未知错误")}}}""";

        var bytes = Encoding.UTF8.GetBytes(response + "\n");
        await pipe.WriteAsync(bytes, ct);
        await pipe.FlushAsync(ct);
    }

    private static async Task WritePingResponseAsync(PipeStream pipe, CancellationToken ct)
    {
        var response = JsonSerializer.Serialize(new
        {
            ok = true,
            protocol_version = ServiceProtocolVersion
        });
        var bytes = Encoding.UTF8.GetBytes(response + "\n");
        await pipe.WriteAsync(bytes, ct);
        await pipe.FlushAsync(ct);
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
