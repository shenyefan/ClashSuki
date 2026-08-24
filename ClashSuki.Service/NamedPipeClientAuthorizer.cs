using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using ClashSuki.ServiceContract;

namespace ClashSuki.Service;

internal sealed class NamedPipeClientAuthorizer(
    ServiceRuntimeContext runtimeContext,
    ILogger<NamedPipeClientAuthorizer> logger)
{
    public bool IsAuthorized(NamedPipeServerStream pipe, out string reason)
    {
        if (!TryGetClientSid(pipe, out var clientSid, out reason))
        {
            return false;
        }

        if (runtimeContext.IsPortable &&
            !string.Equals(
                clientSid,
                runtimeContext.PortableRegistration!.OwnerSid,
                StringComparison.OrdinalIgnoreCase))
        {
            reason = $"拒绝未登记的便携服务客户端 SID：{clientSid}";
            return false;
        }

        if (!GetNamedPipeClientProcessId(pipe.SafePipeHandle.DangerousGetHandle(), out var processId))
        {
            reason = $"无法读取管道客户端进程标识，Win32 错误码: {Marshal.GetLastWin32Error()}";
            return false;
        }

        try
        {
            using var process = Process.GetProcessById(checked((int)processId));
            var clientPath = process.MainModule?.FileName;
            if (string.IsNullOrWhiteSpace(clientPath))
            {
                reason = $"无法读取客户端进程路径，进程标识: {processId}";
                return false;
            }

            var normalizedClientPath = Path.GetFullPath(clientPath);
            if (runtimeContext.IsPortable)
            {
                if (!ValidatePortableClient(normalizedClientPath, out reason))
                {
                    return false;
                }

                reason = string.Empty;
                return true;
            }

            if (runtimeContext.GetTrustedMsixClientPaths().Contains(normalizedClientPath))
            {
                reason = string.Empty;
                return true;
            }

            reason = $"拒绝非 ClashSuki 客户端，进程标识: {processId}，路径: {normalizedClientPath}";
            return false;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "验证管道客户端失败，进程标识: {ProcessId}", processId);
            reason = $"无法验证客户端进程，进程标识: {processId}";
            return false;
        }
    }

    private bool ValidatePortableClient(string clientPath, out string reason)
    {
        var registration = runtimeContext.PortableRegistration!;
        if (!PortableServiceConfiguration.PathsEqual(clientPath, registration.ClientPath))
        {
            reason = $"便携服务客户端路径未登记：{clientPath}";
            return false;
        }

        var clientDllPath = Path.Combine(Path.GetDirectoryName(clientPath)!, "ClashSuki.dll");
        if (!IsRegularFile(clientPath) || !IsRegularFile(clientDllPath))
        {
            reason = "便携服务客户端文件缺失或是重解析点。";
            return false;
        }

        if (!string.Equals(
                FileIntegrity.ComputeSha256(clientPath),
                registration.ClientExeSha256,
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(
                FileIntegrity.ComputeSha256(clientDllPath),
                registration.ClientDllSha256,
                StringComparison.OrdinalIgnoreCase))
        {
            reason = "便携服务客户端完整性校验失败，请重新安装便携服务。";
            return false;
        }

        reason = string.Empty;
        return true;
    }

    private static bool TryGetClientSid(
        NamedPipeServerStream pipe,
        out string clientSid,
        out string reason)
    {
        string? capturedSid = null;
        try
        {
            pipe.RunAsClient(() =>
            {
                using var identity = WindowsIdentity.GetCurrent(ifImpersonating: true);
                capturedSid = identity?.User?.Value;
            });
        }
        catch (Exception ex)
        {
            clientSid = string.Empty;
            reason = $"无法读取管道客户端 SID：{ex.Message}";
            return false;
        }

        if (string.IsNullOrWhiteSpace(capturedSid))
        {
            clientSid = string.Empty;
            reason = "管道客户端 SID 为空。";
            return false;
        }

        clientSid = capturedSid;
        reason = string.Empty;
        return true;
    }

    private static bool IsRegularFile(string path) =>
        File.Exists(path) &&
        (File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0;

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeClientProcessId(IntPtr pipe, out uint clientProcessId);
}
