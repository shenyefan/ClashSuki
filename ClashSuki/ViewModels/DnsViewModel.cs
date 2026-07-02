using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ClashSuki.Services;
using ClashSuki.Stores;
using ClashSuki.Utilities;

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
        FakeIpFilter = ConfigTextCodec.FormatLines(settings.FakeIpFilter);
        FakeIpFilterModeIndex = settings.FakeIpFilterMode.ToLowerInvariant() switch
        {
            "whitelist" => 1,
            "rule" => 2,
            _ => 0
        };
        Nameserver = ConfigTextCodec.FormatLines(settings.Nameserver);
        Fallback = ConfigTextCodec.FormatLines(settings.Fallback);
        DefaultNameserver = ConfigTextCodec.FormatLines(settings.DefaultNameserver);
        DirectNameserver = ConfigTextCodec.FormatLines(settings.DirectNameserver);
        ProxyServerNameserver = ConfigTextCodec.FormatLines(settings.ProxyServerNameserver);
        FallbackGeoIp = settings.FallbackGeoIp;
        FallbackGeoIpCode = settings.FallbackGeoIpCode;
        FallbackIpCidr = ConfigTextCodec.FormatLines(settings.FallbackIpCidr);
        FallbackDomain = ConfigTextCodec.FormatLines(settings.FallbackDomain);
        Hosts = ConfigTextCodec.FormatMapping(settings.Hosts);
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        try
        {
            await _coordinator.SaveDnsSettingsAsync(new YamlConfigService.DnsSectionSettings(
                DnsEnable,
                EnhancedModeIndex switch { 1 => "redir-host", 2 => "normal", _ => "fake-ip" },
                DnsIpv6,
                RespectRules,
                UseHosts,
                UseSystemHosts,
                FakeIpRange.Trim(),
                ConfigTextCodec.ParseLines(FakeIpFilter),
                FakeIpFilterModeIndex switch { 1 => "whitelist", 2 => "rule", _ => "blacklist" },
                ConfigTextCodec.ParseLines(Nameserver),
                ConfigTextCodec.ParseLines(Fallback),
                ConfigTextCodec.ParseLines(DefaultNameserver),
                ConfigTextCodec.ParseLines(DirectNameserver),
                ConfigTextCodec.ParseLines(ProxyServerNameserver),
                FallbackGeoIp,
                FallbackGeoIpCode.Trim(),
                ConfigTextCodec.ParseLines(FallbackIpCidr),
                ConfigTextCodec.ParseLines(FallbackDomain),
                ConfigTextCodec.ParseMapping(Hosts)));
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

}
