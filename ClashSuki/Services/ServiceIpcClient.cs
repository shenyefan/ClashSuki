using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using ClashSuki.ServiceContract;

namespace ClashSuki.Services;

internal sealed class ServiceIpcClient
{
    private static readonly JsonSerializerOptions JsonOptions = ServiceProtocol.CreateJsonOptions();

    public async Task<ServiceResponse> SendAsync(
        ServiceRequest request,
        CancellationToken cancellationToken,
        int connectTimeoutMilliseconds = 1500)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));

        await using var pipe = new NamedPipeClientStream(
            ".",
            PackageIdentityService.IsPackaged
                ? ServiceProtocol.PipeName
                : ServiceProtocol.PortablePipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous);
        await pipe.ConnectAsync(connectTimeoutMilliseconds, timeout.Token);

        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(request, JsonOptions) + "\n");
        await pipe.WriteAsync(bytes, timeout.Token);
        await pipe.FlushAsync(timeout.Token);

        using var reader = new StreamReader(pipe, Encoding.UTF8, leaveOpen: true);
        var responseLine = await reader.ReadLineAsync(timeout.Token);
        if (string.IsNullOrWhiteSpace(responseLine))
        {
            throw new InvalidOperationException("服务 IPC 未返回执行结果。");
        }

        var response = JsonSerializer.Deserialize<ServiceResponse>(responseLine, JsonOptions)
                       ?? throw new InvalidOperationException("服务 IPC 返回了无效结果。");
        if (!response.Ok)
        {
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(response.Error)
                ? "服务 IPC 命令执行失败。"
                : response.Error);
        }

        return response;
    }
}
