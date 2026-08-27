using System.Security.Principal;

namespace ClashSuki.Services;

internal static class AppLifetimeGuard
{
    // MSIX and Portable builds share config, ports and service state. A user-scoped
    // global lock prevents every distribution and Windows session from running a
    // second owner; it intentionally performs no activation or window IPC.
    private static readonly string MutexName =
        $@"Global\ClashSuki.SingleInstance.{GetCurrentUserSid()}";

    private static Mutex? _mutex;

    public static bool TryAcquire()
    {
        var mutex = new Mutex(initiallyOwned: true, MutexName, out var createdNew);
        if (!createdNew)
        {
            mutex.Dispose();
            return false;
        }

        _mutex = mutex;
        return true;
    }

    public static void Release()
    {
        var mutex = Interlocked.Exchange(ref _mutex, null);
        if (mutex is null)
        {
            return;
        }

        try
        {
            mutex.ReleaseMutex();
        }
        catch (Exception ex)
        {
            DiagnosticLog.WriteAppException(
                "STARTUP-GUARD",
                ex,
                "释放应用生命周期锁失败",
                "WARN");
        }
        finally
        {
            mutex.Dispose();
        }
    }

    private static string GetCurrentUserSid()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return identity.User?.Value
            ?? throw new InvalidOperationException("无法获取当前 Windows 用户标识。");
    }
}
