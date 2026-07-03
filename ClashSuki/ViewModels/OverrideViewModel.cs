using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ClashSuki.Services;
using ClashSuki.Stores;

namespace ClashSuki.ViewModels;

public sealed partial class OverrideItemViewModel : ObservableObject
{
    public OverrideItemViewModel(OverrideEntry entry) => Entry = entry;

    [ObservableProperty] private bool isUpdating;

    public OverrideEntry Entry { get; private set; }
    public string Id => Entry.Id;
    public string Name { get => Entry.Name; set { Entry.Name = value; OnPropertyChanged(); } }
    public bool IsRemote => Entry.Type.Equals("remote", StringComparison.OrdinalIgnoreCase);
    public bool IsJavaScript => Entry.Ext.Equals("js", StringComparison.OrdinalIgnoreCase);
    public string TypeText => Entry.Type.Equals("remote", StringComparison.OrdinalIgnoreCase) ? "远程" : "本地";
    public string ExtText => Entry.Ext.ToUpperInvariant();
    public string DetailText
    {
        get
        {
            var parts = new List<string>
            {
                TypeText,
                ExtText,
                Entry.UpdatedAt.ToString("yyyy-MM-dd HH:mm")
            };
            if (IsRemote && Entry.AutoUpdate)
            {
                var interval = Entry.Interval is > 0 ? Entry.Interval.Value : 1440;
                parts.Add($"自动更新 {interval} 分钟");
            }

            return string.Join(" · ", parts);
        }
    }

    public string Url => Entry.Url ?? "";

    public void RefreshDetail()
    {
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(Url));
        OnPropertyChanged(nameof(DetailText));
    }

    public void Apply(OverrideEntry entry)
    {
        Entry = entry;
        OnPropertyChanged(nameof(Id));
        OnPropertyChanged(nameof(IsRemote));
        OnPropertyChanged(nameof(IsJavaScript));
        OnPropertyChanged(nameof(TypeText));
        OnPropertyChanged(nameof(ExtText));
        OnPropertyChanged(nameof(Enabled));
        RefreshDetail();
    }

    public bool Enabled
    {
        get => Entry.Enabled;
        set
        {
            if (Entry.Enabled == value) return;
            Entry.Enabled = value;
            OnPropertyChanged();
        }
    }
}

public sealed partial class OverrideViewModel : ObservableObject
{
    private readonly AppCoordinator _coordinator;
    private readonly OverrideService _service = new();
    private OverrideConfig _config = new();

    [ObservableProperty] private string importUrl = "";
    [ObservableProperty] private string importUserAgent = "";
    [ObservableProperty] private string importAuthToken = "";
    [ObservableProperty] private string infoTitle = "编辑信息";
    [ObservableProperty] private string infoName = "";
    [ObservableProperty] private string infoUrl = "";
    [ObservableProperty] private bool infoEnabled;
    [ObservableProperty] private bool isInfoEditingRemote;
    [ObservableProperty] private string infoTypeText = "";
    [ObservableProperty] private string infoUserAgent = "";
    [ObservableProperty] private string infoAuthToken = "";
    [ObservableProperty] private bool infoAutoUpdate;
    [ObservableProperty] private string infoUpdateIntervalMinutes = "";
    [ObservableProperty] private string infoUpdateTimeoutSeconds = "";
    [ObservableProperty] private string editorTitle = "编辑覆写";
    [ObservableProperty] private string editorContent = "";
    [ObservableProperty] private string editorPath = "";
    [ObservableProperty] private string logTitle = "执行日志";
    [ObservableProperty] private string logContent = "";
    [ObservableProperty] private string logPath = "";
    private OverrideItemViewModel? _infoEditingItem;
    private OverrideItemViewModel? _editingItem;

    public OverrideViewModel(AppCoordinator coordinator)
    {
        _coordinator = coordinator;
        Runtime = coordinator.Runtime;
    }

    public RuntimeStore Runtime { get; }
    public ObservableCollection<OverrideItemViewModel> Items { get; } = [];
    public int ItemCount => Items.Count;

