using System.ServiceProcess;
using ClashSuki.ServiceContract;

namespace ClashSuki.Services;

public static class PackagedServiceController
{
    public const string ServiceName = ServiceProtocol.ServiceName;

    public static bool IsInstalled()
    {
        using var controller = FindController();
        return controller is not null;
    }

    public static bool IsRunning()
    {
        using var controller = FindController();
        return controller?.Status is ServiceControllerStatus.Running or ServiceControllerStatus.StartPending;
    }

    public static void Start()
    {
        using var controller = FindController()
                               ?? throw new InvalidOperationException(
                                   "ClashSuki 打包服务未注册，请先修复应用包。");

        if (controller.Status is ServiceControllerStatus.Running or ServiceControllerStatus.StartPending)
        {
            return;
        }

        controller.Start();
        controller.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(15));
    }

    public static void Restart()
    {
        Stop();
        Start();
    }

    public static void Stop()
    {
        using var controller = FindController();
        if (controller?.Status is not (ServiceControllerStatus.Running or ServiceControllerStatus.StartPending))
        {
            return;
        }

        controller.Stop();
        controller.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(15));
    }

    private static ServiceController? FindController()
    {
        foreach (var controller in ServiceController.GetServices())
        {
            if (string.Equals(controller.ServiceName, ServiceName, StringComparison.OrdinalIgnoreCase))
            {
                return controller;
            }

            controller.Dispose();
        }

        return null;
    }
}
