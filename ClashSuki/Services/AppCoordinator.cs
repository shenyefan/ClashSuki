using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Collections.Concurrent;
using ClashSuki.Models;
using ClashSuki.Stores;
using ClashSuki.Utilities;

namespace ClashSuki.Services;

public sealed class AppCoordinator : IAsyncDisposable
{
    private readonly MihomoApiClient _api = new();
    private readonly MihomoCoreManager _core = new();
    private readonly MihomoCoreDownloadService _coreDownloader = new();
    private readonly MihomoServiceManager _serviceManager = new();
    private readonly WindowsSystemProxyService _systemProxy = new();
    private readonly OverrideService _overrideService = new();
    private readonly RuntimeConfigService _runtimeConfig;
    private readonly GistSyncService _gistSync = new();
    private readonly MihomoWsClient _ws = new();
    private readonly ProfileAutoUpdateService _profileAutoUpdate;
    private readonly OverrideAutoUpdateService _overrideAutoUpdate;
    private readonly UiDispatcher _dispatcher;
    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _systemProxyLock = new(1, 1);
    private readonly SemaphoreSlim _tunLock = new(1, 1);
    private readonly SemaphoreSlim _allowLanLock = new(1, 1);
    private readonly SemaphoreSlim _modeLock = new(1, 1);
    private readonly SemaphoreSlim _configMutationLock = new(1, 1);
    private readonly TargetTransitionState<bool> _systemProxyTargets = new();
    private readonly TargetTransitionState<bool> _tunTargets = new();
    private readonly TargetTransitionState<bool> _allowLanTargets = new();
    private readonly TargetTransitionState<string> _modeTargets = new();
    private readonly ConcurrentQueue<(string Level, string Message)> _mihomoLogQueue = new();
    private DateTime _lastCoreRecoverAttempt = DateTime.MinValue;
    private bool _systemProxyTransitionInProgress;
    private bool _tunTransitionInProgress;
    private bool _allowLanTransitionInProgress;
    private bool _modeTransitionInProgress;
    private int? _activeSystemProxyPort;
    private bool _desiredSystemProxyEnabled;
    private Task? _runtimeLoopTask;
    private Task? _proxyLoopTask;
    private Task? _rulesLoopTask;
    private bool _startupPrepared;
    private bool _startupStarted;
    private bool _ssidDirectActive;
    private bool _ssidDnsDisabled;
    private bool _ssidDnsEnabledBeforeDirect = true;
    private string _ssidModeBeforeDirect = "rule";
    private DateTime _lastSsidCheck = DateTime.MinValue;
    private int _disposeStarted;
    private int _mihomoLogFlushScheduled;

    public AppCoordinator(
        UiDispatcher dispatcher,
        RuntimeStore runtime,
        ProxyStore proxies,
        ConnectionStore connections,
        TrafficStatisticsStore trafficStatistics,
        RuleStore rules,
        ProfileStore profiles,
        ProfileService profileService,
        LogStore logs)
    {
        _dispatcher = dispatcher;
        Runtime = runtime;
        Proxies = proxies;
        Connections = connections;
        TrafficStatistics = trafficStatistics;
        Rules = rules;
        Profiles = profiles;
        Logs = logs;
        var overrideRuntime = new OverrideRuntimeService(_overrideService);
        _runtimeConfig = new RuntimeConfigService(Profiles, overrideRuntime, _core);
        _profileAutoUpdate = new ProfileAutoUpdateService(
            profileService,
            (uid, _) => UpdateProfileAsync(uid));
        _overrideAutoUpdate = new OverrideAutoUpdateService(
            _overrideService,
            AutoUpdateOverrideAsync);

        _core.AppLogReceived += (level, message) =>
            _ = _dispatcher.RunAsync(() => Logs.AddApp(level, message, LogSources.Core));
        DiagnosticLog.AppEntryWritten += OnAppEntryWritten;
        _ws.LogReceived += QueueMihomoLog;
        _ws.TrafficReceived += (up, down) => _ = _dispatcher.RunAsync(() =>
        {
            Runtime.ApplyTraffic(up, down);
            TrafficStatistics.ApplyTraffic(up, down);
        });
        _ws.MemoryReceived += inUse => _ = _dispatcher.RunAsync(() => Runtime.ApplyMemory(inUse));
        _ws.ConnectionsReceived += snapshot => _ = _dispatcher.RunAsync(() =>
        {
            Runtime.ApplyTotals(snapshot.UploadTotal, snapshot.DownloadTotal);
            TrafficStatistics.ApplyConnections(snapshot);
            Connections.Apply(snapshot);
        });
    }

    public RuntimeStore Runtime { get; }
    public ProxyStore Proxies { get; }
    public ConnectionStore Connections { get; }
    public TrafficStatisticsStore TrafficStatistics { get; }
    public RuleStore Rules { get; }
    public ProfileStore Profiles { get; }
    public LogStore Logs { get; }
    private void OnAppEntryWritten(DiagnosticLogEntry entry)
    {
        if (Volatile.Read(ref _disposeStarted) != 0)
        {
            return;
        }

        _ = AddPersistedAppEntryAsync(entry);
    }

    private async Task AddPersistedAppEntryAsync(DiagnosticLogEntry entry)
    {
        try
        {
            await _dispatcher.RunAsync(() => Logs.AddPersistedApp(entry));
        }
        catch
        {
            // 文件日志已写入；界面关闭时无需继续更新日志列表。
        }
    }

    private void QueueMihomoLog(string level, string message)
    {
        _mihomoLogQueue.Enqueue((level, message));
        if (Interlocked.Exchange(ref _mihomoLogFlushScheduled, 1) == 0)
        {
            _ = FlushMihomoLogsAsync();
        }
    }

    private async Task FlushMihomoLogsAsync()
    {
        try
        {
            await Task.Delay(50, _cts.Token);
            var batch = new List<(string Level, string Message)>();
            while (_mihomoLogQueue.TryDequeue(out var entry))
            {
                batch.Add(entry);
            }

            if (batch.Count > 0)
            {
                await _dispatcher.RunAsync(() => Logs.AddMihomoBatch(batch));
            }
        }
        catch (OperationCanceledException) when (_cts.IsCancellationRequested)
        {
            // normal shutdown
        }
        catch (Exception ex)
        {
            DiagnosticLog.WriteAppException("MIHOMO-LOG-BATCH", ex);
        }
        finally
        {
            Interlocked.Exchange(ref _mihomoLogFlushScheduled, 0);
            if (!_mihomoLogQueue.IsEmpty &&
                Interlocked.Exchange(ref _mihomoLogFlushScheduled, 1) == 0)
            {
                _ = FlushMihomoLogsAsync();
            }
        }
    }

    public async Task StartAsync()
    {
        if (_startupStarted)
        {
            return;
        }

        _startupStarted = true;
        await PrepareForWindowAsync();

        _ws.Start(_cts.Token);
        var desiredTunEnabled = Runtime.IsTunEnabled;

        try
        {
            var serviceStatus = await _serviceManager.GetStatusAsync(_cts.Token);
            await _dispatcher.RunAsync(() => Runtime.ApplyTunCapability(serviceStatus));
            await _core.EnsureStartedAsync(desiredTunEnabled, _cts.Token);
            serviceStatus = await _serviceManager.GetStatusAsync(_cts.Token);
            await _dispatcher.RunAsync(() => Runtime.ApplyTunCapability(serviceStatus));
            await ReconcileTunStateAfterStartupAsync(desiredTunEnabled, _cts.Token);

            await RestoreDesiredSystemProxyAsync(_cts.Token);
            await RefreshRuntimeAsync(_cts.Token);
        }
        catch (Exception ex) when (!IsAppCancellation(ex))
        {
            await SyncSwitchStatesFromConfigAsync(_cts.Token);

            try
            {
                if (_core.RunMode == CoreRunMode.NotRunning && !_core.IsRunning)
                {
                    if (desiredTunEnabled)
                    {
                        await ApplyConfigPatchTransactionAsync(
                            new Dictionary<string, object?>
                            {
                                ["tun"] = new Dictionary<string, object?> { ["enable"] = false }
                            },
                            reloadAfterPatch: false);
                        await _dispatcher.RunAsync(() => Runtime.SyncTunEnabled(false));
                    }

                    await _core.EnsureStartedAsync(requireTun: false, _cts.Token);
                }

                await RefreshRuntimeAsync(_cts.Token);
            }
            catch (Exception fallbackEx) when (!IsAppCancellation(fallbackEx))
            {
                DiagnosticLog.WriteAppException("CORE-STARTUP-FALLBACK", fallbackEx);
            }

            if (string.Equals(Runtime.ConnectionText, "已连接", StringComparison.Ordinal))
            {
                await _dispatcher.RunAsync(() =>
                {
                    Runtime.Notifications.Warning(
                        "虚拟网卡不可用，内核已以普通模式运行（系统代理仍可用）",
                        source: LogSources.Tun,
                        exception: ex);
                });
                _ = SafeVoid(RestoreDesiredSystemProxyAsync(_cts.Token), "SYSTEM-PROXY-STARTUP");
            }
            else
            {
                await _dispatcher.RunAsync(() =>
                {
                    Runtime.CoreStatusText = "内核启动失败";
                    Runtime.Notifications.Error(
                        "内核启动失败",
                        source: LogSources.Core,
                        exception: ex);
                });

                _ = SafeVoid(RestoreDesiredSystemProxyAsync(_cts.Token), "SYSTEM-PROXY-STARTUP");
            }
        }

        _runtimeLoopTask = RunRuntimeLoopAsync(_cts.Token);
        _proxyLoopTask = RunProxyLoopAsync(_cts.Token);
        _rulesLoopTask = RunRulesLoopAsync(_cts.Token);
        _profileAutoUpdate.Start(_cts.Token);
        _overrideAutoUpdate.Start(_cts.Token);
    }

    public async Task PrepareForWindowAsync()
    {
        if (_startupPrepared)
        {
            return;
        }

        var settings = await AppSettingsService.LoadAsync(_cts.Token);
        if (!settings.OfflineDnsDefaultMigrated)
        {
            await YamlConfigService.DisableGeoIpForLegacyFactoryDnsAsync(
                AppPaths.BaseConfigPath,
                _cts.Token);
            await AppSettingsService.PatchAsync(
                value => value.OfflineDnsDefaultMigrated = true,
                _cts.Token);
            settings.OfflineDnsDefaultMigrated = true;
        }

        _desiredSystemProxyEnabled = settings.SystemProxyEnabled;
        await Profiles.LoadAsync(_cts.Token);
        ApplyCoreWorkDirectory(Profiles.ActiveUid, settings);
        await RebuildRuntimeFromSourcesAsync(_cts.Token);
        await SyncSwitchStatesFromConfigAsync(_cts.Token);
        await ReconcileStaleSystemProxyAsync(settings, _cts.Token);
        await ApplyApiEndpointFromConfigAsync(_cts.Token);
        await _dispatcher.RunAsync(() =>
        {
            SyncSystemProxyUiFromState(settings);
            Proxies.SetGlobalOnly(Runtime.IsGlobalMode);
        });
        _startupPrepared = true;
    }

    private async Task RebuildRuntimeFromSourcesAsync(CancellationToken cancellationToken)
    {
        await _runtimeConfig.RebuildAsync(cancellationToken);
    }

    public async Task RefreshNowAsync() => await RefreshRuntimeAsync(_cts.Token);

