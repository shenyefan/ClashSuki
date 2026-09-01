using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Security.Principal;
using ClashSuki.ServiceContract;

namespace ClashSuki.Services;

internal static class PortableServiceMaintenanceLauncher
{
    private const string RepairRelativePath = @"ServiceInstaller\ClashSuki.Repair.exe";
    private const int ErrorCancelled = 1223;

    public static Task InstallAsync(CancellationToken cancellationToken)
    {
        EnsurePortableMode();

        var clientPath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(clientPath) || !File.Exists(clientPath))
        {
            throw new InvalidOperationException("无法确定 ClashSuki 主程序路径。");
        }

        using var identity = WindowsIdentity.GetCurrent();
        var ownerSid = identity.User?.Value;
        if (string.IsNullOrWhiteSpace(ownerSid))
        {
            throw new InvalidOperationException("无法读取当前 Windows 用户标识。");
        }

        return RunElevatedAsync(
            "安装",
            [
                ServiceProtocol.InstallPortableServiceArgument,
                "--owner-sid",
                ownerSid,
                "--data-root",
                AppPaths.DataRoot,
                "--client-path",
                Path.GetFullPath(clientPath)
            ],
            cancellationToken);
    }

    public static Task UninstallAsync(CancellationToken cancellationToken)
    {
        EnsurePortableMode();
        return RunElevatedAsync(
            "卸载",
            [ServiceProtocol.UninstallPortableServiceArgument],
            cancellationToken);
    }

    private static async Task RunElevatedAsync(
        string operation,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        var repairPath = Path.GetFullPath(Path.Combine(
            AppPaths.DistributionRootDirectory,
            RepairRelativePath));
        if (!File.Exists(repairPath))
        {
            throw new FileNotFoundException(
                "找不到便携服务维护程序，请重新解压完整 ZIP。",
                repairPath);
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = repairPath,
            WorkingDirectory = Path.GetDirectoryName(repairPath)!,
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        Process process;
        try
        {
            process = Process.Start(startInfo)
                      ?? throw new InvalidOperationException($"无法启动便携服务{operation}程序。");
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorCancelled)
        {
            throw new OperationCanceledException($"已取消服务{operation}。", ex, cancellationToken);
        }

        using (process)
        {
            await process.WaitForExitAsync(cancellationToken);
            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    $"服务{operation}失败，退出码：" +
                    $"{process.ExitCode.ToString(CultureInfo.InvariantCulture)}。请查看服务日志。");
            }
        }
    }

    private static void EnsurePortableMode()
    {
        if (PackageIdentityService.IsPackaged)
        {
            throw new InvalidOperationException("MSIX 版本不能运行便携服务维护程序。");
        }
    }
}
