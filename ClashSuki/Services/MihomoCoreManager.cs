using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Principal;

namespace ClashSuki.Services;

public sealed class MihomoCoreManager : IAsyncDisposable
{
    // ClashSuki 默认 HTTP 外部控制地址（启用开关后写入配置）
    public const string FixedController = MihomoControllerEndpoint.DefaultHttpAddress;
    public const string ControllerPipePath = MihomoControllerEndpoint.PipePath;

    private readonly MihomoServiceManager _serviceManager = new();
    private readonly SemaphoreSlim _lifecycleLock = new(1, 1);
    private Process? _process;
    private EventHandler? _processExitedHandler;
    private CoreRunMode _runMode = CoreRunMode.NotRunning;

    public event Action<string>? CoreLogReceived;
    public event Action<string, string>? AppLogReceived;

    public bool IsRunning => _process?.HasExited == false;
    public int? ProcessId => IsRunning ? _process?.Id : null;
    public CoreRunMode RunMode => _runMode;
    public string WorkDirectory { get; set; } = AppPaths.DataRoot;

    /// <summary>当前进程是否以管理员身份运行（TUN sidecar 模式需要）。</summary>
    public static bool IsElevated { get; } = CheckElevated();

    private static bool CheckElevated()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch (Exception ex)
        {
            DiagnosticLog.WriteAppException("CORE-ELEVATION", ex, "检测管理员权限失败");
            return false;
        }
    }

    public async Task EnsureStartedAsync(bool requireTun = false, CancellationToken cancellationToken = default)
    {
        await _lifecycleLock.WaitAsync(cancellationToken);
        try
        {
            await EnsureStartedCoreAsync(requireTun, cancellationToken);
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    private async Task EnsureStartedCoreAsync(bool requireTun, CancellationToken cancellationToken)
    {
        await AppPaths.BootstrapAsync(cancellationToken);
        var serviceStatus = await _serviceManager.GetStatusAsync(cancellationToken);
        if (requireTun &&
            serviceStatus != MihomoServiceStatus.Ready &&
            MihomoServiceInstaller.IsInstalled())
        {
            serviceStatus = await _serviceManager.EnsureReadyAsync(cancellationToken);
        }

        if (!requireTun &&
            serviceStatus is MihomoServiceStatus.Ready or MihomoServiceStatus.Unavailable)
        {
            await _serviceManager.StopHostAsync(cancellationToken);
            serviceStatus = MihomoServiceStatus.Stopped;
            _runMode = CoreRunMode.NotRunning;
            EmitAppLog("虚拟网卡未启用，服务已停止，内核将使用子进程模式。");
        }

        if (_runMode == CoreRunMode.Service)
        {
            if (await IsServiceCoreRunningAsync(cancellationToken))
            {
                return;
            }

            _runMode = CoreRunMode.NotRunning;
        }

        if (requireTun &&
            serviceStatus == MihomoServiceStatus.Unavailable &&
            MihomoServiceInstaller.IsInstalled())
        {
            throw new InvalidOperationException(
                "ClashSuki 服务已安装但当前不可用。为避免同时启动多个 mihomo 内核，已取消 sidecar 回退；请修复或卸载服务后重试。");
        }

        if (requireTun &&
            serviceStatus == MihomoServiceStatus.Ready &&
            await IsServiceCoreRunningAsync(cancellationToken))
        {
            _runMode = CoreRunMode.Service;
            return;
        }

        if (!File.Exists(AppPaths.ManagedCorePath))
        {
            throw new FileNotFoundException(
                $"找不到 mihomo 内核，请将 mihomo.exe 放到 {Path.Combine("ClashSuki", "Assets", "Core", "mihomo.exe")} 或 {AppPaths.ManagedCorePath}。",
                AppPaths.ManagedCorePath);
        }

        // 清理上次异常退出遗留的内核进程，避免 9090/混合端口被自己占用
        KillOrphanCores();

        if (IsRunning)
        {
            if (!requireTun)
            {
                return;
            }

            if (serviceStatus == MihomoServiceStatus.Ready)
            {
                await StopCoreAsync(cancellationToken);
                await MihomoControllerEndpoint.PrepareRuntimeConfigForCoreAsync(cancellationToken);
                await StartByServiceAsync(cancellationToken);
                return;
            }

            if (!CanRunTun(serviceStatus))
            {
                EmitAppLog(
                    "虚拟网卡需要安装 ClashSuki 服务或管理员权限，内核继续以子进程模式运行（系统代理仍可用）。",
                    "WARN");
                return;
            }

            return;
        }

        // 混合端口被其他程序占用时自动换成空闲端口（参考 Clash Verge 的端口避让）
        await EnsureMixedPortAvailableAsync(cancellationToken);

        var settings = await AppSettingsService.LoadAsync(cancellationToken);
        if (settings.TestProfileOnStart)
        {
            await ValidateConfigAsync(cancellationToken);
        }

        if (requireTun && serviceStatus == MihomoServiceStatus.Ready)
        {
            await MihomoControllerEndpoint.PrepareRuntimeConfigForCoreAsync(
                cancellationToken,
                tunEnabledOverride: requireTun ? null : false);
            await StartByServiceAsync(cancellationToken);
            return;
        }

        if (requireTun && !CanRunTun(serviceStatus))
        {
            EmitAppLog(
                "虚拟网卡不可用，内核将以普通子进程模式启动（系统代理仍可用）。",
                "WARN");
            await MihomoControllerEndpoint.PrepareRuntimeConfigForCoreAsync(
                cancellationToken,
                tunEnabledOverride: false);
            await StartBySidecarAsync();
            return;
        }

        await MihomoControllerEndpoint.PrepareRuntimeConfigForCoreAsync(
            cancellationToken,
            tunEnabledOverride: requireTun ? null : false);
        await StartBySidecarAsync();
    }

    public async Task<bool> TryAdoptServiceCoreAsync(
        bool allowServiceMode,
        CancellationToken cancellationToken = default)
    {
        await _lifecycleLock.WaitAsync(cancellationToken);
        try
        {
            if (!allowServiceMode)
            {
                return _runMode == CoreRunMode.Sidecar && IsRunning;
            }

            if (_runMode != CoreRunMode.NotRunning)
            {
                return _runMode == CoreRunMode.Service || IsRunning;
            }

            if (await IsServiceCoreRunningAsync(cancellationToken))
            {
                _runMode = CoreRunMode.Service;
                return true;
            }

            return false;
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    private static bool CanRunTun(MihomoServiceStatus serviceStatus) =>
        serviceStatus == MihomoServiceStatus.Ready || IsElevated;

    /// <summary>
    /// 杀掉由本程序数据目录启动、但不归当前实例管理的 mihomo 进程
    /// （上次崩溃 / 调试遗留），防止端口被占导致新内核启动异常。
    /// </summary>
    private void KillOrphanCores()
    {
        foreach (var process in Process.GetProcessesByName("mihomo"))
        {
            try
            {
                if (process.Id == _process?.Id) continue;
                var path = process.MainModule?.FileName;
                if (string.Equals(path, AppPaths.ManagedCorePath, StringComparison.OrdinalIgnoreCase))
                {
                    process.Kill(entireProcessTree: true);
                    EmitAppLog($"已清理上次遗留的 mihomo 进程；进程标识={process.Id}");
                }
            }
            catch (Exception ex)
            {
                DiagnosticLog.WriteAppException(
                    "CORE-ORPHAN",
                    ex,
                    $"检查或清理遗留内核进程失败；进程标识={process.Id}");
            }
            finally
            {
                process.Dispose();
            }
        }
    }

    /// <summary>
    /// 检查配置中的 mixed-port 是否可绑定；被其他程序占用时自动改写为空闲端口。
    /// mihomo 绑定失败不会报错退出，而是悄悄把端口置 0，导致系统代理指向 127.0.0.1:0。
    /// </summary>
    private async Task EnsureMixedPortAvailableAsync(CancellationToken cancellationToken)
    {
        await YamlConfigService.EnsureMixedPortAvailableAsync(
            AppPaths.ConfigPath,
            IsPortFree,
            message => EmitAppLog(message),
            cancellationToken);
    }

    private static bool IsPortFree(int port)
    {
        // Windows 允许 0.0.0.0:p 与他人占用的 127.0.0.1:p 共存，
        // 必须分别检测回环与通配地址，两者都能绑定才算空闲
        foreach (var address in new[] { IPAddress.Loopback, IPAddress.Any })
        {
            try
            {
                using var listener = new TcpListener(address, port);
                listener.Start();
                listener.Stop();
            }
            catch (SocketException)
            {
                return false;
            }
        }
        return true;
    }

    private async Task StartByServiceAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(WorkDirectory);
        await _serviceManager.StartCoreAsync(WorkDirectory, cancellationToken);
        _runMode = CoreRunMode.Service;
        EmitAppLog($"mihomo 已通过服务模式启动；工作目录={WorkDirectory}");
    }

    private async Task<bool> IsServiceCoreRunningAsync(CancellationToken cancellationToken)
    {
        try
        {
            var (running, _) = await _serviceManager.GetCoreStatusAsync(cancellationToken);
            return running;
        }
        catch (Exception ex)
        {
            DiagnosticLog.WriteAppException("CORE-SERVICE-STATUS", ex, "读取服务内核运行状态失败");
            return false;
        }
    }

    private async Task StartBySidecarAsync()
    {
        Directory.CreateDirectory(WorkDirectory);
        var startInfo = new ProcessStartInfo
        {
            FileName = AppPaths.ManagedCorePath,
            WorkingDirectory = WorkDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        startInfo.ArgumentList.Add("-d");
        startInfo.ArgumentList.Add(WorkDirectory);
        startInfo.ArgumentList.Add("-f");
        startInfo.ArgumentList.Add(AppPaths.RuntimeConfigPath);
        startInfo.ArgumentList.Add("-ext-ctl-pipe");
        startInfo.ArgumentList.Add(ControllerPipePath);
        ClearProxyEnvironment(startInfo);

        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, args) => EmitCoreLog(args.Data);
        process.ErrorDataReceived += (_, args) => EmitCoreLog(args.Data);
        _processExitedHandler = (_, _) => OnSidecarExited(process);
        process.Exited += _processExitedHandler;

        if (!process.Start())
        {
            throw new InvalidOperationException("无法启动 mihomo 内核。");
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        _process = process;
        _runMode = CoreRunMode.Sidecar;
        await ApplyPriorityAsync(process);
        EmitAppLog($"mihomo 已通过子进程模式启动；进程标识={process.Id}；配置路径={AppPaths.RuntimeConfigPath}");
    }

    private static async Task ApplyPriorityAsync(Process process)
    {
        try
        {
            var settings = await AppSettingsService.LoadAsync(CancellationToken.None);
            process.PriorityClass = settings.MihomoCpuPriority.ToLowerInvariant() switch
            {
                "idle" => ProcessPriorityClass.Idle,
                "below_normal" => ProcessPriorityClass.BelowNormal,
                "above_normal" => ProcessPriorityClass.AboveNormal,
                "high" => ProcessPriorityClass.High,
                "real_time" => ProcessPriorityClass.RealTime,
                _ => ProcessPriorityClass.Normal
            };
        }
        catch (Exception ex)
        {
            DiagnosticLog.WriteAppException("CORE-PRIORITY", ex, "设置内核进程优先级失败");
        }
    }

    public async Task ApplyPriorityToRunningSidecarAsync()
    {
        if (_runMode != CoreRunMode.Sidecar || !IsRunning)
        {
            return;
        }

        var process = _process;
        if (process is null || process.HasExited)
        {
            return;
        }

        await ApplyPriorityAsync(process);
    }

    public async Task RestartAsync(bool requireTun = false, CancellationToken cancellationToken = default)
    {
        await _lifecycleLock.WaitAsync(cancellationToken);
        try
        {
            await StopCoreAsync(cancellationToken);
            await EnsureStartedCoreAsync(requireTun, cancellationToken);
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    public Task ValidateConfigAsync(CancellationToken cancellationToken = default) =>
        ValidateConfigAsync(AppPaths.ConfigPath, cancellationToken);

    public async Task ValidateConfigAsync(string configPath, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(WorkDirectory);
        var startInfo = new ProcessStartInfo
        {
            FileName = AppPaths.ManagedCorePath,
            WorkingDirectory = WorkDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        startInfo.ArgumentList.Add("-d");
        startInfo.ArgumentList.Add(WorkDirectory);
        startInfo.ArgumentList.Add("-f");
        startInfo.ArgumentList.Add(configPath);
        startInfo.ArgumentList.Add("-t");
        ClearProxyEnvironment(startInfo);

        using var process = Process.Start(startInfo)
                            ?? throw new InvalidOperationException("无法运行 mihomo 配置测试。");

        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);

        var output = (await outputTask + await errorTask).Trim();
        if (!string.IsNullOrWhiteSpace(output))
        {
            EmitAppLog($"mihomo 配置测试输出：{output}");
        }

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(output)
                ? $"mihomo 配置测试失败；退出代码={process.ExitCode}"
                : output);
        }
    }

    public async Task PrepareForCoreReplacementAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycleLock.WaitAsync(cancellationToken);
        try
        {
            if (MihomoServiceInstaller.IsInstalled())
            {
                try
                {
                    await _serviceManager.StopCoreAsync(cancellationToken);
                }
                catch (Exception ex)
                {
                    DiagnosticLog.WriteAppException(
                        "CORE-REPLACE",
                        ex,
                        "替换内核前停止服务内核失败，服务中可能没有正在运行的内核");
                }

                await WaitForServiceCoreStoppedAsync(cancellationToken);
            }

            await StopCoreAsync(cancellationToken);
            KillOrphanCores();
            await MihomoCoreFileInstaller.WaitForWritableAsync(AppPaths.ManagedCorePath, cancellationToken);
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    private async Task WaitForServiceCoreStoppedAsync(CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!await IsServiceCoreRunningAsync(cancellationToken))
            {
                return;
            }

            await Task.Delay(200, cancellationToken);
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycleLock.WaitAsync(cancellationToken);
        try
        {
            await StopCoreAsync(cancellationToken);
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    private async Task StopCoreAsync(CancellationToken cancellationToken)
    {
        if (_runMode == CoreRunMode.Service)
        {
            try
            {
                await _serviceManager.StopCoreAsync(cancellationToken);
            }
            finally
            {
                await _serviceManager.StopHostAsync(cancellationToken);
                _runMode = CoreRunMode.NotRunning;
            }
            EmitAppLog("mihomo 服务模式和服务宿主已停止。");
            return;
        }

        var process = _process;
        _process = null;
        _runMode = CoreRunMode.NotRunning;

        if (process is null)
        {
            return;
        }

        DetachExitedHandler(process);

        try
        {
            if (!process.HasExited)
            {
                TryCancelOutputRead(process);

                if (process.CloseMainWindow())
                {
                    await WaitForExitAsync(process, TimeSpan.FromMilliseconds(800), cancellationToken);
                }

                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync(cancellationToken);
                }

                EmitAppLog($"mihomo 子进程已停止；进程标识={process.Id}");
            }
        }
        catch (Exception ex)
        {
            EmitAppLog($"mihomo 停止失败：{ex.Message}", "ERROR");
            DiagnosticLog.WriteAppException("CORE-STOP", ex, "停止 mihomo 内核失败");
        }
        finally
        {
            process.Dispose();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync(CancellationToken.None);
        _lifecycleLock.Dispose();
    }

    private void OnSidecarExited(Process process)
    {
        try
        {
            if (ReferenceEquals(_process, process))
            {
                _process = null;
                _runMode = CoreRunMode.NotRunning;
                _processExitedHandler = null;
            }

            EmitAppLog($"mihomo 子进程异常退出；退出代码={SafeExitCode(process)}", "WARN");
        }
        catch (Exception ex)
        {
            EmitAppLog($"读取 mihomo 子进程退出状态失败：{ex.Message}", "ERROR");
            DiagnosticLog.WriteAppException("CORE-EXIT", ex, "读取 mihomo 子进程退出状态失败");
        }
        finally
        {
            try { process.Dispose(); }
            catch (Exception ex)
            {
                DiagnosticLog.WriteAppException("CORE-DISPOSE", ex, "释放 mihomo 进程资源失败");
            }
        }
    }

    private void DetachExitedHandler(Process process)
    {
        if (_processExitedHandler is null)
        {
            return;
        }

        process.Exited -= _processExitedHandler;
        _processExitedHandler = null;
    }

    private static async Task WaitForExitAsync(Process process, TimeSpan timeout, CancellationToken cancellationToken)
    {
        try
        {
            await process.WaitForExitAsync(cancellationToken).WaitAsync(timeout, cancellationToken);
        }
        catch (TimeoutException)
        {
            DiagnosticLog.WriteApp(
                "CORE-STOP",
                "WARN",
                $"等待内核进程正常退出超时；超时={timeout.TotalSeconds:0.#} 秒；将继续强制停止");
        }
    }

    private static void TryCancelOutputRead(Process process)
    {
        try { process.CancelOutputRead(); }
        catch (Exception ex)
        {
            DiagnosticLog.WriteAppException("CORE-OUTPUT", ex, "取消内核标准输出读取失败");
        }

        try { process.CancelErrorRead(); }
        catch (Exception ex)
        {
            DiagnosticLog.WriteAppException("CORE-OUTPUT", ex, "取消内核错误输出读取失败");
        }
    }

    private static string SafeExitCode(Process process)
    {
        try { return process.ExitCode.ToString(); }
        catch { return "未知"; }
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

    private void EmitCoreLog(string? message)
    {
        if (!string.IsNullOrWhiteSpace(message))
        {
            CoreLogReceived?.Invoke(message);
        }
    }

    private void EmitAppLog(string? message, string level = "INFO")
    {
        if (!string.IsNullOrWhiteSpace(message))
        {
            AppLogReceived?.Invoke(level, message);
        }
    }
}
