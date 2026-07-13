using System.Collections.ObjectModel;
using ClashSuki.Models;
using ClashSuki.Services;
using ClashSuki.Utilities;
using ClashSuki.ViewModels;

namespace ClashSuki.Stores;

public sealed class ProfileStore : IDisposable
{
    public sealed record UpdateSnapshot(ProfileItem Item, string? Content);

    private readonly ProfileService _service;
    private ProfilesConfig _config = new();

    public ProfileStore(ProfileService service)
    {
        _service = service;
    }

    public ObservableCollection<ProfileItemViewModel> Items { get; } = [];
    public string ActiveUid => _config.Current ?? "";

    public async Task<string?> BuildActiveRuntimeYamlAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_config.Current))
        {
            return null;
        }

        var profile = _config.Items.FirstOrDefault(
            item => string.Equals(item.Uid, _config.Current, StringComparison.Ordinal));
        return profile is null
            ? null
            : await _service.BuildRuntimeYamlAsync(profile, cancellationToken);
    }

    public async Task LoadAsync(CancellationToken cancellationToken)
    {
        _config = await _service.LoadAsync(cancellationToken);
        var existing = Items.ToDictionary(item => item.Uid, StringComparer.Ordinal);
        var desired = new List<ProfileItemViewModel>(_config.Items.Count);
        foreach (var profile in _config.Items)
        {
            if (!existing.TryGetValue(profile.Uid, out var item))
            {
                item = new ProfileItemViewModel { Uid = profile.Uid };
            }

            item.Name = profile.Name;
            item.Type = profile.Type;
            item.Url = profile.Url ?? profile.File ?? "";
            item.File = profile.File ?? "";
            item.IsActive = profile.Uid == _config.Current;
            item.UpdatedText = profile.Updated is { } ts
                ? DateTimeOffset.FromUnixTimeSeconds(ts).LocalDateTime.ToString("MM/dd HH:mm")
                : "未更新";
            item.UsedText = profile.Extra is { } e
                ? e.Total > 0
                    ? $"已用 {Formatters.FormatBytes(e.Used)} / 总量 {Formatters.FormatBytes(e.Total)}"
                    : $"已用 {Formatters.FormatBytes(e.Used)} / 不限流量"
                : "";
            item.ExpireText = profile.Extra?.Expire is > 0 and var exp
                ? DateTimeOffset.FromUnixTimeSeconds(exp).LocalDateTime.ToString("yyyy/MM/dd 到期")
                : "";
            item.UserAgent = profile.UserAgent ?? "";
            item.AuthToken = profile.AuthToken ?? "";
            item.AgeSecretKey = profile.AgeSecretKey ?? "";
            item.IntervalMinutes = profile.Interval;
            item.AutoUpdate = profile.AutoUpdate;
            desired.Add(item);
        }

        CollectionSync.Sync(Items, desired);
    }

    public async Task<ProfileItem> AddRemoteAsync(
        string name,
        string url,
        string? userAgent,
        string? authToken,
        string? ageSecretKey,
        string? fileName,
        int? mixedPort,
        CancellationToken cancellationToken)
    {
        var profile = new ProfileItem
        {
            Name = string.IsNullOrWhiteSpace(name) ? InferName(url) : name.Trim(),
            NameCustomized = !string.IsNullOrWhiteSpace(name),
            Type = "remote",
            Url = url.Trim(),
            UserAgent = string.IsNullOrWhiteSpace(userAgent) ? null : userAgent.Trim(),
            AuthToken = string.IsNullOrWhiteSpace(authToken) ? null : authToken.Trim(),
            AgeSecretKey = NormalizeAgeSecretKey(ageSecretKey),
            File = string.IsNullOrWhiteSpace(fileName) ? null : fileName.Trim()
        };

        profile = await _service.DownloadAsync(profile, mixedPort, cancellationToken);
        _config.Items.Add(profile);
        await _service.SaveAsync(_config, cancellationToken);
        await LoadAsync(cancellationToken);
        return profile;
    }

    public async Task UpdateSettingsAsync(
        string uid,
        string name,
        string? url,
        string? userAgent,
        string? authToken,
        string? ageSecretKey,
        string? fileName,
        int? updateIntervalMinutes,
        bool autoUpdate,
        CancellationToken cancellationToken)
    {
        var profile = Require(uid);
        if (!string.IsNullOrWhiteSpace(name) &&
            !string.Equals(profile.Name, name.Trim(), StringComparison.Ordinal))
        {
            profile.Name = name.Trim();
            profile.NameCustomized = true;
        }
        profile.UserAgent = string.IsNullOrWhiteSpace(userAgent) ? null : userAgent.Trim();
        profile.AuthToken = string.IsNullOrWhiteSpace(authToken) ? null : authToken.Trim();
        profile.AgeSecretKey = NormalizeAgeSecretKey(ageSecretKey);
        profile.Interval = updateIntervalMinutes;
        profile.AutoUpdate = autoUpdate;

        if (string.Equals(profile.Type, "remote", StringComparison.OrdinalIgnoreCase))
        {
            profile.Url = string.IsNullOrWhiteSpace(url) ? profile.Url : url.Trim();
        }

        if (!string.IsNullOrWhiteSpace(fileName))
        {
            var nextFile = NormalizeProfileFileName(fileName, profile.Uid);
            if (!string.Equals(profile.File, nextFile, StringComparison.OrdinalIgnoreCase))
            {
                RenameProfileFile(profile.File, nextFile);
                profile.File = nextFile;
            }
        }

        await _service.SaveAsync(_config, cancellationToken);
        await LoadAsync(cancellationToken);
    }

    public async Task<ProfileItem> ImportLocalAsync(
        string name,
        string fileName,
        string content,
        CancellationToken cancellationToken)
    {
        var profile = new ProfileItem
        {
            Name = string.IsNullOrWhiteSpace(name) ? Path.GetFileNameWithoutExtension(fileName) : name.Trim(),
            Type = "local",
            File = string.IsNullOrWhiteSpace(fileName) ? null : fileName.Trim()
        };

        profile = await _service.ImportLocalAsync(profile, content, cancellationToken);
        _config.Items.Add(profile);
        await _service.SaveAsync(_config, cancellationToken);
        await LoadAsync(cancellationToken);
        return profile;
    }

    public async Task<ProfileItem> UpdateAsync(
        string uid,
        int? mixedPort,
        CancellationToken cancellationToken)
    {
        var profile = Require(uid);
        if (!string.Equals(profile.Type, "remote", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("本地配置暂不支持在线更新。");
        }

        SetBusy(uid, true);
        try
        {
            var updated = await _service.DownloadAsync(profile, mixedPort, cancellationToken);
            var index = _config.Items.FindIndex(item => item.Uid == uid);
            _config.Items[index] = updated;
            await _service.SaveAsync(_config, cancellationToken);
            await LoadAsync(cancellationToken);
            return updated;
        }
        finally
        {
            SetBusy(uid, false);
        }
    }

    public async Task<UpdateSnapshot> CaptureUpdateSnapshotAsync(
        string uid,
        CancellationToken cancellationToken)
    {
        var item = CloneProfile(Require(uid));
        var path = string.IsNullOrWhiteSpace(item.File)
            ? null
            : Path.Combine(AppPaths.DataRoot, "profiles", item.File);
        var content = path is not null && File.Exists(path)
            ? await File.ReadAllTextAsync(path, cancellationToken)
            : null;
        return new UpdateSnapshot(item, content);
    }

    public async Task RestoreUpdateSnapshotAsync(
        UpdateSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        var index = _config.Items.FindIndex(item => item.Uid == snapshot.Item.Uid);
        if (index >= 0)
        {
            _config.Items[index] = CloneProfile(snapshot.Item);
        }

        if (!string.IsNullOrWhiteSpace(snapshot.Item.File) && snapshot.Content is not null)
        {
            var path = Path.Combine(AppPaths.DataRoot, "profiles", snapshot.Item.File);
            await File.WriteAllTextAsync(path, snapshot.Content, cancellationToken);
        }

        await _service.SaveAsync(_config, cancellationToken);
        await LoadAsync(cancellationToken);
    }

    public async Task SetActiveAsync(
        string uid,
        CancellationToken cancellationToken)
    {
        _ = Require(uid);
        SetBusy(uid, true);
        try
        {
            _config.Current = uid;
            await _service.SaveAsync(_config, cancellationToken);
            await LoadAsync(cancellationToken);
        }
        finally
        {
            SetBusy(uid, false);
        }
    }

    public async Task RestoreActiveAsync(
        string? uid,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(uid))
        {
            _ = Require(uid);
        }

        _config.Current = string.IsNullOrWhiteSpace(uid) ? null : uid;
        await _service.SaveAsync(_config, cancellationToken);
        await LoadAsync(cancellationToken);
    }

    public async Task DeleteAsync(string uid, CancellationToken cancellationToken)
    {
        var profile = Require(uid);
        _service.Delete(profile);
        _config.Items.Remove(profile);
        if (_config.Current == uid)
        {
            _config.Current = _config.Items.FirstOrDefault()?.Uid;
        }

        await _service.SaveAsync(_config, cancellationToken);
        await LoadAsync(cancellationToken);
    }

    public string GetProfileFilePath(string uid)
    {
        var profile = Require(uid);
        if (string.IsNullOrWhiteSpace(profile.File))
        {
            throw new InvalidOperationException("该配置没有关联的本地文件。");
        }

        return Path.Combine(AppPaths.DataRoot, "profiles", profile.File);
    }

    private ProfileItem Require(string uid) =>
        _config.Items.FirstOrDefault(item => item.Uid == uid)
        ?? throw new InvalidOperationException("订阅不存在或已删除。");

    private void SetBusy(string uid, bool busy)
    {
        var item = Items.FirstOrDefault(item => item.Uid == uid);
        if (item is not null)
        {
            item.IsBusy = busy;
        }
    }

    private static string InferName(string url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && !string.IsNullOrWhiteSpace(uri.Host))
        {
            return uri.Host;
        }

        return "远程订阅";
    }

    private static void RenameProfileFile(string? oldFile, string nextFile)
    {
        if (string.IsNullOrWhiteSpace(oldFile))
        {
            return;
        }

        var oldPath = Path.Combine(AppPaths.DataRoot, "profiles", oldFile);
        var nextPath = Path.Combine(AppPaths.DataRoot, "profiles", nextFile);
        if (!File.Exists(oldPath) || string.Equals(oldPath, nextPath, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(nextPath)!);
        File.Move(oldPath, nextPath, overwrite: true);
    }

    private static string NormalizeProfileFileName(string? fileName, string uid)
    {
        fileName = string.IsNullOrWhiteSpace(fileName) ? $"{uid}.yaml" : Path.GetFileName(fileName.Trim());
        foreach (var invalid in Path.GetInvalidFileNameChars())
        {
            fileName = fileName.Replace(invalid, '_');
        }

        var extension = Path.GetExtension(fileName);
        if (!extension.Equals(".yaml", StringComparison.OrdinalIgnoreCase) &&
            !extension.Equals(".yml", StringComparison.OrdinalIgnoreCase))
        {
            fileName += ".yaml";
        }

        return string.IsNullOrWhiteSpace(fileName) ? $"{uid}.yaml" : fileName;
    }

    private static string? NormalizeAgeSecretKey(string? ageSecretKey)
    {
        if (string.IsNullOrWhiteSpace(ageSecretKey))
        {
            return null;
        }

        var keys = ageSecretKey
            .Split(new[] { '\r', '\n', '\t', ' ', ',' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(key => key.StartsWith("AGE-SECRET-KEY-1", StringComparison.OrdinalIgnoreCase) ||
                          key.StartsWith("AGE-SECRET-KEY-PQ-1", StringComparison.OrdinalIgnoreCase));

        var normalized = string.Join(Environment.NewLine, keys);
        return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
    }

    public void Dispose() => _service.Dispose();

    private static ProfileItem CloneProfile(ProfileItem source) => new()
    {
        Uid = source.Uid,
        Type = source.Type,
        Name = source.Name,
        NameCustomized = source.NameCustomized,
        Url = source.Url,
        File = source.File,
        Desc = source.Desc,
        Updated = source.Updated,
        Extra = source.Extra is null
            ? null
            : new ProfileExtra
            {
                Upload = source.Extra.Upload,
                Download = source.Extra.Download,
                Total = source.Extra.Total,
                Expire = source.Extra.Expire
            },
        UserAgent = source.UserAgent,
        AuthToken = source.AuthToken,
        AgeSecretKey = source.AgeSecretKey,
        Interval = source.Interval,
        AutoUpdate = source.AutoUpdate
    };
}
