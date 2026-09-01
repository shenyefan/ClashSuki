using ClashSuki.ServiceContract;

namespace ClashSuki.Repair;

internal static class PortableServiceUninstaller
{
    public static bool IsUninstallCommand(IReadOnlyList<string> args) =>
        args.Count == 1 &&
        string.Equals(
            args[0],
            ServiceProtocol.UninstallPortableServiceArgument,
            StringComparison.Ordinal);

    public static int Run(IReadOnlyList<string> args)
    {
        try
        {
            if (!IsUninstallCommand(args))
            {
                throw new ArgumentException(
                    $"用法：{ServiceProtocol.UninstallPortableServiceArgument}");
            }

            Uninstall();
            Program.WriteLog("INFO", "便携服务已卸载");
            return 0;
        }
        catch (Exception ex)
        {
            Program.WriteLog("ERROR", "便携服务卸载失败", ex.ToString());
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }

    private static void Uninstall()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("只能在 Windows 上卸载便携服务。");
        }

        PortableServicePayload.EnsureElevated();

        var serviceDirectory = PortableServiceConfiguration.GetInstallDirectory();
        using var serviceManager = new WindowsServiceInstaller();
        using (var service = serviceManager.TryOpen(ServiceProtocol.PortableServiceName))
        {
            if (service is not null)
            {
                serviceManager.ValidateConfiguration(
                    service,
                    PortableServiceConfiguration.GetImagePath());
                serviceManager.StopAndWait(service);
                serviceManager.Delete(service);
            }
        }

        if (!Directory.Exists(serviceDirectory))
        {
            return;
        }

        PortableServicePayload.EnsureInstallDirectoryIsReplaceable(serviceDirectory);
        Directory.Delete(serviceDirectory, recursive: true);

        var parentDirectory = Path.GetDirectoryName(serviceDirectory);
        if (!string.IsNullOrWhiteSpace(parentDirectory) &&
            Directory.Exists(parentDirectory) &&
            !Directory.EnumerateFileSystemEntries(parentDirectory).Any())
        {
            Directory.Delete(parentDirectory);
        }
    }
}
