using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;
using ClashSuki.ServiceContract;
using Microsoft.Win32.SafeHandles;

namespace ClashSuki.Service;

internal static class PortableServiceInstaller
{
    private const string OwnerSidArgument = "--owner-sid";
    private const string DataRootArgument = "--data-root";
    private const string ClientPathArgument = "--client-path";
    private const string ServiceDisplayName = "ClashSuki Portable Service";
    private static readonly TimeSpan ServiceTransitionTimeout = TimeSpan.FromSeconds(20);

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
            ServiceDiagnostics.Write("安装便携服务", "便携服务已安装并启动");
            return 0;
        }
        catch (Exception ex)
        {
            ServiceDiagnostics.WriteException("安装便携服务", "便携服务安装失败", ex);
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

        EnsureElevated();

        var sourceDirectory = Path.GetFullPath(AppContext.BaseDirectory);
        var sourceExecutable = Path.Combine(sourceDirectory, "ClashSuki.Service.exe");
        EnsureRegularFile(sourceExecutable, "便携服务安装程序");
        var sourceCorePath = Path.GetFullPath(Path.Combine(
            sourceDirectory,
            "..",
            "Assets",
            "Core",
            "mihomo.exe"));
        EnsureRegularFile(sourceCorePath, "便携包中的 mihomo 内核");

        var serviceDirectory = ServiceRuntimeContext.GetPortableServiceDirectory();
        var serviceParentDirectory = Path.GetDirectoryName(serviceDirectory)
                                     ?? throw new InvalidOperationException("无法确定便携服务父目录。");
        var installedExecutable = Path.Combine(serviceDirectory, "ClashSuki.Service.exe");
        var imagePath = $"\"{installedExecutable}\" {ServiceProtocol.PortableServiceHostArgument}";
        Directory.CreateDirectory(serviceParentDirectory);

        var operationId = Guid.NewGuid().ToString("N");
        var stagingDirectory = Path.Combine(serviceParentDirectory, $"PortableService.staging-{operationId}");
        var backupDirectory = Path.Combine(serviceParentDirectory, $"PortableService.previous-{operationId}");
        EnsureExpectedSiblingPath(stagingDirectory, serviceParentDirectory, "暂存目录");
        EnsureExpectedSiblingPath(backupDirectory, serviceParentDirectory, "备份目录");

        var registration = CreateRegistration(options);
        StagePayload(sourceExecutable, sourceCorePath, stagingDirectory, registration);

        using var serviceManager = OpenServiceManager();
        SafeServiceHandle? service = null;
        var serviceCreated = false;
        var oldDirectoryMoved = false;
        var newDirectoryPromoted = false;
        try
        {
            service = TryOpenService(serviceManager, ServiceProtocol.PortableServiceName);
            if (service is not null)
            {
                ValidateExistingServiceConfiguration(service, imagePath);
                StopServiceAndWait(service);
            }

            if (Directory.Exists(serviceDirectory))
            {
                EnsureInstallDirectoryIsReplaceable(serviceDirectory);
                Directory.Move(serviceDirectory, backupDirectory);
                oldDirectoryMoved = true;
            }

            Directory.Move(stagingDirectory, serviceDirectory);
            newDirectoryPromoted = true;
            ApplyProtectedAcl(serviceDirectory);

            EnsureRegularFile(installedExecutable, "已安装的服务程序");

            if (service is null)
            {
                service = CreateService(serviceManager, imagePath);
                serviceCreated = true;
            }
            else
            {
                UpdateServiceConfiguration(service, imagePath);
            }

            ApplyServiceAccessControl(service, new SecurityIdentifier(options.OwnerSid));
            SetServiceDescription(
                service,
                "为 ClashSuki 便携版提供受保护的虚拟网卡、防火墙和 UWP 回环功能。");
            StartServiceAndWait(service);

            if (oldDirectoryMoved)
            {
                TryDeleteDirectory(backupDirectory);
            }
        }
        catch
        {
            if (serviceCreated && service is not null)
            {
                _ = DeleteService(service);
            }

            service?.Dispose();
            service = null;

            if (newDirectoryPromoted && Directory.Exists(serviceDirectory))
            {
                TryDeleteDirectory(serviceDirectory);
            }

            if (oldDirectoryMoved && Directory.Exists(backupDirectory) && !Directory.Exists(serviceDirectory))
            {
                Directory.Move(backupDirectory, serviceDirectory);
                try
                {
                    using var previousService = TryOpenService(serviceManager, ServiceProtocol.PortableServiceName);
                    if (previousService is not null)
                    {
                        StartServiceAndWait(previousService);
                    }
                }
                catch (Exception rollbackException)
                {
                    ServiceDiagnostics.WriteException(
                        "回滚便携服务",
                        "旧版便携服务目录已恢复，但服务未能重新启动",
                        rollbackException,
                        "WARN");
                }
            }

            throw;
        }
        finally
        {
            service?.Dispose();
            TryDeleteDirectory(stagingDirectory);
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

        var dataRoot = NormalizeAbsoluteLocalPath(dataRootText, "数据目录");
        if (!Directory.Exists(dataRoot))
        {
            throw new DirectoryNotFoundException($"找不到 ClashSuki 数据目录：{dataRoot}");
        }
        EnsureNotReparsePoint(dataRoot, "ClashSuki 数据目录");

        var clientPath = NormalizeAbsoluteLocalPath(clientPathText, "客户端路径");
        if (!string.Equals(Path.GetFileName(clientPath), "ClashSuki.exe", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("客户端路径必须指向 ClashSuki.exe。");
        }

        EnsureRegularFile(clientPath, "ClashSuki 客户端");
        var clientDllPath = Path.Combine(Path.GetDirectoryName(clientPath)!, "ClashSuki.dll");
        EnsureRegularFile(clientDllPath, "ClashSuki 客户端程序集");
        return new InstallOptions(ownerSid.Value, dataRoot, clientPath, clientDllPath);
    }

    private static ServiceRuntimeContext.PortableServiceRegistration CreateRegistration(InstallOptions options) =>
        new()
        {
            OwnerSid = options.OwnerSid,
            DataRoot = options.DataRoot,
            ClientPath = options.ClientPath,
            ClientExeSha256 = ComputeSha256(options.ClientPath),
            ClientDllSha256 = ComputeSha256(options.ClientDllPath)
        };

    private static void StagePayload(
        string sourceExecutable,
        string sourceCorePath,
        string stagingDirectory,
        ServiceRuntimeContext.PortableServiceRegistration registration)
    {
        Directory.CreateDirectory(stagingDirectory);
        ApplyProtectedAcl(stagingDirectory);

        var stagedExecutable = Path.Combine(stagingDirectory, "ClashSuki.Service.exe");
        CopyLockedAndVerify(sourceExecutable, stagedExecutable);
        EnsureRegularFile(stagedExecutable, "服务安装载荷");

        var stagedCoreDirectory = Path.Combine(stagingDirectory, "Core");
        Directory.CreateDirectory(stagedCoreDirectory);
        var stagedCorePath = Path.Combine(stagedCoreDirectory, "mihomo.exe");
        CopyLockedAndVerify(sourceCorePath, stagedCorePath);

        var registrationJson = JsonSerializer.Serialize(
            registration,
            new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(
            Path.Combine(stagingDirectory, ServiceRuntimeContext.PortableRegistrationFileName),
            registrationJson);
        ApplyProtectedAcl(stagingDirectory);
    }

    private static void CopyLockedAndVerify(string sourcePath, string destinationPath)
    {
        using var source = new FileStream(
            sourcePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 128 * 1024,
            FileOptions.SequentialScan);
        var sourceHash = Convert.ToHexString(SHA256.HashData(source));
        source.Position = 0;
        using (var destination = new FileStream(
                   destinationPath,
                   FileMode.CreateNew,
                   FileAccess.Write,
                   FileShare.None,
                   bufferSize: 128 * 1024,
                   FileOptions.SequentialScan))
        {
            source.CopyTo(destination);
            destination.Flush(flushToDisk: true);
        }

        if (!string.Equals(sourceHash, ComputeSha256(destinationPath), StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"复制服务文件时完整性校验失败：{Path.GetFileName(sourcePath)}");
        }
    }

    private static void ApplyProtectedAcl(string directory)
    {
        var inheritance = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            FileSystemRights.FullControl,
            inheritance,
            PropagationFlags.None,
            AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
            FileSystemRights.FullControl,
            inheritance,
            PropagationFlags.None,
            AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null),
            FileSystemRights.ReadAndExecute | FileSystemRights.Synchronize,
            inheritance,
            PropagationFlags.None,
            AccessControlType.Allow));
        new DirectoryInfo(directory).SetAccessControl(security);
    }

    private static string ComputeSha256(string path)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read | FileShare.Delete,
            bufferSize: 128 * 1024,
            FileOptions.SequentialScan);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static void EnsureElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(identity);
        if (!principal.IsInRole(WindowsBuiltInRole.Administrator))
        {
            throw new UnauthorizedAccessException("安装便携服务需要管理员权限。");
        }
    }

    private static string NormalizeAbsoluteLocalPath(string path, string displayName)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
        {
            throw new ArgumentException($"{displayName}必须是绝对路径。");
        }

        var normalized = Path.GetFullPath(path);
        if (normalized.StartsWith(@"\\", StringComparison.Ordinal))
        {
            throw new ArgumentException($"{displayName}必须位于本机磁盘。");
        }

        return normalized;
    }

    private static void EnsureRegularFile(string path, string displayName)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"找不到{displayName}。", path);
        }

        EnsureNotReparsePoint(path, displayName);
    }

    private static void EnsureNotReparsePoint(string path, string displayName)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidOperationException($"{displayName}不能是重解析点：{path}");
        }
    }

    private static void EnsureExpectedSiblingPath(string path, string parent, string displayName)
    {
        var normalizedParent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(parent));
        var actualParent = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.Equals(actualParent, normalizedParent, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"便携服务{displayName}不在预期目录中。");
        }
    }

    private static void EnsureInstallDirectoryIsReplaceable(string serviceDirectory)
    {
        if (!ServiceRuntimeContext.PathsEqual(
                serviceDirectory,
                ServiceRuntimeContext.GetPortableServiceDirectory()))
        {
            throw new InvalidOperationException("拒绝替换非预期的服务目录。");
        }

        EnsureNotReparsePoint(serviceDirectory, "现有便携服务目录");
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception ex)
        {
            ServiceDiagnostics.WriteException(
                "清理便携服务文件",
                $"无法删除目录：{path}",
                ex,
                "WARN");
        }
    }

    private static SafeServiceHandle OpenServiceManager()
    {
        var handle = OpenSCManager(null, null, ScManagerConnect | ScManagerCreateService);
        if (handle.IsInvalid)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "无法打开 Windows 服务控制管理器。");
        }

        return handle;
    }

    private static SafeServiceHandle? TryOpenService(SafeServiceHandle manager, string serviceName)
    {
        var handle = OpenService(manager, serviceName, ServiceAllAccess);
        if (!handle.IsInvalid)
        {
            return handle;
        }

        var error = Marshal.GetLastWin32Error();
        handle.Dispose();
        if (error == ErrorServiceDoesNotExist)
        {
            return null;
        }

        throw new Win32Exception(error, $"无法打开 Windows 服务：{serviceName}");
    }

    private static SafeServiceHandle CreateService(SafeServiceHandle manager, string imagePath)
    {
        var handle = CreateService(
            manager,
            ServiceProtocol.PortableServiceName,
            ServiceDisplayName,
            ServiceAllAccess,
            ServiceWin32OwnProcess,
            ServiceDemandStart,
            ServiceErrorNormal,
            imagePath,
            null,
            IntPtr.Zero,
            null,
            "LocalSystem",
            null);
        if (handle.IsInvalid)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "无法创建 ClashSuki 便携服务。");
        }

        return handle;
    }

    private static void UpdateServiceConfiguration(SafeServiceHandle service, string imagePath)
    {
        if (!ChangeServiceConfig(
                service,
                ServiceWin32OwnProcess,
                ServiceDemandStart,
                ServiceErrorNormal,
                imagePath,
                null,
                IntPtr.Zero,
                null,
                "LocalSystem",
                null,
                ServiceDisplayName))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "无法更新 ClashSuki 便携服务配置。");
        }
    }

    private static void ValidateExistingServiceConfiguration(
        SafeServiceHandle service,
        string expectedImagePath)
    {
        var configuration = QueryConfiguration(service);
        if (configuration.ServiceType != ServiceWin32OwnProcess ||
            !string.Equals(
                configuration.BinaryPathName.Trim(),
                expectedImagePath,
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(
                configuration.ServiceStartName.Trim(),
                "LocalSystem",
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"已存在同名服务，但其配置不属于 ClashSuki：{configuration.BinaryPathName}");
        }
    }

    private static QueriedServiceConfiguration QueryConfiguration(SafeServiceHandle service)
    {
        _ = QueryServiceConfig(service, IntPtr.Zero, 0, out var requiredSize);
        var error = Marshal.GetLastWin32Error();
        if (requiredSize <= 0 || error != ErrorInsufficientBuffer)
        {
            throw new Win32Exception(error, "无法读取现有 ClashSuki 便携服务配置。");
        }

        var buffer = Marshal.AllocHGlobal(requiredSize);
        try
        {
            if (!QueryServiceConfig(service, buffer, requiredSize, out _))
            {
                throw new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    "无法读取现有 ClashSuki 便携服务配置。");
            }

            var native = Marshal.PtrToStructure<QueryServiceConfigValue>(buffer);
            return new QueriedServiceConfiguration(
                native.ServiceType,
                Marshal.PtrToStringUni(native.BinaryPathName) ?? "",
                Marshal.PtrToStringUni(native.ServiceStartName) ?? "");
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static void SetServiceDescription(SafeServiceHandle service, string description)
    {
        var value = new ServiceDescription { Description = description };
        if (!ChangeServiceConfig2(service, ServiceConfigDescription, ref value))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "无法设置 ClashSuki 便携服务说明。");
        }
    }

    private static void ApplyServiceAccessControl(
        SafeServiceHandle service,
        SecurityIdentifier ownerSid)
    {
        var systemSid = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        var administratorsSid = new SecurityIdentifier(
            WellKnownSidType.BuiltinAdministratorsSid,
            null);
        var discretionaryAcl = new RawAcl(GenericAcl.AclRevision, capacity: 3);
        discretionaryAcl.InsertAce(
            0,
            CreateServiceAccessAce(systemSid, ServiceAllAccess));
        discretionaryAcl.InsertAce(
            1,
            CreateServiceAccessAce(administratorsSid, ServiceAllAccess));
        discretionaryAcl.InsertAce(
            2,
            CreateServiceAccessAce(ownerSid, PortableOwnerServiceAccess));

        var descriptor = new RawSecurityDescriptor(
            ControlFlags.DiscretionaryAclPresent,
            administratorsSid,
            systemSid,
            systemAcl: null,
            discretionaryAcl);
        var descriptorBytes = new byte[descriptor.BinaryLength];
        descriptor.GetBinaryForm(descriptorBytes, 0);

        if (!SetServiceObjectSecurity(
                service,
                DaclSecurityInformation,
                descriptorBytes))
        {
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                "无法设置 ClashSuki 便携服务访问权限。");
        }
    }

    private static CommonAce CreateServiceAccessAce(
        SecurityIdentifier sid,
        uint accessMask) =>
        new(
            AceFlags.None,
            AceQualifier.AccessAllowed,
            checked((int)accessMask),
            sid,
            isCallback: false,
            opaque: null);

    private static void StopServiceAndWait(SafeServiceHandle service)
    {
        var status = QueryStatus(service);
        if (status.CurrentState == ServiceStopped)
        {
            return;
        }

        if (status.CurrentState != ServiceStopPending &&
            !ControlService(service, ServiceControlStop, out _))
        {
            var error = Marshal.GetLastWin32Error();
            if (error != ErrorServiceNotActive)
            {
                throw new Win32Exception(error, "无法停止现有 ClashSuki 便携服务。");
            }
        }

        WaitForState(service, ServiceStopped, ServiceTransitionTimeout, "停止");
    }

    private static void StartServiceAndWait(SafeServiceHandle service)
    {
        var status = QueryStatus(service);
        if (status.CurrentState == ServiceRunning)
        {
            return;
        }

        if (status.CurrentState != ServiceStartPending && !StartService(service, 0, IntPtr.Zero))
        {
            var error = Marshal.GetLastWin32Error();
            if (error != ErrorServiceAlreadyRunning)
            {
                throw new Win32Exception(error, "无法启动 ClashSuki 便携服务。");
            }
        }

        WaitForState(service, ServiceRunning, ServiceTransitionTimeout, "启动");
    }

    private static void WaitForState(
        SafeServiceHandle service,
        uint expectedState,
        TimeSpan timeout,
        string action)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var status = QueryStatus(service);
            if (status.CurrentState == expectedState)
            {
                return;
            }

            if (status.CurrentState == ServiceStopped && expectedState != ServiceStopped)
            {
                throw new InvalidOperationException(
                    $"ClashSuki 便携服务在{action}时退出，Win32 退出码：{status.Win32ExitCode}。");
            }

            Thread.Sleep(200);
        }

        throw new TimeoutException($"等待 ClashSuki 便携服务{action}超时。");
    }

    private static ServiceStatusProcess QueryStatus(SafeServiceHandle service)
    {
        if (!QueryServiceStatusEx(
                service,
                ScStatusProcessInfo,
                out var status,
                Marshal.SizeOf<ServiceStatusProcess>(),
                out _))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "无法查询 ClashSuki 便携服务状态。");
        }

        return status;
    }

    private sealed record InstallOptions(
        string OwnerSid,
        string DataRoot,
        string ClientPath,
        string ClientDllPath);

    private sealed record QueriedServiceConfiguration(
        uint ServiceType,
        string BinaryPathName,
        string ServiceStartName);

    private sealed class SafeServiceHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        private SafeServiceHandle()
            : base(ownsHandle: true)
        {
        }

        protected override bool ReleaseHandle() => CloseServiceHandle(handle);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ServiceDescription
    {
        [MarshalAs(UnmanagedType.LPWStr)]
        public string Description;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceStatus
    {
        public uint ServiceType;
        public uint CurrentState;
        public uint ControlsAccepted;
        public uint Win32ExitCode;
        public uint ServiceSpecificExitCode;
        public uint CheckPoint;
        public uint WaitHint;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceStatusProcess
    {
        public uint ServiceType;
        public uint CurrentState;
        public uint ControlsAccepted;
        public uint Win32ExitCode;
        public uint ServiceSpecificExitCode;
        public uint CheckPoint;
        public uint WaitHint;
        public uint ProcessId;
        public uint ServiceFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct QueryServiceConfigValue
    {
        public uint ServiceType;
        public uint StartType;
        public uint ErrorControl;
        public IntPtr BinaryPathName;
        public IntPtr LoadOrderGroup;
        public uint TagId;
        public IntPtr Dependencies;
        public IntPtr ServiceStartName;
        public IntPtr DisplayName;
    }

    private const uint ScManagerConnect = 0x0001;
    private const uint ScManagerCreateService = 0x0002;
    private const uint ServiceAllAccess = 0x000F01FF;
    private const uint ServiceQueryConfig = 0x0001;
    private const uint ServiceQueryStatus = 0x0004;
    private const uint ServiceStart = 0x0010;
    private const uint ServiceStop = 0x0020;
    private const uint ServiceInterrogate = 0x0080;
    private const uint ReadControl = 0x00020000;
    private const uint PortableOwnerServiceAccess =
        ServiceQueryConfig |
        ServiceQueryStatus |
        ServiceStart |
        ServiceStop |
        ServiceInterrogate |
        ReadControl;
    private const uint ServiceWin32OwnProcess = 0x00000010;
    private const uint ServiceDemandStart = 0x00000003;
    private const uint ServiceErrorNormal = 0x00000001;
    private const uint ServiceControlStop = 0x00000001;
    private const uint ServiceStopped = 0x00000001;
    private const uint ServiceStartPending = 0x00000002;
    private const uint ServiceStopPending = 0x00000003;
    private const uint ServiceRunning = 0x00000004;
    private const int ScStatusProcessInfo = 0;
    private const int ServiceConfigDescription = 1;
    private const int ErrorServiceAlreadyRunning = 1056;
    private const int ErrorServiceDoesNotExist = 1060;
    private const int ErrorServiceNotActive = 1062;
    private const int ErrorInsufficientBuffer = 122;
    private const uint DaclSecurityInformation = 0x00000004;

    [DllImport("advapi32.dll", EntryPoint = "OpenSCManagerW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeServiceHandle OpenSCManager(
        string? machineName,
        string? databaseName,
        uint desiredAccess);

    [DllImport("advapi32.dll", EntryPoint = "OpenServiceW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeServiceHandle OpenService(
        SafeServiceHandle serviceManager,
        string serviceName,
        uint desiredAccess);

    [DllImport("advapi32.dll", EntryPoint = "CreateServiceW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeServiceHandle CreateService(
        SafeServiceHandle serviceManager,
        string serviceName,
        string displayName,
        uint desiredAccess,
        uint serviceType,
        uint startType,
        uint errorControl,
        string binaryPathName,
        string? loadOrderGroup,
        IntPtr tagId,
        string? dependencies,
        string? serviceStartName,
        string? password);

    [DllImport("advapi32.dll", EntryPoint = "ChangeServiceConfigW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ChangeServiceConfig(
        SafeServiceHandle service,
        uint serviceType,
        uint startType,
        uint errorControl,
        string? binaryPathName,
        string? loadOrderGroup,
        IntPtr tagId,
        string? dependencies,
        string? serviceStartName,
        string? password,
        string? displayName);

    [DllImport("advapi32.dll", EntryPoint = "QueryServiceConfigW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryServiceConfig(
        SafeServiceHandle service,
        IntPtr serviceConfig,
        int bufferSize,
        out int bytesNeeded);

    [DllImport("advapi32.dll", EntryPoint = "ChangeServiceConfig2W", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ChangeServiceConfig2(
        SafeServiceHandle service,
        int infoLevel,
        ref ServiceDescription info);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ControlService(
        SafeServiceHandle service,
        uint control,
        out ServiceStatus status);

    [DllImport("advapi32.dll", EntryPoint = "StartServiceW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool StartService(
        SafeServiceHandle service,
        int argumentCount,
        IntPtr arguments);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryServiceStatusEx(
        SafeServiceHandle service,
        int infoLevel,
        out ServiceStatusProcess status,
        int bufferSize,
        out int bytesNeeded);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetServiceObjectSecurity(
        SafeServiceHandle service,
        uint securityInformation,
        byte[] securityDescriptor);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteService(SafeServiceHandle service);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseServiceHandle(IntPtr serviceHandle);
}
