using System.IO.Compression;

namespace ClashSuki.Services;

public static class BackupService
{
    public static string BackupDirectory { get; } = Path.Combine(AppPaths.DataRoot, "backups");

    public static async Task<string> CreateBackupAsync(CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(BackupDirectory);
        var target = Path.Combine(BackupDirectory, $"ClashSuki-{DateTime.Now:yyyyMMdd-HHmmss}.zip");
        await Task.Run(() =>
        {
            using var archive = ZipFile.Open(target, ZipArchiveMode.Create);
            foreach (var file in Directory.EnumerateFiles(AppPaths.DataRoot, "*", SearchOption.AllDirectories))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (file.StartsWith(BackupDirectory, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var entryName = Path.GetRelativePath(AppPaths.DataRoot, file);
                archive.CreateEntryFromFile(file, entryName, CompressionLevel.Optimal);
            }
        }, cancellationToken);
        return target;
    }

    public static async Task RestoreLatestAsync(CancellationToken cancellationToken = default)
    {
        var latest = Directory.Exists(BackupDirectory)
            ? Directory.EnumerateFiles(BackupDirectory, "*.zip")
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault()
            : null;
        if (latest is null)
        {
            throw new FileNotFoundException("没有找到可恢复的备份。");
        }

        await RestoreAsync(latest, cancellationToken);
    }

    public static Task RestoreAsync(string backupPath, CancellationToken cancellationToken = default) =>
        Task.Run(() =>
        {
            if (!File.Exists(backupPath))
            {
                throw new FileNotFoundException("备份文件不存在。", backupPath);
            }

            Directory.CreateDirectory(AppPaths.DataRoot);
            using var archive = ZipFile.OpenRead(backupPath);
            foreach (var entry in archive.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (string.IsNullOrWhiteSpace(entry.Name))
                {
                    continue;
                }

                var targetPath = Path.GetFullPath(Path.Combine(AppPaths.DataRoot, entry.FullName));
                if (!targetPath.StartsWith(AppPaths.DataRoot, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
                entry.ExtractToFile(targetPath, overwrite: true);
            }

            AppSettingsService.InvalidateCache();
        }, cancellationToken);
}
