using System.Text.Json;

namespace ClashSuki.Services;

public sealed class OverrideEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "override.yaml";
    public string Type { get; set; } = "local";
    public string Ext { get; set; } = "yaml";
    public string? Url { get; set; }
    public bool Enabled { get; set; } = true;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.Now;
    public string? UserAgent { get; set; }
    public string? AuthToken { get; set; }
    public bool AutoUpdate { get; set; }
    public int? Interval { get; set; }
    public int? UpdateTimeout { get; set; }
}

public sealed class OverrideConfig
{
    public List<OverrideEntry> Items { get; set; } = [];
}

public sealed class OverrideService : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static string OverrideDirectory => Path.Combine(AppPaths.DataRoot, "overrides");
    private static string ConfigPath => Path.Combine(OverrideDirectory, "overrides.json");

    private readonly RemoteResourceFetchService _fetch = new();

    public void Dispose() => _fetch.Dispose();

    public async Task<OverrideConfig> LoadAsync(CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(OverrideDirectory);
        if (!File.Exists(ConfigPath))
        {
            return new OverrideConfig();
        }

        await using var stream = File.OpenRead(ConfigPath);
        return await JsonSerializer.DeserializeAsync<OverrideConfig>(stream, JsonOptions, cancellationToken)
               ?? new OverrideConfig();
    }

    public async Task SaveAsync(OverrideConfig config, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(OverrideDirectory);
        var json = JsonSerializer.Serialize(config, JsonOptions);
        await File.WriteAllTextAsync(ConfigPath, json, cancellationToken);
    }

    public string GetFilePath(OverrideEntry entry) =>
        Path.Combine(OverrideDirectory, $"{entry.Id}.{NormalizeExt(entry.Ext)}");

    public string GetLogPath(OverrideEntry entry) =>
        Path.Combine(OverrideDirectory, $"{entry.Id}.log");

    public async Task<string> ReadContentAsync(OverrideEntry entry, CancellationToken cancellationToken = default)
    {
        var path = GetFilePath(entry);
        return File.Exists(path) ? await File.ReadAllTextAsync(path, cancellationToken) : "";
    }

    public async Task<string> ReadLogAsync(OverrideEntry entry, CancellationToken cancellationToken = default)
    {
        var path = GetLogPath(entry);
        return File.Exists(path) ? await File.ReadAllTextAsync(path, cancellationToken) : "";
    }

    public async Task WriteContentAsync(OverrideEntry entry, string content, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(OverrideDirectory);
        await File.WriteAllTextAsync(GetFilePath(entry), content, cancellationToken);
    }

    public async Task<OverrideEntry> CreateAsync(OverrideConfig config, string name, string ext, string content, CancellationToken cancellationToken = default)
    {
        var entry = new OverrideEntry
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = string.IsNullOrWhiteSpace(name) ? $"override.{NormalizeExt(ext)}" : name.Trim(),
            Type = "local",
            Ext = NormalizeExt(ext),
            UpdatedAt = DateTimeOffset.Now
        };
        config.Items.Add(entry);
        await WriteContentAsync(entry, content, cancellationToken);
        await SaveAsync(config, cancellationToken);
        return entry;
    }

    public async Task<OverrideEntry> ImportLocalFileAsync(
        OverrideConfig config,
        string path,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("覆写文件不存在。", path);
        }

        var fileName = Path.GetFileName(path);
        var content = await File.ReadAllTextAsync(path, cancellationToken);
        var ext = fileName.EndsWith(".js", StringComparison.OrdinalIgnoreCase) ? "js" : "yaml";
        return await CreateAsync(config, fileName, ext, content, cancellationToken);
    }

    public async Task<OverrideEntry> ImportRemoteAsync(
        OverrideConfig config,
        string url,
        int? mixedPort,
        RemoteFetchRequest? fetchRequest = null,
        CancellationToken cancellationToken = default)
    {
        var content = await DownloadRemoteAsync(url, fetchRequest, mixedPort, cancellationToken);
        var fileName = TryGetFileName(url);
        var ext = fileName.EndsWith(".js", StringComparison.OrdinalIgnoreCase) ? "js" : "yaml";
        var entry = new OverrideEntry
        {
            Name = string.IsNullOrWhiteSpace(fileName) ? $"remote.{ext}" : fileName,
            Type = "remote",
            Ext = ext,
            Url = url.Trim(),
            UserAgent = fetchRequest?.UserAgent,
            AuthToken = fetchRequest?.AuthToken,
            UpdateTimeout = fetchRequest?.TimeoutSeconds,
            UpdatedAt = DateTimeOffset.Now
        };
        config.Items.Add(entry);
        await WriteContentAsync(entry, content, cancellationToken);
        await SaveAsync(config, cancellationToken);
        return entry;
    }

    public async Task RefreshRemoteAsync(
        OverrideConfig config,
        OverrideEntry entry,
        int? mixedPort,
        CancellationToken cancellationToken = default)
    {
        if (!entry.Type.Equals("remote", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(entry.Url))
        {
            return;
        }

        var content = await DownloadRemoteAsync(
            entry.Url,
            BuildFetchRequest(entry),
            mixedPort,
            cancellationToken);
        entry.UpdatedAt = DateTimeOffset.Now;
        await WriteContentAsync(entry, content, cancellationToken);
        await SaveAsync(config, cancellationToken);
    }

    public async Task DeleteAsync(OverrideConfig config, OverrideEntry entry, CancellationToken cancellationToken = default)
    {
        config.Items.RemoveAll(item => item.Id == entry.Id);
        var path = GetFilePath(entry);
        if (File.Exists(path))
        {
            File.Delete(path);
        }

        await SaveAsync(config, cancellationToken);
    }

    private async Task<string> DownloadRemoteAsync(
        string url,
        RemoteFetchRequest? fetchRequest,
        int? mixedPort,
        CancellationToken cancellationToken) =>
        await _fetch.FetchAsync(
            url,
            fetchRequest ?? new RemoteFetchRequest(),
            mixedPort,
            cancellationToken);

    private static RemoteFetchRequest BuildFetchRequest(OverrideEntry entry) =>
        new(entry.UserAgent, entry.AuthToken, entry.UpdateTimeout);

    private static string NormalizeExt(string? ext) =>
        ext?.TrimStart('.').ToLowerInvariant() == "js" ? "js" : "yaml";

    private static string TryGetFileName(string url)
    {
        try
        {
            var path = new Uri(url).AbsolutePath;
            return Uri.UnescapeDataString(Path.GetFileName(path));
        }
        catch
        {
            return "";
        }
    }
}
