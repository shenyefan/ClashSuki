using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using ClashSuki.Services;

namespace ClashSuki.Utilities;

public static class ProxyIconProvider
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };
    private static readonly SemaphoreSlim DownloadGate = new(4, 4);
    private static readonly ConcurrentDictionary<string, Uri> Cache = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, byte> Failed = new(StringComparer.Ordinal);

    public static async Task<Uri?> GetIconUriAsync(string? icon, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(icon))
        {
            return null;
        }

        var key = icon.Trim();
        if (TryGetCachedUri(key) is { } cached)
        {
            return cached;
        }

        if (Failed.ContainsKey(key))
        {
            return null;
        }

        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            byte[]? bytes;
            var extension = ".png";

            if (key.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                key.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                await DownloadGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    using var response = await Http.GetAsync(key, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                        .ConfigureAwait(false);
                    if (!response.IsSuccessStatusCode)
                    {
                        Failed.TryAdd(key, 0);
                        DiagnosticLog.WriteApp(
                            LogSources.Proxy,
                            "WARN",
                            $"代理图标下载失败，状态码: {(int)response.StatusCode}，图标: {DescribeIcon(key)}");
                        return null;
                    }

                    bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
                    extension = ResolveExtension(response.Content.Headers.ContentType?.MediaType, key);
                }
                finally
                {
                    DownloadGate.Release();
                }
            }
            else if (key.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                (bytes, extension) = ParseDataUri(key);
            }
            else if (key.StartsWith("<svg", StringComparison.OrdinalIgnoreCase))
            {
                bytes = Encoding.UTF8.GetBytes(key);
                extension = ".svg";
            }
            else if (File.Exists(key))
            {
                var fileUri = new Uri(Path.GetFullPath(key));
                Cache[key] = fileUri;
                return fileUri;
            }
            else
            {
                Failed.TryAdd(key, 0);
                return null;
            }

            if (bytes is not { Length: > 0 })
            {
                Failed.TryAdd(key, 0);
                return null;
            }

            var cachePath = GetCachePath(key, extension);
            Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);
            await File.WriteAllBytesAsync(cachePath, bytes, cancellationToken).ConfigureAwait(false);

            var uri = new Uri(cachePath);
            Cache[key] = uri;
            return uri;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception ex)
        {
            Failed.TryAdd(key, 0);
            DiagnosticLog.WriteAppExceptionThrottled(
                $"proxy-icon:{HashKey(key)}",
                LogSources.Proxy,
                ex,
                $"加载代理图标失败，图标: {DescribeIcon(key)}",
                level: "WARN");
            return null;
        }
    }

    public static async Task<Uri?> RefreshIconUriAsync(
        string? icon,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(icon))
        {
            return null;
        }

        var key = icon.Trim();
        Cache.TryRemove(key, out _);
        Failed.TryRemove(key, out _);

        var hash = HashKey(key);
        foreach (var extension in new[] { ".png", ".jpg", ".webp", ".gif", ".svg" })
        {
            var path = Path.Combine(GetCacheDirectory(), hash + extension);
            try
            {
                File.Delete(path);
            }
            catch (Exception ex)
            {
                DiagnosticLog.WriteAppExceptionThrottled(
                    $"proxy-icon-cache-delete:{hash}",
                    LogSources.Proxy,
                    ex,
                    $"清理代理图标缓存失败，图标: {DescribeIcon(key)}",
                    level: "WARN");
            }
        }

        return await GetIconUriAsync(key, cancellationToken).ConfigureAwait(false);
    }

    public static Uri? TryGetCachedUri(string? icon)
    {
        if (string.IsNullOrWhiteSpace(icon))
        {
            return null;
        }

        var key = icon.Trim();
        if (Cache.TryGetValue(key, out var cached))
        {
            return cached;
        }

        var cachePath = GetCachePath(key);
        if (!File.Exists(cachePath))
        {
            return null;
        }

        cached = new Uri(cachePath);
        Cache[key] = cached;
        return cached;
    }

    private static string ResolveExtension(string? mediaType, string url)
    {
        return mediaType?.ToLowerInvariant() switch
        {
            "image/jpeg" => ".jpg",
            "image/webp" => ".webp",
            "image/gif" => ".gif",
            "image/svg+xml" => ".svg",
            _ when url.Contains(".jpg", StringComparison.OrdinalIgnoreCase) ||
                   url.Contains(".jpeg", StringComparison.OrdinalIgnoreCase) => ".jpg",
            _ when url.Contains(".webp", StringComparison.OrdinalIgnoreCase) => ".webp",
            _ when url.Contains(".svg", StringComparison.OrdinalIgnoreCase) => ".svg",
            _ => ".png"
        };
    }

    private static (byte[]? Bytes, string Extension) ParseDataUri(string dataUri)
    {
        var match = Regex.Match(
            dataUri,
            @"^data:(?<mime>[^;]+)?(?:;base64)?,(?<data>.+)$",
            RegexOptions.Singleline | RegexOptions.IgnoreCase);
        if (!match.Success)
        {
            return (null, ".png");
        }

        var mime = match.Groups["mime"].Value.ToLowerInvariant();
        var payload = match.Groups["data"].Value;
        var extension = mime switch
        {
            "image/jpeg" => ".jpg",
            "image/webp" => ".webp",
            "image/gif" => ".gif",
            "image/svg+xml" => ".svg",
            _ => ".png"
        };

        try
        {
            if (dataUri.Contains(";base64", StringComparison.OrdinalIgnoreCase))
            {
                return (Convert.FromBase64String(payload), extension);
            }

            return (Encoding.UTF8.GetBytes(Uri.UnescapeDataString(payload)), extension);
        }
        catch (Exception ex)
        {
            DiagnosticLog.WriteAppExceptionThrottled(
                $"proxy-icon-data:{HashKey(dataUri)}",
                LogSources.Proxy,
                ex,
                "解析嵌入式代理图标失败",
                level: "WARN");
            return (null, extension);
        }
    }

    private static string DescribeIcon(string icon) =>
        icon.StartsWith("data:", StringComparison.OrdinalIgnoreCase)
            ? $"嵌入式数据（{HashKey(icon)[..12]}）"
            : icon.StartsWith("<svg", StringComparison.OrdinalIgnoreCase)
                ? $"内嵌 SVG（{HashKey(icon)[..12]}）"
                : icon;

    private static string HashKey(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static string GetCachePath(string iconKey, string? extension = null)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(iconKey)));
        var directory = GetCacheDirectory();

        if (string.IsNullOrWhiteSpace(extension))
        {
            if (iconKey.StartsWith("<svg", StringComparison.OrdinalIgnoreCase))
            {
                extension = ".svg";
            }
            else if (iconKey.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                extension = ParseDataUri(iconKey).Extension;
            }
            else
            {
                extension = ResolveExtension(null, iconKey);
            }
        }

        return Path.Combine(directory, hash + extension);
    }

    private static string GetCacheDirectory() =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ClashSuki",
            "proxy-icon-cache");
}
