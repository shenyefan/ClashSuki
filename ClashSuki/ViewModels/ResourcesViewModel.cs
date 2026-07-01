using System.Collections.ObjectModel;
using System.Collections.Specialized;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ClashSuki.Services;
using ClashSuki.Stores;
using ClashSuki.Utilities;

namespace ClashSuki.ViewModels;

public sealed partial class ResourcesViewModel : ObservableObject
{
    private readonly AppCoordinator _coordinator;
    private RuleProviderItemViewModel? _viewerProvider;
    private YamlConfigService.GeoDataSettings _savedGeoData = new("", "", "", "", false, false, 24);
    private IReadOnlyList<ViewerLineViewModel> _viewerAllLines = [];
    private bool _syncingGeoData;
    private bool _geoSavePending;
    private long _viewerLoadVersion;

    [ObservableProperty] private string searchText = "";
    [ObservableProperty] private string geoIpUrl = "";
    [ObservableProperty] private string geoSiteUrl = "";
    [ObservableProperty] private string mmdbUrl = "";
    [ObservableProperty] private string asnUrl = "";
    [ObservableProperty] private bool geoDataMode;
    [ObservableProperty] private bool geoAutoUpdate;
    [ObservableProperty] private double geoUpdateIntervalValue = 24;
    [ObservableProperty] private bool isLoadingGeoData;
    [ObservableProperty] private bool isSavingGeoData;
    [ObservableProperty] private bool isUpdatingGeoData;
    [ObservableProperty] private bool isUpdatingAll;
    [ObservableProperty] private bool isViewerOpen;
    [ObservableProperty] private bool isViewerLoading;
    [ObservableProperty] private string viewerTitle = "";
    [ObservableProperty] private string viewerSearchText = "";
    [ObservableProperty] private IReadOnlyList<ViewerLineViewModel> viewerLines = [];
    [ObservableProperty] private string viewerSource = "";
    [ObservableProperty] private string viewerFormat = "";

    public ResourcesViewModel(AppCoordinator coordinator)
    {
        _coordinator = coordinator;
        Runtime = coordinator.Runtime;
        Rules = coordinator.Rules;
        Rules.Providers.CollectionChanged += RuleProvidersChanged;
        ApplyFilter();
    }

    public RuntimeStore Runtime { get; }
    public RuleStore Rules { get; }
    public ObservableCollection<RuleProviderItemViewModel> FilteredRuleProviders { get; } = [];
    public int FilteredRuleProviderCount => FilteredRuleProviders.Count;
    public string EmptyText => string.IsNullOrWhiteSpace(SearchText) ? "暂无规则集合" : "没有匹配的规则集合";
    public bool IsGeoIpDirty => GeoIpUrl != _savedGeoData.GeoIpUrl;
    public bool IsGeoSiteDirty => GeoSiteUrl != _savedGeoData.GeoSiteUrl;
    public bool IsMmdbDirty => MmdbUrl != _savedGeoData.MmdbUrl;
    public bool IsAsnDirty => AsnUrl != _savedGeoData.AsnUrl;
    public string ViewerMatchText => string.IsNullOrWhiteSpace(ViewerSearchText)
        ? $"{ViewerLines.Count:N0} 行"
        : $"{ViewerLines.Count:N0} 个匹配";

    partial void OnSearchTextChanged(string value)
    {
        OnPropertyChanged(nameof(EmptyText));
        ApplyFilter();
    }

    partial void OnGeoIpUrlChanged(string value) => OnPropertyChanged(nameof(IsGeoIpDirty));
    partial void OnGeoSiteUrlChanged(string value) => OnPropertyChanged(nameof(IsGeoSiteDirty));
    partial void OnMmdbUrlChanged(string value) => OnPropertyChanged(nameof(IsMmdbDirty));
    partial void OnAsnUrlChanged(string value) => OnPropertyChanged(nameof(IsAsnDirty));
    partial void OnViewerSearchTextChanged(string value) => ApplyViewerSearch();
    partial void OnViewerLinesChanged(IReadOnlyList<ViewerLineViewModel> value) => OnPropertyChanged(nameof(ViewerMatchText));

    partial void OnGeoDataModeChanged(bool value)
    {
        if (!_syncingGeoData)
        {
            _ = SaveGeoDataAsync();
        }
    }

    partial void OnGeoAutoUpdateChanged(bool value)
    {
        if (!_syncingGeoData)
        {
            _ = SaveGeoDataAsync();
        }
    }

    partial void OnGeoUpdateIntervalValueChanged(double value)
    {
        if (!_syncingGeoData)
        {
            _ = SaveGeoDataAsync();
        }
    }

