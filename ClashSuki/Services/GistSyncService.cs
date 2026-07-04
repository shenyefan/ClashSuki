using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ClashSuki.Services;

public sealed class GistSyncService
{
    public sealed record AgeKeyPair(string SecretKey, string Recipient);

    private static readonly HttpClient Http = new();

    public async Task<string> SyncRuntimeConfigAsync(AppSettings settings, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(settings.GitHubToken))
        {
            throw new InvalidOperationException("请先填写 GitHub Token。");
        }

        var path = File.Exists(AppPaths.RuntimeConfigPath)
            ? AppPaths.RuntimeConfigPath
            : AppPaths.BaseConfigPath;
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("运行时配置文件不存在。", path);
        }

        var content = await File.ReadAllTextAsync(path, cancellationToken);
        if (settings.GistAgeEncrypt)
        {
            if (string.IsNullOrWhiteSpace(settings.GistAgeRecipient))
            {
                throw new InvalidOperationException("启用 Age 加密时必须填写接收方公钥。");
            }

            content = await EncryptWithAgeAsync(content, settings.GistAgeRecipient, cancellationToken);
        }

        var fileName = settings.GistAgeEncrypt ? "mihomo-runtime.yaml.age" : "mihomo-runtime.yaml";
        return string.IsNullOrWhiteSpace(settings.GistId)
            ? await CreateGistAsync(settings.GitHubToken, fileName, content, cancellationToken)
            : await UpdateGistAsync(settings.GitHubToken, settings.GistId, fileName, content, cancellationToken);
    }

    public async Task<AgeKeyPair> GenerateAgeKeyPairAsync(CancellationToken cancellationToken)
    {
        var keygen = ResolveAgeTool("age-keygen.exe") ?? ResolveAgeTool("age.exe")
                     ?? throw new FileNotFoundException("未找到 Age 工具。");
        var startInfo = new ProcessStartInfo
        {
            FileName = keygen,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        if (Path.GetFileName(keygen).Equals("age.exe", StringComparison.OrdinalIgnoreCase))
        {
            startInfo.ArgumentList.Add("--generate-key");
        }

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Age 密钥生成启动失败。");
        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        var output = $"{await outputTask}{Environment.NewLine}{await errorTask}".Trim();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(output);
        }

        var lines = output.ReplaceLineEndings("\n")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var secret = lines
            .Select(line => line.Trim())
            .FirstOrDefault(line => line.StartsWith("AGE-SECRET-KEY-", StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException("Age 工具未返回私钥。");
        var publicLine = lines
            .Select(line => line.Trim().TrimStart('#').Trim())
            .FirstOrDefault(line => line.StartsWith("public key:", StringComparison.OrdinalIgnoreCase));
        var separator = publicLine?.IndexOf(':') ?? -1;
        var recipient = separator >= 0 ? publicLine![(separator + 1)..].Trim() : null;
        if (string.IsNullOrWhiteSpace(recipient))
        {
            throw new InvalidOperationException("Age 工具未返回接收方公钥。");
        }

        return new AgeKeyPair(secret, recipient);
    }

    private static async Task<string> EncryptWithAgeAsync(string content, string recipient, CancellationToken cancellationToken)
    {
        var age = ResolveAgeTool("age.exe") ?? throw new FileNotFoundException("未找到 age.exe。");
        var tempIn = Path.Combine(Path.GetTempPath(), $"clashsuki-age-{Guid.NewGuid():N}.yaml");
        var tempOut = tempIn + ".age";
        await File.WriteAllTextAsync(tempIn, content, cancellationToken);
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = age,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            startInfo.ArgumentList.Add("-a");
            startInfo.ArgumentList.Add("-r");
            startInfo.ArgumentList.Add(recipient.Trim());
            startInfo.ArgumentList.Add("-o");
            startInfo.ArgumentList.Add(tempOut);
            startInfo.ArgumentList.Add(tempIn);

            using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Age 加密启动失败。");
            var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException((await errorTask).Trim());
            }

            return await File.ReadAllTextAsync(tempOut, cancellationToken);
        }
        finally
        {
            TryDelete(tempIn);
            TryDelete(tempOut);
        }
    }

    private static async Task<string> CreateGistAsync(string token, string fileName, string content, CancellationToken cancellationToken)
    {
        var body = new GistRequest("ClashSuki runtime config", false, new Dictionary<string, GistFile>
        {
            [fileName] = new(content)
        });
        using var response = await SendGitHubAsync(HttpMethod.Post, "https://api.github.com/gists", token, body, cancellationToken);
        var result = await JsonSerializer.DeserializeAsync<GistResponse>(
            await response.Content.ReadAsStreamAsync(cancellationToken),
            cancellationToken: cancellationToken);
        return result?.Id ?? throw new InvalidOperationException("GitHub 未返回 Gist ID。");
    }

    private static async Task<string> UpdateGistAsync(string token, string gistId, string fileName, string content, CancellationToken cancellationToken)
    {
        var body = new { files = new Dictionary<string, GistFile> { [fileName] = new(content) } };
        using var response = await SendGitHubAsync(HttpMethod.Patch, $"https://api.github.com/gists/{gistId.Trim()}", token, body, cancellationToken);
        var result = await JsonSerializer.DeserializeAsync<GistResponse>(
            await response.Content.ReadAsStreamAsync(cancellationToken),
            cancellationToken: cancellationToken);
        return result?.Id ?? gistId.Trim();
    }

    private static async Task<HttpResponseMessage> SendGitHubAsync(HttpMethod method, string url, string token, object body, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, url);
        request.Headers.UserAgent.ParseAdd("ClashSuki/1.0");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Trim());
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        var response = await Http.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        return response;
    }

    private static string? ResolveAgeTool(string fileName)
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "Assets", "Age", fileName),
            Path.Combine(AppContext.BaseDirectory, fileName)
        };
        return candidates.FirstOrDefault(File.Exists);
    }

    private static void TryDelete(string path)
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
                LogSources.Gist,
                ex,
                $"删除 Gist 同步临时文件失败，路径: {path}",
                "WARN");
        }
    }

    private sealed record GistRequest(
        [property: JsonPropertyName("description")] string Description,
        [property: JsonPropertyName("public")] bool Public,
        [property: JsonPropertyName("files")] Dictionary<string, GistFile> Files);

    private sealed record GistFile([property: JsonPropertyName("content")] string Content);

    private sealed class GistResponse
    {
        [JsonPropertyName("id")]
        public string? Id { get; set; }
    }
}
