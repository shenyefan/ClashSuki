using System.Runtime.InteropServices;
using Windows.Networking.Connectivity;

namespace ClashSuki.Services;

public static class WindowsNetworkEnvironmentService
{
    private const int AccessDeniedHResult = unchecked((int)0x80070005);

    public static Task<string?> GetCurrentWifiSsidAsync(CancellationToken cancellationToken) =>
        Task.Run(() => GetCurrentWifiSsid(cancellationToken), cancellationToken);

    private static string? GetCurrentWifiSsid(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            // The Internet profile is the most useful choice when more than one WLAN
            // interface is connected. It may be Ethernet or null, so fall back to all
            // profiles to cover a Wi-Fi connection with only local/no Internet access.
            var internetProfile = NetworkInformation.GetInternetConnectionProfile();
            var internetSsid = GetConnectedSsid(internetProfile);
            cancellationToken.ThrowIfCancellationRequested();
            if (internetSsid is not null)
            {
                return internetSsid;
            }

            cancellationToken.ThrowIfCancellationRequested();
            string? bestSsid = null;
            var bestConnectivityRank = -1;

            foreach (var profile in NetworkInformation.GetConnectionProfiles())
            {
                cancellationToken.ThrowIfCancellationRequested();
                var ssid = GetConnectedSsid(profile);
                if (ssid is null)
                {
                    continue;
                }

                var connectivityRank = GetConnectivityRank(profile.GetNetworkConnectivityLevel());
                if (bestSsid is null || connectivityRank > bestConnectivityRank)
                {
                    bestSsid = ssid;
                    bestConnectivityRank = connectivityRank;
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            return bestSsid;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
        catch (COMException ex) when (ex.HResult == AccessDeniedHResult)
        {
            return null;
        }
    }

    private static string? GetConnectedSsid(ConnectionProfile? profile)
    {
        var ssid = profile?.WlanConnectionProfileDetails?.GetConnectedSsid();
        return string.IsNullOrWhiteSpace(ssid) ? null : ssid.Trim();
    }

    private static int GetConnectivityRank(NetworkConnectivityLevel level) => level switch
    {
        NetworkConnectivityLevel.InternetAccess => 3,
        NetworkConnectivityLevel.ConstrainedInternetAccess => 2,
        NetworkConnectivityLevel.LocalAccess => 1,
        _ => 0
    };
}
