using ClashSuki.ServiceContract;

namespace ClashSuki.Service;

internal sealed class ServiceCommandDispatcher(
    CoreProcessSupervisor coreSupervisor,
    CoreLaunchRequestValidator launchValidator,
    WindowsFirewallManager firewallManager,
    LoopbackExemptionManager loopbackExemptionManager,
    ILogger<ServiceCommandDispatcher> logger)
{
    public async Task<ServiceCommandResult> DispatchAsync(
        ServiceRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Command))
        {
            return ServiceCommandResult.Failure("请求命令不能为空。");
        }

        try
        {
            return request.Command switch
            {
                ServiceCommands.Ping => ServiceCommandResult.Success(new ServiceResponse
                {
                    Ok = true,
                    ProtocolVersion = ServiceProtocol.Version
                }),
                ServiceCommands.GetStatus => await GetStatusAsync(cancellationToken),
                ServiceCommands.StartCore => await StartCoreAsync(request, cancellationToken),
                ServiceCommands.SetCorePriority => await SetCorePriorityAsync(request, cancellationToken),
                ServiceCommands.StopCore => await StopCoreAsync(cancellationToken),
                ServiceCommands.ConfigureFirewall => ConfigureFirewall(request, cancellationToken),
                ServiceCommands.SetLoopbackExemptions => SetLoopbackExemptions(request, cancellationToken),
                ServiceCommands.ReplaceCore => await ReplaceCoreAsync(request, cancellationToken),
                ServiceCommands.StopService => await StopServiceAsync(cancellationToken),
                _ => ServiceCommandResult.Failure($"未知命令：{request.Command}")
            };
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "服务命令执行失败，命令: {Command}", request.Command);
            return ServiceCommandResult.Failure(ToClientMessage(ex));
        }
    }

    private async Task<ServiceCommandResult> GetStatusAsync(CancellationToken cancellationToken)
    {
        var status = await coreSupervisor.GetStatusAsync(cancellationToken);
        return ServiceCommandResult.Success(new ServiceResponse
        {
            Ok = true,
            CoreRunning = status.Running,
            CorePid = status.ProcessId
        });
    }

    private async Task<ServiceCommandResult> StartCoreAsync(
        ServiceRequest request,
        CancellationToken cancellationToken)
    {
        await coreSupervisor.StartAsync(launchValidator.Validate(request), cancellationToken);
        return ServiceCommandResult.Success(ServiceResponse.Success());
    }

    private async Task<ServiceCommandResult> StopCoreAsync(CancellationToken cancellationToken)
    {
        await coreSupervisor.StopAsync(cancellationToken);
        return ServiceCommandResult.Success(ServiceResponse.Success());
    }

    private async Task<ServiceCommandResult> SetCorePriorityAsync(
        ServiceRequest request,
        CancellationToken cancellationToken)
    {
        await coreSupervisor.SetPriorityAsync(
            CoreLaunchRequestValidator.NormalizePriority(request.CorePriority),
            cancellationToken);
        return ServiceCommandResult.Success(ServiceResponse.Success());
    }

    private ServiceCommandResult ConfigureFirewall(
        ServiceRequest request,
        CancellationToken cancellationToken)
    {
        firewallManager.Configure(request.FirewallRules, cancellationToken);
        return ServiceCommandResult.Success(ServiceResponse.Success());
    }

    private ServiceCommandResult SetLoopbackExemptions(
        ServiceRequest request,
        CancellationToken cancellationToken)
    {
        loopbackExemptionManager.SetExemptions(request.LoopbackExemptSids, cancellationToken);
        return ServiceCommandResult.Success(ServiceResponse.Success());
    }

    private async Task<ServiceCommandResult> ReplaceCoreAsync(
        ServiceRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.CoreSourcePath) ||
            string.IsNullOrWhiteSpace(request.CoreDestinationPath))
        {
            throw new InvalidOperationException("内核替换路径不能为空。");
        }

        await coreSupervisor.StopAsync(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        CoreReplacer.Replace(request.CoreSourcePath, request.CoreDestinationPath);
        return ServiceCommandResult.Success(ServiceResponse.Success());
    }

    private async Task<ServiceCommandResult> StopServiceAsync(CancellationToken cancellationToken)
    {
        await coreSupervisor.StopAsync(cancellationToken);
        return new ServiceCommandResult(ServiceResponse.Success(), StopHost: true);
    }

    private static string ToClientMessage(Exception exception)
    {
        return exception switch
        {
            FileNotFoundException or DirectoryNotFoundException or InvalidOperationException or TimeoutException
                => exception.Message,
            _ => "服务执行命令失败，请查看服务日志。"
        };
    }
}

internal readonly record struct ServiceCommandResult(ServiceResponse Response, bool StopHost)
{
    public static ServiceCommandResult Success(ServiceResponse response) => new(response, false);

    public static ServiceCommandResult Failure(string error) => new(ServiceResponse.Failure(error), false);
}
