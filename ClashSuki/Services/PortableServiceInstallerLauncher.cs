using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Security.Principal;

namespace ClashSuki.Services;

internal static class PortableServiceInstallerLauncher
{
    private const string InstallerRelativePath = @"ServiceInstaller\ClashSuki.Repair.exe";
    private const int ErrorCancelled = 1223;

    public static async Task InstallAsync(CancellationToken cancellationToken)
    {
        if (PackageIdentityService.IsPackaged)
        {
            throw new InvalidOperationException("MSIX 版本不能运行便携服务安装程序。");
        }

        var installerPath = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            InstallerRelativePath));
        if (!File.Exists(installerPath))
        {
            throw new FileNotFoundException(
                "找不到便携服务安装程序，请重新解压完整 ZIP。",
                installerPath);
        }

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

        var startInfo = new ProcessStartInfo
        {
            FileName = installerPath,
            WorkingDirectory = Path.GetDirectoryName(installerPath)!,
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden
        };
        startInfo.ArgumentList.Add("--install-portable-service");
        startInfo.ArgumentList.Add("--owner-sid");
        startInfo.ArgumentList.Add(ownerSid);
        startInfo.ArgumentList.Add("--data-root");
        startInfo.ArgumentList.Add(AppPaths.DataRoot);
        startInfo.ArgumentList.Add("--client-path");
        startInfo.ArgumentList.Add(Path.GetFullPath(clientPath));

        Process process;
        try
        {
            process = Process.Start(startInfo)
                      ?? throw new InvalidOperationException("无法启动便携服务安装程序。");
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorCancelled)
        {
            throw new OperationCanceledException("已取消服务安装。", ex, cancellationToken);
        }

        using (process)
        {
            await process.WaitForExitAsync(cancellationToken);
            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    $"服务安装失败，退出码：{process.ExitCode.ToString(CultureInfo.InvariantCulture)}。请查看服务日志。");
            }
        }
    }
}
