using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Principal;
using System.Text;
using ClashSuki.Services;

namespace ClashSuki.Utilities;

public static class SingleInstanceManager
{
    private static readonly string InstanceScope = GetCurrentUserScope();
    private static readonly string MutexName = $@"Global\ClashSuki.SingleInstance.{InstanceScope}";
    private static readonly string SessionMarkerName = $@"Local\ClashSuki.SingleInstance.{InstanceScope}";
    private static readonly string PipeName =
        $"ClashSuki.SingleInstance.{InstanceScope}.{Process.GetCurrentProcess().SessionId}";
    private const string ActivateCommand = "show";

    private static Mutex? _mutex;
    private static Mutex? _sessionMarker;
    private static CancellationTokenSource? _listenerCts;
    private static Action? _activateHandler;

    private static string GetCurrentUserScope()
    {
        // A Local\ mutex is isolated to one interactive logon session. Windows can
        // start the same user's startup task again in another desktop/session, so
        // use the user's SID to enforce one instance across those sessions without
        // preventing a different Windows user from running their own instance.
        return WindowsIdentity.GetCurrent().User?.Value
               ?? throw new InvalidOperationException("无法获取当前 Windows 用户标识。");
    }

    public static bool TryAcquirePrimary()
    {
        _mutex = new Mutex(initiallyOwned: true, MutexName, out var createdNew);
        if (createdNew)
        {
            // This marker lets a secondary launch distinguish another window in
            // this session from the same user's primary instance in another one.
            _sessionMarker = new Mutex(initiallyOwned: false, SessionMarkerName);
        }

        return createdNew;
    }

    public static void RegisterActivateHandler(Action handler) => _activateHandler = handler;

    public static void StartListening()
    {
        _listenerCts = new CancellationTokenSource();
        _ = Task.Run(() => ListenLoopAsync(_listenerCts.Token));
    }

    public static void StopListening()
    {
        _listenerCts?.Cancel();
        _listenerCts?.Dispose();
        _listenerCts = null;
    }

    public static void ReleasePrimary()
    {
        StopListening();
        _sessionMarker?.Dispose();
        _sessionMarker = null;

        if (_mutex is null)
        {
            return;
        }

        try
        {
            _mutex.ReleaseMutex();
        }
        catch (Exception ex)
        {
            DiagnosticLog.WriteAppExceptionThrottled(
                "SINGLE-INSTANCE-MUTEX",
                LogSources.Application,
                ex,
                "释放单实例互斥锁失败",
                level: "WARN");
        }
        finally
        {
            _mutex.Dispose();
            _mutex = null;
        }
    }

    public static void RequestActivatePrimary()
    {
        if (!HasPrimaryInCurrentSession())
        {
            DiagnosticLog.WriteApp(
                "STARTUP",
                "已有实例位于其他 Windows 会话，本次启动直接退出");
            return;
        }

        for (var attempt = 0; attempt < 25; attempt++)
        {
            try
            {
                using var client = new NamedPipeClientStream(
                    ".",
                    PipeName,
                    PipeDirection.Out,
                    PipeOptions.None);

                client.Connect(300);
                var payload = Encoding.UTF8.GetBytes(ActivateCommand + "\n");
                client.Write(payload, 0, payload.Length);
                client.Flush();
                return;
            }
            catch (Exception ex)
            {
                if (attempt == 24)
                {
                    DiagnosticLog.WriteAppException(
                        LogSources.Application,
                        ex,
                        "通知主实例显示窗口失败",
                        "WARN");
                }
                Thread.Sleep(200);
            }
        }
    }

    private static bool HasPrimaryInCurrentSession()
    {
        // Allow a brief race while the primary creates its per-session marker.
        for (var attempt = 0; attempt < 5; attempt++)
        {
            if (Mutex.TryOpenExisting(SessionMarkerName, out var marker))
            {
                marker.Dispose();
                return true;
            }

            Thread.Sleep(20);
        }

        return false;
    }

    private static async Task ListenLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await using var server = new NamedPipeServerStream(
                    PipeName,
                    PipeDirection.In,
                    maxNumberOfServerInstances: 1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous);

                await server.WaitForConnectionAsync(cancellationToken);

                using var reader = new StreamReader(server, Encoding.UTF8, leaveOpen: true);
                var command = await reader.ReadLineAsync(cancellationToken);
                if (string.Equals(command, ActivateCommand, StringComparison.OrdinalIgnoreCase))
                {
                    _activateHandler?.Invoke();
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                DiagnosticLog.WriteAppExceptionThrottled(
                    "SINGLE-INSTANCE-LISTENER",
                    LogSources.Application,
                    ex,
                    "单实例激活监听发生错误",
                    level: "WARN");
                try
                {
                    await Task.Delay(300, cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
            }
        }
    }
}
