using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;

namespace ClashSuki.Service;

internal sealed class NamedPipeClientAuthorizer(ILogger<NamedPipeClientAuthorizer> logger)
{
    public bool IsAuthorized(NamedPipeServerStream pipe, out string reason)
    {
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
            if (GetAllowedClientPaths().Contains(normalizedClientPath, StringComparer.OrdinalIgnoreCase))
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

    private static IEnumerable<string> GetAllowedClientPaths()
    {
        var serviceDirectory = Path.GetFullPath(AppContext.BaseDirectory);
        var parent = Directory.GetParent(serviceDirectory)?.FullName;
        var grandParent = parent is null ? null : Directory.GetParent(parent)?.FullName;

        var roots = new[] { serviceDirectory, parent, grandParent }
            .Where(static path => !string.IsNullOrWhiteSpace(path))
            .Cast<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase);

        foreach (var root in roots)
        {
            yield return Path.GetFullPath(Path.Combine(root, "ClashSuki.exe"));
            yield return Path.GetFullPath(Path.Combine(root, "ClashSuki", "ClashSuki.exe"));
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeClientProcessId(IntPtr pipe, out uint clientProcessId);
}
