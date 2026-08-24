using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using ClashSuki.ServiceContract;

namespace ClashSuki.Service;

internal sealed class ServiceRuntimeContext
{
    private ServiceRuntimeContext(
        bool isPortable,
        string serviceName,
        string pipeName,
        string corePath,
        PortableServiceConfiguration.Registration? portableRegistration)
    {
        IsPortable = isPortable;
        ServiceName = serviceName;
        PipeName = pipeName;
        CorePath = Path.GetFullPath(corePath);
        PortableRegistration = portableRegistration;
    }

    public bool IsPortable { get; }

    public string ServiceName { get; }

    public string PipeName { get; }

    public string CorePath { get; }

    public PortableServiceConfiguration.Registration? PortableRegistration { get; }

    public static ServiceRuntimeContext Create(IReadOnlyList<string> args)
    {
        if (args.Count == 0)
        {
            return CreateMsix();
        }

        if (args.Count == 1 &&
            string.Equals(
                args[0],
                ServiceProtocol.PortableServiceHostArgument,
                StringComparison.Ordinal))
        {
            return CreatePortable();
        }

        throw new InvalidOperationException("服务宿主参数无效。");
    }

    public IReadOnlySet<string> GetTrustedMsixClientPaths()
    {
        var serviceDirectory = Path.GetFullPath(AppContext.BaseDirectory);
        var parent = Directory.GetParent(serviceDirectory)?.FullName;
        var grandParent = parent is null ? null : Directory.GetParent(parent)?.FullName;
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var root in new[] { serviceDirectory, parent, grandParent })
        {
            if (string.IsNullOrWhiteSpace(root))
            {
                continue;
            }

            result.Add(Path.GetFullPath(Path.Combine(root, "ClashSuki.exe")));
            result.Add(Path.GetFullPath(Path.Combine(root, "ClashSuki", "ClashSuki.exe")));
        }

        return result;
    }

    private static ServiceRuntimeContext CreateMsix()
    {
        var corePath = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..",
            "ClashSuki",
            "Assets",
            "Core",
            "mihomo.exe"));
        EnsureCoreExists(corePath);
        return new ServiceRuntimeContext(
            isPortable: false,
            ServiceProtocol.ServiceName,
            ServiceProtocol.PipeName,
            corePath,
            portableRegistration: null);
    }

    private static ServiceRuntimeContext CreatePortable()
    {
        var expectedDirectory = PortableServiceConfiguration.GetInstallDirectory();
        var actualDirectory = Path.GetFullPath(AppContext.BaseDirectory);
        if (!PortableServiceConfiguration.PathsEqual(actualDirectory, expectedDirectory))
        {
            throw new InvalidOperationException(
                $"便携服务只能从受保护目录启动：{expectedDirectory}");
        }

        EnsurePortableDirectoryIsProtected(actualDirectory);

        var registrationPath = Path.Combine(actualDirectory, PortableServiceConfiguration.RegistrationFileName);
        var registration = JsonSerializer.Deserialize<PortableServiceConfiguration.Registration>(
                               File.ReadAllText(registrationPath))
                           ?? throw new InvalidDataException("便携服务注册信息无效。");
        registration.Validate();

        var corePath = Path.Combine(actualDirectory, "Core", "mihomo.exe");
        EnsureCoreExists(corePath);
        return new ServiceRuntimeContext(
            isPortable: true,
            ServiceProtocol.PortableServiceName,
            ServiceProtocol.PortablePipeName,
            corePath,
            registration);
    }

    private static void EnsureCoreExists(string corePath)
    {
        if (!File.Exists(corePath))
        {
            throw new FileNotFoundException("找不到受保护的 mihomo 内核。", corePath);
        }

        if ((File.GetAttributes(corePath) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidOperationException("受保护的 mihomo 内核不能是重解析点。");
        }
    }

    private static void EnsurePortableDirectoryIsProtected(string directory)
    {
        if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidOperationException("便携服务目录不能是重解析点。");
        }

        var security = new DirectoryInfo(directory).GetAccessControl(AccessControlSections.Access);
        var privilegedWriteSids = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null).Value,
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null).Value
        };
        const FileSystemRights writeRights =
            FileSystemRights.Write |
            FileSystemRights.Modify |
            FileSystemRights.FullControl |
            FileSystemRights.Delete |
            FileSystemRights.DeleteSubdirectoriesAndFiles |
            FileSystemRights.ChangePermissions |
            FileSystemRights.TakeOwnership;

        foreach (FileSystemAccessRule rule in security.GetAccessRules(
                     includeExplicit: true,
                     includeInherited: true,
                     typeof(SecurityIdentifier)))
        {
            if (rule.AccessControlType == AccessControlType.Allow &&
                rule.IdentityReference is SecurityIdentifier sid &&
                !privilegedWriteSids.Contains(sid.Value) &&
                (rule.FileSystemRights & writeRights) != 0)
            {
                throw new InvalidOperationException(
                    $"便携服务目录向非特权主体授予了写权限：{sid.Value}");
            }
        }
    }

}
