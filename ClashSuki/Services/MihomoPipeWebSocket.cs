using System.IO.Pipes;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;

namespace ClashSuki.Services;

/// <summary>
/// 通过 mihomo 命名管道建立 WebSocket 订阅（与 Clash Verge 的 ws_traffic / connect_memory 一致）。
/// 握手为最小手写 HTTP Upgrade（命名管道无法复用 ClientWebSocket），握手成功后交给
/// 框架内置的 <see cref="WebSocket.CreateFromStream"/> 处理帧解析、分片、ping/pong 与 close。
/// </summary>
internal sealed class MihomoPipeWebSocket : IAsyncDisposable
{
    private const string WebSocketGuid = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";

    private NamedPipeClientStream? _pipe;
    private WebSocket? _webSocket;

    public async Task ConnectAsync(string path, string? secret, CancellationToken cancellationToken)
    {
        await DisposeAsync();

        var pipe = new NamedPipeClientStream(
            ".",
            MihomoControllerEndpoint.PipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous);
        await pipe.ConnectAsync(5000, cancellationToken);
        _pipe = pipe;

        await HandshakeAsync(pipe, path, secret, cancellationToken);

        // 握手已逐字节读到 \r\n\r\n 为止，pipe 中剩余字节都是 WebSocket 帧，可安全交给框架解析。
        _webSocket = WebSocket.CreateFromStream(
            pipe,
            isServer: false,
            subProtocol: null,
            keepAliveInterval: TimeSpan.FromSeconds(30));
    }

    public async Task RunReceiveLoopAsync(
        Func<ReadOnlyMemory<byte>, Task> onMessage,
        CancellationToken cancellationToken,
        TimeSpan? receiveTimeout)
    {
        var webSocket = _webSocket
                        ?? throw new InvalidOperationException("WebSocket 尚未连接。");

        var buffer = new byte[16 * 1024];
        using var messageStream = new MemoryStream();

        while (!cancellationToken.IsCancellationRequested &&
               webSocket.State == WebSocketState.Open)
        {
            messageStream.SetLength(0);
            WebSocketReceiveResult result;
            do
            {
                if (receiveTimeout.HasValue)
                {
                    using var receiveCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    receiveCts.CancelAfter(receiveTimeout.Value);
                    try
                    {
                        result = await webSocket.ReceiveAsync(buffer, receiveCts.Token);
                    }
                    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                    {
                        throw new TimeoutException(
                            $"No mihomo WebSocket data was received for {receiveTimeout.Value.TotalSeconds:0} seconds.");
                    }
                }
                else
                {
                    result = await webSocket.ReceiveAsync(buffer, cancellationToken);
                }

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    return;
                }

                messageStream.Write(buffer, 0, result.Count);
            }
            while (!result.EndOfMessage);

            if (messageStream.Length > 0)
            {
                await onMessage(messageStream.GetBuffer().AsMemory(0, (int)messageStream.Length));
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_webSocket is not null)
        {
            try
            {
                if (_webSocket.State == WebSocketState.Open)
                {
                    using var closeCts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
                    await _webSocket.CloseOutputAsync(
                        WebSocketCloseStatus.NormalClosure,
                        statusDescription: null,
                        closeCts.Token);
                }
            }
            catch (Exception ex)
            {
                DiagnosticLog.WriteAppExceptionThrottled(
                    "mihomo-pipe-websocket-close",
                    LogSources.Core,
                    ex,
                    "关闭内核 WebSocket 连接失败",
                    level: "WARN");
            }

            _webSocket.Dispose();
            _webSocket = null;
        }

        if (_pipe is not null)
        {
            await _pipe.DisposeAsync();
            _pipe = null;
        }
    }

    private static async Task HandshakeAsync(Stream stream, string path, string? secret, CancellationToken cancellationToken)
    {
        var key = Convert.ToBase64String(RandomNumberGenerator.GetBytes(16));
        var request = new StringBuilder()
            .Append("GET ").Append(path).Append(" HTTP/1.1\r\n")
            .Append("Host: 127.0.0.1\r\n")
            .Append("Upgrade: websocket\r\n")
            .Append("Connection: Upgrade\r\n")
            .Append("Sec-WebSocket-Key: ").Append(key).Append("\r\n")
            .Append("Sec-WebSocket-Version: 13\r\n");

        if (!string.IsNullOrWhiteSpace(secret))
        {
            request.Append("Authorization: Bearer ").Append(secret.Trim()).Append("\r\n");
        }

        request.Append("\r\n");

        var requestBytes = Encoding.UTF8.GetBytes(request.ToString());
        await stream.WriteAsync(requestBytes, cancellationToken);
        await stream.FlushAsync(cancellationToken);

        var response = await ReadHttpHeadersAsync(stream, cancellationToken);
        if (!response.StartsWith("HTTP/1.1 101", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"WebSocket 协议升级失败：{response.Split('\r', '\n').FirstOrDefault()}");
        }

        var accept = ParseHeader(response, "Sec-WebSocket-Accept");
        var expected = ComputeAccept(key);
        if (!string.Equals(accept, expected, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("WebSocket 接受密钥不匹配。");
        }
    }

    /// <summary>
    /// 逐字节读取，直到读到首个 CRLFCRLF 为止——只消费 HTTP 响应头，绝不多读属于
    /// WebSocket 首帧的字节（mihomo 流式接口在 101 后会立即推送首帧）。
    /// </summary>
    private static async Task<string> ReadHttpHeadersAsync(Stream stream, CancellationToken cancellationToken)
    {
        var builder = new StringBuilder(256);
        var single = new byte[1];
        var state = 0; // 已匹配的 \r\n\r\n 前缀长度

        while (true)
        {
            var read = await stream.ReadAsync(single.AsMemory(0, 1), cancellationToken);
            if (read == 0)
            {
                break;
            }

            var c = (char)single[0];
            builder.Append(c);

            state = (state, c) switch
            {
                (0, '\r') => 1,
                (1, '\n') => 2,
                (2, '\r') => 3,
                (3, '\n') => 4,
                (_, '\r') => 1,
                _ => 0
            };

            if (state == 4)
            {
                break;
            }

            if (builder.Length > 8192)
            {
                throw new InvalidOperationException("WebSocket handshake response headers too large.");
            }
        }

        return builder.ToString();
    }

    private static string? ParseHeader(string response, string name)
    {
        foreach (var line in response.Split("\r\n", StringSplitOptions.RemoveEmptyEntries))
        {
            var index = line.IndexOf(':');
            if (index <= 0)
            {
                continue;
            }

            if (string.Equals(line[..index].Trim(), name, StringComparison.OrdinalIgnoreCase))
            {
                return line[(index + 1)..].Trim();
            }
        }

        return null;
    }

    private static string ComputeAccept(string secKey)
    {
        var hash = SHA1.HashData(Encoding.UTF8.GetBytes(secKey + WebSocketGuid));
        return Convert.ToBase64String(hash);
    }
}
