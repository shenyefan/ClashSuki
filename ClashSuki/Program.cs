using System.Runtime.InteropServices;
using ClashSuki.Services;
using ClashSuki.Utilities;

namespace ClashSuki;

public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        var ownsPrimaryInstance = false;
        try
        {
            WinRT.ComWrappersSupport.InitializeComWrappers();

            if (!SingleInstanceManager.TryAcquirePrimary())
            {
                SingleInstanceManager.RequestActivatePrimary();
                return 0;
            }

            ownsPrimaryInstance = true;
            SingleInstanceManager.StartListening();

            Microsoft.UI.Xaml.Application.Start(_ =>
            {
                var context = new Microsoft.UI.Dispatching.DispatcherQueueSynchronizationContext(
                    Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread());
                SynchronizationContext.SetSynchronizationContext(context);
                new App();
            });
            return 0;
        }
        catch (Exception ex)
        {
            DiagnosticLog.WriteAppException("STARTUP", ex, "应用启动失败", "FATAL");
            ShowStartupFailure(ex);
            return 1;
        }
        finally
        {
            if (ownsPrimaryInstance)
            {
                SingleInstanceManager.ReleasePrimary();
            }
        }
    }

    private static void ShowStartupFailure(Exception exception)
    {
        try
        {
            _ = MessageBox(
                IntPtr.Zero,
                $"ClashSuki 无法启动。\n\n{exception.Message}\n\n诊断日志：\n{DiagnosticLog.AppLogPath}",
                "ClashSuki 启动失败",
                0x10);
        }
        catch
        {
            // The startup reporter must not mask the original failure.
        }
    }

    [DllImport("user32.dll", EntryPoint = "MessageBoxW", CharSet = CharSet.Unicode)]
    private static extern int MessageBox(
        IntPtr windowHandle,
        string text,
        string caption,
        uint type);
}
