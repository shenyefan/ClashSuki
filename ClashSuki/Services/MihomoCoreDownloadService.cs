using System.IO.Compression;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.Json.Serialization;
using System.Text.Json;

namespace ClashSuki.Services;

public enum MihomoCoreReleaseKind
{
    Latest,
    Preview,
    Smart,
    Specific
}

public sealed record MihomoCoreDownloadRequest(MihomoCoreReleaseKind Kind, string SpecificVersion);

public sealed record MihomoCoreDownloadResult(
    string Version,
    string DownloadUrl,
    string ExecutablePath,
    string TempDirectory);

public sealed class MihomoCoreDownloadService
{
    private static readonly TimeSpan TagsCacheTtl = TimeSpan.FromMinutes(5);
    private const string MihomoReleaseVersionUrl = "https://github.com/MetaCubeX/mihomo/releases/latest/download/version.txt";
    private const string MihomoAlphaVersionUrl = "https://github.com/MetaCubeX/mihomo/releases/download/Prerelease-Alpha/version.txt";
    private const string MihomoSmartVersionUrl = "https://github.com/vernesong/mihomo/releases/download/Prerelease-Alpha/version.txt";
    private const string MihomoReleasePrefix = "https://github.com/MetaCubeX/mihomo/releases/download";
    private const string MihomoSmartPrefix = "https://github.com/vernesong/mihomo/releases/download/Prerelease-Alpha";
    private const string MihomoTagsUrl = "https://api.github.com/repos/MetaCubeX/mihomo/tags?per_page=100";

    private static readonly HttpClient Http = CreateHttpClient();
    private static readonly SemaphoreSlim TagsLock = new(1, 1);
    private static IReadOnlyList<string>? _cachedTags;
    private static DateTimeOffset _cachedTagsAt;

    public async Task<MihomoCoreDownloadResult> DownloadAsync(
        MihomoCoreDownloadRequest request,
        CancellationToken cancellationToken)
    {
        var info = await ResolveAsync(request, cancellationToken);
        var tempDirectory = Path.Combine(Path.GetTempPath(), "ClashSuki", "core", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);

        var archivePath = Path.Combine(tempDirectory, info.ArchiveName);
        await DownloadFileAsync(info.DownloadUrl, archivePath, cancellationToken);

        var executablePath = Path.Combine(tempDirectory, "mihomo.exe");
        ExtractExecutable(archivePath, info.ExecutableName, executablePath);

        return new MihomoCoreDownloadResult(info.Version, info.DownloadUrl, executablePath, tempDirectory);
    }

    public async Task<IReadOnlyList<string>> GetSpecificVersionsAsync(
        bool forceRefresh,
        CancellationToken cancellationToken)
    {
        await TagsLock.WaitAsync(cancellationToken);
        try
        {
            if (!forceRefresh &&
                _cachedTags is not null &&
                DateTimeOffset.Now - _cachedTagsAt < TagsCacheTtl)
            {
                return _cachedTags;
            }

            using var request = new HttpRequestMessage(HttpMethod.Get, MihomoTagsUrl);
            request.Headers.Accept.ParseAdd("application/vnd.github+json");
            request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
            using var response = await Http.SendAsync(request, cancellationToken);
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            var tags = await JsonSerializer.DeserializeAsync<List<GitHubTag>>(stream, cancellationToken: cancellationToken)
                       ?? [];
            _cachedTags = tags
                .Select(tag => tag.Name?.Trim())
                .Where(tag => !string.IsNullOrWhiteSpace(tag))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList()!;
            _cachedTagsAt = DateTimeOffset.Now;
            return _cachedTags;
        }
        finally
        {
            TagsLock.Release();
        }
    }

