using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ClashSuki.Services;
using ClashSuki.Stores;

namespace ClashSuki.ViewModels;

public sealed partial class SnifferViewModel : ObservableObject
{
    private readonly AppCoordinator _coordinator;

    [ObservableProperty] private bool snifferEnable = true;
    [ObservableProperty] private bool overrideDestination;
    [ObservableProperty] private bool forceDnsMapping = true;
    [ObservableProperty] private bool parsePureIp;
    [ObservableProperty] private string httpPorts = "80";
    [ObservableProperty] private string tlsPorts = "443";
    [ObservableProperty] private string quicPorts = "443";
    [ObservableProperty] private string skipDomain = "";
    [ObservableProperty] private string forceDomain = "";
    [ObservableProperty] private string skipDstAddress = "";
    [ObservableProperty] private string skipSrcAddress = "";

    public SnifferViewModel(AppCoordinator coordinator)
    {
        _coordinator = coordinator;
        Runtime = coordinator.Runtime;
    }

    public RuntimeStore Runtime { get; }

    public async Task LoadAsync()
    {
        var settings = await _coordinator.LoadSnifferSettingsAsync();
        SnifferEnable = settings.Enable;
        OverrideDestination = settings.OverrideDestination;
        ForceDnsMapping = settings.ForceDnsMapping;
        ParsePureIp = settings.ParsePureIp;
        HttpPorts = settings.HttpPorts;
        TlsPorts = settings.TlsPorts;
        QuicPorts = settings.QuicPorts;
        SkipDomain = settings.SkipDomain;
        ForceDomain = settings.ForceDomain;
        SkipDstAddress = settings.SkipDstAddress;
        SkipSrcAddress = settings.SkipSrcAddress;
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        try
        {
            var sniffer = new Dictionary<string, object?>
            {
                ["enable"] = SnifferEnable,
                ["override-destination"] = OverrideDestination,
                ["force-dns-mapping"] = ForceDnsMapping,
                ["parse-pure-ip"] = ParsePureIp,
                ["sniff"] = new Dictionary<string, object?>
                {
                    ["HTTP"] = new Dictionary<string, object?> { ["ports"] = SplitPorts(HttpPorts) },
                    ["TLS"] = new Dictionary<string, object?> { ["ports"] = SplitPorts(TlsPorts) },
                    ["QUIC"] = new Dictionary<string, object?> { ["ports"] = SplitPorts(QuicPorts) }
                },
                ["skip-domain"] = SplitLines(SkipDomain),
                ["force-domain"] = SplitLines(ForceDomain),
                ["skip-dst-address"] = SplitLines(SkipDstAddress),
                ["skip-src-address"] = SplitLines(SkipSrcAddress)
            };
            await _coordinator.SaveSnifferSettingsAsync(new Dictionary<string, object?> { ["sniffer"] = sniffer });
            Runtime.Notifications.Success(
                "嗅探配置已保存并重载。",
                source: LogSources.Sniffer);
        }
        catch (Exception ex)
        {
            Runtime.Notifications.Error(
                $"嗅探配置保存失败：{ex.Message}",
                source: LogSources.Sniffer,
                exception: ex);
            await LoadAsync();
        }
    }

    [RelayCommand]
    public void ApplyDefaults()
    {
        SnifferEnable = true;
        OverrideDestination = false;
        ForceDnsMapping = true;
        ParsePureIp = false;
        HttpPorts = "80";
        TlsPorts = "443";
        QuicPorts = "443";
        SkipDomain = "";
        ForceDomain = "";
        SkipDstAddress = "";
        SkipSrcAddress = "";
    }

    [RelayCommand]
    public async Task ResetAndSaveAsync()
    {
        ApplyDefaults();
        await SaveAsync();
    }

    private static string[] SplitLines(string text) =>
        text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static string[] SplitPorts(string text) =>
        text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
