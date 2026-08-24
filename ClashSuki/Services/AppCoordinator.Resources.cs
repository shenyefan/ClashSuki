using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace ClashSuki.Services;

public sealed partial class AppCoordinator
{
    public async Task CloseAllConnectionsAsync()
    {
        await _api.CloseAllConnectionsAsync(_cts.Token);
    }

    public async Task CloseConnectionAsync(string id)
    {
        if (!string.IsNullOrWhiteSpace(id))
        {
            await _api.CloseConnectionAsync(id, _cts.Token);
        }
    }

    public async Task TestGroupDelayAsync(string groupName)
    {
        var group = Proxies.FindGroup(groupName);
        if (group is null)
        {
            return;
        }

        var targets = group.FilteredNodes.ToList();
        if (targets.Count == 0)
        {
            return;
        }

        await _dispatcher.RunAsync(() =>
        {
            foreach (var node in targets)
            {
                node.IsGroupDelayPending = true;
            }

            group.NotifyGroupDelayState();
        });

        try
        {
            var settings = await AppSettingsService.LoadAsync(_cts.Token);
            var url = IsDefaultDelayTestUrl(group.TestUrl)
                ? settings.DelayTestUrl
                : group.TestUrl;
            var timeout = group.TimeoutMs > 0 ? group.TimeoutMs : Math.Max(1000, settings.DelayTestTimeout);
            var concurrency = Math.Clamp(settings.DelayTestConcurrency, 1, 100);
            using var semaphore = new SemaphoreSlim(concurrency, concurrency);
            var tasks = targets.Select(async node =>
            {
                await semaphore.WaitAsync(_cts.Token);
                try
                {
                    var delay = await _api.TestProxyDelayAsync(node.Name, url, timeout, _cts.Token);
                    await _dispatcher.RunAsync(() =>
                    {
                        node.Delay = delay;
                        if (node.Name.Equals(group.CurrentNode, StringComparison.OrdinalIgnoreCase))
                        {
                            group.Delay = delay;
                        }
                    });
                }
                finally
                {
                    await _dispatcher.RunAsync(() =>
                    {
                        node.IsGroupDelayPending = false;
                        group.NotifyGroupDelayState();
                    });
                    semaphore.Release();
                }
            });
            await Task.WhenAll(tasks);
            await _dispatcher.RunAsync(group.RefreshFiltered);
            _ = RefreshProxiesAsync(_cts.Token);
        }
        catch (Exception ex) when (!IsAppCancellation(ex))
        {
            await _dispatcher.RunAsync(() =>
            {
                Runtime.Notifications.Error(
                    "测速失败",
                    source: LogSources.Proxy,
                    exception: ex);
            });
        }
        finally
        {
            await _dispatcher.RunAsync(() =>
            {
                foreach (var node in targets)
                {
                    node.IsGroupDelayPending = false;
                }

                group.NotifyGroupDelayState();
            });
        }
    }

    public async Task UnfixProxyAsync(string groupName)
    {
        try
        {
            await _api.UnfixProxyAsync(groupName, _cts.Token);
            await RefreshProxiesAsync(_cts.Token);
        }
        catch (Exception ex) when (!IsAppCancellation(ex))
        {
            await _dispatcher.RunAsync(() =>
            {
                Runtime.Notifications.Error(
                    "取消固定节点失败",
                    source: LogSources.Proxy,
                    exception: ex);
            });
        }
    }

    public async Task TestNodeDelayAsync(string groupName, string nodeName)
    {
        var group = Proxies.FindGroup(groupName);
        if (group is null)
        {
            return;
        }

        var node = group.Nodes.FirstOrDefault(n =>
            n.Name.Equals(nodeName, StringComparison.OrdinalIgnoreCase));
        if (node is null)
        {
            return;
        }

        await _dispatcher.RunAsync(() => node.IsTesting = true);
        try
        {
            var settings = await AppSettingsService.LoadAsync(_cts.Token);
            var url = IsDefaultDelayTestUrl(group.TestUrl)
                ? settings.DelayTestUrl
                : group.TestUrl;
            var timeout = group.TimeoutMs > 0 ? group.TimeoutMs : Math.Max(1000, settings.DelayTestTimeout);
            var delay = await _api.TestProxyDelayAsync(node.Name, url, timeout, _cts.Token);
            await _dispatcher.RunAsync(() =>
            {
                node.Delay = delay;
                if (node.Name.Equals(group.CurrentNode, StringComparison.OrdinalIgnoreCase))
                {
                    group.Delay = delay;
                }
            });
        }
        catch (Exception ex) when (!IsAppCancellation(ex))
        {
            await _dispatcher.RunAsync(() =>
            {
                Runtime.Notifications.Error(
                    "节点测速失败",
                    source: LogSources.Proxy,
                    exception: ex);
            });
        }
        finally
        {
            await _dispatcher.RunAsync(() => node.IsTesting = false);
            _ = RefreshProxiesAsync(_cts.Token);
        }
    }

