using System.Diagnostics;

namespace ClashSuki.ServiceContract;

internal static class CoreProcessSettings
{
    private static readonly string[] ProxyEnvironmentVariables =
    [
        "HTTP_PROXY", "HTTPS_PROXY", "ALL_PROXY", "NO_PROXY",
        "http_proxy", "https_proxy", "all_proxy", "no_proxy"
    ];

    public static void ClearProxyEnvironment(ProcessStartInfo startInfo)
    {
        foreach (var name in ProxyEnvironmentVariables)
        {
            startInfo.Environment.Remove(name);
        }
    }

    public static string NormalizePriority(string? value) =>
        value?.Trim().ToLowerInvariant() switch
        {
            "idle" => "idle",
            "below_normal" => "below_normal",
            "above_normal" => "above_normal",
            "high" => "high",
            "real_time" => "real_time",
            _ => "normal"
        };

    public static ProcessPriorityClass ParsePriority(string? value) =>
        NormalizePriority(value) switch
        {
            "idle" => ProcessPriorityClass.Idle,
            "below_normal" => ProcessPriorityClass.BelowNormal,
            "above_normal" => ProcessPriorityClass.AboveNormal,
            "high" => ProcessPriorityClass.High,
            "real_time" => ProcessPriorityClass.RealTime,
            _ => ProcessPriorityClass.Normal
        };
}
