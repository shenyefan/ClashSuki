using System.IO;
using System.Net.Http;

namespace ClashSuki.Services;

public sealed class RemoteConfigService : IDisposable
{
    private const int MaxConfigBytes = 5 * 1024 * 1024;
    private readonly HttpClient _client = new()
    {
        Timeout = TimeSpan.FromSeconds(30)
    };

    public async Task<string> DownloadToTemporaryFileAsync(
        string urlText,
        string? fileName,
        CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(urlText.Trim(), UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https"))
        {
            throw new ArgumentException("配置地址必须是 http 或 https。");
        }

        await AppPaths.BootstrapAsync(cancellationToken);

        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.UserAgent.ParseAdd("clash.meta/unknown");
        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();

        if (response.Content.Headers.ContentLength > MaxConfigBytes)
        {
            throw new InvalidOperationException($"配置文件超过大小限制 {MaxConfigBytes} bytes。");
        }

        var normalizedName = NormalizeConfigFileName(fileName, InferFileName(uri));
        var tempPath = Path.Combine(AppPaths.ConfigDirectory, $"{normalizedName}.download");
        var finalTempPath = Path.Combine(AppPaths.ConfigDirectory, $"{Path.GetFileNameWithoutExtension(normalizedName)}.validated{Path.GetExtension(normalizedName)}");

        try
        {
            await using (var input = await response.Content.ReadAsStreamAsync(cancellationToken))
            await using (var output = File.Create(tempPath))
            {
                var buffer = new byte[64 * 1024];
                var total = 0;
                while (true)
                {
                    var read = await input.ReadAsync(buffer, cancellationToken);
                    if (read == 0)
                    {
                        break;
                    }

                    total += read;
                    if (total > MaxConfigBytes)
                    {
                        throw new InvalidOperationException($"配置文件超过大小限制 {MaxConfigBytes} bytes。");
                    }

                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                }
            }

            if (new FileInfo(tempPath).Length == 0)
            {
                File.Delete(tempPath);
                throw new InvalidOperationException("远程配置响应为空。");
            }
        }
        catch
        {
            DeleteIfExists(tempPath);
            throw;
        }

        if (File.Exists(finalTempPath))
        {
            File.Delete(finalTempPath);
        }

        File.Move(tempPath, finalTempPath);
        return finalTempPath;
    }

    public void Dispose() => _client.Dispose();

    public static void PromoteValidatedConfig(string validatedPath)
    {
        File.Copy(validatedPath, AppPaths.ConfigPath, overwrite: true);
        File.Delete(validatedPath);
    }

    private static string NormalizeConfigFileName(string? input, string fallback)
    {
        var candidate = string.IsNullOrWhiteSpace(input) ? fallback : input.Trim();
        candidate = Path.GetFileName(candidate);
        if (string.IsNullOrWhiteSpace(candidate) || candidate is "." or "..")
        {
            throw new ArgumentException("配置文件名无效。");
        }

        var extension = Path.GetExtension(candidate).ToLowerInvariant();
        if (string.IsNullOrEmpty(extension))
        {
            return candidate + ".yaml";
        }

        if (extension is not (".yaml" or ".yml"))
        {
            throw new ArgumentException("配置文件名必须以 .yaml 或 .yml 结尾。");
        }

        return candidate;
    }

    private static string InferFileName(Uri uri)
    {
        var name = Path.GetFileName(uri.LocalPath);
        if (string.IsNullOrWhiteSpace(name))
        {
            return "remote-config.yaml";
        }

        var extension = Path.GetExtension(name).ToLowerInvariant();
        return extension is ".yaml" or ".yml"
            ? name
            : $"{Path.GetFileNameWithoutExtension(name)}.yaml";
    }

    private static void DeleteIfExists(string path)
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
            DiagnosticLog.WriteApp(
                "REMOTE-CONFIG",
                "WARN",
                $"删除远程配置临时文件失败；路径={path}；{ex.Message}");
        }
    }
}
