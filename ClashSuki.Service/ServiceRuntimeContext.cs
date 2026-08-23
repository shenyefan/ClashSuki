using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using ClashSuki.ServiceContract;

namespace ClashSuki.Service;

internal sealed class ServiceRuntimeContext
{
    internal const string PortableRegistrationFileName = "portable-service.json";
    private const int PortableRegistrationSchemaVersion = 1;

    private ServiceRuntimeContext(
        bool isPortable,
        string serviceName,
        string pipeName,
        string corePath,
        PortableServiceRegistration? portableRegistration)
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

    public PortableServiceRegistration? PortableRegistration { get; }

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

    public static string GetPortableServiceDirectory()
    {
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        if (string.IsNullOrWhiteSpace(programFiles))
        {
            throw new InvalidOperationException("无法确定 Program Files 目录。");
        }

        return Path.GetFullPath(Path.Combine(programFiles, "ClashSuki", "PortableService"));
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
        var expectedDirectory = GetPortableServiceDirectory();
        var actualDirectory = Path.GetFullPath(AppContext.BaseDirectory);
        if (!PathsEqual(actualDirectory, expectedDirectory))
        {
            throw new InvalidOperationException(
                $"便携服务只能从受保护目录启动：{expectedDirectory}");
        }

        EnsurePortableDirectoryIsProtected(actualDirectory);

        var registrationPath = Path.Combine(actualDirectory, PortableRegistrationFileName);
        var registration = JsonSerializer.Deserialize<PortableServiceRegistration>(
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

    internal static bool PathsEqual(string left, string right) =>
        string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
            StringComparison.OrdinalIgnoreCase);

    internal sealed class PortableServiceRegistration
    {
        public int SchemaVersion { get; init; } = PortableRegistrationSchemaVersion;

        public string OwnerSid { get; init; } = "";

        public string DataRoot { get; init; } = "";

        public string ClientPath { get; init; } = "";

        public string ClientExeSha256 { get; init; } = "";

        public string ClientDllSha256 { get; init; } = "";

        public void Validate()
        {
            if (SchemaVersion != PortableRegistrationSchemaVersion)
            {
                throw new InvalidDataException("便携服务注册信息版本不受支持。");
            }

            _ = new SecurityIdentifier(OwnerSid);
            ValidateAbsoluteLocalPath(DataRoot, "数据目录");
            ValidateAbsoluteLocalPath(ClientPath, "客户端路径");
            if (!string.Equals(Path.GetFileName(ClientPath), "ClashSuki.exe", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("便携服务客户端必须是 ClashSuki.exe。");
            }

            ValidateSha256(ClientExeSha256, "客户端 EXE");
            ValidateSha256(ClientDllSha256, "客户端 DLL");
        }

        private static void ValidateAbsoluteLocalPath(string path, string displayName)
        {
            if (string.IsNullOrWhiteSpace(path) ||
                !Path.IsPathFullyQualified(path) ||
                Path.GetFullPath(path).StartsWith(@"\\", StringComparison.Ordinal))
            {
                throw new InvalidDataException($"便携服务{displayName}必须是本机绝对路径。");
            }
        }

        private static void ValidateSha256(string value, string displayName)
        {
            if (value.Length != 64 || value.Any(static character => !Uri.IsHexDigit(character)))
            {
                throw new InvalidDataException($"便携服务{displayName}哈希无效。");
            }
        }
    }
}
