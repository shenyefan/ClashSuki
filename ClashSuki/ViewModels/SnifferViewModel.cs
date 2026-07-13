using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ClashSuki.Services;
using ClashSuki.Stores;
using ClashSuki.Utilities;

namespace ClashSuki.ViewModels;

public sealed partial class SnifferViewModel : ObservableObject
{
    private readonly AppCoordinator _coordinator;

    [ObservableProperty] private bool snifferOverrideEnabled = true;
    [ObservableProperty] private bool snifferEnabled = true;
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
        SnifferOverrideEnabled = settings.OverrideEnabled;
        SnifferEnabled = settings.Enabled;
        OverrideDestination = settings.OverrideDestination;
        ForceDnsMapping = settings.ForceDnsMapping;
        ParsePureIp = settings.ParsePureIp;
        HttpPorts = ConfigTextCodec.FormatValues(settings.HttpPorts);
        TlsPorts = ConfigTextCodec.FormatValues(settings.TlsPorts);
        QuicPorts = ConfigTextCodec.FormatValues(settings.QuicPorts);
        SkipDomain = ConfigTextCodec.FormatLines(settings.SkipDomain);
        ForceDomain = ConfigTextCodec.FormatLines(settings.ForceDomain);
        SkipDstAddress = ConfigTextCodec.FormatLines(settings.SkipDstAddress);
        SkipSrcAddress = ConfigTextCodec.FormatLines(settings.SkipSrcAddress);
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        try
        {
            await _coordinator.SaveSnifferSettingsAsync(new YamlConfigService.SnifferSectionSettings(
                SnifferOverrideEnabled,
                SnifferEnabled,
                OverrideDestination,
                ForceDnsMapping,
                ParsePureIp,
                ConfigTextCodec.ParseValues(HttpPorts, ','),
                ConfigTextCodec.ParseValues(TlsPorts, ','),
                ConfigTextCodec.ParseValues(QuicPorts, ','),
                ConfigTextCodec.ParseLines(SkipDomain),
                ConfigTextCodec.ParseLines(ForceDomain),
                ConfigTextCodec.ParseLines(SkipDstAddress),
                ConfigTextCodec.ParseLines(SkipSrcAddress)));
            Runtime.Notifications.Success(
                "嗅探配置已保存并重载",
                source: LogSources.Sniffer);
        }
        catch (Exception ex)
        {
            Runtime.Notifications.Error(
                "嗅探配置保存失败",
                source: LogSources.Sniffer,
                exception: ex);
            await LoadAsync();
        }
    }

    [RelayCommand]
    public void ApplyDefaults()
    {
        SnifferOverrideEnabled = true;
        SnifferEnabled = true;
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

}
