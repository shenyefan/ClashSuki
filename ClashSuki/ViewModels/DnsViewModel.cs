using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ClashSuki.Services;
using ClashSuki.Stores;

namespace ClashSuki.ViewModels;

public sealed partial class DnsViewModel : ObservableObject
{
    private readonly AppCoordinator _coordinator;

    [ObservableProperty] private bool dnsEnable = true;
    [ObservableProperty] private int enhancedModeIndex;
    [ObservableProperty] private bool dnsIpv6;
    [ObservableProperty] private bool respectRules;
    [ObservableProperty] private bool useHosts;
    [ObservableProperty] private bool useSystemHosts = true;
    [ObservableProperty] private string fakeIpRange = "198.18.0.0/15";
    [ObservableProperty] private string fakeIpFilter = "*.lan\nlocalhost.ptlogin2.qq.com";
    [ObservableProperty] private int fakeIpFilterModeIndex;
    [ObservableProperty] private string nameserver = "114.114.114.114\n8.8.8.8";
    [ObservableProperty] private string fallback = "tls://1.1.1.1\ntls://8.8.4.4";
    [ObservableProperty] private string defaultNameserver = "114.114.114.114\n8.8.8.8";
    [ObservableProperty] private string directNameserver = "";
    [ObservableProperty] private string proxyServerNameserver = "";
    [ObservableProperty] private bool fallbackGeoIp = true;
    [ObservableProperty] private string fallbackGeoIpCode = "CN";
    [ObservableProperty] private string fallbackIpCidr = "";
    [ObservableProperty] private string fallbackDomain = "";
    [ObservableProperty] private string hosts = "";

    public DnsViewModel(AppCoordinator coordinator)
    {
        _coordinator = coordinator;
        Runtime = coordinator.Runtime;
    }

    public RuntimeStore Runtime { get; }

    public async Task LoadAsync()
    {
        var settings = await _coordinator.LoadDnsSettingsAsync();
        DnsEnable = settings.Enable;
        EnhancedModeIndex = settings.EnhancedMode.ToLowerInvariant() switch
        {
            "redir-host" => 1,
            "normal" => 2,
            _ => 0
        };
        DnsIpv6 = settings.Ipv6;
        RespectRules = settings.RespectRules;
        UseHosts = settings.UseHosts;
        UseSystemHosts = settings.UseSystemHosts;
        FakeIpRange = settings.FakeIpRange;
        FakeIpFilter = settings.FakeIpFilter;
        FakeIpFilterModeIndex = settings.FakeIpFilterMode.ToLowerInvariant() switch
        {
            "whitelist" => 1,
            "rule" => 2,
            _ => 0
        };
        Nameserver = settings.Nameserver;
        Fallback = settings.Fallback;
        DefaultNameserver = settings.DefaultNameserver;
        DirectNameserver = settings.DirectNameserver;
        ProxyServerNameserver = settings.ProxyServerNameserver;
        FallbackGeoIp = bool.TryParse(settings.FallbackGeoIp, out var geoIp) ? geoIp : true;
        FallbackGeoIpCode = settings.FallbackGeoIpCode;
        FallbackIpCidr = settings.FallbackIpCidr;
        FallbackDomain = settings.FallbackDomain;
        Hosts = settings.Hosts;
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        try
        {
            var patch = new Dictionary<string, object?>
            {
                ["dns"] = new Dictionary<string, object?>
                {
                    ["enable"] = DnsEnable,
                    ["enhanced-mode"] = EnhancedModeIndex switch { 1 => "redir-host", 2 => "normal", _ => "fake-ip" },
                    ["ipv6"] = DnsIpv6,
                    ["respect-rules"] = RespectRules,
                    ["use-hosts"] = UseHosts,
                    ["use-system-hosts"] = UseSystemHosts,
                    ["fake-ip-range"] = FakeIpRange,
                    ["fake-ip-filter"] = SplitLines(FakeIpFilter),
                    ["fake-ip-filter-mode"] = FakeIpFilterModeIndex switch { 1 => "whitelist", 2 => "rule", _ => "blacklist" },
                    ["nameserver"] = SplitLines(Nameserver),
                    ["fallback"] = SplitLines(Fallback),
                    ["default-nameserver"] = SplitLines(DefaultNameserver),
                    ["direct-nameserver"] = SplitLines(DirectNameserver),
                    ["proxy-server-nameserver"] = SplitLines(ProxyServerNameserver),
                    ["fallback-filter"] = new Dictionary<string, object?>
                    {
                        ["geoip"] = FallbackGeoIp,
                        ["geoip-code"] = FallbackGeoIpCode,
                        ["ipcidr"] = SplitLines(FallbackIpCidr),
                        ["domain"] = SplitLines(FallbackDomain)
                    }
                },
                ["hosts"] = ParseHosts(Hosts)
            };
            await _coordinator.SaveDnsSettingsAsync(patch);
            Runtime.Notifications.Success("DNS 配置已保存。", source: LogSources.Dns);
        }
        catch (Exception ex)
        {
            Runtime.Notifications.Error(
                $"DNS 配置保存失败：{ex.Message}",
                source: LogSources.Dns,
                exception: ex);
            await LoadAsync();
        }
    }

    [RelayCommand]
    public void ApplyDefaults()
    {
        DnsEnable = true;
        EnhancedModeIndex = 0;
        DnsIpv6 = false;
        RespectRules = false;
        UseHosts = false;
        UseSystemHosts = true;
        FakeIpRange = "198.18.0.0/15";
        FakeIpFilter = "*.lan\nlocalhost.ptlogin2.qq.com";
        FakeIpFilterModeIndex = 0;
        Nameserver = "114.114.114.114\n8.8.8.8";
        Fallback = "tls://1.1.1.1\ntls://8.8.4.4";
        DefaultNameserver = "114.114.114.114\n8.8.8.8";
        DirectNameserver = "";
        ProxyServerNameserver = "";
        FallbackGeoIp = true;
        FallbackGeoIpCode = "CN";
        FallbackIpCidr = "";
        FallbackDomain = "";
        Hosts = "";
    }

    [RelayCommand]
    public async Task ResetAndSaveAsync()
    {
        ApplyDefaults();
        await SaveAsync();
    }

    private static string[] SplitLines(string text) =>
        text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static Dictionary<string, object?> ParseHosts(string text)
    {
        var result = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in SplitLines(text))
        {
            var index = line.IndexOfAny(['=', ':']);
            if (index <= 0 || index >= line.Length - 1)
            {
                continue;
            }

            var key = line[..index].Trim();
            var value = line[(index + 1)..]
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            result[key] = value.Length > 1 ? value : value.FirstOrDefault() ?? "";
        }

        return result;
    }
}
