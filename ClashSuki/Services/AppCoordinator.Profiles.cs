using ClashSuki.Stores;

namespace ClashSuki.Services;

public sealed partial class AppCoordinator
{
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
            var profile = await Profiles.AddRemoteAsync(
                name,
                url,
                userAgent,
                authToken,
                ageSecretKey,
                fileName,
                Runtime.MixedPortNumber > 0 ? Runtime.MixedPortNumber : null,
                _cts.Token);

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
            var profile = await Profiles.ImportLocalAsync(name, fileName, content, _cts.Token);

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
                await ActivateProfileOrThrowAsync(uid);
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
        try
        {
            await ActivateProfileOrThrowAsync(uid);
            if (reportResult)
            {
                await _dispatcher.RunAsync(() =>
                    Logs.AddApp("INFO", "订阅已启用", LogSources.Subscription));
            }

            return true;
        }
        catch (Exception ex) when (!IsAppCancellation(ex))
        {
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

    private async Task ActivateProfileOrThrowAsync(string uid)
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
        }
        catch
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

            throw;
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

        if (isUri)
        {
            return WindowsShellLauncher.LaunchUriAsync(uri!, label);
        }

        if (Directory.Exists(path))
        {
            return WindowsShellLauncher.LaunchFolderPathAsync(path, label);
        }

        return WindowsShellLauncher.LaunchFileAsync(path, label);
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
}
