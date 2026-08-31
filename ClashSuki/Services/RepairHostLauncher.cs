using System.ComponentModel;
using System.Diagnostics;
using Windows.ApplicationModel;

namespace ClashSuki.Services;

internal static class RepairHostLauncher
{
    private const string RepairExecutableName = "ClashSuki.Repair.exe";
    private const int ErrorCancelled = 1223;
    private static readonly TimeSpan StaleHostAge = TimeSpan.FromDays(1);

    public static async Task StartPackageRepairAfterCurrentProcessExitsAsync(
        CancellationToken cancellationToken)
    {
        if (!PackageIdentityService.IsPackaged)
        {
            throw new InvalidOperationException("便携版不使用应用包修复。");
        }

        var destinationDirectory = await CreatePackagedRepairHostAsync(cancellationToken);
        try
        {
            var package = Package.Current;
            var startInfo = new ProcessStartInfo
            {
                FileName = Path.Combine(destinationDirectory, RepairExecutableName),
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = destinationDirectory
            };
            startInfo.ArgumentList.Add("--wait-pid");
            startInfo.ArgumentList.Add(Environment.ProcessId.ToString(
                System.Globalization.CultureInfo.InvariantCulture));
            startInfo.ArgumentList.Add("--package-full-name");
            startInfo.ArgumentList.Add(package.Id.FullName);
            startInfo.ArgumentList.Add("--app-user-model-id");
            startInfo.ArgumentList.Add($"{package.Id.FamilyName}!App");

            _ = Process.Start(startInfo)
                ?? throw new InvalidOperationException("无法启动应用包修复进程。");
        }
        catch
        {
            TryDeleteDirectory(destinationDirectory);
            throw;
        }
    }

    public static async Task RunElevatedAsync(
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        cancellationToken.ThrowIfCancellationRequested();

        string? temporaryDirectory = null;
        var executablePath = PackageIdentityService.IsPackaged
            ? Path.Combine(
                temporaryDirectory = await CreatePackagedRepairHostAsync(cancellationToken),
                RepairExecutableName)
            : ResolvePortableRepairExecutable();

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = executablePath,
                WorkingDirectory = Path.GetDirectoryName(executablePath)!,
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
                          ?? throw new InvalidOperationException("无法启动提权修复进程。");
            }
            catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorCancelled)
            {
                throw new OperationCanceledException("已取消管理员权限请求。", ex, cancellationToken);
            }

            using (process)
            {
                await process.WaitForExitAsync();
                if (process.ExitCode != 0)
                {
                    throw new InvalidOperationException(
                        $"提权修复进程执行失败，退出码：{process.ExitCode}。请查看 repair.log。");
                }
            }
        }
        finally
        {
            if (temporaryDirectory is not null)
            {
                TryDeleteDirectory(temporaryDirectory);
            }
        }
    }

    private static async Task<string> CreatePackagedRepairHostAsync(
        CancellationToken cancellationToken)
    {
        var sourceDirectory = ResolvePackagedRepairHostDirectory();
        var repairHostRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ClashSuki",
            "RepairHost");
        CleanupStaleRepairHosts(repairHostRoot);
        var destinationDirectory = Path.Combine(repairHostRoot, Guid.NewGuid().ToString("N"));
        await Task.Run(
            () => CopyDirectory(sourceDirectory, destinationDirectory, cancellationToken),
            cancellationToken);
        return destinationDirectory;
    }

    private static string ResolvePackagedRepairHostDirectory()
    {
        var directory = Path.Combine(AppPaths.DistributionRootDirectory, "ClashSuki.Repair");
        var executablePath = Path.Combine(directory, RepairExecutableName);
        return File.Exists(executablePath)
            ? directory
            : throw new FileNotFoundException(
                "找不到 ClashSuki.Repair.exe，请重新生成 MSIX。",
                executablePath);
    }

    private static string ResolvePortableRepairExecutable()
    {
        var path = Path.GetFullPath(Path.Combine(
            AppPaths.DistributionRootDirectory,
            "ServiceInstaller",
            RepairExecutableName));
        return File.Exists(path)
            ? path
            : throw new FileNotFoundException(
                "找不到 ClashSuki.Repair.exe，请重新解压完整便携包。",
                path);
    }

    private static void CopyDirectory(
        string sourceDirectory,
        string destinationDirectory,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(destinationDirectory);
        foreach (var directory in Directory.EnumerateDirectories(
                     sourceDirectory,
                     "*",
                     SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            Directory.CreateDirectory(Path.Combine(
                destinationDirectory,
                Path.GetRelativePath(sourceDirectory, directory)));
        }

        foreach (var file in Directory.EnumerateFiles(
                     sourceDirectory,
                     "*",
                     SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var destination = Path.Combine(
                destinationDirectory,
                Path.GetRelativePath(sourceDirectory, file));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination, overwrite: false);
        }
    }

    private static void CleanupStaleRepairHosts(string repairHostRoot)
    {
        if (!Directory.Exists(repairHostRoot))
        {
            return;
        }

        var cutoff = DateTime.UtcNow - StaleHostAge;
        foreach (var directory in Directory.EnumerateDirectories(repairHostRoot))
        {
            try
            {
                if (Directory.GetLastWriteTimeUtc(directory) < cutoff)
                {
                    Directory.Delete(directory, recursive: true);
                }
            }
            catch (Exception ex)
            {
                DiagnosticLog.WriteAppException(
                    "REPAIR-CLEANUP",
                    ex,
                    $"清理过期修复目录失败：{directory}");
            }
        }
    }

    private static void TryDeleteDirectory(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch (Exception ex)
        {
            DiagnosticLog.WriteAppException(
                "REPAIR-CLEANUP",
                ex,
                $"清理修复目录失败：{directory}");
        }
    }
}
