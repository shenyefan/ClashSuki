using System.ServiceProcess;
using ClashSuki.ServiceContract;

namespace ClashSuki.Services;

/// <summary>
/// Controls the Windows service selected for the current distribution mode.
/// Installation and repair belong to ClashSuki.Repair, not to this controller.
/// </summary>
public static class ClashSukiServiceController
{
    public static string ServiceName => PackageIdentityService.IsPackaged
        ? ServiceProtocol.ServiceName
        : ServiceProtocol.PortableServiceName;

    public static bool IsInstalled()
    {
        using var controller = FindController();
        return controller is not null;
    }

    public static bool IsRunning()
    {
        using var controller = FindController();
        return controller?.Status == ServiceControllerStatus.Running;
    }

    public static void Start()
    {
        using var controller = FindController()
                               ?? throw new InvalidOperationException(
                                   "ClashSuki 服务未安装，请先修复应用。");

        if (controller.Status == ServiceControllerStatus.Running)
        {
            return;
        }

        if (controller.Status == ServiceControllerStatus.StartPending)
        {
            controller.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(15));
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
        if (controller is null || controller.Status == ServiceControllerStatus.Stopped)
        {
            return;
        }

        if (controller.Status == ServiceControllerStatus.StartPending)
        {
            controller.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(15));
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
