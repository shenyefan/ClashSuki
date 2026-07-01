using System.Text.Json;
using ClashSuki.Models;

namespace ClashSuki.Services;

/// <summary>
/// 通过命名管道 WebSocket 订阅 mihomo 实时数据（对齐 Clash Verge 的 MihomoWebSocket.connect_*）。
/// </summary>
public sealed class MihomoWsClient : IDisposable, IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private static readonly TimeSpan ReconnectDelay = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan StaleTimeout = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan RealtimeReceiveTimeout = TimeSpan.FromSeconds(10);

    private string? _secret;
    private string _logLevel = "info";
    private CancellationTokenSource? _cts;
    private CancellationTokenSource _connectionRefresh = new();
    private readonly object _configurationGate = new();
    private Task[] _loops = [];

    public event Action<long, long>? TrafficReceived;
    public event Action<ConnectionsSnapshot>? ConnectionsReceived;
    public event Action<long>? MemoryReceived;
    public event Action<string, string>? LogReceived;

    public void Configure(MihomoApiClient api, string? logLevel = null)
    {
        CancellationTokenSource? refresh = null;
        lock (_configurationGate)
        {
            var nextLevel = string.IsNullOrWhiteSpace(logLevel)
                ? _logLevel
                : NormalizeLogLevel(logLevel);
            var changed = !string.Equals(_secret, api.Secret, StringComparison.Ordinal) ||
                          !string.Equals(_logLevel, nextLevel, StringComparison.Ordinal);
            _secret = api.Secret;
            _logLevel = nextLevel;
            if (changed && _cts is not null)
            {
                refresh = _connectionRefresh;
                _connectionRefresh = new CancellationTokenSource();
            }
        }

        refresh?.Cancel();
        refresh?.Dispose();
    }

    public void Start(CancellationToken externalToken)
    {
        if (_cts is not null)
        {
            return;
        }

        _cts = CancellationTokenSource.CreateLinkedTokenSource(externalToken);
        var token = _cts.Token;
        _loops =
        [
            RunTrafficStreamAsync(token),
            RunConnectionsStreamAsync(token),
            RunMemoryStreamAsync(token),
            RunLogStreamAsync(token)
        ];
    }

    private async Task RunTrafficStreamAsync(CancellationToken cancellationToken) =>
        await RunStreamLoopAsync(
            () => "/traffic",
            HandleTrafficMessageAsync,
            "traffic",
            cancellationToken,
            RealtimeReceiveTimeout);

    private async Task RunMemoryStreamAsync(CancellationToken cancellationToken) =>
        await RunStreamLoopAsync(
            () => "/memory",
            HandleMemoryMessageAsync,
            "memory",
            cancellationToken,
            RealtimeReceiveTimeout);

    private async Task RunConnectionsStreamAsync(CancellationToken cancellationToken) =>
        await RunStreamLoopAsync(
            () => "/connections",
            HandleConnectionsMessageAsync,
            "connections",
            cancellationToken,
            RealtimeReceiveTimeout);

    private async Task RunLogStreamAsync(CancellationToken cancellationToken) =>
        await RunStreamLoopAsync(
            () => $"/logs?level={Uri.EscapeDataString(_logLevel)}",
            HandleLogMessageAsync,
            "logs",
            cancellationToken,
            receiveTimeout: null);

    private async Task RunStreamLoopAsync(
        Func<string> pathFactory,
        Func<ReadOnlyMemory<byte>, Task> handler,
        string label,
        CancellationToken cancellationToken,
        TimeSpan? receiveTimeout)
    {
        var lastEvent = DateTime.UtcNow;

        while (!cancellationToken.IsCancellationRequested)
        {
            CancellationToken refreshToken;
            string? secret;
            lock (_configurationGate)
            {
                refreshToken = _connectionRefresh.Token;
                secret = _secret;
            }

            using var connectionCts =
                CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, refreshToken);
            var connectionToken = connectionCts.Token;
            await using var socket = new MihomoPipeWebSocket();
            try
            {
                await socket.ConnectAsync(pathFactory(), secret, connectionToken);
                lastEvent = DateTime.UtcNow;

                await socket.RunReceiveLoopAsync(async payload =>
                {
                    lastEvent = DateTime.UtcNow;
                    await handler(payload);
                }, connectionToken, receiveTimeout);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (OperationCanceledException) when (refreshToken.IsCancellationRequested)
            {
                continue;
            }
            catch (Exception ex)
            {
                DiagnosticLog.WriteAppExceptionThrottled(
                    $"realtime-websocket:{label}",
                    LogSources.Realtime,
                    ex,
                    $"{FormatStreamLabel(label)} WebSocket 连接失败",
                    level: "WARN");
            }

            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            if ((DateTime.UtcNow - lastEvent) >= StaleTimeout)
            {
                DiagnosticLog.WriteApp(
                    "REALTIME",
                    "WARN",
                    $"{FormatStreamLabel(label)} WebSocket 已失效，正在重新连接。");
            }

            try
            {
                await Task.Delay(ReconnectDelay, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
        }
    }

    private Task HandleTrafficMessageAsync(ReadOnlyMemory<byte> payload)
    {
        var traffic = JsonSerializer.Deserialize<TrafficSnapshot>(payload.Span, JsonOptions);
        if (traffic is not null)
        {
            TrafficReceived?.Invoke(traffic.Up, traffic.Down);
        }

        return Task.CompletedTask;
    }

    private Task HandleMemoryMessageAsync(ReadOnlyMemory<byte> payload)
    {
        var memory = JsonSerializer.Deserialize<MemorySnapshot>(payload.Span, JsonOptions);
        if (memory is not null)
        {
            // 与 Verge parseTraffic(memory.inuse) 一致：单位为字节
            MemoryReceived?.Invoke(memory.InUse);
        }

        return Task.CompletedTask;
    }

    private Task HandleConnectionsMessageAsync(ReadOnlyMemory<byte> payload)
    {
        var snapshot = JsonSerializer.Deserialize<ConnectionsSnapshot>(payload.Span, JsonOptions);
        if (snapshot is not null)
        {
            ConnectionsReceived?.Invoke(snapshot);
        }

        return Task.CompletedTask;
    }

    private Task HandleLogMessageAsync(ReadOnlyMemory<byte> payload)
    {
        var log = JsonSerializer.Deserialize<MihomoLogEvent>(payload.Span, JsonOptions);
        if (!string.IsNullOrWhiteSpace(log?.Payload))
        {
            LogReceived?.Invoke(
                string.IsNullOrWhiteSpace(log.Type) ? "INFO" : log.Type,
                log.Payload);
        }

        return Task.CompletedTask;
    }

    private static string NormalizeLogLevel(string value) =>
        value.Trim().ToLowerInvariant() switch
        {
            "warn" => "warning",
            "silent" => "silent",
            "error" => "error",
            "warning" => "warning",
            "debug" => "debug",
            _ => "info"
        };

    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
        _connectionRefresh.Cancel();
        _connectionRefresh.Dispose();
        _connectionRefresh = new CancellationTokenSource();
        _loops = [];
    }

    public async ValueTask DisposeAsync()
    {
        var cts = _cts;
        if (cts is null)
        {
            return;
        }

        cts.Cancel();
        try
        {
            await Task.WhenAll(_loops).WaitAsync(TimeSpan.FromSeconds(2));
        }
        catch (Exception ex) when (ex is OperationCanceledException or TimeoutException)
        {
            DiagnosticLog.WriteApp(
                "REALTIME",
                "WARN",
                $"实时连接关闭已跳过或超时；异常类型={ex.GetType().Name}");
        }
        catch (Exception ex)
        {
            DiagnosticLog.WriteAppException("REALTIME-SHUTDOWN", ex);
        }
        finally
        {
            cts.Dispose();
            _cts = null;
            _connectionRefresh.Cancel();
            _connectionRefresh.Dispose();
            _connectionRefresh = new CancellationTokenSource();
            _loops = [];
        }
    }

    private static string FormatStreamLabel(string label) => label.ToLowerInvariant() switch
    {
        "traffic" => "流量",
        "memory" => "内存",
        "connections" => "连接",
        "logs" => "内核日志",
        _ => label
    };
}
