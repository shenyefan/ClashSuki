using System.Security.Principal;
using ClashSuki.ServiceContract;

namespace ClashSuki.Repair;

internal static class PortableServiceInstaller
{
    private const string OwnerSidArgument = "--owner-sid";
    private const string DataRootArgument = "--data-root";
    private const string ClientPathArgument = "--client-path";

    public static bool IsInstallCommand(IReadOnlyList<string> args) =>
        args.Count > 0 &&
        string.Equals(
            args[0],
            ServiceProtocol.InstallPortableServiceArgument,
            StringComparison.Ordinal);

    public static int Run(IReadOnlyList<string> args)
    {
        try
        {
            Install(ParseOptions(args));
            Program.WriteLog("INFO", "便携服务已安装并启动");
            return 0;
        }
        catch (Exception ex)
        {
            Program.WriteLog("ERROR", "便携服务安装失败", ex.ToString());
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }

    private static void Install(InstallOptions options)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("只能在 Windows 上安装便携服务。");
        }

        PortableServicePayload.EnsureElevated();

        var sourceDirectory = Path.GetFullPath(AppContext.BaseDirectory);
        var sourceExecutable = Path.Combine(sourceDirectory, "ClashSuki.Service.exe");
        PortableServicePayload.EnsureRegularFile(sourceExecutable, "便携服务载荷");
        var sourceCorePath = Path.GetFullPath(Path.Combine(
            sourceDirectory,
            "..",
            "Assets",
            "Core",
            "mihomo.exe"));
        PortableServicePayload.EnsureRegularFile(sourceCorePath, "便携包中的 mihomo 内核");

        var serviceDirectory = PortableServiceConfiguration.GetInstallDirectory();
        var serviceParentDirectory = Path.GetDirectoryName(serviceDirectory)
                                     ?? throw new InvalidOperationException("无法确定便携服务父目录。");
        var installedExecutable = Path.Combine(serviceDirectory, "ClashSuki.Service.exe");
        var imagePath = $"\"{installedExecutable}\" {ServiceProtocol.PortableServiceHostArgument}";
        Directory.CreateDirectory(serviceParentDirectory);

        var operationId = Guid.NewGuid().ToString("N");
        var stagingDirectory = Path.Combine(serviceParentDirectory, $"PortableService.staging-{operationId}");
        var backupDirectory = Path.Combine(serviceParentDirectory, $"PortableService.previous-{operationId}");
        PortableServicePayload.EnsureExpectedSiblingPath(stagingDirectory, serviceParentDirectory, "暂存目录");
        PortableServicePayload.EnsureExpectedSiblingPath(backupDirectory, serviceParentDirectory, "备份目录");

        var registration = CreateRegistration(options);
        PortableServicePayload.Stage(sourceExecutable, sourceCorePath, stagingDirectory, registration);

