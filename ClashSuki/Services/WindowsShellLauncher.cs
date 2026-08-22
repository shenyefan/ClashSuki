using Windows.Storage;
using Windows.System;

namespace ClashSuki.Services;

public static class WindowsShellLauncher
{
    private static readonly HashSet<string> BlockedFileExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".application",
        ".bat",
        ".cmd",
        ".com",
        ".cpl",
        ".exe",
        ".gadget",
        ".hta",
        ".inf",
        ".jar",
        ".js",
        ".jse",
        ".lnk",
        ".msc",
        ".msi",
        ".msp",
        ".mst",
        ".pif",
        ".ps1",
        ".ps1xml",
        ".ps2",
        ".ps2xml",
        ".psc1",
        ".psc2",
        ".reg",
        ".scf",
        ".scr",
        ".url",
        ".vbe",
        ".vbs",
        ".wsf",
        ".wsh"
    };

    public static async Task LaunchUriAsync(Uri uri, string label)
    {
        ArgumentNullException.ThrowIfNull(uri);
        if (!uri.IsAbsoluteUri)
        {
            throw new ArgumentException("只能打开绝对 URI。", nameof(uri));
        }

        var targetName = NormalizeLabel(label, "链接");
        await EnsureLaunchedAsync(
            () => RunOnUiThreadAsync(async () => await Launcher.LaunchUriAsync(uri)),
            $"Windows 无法打开{targetName}：{uri.AbsoluteUri}");
    }

    public static async Task LaunchFolderPathAsync(string folderPath, string label)
    {
        if (string.IsNullOrWhiteSpace(folderPath))
        {
            throw new DirectoryNotFoundException("目录路径为空。");
        }

        var fullPath = Path.GetFullPath(folderPath);
        if (!Directory.Exists(fullPath))
        {
            throw new DirectoryNotFoundException($"{NormalizeLabel(label, "目录")}不存在：{fullPath}");
        }

        var targetName = NormalizeLabel(label, "目录");
        await EnsureLaunchedAsync(
            () => RunOnUiThreadAsync(async () => await Launcher.LaunchFolderPathAsync(fullPath)),
            $"Windows 无法打开{targetName}：{fullPath}");
    }

    public static async Task LaunchFileAsync(string filePath, string label)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            throw new FileNotFoundException("文件路径为空。", filePath);
        }

        var fullPath = Path.GetFullPath(filePath);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException($"{NormalizeLabel(label, "文件")}不存在。", fullPath);
        }

        if (BlockedFileExtensions.Contains(Path.GetExtension(fullPath)))
        {
            throw new InvalidOperationException(
                $"出于安全考虑，ClashSuki 不会启动可执行文件、安装包、脚本、注册表文件或快捷方式：{fullPath}");
        }

        var targetName = NormalizeLabel(label, "文件");
        await EnsureLaunchedAsync(
            () => RunOnUiThreadAsync(async () =>
            {
                var file = await StorageFile.GetFileFromPathAsync(fullPath);
                return await Launcher.LaunchFileAsync(file);
            }),
            $"Windows 无法打开{targetName}：{fullPath}");
    }

    private static async Task EnsureLaunchedAsync(
        Func<Task<bool>> launchOperation,
        string failureMessage)
    {
        bool launched;
        try
        {
            launched = await launchOperation();
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"{failureMessage}（{ex.Message}）", ex);
        }

        if (!launched)
        {
            throw new InvalidOperationException(failureMessage);
        }
    }

    private static Task<T> RunOnUiThreadAsync<T>(Func<Task<T>> operation)
    {
        var dispatcher = App.CurrentWindow?.DispatcherQueue ??
                         Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        if (dispatcher is null)
        {
            return Task.FromException<T>(new InvalidOperationException("找不到可用的 UI 调度线程。"));
        }

        if (dispatcher.HasThreadAccess)
        {
            try
            {
                return operation();
            }
            catch (Exception ex)
            {
                return Task.FromException<T>(ex);
            }
        }

        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!dispatcher.TryEnqueue(async () =>
            {
                try
                {
                    completion.TrySetResult(await operation());
                }
                catch (Exception ex)
                {
                    completion.TrySetException(ex);
                }
            }))
        {
            completion.TrySetException(new InvalidOperationException("UI 调度线程已停止接收任务。"));
        }

        return completion.Task;
    }

    private static string NormalizeLabel(string? label, string fallback) =>
        string.IsNullOrWhiteSpace(label) ? fallback : label.Trim();
}