    public async Task LoadAsync()
    {
        _config = await _service.LoadAsync();
        var existing = Items.ToDictionary(item => item.Id, StringComparer.OrdinalIgnoreCase);
        var desired = new List<OverrideItemViewModel>(_config.Items.Count);
        foreach (var entry in _config.Items)
        {
            if (!existing.TryGetValue(entry.Id, out var item))
            {
                item = new OverrideItemViewModel(entry);
            }
            else
            {
                item.Apply(entry);
            }

            desired.Add(item);
        }

        ClashSuki.Utilities.CollectionSync.Sync(Items, desired);
        OnPropertyChanged(nameof(ItemCount));
    }

    [RelayCommand]
    private async Task ImportUrlAsync()
    {
        if (string.IsNullOrWhiteSpace(ImportUrl))
        {
            Runtime.Notifications.Warning(
                "请输入覆写 URL。",
                source: LogSources.Override,
                writeLog: false);
            return;
        }

        try
        {
            var fetchRequest = BuildFetchRequest(ImportUserAgent, ImportAuthToken, null);
            var entry = await _service.ImportRemoteAsync(
                _config,
                ImportUrl.Trim(),
                _coordinator.GetMixedPortForDownload(),
                fetchRequest);
            var item = new OverrideItemViewModel(entry);
            Items.Add(item);
            ImportUrl = "";
            ImportUserAgent = "";
            ImportAuthToken = "";
            OnPropertyChanged(nameof(ItemCount));
            Runtime.Notifications.Success("覆写已导入。", source: LogSources.Override);
            if (entry.Enabled)
            {
                await ApplyAsync();
            }
        }
        catch (Exception ex)
        {
            Runtime.Notifications.Error(
                $"覆写导入失败：{ex.Message}",
                source: LogSources.Override,
                exception: ex);
        }
    }

    public async Task ImportFileAsync(string path)
    {
        try
        {
            var entry = await _service.ImportLocalFileAsync(_config, path);
            Items.Add(new OverrideItemViewModel(entry));
            OnPropertyChanged(nameof(ItemCount));
            Runtime.Notifications.Success("覆写文件已导入。", source: LogSources.Override);
        }
        catch (Exception ex)
        {
            Runtime.Notifications.Error(
                $"覆写文件导入失败：{ex.Message}",
                source: LogSources.Override,
                exception: ex);
        }
    }

    [RelayCommand]
    private async Task NewYamlAsync() =>
        await CreateNewAsync("新建 YAML", "yaml", "# https://clashparty.org/docs/guide/override/yaml\n");

    [RelayCommand]
    private async Task NewJsAsync() =>
        await CreateNewAsync(
            "新建 JS",
            "js",
            """
            // https://clashparty.org/docs/guide/override/javascript
            function main(config) {
              return config
            }

            """);

    private async Task CreateNewAsync(string name, string ext, string content)
    {
        var entry = await _service.CreateAsync(_config, name, ext, content);
        Items.Add(new OverrideItemViewModel(entry));
        OnPropertyChanged(nameof(ItemCount));
        Runtime.Notifications.Success($"{name} 已创建。", source: LogSources.Override);
    }

    public async Task BeginEditAsync(OverrideItemViewModel item)
    {
        _editingItem = item;
        EditorTitle = item.IsJavaScript ? $"编辑覆写脚本 - {item.Name}" : $"编辑覆写配置 - {item.Name}";
        EditorPath = _service.GetFilePath(item.Entry);
        EditorContent = await _service.ReadContentAsync(item.Entry);
    }

    public void BeginEditInfo(OverrideItemViewModel item)
    {
        _infoEditingItem = item;
        InfoTitle = $"编辑信息 - {item.Name}";
        InfoName = item.Entry.Name;
        InfoUrl = item.Entry.Url ?? "";
        InfoEnabled = item.Entry.Enabled;
        IsInfoEditingRemote = item.IsRemote;
        InfoTypeText = $"{item.TypeText} · {item.ExtText}";
        InfoUserAgent = item.Entry.UserAgent ?? "";
        InfoAuthToken = item.Entry.AuthToken ?? "";
        InfoAutoUpdate = item.Entry.AutoUpdate;
        InfoUpdateIntervalMinutes = item.Entry.Interval?.ToString() ?? "";
        InfoUpdateTimeoutSeconds = item.Entry.UpdateTimeout?.ToString() ?? "";
    }

    public async Task<bool> SaveInfoAsync()
    {
        if (_infoEditingItem is null)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(InfoName))
        {
            Runtime.Notifications.Warning(
                "显示名称不能为空。",
                source: LogSources.Override,
                writeLog: false);
            return false;
        }