    public async Task SwitchModeAsync(string mode)
    {
        if (string.IsNullOrWhiteSpace(mode))
        {
            return;
        }

        var normalizedMode = mode.Trim().ToLowerInvariant();
        _modeTargets.Queue(normalizedMode);
        await _dispatcher.RunAsync(() => Runtime.CurrentMode = normalizedMode);

        if (!await _modeLock.WaitAsync(0, _cts.Token))
        {
            return;
        }

        _modeTransitionInProgress = true;
        string? queuedTarget = null;

        try
        {
            while (_modeTargets.TryTake(out var target))
            {
                _modeTargets.SetVisible(target);
                try
                {
                    var patch = new Dictionary<string, object?> { ["mode"] = target };
                    await ApplyConfigPatchTransactionAsync(patch, reloadAfterPatch: false);
                    await CloseConnectionsAfterSwitchIfNeededAsync();
                    await RefreshRuntimeAsync(_cts.Token);
                }
                catch (Exception ex) when (!IsAppCancellation(ex))
                {
                    await SyncSwitchStatesFromRealityAsync(_cts.Token);
                    await _dispatcher.RunAsync(() =>
                    {
                        Runtime.Notifications.Error(
                            "模式切换失败",
                            source: LogSources.Core,
                            exception: ex);
                    });
                }
            }
        }
        finally
        {
            _modeTransitionInProgress = false;
            _modeLock.Release();
            if (!_modeTargets.TryPeek(out queuedTarget))
            {
                _modeTargets.ClearVisible();
            }
        }

        if (!string.IsNullOrWhiteSpace(queuedTarget))
        {
            await SwitchModeAsync(queuedTarget);
        }
        else
        {
            await RefreshRuntimeAsync(_cts.Token);
        }
    }

    private async Task PatchConfigOrThrowAsync(
        Dictionary<string, object?> patch,
        IReadOnlySet<string>? replaceRootMappings = null)
    {
        await ApplyConfigPatchTransactionAsync(patch, reloadAfterPatch: false, replaceRootMappings);
    }

