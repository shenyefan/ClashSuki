using System.Security.Cryptography;
using System.Text;
using ClashSuki.Services;
using Windows.Storage;
using Windows.Storage.FileProperties;

namespace ClashSuki.Utilities;

public static class ProcessIconProvider
{
    private static readonly Dictionary<string, Uri> Cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> Failed = new(StringComparer.OrdinalIgnoreCase);

    public static async Task<Uri?> GetIconAsync(string? processPath, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(processPath) || !File.Exists(processPath))
        {
            return null;
        }

        if (Cache.TryGetValue(processPath, out var cached))
        {
            return cached;
        }

        if (Failed.Contains(processPath))
        {
            return null;
        }

        try
        {
            var cachePath = GetCachePath(processPath);
            if (File.Exists(cachePath))
            {
                var uri = new Uri(cachePath);
                Cache[processPath] = uri;
                return uri;
            }

            cancellationToken.ThrowIfCancellationRequested();
            var file = await StorageFile.GetFileFromPathAsync(processPath);
            using var thumbnail = await file.GetThumbnailAsync(
                ThumbnailMode.SingleItem,
                32,
                ThumbnailOptions.UseCurrentScale);

            if (thumbnail.Size == 0)
            {
                Failed.Add(processPath);
                return null;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);
            thumbnail.Seek(0);
            await using (var target = File.Create(cachePath))
            {
                await thumbnail.AsStreamForRead().CopyToAsync(target, cancellationToken);
            }

            var iconUri = new Uri(cachePath);
            Cache[processPath] = iconUri;
            return iconUri;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception ex)
        {
            Failed.Add(processPath);
            DiagnosticLog.WriteAppExceptionThrottled(
                $"process-icon:{processPath}",
                LogSources.Connection,
                ex,
                $"读取进程图标失败；路径={processPath}",
                level: "WARN");
            return null;
        }
    }

    private static string GetCachePath(string processPath)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(processPath.ToUpperInvariant())));
        var directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ClashSuki",
            "icon-cache");

        return Path.Combine(directory, $"{hash}.png");
    }
}
