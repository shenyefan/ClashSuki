using System.Collections.Concurrent;
using ClashSuki.Stores;

namespace ClashSuki.Services;

public sealed partial class AppCoordinator : IAsyncDisposable
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
        if (!settings.BundledGeoDataDefaultMigrated)
        {
            await YamlConfigService.EnableGeoIpForBundledFactoryDnsAsync(
                AppPaths.BaseConfigPath,
                _cts.Token);
            await AppSettingsService.PatchAsync(
                value => value.BundledGeoDataDefaultMigrated = true,
                _cts.Token);
            settings.BundledGeoDataDefaultMigrated = true;
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
                ["enable"] = settings.Enabled,
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
        var currentSsid = ssids.Count == 0
            ? null
            : await WindowsNetworkEnvironmentService.GetCurrentWifiSsidAsync(cancellationToken);
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
            throw new InvalidOperationException(
                PackageIdentityService.IsPackaged
                    ? "虚拟网卡服务不可用，请先修复服务"
                    : "虚拟网卡服务不可用，请先在虚拟网卡页面安装服务");
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
