namespace ClashSuki.Services;

public sealed class ConfigFileSnapshot
{
    private readonly IReadOnlyDictionary<string, string?> _files;

    private ConfigFileSnapshot(IReadOnlyDictionary<string, string?> files)
    {
        _files = files;
    }

    public static async Task<ConfigFileSnapshot> CaptureAsync(
        IEnumerable<string> paths,
        CancellationToken cancellationToken)
    {
        var files = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in paths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            files[path] = File.Exists(path)
                ? await File.ReadAllTextAsync(path, cancellationToken)
                : null;
        }

        return new ConfigFileSnapshot(files);
    }

    public string? GetContent(string path) =>
        _files.TryGetValue(path, out var content) ? content : null;

    public async Task RestoreAsync()
    {
        foreach (var (path, content) in _files)
        {
            if (content is null)
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }

                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, content, CancellationToken.None);
        }
    }
}
