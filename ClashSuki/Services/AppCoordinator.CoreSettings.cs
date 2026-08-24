namespace ClashSuki.Services;

public sealed partial class AppCoordinator
{
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
                ["enable"] = settings.Enabled,
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
        await WindowsFirewallService.SetupMihomoRulesAsync(_serviceManager, _cts.Token);
        await RestartCoreAsync();
    }

    public Task FlushFakeIpAsync() => _api.FlushFakeIpAsync(_cts.Token);
}
