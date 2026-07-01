using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ClashSuki.Services;
using ClashSuki.Stores;

namespace ClashSuki.ViewModels;

public sealed partial class MainViewModel : ObservableObject
{
    private readonly AppCoordinator _coordinator;

    public MainViewModel(AppCoordinator coordinator)
    {
        _coordinator = coordinator;
        Runtime = coordinator.Runtime;
        Proxies = coordinator.Proxies;
        Connections = coordinator.Connections;
        Rules = coordinator.Rules;
        Profiles = coordinator.Profiles;
        Logs = coordinator.Logs;

        Dashboard = new DashboardViewModel(coordinator);
        ProxiesVm = new ProxiesViewModel(coordinator);
        ConnectionsVm = new ConnectionsViewModel(coordinator);
        RulesVm = new RulesViewModel(coordinator);
        ProfilesVm = new ProfilesViewModel(coordinator);
        LogsVm = new LogsViewModel(coordinator);
        Resources = new ResourcesViewModel(coordinator);
        CoreSettings = new CoreSettingsViewModel(coordinator);
        SettingsVm = new SettingsViewModel(coordinator);
        DnsVm = new DnsViewModel(coordinator);
        TunVm = new TunViewModel(coordinator);
        SnifferVm = new SnifferViewModel(coordinator);
        OverrideVm = new OverrideViewModel(coordinator);
    }

    public RuntimeStore Runtime { get; }
    public ProxyStore Proxies { get; }
    public ConnectionStore Connections { get; }
    public RuleStore Rules { get; }
    public ProfileStore Profiles { get; }
    public LogStore Logs { get; }

    public DashboardViewModel Dashboard { get; }
    public ProxiesViewModel ProxiesVm { get; }
    public ConnectionsViewModel ConnectionsVm { get; }
    public RulesViewModel RulesVm { get; }
    public ProfilesViewModel ProfilesVm { get; }
    public LogsViewModel LogsVm { get; }
    public ResourcesViewModel Resources { get; }
    public CoreSettingsViewModel CoreSettings { get; }
    public SettingsViewModel SettingsVm { get; }
    public DnsViewModel DnsVm { get; }
    public TunViewModel TunVm { get; }
    public SnifferViewModel SnifferVm { get; }
    public OverrideViewModel OverrideVm { get; }

    [RelayCommand]
    private async Task StartAsync() => await _coordinator.StartAsync();

    [RelayCommand]
    private async Task RefreshAsync() => await _coordinator.RefreshNowAsync();
}
