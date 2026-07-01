namespace ClashSuki.Services;

public static class ProxyEnvironmentService
{
    public static string Format(string? type, string host, int mixedPort)
    {
        if (mixedPort is <= 0 or > 65535)
        {
            throw new InvalidOperationException("混合端口不可用。");
        }

        var normalizedHost = string.IsNullOrWhiteSpace(host) ? "127.0.0.1" : host.Trim();
        var httpProxy = $"http://{normalizedHost}:{mixedPort}";
        var socksProxy = $"socks5://{normalizedHost}:{mixedPort}";
        return type?.Trim().ToLowerInvariant() switch
        {
            "cmd" => $"set HTTP_PROXY={httpProxy}\r\nset HTTPS_PROXY={httpProxy}\r\nset ALL_PROXY={socksProxy}",
            "bash" => $"export HTTP_PROXY=\"{httpProxy}\" HTTPS_PROXY=\"{httpProxy}\" ALL_PROXY=\"{socksProxy}\"",
            "fish" => $"set -x HTTP_PROXY {httpProxy}; set -x HTTPS_PROXY {httpProxy}; set -x ALL_PROXY {socksProxy}",
            "nushell" => $"load-env {{ HTTP_PROXY: \"{httpProxy}\", HTTPS_PROXY: \"{httpProxy}\", ALL_PROXY: \"{socksProxy}\" }}",
            _ => $"$env:HTTP_PROXY=\"{httpProxy}\"; $env:HTTPS_PROXY=\"{httpProxy}\"; $env:ALL_PROXY=\"{socksProxy}\""
        };
    }
}