    public async Task UpdateProxyProviderAsync(string provider)
    {
        await _api.UpdateProxyProviderAsync(provider, _cts.Token);
        await RefreshProxiesAsync(_cts.Token);
    }

    public Task RefreshProxiesNowAsync() => RefreshProxiesAsync(_cts.Token);

    public async Task UpdateRuleProviderAsync(string provider)
    {
        await _api.UpdateRuleProviderAsync(provider, _cts.Token);
        await RefreshRulesAsync(_cts.Token);
    }

    public Task RefreshRulesNowAsync() => RefreshRulesAsync(_cts.Token);

    public async Task<RuleProviderDocument> OpenRuleProviderDocumentAsync(string provider)
    {
        var configs = await YamlConfigService.LoadRuleProviderConfigsAsync(AppPaths.RuntimeConfigPath, _cts.Token);
        configs.TryGetValue(provider, out var config);

        var vehicleType = config?.VehicleType ?? "";
        var format = config?.Format ?? "YamlRule";

        if (vehicleType.Equals("Inline", StringComparison.OrdinalIgnoreCase))
        {
            var content = string.IsNullOrWhiteSpace(config?.Payload) ? "[]" : config.Payload;
            return new RuleProviderDocument(provider, content, AppPaths.RuntimeConfigPath, format);
        }

        var sourcePath = ResolveProviderPath(config);
        if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
        {
            throw new FileNotFoundException(string.IsNullOrWhiteSpace(sourcePath)
                ? "未找到规则集合文件路径"
                : $"规则集合文件不存在：{sourcePath}", sourcePath);
        }

        var fileContent = format.Equals("MrsRule", StringComparison.OrdinalIgnoreCase)
            ? await ConvertMrsRulesetAsync(sourcePath, config?.Behavior ?? "domain")
            : await File.ReadAllTextAsync(sourcePath, _cts.Token);
        return new RuleProviderDocument(provider, fileContent, sourcePath, format);
    }

    public async Task SetRuleDisabledAsync(int ruleIndex, bool disabled)
    {
        try
        {
            await _api.DisableRulesAsync(new Dictionary<int, bool> { [ruleIndex] = disabled }, _cts.Token);
            await RefreshRulesAsync(_cts.Token);
        }
        catch (Exception ex) when (!IsAppCancellation(ex))
        {
            await _dispatcher.RunAsync(() =>
            {
                Runtime.Notifications.Error(
                    "规则状态更新失败",
                    source: LogSources.Rule,
                    exception: ex);
            });
            throw;
        }
    }

