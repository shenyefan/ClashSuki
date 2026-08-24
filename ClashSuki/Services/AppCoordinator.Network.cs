namespace ClashSuki.Services;

public sealed partial class AppCoordinator
{
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
}