        using var serviceManager = new WindowsServiceInstaller();
        WindowsServiceInstaller.ServiceHandle? service = null;
        var serviceCreated = false;
        var oldDirectoryMoved = false;
        var newDirectoryPromoted = false;
        try
        {
            service = serviceManager.TryOpen(ServiceProtocol.PortableServiceName);
            if (service is not null)
            {
                serviceManager.ValidateConfiguration(service, imagePath);
                serviceManager.StopAndWait(service);
            }

            if (Directory.Exists(serviceDirectory))
            {
                PortableServicePayload.EnsureInstallDirectoryIsReplaceable(serviceDirectory);
                Directory.Move(serviceDirectory, backupDirectory);
                oldDirectoryMoved = true;
            }

            Directory.Move(stagingDirectory, serviceDirectory);
            newDirectoryPromoted = true;
            PortableServicePayload.ApplyProtectedAcl(serviceDirectory);
            PortableServicePayload.EnsureRegularFile(installedExecutable, "已安装的服务程序");

            if (service is null)
            {
                service = serviceManager.Create(imagePath);
                serviceCreated = true;
            }
            else
            {
                serviceManager.UpdateConfiguration(service, imagePath);
            }

            serviceManager.ApplyAccessControl(service, new SecurityIdentifier(options.OwnerSid));
            serviceManager.SetDescription(
                service,
                "为 ClashSuki 便携版提供受保护的虚拟网卡、防火墙和 UWP 回环功能。");
            serviceManager.StartAndWait(service);

            if (oldDirectoryMoved)
            {
                PortableServicePayload.TryDeleteDirectory(backupDirectory);
            }
        }
        catch
        {
            if (serviceCreated && service is not null)
            {
                serviceManager.Delete(service);
            }

            service?.Dispose();
            service = null;

            if (newDirectoryPromoted && Directory.Exists(serviceDirectory))
            {
                PortableServicePayload.TryDeleteDirectory(serviceDirectory);
            }

            if (oldDirectoryMoved && Directory.Exists(backupDirectory) && !Directory.Exists(serviceDirectory))
            {
                Directory.Move(backupDirectory, serviceDirectory);
                TryRestartRestoredService(serviceManager);
            }

            throw;
        }
        finally
        {
            service?.Dispose();
            PortableServicePayload.TryDeleteDirectory(stagingDirectory);
        }
    }

    private static void TryRestartRestoredService(WindowsServiceInstaller serviceManager)
    {
        try
        {
            using var previousService = serviceManager.TryOpen(ServiceProtocol.PortableServiceName);
            if (previousService is not null)
            {
                serviceManager.StartAndWait(previousService);
            }
        }
        catch (Exception rollbackException)
        {
            Program.WriteLog(
                "WARN",
                "旧版便携服务目录已恢复，但服务未能重新启动",
                rollbackException.ToString());
        }
    }

    private static InstallOptions ParseOptions(IReadOnlyList<string> args)
    {
        if (!IsInstallCommand(args) || args.Count != 7)
        {
            throw new ArgumentException(
                $"用法：{ServiceProtocol.InstallPortableServiceArgument} " +
                $"{OwnerSidArgument} <SID> {DataRootArgument} <绝对路径> " +
                $"{ClientPathArgument} <ClashSuki.exe>");
        }

        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var index = 1; index < args.Count; index += 2)
        {
            var name = args[index];
            if (index + 1 >= args.Count ||
                name is not (OwnerSidArgument or DataRootArgument or ClientPathArgument) ||
                !values.TryAdd(name, args[index + 1]))
            {
                throw new ArgumentException("便携服务安装参数无效。");
            }
        }

        if (!values.TryGetValue(OwnerSidArgument, out var ownerSidText) ||
            !values.TryGetValue(DataRootArgument, out var dataRootText) ||
            !values.TryGetValue(ClientPathArgument, out var clientPathText))
        {
            throw new ArgumentException("便携服务安装参数不完整。");
        }

        var ownerSid = new SecurityIdentifier(ownerSidText.Trim());
        if (!ownerSid.IsAccountSid())
        {
            throw new ArgumentException("便携服务所有者必须是 Windows 帐户 SID。");
        }

        var dataRoot = PortableServicePayload.NormalizeAbsoluteLocalPath(dataRootText, "数据目录");
        if (!Directory.Exists(dataRoot))
        {
            throw new DirectoryNotFoundException($"找不到 ClashSuki 数据目录：{dataRoot}");
        }
        PortableServicePayload.EnsureNotReparsePoint(dataRoot, "ClashSuki 数据目录");

        var clientPath = PortableServicePayload.NormalizeAbsoluteLocalPath(clientPathText, "客户端路径");
        if (!string.Equals(Path.GetFileName(clientPath), "ClashSuki.exe", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("客户端路径必须指向 ClashSuki.exe。");
        }

        PortableServicePayload.EnsureRegularFile(clientPath, "ClashSuki 客户端");
        var clientDllPath = Path.Combine(Path.GetDirectoryName(clientPath)!, "ClashSuki.dll");
        PortableServicePayload.EnsureRegularFile(clientDllPath, "ClashSuki 客户端程序集");
        return new InstallOptions(ownerSid.Value, dataRoot, clientPath, clientDllPath);
    }

    private static PortableServiceConfiguration.Registration CreateRegistration(InstallOptions options) =>
        new()
        {
            OwnerSid = options.OwnerSid,
            DataRoot = options.DataRoot,
            ClientPath = options.ClientPath,
            ClientExeSha256 = FileIntegrity.ComputeSha256(options.ClientPath),
            ClientDllSha256 = FileIntegrity.ComputeSha256(options.ClientDllPath)
        };

    private sealed record InstallOptions(
        string OwnerSid,
        string DataRoot,
        string ClientPath,
        string ClientDllPath);
}
