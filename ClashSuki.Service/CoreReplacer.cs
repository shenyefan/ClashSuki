using System.Diagnostics;

namespace ClashSuki.Service;

internal static class CoreReplacer
{
    public static void Replace(string sourcePath, string destinationPath)
    {
        var normalizedSourcePath = Path.GetFullPath(sourcePath);
        var normalizedDestinationPath = Path.GetFullPath(destinationPath);
        ValidatePaths(normalizedSourcePath, normalizedDestinationPath);

        if (!File.Exists(normalizedSourcePath))
        {
            throw new FileNotFoundException("找不到源内核程序。", normalizedSourcePath);
        }

        var directory = Path.GetDirectoryName(normalizedDestinationPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        KillProcessesUsingFile(normalizedDestinationPath);

        var backupPath = normalizedDestinationPath + ".bak";
        var restoringBackup = string.Equals(
            normalizedSourcePath,
            backupPath,
            StringComparison.OrdinalIgnoreCase);
        if (!restoringBackup && File.Exists(normalizedDestinationPath))
        {
            File.Copy(normalizedDestinationPath, backupPath, overwrite: true);
        }
        else if (!restoringBackup)
        {
            TryDeleteFile(backupPath);
        }

        File.Copy(normalizedSourcePath, normalizedDestinationPath, overwrite: true);
    }

    private static void ValidatePaths(string sourcePath, string destinationPath)
    {
        if (!string.Equals(Path.GetFileName(destinationPath), "mihomo.exe", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(
                Path.GetFileName(Path.GetDirectoryName(destinationPath)),
                "core",
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("提权进程仅允许替换 ClashSuki 的 mihomo.exe。");
        }

        var allowedSources = new[]
        {
            destinationPath + ".new",
            destinationPath + ".bak"
        };
        if (!allowedSources.Contains(sourcePath, StringComparer.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("内核替换源文件不属于 ClashSuki 的受管临时文件。");
        }
    }

    private static void KillProcessesUsingFile(string filePath)
    {
        var processName = Path.GetFileNameWithoutExtension(filePath);
        foreach (var process in Process.GetProcessesByName(processName))
        {
            try
            {
                if (process.Id == Environment.ProcessId)
                {
                    continue;
                }

                var imagePath = process.MainModule?.FileName;
                if (string.Equals(imagePath, filePath, StringComparison.OrdinalIgnoreCase))
                {
                    process.Kill(entireProcessTree: true);
                    if (!process.WaitForExit(5000))
                    {
                        throw new TimeoutException($"等待进程 {process.Id} 退出超时。");
                    }
                }
            }
            catch (Exception ex)
            {
                ServiceDiagnostics.Write(
                    "停止占用内核文件的进程",
                    $"进程标识: {process.Id}，{ex.Message}",
                    "WARN");
            }
            finally
            {
                process.Dispose();
            }
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex)
        {
            ServiceDiagnostics.Write("删除内核备份", $"删除文件失败，路径: {path}，{ex.Message}", "WARN");
        }
    }
}
