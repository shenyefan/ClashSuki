using System.Diagnostics;

namespace ClashSuki.Services;

public static class MihomoCoreFileInstaller
{
    public static async Task InstallAsync(
        string sourcePath,
        string destinationPath,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
        var pendingPath = destinationPath + ".new";
        var backupPath = destinationPath + ".bak";

        File.Copy(sourcePath, pendingPath, overwrite: true);
        try
        {
            await ReplaceDestinationAsync(pendingPath, destinationPath, backupPath, cancellationToken);
        }
        finally
        {
            TryDeleteFile(pendingPath);
        }
    }

    public static bool HasBackup(string destinationPath) =>
        File.Exists(destinationPath + ".bak");

    public static async Task RestoreBackupAsync(
        string destinationPath,
        CancellationToken cancellationToken = default)
    {
        var backupPath = destinationPath + ".bak";
        if (!File.Exists(backupPath))
        {
            TryDeleteFile(destinationPath);
            return;
        }

        try
        {
            File.Copy(backupPath, destinationPath, overwrite: true);
        }
        catch (Exception ex) when (IsAccessDenied(ex))
        {
            await MihomoServiceManager.ReplaceCoreFileElevatedAsync(backupPath, destinationPath, cancellationToken);
        }
    }

    internal static async Task ReplaceInProcessAsync(
        string sourcePath,
        string destinationPath,
        CancellationToken cancellationToken)
    {
        KillProcessesUsingFile(destinationPath);
        await WaitForWritableAsync(destinationPath, cancellationToken);

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
                var imagePath = process.MainModule?.FileName;
                if (string.Equals(imagePath, filePath, StringComparison.OrdinalIgnoreCase))
                {
                    process.Kill(entireProcessTree: true);
                    process.WaitForExit(5000);
                }
            }
            catch (Exception ex)
            {
                DiagnosticLog.WriteAppException(
                    LogSources.Core,
                    ex,
                    $"停止占用内核文件的进程失败；进程标识={process.Id}",
                    "WARN");
            }
            finally
            {
                process.Dispose();
            }
        }
    }

    private static async Task ReplaceDestinationAsync(
        string pendingPath,
        string destinationPath,
        string backupPath,
        CancellationToken cancellationToken)
    {
        await WaitForWritableAsync(destinationPath, cancellationToken);

        try
        {
            if (File.Exists(destinationPath))
            {
                File.Copy(destinationPath, backupPath, overwrite: true);
            }
            else
            {
                TryDeleteFile(backupPath);
            }

            File.Move(pendingPath, destinationPath, overwrite: true);
        }
        catch (Exception ex) when (IsAccessDenied(ex))
        {
            await MihomoServiceManager.ReplaceCoreFileElevatedAsync(pendingPath, destinationPath, cancellationToken);
        }
    }

    internal static async Task WaitForWritableAsync(string path, CancellationToken cancellationToken, int timeoutMs = 8000)
    {
        if (!File.Exists(path))
        {
            return;
        }

        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await using var stream = new FileStream(
                    path,
                    FileMode.Open,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    1,
                    FileOptions.Asynchronous);
                return;
            }
            catch (IOException)
            {
                await Task.Delay(200, cancellationToken);
            }
            catch (UnauthorizedAccessException ex)
            {
                DiagnosticLog.WriteAppException(
                    LogSources.Core,
                    ex,
                    $"等待内核文件可写时访问被拒绝；路径={path}",
                    "WARN");
                return;
            }
        }

        DiagnosticLog.WriteApp(
            LogSources.Core,
            "WARN",
            $"等待内核文件可写超时；路径={path}；超时={timeoutMs} 毫秒");
    }

    private static bool IsAccessDenied(Exception ex) =>
        ex is UnauthorizedAccessException ||
        ex is IOException { HResult: unchecked((int)0x80070005) or unchecked((int)0x80070020) };

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
            DiagnosticLog.WriteAppException(
                LogSources.Core,
                ex,
                $"删除内核临时文件失败；路径={path}",
                "WARN");
        }
    }
}