    public async Task SaveDnsSettingsAsync(YamlConfigService.DnsSectionSettings settings)
    {
        var hosts = settings.Hosts.ToDictionary(
            pair => pair.Key,
            pair => pair.Value.Count == 1 ? (object?)pair.Value[0] : pair.Value,
            StringComparer.OrdinalIgnoreCase);
        var patch = settings.OverrideEnabled
            ? new Dictionary<string, object?>
        {
            ["dns"] = new Dictionary<string, object?>
            {
                ["enable"] = true,
                ["enhanced-mode"] = settings.EnhancedMode,
                ["ipv6"] = settings.Ipv6,
                ["respect-rules"] = settings.RespectRules,
                ["use-hosts"] = settings.UseHosts,
                ["use-system-hosts"] = settings.UseSystemHosts,
                ["fake-ip-range"] = settings.FakeIpRange,
                ["fake-ip-filter"] = settings.FakeIpFilter,
                ["fake-ip-filter-mode"] = settings.FakeIpFilterMode,
                ["nameserver"] = settings.Nameserver,
                ["fallback"] = settings.Fallback,
                ["default-nameserver"] = settings.DefaultNameserver,
                ["direct-nameserver"] = settings.DirectNameserver,
                ["proxy-server-nameserver"] = settings.ProxyServerNameserver,
                ["fallback-filter"] = new Dictionary<string, object?>
                {
                    ["geoip"] = settings.FallbackGeoIp,
                    ["geoip-code"] = settings.FallbackGeoIpCode,
                    ["ipcidr"] = settings.FallbackIpCidr,
                    ["domain"] = settings.FallbackDomain
                }
            },
            ["hosts"] = hosts
        }
            : [];
        var previous = (await AppSettingsService.LoadAsync(_cts.Token)).DnsOverrideEnabled;
        await AppSettingsService.PatchAsync(value => value.DnsOverrideEnabled = settings.OverrideEnabled, _cts.Token);
        try
        {
            await ApplyConfigPatchTransactionAsync(
                patch,
                reloadAfterPatch: true,
                settings.OverrideEnabled
                    ? new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "hosts" }
                    : null);
        }
        catch
        {
            await AppSettingsService.PatchAsync(value => value.DnsOverrideEnabled = previous, CancellationToken.None);
            throw;
        }
    }

    public async Task SelectNodeAsync(string group, string node)
    {
        var groupVm = Proxies.FindGroup(group);
        var previousNode = groupVm?.CurrentNode;
        if (groupVm is not null)
        {
            groupVm.CurrentNode = node;
            foreach (var n in groupVm.Nodes)
            {
                n.IsSelected = n.Name.Equals(node, StringComparison.OrdinalIgnoreCase);
            }
        }

        try
        {
            await _api.SwitchProxyAsync(group, node, _cts.Token);
            await CloseConnectionsAfterSwitchIfNeededAsync();
        }
        catch (Exception ex) when (!IsAppCancellation(ex))
        {
            await _dispatcher.RunAsync(() =>
            {
                if (groupVm is not null && previousNode is not null)
                {
                    groupVm.CurrentNode = previousNode;
                    foreach (var item in groupVm.Nodes)
                    {
                        item.IsSelected = item.Name.Equals(previousNode, StringComparison.OrdinalIgnoreCase);
                    }
                }

                Runtime.Notifications.Error(
                    "节点切换失败",
                    source: LogSources.Proxy,
                    exception: ex);
            });
        }

        await RefreshProxiesAsync(_cts.Token);
    }

    public async Task<bool> AddProfileAsync(
        string name,
        string url,
        string? userAgent,
        string? authToken,
        string? ageSecretKey,
        string? fileName)
    {
        try
        {
            var hadActiveProfile = !string.IsNullOrWhiteSpace(Profiles.ActiveUid);
            var profile = await Profiles.AddRemoteAsync(
                name,
                url,
                userAgent,
                authToken,
                ageSecretKey,
                fileName,
                Runtime.MixedPortNumber > 0 ? Runtime.MixedPortNumber : null,
                _cts.Token);

            if (!hadActiveProfile && !await ActivateProfileAsync(profile.Uid, reportResult: false))
            {
                await Profiles.DeleteAsync(profile.Uid, CancellationToken.None);
                throw new InvalidOperationException("订阅配置未能通过校验并激活");
            }

            await _dispatcher.RunAsync(() =>
                Logs.AddApp("INFO", $"订阅已添加：{profile.Name}", LogSources.Subscription));
            return true;
        }
        catch (Exception ex) when (!IsAppCancellation(ex))
        {
            await _dispatcher.RunAsync(() =>
            {
                Runtime.Notifications.Error(
                    "订阅添加失败",
                    source: LogSources.Subscription,
                    exception: ex);
            });
            return false;
        }
    }

    private async Task CloseConnectionsAfterSwitchIfNeededAsync()
    {
        var settings = await AppSettingsService.LoadAsync(_cts.Token);
        if (!settings.AutoCloseConnection)
        {
            return;
        }

        try
        {
            await _api.CloseAllConnectionsAsync(_cts.Token);
        }
        catch (Exception ex) when (!IsAppCancellation(ex))
        {
            DiagnosticLog.WriteAppException(
                LogSources.Connection,
                ex,
                "切换已生效，但关闭旧连接失败",
                "WARN");
        }
    }

    public async Task<bool> UpdateProfileSettingsAsync(
        string uid,
        string name,
        string? url,
        string? userAgent,
        string? authToken,
        string? ageSecretKey,
        string? fileName,
        int? updateIntervalMinutes,
        bool autoUpdate)
    {
        try
        {
            await Profiles.UpdateSettingsAsync(
                uid,
                name,
                url,
                userAgent,
                authToken,
                ageSecretKey,
                fileName,
                updateIntervalMinutes,
                autoUpdate,
                _cts.Token);
            await _dispatcher.RunAsync(() =>
                Logs.AddApp("INFO", "订阅信息已保存", LogSources.Subscription));
            return true;
        }
        catch (Exception ex) when (!IsAppCancellation(ex))
        {
            await _dispatcher.RunAsync(() =>
            {
                Runtime.Notifications.Error(
                    "订阅设置保存失败",
                    source: LogSources.Subscription,
                    exception: ex);
            });
            return false;
        }
    }

    public async Task<bool> ImportLocalProfileAsync(string name, string fileName, string content)
    {
        try
        {
            var hadActiveProfile = !string.IsNullOrWhiteSpace(Profiles.ActiveUid);
            var profile = await Profiles.ImportLocalAsync(name, fileName, content, _cts.Token);

            if (!hadActiveProfile && !await ActivateProfileAsync(profile.Uid, reportResult: false))
            {
                await Profiles.DeleteAsync(profile.Uid, CancellationToken.None);
                throw new InvalidOperationException("本地配置未能通过校验并激活");
            }

            await _dispatcher.RunAsync(() =>
                Logs.AddApp("INFO", $"本地配置已导入：{profile.Name}", LogSources.Subscription));
            return true;
        }
        catch (Exception ex) when (!IsAppCancellation(ex))
        {
            await _dispatcher.RunAsync(() =>
            {
                Runtime.Notifications.Error(
                    "本地配置导入失败",
                    source: LogSources.Subscription,
                    exception: ex);
            });
            return false;
        }
    }

    public async Task UpdateProfileAsync(string uid)
    {
        ProfileStore.UpdateSnapshot? snapshot = null;
        try
        {
            snapshot = await Profiles.CaptureUpdateSnapshotAsync(uid, _cts.Token);
            var profile = await Profiles.UpdateAsync(
                uid,
                Runtime.MixedPortNumber > 0 ? Runtime.MixedPortNumber : null,
                _cts.Token);
            if (profile.Uid == Profiles.ActiveUid)
            {
                if (!await ActivateProfileAsync(uid, reportResult: false))
                {
                    throw new InvalidOperationException("新订阅配置未能激活");
                }
            }

            await _dispatcher.RunAsync(() =>
                Logs.AddApp("INFO", $"订阅已更新：{profile.Name}", LogSources.Subscription));
        }
        catch (Exception ex) when (!IsAppCancellation(ex))
        {
            if (snapshot is not null)
            {
                try
                {
                    await Profiles.RestoreUpdateSnapshotAsync(snapshot, CancellationToken.None);
                    if (snapshot.Item.Uid == Profiles.ActiveUid)
                    {
                        await ActivateProfileAsync(snapshot.Item.Uid, reportResult: false);
                    }
                }
                catch (Exception rollbackEx)
                {
                    DiagnosticLog.WriteAppException("PROFILE-UPDATE-ROLLBACK", rollbackEx);
                }
            }

            await _dispatcher.RunAsync(() =>
            {
                Runtime.Notifications.Error(
                    "订阅更新失败",
                    source: LogSources.Subscription,
                    exception: ex);
            });
        }
    }

    public async Task<bool> ActivateProfileAsync(string uid, bool reportResult = true)
    {
        var previousUid = Profiles.ActiveUid;
        try
        {
            var settings = await AppSettingsService.LoadAsync(_cts.Token);
            ApplyCoreWorkDirectory(uid, settings);
            await Profiles.SetActiveAsync(uid, _cts.Token);
            await RebuildAndApplyRuntimeAsync(
                startIfStopped: true,
                settings.UseHotReloadProfile && !settings.DiffWorkDir,
                settings.HotReloadProfileAutoCloseConnection);
            await ApplyApiEndpointFromConfigAsync(_cts.Token);
            await RefreshRuntimeAsync(_cts.Token);
            await RefreshProxiesAsync(_cts.Token);
            await RefreshRulesAsync(_cts.Token);
            await SyncRuntimeConfigToGistIfEnabledAsync();
            if (reportResult)
            {
                await _dispatcher.RunAsync(() =>
                    Logs.AddApp("INFO", "订阅已启用", LogSources.Subscription));
            }

            return true;
        }
        catch (Exception ex) when (!IsAppCancellation(ex))
        {
            if (!string.Equals(previousUid, Profiles.ActiveUid, StringComparison.Ordinal))
            {
                try
                {
                    await Profiles.RestoreActiveAsync(previousUid, CancellationToken.None);
                }
                catch (Exception rollbackEx)
                {
                    DiagnosticLog.WriteAppException(
                        LogSources.Subscription,
                        rollbackEx,
                        "恢复原订阅状态失败");
                }
            }

            try
            {
                var rollbackSettings = await AppSettingsService.LoadAsync(CancellationToken.None);
                ApplyCoreWorkDirectory(previousUid, rollbackSettings);
            }
            catch (Exception rollbackEx)
            {
                DiagnosticLog.WriteAppException(
                    LogSources.Subscription,
                    rollbackEx,
                    "恢复内核工作目录失败");
            }

            if (reportResult)
            {
                await _dispatcher.RunAsync(() =>
                {
                    Runtime.Notifications.Error(
                        "订阅启用失败",
                        source: LogSources.Subscription,
                        exception: ex);
                });
            }

            return false;
        }
    }

    public int? GetMixedPortForDownload() =>
        Runtime.MixedPortNumber > 0 ? Runtime.MixedPortNumber : null;

    public async Task RefreshOverrideRemoteAsync(
        OverrideConfig config,
        OverrideEntry entry,
        CancellationToken cancellationToken = default)
    {
        await _overrideService.RefreshRemoteAsync(
            config,
            entry,
            GetMixedPortForDownload(),
            cancellationToken);
    }

    public async Task<OverrideApplyResult> ApplyOverridesAsync()
    {
        var settings = await AppSettingsService.LoadAsync(_cts.Token);
        ApplyCoreWorkDirectory(Profiles.ActiveUid, settings);
        var result = await RebuildAndApplyRuntimeAsync(
            startIfStopped: true,
            settings.UseHotReloadProfile && !settings.DiffWorkDir,
            settings.HotReloadProfileAutoCloseConnection);
        await ApplyApiEndpointFromConfigAsync(_cts.Token);
        await RefreshRuntimeAsync(_cts.Token);
        await RefreshProxiesAsync(_cts.Token);
        await RefreshRulesAsync(_cts.Token);
        await SyncRuntimeConfigToGistIfEnabledAsync();
        return result;
    }

    private async Task<OverrideApplyResult> RebuildAndApplyRuntimeAsync(
        bool startIfStopped,
        bool useHotReload,
        bool closeConnectionsBeforeHotReload)
    {
        var snapshot = await ConfigFileSnapshot.CaptureAsync(
            [AppPaths.BaseConfigPath, AppPaths.RuntimeConfigPath],
            _cts.Token);
        var previousRuntime = snapshot.GetContent(AppPaths.RuntimeConfigPath);
        var coreWasRunning = _core.RunMode != CoreRunMode.NotRunning || _core.IsRunning;
        var previousTunEnabled = previousRuntime is not null &&
                                 YamlConfigService.IsTunEnabled(previousRuntime);

        try
        {
            var result = await _runtimeConfig.RebuildAsync(_cts.Token);
            _cachedGroupOrder = null;
            var requireTun = await YamlConfigService.IsTunEnabledAsync(
                AppPaths.RuntimeConfigPath,
                _cts.Token);

            if (_core.RunMode != CoreRunMode.NotRunning && useHotReload)
            {
                try
                {
                    if (closeConnectionsBeforeHotReload)
                    {
                        await _api.CloseAllConnectionsAsync(_cts.Token);
                    }

                    await _api.ReloadConfigAsync(AppPaths.RuntimeConfigPath, _cts.Token);
                }
                catch (Exception ex) when (!IsAppCancellation(ex))
                {
                    await _core.RestartAsync(requireTun, _cts.Token);
                }
            }
            else if (_core.RunMode != CoreRunMode.NotRunning)
            {
                await _core.RestartAsync(requireTun, _cts.Token);
            }
            else if (startIfStopped)
            {
                await _core.EnsureStartedAsync(requireTun, _cts.Token);
            }

            return result;
        }
        catch
        {
            await snapshot.RestoreAsync();
            _cachedGroupOrder = null;
            if (coreWasRunning && previousRuntime is not null)
            {
                try
                {
                    await _api.ReloadConfigAsync(AppPaths.RuntimeConfigPath, CancellationToken.None);
                }
                catch
                {
                    await _core.RestartAsync(previousTunEnabled, CancellationToken.None);
                }
            }

            throw;
        }
    }

    private async Task ReloadCurrentConfigAsync()
    {
        if (_core.RunMode == CoreRunMode.NotRunning)
        {
            return;
        }

        var requireTun = await YamlConfigService.IsTunEnabledAsync(AppPaths.RuntimeConfigPath, _cts.Token);
        try
        {
            await MihomoControllerEndpoint.PrepareRuntimeConfigForCoreAsync(_cts.Token);
            await _api.ReloadConfigAsync(AppPaths.RuntimeConfigPath, _cts.Token);
        }
        catch (Exception ex) when (!IsAppCancellation(ex))
        {
            await _core.RestartAsync(requireTun, _cts.Token);
        }
    }

    public async Task DeleteProfileAsync(string uid)
    {
        try
        {
            var wasActive = string.Equals(uid, Profiles.ActiveUid, StringComparison.Ordinal);
            await Profiles.DeleteAsync(uid, _cts.Token);
            if (wasActive && !string.IsNullOrWhiteSpace(Profiles.ActiveUid))
            {
                await ActivateProfileAsync(Profiles.ActiveUid, reportResult: false);
            }

            await _dispatcher.RunAsync(() =>
                Logs.AddApp("INFO", "订阅已删除", LogSources.Subscription));
        }
        catch (Exception ex) when (!IsAppCancellation(ex))
        {
            await _dispatcher.RunAsync(() =>
            {
                Runtime.Notifications.Error(
                    "订阅删除失败",
                    source: LogSources.Subscription,
                    exception: ex);
            });
        }
    }

    public async Task OpenExternalFileAsync(string path, string label)
    {
        try
        {
            await OpenExternalFileOrThrowAsync(path, label);
        }
        catch (Exception ex) when (!IsAppCancellation(ex))
        {
            await _dispatcher.RunAsync(() =>
            {
                Runtime.Notifications.Error(
                    $"打开{label}失败",
                    source: LogSources.Subscription,
                    exception: ex);
            });
        }
    }

    public string GetProfileFilePath(string uid) => Profiles.GetProfileFilePath(uid);

    private static Task OpenExternalFileOrThrowAsync(string path, string label)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new FileNotFoundException($"{label}不存在", path);
        }

        var isUri = Uri.TryCreate(path, UriKind.Absolute, out var uri) &&
                    uri.Scheme is "http" or "https";
        if (!isUri && !File.Exists(path) && !Directory.Exists(path))
        {
            throw new FileNotFoundException($"{label}不存在", path);
        }

        Process.Start(new ProcessStartInfo
        {
            FileName = path,
            UseShellExecute = true
        });
        return Task.CompletedTask;
    }

    public async Task<string?> ReadProfileFileAsync(string uid)
    {
        try
        {
            var path = Profiles.GetProfileFilePath(uid);
            if (!File.Exists(path))
            {
                throw new FileNotFoundException("配置文件不存在", path);
            }

            return await File.ReadAllTextAsync(path, _cts.Token);
        }
        catch (Exception ex) when (!IsAppCancellation(ex))
        {
            await _dispatcher.RunAsync(() =>
            {
                Runtime.Notifications.Error(
                    "读取配置文件失败",
                    source: LogSources.Subscription,
                    exception: ex);
            });
            return null;
        }
    }

    public async Task<bool> ImportLocalProfileFileAsync(string name, string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                throw new FileNotFoundException("本地配置文件不存在", path);
            }

            var content = await File.ReadAllTextAsync(path, _cts.Token);
            return await ImportLocalProfileAsync(
                string.IsNullOrWhiteSpace(name) ? Path.GetFileNameWithoutExtension(path) : name,
                Path.GetFileName(path),
                content);
        }
        catch (Exception ex) when (!IsAppCancellation(ex))
        {
            await _dispatcher.RunAsync(() =>
            {
                Runtime.Notifications.Error(
                    "本地配置导入失败",
                    source: LogSources.Subscription,
                    exception: ex);
            });
            return false;
        }
    }

    public async Task<bool> SaveProfileFileAsync(string uid, string content)
    {
        string? previousContent = null;
        string? path = null;
        try
        {
            path = Profiles.GetProfileFilePath(uid);
            previousContent = File.Exists(path)
                ? await File.ReadAllTextAsync(path, _cts.Token)
                : null;

            var validationPath = Path.Combine(
                Path.GetDirectoryName(path)!,
                $".{Path.GetFileName(path)}.validate.tmp");
            await File.WriteAllTextAsync(
                validationPath,
                YamlConfigService.EnsureGlobalConfig(content),
                _cts.Token);
            try
            {
                await _core.ValidateConfigAsync(validationPath, _cts.Token);
            }
            finally
            {
                try
                {
                    if (File.Exists(validationPath))
                    {
                        File.Delete(validationPath);
                    }
                }
                catch (Exception cleanupEx)
                {
                    DiagnosticLog.WriteAppException("PROFILE-EDIT-TEMP-CLEANUP", cleanupEx);
                }
            }

            await File.WriteAllTextAsync(path, content, _cts.Token);

            if (uid == Profiles.ActiveUid)
            {
                var activated = await ActivateProfileAsync(uid, reportResult: false);
                if (!activated)
                {
                    throw new InvalidOperationException("编辑后的配置未能应用");
                }
            }

            await _dispatcher.RunAsync(() =>
                Logs.AddApp("INFO", "订阅配置文件已保存", LogSources.Subscription));
            return true;
        }
        catch (Exception ex) when (!IsAppCancellation(ex))
        {
            if (path is not null && previousContent is not null)
            {
                try
                {
                    await File.WriteAllTextAsync(path, previousContent, CancellationToken.None);
                    if (uid == Profiles.ActiveUid)
                    {
                        await ActivateProfileAsync(uid, reportResult: false);
                    }
                }
                catch (Exception rollbackEx)
                {
                    DiagnosticLog.WriteAppException("PROFILE-EDIT-ROLLBACK", rollbackEx);
                }
            }

            await _dispatcher.RunAsync(() =>
            {
                Runtime.Notifications.Error(
                    "保存配置文件失败",
                    source: LogSources.Subscription,
                    exception: ex);
            });
            return false;
        }
    }

    public async Task ToggleSystemProxyAsync()
    {
        await SetSystemProxyAsync(!Runtime.IsSystemProxyEnabled);
    }

    public async Task SetSystemProxyAsync(bool enabled)
    {
        _systemProxyTargets.Queue(enabled);
        await _dispatcher.RunAsync(() => Runtime.SyncSystemProxyEnabled(enabled));

        if (!await _systemProxyLock.WaitAsync(0, _cts.Token))
        {
            return;
        }

        _systemProxyTransitionInProgress = true;
        bool? queuedTarget = null;

        try
        {
            while (_systemProxyTargets.TryTake(out var target))
            {
                _systemProxyTargets.SetVisible(target);
                await ApplySystemProxyTargetAsync(target);
            }
        }
        finally
        {
            _systemProxyTransitionInProgress = false;
            _systemProxyLock.Release();
            if (_systemProxyTargets.TryPeek(out var queued))
            {
                queuedTarget = queued;
            }
            else
            {
                _systemProxyTargets.ClearVisible();
            }
        }

        if (queuedTarget.HasValue)
        {
            await SetSystemProxyAsync(queuedTarget.Value);
        }
        else
        {
            await SyncSwitchStatesFromRealityAsync(_cts.Token);
        }
    }

    private async Task ApplySystemProxyTargetAsync(bool enabled)
    {
        try
        {
            if (enabled)
            {
                var config = await Safe(
                    _api.GetConfigsAsync(TimeSpan.FromSeconds(2), _cts.Token),
                    "SYSTEM-PROXY-CONFIG",
                    LogSources.SystemProxy,
                    "读取系统代理对应的内核配置失败");
                if (config is null)
                {
                    throw new InvalidOperationException("mihomo 内核尚未就绪，无法开启系统代理");
                }

                await _dispatcher.RunAsync(() =>
                {
                    Runtime.ApplyConnected(null, config, _core.RunMode, _core.ProcessId, syncTun: !_tunTransitionInProgress);
                    KeepPendingSwitchTargetsVisible();
                });
            }

            if (enabled && Runtime.MixedPortNumber <= 0)
            {
                throw new InvalidOperationException("混合端口不可用，无法开启系统代理");
            }

            var mixedPort = Runtime.MixedPortNumber;
            var settings = await AppSettingsService.LoadAsync(_cts.Token);
            await Task.Run(() =>
            {
                if (enabled)
                {
                    _systemProxy.Enable(mixedPort, settings);
                    _activeSystemProxyPort = mixedPort;
                }
                else
                {
                    if (_systemProxy.IsEnabledFor(mixedPort, settings))
                    {
                        _systemProxy.Disable();
                    }
                    _activeSystemProxyPort = null;
                }
            }, _cts.Token);

            _desiredSystemProxyEnabled = enabled;
            await AppSettingsService.SetSystemProxyEnabledAsync(enabled, _cts.Token);

            await _dispatcher.RunAsync(() =>
            {
                Runtime.SyncSystemProxyEnabled(enabled);
                Logs.AddApp(
                    "INFO",
                    enabled ? "系统代理已开启" : "系统代理已关闭",
                    LogSources.SystemProxy);
            });
        }
        catch (Exception ex) when (!IsAppCancellation(ex))
        {
            var settings = await AppSettingsService.LoadAsync(_cts.Token);
            var actual = await Task.Run(() => IsSystemProxyEnabledForApp(settings));
            _desiredSystemProxyEnabled = actual;
            await AppSettingsService.SetSystemProxyEnabledAsync(actual, _cts.Token);
            await _dispatcher.RunAsync(() => Runtime.SyncSystemProxyEnabled(actual));
            TryAttachSystemProxyDiagnostics(ex, Runtime.MixedPortNumber);

            await _dispatcher.RunAsync(() =>
            {
                Runtime.Notifications.Error(
                    "系统代理切换失败",
                    source: LogSources.SystemProxy,
                    exception: ex);
            });
        }
    }

    private void TryAttachSystemProxyDiagnostics(Exception exception, int mixedPort)
    {
        try
        {
            exception.Data["系统代理诊断"] = _systemProxy.GetDetailedDiagnostics(mixedPort);
        }
        catch (Exception diagnosticsException)
        {
            exception.Data["系统代理诊断"] = $"收集失败：{diagnosticsException.Message}";
        }
    }

    private async Task RestoreDesiredSystemProxyAsync(CancellationToken cancellationToken)
    {
        if (!_desiredSystemProxyEnabled)
        {
            return;
        }

        try
        {
            var config = await _api.GetConfigsAsync(TimeSpan.FromSeconds(5), cancellationToken);
            await _dispatcher.RunAsync(() =>
                Runtime.ApplyConnected(null, config, _core.RunMode, _core.ProcessId, syncTun: !_tunTransitionInProgress));

            if (Runtime.MixedPortNumber <= 0)
            {
                throw new InvalidOperationException("混合端口不可用，无法恢复系统代理");
            }

            var mixedPort = Runtime.MixedPortNumber;
            var settings = await AppSettingsService.LoadAsync(cancellationToken);
            await Task.Run(() =>
            {
                _systemProxy.Enable(mixedPort, settings);
                _activeSystemProxyPort = mixedPort;
            }, cancellationToken);

            await _dispatcher.RunAsync(() =>
            {
                Runtime.SyncSystemProxyEnabled(true);
                Logs.AddApp(
                    "INFO",
                    "系统代理已按上次状态恢复",
                    LogSources.SystemProxy);
            });
        }
        catch (Exception ex) when (!IsAppCancellation(ex))
        {
            TryAttachSystemProxyDiagnostics(ex, Runtime.MixedPortNumber);
            await _dispatcher.RunAsync(() =>
            {
                Runtime.SyncSystemProxyEnabled(false);
                Runtime.Notifications.Error(
                    "系统代理自动恢复失败",
                    source: LogSources.SystemProxy,
                    exception: ex);
            });
        }
    }

    private void SyncSystemProxyUiFromState(AppSettings settings)
    {
        if (_systemProxyTransitionInProgress)
        {
            return;
        }

        if (_systemProxyTargets.TryGetVisible(out var visible))
        {
            Runtime.SyncSystemProxyEnabled(visible);
            return;
        }

        Runtime.SyncSystemProxyEnabled(IsSystemProxyEnabledForApp(settings));
    }

    private async Task ReconcileStaleSystemProxyAsync(
        AppSettings settings,
        CancellationToken cancellationToken)
    {
        if (_desiredSystemProxyEnabled || !File.Exists(AppPaths.RuntimeConfigPath))
        {
            return;
        }

        var coreSettings = await YamlConfigService.LoadCoreSettingsAsync(AppPaths.RuntimeConfigPath, cancellationToken);
        if (coreSettings.MixedPort <= 0)
        {
            return;
        }

        var ownsCurrentProxy = await Task.Run(
            () => _systemProxy.IsEnabledFor(coreSettings.MixedPort, settings),
            cancellationToken);
        if (!ownsCurrentProxy)
        {
            return;
        }

        await Task.Run(() => _systemProxy.Disable(), cancellationToken);
        _activeSystemProxyPort = null;
        DiagnosticLog.WriteApp("SYSTEM-PROXY", "启动时已清理 ClashSuki 遗留的系统代理设置");
    }

    private async Task AutoUpdateOverrideAsync(string id, CancellationToken token)
    {
        var config = await _overrideService.LoadAsync(token);
        var entry = config.Items.FirstOrDefault(item => item.Id == id);
        if (entry is null)
        {
            return;
        }

        var previousContent = await _overrideService.ReadContentAsync(entry, token);
        var previousUpdatedAt = entry.UpdatedAt;
        try
        {
            await _overrideService.RefreshRemoteAsync(
                config,
                entry,
                GetMixedPortForDownload(),
                token);
            if (entry.Enabled)
            {
                await ApplyOverridesAsync();
            }

            await _dispatcher.RunAsync(() =>
                Logs.AddApp("INFO", $"覆写自动更新完成，名称: {entry.Name}", LogSources.Override));
        }
        catch (Exception ex) when (!IsAppCancellation(ex))
        {
            try
            {
                entry.UpdatedAt = previousUpdatedAt;
                await _overrideService.WriteContentAsync(entry, previousContent, CancellationToken.None);
                await _overrideService.SaveAsync(config, CancellationToken.None);
            }
            catch (Exception rollbackEx)
            {
                DiagnosticLog.WriteAppException("OVERRIDE-UPDATE-ROLLBACK", rollbackEx);
            }

            DiagnosticLog.WriteAppException(
                LogSources.Override,
                ex,
                $"覆写自动更新失败，名称: {entry.Name}");
        }
    }

    private async Task SyncRuntimeConfigToGistIfEnabledAsync()
    {
        var settings = await AppSettingsService.LoadAsync(_cts.Token);
        if (!settings.SyncRuntimeConfigToGist)
        {
            return;
        }

        try
        {
            var gistId = await _gistSync.SyncRuntimeConfigAsync(settings, _cts.Token);
            if (!string.Equals(settings.GistId, gistId, StringComparison.OrdinalIgnoreCase))
            {
                await AppSettingsService.PatchAsync(item => item.GistId = gistId, _cts.Token);
            }
            await _dispatcher.RunAsync(() =>
                Logs.AddApp("INFO", "运行时配置已同步到 Gist", LogSources.Gist));
        }
        catch (Exception ex) when (!IsAppCancellation(ex))
        {
            await _dispatcher.RunAsync(() =>
            {
                Runtime.Notifications.Warning(
                    "运行时配置同步到 Gist 失败",
                    source: LogSources.Gist,
                    exception: ex);
            });
        }
    }

    private void ApplyCoreWorkDirectory(string uid, AppSettings settings)
    {
        if (!settings.DiffWorkDir || string.IsNullOrWhiteSpace(uid))
        {
            _core.WorkDirectory = AppPaths.DataRoot;
            return;
        }

        var safeUid = string.Concat(uid.Select(ch => Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch));
        _core.WorkDirectory = Path.Combine(AppPaths.DataRoot, "workdirs", safeUid);
    }

    public async Task ApplySavedSettingsSideEffectsAsync(CancellationToken cancellationToken = default)
    {
        var settings = await AppSettingsService.LoadAsync(cancellationToken);
        var previousWorkDirectory = _core.WorkDirectory;
        ApplyCoreWorkDirectory(Profiles.ActiveUid, settings);

        if (!string.Equals(previousWorkDirectory, _core.WorkDirectory, StringComparison.OrdinalIgnoreCase) &&
            (_core.RunMode != CoreRunMode.NotRunning || _core.IsRunning))
        {
            var tunEnabled = await YamlConfigService.IsTunEnabledAsync(
                AppPaths.RuntimeConfigPath,
                cancellationToken);
            await _core.RestartAsync(tunEnabled, cancellationToken);
            await ApplyApiEndpointFromConfigAsync(cancellationToken);
            await RefreshRuntimeAsync(cancellationToken);
        }

        await _core.ApplyPriorityToRunningCoreAsync();
        _lastSsidCheck = DateTime.MinValue;
        await ApplySsidDirectIfNeededAsync(cancellationToken);
    }

    public async Task ReloadRestoredStateAsync(CancellationToken cancellationToken = default)
    {
        await YamlConfigService.NormalizeBaseFileAsync(
            AppPaths.BaseConfigPath,
            cancellationToken);
        var settings = await AppSettingsService.LoadAsync(cancellationToken);
        _desiredSystemProxyEnabled = settings.SystemProxyEnabled;
        await Profiles.LoadAsync(cancellationToken);
        ApplyCoreWorkDirectory(Profiles.ActiveUid, settings);
        await _runtimeConfig.RebuildAsync(cancellationToken);
        await ApplyApiEndpointFromConfigAsync(cancellationToken);

        var tunEnabled = await YamlConfigService.IsTunEnabledAsync(
            AppPaths.RuntimeConfigPath,
            cancellationToken);
        if (_core.RunMode != CoreRunMode.NotRunning || _core.IsRunning)
        {
            await _core.RestartAsync(tunEnabled, cancellationToken);
        }
        else
        {
            await _core.EnsureStartedAsync(tunEnabled, cancellationToken);
        }

        await SyncSwitchStatesFromConfigAsync(cancellationToken);
        await SetSystemProxyAsync(settings.SystemProxyEnabled);
        await RefreshRuntimeAsync(cancellationToken);
        await RefreshProxiesAsync(cancellationToken);
        await RefreshRulesAsync(cancellationToken);
        await _core.ApplyPriorityToRunningCoreAsync();
    }

    public async Task SetTunAsync(bool enabled)
    {
        _tunTargets.Queue(enabled);
        await _dispatcher.RunAsync(() => Runtime.SyncTunEnabled(enabled));

        if (!await _tunLock.WaitAsync(0, _cts.Token))
        {
            return;
        }

        _tunTransitionInProgress = true;
        bool? queuedTarget = null;

        try
        {
            while (_tunTargets.TryTake(out var target))
            {
                _tunTargets.SetVisible(target);
                await ApplyTunTargetAsync(target);
            }
        }
        finally
        {
            _tunTransitionInProgress = false;
            _tunLock.Release();
            if (_tunTargets.TryPeek(out var queued))
            {
                queuedTarget = queued;
            }
            else
            {
                _tunTargets.ClearVisible();
            }
        }

        if (queuedTarget.HasValue)
        {
            await SetTunAsync(queuedTarget.Value);
            return;
        }

        try
        {
            await SyncSwitchStatesFromRealityAsync(_cts.Token);
        }
        catch (Exception ex) when (!IsAppCancellation(ex))
        {
            DiagnosticLog.WriteAppException("TUN-SYNC", ex);
        }
        catch (OperationCanceledException)
        {
            // app is shutting down
        }
    }

    private async Task ApplyTunTargetAsync(bool enabled)
    {
        await _configMutationLock.WaitAsync(_cts.Token);
        try
        {
        var previousTunEnabled = !enabled;
        ConfigFileSnapshot? snapshot = null;
        try
        {
            snapshot = await ConfigFileSnapshot.CaptureAsync(
                [AppPaths.BaseConfigPath, AppPaths.RuntimeConfigPath],
                _cts.Token);
            previousTunEnabled = await YamlConfigService.IsTunEnabledAsync(
                AppPaths.RuntimeConfigPath,
                _cts.Token);

            await _dispatcher.RunAsync(() => Runtime.SyncTunEnabled(enabled));
            var serviceStatus = await _serviceManager.GetStatusAsync(_cts.Token);
            await _dispatcher.RunAsync(() => Runtime.ApplyTunCapability(serviceStatus));

            if (enabled)
            {
                EnsureTunActivationPossible();
                await _core.EnsureStartedAsync(requireTun: true, _cts.Token);
                await WaitForApiReadyAsync("开启虚拟网卡", TimeSpan.FromSeconds(15), _cts.Token);
                await ApplyTunConfigAndVerifyAsync(true, _cts.Token);
            }
            else
            {
                await WaitForApiReadyAsync("关闭虚拟网卡", TimeSpan.FromSeconds(5), _cts.Token);
                await ApplyTunConfigAndVerifyAsync(false, _cts.Token);
                if (_core.RunMode == CoreRunMode.Service)
                {
                    await _core.RestartAsync(requireTun: false, _cts.Token);
                    await WaitForApiReadyAsync("切换到子进程模式", TimeSpan.FromSeconds(15), _cts.Token);
                }
            }

            await RefreshRuntimeAsync(_cts.Token);
            serviceStatus = await _serviceManager.GetStatusAsync(_cts.Token);
            await _dispatcher.RunAsync(() =>
            {
                Runtime.ApplyTunCapability(serviceStatus);
                Logs.AddApp(
                    "INFO",
                    enabled ? "虚拟网卡已开启" : "虚拟网卡已关闭",
                    LogSources.Tun);
            });
        }
        catch (Exception ex) when (!IsAppCancellation(ex))
        {
            await RollBackTunAfterFailureAsync(
                snapshot,
                previousTunEnabled,
                _cts.Token);

            await _dispatcher.RunAsync(() =>
            {
                Runtime.Notifications.Error(
                    "虚拟网卡切换失败",
                    source: LogSources.Tun,
                    exception: ex);
            });

            await RefreshRuntimeAsync(_cts.Token);
            var currentServiceStatus = await _serviceManager.GetStatusAsync(_cts.Token);
            await _dispatcher.RunAsync(() =>
                Runtime.ApplyTunCapability(currentServiceStatus));
        }
        }
        finally
        {
            _configMutationLock.Release();
        }
    }

    public async Task ToggleAllowLanAsync()
    {
        await SetAllowLanAsync(!Runtime.IsAllowLan);
    }

    public async Task SetAllowLanAsync(bool enabled)
    {
        _allowLanTargets.Queue(enabled);
        await _dispatcher.RunAsync(() => Runtime.SyncAllowLan(enabled));

        if (!await _allowLanLock.WaitAsync(0, _cts.Token))
        {
            return;
        }

        _allowLanTransitionInProgress = true;
        bool? queuedTarget = null;

        try
        {
            while (_allowLanTargets.TryTake(out var target))
            {
                _allowLanTargets.SetVisible(target);
                await ApplyAllowLanTargetAsync(target);
            }
        }
        finally
        {
            _allowLanTransitionInProgress = false;
            _allowLanLock.Release();
            if (_allowLanTargets.TryPeek(out var queued))
            {
                queuedTarget = queued;
            }
            else
            {
                _allowLanTargets.ClearVisible();
            }
        }

        if (queuedTarget.HasValue)
        {
            await SetAllowLanAsync(queuedTarget.Value);
        }
        else
        {
            await SyncSwitchStatesFromRealityAsync(_cts.Token);
        }
    }

    private async Task ApplyAllowLanTargetAsync(bool enabled)
    {
        try
        {
            await ApplyConfigPatchTransactionAsync(
                new Dictionary<string, object?> { ["allow-lan"] = enabled },
                reloadAfterPatch: false);
            await SyncSwitchStatesFromRealityAsync(_cts.Token);
        }
        catch (Exception ex) when (!IsAppCancellation(ex))
        {
            await SyncSwitchStatesFromRealityAsync(_cts.Token);
            await _dispatcher.RunAsync(() =>
            {
                Runtime.Notifications.Error(
                    "允许局域网切换失败",
                    source: LogSources.Network,
                    exception: ex);
            });
        }
    }

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
            var outputTask = process.StandardOutput.ReadToEndAsync();
            var errorTask = process.StandardError.ReadToEndAsync();
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

    public async Task RestartCoreAsync()
    {
        await RestartCoreProcessAsync();
    }

    private async Task RestartCoreProcessAsync()
    {
        if (_core.RunMode == CoreRunMode.NotRunning && !_core.IsRunning)
        {
            await MihomoControllerEndpoint.PrepareRuntimeConfigForCoreAsync(_cts.Token);
            return;
        }

        var requireTun = await YamlConfigService.IsTunEnabledAsync(AppPaths.RuntimeConfigPath, _cts.Token);
        await _core.RestartAsync(requireTun, _cts.Token);
        await Task.Delay(1500, _cts.Token);
        await RefreshRuntimeAsync(_cts.Token);
    }

    public Task UpdateGeoAsync() => _api.UpdateGeoAsync(_cts.Token);

    public async Task ApplyCoreReleaseAsync(MihomoCoreReleaseKind kind, string specificVersion)
    {
        await SaveCoreDownloadSettingsAsync(kind, specificVersion);
        await DownloadCoreAsync(kind, specificVersion);
    }

    public async Task DownloadCoreAsync(MihomoCoreReleaseKind kind, string specificVersion)
    {
        MihomoCoreDownloadResult? downloaded = null;
        var wasRunning = _core.RunMode != CoreRunMode.NotRunning || _core.IsRunning;

        try
        {
            downloaded = await _coreDownloader.DownloadAsync(
                new MihomoCoreDownloadRequest(kind, specificVersion),
                _cts.Token);

            await _core.PrepareForCoreReplacementAsync(_cts.Token);
            await MihomoCoreFileInstaller.InstallAsync(downloaded.ExecutablePath, AppPaths.ManagedCorePath, _cts.Token);

            try
            {
                await _core.ValidateConfigAsync(_cts.Token);
            }
            catch
            {
                await MihomoCoreFileInstaller.RestoreBackupAsync(AppPaths.ManagedCorePath, _cts.Token);
                throw;
            }

            await _core.EnsureStartedAsync(Runtime.IsTunEnabled, _cts.Token);

            await Task.Delay(800, _cts.Token);
            await RefreshRuntimeAsync(_cts.Token);
            await _dispatcher.RunAsync(() =>
                Logs.AddApp(
                    "INFO",
                    $"内核已安装：{FormatCoreKind(kind)} {downloaded.Version}",
                    LogSources.Core));
        }
        catch
        {
            if (wasRunning)
            {
                try
                {
                    await _core.EnsureStartedAsync(Runtime.IsTunEnabled, _cts.Token);
                }
                catch (Exception restartEx)
                {
                    DiagnosticLog.WriteAppException(
                        LogSources.Core,
                        restartEx,
                        "内核更新失败后恢复原内核运行状态失败");
                }
            }

            throw;
        }
        finally
        {
            if (downloaded is not null)
            {
                TryDeleteDirectory(downloaded.TempDirectory);
            }
        }
    }

    public async Task SaveCoreDownloadSettingsAsync(MihomoCoreReleaseKind kind, string specificVersion)
    {
        await AppSettingsService.PatchAsync(settings =>
        {
            settings.CoreReleaseChannel = FormatCoreSetting(kind);
            settings.CoreSpecificVersion = specificVersion.Trim();
        }, _cts.Token);
    }

    public Task<IReadOnlyList<string>> LoadCoreSpecificVersionsAsync(bool forceRefresh) =>
        _coreDownloader.GetSpecificVersionsAsync(forceRefresh, _cts.Token);

    public Task ValidateCurrentConfigAsync() => _core.ValidateConfigAsync(_cts.Token);

    public async Task SaveCoreSettingsAsync(YamlConfigService.CoreSectionSettings settings, bool enableExternalController)
    {
        await _configMutationLock.WaitAsync(_cts.Token);
        try
        {
        var patch = new Dictionary<string, object?>
        {
            ["ipv6"] = settings.Ipv6,
            ["unified-delay"] = settings.UnifiedDelay,
            ["tcp-concurrent"] = settings.TcpConcurrent,
            ["log-level"] = settings.LogLevel,
            ["find-process-mode"] = settings.FindProcessMode,
            ["mixed-port"] = settings.MixedPort,
            ["socks-port"] = settings.SocksPort,
            ["port"] = settings.HttpPort,
            ["redir-port"] = settings.RedirPort,
            ["tproxy-port"] = settings.TproxyPort,
            ["external-controller"] = enableExternalController
                ? MihomoControllerEndpoint.ResolveHttpAddress(settings.ExternalController)
                : null,
            ["secret"] = settings.Secret,
            ["allow-lan"] = settings.AllowLan,
            ["lan-allowed-ips"] = settings.LanAllowedIps,
            ["lan-disallowed-ips"] = settings.LanDisallowedIps,
            ["authentication"] = settings.Authentication,
            ["skip-auth-prefixes"] = settings.SkipAuthPrefixes,
            ["profile"] = new Dictionary<string, object?>
            {
                ["store-selected"] = settings.StoreSelected,
                ["store-fake-ip"] = settings.StoreFakeIp
            }
        };

        var snapshot = await ConfigFileSnapshot.CaptureAsync(
            [
                AppPaths.BaseConfigPath,
                AppPaths.RuntimeConfigPath
            ],
            _cts.Token);
        var previousRuntime = snapshot.GetContent(AppPaths.RuntimeConfigPath);
        var coreWasRunning = _core.RunMode != CoreRunMode.NotRunning || _core.IsRunning;
        var previousTunEnabled = previousRuntime is not null &&
                                 YamlConfigService.IsTunEnabled(previousRuntime);

        try
        {
            await YamlConfigService.PersistBasePatchAsync(patch, _cts.Token);
            await _runtimeConfig.RebuildAsync(_cts.Token);
            await ApplyApiEndpointFromConfigAsync(_cts.Token);
            await RestartCoreProcessAsync();
            await SyncRuntimeConfigToGistIfEnabledAsync();
        }
        catch
        {
            await snapshot.RestoreAsync();
            await ApplyApiEndpointFromConfigAsync(CancellationToken.None);
            await RestorePreviousRuntimeAsync(coreWasRunning, previousRuntime, previousTunEnabled);
            throw;
        }
        }
        finally
        {
            _configMutationLock.Release();
        }
    }

    public async Task SaveCoreAppSettingsAsync(
        int maxLogDays,
        int maxLogFileSizeMb,
        IReadOnlyList<WebUiPanelSetting> panels)
    {
        await AppSettingsService.PatchAsync(settings =>
        {
            settings.MaxLogDays = Math.Max(1, maxLogDays);
            settings.MaxLogFileSizeMb = Math.Max(1, maxLogFileSizeMb);
            settings.WebUiPanels = panels
                .Where(panel => !string.IsNullOrWhiteSpace(panel.Name) && !string.IsNullOrWhiteSpace(panel.Url))
                .Select(panel => new WebUiPanelSetting { Name = panel.Name.Trim(), Url = panel.Url.Trim() })
                .ToList();
        }, _cts.Token);
    }

    public async Task OpenCoreDirectoryAsync() =>
        await OpenExternalFileAsync(AppPaths.CoreDirectory, "内核目录");

    public async Task OpenConfigDirectoryAsync() =>
        await OpenExternalFileAsync(AppPaths.ConfigDirectory, "配置目录");

    public async Task OpenWebUiAsync(string template)
    {
        var settings = await LoadCoreSettingsAsync();
        if (string.IsNullOrWhiteSpace(settings.ExternalController))
        {
            await _dispatcher.RunAsync(() =>
            {
                Runtime.Notifications.Warning(
                    "请先开启外部控制后再打开 WebUI",
                    source: LogSources.Core);
            });
            return;
        }

        var controller = MihomoControllerEndpoint.ResolveHttpAddress(settings.ExternalController);
        var (host, port) = SplitController(controller);
        var url = template
            .Replace("%host", host, StringComparison.OrdinalIgnoreCase)
            .Replace("%port", port, StringComparison.OrdinalIgnoreCase)
            .Replace("%secret", Uri.EscapeDataString(settings.Secret), StringComparison.OrdinalIgnoreCase);
        await OpenExternalFileAsync(url, "WebUI");
    }

    private async Task ApplyApiEndpointFromConfigAsync(CancellationToken cancellationToken)
    {
        var settings = await LoadCoreSettingsAsync(cancellationToken);
        var secret = string.IsNullOrWhiteSpace(settings.Secret) ? null : settings.Secret.Trim();

        _api.Configure(secret);
        _ws.Configure(_api, settings.LogLevel);
    }

    public Task<YamlConfigService.GeoDataSettings> LoadGeoDataSettingsAsync() =>
        YamlConfigService.LoadGeoDataSettingsAsync(GetSettingsConfigPath(), _cts.Token);

    public Task<YamlConfigService.CoreSectionSettings> LoadCoreSettingsAsync() =>
        LoadCoreSettingsAsync(_cts.Token);

    private static Task<YamlConfigService.CoreSectionSettings> LoadCoreSettingsAsync(
        CancellationToken cancellationToken)
    {
        return YamlConfigService.LoadCoreSettingsAsync(GetSettingsConfigPath(), cancellationToken);
    }

    public async Task<YamlConfigService.DnsSectionSettings> LoadDnsSettingsAsync()
    {
        var result = await YamlConfigService.LoadDnsSettingsAsync(GetSettingsConfigPath(), _cts.Token);
        var settings = await AppSettingsService.LoadAsync(_cts.Token);
        return result with { OverrideEnabled = settings.DnsOverrideEnabled };
    }

    public async Task<YamlConfigService.SnifferSectionSettings> LoadSnifferSettingsAsync()
    {
        var result = await YamlConfigService.LoadSnifferSettingsAsync(GetSettingsConfigPath(), _cts.Token);
        var settings = await AppSettingsService.LoadAsync(_cts.Token);
        return result with { OverrideEnabled = settings.SnifferOverrideEnabled };
    }

    public Task<YamlConfigService.TunSectionSettings> LoadTunSettingsAsync() =>
        YamlConfigService.LoadTunSettingsAsync(GetSettingsConfigPath(), _cts.Token);

    private static string GetSettingsConfigPath() => AppPaths.BaseConfigPath;

    public async Task SaveSnifferSettingsAsync(YamlConfigService.SnifferSectionSettings settings)
    {
        var patch = settings.OverrideEnabled
            ? new Dictionary<string, object?>
        {
            ["sniffer"] = new Dictionary<string, object?>
            {
                ["enable"] = true,
                ["override-destination"] = settings.OverrideDestination,
                ["force-dns-mapping"] = settings.ForceDnsMapping,
                ["parse-pure-ip"] = settings.ParsePureIp,
                ["sniff"] = new Dictionary<string, object?>
                {
                    ["HTTP"] = new Dictionary<string, object?> { ["ports"] = settings.HttpPorts },
                    ["TLS"] = new Dictionary<string, object?> { ["ports"] = settings.TlsPorts },
                    ["QUIC"] = new Dictionary<string, object?> { ["ports"] = settings.QuicPorts }
                },
                ["skip-domain"] = settings.SkipDomain,
                ["force-domain"] = settings.ForceDomain,
                ["skip-dst-address"] = settings.SkipDstAddress,
                ["skip-src-address"] = settings.SkipSrcAddress
            }
        }
            : [];
        var previous = (await AppSettingsService.LoadAsync(_cts.Token)).SnifferOverrideEnabled;
        await AppSettingsService.PatchAsync(value => value.SnifferOverrideEnabled = settings.OverrideEnabled, _cts.Token);
        try
        {
            await ApplyConfigPatchTransactionAsync(patch, reloadAfterPatch: true);
        }
        catch
        {
            await AppSettingsService.PatchAsync(value => value.SnifferOverrideEnabled = previous, CancellationToken.None);
            throw;
        }
    }

    public Task SetDnsOverrideEnabledAsync(bool enabled) =>
        SetBuiltInOverrideEnabledAsync(
            enabled,
            static (settings, value) => settings.DnsOverrideEnabled = value);

    public Task SetSnifferOverrideEnabledAsync(bool enabled) =>
        SetBuiltInOverrideEnabledAsync(
            enabled,
            static (settings, value) => settings.SnifferOverrideEnabled = value);

    private async Task SetBuiltInOverrideEnabledAsync(
        bool enabled,
        Action<AppSettings, bool> update)
    {
        var previousSettings = await AppSettingsService.LoadAsync(_cts.Token);
        await AppSettingsService.PatchAsync(value => update(value, enabled), _cts.Token);
        try
        {
            await ApplyConfigPatchTransactionAsync([], reloadAfterPatch: true);
        }
        catch
        {
            await AppSettingsService.SaveAsync(previousSettings, CancellationToken.None);
            throw;
        }
    }

    public async Task SaveTunSettingsAsync(YamlConfigService.TunSectionSettings settings)
    {
        var patch = new Dictionary<string, object?>
        {
            ["tun"] = new Dictionary<string, object?>
            {
                ["stack"] = settings.Stack,
                ["auto-route"] = settings.AutoRoute,
                ["auto-detect-interface"] = settings.AutoDetectInterface,
                ["strict-route"] = settings.StrictRoute,
                ["mtu"] = settings.Mtu,
                ["device"] = settings.DeviceName,
                ["device-name"] = null,
                ["dns-hijack"] = settings.DnsHijack,
                ["route-exclude-address"] = settings.RouteExcludeAddress
            }
        };
        await ApplyConfigPatchTransactionAsync(patch, reloadAfterPatch: true);
    }

    public async Task SaveGeoDataSettingsAsync(YamlConfigService.GeoDataSettings settings)
    {
        var patch = new Dictionary<string, object?>
        {
            ["geox-url"] = new Dictionary<string, object?>
            {
                ["geoip"] = settings.GeoIpUrl,
                ["geosite"] = settings.GeoSiteUrl,
                ["mmdb"] = settings.MmdbUrl,
                ["asn"] = settings.AsnUrl
            },
            ["geodata-mode"] = settings.GeoDataMode,
            ["geo-auto-update"] = settings.AutoUpdate,
            ["geo-update-interval"] = settings.UpdateInterval
        };

        await ApplyConfigPatchTransactionAsync(patch, reloadAfterPatch: true);
    }

    private async Task ApplyConfigPatchTransactionAsync(
        Dictionary<string, object?> patch,
        bool reloadAfterPatch,
        IReadOnlySet<string>? replaceRootMappings = null)
    {
        await _configMutationLock.WaitAsync(_cts.Token);
        try
        {
        var snapshot = await ConfigFileSnapshot.CaptureAsync(
            [AppPaths.BaseConfigPath, AppPaths.RuntimeConfigPath],
            _cts.Token);
        var previousRuntime = snapshot.GetContent(AppPaths.RuntimeConfigPath);
        var coreWasRunning = _core.RunMode != CoreRunMode.NotRunning || _core.IsRunning;
        var previousTunEnabled = previousRuntime is not null &&
                                 YamlConfigService.IsTunEnabled(previousRuntime);

        try
        {
            await YamlConfigService.PersistBasePatchAsync(
                patch,
                _cts.Token,
                replaceRootMappings);
            await _runtimeConfig.RebuildAsync(_cts.Token);
            if (coreWasRunning)
            {
                if (reloadAfterPatch)
                {
                    await ReloadCurrentConfigAsync();
                }
                else
                {
                    await _api.PatchConfigAsync(patch, _cts.Token);
                }
            }

            if (coreWasRunning)
            {
                await RefreshRuntimeAsync(_cts.Token);
            }
        }
        catch
        {
            await snapshot.RestoreAsync();
            await RestorePreviousRuntimeAsync(coreWasRunning, previousRuntime, previousTunEnabled);
            throw;
        }

        await SyncRuntimeConfigToGistIfEnabledAsync();
        }
        finally
        {
            _configMutationLock.Release();
        }
    }

    private async Task RestorePreviousRuntimeAsync(
        bool coreWasRunning,
        string? previousRuntime,
        bool previousTunEnabled)
    {
        if (!coreWasRunning || previousRuntime is null)
        {
            return;
        }

        try
        {
            await _api.ReloadConfigAsync(AppPaths.RuntimeConfigPath, CancellationToken.None);
        }
        catch
        {
            await _core.RestartAsync(previousTunEnabled, CancellationToken.None);
        }
    }

    public async Task SetupTunFirewallAsync()
    {
        await WindowsFirewallService.SetupMihomoRulesAsync();
        await RestartCoreAsync();
    }

    public Task FlushFakeIpAsync() => _api.FlushFakeIpAsync(_cts.Token);

    public async Task RepairServiceAsync()
    {
        try
        {
            await _serviceManager.RepairAsync(_cts.Token);
            await _dispatcher.RunAsync(() =>
            {
                Runtime.TunServiceStatusText = "正在退出并修复服务";
                Runtime.IsTunToggleAvailable = false;
                Logs.AddApp("INFO", "已启动包外修复进程，应用即将退出", LogSources.Service);
            });
        }
        catch (Exception ex) when (!IsAppCancellation(ex))
        {
            await _dispatcher.RunAsync(() =>
            {
                Runtime.TunServiceStatusText = "服务修复失败";
                Runtime.IsTunToggleAvailable = false;
                Runtime.ShowTunServiceRepair = true;
            });
            throw;
        }
    }

    public async Task StopServiceAsync()
    {
        if (await YamlConfigService.IsTunEnabledAsync(AppPaths.RuntimeConfigPath, _cts.Token))
        {
            await SetTunAsync(false);
        }

        await _serviceManager.StopHostAsync(_cts.Token);
        var status = await _serviceManager.GetStatusAsync(_cts.Token);
        await _dispatcher.RunAsync(() =>
        {
            Runtime.ApplyTunCapability(status);
            Logs.AddApp("INFO", "ClashSuki 服务已停止，将在启用虚拟网卡时按需启动", LogSources.Service);
        });
        await RefreshRuntimeAsync(_cts.Token);
    }

    private async Task RunRuntimeLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await RefreshRuntimeAsync(cancellationToken);
                try
                {
                    await ApplySsidDirectIfNeededAsync(cancellationToken);
                }
                catch (Exception ex) when (!IsAppCancellation(ex))
                {
                    DiagnosticLog.WriteAppExceptionThrottled(
                        "ssid-state-sync",
                        LogSources.Network,
                        ex,
                        "SSID 状态同步失败",
                        level: "WARN");
                }

                await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // normal shutdown
        }
    }

    private async Task RunProxyLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await RefreshProxiesAsync(cancellationToken);
                await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // normal shutdown
        }
    }

    private async Task ApplySsidDirectIfNeededAsync(CancellationToken cancellationToken)
    {
        if (DateTime.UtcNow - _lastSsidCheck < TimeSpan.FromSeconds(15))
        {
            return;
        }

        _lastSsidCheck = DateTime.UtcNow;
        var settings = await AppSettingsService.LoadAsync(cancellationToken);
        var ssids = settings.PauseSsids;
        var currentSsid = await WindowsNetworkEnvironmentService.GetCurrentWifiSsidAsync(cancellationToken);
        var shouldDirect = ssids.Count > 0 &&
                           !string.IsNullOrWhiteSpace(currentSsid) &&
                           ssids.Contains(currentSsid, StringComparer.OrdinalIgnoreCase);
        if (shouldDirect)
        {
            if (!_ssidDirectActive)
            {
                _ssidModeBeforeDirect = NormalizeRestorableMode(Runtime.CurrentMode);
                await _dispatcher.RunAsync(() =>
                    Logs.AddApp(
                        "INFO",
                        $"SSID {currentSsid} 命中直连规则，切换到 DIRECT",
                        LogSources.Network));
                await PatchConfigOrThrowAsync(new Dictionary<string, object?>
                {
                    ["mode"] = "direct"
                });
                if (!Runtime.CurrentMode.Equals("direct", StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                _ssidDirectActive = true;
            }
            else if (!Runtime.CurrentMode.Equals("direct", StringComparison.OrdinalIgnoreCase))
            {
                _ssidModeBeforeDirect = NormalizeRestorableMode(Runtime.CurrentMode);
                await PatchConfigOrThrowAsync(new Dictionary<string, object?>
                {
                    ["mode"] = "direct"
                });
                if (!Runtime.CurrentMode.Equals("direct", StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }
            }

            if (settings.DisableDnsOnPauseSsid && !_ssidDnsDisabled)
            {
                _ssidDnsEnabledBeforeDirect = await YamlConfigService.IsDnsEnabledAsync(
                    AppPaths.RuntimeConfigPath,
                    cancellationToken);
                if (_ssidDnsEnabledBeforeDirect)
                {
                    await PatchConfigOrThrowAsync(new Dictionary<string, object?>
                    {
                        ["dns"] = new Dictionary<string, object?> { ["enable"] = false }
                    });
                    _ssidDnsDisabled = true;
                }
            }
            else if (settings.DisableDnsOnPauseSsid &&
                     _ssidDnsDisabled &&
                     (await _api.GetConfigsAsync(cancellationToken)).Dns?.Enable != false)
            {
                await PatchConfigOrThrowAsync(new Dictionary<string, object?>
                {
                    ["dns"] = new Dictionary<string, object?> { ["enable"] = false }
                });
            }
            else if (!settings.DisableDnsOnPauseSsid && _ssidDnsDisabled)
            {
                await RestoreSsidDnsAsync();
            }
        }
        else if (!shouldDirect && _ssidDirectActive)
        {
            var restoreMode = _ssidModeBeforeDirect;
            await _dispatcher.RunAsync(() =>
                Logs.AddApp(
                    "INFO",
                    $"SSID 直连规则已离开，恢复 {restoreMode.ToUpperInvariant()} 模式",
                    LogSources.Network));
            await PatchConfigOrThrowAsync(new Dictionary<string, object?>
            {
                ["mode"] = restoreMode
            });
            if (!Runtime.CurrentMode.Equals(restoreMode, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            if (_ssidDnsDisabled)
            {
                await RestoreSsidDnsAsync();
            }

            _ssidDirectActive = false;
        }
    }

    private async Task RestoreSsidDnsAsync()
    {
        await PatchConfigOrThrowAsync(new Dictionary<string, object?>
        {
            ["dns"] = new Dictionary<string, object?> { ["enable"] = _ssidDnsEnabledBeforeDirect }
        });
        _ssidDnsDisabled = false;
    }

    private static string NormalizeRestorableMode(string? mode) =>
        mode?.Trim().ToLowerInvariant() switch
        {
            "global" => "global",
            "direct" => "direct",
            _ => "rule"
        };

    private async Task RunRulesLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await RefreshRulesAsync(cancellationToken);
                await Task.Delay(TimeSpan.FromSeconds(20), cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // normal shutdown
        }
    }

    private async Task RefreshRuntimeAsync(CancellationToken cancellationToken)
    {
        try
        {
            var versionTask = Safe(
                _api.GetVersionAsync(cancellationToken),
                "RUNTIME-VERSION",
                LogSources.Core,
                "读取内核版本失败");
            var configTask = Safe(
                _api.GetConfigsAsync(cancellationToken),
                "RUNTIME-CONFIG",
                LogSources.Core,
                "读取运行时配置失败");
            await Task.WhenAll(versionTask, configTask);
            var version = await versionTask;
            var config = await configTask;
            var settings = await AppSettingsService.LoadAsync(cancellationToken);
            await _core.TryAdoptServiceCoreAsync(Runtime.IsTunEnabled, cancellationToken);

            if (version is null && config is null)
            {
                await SyncSwitchStatesFromConfigAsync(cancellationToken);
                await _dispatcher.RunAsync(() =>
                {
                    if (!_systemProxyTransitionInProgress)
                    {
                        SyncSystemProxyUiFromState(settings);
                    }
                    KeepPendingSwitchTargetsVisible();
                    Runtime.ApplyDisconnected();
                });
                await TryRecoverCoreAsync();
                return;
            }

            await _dispatcher.RunAsync(() =>
            {
                if (!_systemProxyTransitionInProgress)
                {
                    SyncSystemProxyUiFromState(settings);
                }
                Runtime.ApplyConnected(version, config, _core.RunMode, _core.ProcessId, syncTun: !_tunTransitionInProgress);
                if (!_allowLanTransitionInProgress && config is not null)
                {
                    Runtime.SyncAllowLan(config.AllowLan ?? false);
                }
                KeepPendingSwitchTargetsVisible();
                Proxies.SetGlobalOnly(Runtime.IsGlobalMode);
            });
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            await _dispatcher.RunAsync(() =>
            {
                Runtime.ApplyDisconnected();
                Runtime.Notifications.Error(
                    "刷新运行状态失败",
                    source: LogSources.Realtime,
                    exception: ex);
            });
        }
    }

    private async Task SyncSwitchStatesFromRealityAsync(CancellationToken cancellationToken)
    {
        var config = await Safe(
            _api.GetConfigsAsync(cancellationToken),
            "SWITCH-STATE-CONFIG",
            LogSources.Core,
            "同步开关状态时读取运行时配置失败");
        var settings = await AppSettingsService.LoadAsync(cancellationToken);
        if (config is null)
        {
            await SyncSwitchStatesFromConfigAsync(cancellationToken);
            await _dispatcher.RunAsync(() =>
            {
                if (!_systemProxyTransitionInProgress)
                {
                    SyncSystemProxyUiFromState(settings);
                }
                KeepPendingSwitchTargetsVisible();
            });
            return;
        }

        await _dispatcher.RunAsync(() =>
        {
            if (!_systemProxyTransitionInProgress)
            {
                SyncSystemProxyUiFromState(settings);
            }

            if (!_modeTransitionInProgress)
            {
                Runtime.CurrentMode = config.Mode ?? "rule";
            }

            if (!_tunTransitionInProgress)
            {
                Runtime.SyncTunEnabled(config.Tun?.Enable ?? false);
            }

            if (!_allowLanTransitionInProgress)
            {
                Runtime.SyncAllowLan(config.AllowLan ?? false);
            }

            KeepPendingSwitchTargetsVisible();
        });
    }

    private void EnsureTunActivationPossible()
    {
        if (!Runtime.IsTunToggleAvailable)
        {
            throw new InvalidOperationException("TUN 需要安装 ClashSuki 服务或以管理员身份运行");
        }
    }

    private async Task ReconcileTunStateAfterStartupAsync(
        bool desiredEnabled,
        CancellationToken cancellationToken)
    {
        try
        {
            await WaitForApiReadyAsync("启动状态同步", TimeSpan.FromSeconds(15), cancellationToken);
            if (await WaitForTunStateAsync(
                desiredEnabled,
                TimeSpan.FromSeconds(2),
                cancellationToken))
            {
                return;
            }

            if (desiredEnabled)
            {
                EnsureTunActivationPossible();
            }

            await ApplyTunConfigAndVerifyAsync(desiredEnabled, cancellationToken);
            await SyncSwitchStatesFromRealityAsync(cancellationToken);
        }
        catch (Exception ex) when (!IsAppCancellation(ex))
        {
            await SyncSwitchStatesFromRealityAsync(cancellationToken);
            await _dispatcher.RunAsync(() =>
                Runtime.Notifications.Warning(
                    desiredEnabled
                        ? "虚拟网卡未能恢复，内核已以普通模式运行（系统代理仍可用）"
                        : "检测到虚拟网卡残留状态但自动关闭失败，请重启内核或服务",
                    source: LogSources.Tun,
                    exception: ex));
        }
    }

    private async Task SyncSwitchStatesFromConfigAsync(CancellationToken cancellationToken)
    {
        var tunEnabled = await YamlConfigService.IsTunEnabledAsync(AppPaths.RuntimeConfigPath, cancellationToken);
        var allowLan = await YamlConfigService.IsAllowLanEnabledAsync(AppPaths.RuntimeConfigPath, cancellationToken);
        await _dispatcher.RunAsync(() =>
        {
            if (!_tunTransitionInProgress)
            {
                Runtime.SyncTunEnabled(tunEnabled);
            }

            if (!_allowLanTransitionInProgress)
            {
                Runtime.SyncAllowLan(allowLan);
            }

            KeepPendingSwitchTargetsVisible();
        });
    }

    private async Task RollBackTunAfterFailureAsync(
        ConfigFileSnapshot? snapshot,
        bool previousTunEnabled,
        CancellationToken cancellationToken)
    {
        try
        {
            await SafeVoid(
                _api.PatchTunAsync(previousTunEnabled, cancellationToken),
                "TUN-ROLLBACK-PATCH");
            if (snapshot is not null)
            {
                await snapshot.RestoreAsync();
            }
            try
            {
                await _api.ReloadConfigAsync(AppPaths.RuntimeConfigPath, cancellationToken);
            }
            catch (Exception ex) when (!IsAppCancellation(ex))
            {
                DiagnosticLog.WriteAppException("TUN-ROLLBACK-RELOAD", ex);
                await _core.RestartAsync(previousTunEnabled, cancellationToken);
            }
        }
        catch (Exception ex) when (!IsAppCancellation(ex))
        {
            DiagnosticLog.WriteAppException(LogSources.Tun, ex, "虚拟网卡状态回滚失败");
        }

        await _dispatcher.RunAsync(() => Runtime.SyncTunEnabled(previousTunEnabled));
    }

    private async Task WaitForApiReadyBestEffortAsync(string reason, TimeSpan timeout, CancellationToken cancellationToken)
    {
        try
        {
            await WaitForApiReadyAsync(reason, timeout, cancellationToken);
        }
        catch (Exception ex) when (!IsAppCancellation(ex))
        {
            // 就绪等待失败不应阻断系统代理 / 首刷；后续运行时循环会持续重试。
            DiagnosticLog.WriteAppExceptionThrottled(
                $"tun-api-ready:{reason}",
                LogSources.Tun,
                ex,
                $"等待内核 API 就绪失败，场景: {reason}",
                level: "WARN");
        }
    }

    private async Task WaitForApiReadyAsync(string reason, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + timeout;
        Exception? lastError = null;

        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var config = await _api.GetConfigsAsync(TimeSpan.FromSeconds(1), cancellationToken);
                await _dispatcher.RunAsync(() =>
                {
                    Runtime.ApplyConnected(null, config, _core.RunMode, _core.ProcessId, syncTun: !_tunTransitionInProgress);
                    KeepPendingSwitchTargetsVisible();
                });
                return;
            }
            catch (Exception ex) when (!IsAppCancellation(ex))
            {
                lastError = ex;
                await Task.Delay(300, cancellationToken);
            }
        }

        throw new TimeoutException($"等待 mihomo API 就绪超时：{reason}", lastError);
    }

    private async Task ApplyTunConfigAndVerifyAsync(bool enabled, CancellationToken cancellationToken)
    {
        const int verifySeconds = 3;
        var verifyTimeout = TimeSpan.FromSeconds(verifySeconds);

        var patch = new Dictionary<string, object?>
        {
            ["tun"] = new Dictionary<string, object?> { ["enable"] = enabled }
        };
        if (enabled)
        {
            patch["dns"] = new Dictionary<string, object?> { ["enable"] = true };
        }

        await YamlConfigService.PersistBasePatchAsync(patch, cancellationToken);
        await _runtimeConfig.RebuildAsync(cancellationToken);

        if (!enabled)
        {
            try
            {
                await _api.PatchTunAsync(false, cancellationToken);
            }
            catch (Exception ex) when (!IsAppCancellation(ex))
            {
            }

            if (await WaitForTunStateAsync(false, verifyTimeout, cancellationToken))
            {
                return;
            }

            await _core.RestartAsync(requireTun: false, cancellationToken);
            await WaitForApiReadyAsync("重启后关闭虚拟网卡", TimeSpan.FromSeconds(10), cancellationToken);
            if (await WaitForTunStateAsync(false, verifyTimeout, cancellationToken))
            {
                return;
            }

            throw new TimeoutException("TUN 关闭失败：tun.enable 仍为 True");
        }

        EnsureTunActivationPossible();

        try
        {
            await MihomoControllerEndpoint.PrepareRuntimeConfigForCoreAsync(cancellationToken);
            await _api.ReloadConfigAsync(AppPaths.RuntimeConfigPath, cancellationToken);
            if (await WaitForTunStateAsync(true, verifyTimeout, cancellationToken))
            {
                return;
            }
        }
        catch (Exception ex) when (!IsAppCancellation(ex))
        {
        }

        await _core.RestartAsync(requireTun: true, cancellationToken);
        await WaitForApiReadyAsync("重启后开启虚拟网卡", TimeSpan.FromSeconds(10), cancellationToken);
        if (await WaitForTunStateAsync(true, verifyTimeout, cancellationToken))
        {
            return;
        }

        throw new TimeoutException("TUN 未能启动：tun.enable 仍为 false");
    }

    private async Task<bool> WaitForTunStateAsync(bool expected, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + timeout;

        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var config = await _api.GetConfigsAsync(TimeSpan.FromSeconds(1), cancellationToken);
                var actual = config.Tun?.Enable ?? false;
                await _dispatcher.RunAsync(() =>
                {
                    if (actual == expected)
                    {
                        Runtime.SyncTunEnabled(actual);
                    }
                    else
                    {
                        KeepPendingSwitchTargetsVisible();
                    }
                });

                if (actual == expected)
                {
                    return true;
                }
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
            }

            await Task.Delay(500, cancellationToken);
        }

        return false;
    }

    private void KeepPendingSwitchTargetsVisible()
    {
        if (_systemProxyTargets.TryGetVisible(out var systemProxy))
        {
            Runtime.SyncSystemProxyEnabled(systemProxy);
        }

        if (_tunTargets.TryGetVisible(out var tun))
        {
            Runtime.SyncTunEnabled(tun);
        }

        if (_allowLanTargets.TryGetVisible(out var allowLan))
        {
            Runtime.SyncAllowLan(allowLan);
        }

        if (_modeTargets.TryGetVisible(out var mode))
        {
            Runtime.CurrentMode = mode;
        }
    }

    private sealed class TargetTransitionState<T>
    {
        private readonly object _gate = new();
        private bool _hasPending;
        private bool _hasVisible;
        private T? _pending;
        private T? _visible;

        public void Queue(T target)
        {
            lock (_gate)
            {
                _pending = target;
                _visible = target;
                _hasPending = true;
                _hasVisible = true;
            }
        }

        public bool TryTake(out T target)
        {
            lock (_gate)
            {
                if (!_hasPending)
                {
                    target = default!;
                    return false;
                }

                target = _pending!;
                _pending = default;
                _visible = target;
                _hasPending = false;
                _hasVisible = true;
                return true;
            }
        }

        public bool TryPeek(out T target)
        {
            lock (_gate)
            {
                if (!_hasPending)
                {
                    target = default!;
                    return false;
                }

                target = _pending!;
                return true;
            }
        }

        public void SetVisible(T target)
        {
            lock (_gate)
            {
                _visible = target;
                _hasVisible = true;
            }
        }

        public bool TryGetVisible(out T target)
        {
            lock (_gate)
            {
                if (!_hasVisible)
                {
                    target = default!;
                    return false;
                }

                target = _visible!;
                return true;
            }
        }

        public void ClearVisible()
        {
            lock (_gate)
            {
                _visible = default;
                _hasVisible = false;
            }
        }
    }

    private List<string>? _cachedGroupOrder;

    private async Task RefreshProxiesAsync(CancellationToken cancellationToken)
    {
        try
        {
            var proxiesTask = Safe(
                _api.GetProxiesAsync(cancellationToken),
                "PROXY-LIST",
                LogSources.Proxy,
                "读取代理列表失败");
            var providersTask = Safe(
                _api.GetProxyProvidersAsync(cancellationToken),
                "PROXY-PROVIDERS",
                LogSources.Proxy,
                "读取代理提供者失败");
            await Task.WhenAll(proxiesTask, providersTask);
            var proxies = await proxiesTask;
            var providers = await providersTask;

            _cachedGroupOrder ??= await SafeOrder(
                () => YamlConfigService.GetProxyGroupOrderAsync(AppPaths.RuntimeConfigPath, cancellationToken));

            await _dispatcher.RunAsync(() =>
            {
                if (proxies is not null) Proxies.ApplyProxyGroups(proxies, _cachedGroupOrder);
                if (providers is not null) Proxies.ApplyProviders(providers);
            });
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            DiagnosticLog.WriteAppExceptionThrottled(
                "proxy-refresh",
                LogSources.Proxy,
                ex,
                "刷新代理数据失败");
        }
    }

    private static async Task<List<string>?> SafeOrder(Func<Task<List<string>>> func)
    {
        try { return await func(); }
        catch (Exception ex)
        {
            DiagnosticLog.WriteAppExceptionThrottled(
                "PROXY-GROUP-ORDER",
                LogSources.Proxy,
                ex,
                "读取代理组顺序失败");
            return null;
        }
    }

    private async Task RefreshRulesAsync(CancellationToken cancellationToken)
    {
        try
        {
            var rulesTask = Safe(
                _api.GetRulesAsync(cancellationToken),
                "RULE-LIST",
                LogSources.Rule,
                "读取规则列表失败");
            var providersTask = Safe(
                _api.GetRuleProvidersAsync(cancellationToken),
                "RULE-PROVIDERS",
                LogSources.Rule,
                "读取规则提供者失败");
            var providerConfigTask = Safe(
                YamlConfigService.LoadRuleProviderConfigsAsync(AppPaths.RuntimeConfigPath, cancellationToken),
                "RULE-PROVIDER-CONFIG",
                LogSources.Rule,
                "读取规则提供者配置失败");
            await Task.WhenAll(rulesTask, providersTask, providerConfigTask);
            var rules = await rulesTask;
            var providers = await providersTask;
            var providerConfigs = await providerConfigTask;
            await _dispatcher.RunAsync(() => Rules.Apply(rules, providers, providerConfigs));
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            DiagnosticLog.WriteAppExceptionThrottled(
                "rules-refresh",
                LogSources.Rule,
                ex,
                "刷新规则数据失败");
        }
    }

    private async Task TryRecoverCoreAsync()
    {
        if ((DateTime.Now - _lastCoreRecoverAttempt).TotalSeconds < 10)
        {
            return;
        }

        _lastCoreRecoverAttempt = DateTime.Now;
        try
        {
            var requireTun = Runtime.IsTunEnabled;
            var wasRunning = _core.RunMode == CoreRunMode.Service || _core.IsRunning;
            if (wasRunning)
            {
                await _core.RestartAsync(requireTun, _cts.Token);
            }
            else
            {
                await _core.EnsureStartedAsync(requireTun, _cts.Token);
            }

            await RefreshRuntimeAsync(_cts.Token);
            await RestoreDesiredSystemProxyAsync(_cts.Token);
            await _dispatcher.RunAsync(() =>
                Logs.AddApp("INFO", "内核已自动恢复", LogSources.Core));
        }
        catch (Exception ex)
        {
            try
            {
                await _core.EnsureStartedAsync(requireTun: false, _cts.Token);
                await RefreshRuntimeAsync(_cts.Token);
                await RestoreDesiredSystemProxyAsync(_cts.Token);
                DiagnosticLog.WriteAppException(
                    LogSources.Core,
                    ex,
                    "虚拟网卡恢复失败，内核已以普通模式恢复",
                    "WARN");
            }
            catch (Exception fallbackEx)
            {
                DiagnosticLog.WriteAppException(
                    LogSources.Core,
                    new AggregateException(ex, fallbackEx),
                    "内核自动恢复失败");
            }
        }
    }

    private static async Task<T?> Safe<T>(
        Task<T> task,
        string key,
        string source,
        string context)
        where T : class
    {
        try { return await task; }
        catch (Exception ex)
        {
            DiagnosticLog.WriteAppExceptionThrottled(key, source, ex, context);
            return null;
        }
    }

    private static async Task SafeVoid(Task task, string source)
    {
        try { await task; }
        catch (Exception ex)
        {
            DiagnosticLog.WriteAppException(source, ex);
        }
    }

    private bool IsAppCancellation(Exception exception) =>
        exception is OperationCanceledException && _cts.IsCancellationRequested;

    private bool IsSystemProxyEnabledForApp(AppSettings settings)
    {
        var port = _activeSystemProxyPort ?? Runtime.MixedPortNumber;
        return _systemProxy.IsEnabledFor(port, settings);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposeStarted, 1) != 0)
        {
            return;
        }

        _cts.Cancel();
        DiagnosticLog.AppEntryWritten -= OnAppEntryWritten;

        await _ws.DisposeAsync();
        await _profileAutoUpdate.DisposeAsync();
        await _overrideAutoUpdate.DisposeAsync();
        _overrideService.Dispose();
        await StopBackgroundLoopsAsync();
        await StopNetworkStateOnExitAsync();
        await _core.DisposeAsync();
        _api.Dispose();
        Profiles.Dispose();
        _systemProxyLock.Dispose();
        _tunLock.Dispose();
        _allowLanLock.Dispose();
        _modeLock.Dispose();
        _configMutationLock.Dispose();
        _cts.Dispose();
        await Task.CompletedTask;
    }

    private async Task StopNetworkStateOnExitAsync()
    {
        try
        {
            await Task.WhenAll(
                    RestoreSystemProxyOnExitAsync(),
                    DisableTunAndStopCoreOnExitAsync())
                .WaitAsync(TimeSpan.FromSeconds(6));
        }
        catch (Exception ex) when (ex is OperationCanceledException or TimeoutException)
        {
            if (ex is TimeoutException)
            {
                DiagnosticLog.WriteAppException("EXIT", ex, "退出时清理网络状态超时", "WARN");
            }
        }
        catch (Exception ex)
        {
            DiagnosticLog.WriteAppException("EXIT-NETWORK", ex);
        }
    }

    private async Task StopBackgroundLoopsAsync()
    {
        var tasks = new[] { _runtimeLoopTask, _proxyLoopTask, _rulesLoopTask }
            .Where(task => task is not null)
            .Cast<Task>()
            .ToArray();
        if (tasks.Length == 0)
        {
            return;
        }

        try
        {
            await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(2));
        }
        catch (Exception ex) when (ex is OperationCanceledException or TimeoutException)
        {
            if (ex is TimeoutException)
            {
                DiagnosticLog.WriteAppException("EXIT", ex, "退出时停止后台任务超时", "WARN");
            }
        }
        catch (Exception ex)
        {
            DiagnosticLog.WriteAppException("EXIT-BACKGROUND", ex);
        }
    }

    private async Task RestoreSystemProxyOnExitAsync()
    {
        try
        {
            var settings = await AppSettingsService.LoadAsync(CancellationToken.None);
            var ownsCurrentProxy = await Task.Run(() => IsSystemProxyEnabledForApp(settings));
            if (!ownsCurrentProxy)
            {
                return;
            }

            await Task.Run(() =>
            {
                _systemProxy.Disable();
                _activeSystemProxyPort = null;
            }).WaitAsync(TimeSpan.FromSeconds(2));
        }
        catch (Exception ex)
        {
            DiagnosticLog.WriteAppException("EXIT", ex, "退出时恢复系统代理失败", "WARN");
        }
    }

    private async Task DisableTunAndStopCoreOnExitAsync()
    {
        if (_core.RunMode != CoreRunMode.NotRunning || _core.IsRunning)
        {
            try
            {
                using var tunTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
                await _api.PatchTunAsync(false, tunTimeout.Token);
            }
            catch (Exception ex)
            {
                DiagnosticLog.WriteAppException("EXIT", ex, "退出时关闭虚拟网卡失败", "WARN");
            }
        }

        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(4));
            await _core.StopAsync(timeout.Token);
        }
        catch (Exception ex)
        {
            DiagnosticLog.WriteAppException("EXIT", ex, "退出时停止内核失败", "WARN");
        }
    }
}