    private static async Task<CoreAssetInfo> ResolveAsync(
        MihomoCoreDownloadRequest request,
        CancellationToken cancellationToken)
    {
        var assetName = request.Kind == MihomoCoreReleaseKind.Smart
            ? ResolveSmartAssetName()
            : ResolveMihomoAssetName();
        var version = request.Kind switch
        {
            MihomoCoreReleaseKind.Latest => await ReadVersionAsync(MihomoReleaseVersionUrl, cancellationToken),
            MihomoCoreReleaseKind.Preview => await ReadVersionAsync(MihomoAlphaVersionUrl, cancellationToken),
            MihomoCoreReleaseKind.Smart => await ReadVersionAsync(MihomoSmartVersionUrl, cancellationToken),
            MihomoCoreReleaseKind.Specific => NormalizeSpecificVersion(request.SpecificVersion),
            _ => throw new ArgumentOutOfRangeException(nameof(request), request.Kind, "未知的内核发布类型。")
        };
        var url = request.Kind switch
        {
            MihomoCoreReleaseKind.Preview => $"{MihomoReleasePrefix}/Prerelease-Alpha/{assetName}-{version}.zip",
            MihomoCoreReleaseKind.Smart => $"{MihomoSmartPrefix}/{assetName}-{version}.zip",
            _ => $"{MihomoReleasePrefix}/{version}/{assetName}-{version}.zip"
        };

        return new CoreAssetInfo(
            version,
            url,
            $"{assetName}-{version}.zip",
            $"{assetName}.exe");
    }

    private static async Task<string> ReadVersionAsync(string url, CancellationToken cancellationToken)
    {
        using var response = await Http.GetAsync(url, cancellationToken);
        response.EnsureSuccessStatusCode();
        var version = (await response.Content.ReadAsStringAsync(cancellationToken)).Trim();
        if (string.IsNullOrWhiteSpace(version))
        {
            throw new InvalidOperationException("远端版本号为空。");
        }

        return version;
    }

    private static async Task DownloadFileAsync(string url, string targetPath, CancellationToken cancellationToken)
    {
        Exception? lastError = null;
        foreach (var candidate in await BuildDownloadUrlsAsync(url, cancellationToken))
        {
            try
            {
                using var response = await Http.GetAsync(candidate, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                response.EnsureSuccessStatusCode();
                await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
                await using var target = File.Create(targetPath);
                await source.CopyToAsync(target, cancellationToken);
                return;
            }
            catch (Exception ex)
            {
                lastError = ex;
                DiagnosticLog.WriteAppException(
                    LogSources.Core,
                    ex,
                    $"内核下载地址失败，将尝试下一个地址，地址: {candidate}",
                    "WARN");
            }
        }

        throw lastError ?? new InvalidOperationException("下载失败。");
    }

    private static async Task<IReadOnlyList<string>> BuildDownloadUrlsAsync(string url, CancellationToken cancellationToken)
    {
        var settings = await AppSettingsService.LoadAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(settings.GitHubProxy))
        {
            return [url];
        }

        var proxy = settings.GitHubProxy.Trim();
        if (!proxy.EndsWith('/'))
        {
            proxy += "/";
        }

        return [$"{proxy}{url}", url];
    }

    private static void ExtractExecutable(string archivePath, string executableName, string targetPath)
    {
        using var archive = ZipFile.OpenRead(archivePath);
        var entry = archive.Entries.FirstOrDefault(item =>
                        string.Equals(Path.GetFileName(item.FullName), executableName, StringComparison.OrdinalIgnoreCase))
                    ?? archive.Entries.FirstOrDefault(item =>
                        string.Equals(Path.GetExtension(item.FullName), ".exe", StringComparison.OrdinalIgnoreCase));
        if (entry is null)
        {
            throw new InvalidOperationException("下载包中未找到 mihomo 可执行文件。");
        }

        entry.ExtractToFile(targetPath, overwrite: true);
    }

    private static string NormalizeSpecificVersion(string version)
    {
        var normalized = version.Trim();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            throw new InvalidOperationException("指定版本不能为空。");
        }

        return normalized.StartsWith('v') ? normalized : $"v{normalized}";
    }

    private static string ResolveMihomoAssetName() =>
        RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X86 => "mihomo-windows-386",
            Architecture.Arm64 => "mihomo-windows-arm64",
            _ => "mihomo-windows-amd64-compatible"
        };

    private static string ResolveSmartAssetName() =>
        RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X86 => "mihomo-windows-386-go120",
            Architecture.Arm64 => "mihomo-windows-arm64",
            _ => "mihomo-windows-amd64-v2-go120"
        };

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient();
        client.DefaultRequestHeaders.UserAgent.ParseAdd("ClashSuki/1.0");
        return client;
    }

    private sealed record CoreAssetInfo(
        string Version,
        string DownloadUrl,
        string ArchiveName,
        string ExecutableName);

    private sealed class GitHubTag
    {
        [JsonPropertyName("name")]
        public string? Name { get; set; }
    }
}