    [RelayCommand]
    public async Task LoadGeoDataAsync()
    {
        if (IsLoadingGeoData)
        {
            return;
        }

        IsLoadingGeoData = true;
        try
        {
            ApplyGeoData(await _coordinator.LoadGeoDataSettingsAsync());
        }
        catch (Exception ex)
        {
            Runtime.Notifications.Error(
                $"GeoData 配置读取失败：{ex.Message}",
                source: LogSources.Resource,
                exception: ex);
        }
        finally
        {
            IsLoadingGeoData = false;
        }
    }

    [RelayCommand]
    private Task SaveGeoIpAsync() => SaveGeoDataAsync();

    [RelayCommand]
    private Task SaveGeoSiteAsync() => SaveGeoDataAsync();

    [RelayCommand]
    private Task SaveMmdbAsync() => SaveGeoDataAsync();

    [RelayCommand]
    private Task SaveAsnAsync() => SaveGeoDataAsync();

    [RelayCommand]
    private async Task UpdateGeoDataAsync()
    {
        if (IsUpdatingGeoData)
        {
            return;
        }

        IsUpdatingGeoData = true;
        try
        {
            await _coordinator.UpdateGeoAsync();
            Runtime.Notifications.Success(
                "GeoData 已更新。",
                source: LogSources.Resource);
        }
        catch (Exception ex)
        {
            Runtime.Notifications.Error(
                $"GeoData 更新失败：{ex.Message}",
                source: LogSources.Resource,
                exception: ex);
        }
        finally
        {
            IsUpdatingGeoData = false;
        }
    }

    [RelayCommand]
    private async Task UpdateAllRuleProvidersAsync()
    {
        if (IsUpdatingAll)
        {
            return;
        }

        IsUpdatingAll = true;
        try
        {
            var failedCount = 0;
            foreach (var provider in FilteredRuleProviders.ToList())
            {
                if (!await UpdateRuleProviderCoreAsync(provider, showNotification: false))
                {
                    failedCount++;
                }
            }

            if (failedCount == 0)
            {
                Runtime.Notifications.Success(
                    "规则集合已全部更新。",
                    source: LogSources.Resource);
            }
            else
            {
                Runtime.Notifications.Warning(
                    $"规则集合更新完成，其中 {failedCount} 项失败。",
                    source: LogSources.Resource);
            }
        }
        finally
        {
            IsUpdatingAll = false;
        }
    }

    [RelayCommand]
    private async Task UpdateRuleProviderAsync(RuleProviderItemViewModel? provider)
    {
        await UpdateRuleProviderCoreAsync(provider, showNotification: true);
    }

    private async Task<bool> UpdateRuleProviderCoreAsync(
        RuleProviderItemViewModel? provider,
        bool showNotification)
    {
        if (provider is null || provider.IsUpdating || string.IsNullOrWhiteSpace(provider.Name))
        {
            return false;
        }

        provider.IsUpdating = true;
        try
        {
            await _coordinator.UpdateRuleProviderAsync(provider.Name);
            if (showNotification)
            {
                Runtime.Notifications.Success(
                    $"规则集合「{provider.Name}」已更新。",
                    source: LogSources.Resource);
            }
            return true;
        }
        catch (Exception ex)
        {
            if (showNotification)
            {
                Runtime.Notifications.Error(
                    $"规则集合更新失败：{ex.Message}",
                    source: LogSources.Resource,
                    exception: ex);
            }
            else
            {
                DiagnosticLog.WriteAppException(
                    LogSources.Resource,
                    ex,
                    $"更新规则集合失败；名称={provider.Name}");
            }
            return false;
        }
        finally
        {
            provider.IsUpdating = false;
        }
    }

    [RelayCommand]
    private async Task OpenRuleProviderAsync(RuleProviderItemViewModel? provider)
    {
        if (provider is null || provider.IsViewing || string.IsNullOrWhiteSpace(provider.Name))
        {
            return;
        }

        _viewerProvider = provider;
        var loadVersion = Interlocked.Increment(ref _viewerLoadVersion);
        provider.IsViewing = true;
        IsViewerLoading = true;
        ViewerTitle = provider.Name;
        ViewerSearchText = "";
        _viewerAllLines = [];
        ViewerLines = [];
        ViewerSource = "";
        ViewerFormat = provider.FormatText;

        try
        {
            var document = await _coordinator.OpenRuleProviderDocumentAsync(provider.Name);
            var lines = await Task.Run(() => SplitViewerLines(document.Content));
            if (loadVersion != _viewerLoadVersion || !ReferenceEquals(_viewerProvider, provider))
            {
                return;
            }

            ViewerTitle = document.Title;
            _viewerAllLines = lines;
            ViewerLines = lines;
            ViewerSource = document.SourcePath;
            ViewerFormat = document.Format;
            IsViewerOpen = true;
        }
        catch (Exception ex)
        {
            if (loadVersion != _viewerLoadVersion)
            {
                return;
            }

            Runtime.Notifications.Error(
                $"读取规则集合失败：{ex.Message}",
                source: LogSources.Resource,
                exception: ex);
            _viewerProvider = null;
        }
        finally
        {
            if (loadVersion == _viewerLoadVersion)
            {
                IsViewerLoading = false;
            }

            provider.IsViewing = false;
        }
    }

