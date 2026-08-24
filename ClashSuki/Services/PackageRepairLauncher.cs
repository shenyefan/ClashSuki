using System.Diagnostics;
using Windows.ApplicationModel;

namespace ClashSuki.Services;

internal static class PackageRepairLauncher
{
    private const string RepairExecutableName = "ClashSuki.Repair.exe";
    private static readonly TimeSpan StaleHostAge = TimeSpan.FromDays(1);

    public static async Task StartAfterCurrentProcessExitsAsync(
        CancellationToken cancellationToken)
    {
        if (!PackageIdentityService.IsPackaged)
        {
            throw new InvalidOperationException(
                "便携版不使用应用包修复。");
        }

        var sourceDirectory = ResolveRepairHostDirectory();
        var repairHostRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ClashSuki",
            "RepairHost");
        CleanupStaleRepairHosts(repairHostRoot);
        var destinationDirectory = Path.Combine(repairHostRoot, Guid.NewGuid().ToString("N"));

        try
        {
            await Task.Run(
                () => CopyDirectory(sourceDirectory, destinationDirectory, cancellationToken),
                cancellationToken);

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

    private static string ResolveRepairHostDirectory()
    {
        var candidates = new[]
        {
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "ClashSuki.Repair")),
            Path.Combine(AppContext.BaseDirectory, "ClashSuki.Repair")
        };

        return candidates.FirstOrDefault(
                   path => File.Exists(Path.Combine(path, RepairExecutableName)))
               ?? throw new FileNotFoundException(
                   "找不到 ClashSuki.Repair.exe，请重新生成 ClashSuki.Package。",
                   Path.Combine(candidates[0], RepairExecutableName));
    }

    private static void CopyDirectory(
        string sourceDirectory,
        string destinationDirectory,
        CancellationToken cancellationToken)
    {
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

        Directory.CreateDirectory(destinationDirectory);
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
                $"清理未启动的修复目录失败：{directory}");
        }
    }
}
