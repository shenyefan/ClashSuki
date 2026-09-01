using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using ClashSuki.ServiceContract;
using Microsoft.Win32.SafeHandles;

namespace ClashSuki.Repair;

internal sealed class WindowsServiceInstaller : IDisposable
{
    private const string ServiceDisplayName = "ClashSuki Portable Service";
    private static readonly TimeSpan ServiceTransitionTimeout = TimeSpan.FromSeconds(20);
    private readonly ServiceHandle _manager;

    public WindowsServiceInstaller()
    {
        _manager = OpenSCManager(null, null, ScManagerConnect | ScManagerCreateService);
        if (_manager.IsInvalid)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "无法打开 Windows 服务控制管理器。");
        }
    }

    public void Dispose() => _manager.Dispose();

    public ServiceHandle? TryOpen(string serviceName)
    {
        var handle = OpenService(_manager, serviceName, ServiceAllAccess);
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

    public ServiceHandle Create(string imagePath)
    {
        var handle = CreateServiceNative(
            _manager,
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

    public void UpdateConfiguration(ServiceHandle service, string imagePath)
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

    public void ValidateConfiguration(ServiceHandle service, string expectedImagePath)
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

    public void SetDescription(ServiceHandle service, string description)
    {
        var value = new ServiceDescription { Description = description };
        if (!ChangeServiceConfig2(service, ServiceConfigDescription, ref value))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "无法设置 ClashSuki 便携服务说明。");
        }
    }

    public void ApplyAccessControl(ServiceHandle service, SecurityIdentifier ownerSid)
    {
        var systemSid = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        var administratorsSid = new SecurityIdentifier(
            WellKnownSidType.BuiltinAdministratorsSid,
            null);
        var discretionaryAcl = new RawAcl(GenericAcl.AclRevision, capacity: 3);
        discretionaryAcl.InsertAce(0, CreateServiceAccessAce(systemSid, ServiceAllAccess));
        discretionaryAcl.InsertAce(1, CreateServiceAccessAce(administratorsSid, ServiceAllAccess));
        discretionaryAcl.InsertAce(2, CreateServiceAccessAce(ownerSid, PortableOwnerServiceAccess));

        var descriptor = new RawSecurityDescriptor(
            ControlFlags.DiscretionaryAclPresent,
            administratorsSid,
            systemSid,
            systemAcl: null,
            discretionaryAcl);
        var descriptorBytes = new byte[descriptor.BinaryLength];
        descriptor.GetBinaryForm(descriptorBytes, 0);

        if (!SetServiceObjectSecurity(service, DaclSecurityInformation, descriptorBytes))
        {
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                "无法设置 ClashSuki 便携服务访问权限。");
        }
    }

    public void StopAndWait(ServiceHandle service)
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

    public void StartAndWait(ServiceHandle service)
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

    public void Delete(ServiceHandle service)
    {
        if (!DeleteService(service))
        {
            var error = Marshal.GetLastWin32Error();
            if (error != ErrorServiceMarkedForDelete)
            {
                throw new Win32Exception(error, "无法删除 ClashSuki 便携服务。");
            }
        }
    }

    private static QueriedServiceConfiguration QueryConfiguration(ServiceHandle service)
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

    private static CommonAce CreateServiceAccessAce(SecurityIdentifier sid, uint accessMask) =>
        new(
            AceFlags.None,
            AceQualifier.AccessAllowed,
            checked((int)accessMask),
            sid,
            isCallback: false,
            opaque: null);

    private static void WaitForState(
        ServiceHandle service,
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

    private static ServiceStatusProcess QueryStatus(ServiceHandle service)
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

    private sealed record QueriedServiceConfiguration(
        uint ServiceType,
        string BinaryPathName,
        string ServiceStartName);

    internal sealed class ServiceHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        private ServiceHandle()
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
    private const int ErrorServiceMarkedForDelete = 1072;
    private const int ErrorInsufficientBuffer = 122;
    private const uint DaclSecurityInformation = 0x00000004;

    [DllImport("advapi32.dll", EntryPoint = "OpenSCManagerW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ServiceHandle OpenSCManager(
        string? machineName,
        string? databaseName,
        uint desiredAccess);

    [DllImport("advapi32.dll", EntryPoint = "OpenServiceW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ServiceHandle OpenService(
        ServiceHandle serviceManager,
        string serviceName,
        uint desiredAccess);

    [DllImport("advapi32.dll", EntryPoint = "CreateServiceW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ServiceHandle CreateServiceNative(
        ServiceHandle serviceManager,
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
        ServiceHandle service,
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
        ServiceHandle service,
        IntPtr serviceConfig,
        int bufferSize,
        out int bytesNeeded);

    [DllImport("advapi32.dll", EntryPoint = "ChangeServiceConfig2W", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ChangeServiceConfig2(
        ServiceHandle service,
        int infoLevel,
        ref ServiceDescription info);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ControlService(
        ServiceHandle service,
        uint control,
        out ServiceStatus status);

    [DllImport("advapi32.dll", EntryPoint = "StartServiceW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool StartService(
        ServiceHandle service,
        int argumentCount,
        IntPtr arguments);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryServiceStatusEx(
        ServiceHandle service,
        int infoLevel,
        out ServiceStatusProcess status,
        int bufferSize,
        out int bytesNeeded);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetServiceObjectSecurity(
        ServiceHandle service,
        uint securityInformation,
        byte[] securityDescriptor);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteService(ServiceHandle service);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseServiceHandle(IntPtr serviceHandle);
}
