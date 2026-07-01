using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace ClashSuki.Services;

public enum CoreRunMode
{
    Service,
    Sidecar,
    NotRunning
}

public enum MihomoServiceStatus
{
    Ready,
    Stopped,
    InstallRequired,
    Unavailable
}

public sealed class MihomoServiceManager
{
    internal const string ServicePipeName = "ClashSukiService";
    private const int ServiceProtocolVersion = 3;

    public async Task<MihomoServiceStatus> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        await AppPaths.BootstrapAsync(cancellationToken);

        if (!MihomoServiceInstaller.IsInstalled())
        {
            return MihomoServiceStatus.InstallRequired;
        }

        if (!MihomoServiceInstaller.IsRunning())
        {
            return MihomoServiceStatus.Stopped;
        }

        return await CanConnectIpcAsync(cancellationToken)
            ? MihomoServiceStatus.Ready
            : MihomoServiceStatus.Unavailable;
    }

    public async Task<MihomoServiceStatus> EnsureReadyAsync(CancellationToken cancellationToken = default)
    {
        await AppPaths.BootstrapAsync(cancellationToken);

        if (!MihomoServiceInstaller.IsInstalled())
        {
            return MihomoServiceStatus.InstallRequired;
        }

        var probe = await ProbeIpcAsync(cancellationToken);
        if (probe.IsCompatible)
        {
            return MihomoServiceStatus.Ready;
        }

        try
        {
            if (probe.IsReachable)
            {
                DiagnosticLog.WriteApp(
                    "SERVICE",
                    $"服务协议版本不匹配；期望版本={ServiceProtocolVersion}；实际版本={probe.ProtocolVersion?.ToString() ?? "未知"}；正在重启服务。");
                MihomoServiceInstaller.Restart();
            }
            else
            {
                MihomoServiceInstaller.Start();
            }

            await WaitUntilReadyAsync(TimeSpan.FromSeconds(15), cancellationToken);
            return MihomoServiceStatus.Ready;
        }
        catch (Exception ex)
        {
            DiagnosticLog.WriteAppException("SERVICE", ex, "已安装的服务未就绪，首次启动尝试失败");
        }

        try
        {
            MihomoServiceInstaller.Restart();
            await WaitUntilReadyAsync(TimeSpan.FromSeconds(15), cancellationToken);
            return MihomoServiceStatus.Ready;
        }
        catch (Exception ex)
        {
            DiagnosticLog.WriteAppException("SERVICE", ex, "重启已安装的服务失败");
            return MihomoServiceStatus.Unavailable;
        }
    }

    public async Task InstallAsync(CancellationToken cancellationToken = default)
    {
        if (PackageIdentityService.IsPackaged)
        {
            throw new InvalidOperationException("打包版本的服务由 MSIX 注册，请修复或重新安装应用包。");
        }

        await AppPaths.BootstrapAsync(cancellationToken);

        if (MihomoCoreManager.IsElevated)
        {
            MihomoServiceInstaller.Install();
            await WaitUntilReadyAsync(TimeSpan.FromSeconds(10), cancellationToken);
            return;
        }

        await RunElevatedServiceAsync(cancellationToken, "--install-service");
        await WaitUntilReadyAsync(TimeSpan.FromSeconds(10), cancellationToken);
    }

    public async Task UninstallAsync(CancellationToken cancellationToken = default)
    {
        if (PackageIdentityService.IsPackaged)
        {
            throw new InvalidOperationException("打包版本的服务由 MSIX 管理，卸载应用包时会一并移除。");
        }

        await AppPaths.BootstrapAsync(cancellationToken);

        if (MihomoCoreManager.IsElevated)
        {
            MihomoServiceInstaller.Uninstall();
            return;
        }

        await RunElevatedServiceAsync(cancellationToken, "--uninstall-service");
    }

    public async Task StopHostAsync(CancellationToken cancellationToken = default)
    {
        if (!MihomoServiceInstaller.IsRunning())
        {
            return;
        }

        try
        {
            await SendIpcAsync(new { command = "stop_service" }, cancellationToken);
            await WaitForHostStateAsync(expectedRunning: false, TimeSpan.FromSeconds(10), cancellationToken);
        }
        catch (Exception ex)
        {
            DiagnosticLog.WriteAppException(
                LogSources.Service,
                ex,
                "通过 IPC 停止服务失败，正在使用服务控制器重试",
                "WARN");
            try
            {
                await Task.Run(MihomoServiceInstaller.Stop, cancellationToken);
            }
            catch (Exception fallbackEx)
            {
                throw new InvalidOperationException(
                    "无法停止 ClashSuki 服务。请以管理员身份重试或重新安装服务。",
                    new AggregateException(ex, fallbackEx));
            }
        }
    }

    public static async Task ReplaceCoreFileElevatedAsync(
        string sourcePath,
        string destinationPath,
        CancellationToken cancellationToken = default)
    {
        if (MihomoCoreManager.IsElevated)
        {
            await MihomoCoreFileInstaller.ReplaceInProcessAsync(sourcePath, destinationPath, cancellationToken);
            return;
        }

        await RunElevatedServiceAsync(cancellationToken, "--replace-core", sourcePath, destinationPath);
    }

    public async Task WaitUntilReadyAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (await CanConnectIpcAsync(cancellationToken))
            {
                return;
            }

            await Task.Delay(500, cancellationToken);
        }

        throw new TimeoutException("服务已安装，但 IPC 管道未就绪。请检查 Windows 服务 ClashSukiService 是否启动成功。");
    }

    public async Task StartCoreAsync(
        string? configDirectory = null,
        CancellationToken cancellationToken = default)
    {
        var effectiveConfigDirectory = string.IsNullOrWhiteSpace(configDirectory)
            ? AppPaths.DataRoot
            : Path.GetFullPath(configDirectory);
        Directory.CreateDirectory(effectiveConfigDirectory);

        var payload = new
        {
            command = "start_core",
            core_path = AppPaths.ManagedCorePath,
            config_path = AppPaths.RuntimeConfigPath,
            config_dir = effectiveConfigDirectory,
            core_ipc_path = MihomoControllerEndpoint.PipePath
        };

        await SendIpcAsync(payload, cancellationToken);
        await WaitForCoreStateAsync(expectedRunning: true, TimeSpan.FromSeconds(10), cancellationToken);
    }

    public async Task StopCoreAsync(CancellationToken cancellationToken = default)
    {
        await SendIpcAsync(new { command = "stop_core" }, cancellationToken);
        await WaitForCoreStateAsync(expectedRunning: false, TimeSpan.FromSeconds(10), cancellationToken);
    }

    public async Task<(bool Running, int? Pid)> GetCoreStatusAsync(CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));

        await using var pipe = new NamedPipeClientStream(".", ServicePipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(1500, timeout.Token);

        var request = Encoding.UTF8.GetBytes("""{"command":"get_status"}""" + "\n");
        await pipe.WriteAsync(request, timeout.Token);
        await pipe.FlushAsync(timeout.Token);

        using var reader = new StreamReader(pipe, Encoding.UTF8, leaveOpen: true);
        var responseLine = await reader.ReadLineAsync(timeout.Token);
        if (string.IsNullOrWhiteSpace(responseLine))
        {
            throw new InvalidOperationException("服务 IPC 未返回状态。");
        }

        using var doc = JsonDocument.Parse(responseLine);
        var root = doc.RootElement;
        var ok = root.TryGetProperty("ok", out var okElement) && okElement.GetBoolean();
        if (!ok)
        {
            throw new InvalidOperationException("服务 IPC 状态查询失败。");
        }

        var running = root.TryGetProperty("core_running", out var runningElement) && runningElement.GetBoolean();
        int? pid = root.TryGetProperty("core_pid", out var pidElement) && pidElement.ValueKind == JsonValueKind.Number
            ? pidElement.GetInt32()
            : null;
        return (running, pid);
    }

    private async Task WaitForCoreStateAsync(
        bool expectedRunning,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + timeout;
        Exception? lastError = null;

        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var (running, _) = await GetCoreStatusAsync(cancellationToken);
                if (running == expectedRunning)
                {
                    return;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                lastError = ex;
            }

            await Task.Delay(200, cancellationToken);
        }

        var target = expectedRunning ? "启动" : "停止";
        throw new TimeoutException(
            lastError is null
                ? $"等待服务内核{target}超时。"
                : $"等待服务内核{target}超时：{lastError.Message}",
            lastError);
    }

    private static async Task WaitForHostStateAsync(
        bool expectedRunning,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (MihomoServiceInstaller.IsRunning() == expectedRunning)
            {
                return;
            }

            await Task.Delay(200, cancellationToken);
        }

        throw new TimeoutException(expectedRunning
            ? "等待服务启动超时。"
            : "等待服务停止超时。");
    }

    private static async Task<bool> CanConnectIpcAsync(CancellationToken cancellationToken)
    {
        return (await ProbeIpcAsync(cancellationToken)).IsCompatible;
    }

    private static async Task<ServiceProbe> ProbeIpcAsync(CancellationToken cancellationToken)
    {
        try
        {
            var response = await SendIpcAsync(
                new { command = "ping" },
                cancellationToken,
                connectTimeoutMs: 500);
            var protocolVersion = response.TryGetProperty("protocol_version", out var versionElement) &&
                                  versionElement.ValueKind == JsonValueKind.Number
                ? versionElement.GetInt32()
                : (int?)null;
            return new ServiceProbe(
                IsReachable: true,
                IsCompatible: protocolVersion == ServiceProtocolVersion,
                ProtocolVersion: protocolVersion);
        }
        catch
        {
            return new ServiceProbe(false, false, null);
        }
    }

    private static async Task<JsonElement> SendIpcAsync(
        object payload,
        CancellationToken cancellationToken,
        int connectTimeoutMs = 1500)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));

        await using var pipe = new NamedPipeClientStream(".", ServicePipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(connectTimeoutMs, timeout.Token);

        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload) + "\n");
        await pipe.WriteAsync(bytes, timeout.Token);
        await pipe.FlushAsync(timeout.Token);

        using var reader = new StreamReader(pipe, Encoding.UTF8, leaveOpen: true);
        var responseLine = await reader.ReadLineAsync(timeout.Token);
        if (string.IsNullOrWhiteSpace(responseLine))
        {
            throw new InvalidOperationException("服务 IPC 未返回执行结果。");
        }

        using var doc = JsonDocument.Parse(responseLine);
        var root = doc.RootElement;
        var ok = root.TryGetProperty("ok", out var okElement) && okElement.GetBoolean();
        if (!ok)
        {
            var error = root.TryGetProperty("error", out var errorElement)
                ? errorElement.GetString()
                : null;
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(error)
                ? "服务 IPC 命令执行失败。"
                : error);
        }

        return root.Clone();
    }

    private readonly record struct ServiceProbe(
        bool IsReachable,
        bool IsCompatible,
        int? ProtocolVersion);

    private static async Task RunElevatedServiceAsync(CancellationToken cancellationToken, params string[] arguments)
    {
        var exePath = ResolveServiceExecutablePath();
        Process? process;
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = exePath,
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden
            };

            foreach (var argument in arguments)
            {
                startInfo.ArgumentList.Add(argument);
            }

            process = Process.Start(startInfo)
                      ?? throw new InvalidOperationException(
                          $"无法启动命令：{CommandLineFormatter.Format(exePath, arguments)}");
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            throw new OperationCanceledException("已取消管理员权限请求。", ex, cancellationToken);
        }

        using (process)
        {
            await process.WaitForExitAsync(cancellationToken);
            if (process.ExitCode != 0)
            {
                var hint = process.ExitCode switch
                {
                    1 => "请以管理员身份运行，并确认 UAC 提权对话框已允许。",
                    5 => "访问被拒绝，请确认已授予管理员权限。",
                    _ => null
                };
                var command = CommandLineFormatter.Format(Path.GetFileName(exePath), arguments);
                var detail = ReadServiceInstallLogTail();
                var baseMessage = hint is null
                    ? $"{command} 执行失败，退出码为 {process.ExitCode}。"
                    : $"{command} 失败（退出码 {process.ExitCode}）：{hint}";
                throw new InvalidOperationException(string.IsNullOrWhiteSpace(detail)
                    ? baseMessage
                    : $"{baseMessage}{Environment.NewLine}服务诊断日志：{Environment.NewLine}{detail}");
            }
        }
    }

    private static string? ReadServiceInstallLogTail()
    {
        try
        {
            var logPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "ClashSuki",
                "service-install.log");
            if (!File.Exists(logPath))
            {
                return null;
            }

            var lines = File.ReadAllLines(logPath);
            return string.Join(Environment.NewLine, lines.TakeLast(15));
        }
        catch (Exception ex)
        {
            DiagnosticLog.WriteAppExceptionThrottled(
                "service-install-log-read",
                LogSources.Service,
                ex,
                "读取服务安装诊断日志失败",
                level: "WARN");
            return null;
        }
    }

    private static string ResolveServiceExecutablePath()
    {
        var baseDirectory = AppContext.BaseDirectory;
        var candidates = new[]
        {
            Path.Combine(baseDirectory, "ClashSuki.Service.exe"),
            Path.Combine(baseDirectory, "AppX", "ClashSuki.Service.exe")
        };

        return candidates.FirstOrDefault(File.Exists)
               ?? throw new FileNotFoundException(
                   "找不到 ClashSuki.Service.exe，请重新生成 ClashSuki。",
                   candidates[0]);
    }
}
