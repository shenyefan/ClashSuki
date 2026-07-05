using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using ClashSuki.ServiceContract;

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
    private readonly ServiceIpcClient _ipcClient = new();

    public async Task<MihomoServiceStatus> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        await AppPaths.BootstrapAsync(cancellationToken);

        if (!PackageIdentityService.IsPackaged)
        {
            return MihomoServiceStatus.InstallRequired;
        }

        if (!PackagedServiceController.IsInstalled())
        {
            return MihomoServiceStatus.InstallRequired;
        }

        if (!PackagedServiceController.IsRunning())
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

        if (!PackageIdentityService.IsPackaged)
        {
            return MihomoServiceStatus.InstallRequired;
        }

        if (!PackagedServiceController.IsInstalled())
        {
            return MihomoServiceStatus.InstallRequired;
        }

        var probe = await ProbeIpcAsync(cancellationToken);
        if (probe.IsCompatible)
        {
            return MihomoServiceStatus.Ready;
        }

        Exception? firstStartError = null;
        try
        {
            if (probe.IsReachable)
            {
                PackagedServiceController.Restart();
            }
            else
            {
                PackagedServiceController.Start();
            }

            await WaitUntilReadyAsync(TimeSpan.FromSeconds(15), cancellationToken);
            return MihomoServiceStatus.Ready;
        }
        catch (Exception ex)
        {
            firstStartError = ex;
        }

        try
        {
            PackagedServiceController.Restart();
            await WaitUntilReadyAsync(TimeSpan.FromSeconds(15), cancellationToken);
            return MihomoServiceStatus.Ready;
        }
        catch (Exception ex)
        {
            var failure = firstStartError is null
                ? ex
                : new AggregateException(firstStartError, ex);
            DiagnosticLog.WriteAppException(
                LogSources.Service,
                failure,
                "服务启动失败");
            return MihomoServiceStatus.Unavailable;
        }
    }

    public async Task RepairAsync(CancellationToken cancellationToken = default)
    {
        await AppPaths.BootstrapAsync(cancellationToken);
        if (!PackageIdentityService.IsPackaged)
        {
            throw new InvalidOperationException(
                "服务仅由 MSIX 包管理，请在 Visual Studio 中启动 ClashSuki.Package 项目");
        }

        if (PackagedServiceController.IsRunning())
        {
            await StopHostAsync(cancellationToken);
        }

        await PackageRepairLauncher.StartAfterCurrentProcessExitsAsync(cancellationToken);
    }

    public async Task StopHostAsync(CancellationToken cancellationToken = default)
    {
        if (!PackagedServiceController.IsRunning())
        {
            return;
        }

        try
        {
            await _ipcClient.SendAsync(
                new ServiceRequest { Command = ServiceCommands.StopService },
                cancellationToken);
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
                await Task.Run(PackagedServiceController.Stop, cancellationToken);
            }
            catch (Exception fallbackEx)
            {
                throw new InvalidOperationException(
                    "无法停止 ClashSuki 服务，请修复应用包后重试",
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

        throw new TimeoutException("服务已安装但 IPC 管道未就绪，请检查 Windows 服务 ClashSukiService 是否启动成功");
    }

    public async Task StartCoreAsync(
        string? configDirectory = null,
        CancellationToken cancellationToken = default)
    {
        var effectiveConfigDirectory = string.IsNullOrWhiteSpace(configDirectory)
            ? AppPaths.DataRoot
            : Path.GetFullPath(configDirectory);
        Directory.CreateDirectory(effectiveConfigDirectory);
        var settings = await AppSettingsService.LoadAsync(cancellationToken);

        var payload = new ServiceRequest
        {
            Command = ServiceCommands.StartCore,
            CorePath = AppPaths.ManagedCorePath,
            ConfigPath = AppPaths.RuntimeConfigPath,
            ConfigDir = effectiveConfigDirectory,
            CoreIpcPath = MihomoControllerEndpoint.PipePath,
            CorePriority = settings.MihomoCpuPriority
        };

        await _ipcClient.SendAsync(payload, cancellationToken);
        await WaitForCoreStateAsync(expectedRunning: true, TimeSpan.FromSeconds(10), cancellationToken);
    }

    public async Task SetCorePriorityAsync(
        string priority,
        CancellationToken cancellationToken = default)
    {
        await _ipcClient.SendAsync(
            new ServiceRequest
            {
                Command = ServiceCommands.SetCorePriority,
                CorePriority = priority
            },
            cancellationToken);
    }

    public async Task StopCoreAsync(CancellationToken cancellationToken = default)
    {
        await _ipcClient.SendAsync(
            new ServiceRequest { Command = ServiceCommands.StopCore },
            cancellationToken);
        await WaitForCoreStateAsync(expectedRunning: false, TimeSpan.FromSeconds(10), cancellationToken);
    }

    public async Task<(bool Running, int? Pid)> GetCoreStatusAsync(CancellationToken cancellationToken = default)
    {
        var response = await _ipcClient.SendAsync(
            new ServiceRequest { Command = ServiceCommands.GetStatus },
            cancellationToken);
        return (response.CoreRunning == true, response.CorePid);
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
                ? $"等待服务内核{target}超时"
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
            if (PackagedServiceController.IsRunning() == expectedRunning)
            {
                return;
            }

            await Task.Delay(200, cancellationToken);
        }

        throw new TimeoutException(expectedRunning
            ? "等待服务启动超时"
            : "等待服务停止超时");
    }

    private async Task<bool> CanConnectIpcAsync(CancellationToken cancellationToken)
    {
        return (await ProbeIpcAsync(cancellationToken)).IsCompatible;
    }

    private async Task<ServiceProbe> ProbeIpcAsync(CancellationToken cancellationToken)
    {
        try
        {
            var response = await _ipcClient.SendAsync(
                new ServiceRequest { Command = ServiceCommands.Ping },
                cancellationToken,
                connectTimeoutMilliseconds: 500);
            var protocolVersion = response.ProtocolVersion;
            return new ServiceProbe(
                IsReachable: true,
                IsCompatible: protocolVersion == ServiceProtocol.Version,
                ProtocolVersion: protocolVersion);
        }
        catch
        {
            return new ServiceProbe(false, false, null);
        }
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
            throw new OperationCanceledException("已取消管理员权限请求", ex, cancellationToken);
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
                    ? $"{command} 执行失败，退出码为 {process.ExitCode}"
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
            Path.GetFullPath(Path.Combine(baseDirectory, "..", "ClashSuki.Service", "ClashSuki.Service.exe"))
        };

        return candidates.FirstOrDefault(File.Exists)
               ?? throw new FileNotFoundException(
                   "找不到 ClashSuki.Service.exe，请重新生成 ClashSuki",
                   candidates[0]);
    }
}
