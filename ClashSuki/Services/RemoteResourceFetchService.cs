using System.Net.Http;

namespace ClashSuki.Services;

public sealed record RemoteFetchRequest(
    string? UserAgent = null,
    string? AuthToken = null,
    int? TimeoutSeconds = null)
{
    public static RemoteFetchRequest Create(
        string? userAgent,
        string? authToken,
        int? timeoutSeconds = null) =>
        new(
            string.IsNullOrWhiteSpace(userAgent) ? null : userAgent.Trim(),
            string.IsNullOrWhiteSpace(authToken) ? null : authToken.Trim(),
            timeoutSeconds);
}

public sealed record RemoteFetchResult(
    string Content,
    IReadOnlyDictionary<string, string> Headers);

public sealed class RemoteResourceFetchService : IDisposable
{
    private const string DefaultUserAgent = "clash.meta";

    private readonly HttpClient _directClient = new(
        new HttpClientHandler { UseProxy = false },
        disposeHandler: true)
    {
        Timeout = Timeout.InfiniteTimeSpan
    };

    public void Dispose() => _directClient.Dispose();

    public async Task<string> FetchAsync(
        string url,
        RemoteFetchRequest request,
        int? mixedPort,
        CancellationToken cancellationToken = default)
    {
        var result = await FetchWithHeadersAsync(url, request, mixedPort, cancellationToken);
        return result.Content;
    }

    public async Task<RemoteFetchResult> FetchWithHeadersAsync(
        string url,
        RemoteFetchRequest request,
        int? mixedPort,
        CancellationToken cancellationToken = default)
    {
        if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("URL 必须以 http:// 或 https:// 开头。");
        }

        var settings = await AppSettingsService.LoadAsync(cancellationToken);
        var urls = GitHubProxyUrlService.BuildCandidates(url, settings);
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
                    cancellationToken);
            }
            catch (Exception ex) when (attemptUrl != urls[^1])
            {
                lastError = ex;
            }
        }

        throw lastError ?? new InvalidOperationException("远程下载失败。");
    }

    private async Task<RemoteFetchResult> TryDownloadWithFallbackAsync(
        string url,
        RemoteFetchRequest request,
        int? mixedPort,
        AppSettings settings,
        CancellationToken cancellationToken)
    {
        var timeoutSeconds = request.TimeoutSeconds is > 0
            ? request.TimeoutSeconds.Value
            : Math.Max(1, settings.SubscriptionTimeout);
        var timeout = TimeSpan.FromSeconds(timeoutSeconds);
        var userAgent = EffectiveUserAgent(request.UserAgent, settings);
        var useProxy = settings.ProfileUseProxy;

        if (!useProxy)
        {
            try
            {
                return await FetchWithClientAsync(
                    _directClient,
                    url,
                    userAgent,
                    request.AuthToken,
                    timeout,
                    cancellationToken);
            }
            catch (Exception ex) when (
                ex is not OperationCanceledException &&
                mixedPort.HasValue)
            {
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
        return await FetchWithClientAsync(
            proxyClient,
            url,
            userAgent,
            request.AuthToken,
            timeout,
            cancellationToken);
    }

    private static string EffectiveUserAgent(string? userAgent, AppSettings settings) =>
        !string.IsNullOrWhiteSpace(userAgent)
            ? userAgent.Trim()
            : string.IsNullOrWhiteSpace(settings.UserAgent)
                ? DefaultUserAgent
                : settings.UserAgent.Trim();

    private static async Task<RemoteFetchResult> FetchWithClientAsync(
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

        return new RemoteFetchResult(content, headers);
    }
}
