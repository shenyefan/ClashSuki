using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using ClashSuki.ServiceContract;

namespace ClashSuki.Service;

internal sealed class Worker(
    ILogger<Worker> logger,
    IHostApplicationLifetime hostLifetime,
    NamedPipeClientAuthorizer clientAuthorizer,
    ServiceCommandDispatcher commandDispatcher,
    CoreProcessSupervisor coreSupervisor) : BackgroundService
{
    private static readonly JsonSerializerOptions JsonOptions = ServiceProtocol.CreateJsonOptions();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("ClashSuki 服务已启动，正在监听命名管道 {PipeName}", ServiceProtocol.PipeName);

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await using var pipeServer = NamedPipeFactory.CreateServer(ServiceProtocol.PipeName);
                    await pipeServer.WaitForConnectionAsync(stoppingToken);
                    await HandleClientAsync(pipeServer, stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "处理管道客户端时发生错误");
                    await Task.Delay(500, stoppingToken);
                }
            }
        }
        finally
        {
            try
            {
                await coreSupervisor.StopAsync(CancellationToken.None);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "服务停止时关闭内核失败");
            }

            logger.LogInformation("ClashSuki 服务已停止");
        }
    }

    private async Task HandleClientAsync(NamedPipeServerStream pipe, CancellationToken stoppingToken)
    {
        if (!clientAuthorizer.IsAuthorized(pipe, out var denialReason))
        {
            logger.LogWarning("拒绝命名管道客户端：{Reason}", denialReason);
            await WriteResponseAsync(pipe, ServiceResponse.Failure("无权访问 ClashSuki 服务。"), stoppingToken);
            return;
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));

        ServiceCommandResult result;
        try
        {
            using var reader = new StreamReader(pipe, Encoding.UTF8, leaveOpen: true);
            var line = await ReadBoundedLineAsync(reader, ServiceProtocol.MaxRequestCharacters, timeout.Token);
            if (string.IsNullOrWhiteSpace(line))
            {
                result = ServiceCommandResult.Failure("请求内容为空。");
            }
            else
            {
                var request = JsonSerializer.Deserialize<ServiceRequest>(line, JsonOptions);
                result = request is null
                    ? ServiceCommandResult.Failure("请求格式无效。")
                    : await commandDispatcher.DispatchAsync(request, timeout.Token);
            }
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "服务 IPC 请求不是有效的 JSON");
            result = ServiceCommandResult.Failure("请求格式无效。");
        }
        catch (InvalidDataException ex)
        {
            logger.LogWarning(ex, "服务 IPC 请求超出限制");
            result = ServiceCommandResult.Failure(ex.Message);
        }

        await WriteResponseAsync(pipe, result.Response, timeout.Token);
        if (result.StopHost)
        {
            hostLifetime.StopApplication();
        }
    }

    private static async Task<string?> ReadBoundedLineAsync(
        TextReader reader,
        int maxCharacters,
        CancellationToken cancellationToken)
    {
        var result = new StringBuilder(Math.Min(maxCharacters, 1024));
        var buffer = new char[1];

        while (true)
        {
            var read = await reader.ReadAsync(buffer.AsMemory(), cancellationToken);
            if (read == 0)
            {
                return result.Length == 0 ? null : result.ToString();
            }

            var character = buffer[0];
            if (character == '\n')
            {
                return result.ToString().TrimEnd('\r');
            }

            if (result.Length >= maxCharacters)
            {
                throw new InvalidDataException($"请求内容不能超过 {maxCharacters} 个字符。");
            }

            result.Append(character);
        }
    }

    private static async Task WriteResponseAsync(
        PipeStream pipe,
        ServiceResponse response,
        CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.Serialize(response, JsonOptions) + "\n";
        var bytes = Encoding.UTF8.GetBytes(payload);
        await pipe.WriteAsync(bytes, cancellationToken);
        await pipe.FlushAsync(cancellationToken);
    }
}
