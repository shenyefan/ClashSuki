using System.Runtime.InteropServices;
using ClashSuki.Services;

namespace ClashSuki;

public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        var ownsPrimaryInstance = false;
        try
        {
            WriteStartupCheckpoint(
                $"进程启动；OS={Environment.OSVersion.VersionString}；" +
                $"Framework={RuntimeInformation.FrameworkDescription}；" +
                $"BaseDirectory={AppContext.BaseDirectory}");
            WinRT.ComWrappersSupport.InitializeComWrappers();
            WriteStartupCheckpoint("WinRT COM 包装器初始化完成");

            if (!AppLifetimeGuard.TryAcquire())
            {
                WriteStartupCheckpoint("检测到已有实例，本次启动直接退出");
                return 0;
            }

            ownsPrimaryInstance = true;
            WriteStartupCheckpoint("即将启动 WinUI XAML");

            Microsoft.UI.Xaml.Application.Start(_ =>
            {
                try
                {
                    WriteStartupCheckpoint("已进入 WinUI XAML 初始化回调");
                    var context = new Microsoft.UI.Dispatching.DispatcherQueueSynchronizationContext(
                        Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread());
                    SynchronizationContext.SetSynchronizationContext(context);
                    new App();
                    WriteStartupCheckpoint("App XAML 资源初始化完成");
                }
                catch (Exception ex)
                {
                    DiagnosticLog.WriteAppException(
                        "XAML-STARTUP",
                        ex,
                        "WinUI XAML 初始化失败",
                        "FATAL");
                    ShowStartupFailure(ex);
                    Environment.Exit(1);
                }
            });
            WriteStartupCheckpoint("WinUI 消息循环已退出");
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
                AppLifetimeGuard.Release();
            }
        }
    }

    private static void WriteStartupCheckpoint(string message) =>
        DiagnosticLog.WriteApp("STARTUP", message);

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
