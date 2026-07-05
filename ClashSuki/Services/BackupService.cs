using System.IO.Compression;

namespace ClashSuki.Services;

public static class BackupService
{
    public static string BackupDirectory { get; } = Path.Combine(AppPaths.DataRoot, "backups");

    public static async Task<string> CreateBackupAsync(CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(BackupDirectory);
        var target = Path.Combine(BackupDirectory, $"ClashSuki-{DateTime.Now:yyyyMMdd-HHmmss-fff}.zip");
        var tempPath = target + ".tmp";
        try
        {
            await Task.Run(() =>
            {
                using var archive = ZipFile.Open(tempPath, ZipArchiveMode.Create);
                foreach (var file in Directory.EnumerateFiles(AppPaths.DataRoot, "*", SearchOption.AllDirectories))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var entryName = NormalizeEntryName(Path.GetRelativePath(AppPaths.DataRoot, file));
                    if (!IsBackupEntry(entryName))
                    {
                        continue;
                    }

                    archive.CreateEntryFromFile(file, entryName, CompressionLevel.Optimal);
                }
            }, cancellationToken);

            File.Move(tempPath, target);
            return target;
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
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
            var entries = archive.Entries
                .Where(entry => !string.IsNullOrWhiteSpace(entry.Name))
                .Where(entry => IsBackupEntry(NormalizeEntryName(entry.FullName)))
                .ToList();

            foreach (var entry in entries)
            {
                var targetPath = Path.GetFullPath(Path.Combine(
                    AppPaths.DataRoot,
                    NormalizeEntryName(entry.FullName)));
                if (!IsWithinDataRoot(targetPath))
                {
                    throw new InvalidDataException($"备份包含非法路径：{entry.FullName}");
                }
            }

            ClearRestoredDirectories(entries);
            foreach (var entry in entries)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var targetPath = Path.GetFullPath(Path.Combine(
                    AppPaths.DataRoot,
                    NormalizeEntryName(entry.FullName)));

                Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
                entry.ExtractToFile(targetPath, overwrite: true);
            }

            AppSettingsService.InvalidateCache();
        }, cancellationToken);

    private static void ClearRestoredDirectories(IReadOnlyCollection<ZipArchiveEntry> entries)
    {
        foreach (var name in new[] { "profiles", "overrides" })
        {
            var prefix = $"{name}/";
            if (!entries.Any(entry =>
                    NormalizeEntryName(entry.FullName).StartsWith(
                        prefix,
                        StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            var path = Path.GetFullPath(Path.Combine(AppPaths.DataRoot, name));
            if (!IsWithinDataRoot(path))
            {
                throw new InvalidOperationException($"恢复目录不在应用数据目录内：{path}");
            }

            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
    }

    private static bool IsBackupEntry(string entryName) =>
        entryName.Equals("app-settings.json", StringComparison.OrdinalIgnoreCase) ||
        entryName.Equals("config/config-base.yaml", StringComparison.OrdinalIgnoreCase) ||
        entryName.StartsWith("profiles/", StringComparison.OrdinalIgnoreCase) ||
        entryName.StartsWith("overrides/", StringComparison.OrdinalIgnoreCase);

    private static string NormalizeEntryName(string value) =>
        value.Replace('\\', '/').TrimStart('/');

    private static bool IsWithinDataRoot(string path)
    {
        var relative = Path.GetRelativePath(
            Path.GetFullPath(AppPaths.DataRoot),
            Path.GetFullPath(path));
        return relative != ".." &&
               !Path.IsPathRooted(relative) &&
               !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal);
    }
}