        if (_infoEditingItem.IsRemote && string.IsNullOrWhiteSpace(InfoUrl))
        {
            Runtime.Notifications.Warning(
                "远程覆写地址不能为空。",
                source: LogSources.Override,
                writeLog: false);
            return false;
        }

        var original = CloneEntry(_infoEditingItem.Entry);
        try
        {
            var enabledChanged = _infoEditingItem.Entry.Enabled != InfoEnabled;
            _infoEditingItem.Entry.Name = InfoName.Trim();
            _infoEditingItem.Entry.Enabled = InfoEnabled;
            if (_infoEditingItem.IsRemote)
            {
                _infoEditingItem.Entry.Url = InfoUrl.Trim();
                _infoEditingItem.Entry.UserAgent = string.IsNullOrWhiteSpace(InfoUserAgent) ? null : InfoUserAgent.Trim();
                _infoEditingItem.Entry.AuthToken = string.IsNullOrWhiteSpace(InfoAuthToken) ? null : InfoAuthToken.Trim();
                _infoEditingItem.Entry.AutoUpdate = InfoAutoUpdate;
                _infoEditingItem.Entry.Interval = ParsePositiveInt(InfoUpdateIntervalMinutes);
                _infoEditingItem.Entry.UpdateTimeout = ParsePositiveInt(InfoUpdateTimeoutSeconds);
            }

            await _service.SaveAsync(_config);
            if (enabledChanged && !await ApplyAsync())
            {
                CopyEntry(original, _infoEditingItem.Entry);
                await _service.SaveAsync(_config);
                _infoEditingItem.RefreshDetail();
                return false;
            }

            _infoEditingItem.RefreshDetail();
            Runtime.Notifications.Success("覆写信息已保存。", source: LogSources.Override);
            return true;
        }
        catch (Exception ex)
        {
            CopyEntry(original, _infoEditingItem.Entry);
            await _service.SaveAsync(_config);
            _infoEditingItem.RefreshDetail();
            Runtime.Notifications.Error(
                $"覆写信息保存失败：{ex.Message}",
                source: LogSources.Override,
                exception: ex);
            return false;
        }
    }

    public async Task BeginViewLogAsync(OverrideItemViewModel item)
    {
        LogTitle = $"执行日志 - {item.Name}";
        LogPath = _service.GetLogPath(item.Entry);
        LogContent = await _service.ReadLogAsync(item.Entry);
        if (string.IsNullOrWhiteSpace(LogContent))
        {
            LogContent = "暂无执行日志。";
        }
    }

    public async Task<bool> SaveEditAsync()
    {
        if (_editingItem is null)
        {
            return false;
        }

        var previousContent = await _service.ReadContentAsync(_editingItem.Entry);
        var previousUpdatedAt = _editingItem.Entry.UpdatedAt;
        try
        {
            _editingItem.Entry.UpdatedAt = DateTimeOffset.Now;
            await _service.WriteContentAsync(_editingItem.Entry, EditorContent);
            await _service.SaveAsync(_config);
            if (!await ApplyAsync())
            {
                throw new InvalidOperationException("覆写内容未能应用。");
            }

            _editingItem.RefreshDetail();
            Runtime.Notifications.Success("覆写已保存。", source: LogSources.Override);
            return true;
        }
        catch (Exception ex)
        {
            _editingItem.Entry.UpdatedAt = previousUpdatedAt;
            await _service.WriteContentAsync(_editingItem.Entry, previousContent);
            await _service.SaveAsync(_config);
            _editingItem.RefreshDetail();
            Runtime.Notifications.Error(
                $"覆写保存失败：{ex.Message}",
                source: LogSources.Override,
                exception: ex);
            return false;
        }
    }

    [RelayCommand]
    private async Task ToggleEnabledAsync(OverrideItemViewModel item)
    {
        var previous = !item.Enabled;
        try
        {
            await _service.SaveAsync(_config);
            if (!await ApplyAsync())
            {
                item.Enabled = previous;
                await _service.SaveAsync(_config);
            }
        }
        catch (Exception ex)
        {
            item.Enabled = previous;
            await _service.SaveAsync(_config);
            Runtime.Notifications.Error(
                $"切换覆写失败：{ex.Message}",
                source: LogSources.Override,
                exception: ex);
        }
    }

    [RelayCommand]
    private async Task DeleteAsync(OverrideItemViewModel item)
    {
        var wasEnabled = item.Enabled;
        await _service.DeleteAsync(_config, item.Entry);
        Items.Remove(item);
        OnPropertyChanged(nameof(ItemCount));
        Runtime.Notifications.Success("覆写已删除。", source: LogSources.Override);
        if (wasEnabled)
        {
            await ApplyAsync();
        }
    }

    [RelayCommand]
    private async Task RefreshRemoteAsync(OverrideItemViewModel item)
    {
        if (!item.IsRemote)
        {
            return;
        }

        item.IsUpdating = true;
        var previousContent = await _service.ReadContentAsync(item.Entry);
        var previousUpdatedAt = item.Entry.UpdatedAt;
        try
        {
            await _coordinator.RefreshOverrideRemoteAsync(_config, item.Entry);
            item.RefreshDetail();
            if (!await ApplyAsync())
            {
                throw new InvalidOperationException("更新后的覆写未能应用。");
            }

            Runtime.Notifications.Success("远程覆写已更新。", source: LogSources.Override);
        }
        catch (Exception ex)
        {
            try
            {
                item.Entry.UpdatedAt = previousUpdatedAt;
                await _service.WriteContentAsync(item.Entry, previousContent);
                await _service.SaveAsync(_config);
                item.RefreshDetail();
            }
            catch (Exception rollbackEx)
            {
                DiagnosticLog.WriteAppException("OVERRIDE-MANUAL-UPDATE-ROLLBACK", rollbackEx);
            }

            Runtime.Notifications.Error(
                $"远程覆写更新失败：{ex.Message}",
                source: LogSources.Override,
                exception: ex);
        }
        finally
        {
            item.IsUpdating = false;
        }
    }

    [RelayCommand]
    private async Task OpenFileAsync(OverrideItemViewModel item) =>
        await _coordinator.OpenExternalFileAsync(_service.GetFilePath(item.Entry), "覆写文件");

    [RelayCommand]
    private async Task<bool> ApplyAsync()
    {
        try
        {
            var result = await _coordinator.ApplyOverridesAsync();
            Runtime.Notifications.Success(
                result.EnabledCount == 0
                    ? "已恢复为当前订阅配置。"
                    : $"覆写已应用：YAML {result.YamlCount}，JS {result.ScriptCount}。",
                source: LogSources.Override);
            return true;
        }
        catch (Exception ex)
        {
            Runtime.Notifications.Error(
                $"应用覆写失败：{ex.Message}",
                source: LogSources.Override,
                exception: ex);
            return false;
        }
    }

    private static RemoteFetchRequest BuildFetchRequest(string? userAgent, string? authToken, int? timeoutSeconds)
    {
        return new RemoteFetchRequest(
            string.IsNullOrWhiteSpace(userAgent) ? null : userAgent.Trim(),
            string.IsNullOrWhiteSpace(authToken) ? null : authToken.Trim(),
            timeoutSeconds);
    }

    private static int? ParsePositiveInt(string? text) =>
        int.TryParse(text?.Trim(), out var value) && value > 0 ? value : null;

    private static OverrideEntry CloneEntry(OverrideEntry source) => new()
    {
        Id = source.Id,
        Name = source.Name,
        Type = source.Type,
        Ext = source.Ext,
        Url = source.Url,
        Enabled = source.Enabled,
        UpdatedAt = source.UpdatedAt,
        UserAgent = source.UserAgent,
        AuthToken = source.AuthToken,
        AutoUpdate = source.AutoUpdate,
        Interval = source.Interval,
        UpdateTimeout = source.UpdateTimeout
    };

    private static void CopyEntry(OverrideEntry source, OverrideEntry target)
    {
        target.Id = source.Id;
        target.Name = source.Name;
        target.Type = source.Type;
        target.Ext = source.Ext;
        target.Url = source.Url;
        target.Enabled = source.Enabled;
        target.UpdatedAt = source.UpdatedAt;
        target.UserAgent = source.UserAgent;
        target.AuthToken = source.AuthToken;
        target.AutoUpdate = source.AutoUpdate;
        target.Interval = source.Interval;
        target.UpdateTimeout = source.UpdateTimeout;
    }
}
