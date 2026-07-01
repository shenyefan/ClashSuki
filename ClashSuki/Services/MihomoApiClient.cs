using System.IO.Pipes;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using ClashSuki.Models;

namespace ClashSuki.Services;

/// <summary>
/// 本应用与 mihomo 内核的 API 客户端，始终通过命名管道通信。
/// 外部 HTTP 控制（external-controller）仅用于第三方工具 / WebUI，与本客户端无关。
/// </summary>
public sealed class MihomoApiClient : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(5);

    private readonly HttpClient _client = CreatePipeHttpClient();

    public string? Secret { get; private set; }

    public void Configure(string? secret)
    {
        Secret = string.IsNullOrWhiteSpace(secret) ? null : secret.Trim();
    }

    public void Dispose() => _client.Dispose();

    public Task<VersionInfo> GetVersionAsync(CancellationToken cancellationToken) =>
        GetAsync<VersionInfo>("/version", DefaultTimeout, cancellationToken);

    public Task<ConfigSnapshot> GetConfigsAsync(CancellationToken cancellationToken) =>
        GetAsync<ConfigSnapshot>("/configs", DefaultTimeout, cancellationToken);

    public Task<ConfigSnapshot> GetConfigsAsync(TimeSpan timeout, CancellationToken cancellationToken) =>
        GetAsync<ConfigSnapshot>("/configs", timeout, cancellationToken);

    public Task<ProxyGroupsResponse> GetProxiesAsync(CancellationToken cancellationToken) =>
        GetAsync<ProxyGroupsResponse>("/proxies", DefaultTimeout, cancellationToken);

    public Task<ProviderSummary> GetProxyProvidersAsync(CancellationToken cancellationToken) =>
        GetAsync<ProviderSummary>("/providers/proxies", DefaultTimeout, cancellationToken);

    public Task SwitchProxyAsync(string group, string target, CancellationToken cancellationToken) =>
        SendJsonAsync(HttpMethod.Put, $"/proxies/{Escape(group)}", new Dictionary<string, object?> { ["name"] = target }, DefaultTimeout, cancellationToken);

    public Task UnfixProxyAsync(string group, CancellationToken cancellationToken) =>
        SendJsonAsync(HttpMethod.Delete, $"/proxies/{Escape(group)}", null, DefaultTimeout, cancellationToken);

    public async Task<int?> TestProxyDelayAsync(string proxyName, string url, int timeoutMs, CancellationToken cancellationToken)
    {
        var path = $"/proxies/{Escape(proxyName)}/delay?url={Uri.EscapeDataString(url)}&timeout={timeoutMs}";
        var reqTimeout = TimeSpan.FromMilliseconds(timeoutMs + 5000);
        try
        {
            var result = await GetAsync<DelayResult>(path, reqTimeout, cancellationToken);
            return result.Delay ?? result.MeanDelay;
        }
        catch
        {
            return null;
        }
    }

    public Task TestGroupDelayAsync(string group, string url, int timeoutMs, CancellationToken cancellationToken)
    {
        var path = $"/group/{Escape(group)}/delay?url={Uri.EscapeDataString(url)}&timeout={timeoutMs}";
        var reqTimeout = TimeSpan.FromMilliseconds(timeoutMs + 10000);
        return GetRawAsync(path, reqTimeout, cancellationToken);
    }

    public Task UpdateProxyProviderAsync(string provider, CancellationToken cancellationToken) =>
        SendJsonAsync(HttpMethod.Put, $"/providers/proxies/{Escape(provider)}", null, TimeSpan.FromSeconds(30), cancellationToken);

    public Task HealthCheckProviderAsync(string provider, CancellationToken cancellationToken) =>
        GetRawAsync($"/providers/proxies/{Escape(provider)}/healthcheck", TimeSpan.FromSeconds(30), cancellationToken);

    public Task CloseAllConnectionsAsync(CancellationToken cancellationToken) =>
        SendJsonAsync(HttpMethod.Delete, "/connections", null, DefaultTimeout, cancellationToken);

    public Task CloseConnectionAsync(string id, CancellationToken cancellationToken) =>
        SendJsonAsync(HttpMethod.Delete, $"/connections/{Escape(id)}", null, DefaultTimeout, cancellationToken);

    public Task<RulesResponse> GetRulesAsync(CancellationToken cancellationToken) =>
        GetAsync<RulesResponse>("/rules", DefaultTimeout, cancellationToken);

    public Task DisableRulesAsync(IReadOnlyDictionary<int, bool> rules, CancellationToken cancellationToken) =>
        SendJsonAsync(
            HttpMethod.Patch,
            "/rules/disable",
            rules.ToDictionary(pair => pair.Key.ToString(System.Globalization.CultureInfo.InvariantCulture), pair => (object?)pair.Value),
            DefaultTimeout,
            cancellationToken);

    public Task<RuleProviderSummary> GetRuleProvidersAsync(CancellationToken cancellationToken) =>
        GetAsync<RuleProviderSummary>("/providers/rules", DefaultTimeout, cancellationToken);

    public Task UpdateRuleProviderAsync(string provider, CancellationToken cancellationToken) =>
        SendJsonAsync(HttpMethod.Put, $"/providers/rules/{Escape(provider)}", null, TimeSpan.FromSeconds(30), cancellationToken);

    public Task PatchTunAsync(bool enable, CancellationToken cancellationToken) =>
        SendJsonAsync(HttpMethod.Patch, "/configs", new Dictionary<string, object?>
        {
            ["tun"] = new Dictionary<string, object?> { ["enable"] = enable }
        }, DefaultTimeout, cancellationToken);

    public Task PatchConfigAsync(Dictionary<string, object?> patch, CancellationToken cancellationToken) =>
        SendJsonAsync(HttpMethod.Patch, "/configs", patch, DefaultTimeout, cancellationToken);

    public Task ReloadConfigAsync(string configPath, CancellationToken cancellationToken) =>
        SendJsonAsync(HttpMethod.Put, "/configs?force=true", new Dictionary<string, object?> { ["path"] = configPath }, TimeSpan.FromSeconds(10), cancellationToken);

    public Task RestartCoreAsync(CancellationToken cancellationToken) =>
        SendJsonAsync(HttpMethod.Post, "/restart", null, TimeSpan.FromSeconds(10), cancellationToken);

    public Task UpdateGeoAsync(CancellationToken cancellationToken) =>
        SendJsonAsync(HttpMethod.Post, "/configs/geo", null, TimeSpan.FromSeconds(60), cancellationToken);

    public Task UpgradeCoreAsync(CancellationToken cancellationToken) =>
        SendJsonAsync(HttpMethod.Post, "/upgrade", null, TimeSpan.FromSeconds(90), cancellationToken);

    public Task FlushFakeIpAsync(CancellationToken cancellationToken) =>
        SendJsonAsync(HttpMethod.Post, "/cache/fakeip/flush", null, DefaultTimeout, cancellationToken);

    private async Task<T> GetAsync<T>(string pathAndQuery, TimeSpan timeout, CancellationToken cancellationToken)
    {
        Exception? lastError = null;

        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                using var response = await SendAsync(HttpMethod.Get, pathAndQuery, null, timeout, cancellationToken);
                response.EnsureSuccessStatusCode();
                await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
                return await JsonSerializer.DeserializeAsync<T>(stream, JsonOptions, cancellationToken)
                       ?? throw new InvalidOperationException("mihomo API 返回了空响应。");
            }
            catch (Exception ex) when (attempt < 2 && IsTransient(ex))
            {
                lastError = ex;
                await Task.Delay(300, cancellationToken);
            }
        }

        throw lastError ?? new InvalidOperationException("mihomo API 请求失败。");
    }

    private async Task GetRawAsync(string pathAndQuery, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(HttpMethod.Get, pathAndQuery, null, timeout, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    private async Task SendJsonAsync(HttpMethod method, string pathAndQuery, object? body, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var content = body is null
            ? null
            : new StringContent(JsonSerializer.Serialize(body, JsonOptions), Encoding.UTF8, "application/json");
        using var response = await SendAsync(method, pathAndQuery, content, timeout, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    private async Task<HttpResponseMessage> SendAsync(
        HttpMethod method,
        string pathAndQuery,
        HttpContent? content,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);

        var request = new HttpRequestMessage(method, BuildUri(pathAndQuery))
        {
            Content = content
        };

        if (!string.IsNullOrWhiteSpace(Secret))
        {
            request.Headers.Authorization = new("Bearer", Secret);
        }

        try
        {
            return await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeoutCts.Token);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"mihomo API 请求超时；超时={timeout.TotalSeconds:0.#} 秒；方法={method}；路径={pathAndQuery}",
                ex);
        }
    }

    private static Uri BuildUri(string pathAndQuery) =>
        new(new Uri("http://127.0.0.1"), pathAndQuery);

    private static HttpClient CreatePipeHttpClient()
    {
        var handler = new SocketsHttpHandler
        {
            ConnectCallback = ConnectNamedPipeAsync,
            MaxConnectionsPerServer = 32,
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2)
        };

        return new HttpClient(handler, disposeHandler: true)
        {
            Timeout = TimeSpan.FromSeconds(15)
        };
    }

    private static async ValueTask<Stream> ConnectNamedPipeAsync(SocketsHttpConnectionContext _, CancellationToken cancellationToken)
    {
        var pipe = new NamedPipeClientStream(
            ".",
            MihomoControllerEndpoint.PipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous);
        await pipe.ConnectAsync(cancellationToken);
        return pipe;
    }

    private static string Escape(string value) => Uri.EscapeDataString(value);

    private static bool IsTransient(Exception ex) =>
        ex is HttpRequestException or TaskCanceledException or TimeoutException or IOException;
}