    private async Task<string> ConvertMrsRulesetAsync(string sourcePath, string behavior)
    {
        if (!File.Exists(AppPaths.ManagedCorePath))
        {
            throw new FileNotFoundException("mihomo 内核不存在，无法转换 MRS 规则集合", AppPaths.ManagedCorePath);
        }

        var tempPath = Path.Combine(Path.GetTempPath(), $"clashsuki-mrs-{Guid.NewGuid():N}.txt");
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = AppPaths.ManagedCorePath,
                WorkingDirectory = AppPaths.DataRoot,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            startInfo.ArgumentList.Add("convert-ruleset");
            startInfo.ArgumentList.Add(string.IsNullOrWhiteSpace(behavior) ? "domain" : behavior);
            startInfo.ArgumentList.Add("mrs");
            startInfo.ArgumentList.Add(sourcePath);
            startInfo.ArgumentList.Add(tempPath);

            using var process = Process.Start(startInfo) ??
                                throw new InvalidOperationException("无法启动 mihomo 转换 MRS 规则集合");
            using var cancellationRegistration =
                ProcessCancellation.TerminateOnCancellation(process, _cts.Token);
            var outputTask = process.StandardOutput.ReadToEndAsync(_cts.Token);
            var errorTask = process.StandardError.ReadToEndAsync(_cts.Token);
            await process.WaitForExitAsync(_cts.Token);
            var output = await outputTask;
            var error = await errorTask;

            if (process.ExitCode != 0)
            {
                var message = string.IsNullOrWhiteSpace(error) ? output : error;
                throw new InvalidOperationException($"MRS 规则集合转换失败：{message.Trim()}");
            }

            return await File.ReadAllTextAsync(tempPath, _cts.Token);
        }
        finally
        {
            TryDeleteFile(tempPath);
        }
    }

    private static string ResolveProviderPath(YamlConfigService.RuleProviderConfigInfo? config)
    {
        var candidates = BuildProviderPathCandidates(config);
        return candidates.FirstOrDefault(File.Exists) ?? candidates.FirstOrDefault() ?? "";
    }

    private static IReadOnlyList<string> BuildProviderPathCandidates(YamlConfigService.RuleProviderConfigInfo? config)
    {
        if (config is null)
        {
            return [];
        }

        var candidates = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void AddPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            path = NormalizeProviderPath(path);
            if (Path.IsPathRooted(path))
            {
                AddCandidate(path);
                return;
            }

            AddCandidate(Path.Combine(AppPaths.DataRoot, path));
            AddCandidate(Path.Combine(AppPaths.ConfigDirectory, path));
        }

        void AddCandidate(string path)
        {
            path = Path.GetFullPath(path);
            if (seen.Add(path))
            {
                candidates.Add(path);
            }
        }

        if (!string.IsNullOrWhiteSpace(config.Path))
        {
            AddPath(config.Path);
            if (!Path.HasExtension(config.Path))
            {
                foreach (var extension in ProviderPathExtensions(config))
                {
                    AddPath(config.Path + extension);
                }
            }

            return candidates;
        }

        var key = string.IsNullOrWhiteSpace(config.Url) ? config.Name : config.Url;
        var basePath = Path.Combine("rules", Md5Hex(key));
        AddPath(basePath);
        foreach (var extension in ProviderPathExtensions(config))
        {
            AddPath(basePath + extension);
        }

        return candidates;
    }

    private static string NormalizeProviderPath(string path)
    {
        path = path.Replace('/', Path.DirectorySeparatorChar).Trim();
        if (path.StartsWith($".{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
        {
            path = path[2..];
        }

        return path;
    }

    private static IEnumerable<string> ProviderPathExtensions(YamlConfigService.RuleProviderConfigInfo config)
    {
        var format = config.Format;
        if (format.Contains("Mrs", StringComparison.OrdinalIgnoreCase))
        {
            yield return ".mrs";
        }
        else if (format.Contains("Text", StringComparison.OrdinalIgnoreCase))
        {
            yield return ".list";
            yield return ".txt";
        }
        else
        {
            yield return ".yaml";
            yield return ".yml";
        }

        yield return ".mrs";
        yield return ".yaml";
        yield return ".yml";
        yield return ".list";
        yield return ".txt";
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex)
        {
            DiagnosticLog.WriteAppException(
                LogSources.Rule,
                ex,
                $"删除规则转换临时文件失败，路径: {path}",
                "WARN");
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception ex)
        {
            DiagnosticLog.WriteAppException(
                LogSources.Core,
                ex,
                $"删除内核下载临时目录失败，路径: {path}",
                "WARN");
        }
    }


    private static string FormatCoreSetting(MihomoCoreReleaseKind kind) =>
        kind switch
        {
            MihomoCoreReleaseKind.Preview => "preview",
            MihomoCoreReleaseKind.Smart => "smart",
            MihomoCoreReleaseKind.Specific => "specific",
            _ => "latest"
        };

    private static string FormatCoreKind(MihomoCoreReleaseKind kind) =>
        kind switch
        {
            MihomoCoreReleaseKind.Preview => "预览版",
            MihomoCoreReleaseKind.Smart => "Smart",
            MihomoCoreReleaseKind.Specific => "指定版本",
            _ => "最新版"
        };

    private static bool IsDefaultDelayTestUrl(string? url) =>
        string.IsNullOrWhiteSpace(url) ||
        string.Equals(url.Trim(), "https://www.gstatic.com/generate_204", StringComparison.OrdinalIgnoreCase);

    private static string Md5Hex(string value)
    {
        var bytes = MD5.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static (string Host, string Port) SplitController(string controller)
    {
        var normalized = controller.Trim();
        if (normalized.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            normalized.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            normalized = new Uri(normalized).Authority;
        }

        var index = normalized.LastIndexOf(':');
        if (index < 0 || index == normalized.Length - 1)
        {
            return (NormalizeControllerHost(normalized), "9090");
        }

        return (NormalizeControllerHost(normalized[..index]), normalized[(index + 1)..]);
    }

    private static string NormalizeControllerHost(string host) =>
        string.Equals(host, "0.0.0.0", StringComparison.OrdinalIgnoreCase) ? "127.0.0.1" : host;
}
