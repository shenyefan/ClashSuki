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

        var serviceManager = new MihomoServiceManager();
        await serviceManager.EnsureAdministrativeServiceReadyAsync(cancellationToken);
        await serviceManager._ipcClient.SendAsync(
            new ServiceRequest
            {
                Command = ServiceCommands.ReplaceCore,
                CoreSourcePath = Path.GetFullPath(sourcePath),
                CoreDestinationPath = Path.GetFullPath(destinationPath)
            },
            cancellationToken);
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

    public async Task ConfigureFirewallAsync(
        IReadOnlyCollection<FirewallRuleRequest> rules,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(rules);
        if (rules.Count == 0 || rules.Count > ServiceProtocol.MaxFirewallRuleCount)
        {
            throw new InvalidOperationException(
                $"防火墙规则数量必须介于 1 和 {ServiceProtocol.MaxFirewallRuleCount} 之间。");
        }

        await EnsureAdministrativeServiceReadyAsync(cancellationToken);

        await _ipcClient.SendAsync(
            new ServiceRequest
            {
                Command = ServiceCommands.ConfigureFirewall,
                FirewallRules = rules.Cast<FirewallRuleRequest?>().ToArray()
            },
            cancellationToken);
    }

    public async Task SetLoopbackExemptionsAsync(
        IReadOnlyCollection<string> selectedSids,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(selectedSids);
        var normalizedSids = selectedSids
            .Where(static sid => !string.IsNullOrWhiteSpace(sid))
            .Select(static sid => sid.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (normalizedSids.Length > ServiceProtocol.MaxLoopbackExemptionCount)
        {
            throw new InvalidOperationException(
                $"回环豁免不能超过 {ServiceProtocol.MaxLoopbackExemptionCount} 项。");
        }

        await EnsureAdministrativeServiceReadyAsync(cancellationToken);
        await _ipcClient.SendAsync(
            new ServiceRequest
            {
                Command = ServiceCommands.SetLoopbackExemptions,
                LoopbackExemptSids = normalizedSids
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

    private async Task EnsureAdministrativeServiceReadyAsync(CancellationToken cancellationToken)
    {
        var serviceStatus = await EnsureReadyAsync(cancellationToken);
        if (serviceStatus != MihomoServiceStatus.Ready)
        {
            throw new InvalidOperationException(serviceStatus switch
            {
                MihomoServiceStatus.InstallRequired => "ClashSuki 服务尚未安装，请先修复应用包。",
                MihomoServiceStatus.Stopped => "ClashSuki 服务未运行。",
                _ => "ClashSuki 服务不可用，请先修复服务。"
            });
        }
    }
}
