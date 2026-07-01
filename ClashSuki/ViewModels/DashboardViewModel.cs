using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ClashSuki.Services;
using ClashSuki.Stores;
using ClashSuki.Utilities;
using LiveChartsCore;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using SkiaSharp;
using Windows.ApplicationModel.DataTransfer;

namespace ClashSuki.ViewModels;

public sealed partial class TrafficRankingItem(string label) : ObservableObject
{
    public string Label { get; } = label;

    [ObservableProperty]
    private int rank;

    [ObservableProperty]
    private string total = "0 B";

    [ObservableProperty]
    private string upload = "0 B";

    [ObservableProperty]
    private string download = "0 B";

    public void Apply(int position, TrafficRanking source)
    {
        Rank = position;
        Total = Formatters.FormatBytes(source.Total);
        Upload = Formatters.FormatBytes(source.Upload);
        Download = Formatters.FormatBytes(source.Download);
    }
}

public sealed partial class DashboardViewModel : ObservableObject
{
    private readonly AppCoordinator _coordinator;
    private readonly TrafficStatisticsStore _trafficStatistics;
    private readonly ObservableCollection<double> _uploadSamples = [];
    private readonly ObservableCollection<double> _downloadSamples = [];

    [ObservableProperty]
    private string bypassList = WindowsSystemProxyService.FormatBypassListForDisplay(WindowsSystemProxyService.DefaultBypass);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPacMode))]
    private int systemProxyModeIndex;

    [ObservableProperty]
    private string systemProxyHost = "127.0.0.1";

    [ObservableProperty]
    private string pacScript = WindowsSystemProxyService.DefaultPacScript;

    public DashboardViewModel(AppCoordinator coordinator)
    {
        _coordinator = coordinator;
        Runtime = coordinator.Runtime;
        _trafficStatistics = coordinator.TrafficStatistics;

        TrafficSeries =
        [
            CreateSeries("上传", _uploadSamples, new SKColor(34, 197, 94)),
            CreateSeries("下载", _downloadSamples, new SKColor(59, 130, 246))
        ];
        TrafficXAxes =
        [
            new Axis
            {
                IsVisible = false
            }
        ];
        TrafficYAxes =
        [
            new Axis
            {
                MinLimit = 0,
                Labeler = value => Formatters.FormatBytes((long)Math.Max(0, value)),
                TextSize = 11,
                SeparatorsPaint = new SolidColorPaint(new SKColor(128, 128, 128, 28))
            }
        ];

        foreach (var sample in _trafficStatistics.Samples)
        {
            AppendTrafficSample(sample);
        }

        RefreshRankings();
        _trafficStatistics.TrafficSampled += TrafficStatistics_TrafficSampled;
        _trafficStatistics.RankingsChanged += TrafficStatistics_RankingsChanged;
    }

    public RuntimeStore Runtime { get; }
    public IReadOnlyList<ISeries> TrafficSeries { get; }
    public IReadOnlyList<Axis> TrafficXAxes { get; }
    public IReadOnlyList<Axis> TrafficYAxes { get; }
    public ObservableCollection<TrafficRankingItem> ProxyRankings { get; } = [];
    public ObservableCollection<TrafficRankingItem> DomainRankings { get; } = [];
    public bool HasProxyRankings => ProxyRankings.Count > 0;
    public bool HasDomainRankings => DomainRankings.Count > 0;

    public bool IsPacMode => SystemProxyModeIndex == 1;

    public async Task SetSystemProxyAsync(bool enabled) => await _coordinator.SetSystemProxyAsync(enabled);

    public async Task SetTunAsync(bool enabled) => await _coordinator.SetTunAsync(enabled);

    [RelayCommand]
    private async Task InstallServiceAsync() => await _coordinator.InstallServiceAsync();

    [RelayCommand]
    private async Task SwitchModeAsync(string? mode)
    {
        if (!string.IsNullOrWhiteSpace(mode))
        {
            await _coordinator.SwitchModeAsync(mode);
        }
    }

    public async Task CopyProxyEnvironmentAsync()
    {
        var settings = await AppSettingsService.LoadAsync();
        var text = ProxyEnvironmentService.Format(
            settings.EnvType,
            settings.SystemProxyHost,
            Runtime.MixedPortNumber);
        var package = new DataPackage();
        package.SetText(text);
        Clipboard.SetContent(package);
        Runtime.Notifications.Success(
            "代理环境变量已复制。",
            source: LogSources.SystemProxy);
    }

    public async Task LoadSystemProxySettingsAsync()
    {
        var settings = await AppSettingsService.LoadAsync();
        BypassList = WindowsSystemProxyService.FormatBypassListForDisplay(settings.SystemProxyBypass);
        SystemProxyHost = string.IsNullOrWhiteSpace(settings.SystemProxyHost) ? "127.0.0.1" : settings.SystemProxyHost;
        SystemProxyModeIndex = WindowsSystemProxyService.NormalizeMode(settings.SystemProxyMode) == "auto" ? 1 : 0;
        PacScript = WindowsSystemProxyService.NormalizePacScript(settings.SystemProxyPacScript);
    }

    [RelayCommand]
    private async Task SaveBypassAsync()
    {
        await SaveSystemProxySettingsAsync();
    }

    [RelayCommand]
    private async Task SaveSystemProxySettingsAsync()
    {
        var previous = await AppSettingsService.LoadAsync();
        try
        {
            var normalized = WindowsSystemProxyService.NormalizeBypassList(BypassList);
            var mode = SystemProxyModeIndex == 1 ? "auto" : "manual";
            var host = string.IsNullOrWhiteSpace(SystemProxyHost) ? "127.0.0.1" : SystemProxyHost.Trim();
            var pac = WindowsSystemProxyService.NormalizePacScript(PacScript);
            await AppSettingsService.PatchAsync(settings =>
            {
                settings.SystemProxyBypass = normalized;
                settings.SystemProxyMode = mode;
                settings.SystemProxyHost = host;
                settings.SystemProxyPacScript = pac;
            });
            BypassList = WindowsSystemProxyService.FormatBypassListForDisplay(normalized);
            SystemProxyHost = host;
            PacScript = pac;
            if (Runtime.IsSystemProxyEnabled)
            {
                await _coordinator.SetSystemProxyAsync(true);
                if (!Runtime.IsSystemProxyEnabled)
                {
                    throw new InvalidOperationException("新的系统代理设置未能应用。");
                }
            }

            Runtime.Notifications.Success(
                "系统代理设置已保存。",
                source: LogSources.SystemProxy);
        }
        catch (Exception ex)
        {
            await AppSettingsService.PatchAsync(settings =>
            {
                settings.SystemProxyBypass = previous.SystemProxyBypass;
                settings.SystemProxyMode = previous.SystemProxyMode;
                settings.SystemProxyHost = previous.SystemProxyHost;
                settings.SystemProxyPacScript = previous.SystemProxyPacScript;
            });
            if (previous.SystemProxyEnabled)
            {
                await _coordinator.SetSystemProxyAsync(true);
            }

            Runtime.Notifications.Error(
                $"系统代理设置保存失败：{ex.Message}",
                source: LogSources.SystemProxy,
                exception: ex);
            await LoadSystemProxySettingsAsync();
        }
    }

    [RelayCommand]
    private async Task ResetBypassAsync()
    {
        await ResetSystemProxySettingsAsync();
    }

    [RelayCommand]
    private async Task ResetSystemProxySettingsAsync()
    {
        BypassList = WindowsSystemProxyService.FormatBypassListForDisplay(WindowsSystemProxyService.DefaultBypass);
        SystemProxyHost = "127.0.0.1";
        SystemProxyModeIndex = 0;
        PacScript = WindowsSystemProxyService.DefaultPacScript;
        await SaveSystemProxySettingsAsync();
    }

    [RelayCommand]
    private async Task OpenUwpToolAsync()
    {
        try
        {
            await UwpLoopbackToolService.OpenAsync();
            Runtime.Notifications.Info("UWP 工具已打开。", source: LogSources.Tun);
        }
        catch (OperationCanceledException)
        {
            Runtime.Notifications.Info("已取消打开 UWP 工具。", source: LogSources.Tun);
        }
        catch (Exception ex)
        {
            Runtime.Notifications.Error(
                $"打开 UWP 工具失败：{ex.Message}",
                source: LogSources.Network,
                exception: ex);
        }
    }

    private static LineSeries<double> CreateSeries(
        string name,
        ObservableCollection<double> values,
        SKColor color)
    {
        return new LineSeries<double>
        {
            Name = name,
            Values = values,
            Stroke = new SolidColorPaint(color) { StrokeThickness = 2 },
            Fill = null,
            GeometryFill = null,
            GeometryStroke = null,
            GeometrySize = 0,
            LineSmoothness = 0.35
        };
    }

    private void TrafficStatistics_TrafficSampled(TrafficSample sample)
    {
        AppendTrafficSample(sample);
    }

    private void AppendTrafficSample(TrafficSample sample)
    {
        _uploadSamples.Add(sample.Upload);
        _downloadSamples.Add(sample.Download);
        while (_uploadSamples.Count > 60)
        {
            _uploadSamples.RemoveAt(0);
            _downloadSamples.RemoveAt(0);
        }
    }

    private void TrafficStatistics_RankingsChanged()
    {
        RefreshRankings();
    }

    private void RefreshRankings()
    {
        var hadProxyRankings = HasProxyRankings;
        var hadDomainRankings = HasDomainRankings;
        SynchronizeRankings(ProxyRankings, _trafficStatistics.ProxyRankings);
        SynchronizeRankings(DomainRankings, _trafficStatistics.DomainRankings);
        if (hadProxyRankings != HasProxyRankings)
        {
            OnPropertyChanged(nameof(HasProxyRankings));
        }

        if (hadDomainRankings != HasDomainRankings)
        {
            OnPropertyChanged(nameof(HasDomainRankings));
        }
    }

    private static void SynchronizeRankings(
        ObservableCollection<TrafficRankingItem> target,
        IReadOnlyList<TrafficRanking> source)
    {
        for (var index = 0; index < source.Count; index++)
        {
            var sourceItem = source[index];
            if (index >= target.Count)
            {
                target.Add(new TrafficRankingItem(sourceItem.Label));
            }
            else if (!target[index].Label.Equals(sourceItem.Label, StringComparison.OrdinalIgnoreCase))
            {
                var existingIndex = -1;
                for (var candidateIndex = index + 1; candidateIndex < target.Count; candidateIndex++)
                {
                    if (target[candidateIndex].Label.Equals(sourceItem.Label, StringComparison.OrdinalIgnoreCase))
                    {
                        existingIndex = candidateIndex;
                        break;
                    }
                }

                if (existingIndex >= 0)
                {
                    target.Move(existingIndex, index);
                }
                else
                {
                    target.Insert(index, new TrafficRankingItem(sourceItem.Label));
                }
            }

            target[index].Apply(index + 1, sourceItem);
        }

        while (target.Count > source.Count)
        {
            target.RemoveAt(target.Count - 1);
        }
    }
}
