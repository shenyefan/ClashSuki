using System.Diagnostics;
using System.Text.RegularExpressions;

namespace ClashSuki.Services;

public static class WindowsNetworkEnvironmentService
{
    private static readonly Regex SsidRegex = new(@"^\s*SSID\s*:\s*(.+)$", RegexOptions.Compiled | RegexOptions.Multiline);

    public static async Task<string?> GetCurrentWifiSsidAsync(CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "netsh",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        startInfo.ArgumentList.Add("wlan");
        startInfo.ArgumentList.Add("show");
        startInfo.ArgumentList.Add("interfaces");

        using var process = Process.Start(startInfo);
        if (process is null)
        {
            return null;
        }

        var output = await process.StandardOutput.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        var match = SsidRegex.Match(output);
        return match.Success ? match.Groups[1].Value.Trim() : null;
    }
}
