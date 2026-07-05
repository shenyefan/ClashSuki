namespace ClashSuki.Services;

public static class GitHubProxyUrlService
{
    public static IReadOnlyList<string> BuildCandidates(string url, AppSettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.GitHubProxy) || !IsGitHubUrl(url))
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

    private static bool IsGitHubUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return false;
        }

        return uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) ||
               uri.Host.Equals("api.github.com", StringComparison.OrdinalIgnoreCase) ||
               uri.Host.EndsWith(".githubusercontent.com", StringComparison.OrdinalIgnoreCase);
    }
}
