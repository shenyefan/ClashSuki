using System.Net.Http;

namespace ClashSuki.Services;

public sealed record RemoteFetchRequest(
    string? UserAgent = null,
    string? AuthToken = null,
    int? TimeoutSeconds = null);

public sealed class RemoteResourceFetchService : IDisposable
{
    private const string DefaultUserAgent = "clash.meta";

    private readonly HttpClient _directClient = new(
        new HttpClientHandler { UseProxy = false },
        disposeHandler: true)
    {
        Timeout = TimeSpan.FromSeconds(30)
    };

    public void Dispose() => _directClient.Dispose();

    public async Task<string> FetchAsync(
        string url,
        RemoteFetchRequest request,
        int? mixedPort,
        Action<string, string>? log = null,
        CancellationToken cancellationToken = default)
    {
        if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("URL 必须以 http:// 或 https:// 开头。");
        }

        var settings = await AppSettingsService.LoadAsync(cancellationToken);
        var urls = BuildDownloadUrls(url, settings);
        Exception? lastError = null;

        foreach (var attemptUrl in urls)
        {
            try
            {
                return await TryDownloadWithFallbackAsync(
                    attemptUrl,
                    request,
                    mixedPort,
                    settings,
                    log,
                    cancellationToken);
            }
            catch (Exception ex) when (attemptUrl != urls[^1])
            {
                lastError = ex;
                log?.Invoke("WARN", $"GitHub 下载代理失败，正在尝试原始地址：{ex.Message}");
            }
        }

        throw lastError ?? new InvalidOperationException("远程下载失败。");
    }

    private async Task<string> TryDownloadWithFallbackAsync(
        string url,
        RemoteFetchRequest request,
        int? mixedPort,
        AppSettings settings,
        Action<string, string>? log,
        CancellationToken cancellationToken)
    {
        var timeoutSeconds = request.TimeoutSeconds is > 0
            ? request.TimeoutSeconds.Value
            : Math.Max(1, settings.SubscriptionTimeout);
        var timeout = TimeSpan.FromSeconds(timeoutSeconds);
        var userAgent = EffectiveUserAgent(request.UserAgent, settings);
        var useProxy = settings.ProfileUseProxy;

        log?.Invoke("INFO", $"远程资源下载地址；主机={FormatUrlHostForLog(url)}");
        log?.Invoke("INFO", $"远程资源下载参数；User-Agent={userAgent}");

        if (!useProxy)
        {
            log?.Invoke("INFO", "正在直连下载远程资源。");
            try
            {
                var (content, _) = await FetchWithClientAsync(
                    _directClient,
                    url,
                    userAgent,
                    request.AuthToken,
                    timeout,
                    cancellationToken);
                log?.Invoke("INFO", $"远程资源直连下载成功；内容大小={content.Length:N0} 字节");
                return content;
            }
            catch (Exception ex) when (mixedPort.HasValue)
            {
                log?.Invoke("WARN", $"远程资源直连下载失败，正在通过本地代理重试；端口={mixedPort}；{ex.Message}");
            }
            catch (Exception ex)
            {
                log?.Invoke("ERROR", $"远程资源下载失败，没有可用的代理回退：{ex.Message}");
                throw;
            }
        }

        if (!mixedPort.HasValue)
        {
            throw new InvalidOperationException("未获取到 mixed-port，无法通过代理下载。");
        }

        using var proxyHandler = new HttpClientHandler
        {
            Proxy = new System.Net.WebProxy($"http://127.0.0.1:{mixedPort}"),
            UseProxy = true
        };
        using var proxyClient = new HttpClient(proxyHandler, disposeHandler: false)
        {
            Timeout = timeout
        };
        var (proxyContent, _) = await FetchWithClientAsync(
            proxyClient,
            url,
            userAgent,
            request.AuthToken,
            timeout,
            cancellationToken);
        log?.Invoke("INFO", $"远程资源代理下载成功；内容大小={proxyContent.Length:N0} 字节");
        return proxyContent;
    }

    private static string[] BuildDownloadUrls(string url, AppSettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.GitHubProxy) ||
            !url.Contains("github.com", StringComparison.OrdinalIgnoreCase))
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

    private static string EffectiveUserAgent(string? userAgent, AppSettings settings) =>
        !string.IsNullOrWhiteSpace(userAgent)
            ? userAgent.Trim()
            : string.IsNullOrWhiteSpace(settings.UserAgent)
                ? DefaultUserAgent
                : settings.UserAgent.Trim();

    private static string FormatUrlHostForLog(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri)
            ? uri.GetComponents(UriComponents.SchemeAndServer, UriFormat.Unescaped)
            : "无效地址";

    private static async Task<(string Content, Dictionary<string, string> Headers)> FetchWithClientAsync(
        HttpClient client,
        string url,
        string userAgent,
        string? authToken,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("User-Agent", userAgent);
        request.Headers.TryAddWithoutValidation("Accept", "*/*");
        request.Headers.TryAddWithoutValidation("Accept-Encoding", "identity");
        if (!string.IsNullOrWhiteSpace(authToken))
        {
            request.Headers.TryAddWithoutValidation("Authorization", authToken);
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);
        using var response = await client.SendAsync(request, timeoutCts.Token);
        response.EnsureSuccessStatusCode();

        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, values) in response.Headers)
        {
            headers[key] = string.Join(", ", values);
        }

        foreach (var (key, values) in response.Content.Headers)
        {
            headers[key] = string.Join(", ", values);
        }

        return (content, headers);
    }
}