    [RelayCommand]
    private async Task OpenViewerSourceAsync() =>
        await _coordinator.OpenExternalFileAsync(ViewerSource, "规则文件");

    [RelayCommand]
    private void CloseViewer()
    {
        Interlocked.Increment(ref _viewerLoadVersion);
        IsViewerOpen = false;
        IsViewerLoading = false;
        ViewerTitle = "";
        ViewerSearchText = "";
        _viewerAllLines = [];
        ViewerLines = [];
        ViewerSource = "";
        ViewerFormat = "";
        _viewerProvider = null;
    }

    private void ApplyViewerSearch()
    {
        var query = ViewerSearchText.Trim();
        ViewerLines = string.IsNullOrWhiteSpace(query)
            ? _viewerAllLines
            : _viewerAllLines.Where(line => line.Text.Contains(query, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    private static IReadOnlyList<ViewerLineViewModel> SplitViewerLines(string content)
    {
        if (string.IsNullOrEmpty(content))
        {
            return [];
        }

        var lines = content.ReplaceLineEndings("\n").Split('\n');
        var result = new ViewerLineViewModel[lines.Length];
        for (var i = 0; i < lines.Length; i++)
        {
            result[i] = new ViewerLineViewModel(i + 1, lines[i]);
        }

        return result;
    }

    private void RuleProvidersChanged(object? sender, NotifyCollectionChangedEventArgs e) => ApplyFilter();

    private async Task SaveGeoDataAsync()
    {
        _geoSavePending = true;
        if (IsSavingGeoData)
        {
            return;
        }

        IsSavingGeoData = true;
        try
        {
            while (_geoSavePending)
            {
                _geoSavePending = false;
                var interval = double.IsNaN(GeoUpdateIntervalValue) || GeoUpdateIntervalValue <= 0
                    ? 24
                    : (int)Math.Round(GeoUpdateIntervalValue);
                if (interval <= 0)
                {
                    interval = 24;
                    GeoUpdateIntervalValue = 24;
                }

                var settings = new YamlConfigService.GeoDataSettings(
                    GeoIpUrl,
                    GeoSiteUrl,
                    MmdbUrl,
                    AsnUrl,
                    GeoDataMode,
                    GeoAutoUpdate,
                    interval);
                await _coordinator.SaveGeoDataSettingsAsync(settings);
                _savedGeoData = settings;
                OnPropertyChanged(nameof(IsGeoIpDirty));
                OnPropertyChanged(nameof(IsGeoSiteDirty));
                OnPropertyChanged(nameof(IsMmdbDirty));
                OnPropertyChanged(nameof(IsAsnDirty));
            }
        }
        catch (Exception ex)
        {
            _geoSavePending = false;
            Runtime.Notifications.Error(
                $"GeoData 配置保存失败：{ex.Message}",
                source: LogSources.Resource,
                exception: ex);
            ApplyGeoData(_savedGeoData);
        }
        finally
        {
            IsSavingGeoData = false;
        }
    }

    private void ApplyGeoData(YamlConfigService.GeoDataSettings settings)
    {
        _syncingGeoData = true;
        try
        {
            _savedGeoData = settings;
            GeoIpUrl = settings.GeoIpUrl;
            GeoSiteUrl = settings.GeoSiteUrl;
            MmdbUrl = settings.MmdbUrl;
            AsnUrl = settings.AsnUrl;
            GeoDataMode = settings.GeoDataMode;
            GeoAutoUpdate = settings.AutoUpdate;
            GeoUpdateIntervalValue = settings.UpdateInterval;
            OnPropertyChanged(nameof(IsGeoIpDirty));
            OnPropertyChanged(nameof(IsGeoSiteDirty));
            OnPropertyChanged(nameof(IsMmdbDirty));
            OnPropertyChanged(nameof(IsAsnDirty));
        }
        finally
        {
            _syncingGeoData = false;
        }
    }

    private void ApplyFilter()
    {
        var query = SearchText.Trim();
        var source = string.IsNullOrWhiteSpace(query)
            ? Rules.Providers.ToList()
            : Rules.Providers
                .Where(provider =>
                    provider.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                    provider.FormatText.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                    provider.ProviderKindText.Contains(query, StringComparison.OrdinalIgnoreCase))
                .ToList();

        CollectionSync.Sync(FilteredRuleProviders, source);
        OnPropertyChanged(nameof(FilteredRuleProviderCount));
    }
}

public sealed class ViewerLineViewModel(int number, string text)
{
    public int Number { get; } = number;
    public string Text { get; } = text;
    public string NumberText => Number.ToString(System.Globalization.CultureInfo.InvariantCulture);
}
