using System.Diagnostics;

namespace ClashSuki.Service;

internal static class CoreReplacer
{
    public static void Replace(string sourcePath, string destinationPath)
    {
        if (!File.Exists(sourcePath))
        {
            throw new FileNotFoundException("找不到源内核程序。", sourcePath);
        }

        var directory = Path.GetDirectoryName(destinationPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        KillProcessesUsingFile(destinationPath);

        var backupPath = destinationPath + ".bak";
        if (File.Exists(destinationPath))
        {
            File.Copy(destinationPath, backupPath, overwrite: true);
        }
        else
        {
            TryDeleteFile(backupPath);
        }

        File.Copy(sourcePath, destinationPath, overwrite: true);
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
                    $"进程标识={process.Id}；{ex.Message}",
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
            ServiceDiagnostics.Write("删除内核备份", $"删除文件失败；路径={path}；{ex.Message}", "WARN");
        }
    }
}
