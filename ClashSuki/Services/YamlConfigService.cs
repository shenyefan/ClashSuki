using System.Collections;
using System.Globalization;
using System.IO;
using System.Text;
using YamlDotNet.RepresentationModel;

namespace ClashSuki.Services;

public static class YamlConfigService
{
    private static readonly SemaphoreSlim BaseWriteLock = new(1, 1);

    public sealed record TunSectionSettings(
        string Stack,
        bool AutoRoute,
        bool AutoDetectInterface,
        bool StrictRoute,
        int Mtu,
        string DeviceName,
        IReadOnlyList<string> DnsHijack,
        IReadOnlyList<string> RouteExcludeAddress);

    public sealed record DnsSectionSettings(
        bool OverrideEnabled,
        bool Enabled,
        string EnhancedMode,
        bool Ipv6,
        bool RespectRules,
        bool UseHosts,
        bool UseSystemHosts,
        string FakeIpRange,
        IReadOnlyList<string> FakeIpFilter,
        string FakeIpFilterMode,
        IReadOnlyList<string> Nameserver,
        IReadOnlyList<string> Fallback,
        IReadOnlyList<string> DefaultNameserver,
        IReadOnlyList<string> DirectNameserver,
        IReadOnlyList<string> ProxyServerNameserver,
        bool FallbackGeoIp,
        string FallbackGeoIpCode,
        IReadOnlyList<string> FallbackIpCidr,
        IReadOnlyList<string> FallbackDomain,
        IReadOnlyDictionary<string, IReadOnlyList<string>> Hosts);

    public sealed record SnifferSectionSettings(
        bool OverrideEnabled,
        bool Enabled,
        bool OverrideDestination,
        bool ForceDnsMapping,
        bool ParsePureIp,
        IReadOnlyList<string> HttpPorts,
        IReadOnlyList<string> TlsPorts,
        IReadOnlyList<string> QuicPorts,
        IReadOnlyList<string> SkipDomain,
        IReadOnlyList<string> ForceDomain,
        IReadOnlyList<string> SkipDstAddress,
        IReadOnlyList<string> SkipSrcAddress);

    public sealed record CoreSectionSettings(
        bool Ipv6,
        bool UnifiedDelay,
        bool TcpConcurrent,
        string LogLevel,
        string FindProcessMode,
        int MixedPort,
        int SocksPort,
        int HttpPort,
        int RedirPort,
        int TproxyPort,
        string ExternalController,
        string Secret,
        bool AllowLan,
        IReadOnlyList<string> LanAllowedIps,
        IReadOnlyList<string> LanDisallowedIps,
        IReadOnlyList<string> Authentication,
        IReadOnlyList<string> SkipAuthPrefixes,
        bool StoreSelected,
        bool StoreFakeIp);

    public sealed record RuleProviderConfigInfo(
        string Name,
        string Type,
        string VehicleType,
        string Behavior,
        string Format,
        string Path,
        string Url,
        string Payload);

    public sealed record GeoDataSettings(
        string GeoIpUrl,
        string GeoSiteUrl,
        string MmdbUrl,
        string AsnUrl,
        bool GeoDataMode,
        bool AutoUpdate,
        int UpdateInterval);

    public static string MergeWithBase(
        string subscriptionYaml,
        string baseYaml,
        IReadOnlySet<string>? excludedRootKeys = null)
    {
        var subscriptionDoc = LoadDocument(subscriptionYaml);
        var subscriptionRoot = EnsureRoot(subscriptionDoc);
        var baseRoot = EnsureRoot(LoadDocument(baseYaml));

        foreach (var (keyNode, valueNode) in baseRoot.Children)
        {
            if (keyNode is not YamlScalarNode scalarKey || string.IsNullOrWhiteSpace(scalarKey.Value))
            {
                continue;
            }

            if (excludedRootKeys?.Contains(scalarKey.Value) == true)
            {
                continue;
            }

            if (scalarKey.Value.Equals("tun", StringComparison.OrdinalIgnoreCase))
            {
                MergeTun(subscriptionRoot, valueNode);
                continue;
            }

            subscriptionRoot.Children[new YamlScalarNode(scalarKey.Value)] = CloneNode(valueNode);
        }

        var baseController = TryGetString(baseRoot, "external-controller") ?? "";
        SetScalar(subscriptionRoot, "external-controller", baseController, overwrite: true);
        SetScalar(subscriptionRoot, "mixed-port", "7890", overwrite: false);
        SetScalar(subscriptionRoot, "allow-lan", "false", overwrite: false);
        SetScalar(subscriptionRoot, "mode", "rule", overwrite: false);
        SetScalar(subscriptionRoot, "log-level", "info", overwrite: false);

        return SaveDocument(subscriptionDoc);
    }

    public static string EnsureGlobalConfig(string yaml)
    {
        var doc = LoadDocument(yaml);
        var root = EnsureRoot(doc);

        SetScalar(root, "mixed-port", "7890", overwrite: false);
        SetScalar(root, "socks-port", "7891", overwrite: false);
        SetScalar(root, "port", "7892", overwrite: false);
        SetScalar(root, "allow-lan", "false", overwrite: false);
        SetScalar(root, "mode", "rule", overwrite: false);
        SetScalar(root, "log-level", "info", overwrite: false);
        SetScalar(root, "ipv6", "false", overwrite: false);
        SetScalar(root, "secret", "", overwrite: false);
        SetScalar(root, "external-controller", "", overwrite: false);
        SetScalar(root, "external-ui", "", overwrite: false);
        SetScalar(root, "unified-delay", "true", overwrite: false);
        SetScalar(root, "tcp-concurrent", "true", overwrite: false);
        SetScalar(root, "find-process-mode", "strict", overwrite: false);
        if (!root.Children.ContainsKey(new YamlScalarNode("skip-auth-prefixes")))
        {
            root.Children[new YamlScalarNode("skip-auth-prefixes")] = ToYamlSequenceNode(new[] { "127.0.0.1/8", "::1/128" });
        }

        var profile = EnsureMap(root, "profile");
        SetScalar(profile, "store-selected", "true", overwrite: false);
        SetScalar(profile, "store-fake-ip", "true", overwrite: false);

        var tun = EnsureMap(root, "tun");
        SetScalar(tun, "enable", "false", overwrite: false);
        SetScalar(tun, "stack", "system", overwrite: false);
        SetScalar(tun, "auto-route", "true", overwrite: false);
        SetScalar(tun, "auto-detect-interface", "true", overwrite: false);
        var dns = EnsureMap(root, "dns");
        SetScalar(dns, "enable", "false", overwrite: false);

        return SaveDocument(doc);
    }

    public static async Task PersistBasePatchAsync(
        IReadOnlyDictionary<string, object?> patch,
        CancellationToken cancellationToken,
        IReadOnlySet<string>? replaceRootMappings = null)
    {
        if (patch.Count == 0)
        {
            return;
        }

        await AppPaths.BootstrapAsync(cancellationToken);
        await BaseWriteLock.WaitAsync(cancellationToken);
        try
        {
            var yaml = await File.ReadAllTextAsync(AppPaths.BaseConfigPath, cancellationToken);
            var doc = LoadDocument(yaml);
            MergePatch(EnsureRoot(doc), patch, replaceRootMappings);
            await WriteTextAtomicAsync(
                AppPaths.BaseConfigPath,
                SaveAndVerifyDocument(doc),
                cancellationToken);
        }
        finally
        {
            BaseWriteLock.Release();
        }
    }

    public static async Task NormalizeBaseFileAsync(
        string path,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return;
        }

        await BaseWriteLock.WaitAsync(cancellationToken);
        try
        {
            var yaml = await File.ReadAllTextAsync(path, cancellationToken);
            var doc = LoadDocument(yaml);
            var root = EnsureRoot(doc);
            var changed = false;
            var tun = TryGetMap(root, "tun");
            if (tun is not null)
            {
                var legacyDeviceKey = new YamlScalarNode("device-name");
                if (tun.Children.TryGetValue(legacyDeviceKey, out var legacyDevice))
                {
                    var deviceKey = new YamlScalarNode("device");
                    if (!tun.Children.ContainsKey(deviceKey))
                    {
                        tun.Children[deviceKey] = CloneNode(legacyDevice);
                    }

                    tun.Children.Remove(legacyDeviceKey);
                    changed = true;
                }
            }

            foreach (var key in new[] { "external-controller-unix", "external-controller-tls" })
            {
                changed |= root.Children.Remove(new YamlScalarNode(key));
            }

            var externalControllerKey = new YamlScalarNode("external-controller");
            if (root.Children.TryGetValue(externalControllerKey, out var controllerNode) &&
                controllerNode is YamlScalarNode controller &&
                string.IsNullOrWhiteSpace(controller.Value))
            {
                root.Children.Remove(externalControllerKey);
                changed = true;
            }

            if (changed)
            {
                await WriteTextAtomicAsync(
                    path,
                    SaveAndVerifyDocument(doc),
                    cancellationToken);
            }
        }
        finally
        {
            BaseWriteLock.Release();
        }
    }

    public static async Task<bool> EnableGeoIpForBundledFactoryDnsAsync(
        string path,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return false;
        }

        await BaseWriteLock.WaitAsync(cancellationToken);
        try
        {
            var yaml = await File.ReadAllTextAsync(path, cancellationToken);
            var doc = LoadDocument(yaml);
            var root = EnsureRoot(doc);
            var dns = TryGetMap(root, "dns");
            var fallbackFilter = TryGetMap(dns, "fallback-filter");
            if (dns is null ||
                fallbackFilter is null ||
                TryGetBool(fallbackFilter, "geoip") != false ||
                !ReadList(dns, "nameserver", []).SequenceEqual(
                    ["114.114.114.114", "8.8.8.8"],
                    StringComparer.OrdinalIgnoreCase) ||
                !ReadList(dns, "fallback", []).SequenceEqual(
                    ["tls://1.1.1.1", "tls://8.8.4.4"],
                    StringComparer.OrdinalIgnoreCase) ||
                !string.Equals(
                    TryGetString(fallbackFilter, "geoip-code"),
                    "CN",
                    StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            SetScalar(fallbackFilter, "geoip", "true", overwrite: true);
            await WriteTextAtomicAsync(
                path,
                SaveAndVerifyDocument(doc),
                cancellationToken);
            return true;
        }
        finally
        {
            BaseWriteLock.Release();
        }
    }

    private static async Task WriteTextAtomicAsync(
        string path,
        string content,
        CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(path)
                        ?? throw new InvalidOperationException("无法确定配置文件目录");
        Directory.CreateDirectory(directory);
        var tempPath = Path.Combine(
            directory,
            $"{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await File.WriteAllTextAsync(tempPath, content, cancellationToken);
            File.Move(tempPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
    }

    public static async Task<string> BuildPatchedConfigAsync(
        string path,
        IReadOnlyDictionary<string, object?> patch,
        CancellationToken cancellationToken,
        IReadOnlySet<string>? replaceRootMappings = null)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("配置文件不存在。", path);
        }

        var yaml = await File.ReadAllTextAsync(path, cancellationToken);
        var doc = LoadDocument(yaml);
        MergePatch(EnsureRoot(doc), patch, replaceRootMappings);
        return SaveAndVerifyDocument(doc);
    }

    public static string RemoveRootKeys(string yaml, IEnumerable<string> keys)
    {
        var doc = LoadDocument(yaml);
        var root = EnsureRoot(doc);
        foreach (var key in keys)
        {
            root.Children.Remove(new YamlScalarNode(key));
        }

        return SaveDocument(doc);
    }

    public static bool IsTunEnabled(string yaml)
    {
        var root = EnsureRoot(LoadDocument(yaml));
        return TryGetNestedBool(root, "tun", "enable") ?? false;
    }

    public static async Task<bool> IsTunEnabledAsync(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return false;
        }

        var yaml = await File.ReadAllTextAsync(path, cancellationToken);
        return IsTunEnabled(yaml);
    }

    public static async Task<bool> IsDnsEnabledAsync(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return false;
        }

        var yaml = await File.ReadAllTextAsync(path, cancellationToken);
        var root = EnsureRoot(LoadDocument(yaml));
        return TryGetNestedBool(root, "dns", "enable") ?? false;
    }

    public static async Task<bool> IsAllowLanEnabledAsync(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return false;
        }

        var yaml = await File.ReadAllTextAsync(path, cancellationToken);
        var root = EnsureRoot(LoadDocument(yaml));
        return TryGetBool(root, "allow-lan") ?? false;
    }

    public static async Task<TunSectionSettings> LoadTunSettingsAsync(string path, CancellationToken cancellationToken)
    {
        var root = await LoadRootFromFileAsync(path, cancellationToken);
        var tun = TryGetMap(root, "tun");
        return new TunSectionSettings(
            TryGetString(tun, "stack") ?? "gVisor",
            TryGetBool(tun, "auto-route") ?? true,
            TryGetBool(tun, "auto-detect-interface") ?? true,
            TryGetBool(tun, "strict-route") ?? false,
            TryGetInt(tun, "mtu") ?? 9000,
            TryGetString(tun, "device") ?? TryGetString(tun, "device-name") ?? "",
            ReadList(tun, "dns-hijack", ["any:53"]),
            ReadList(tun, "route-exclude-address", []));
    }

    public static async Task<DnsSectionSettings> LoadDnsSettingsAsync(string path, CancellationToken cancellationToken)
    {
        var root = await LoadRootFromFileAsync(path, cancellationToken);
        var dns = TryGetMap(root, "dns");
        return new DnsSectionSettings(
            true,
            TryGetBool(dns, "enable") ?? true,
            TryGetString(dns, "enhanced-mode") ?? "fake-ip",
            TryGetBool(dns, "ipv6") ?? false,
            TryGetBool(dns, "respect-rules") ?? false,
            TryGetBool(dns, "use-hosts") ?? false,
            TryGetBool(dns, "use-system-hosts") ?? true,
            TryGetString(dns, "fake-ip-range") ?? "198.18.0.0/15",
            ReadList(dns, "fake-ip-filter", ["*.lan", "localhost.ptlogin2.qq.com"]),
            TryGetString(dns, "fake-ip-filter-mode") ?? "blacklist",
            ReadList(dns, "nameserver", ["114.114.114.114", "8.8.8.8"]),
            ReadList(dns, "fallback", ["tls://1.1.1.1", "tls://8.8.4.4"]),
            ReadList(dns, "default-nameserver", ["114.114.114.114", "8.8.8.8"]),
            ReadList(dns, "direct-nameserver", []),
            ReadList(dns, "proxy-server-nameserver", []),
            TryGetBool(TryGetMap(dns, "fallback-filter"), "geoip") ?? true,
            TryGetString(TryGetMap(dns, "fallback-filter"), "geoip-code") ?? "CN",
            ReadList(TryGetMap(dns, "fallback-filter"), "ipcidr", []),
            ReadList(TryGetMap(dns, "fallback-filter"), "domain", []),
            ReadSimpleMapping(TryGetMap(root, "hosts")));
    }

    public static async Task<SnifferSectionSettings> LoadSnifferSettingsAsync(string path, CancellationToken cancellationToken)
    {
        var root = await LoadRootFromFileAsync(path, cancellationToken);
        var sniffer = TryGetMap(root, "sniffer");
        var sniff = TryGetMap(sniffer, "sniff");
        return new SnifferSectionSettings(
            true,
            TryGetBool(sniffer, "enable") ?? true,
            TryGetBool(sniffer, "override-destination") ?? false,
            TryGetBool(sniffer, "force-dns-mapping") ?? true,
            TryGetBool(sniffer, "parse-pure-ip") ?? false,
            ReadPorts(sniff, "HTTP", ["80"]),
            ReadPorts(sniff, "TLS", ["443"]),
            ReadPorts(sniff, "QUIC", ["443"]),
            ReadList(sniffer, "skip-domain", []),
            ReadList(sniffer, "force-domain", []),
            ReadList(sniffer, "skip-dst-address", []),
            ReadList(sniffer, "skip-src-address", []));
    }

    public static async Task<CoreSectionSettings> LoadCoreSettingsAsync(string path, CancellationToken cancellationToken)
    {
        var root = await LoadRootFromFileAsync(path, cancellationToken);
        var profile = TryGetMap(root, "profile");
        return new CoreSectionSettings(
            TryGetBool(root, "ipv6") ?? false,
            TryGetBool(root, "unified-delay") ?? true,
            TryGetBool(root, "tcp-concurrent") ?? true,
            TryGetString(root, "log-level") ?? "info",
            TryGetString(root, "find-process-mode") ?? "strict",
            TryGetInt(root, "mixed-port") ?? 7890,
            TryGetInt(root, "socks-port") ?? 7891,
            TryGetInt(root, "port") ?? 7892,
            TryGetInt(root, "redir-port") ?? 0,
            TryGetInt(root, "tproxy-port") ?? 0,
            TryGetString(root, "external-controller") ?? "",
            TryGetString(root, "secret") ?? "",
            TryGetBool(root, "allow-lan") ?? false,
            ReadList(root, "lan-allowed-ips", []),
            ReadList(root, "lan-disallowed-ips", []),
            ReadList(root, "authentication", []),
            ReadList(root, "skip-auth-prefixes", ["127.0.0.1/8", "::1/128"]),
            TryGetBool(profile, "store-selected") ?? true,
            TryGetBool(profile, "store-fake-ip") ?? true);
    }

    public static async Task<Dictionary<string, RuleProviderConfigInfo>> LoadRuleProviderConfigsAsync(
        string path,
        CancellationToken cancellationToken)
    {
        var root = await LoadRootFromFileAsync(path, cancellationToken);
        var providers = TryGetMap(root, "rule-providers");
        if (providers is null)
        {
            return [];
        }

        var result = new Dictionary<string, RuleProviderConfigInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in providers.Children)
        {
            if (key is not YamlScalarNode { Value: { Length: > 0 } name } ||
                value is not YamlMappingNode provider)
            {
                continue;
            }

            var type = TryGetString(provider, "type") ?? "";
            var vehicleType = string.IsNullOrWhiteSpace(type) ? "" : char.ToUpperInvariant(type[0]) + type[1..];
            var behavior = TryGetString(provider, "behavior") ?? "domain";
            var format = TryGetString(provider, "format") ?? "YamlRule";
            var providerPath = TryGetString(provider, "path") ?? "";
            var url = TryGetString(provider, "url") ?? "";
            var payload = TryGetNode(provider, "payload") is { } payloadNode ? SaveNode(payloadNode) : "";

            result[name] = new RuleProviderConfigInfo(
                name,
                type,
                vehicleType,
                behavior,
                format,
                providerPath,
                url,
                payload);
        }

        return result;
    }

    public static async Task<GeoDataSettings> LoadGeoDataSettingsAsync(
        string path,
        CancellationToken cancellationToken)
    {
        var root = await LoadRootFromFileAsync(path, cancellationToken);
        var geoxUrl = TryGetMap(root, "geox-url");
        return new GeoDataSettings(
            TryGetString(geoxUrl, "geoip") ?? "https://github.com/MetaCubeX/meta-rules-dat/releases/download/latest/geoip-lite.dat",
            TryGetString(geoxUrl, "geosite") ?? "https://github.com/MetaCubeX/meta-rules-dat/releases/download/latest/geosite.dat",
            TryGetString(geoxUrl, "mmdb") ?? "https://github.com/MetaCubeX/meta-rules-dat/releases/download/latest/geoip.metadb",
            TryGetString(geoxUrl, "asn") ?? "https://github.com/MetaCubeX/meta-rules-dat/releases/download/latest/GeoLite2-ASN.mmdb",
            TryGetBool(root, "geodata-mode") ?? false,
            TryGetBool(root, "geo-auto-update") ?? false,
            TryGetInt(root, "geo-update-interval") ?? 24);
    }

    public static async Task EnsureMixedPortAvailableAsync(
        string configPath,
        Func<int, bool> isPortFree,
        Action<string> log,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(configPath))
        {
            return;
        }

        var yaml = await File.ReadAllTextAsync(configPath, cancellationToken);
        var doc = LoadDocument(yaml);
        var root = EnsureRoot(doc);
        var current = TryGetInt(root, "mixed-port");
        if (current is null || isPortFree(current.Value))
        {
            return;
        }

        var replacement = Enumerable.Range(current.Value + 1, 100).FirstOrDefault(isPortFree);
        if (replacement == 0)
        {
            log($"[port conflict] mixed-port {current} 被占用且找不到空闲端口");
            return;
        }

        if (string.Equals(
                Path.GetFullPath(configPath),
                Path.GetFullPath(AppPaths.RuntimeConfigPath),
                StringComparison.OrdinalIgnoreCase))
        {
            await PersistBasePatchAsync(
                new Dictionary<string, object?> { ["mixed-port"] = replacement },
                cancellationToken);
        }

        SetScalar(root, "mixed-port", replacement.ToString(CultureInfo.InvariantCulture), overwrite: true);
        await WriteTextAtomicAsync(configPath, SaveAndVerifyDocument(doc), cancellationToken);
        log($"[port conflict] mixed-port {current} 被其他程序占用，已自动改用 {replacement}");
    }

    private static YamlDocument LoadDocument(string yaml)
    {
        using var reader = new StringReader(string.IsNullOrWhiteSpace(yaml) ? "{}" : yaml);
        var stream = new YamlStream();
        stream.Load(reader);
        if (stream.Documents.Count == 0 || stream.Documents[0].RootNode is not YamlMappingNode)
        {
            return new YamlDocument(new YamlMappingNode());
        }

        return stream.Documents[0];
    }

    private static YamlMappingNode EnsureRoot(YamlDocument doc)
    {
        if (doc.RootNode is YamlMappingNode map)
        {
            return map;
        }

        throw new InvalidOperationException("YAML root must be a mapping node.");
    }

    private static async Task<YamlMappingNode> LoadRootFromFileAsync(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return EnsureRoot(LoadDocument("{}"));
        }

        var yaml = await File.ReadAllTextAsync(path, cancellationToken);
        return EnsureRoot(LoadDocument(yaml));
    }

    private static YamlMappingNode EnsureMap(YamlMappingNode parent, string key)
    {
        var keyNode = new YamlScalarNode(key);
        if (parent.Children.TryGetValue(keyNode, out var existing) && existing is YamlMappingNode map)
        {
            return map;
        }

        var created = new YamlMappingNode();
        parent.Children[keyNode] = created;
        return created;
    }

    private static void SetScalar(YamlMappingNode map, string key, string value, bool overwrite)
    {
        var keyNode = new YamlScalarNode(key);
        if (!overwrite && map.Children.ContainsKey(keyNode))
        {
            return;
        }

        map.Children[keyNode] = new YamlScalarNode(value);
    }

    private static int? TryGetInt(YamlMappingNode? map, string key)
    {
        var keyNode = new YamlScalarNode(key);
        if (map?.Children.TryGetValue(keyNode, out var node) == true &&
            node is YamlScalarNode scalar &&
            int.TryParse(scalar.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
        {
            return value;
        }

        return null;
    }

    private static YamlMappingNode? TryGetMap(YamlMappingNode? map, string key)
    {
        var keyNode = new YamlScalarNode(key);
        return map?.Children.TryGetValue(keyNode, out var node) == true && node is YamlMappingNode nested
            ? nested
            : null;
    }

    private static string? TryGetString(YamlMappingNode? map, string key)
    {
        var keyNode = new YamlScalarNode(key);
        return map?.Children.TryGetValue(keyNode, out var node) == true && node is YamlScalarNode scalar
            ? scalar.Value
            : null;
    }

    private static YamlNode? TryGetNode(YamlMappingNode? map, string key)
    {
        var keyNode = new YamlScalarNode(key);
        return map?.Children.TryGetValue(keyNode, out var node) == true ? node : null;
    }

    private static string[] TryGetStringList(YamlMappingNode? map, string key)
    {
        var keyNode = new YamlScalarNode(key);
        if (map?.Children.TryGetValue(keyNode, out var node) != true)
        {
            return [];
        }

        return node switch
        {
            YamlSequenceNode sequence => sequence.Children
                .OfType<YamlScalarNode>()
                .Select(item => item.Value ?? "")
                .Where(item => !string.IsNullOrWhiteSpace(item))
                .ToArray(),
            YamlScalarNode scalar when !string.IsNullOrWhiteSpace(scalar.Value) => [scalar.Value],
            _ => []
        };
    }

    private static string[] TryGetPorts(YamlMappingNode? sniff, string protocol)
    {
        var protocolMap = TryGetMap(sniff, protocol);
        return TryGetStringList(protocolMap, "ports");
    }

    private static bool? TryGetBool(YamlMappingNode? map, string key)
    {
        var keyNode = new YamlScalarNode(key);
        return map?.Children.TryGetValue(keyNode, out var node) == true && node is YamlScalarNode scalar
            ? ParseBool(scalar.Value)
            : null;
    }

    private static bool? TryGetNestedBool(YamlMappingNode map, string key, string nestedKey)
    {
        var keyNode = new YamlScalarNode(key);
        if (!map.Children.TryGetValue(keyNode, out var node) || node is not YamlMappingNode nested)
        {
            return null;
        }

        return TryGetBool(nested, nestedKey);
    }

    private static bool? ParseBool(string? value)
    {
        if (bool.TryParse(value, out var result))
        {
            return result;
        }

        return value?.Trim() switch
        {
            "1" => true,
            "0" => false,
            _ => null
        };
    }

    private static string SaveDocument(YamlDocument document)
    {
        var stream = new YamlStream(document);
        using var writer = new StringWriter(new StringBuilder(), CultureInfo.InvariantCulture);
        stream.Save(writer, assignAnchors: false);
        return writer.ToString();
    }

    private static string SaveAndVerifyDocument(YamlDocument document)
    {
        var yaml = SaveDocument(document);
        _ = EnsureRoot(LoadDocument(yaml));
        return yaml;
    }

    private static string SaveNode(YamlNode node)
    {
        var document = new YamlDocument(CloneNode(node));
        return SaveDocument(document);
    }

    private static IReadOnlyList<string> ReadList(
        YamlMappingNode? map,
        string key,
        IReadOnlyList<string> fallback) =>
        TryGetNode(map, key) is null
            ? fallback
            : TryGetStringList(map, key);

    private static IReadOnlyList<string> ReadPorts(
        YamlMappingNode? sniff,
        string key,
        IReadOnlyList<string> fallback) =>
        TryGetNode(TryGetMap(sniff, key), "ports") is null
            ? fallback
            : TryGetPorts(sniff, key);

    private static IReadOnlyDictionary<string, IReadOnlyList<string>> ReadSimpleMapping(
        YamlMappingNode? map)
    {
        if (map is null || map.Children.Count == 0)
        {
            return new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        }

        var values = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (keyNode, valueNode) in map.Children)
        {
            if (keyNode is not YamlScalarNode { Value: { Length: > 0 } key })
            {
                continue;
            }

            var value = valueNode switch
            {
                YamlSequenceNode sequence => sequence.Children
                    .OfType<YamlScalarNode>()
                    .Select(item => item.Value ?? "")
                    .Where(item => !string.IsNullOrWhiteSpace(item))
                    .ToArray(),
                YamlScalarNode scalar when !string.IsNullOrWhiteSpace(scalar.Value) => [scalar.Value],
                _ => []
            };

            if (value.Length > 0)
            {
                values[key] = value;
            }
        }

        return values;
    }

    private static void MergeTun(YamlMappingNode root, YamlNode patchNode)
    {
        if (patchNode is not YamlMappingNode patchTun)
        {
            root.Children[new YamlScalarNode("tun")] = CloneNode(patchNode);
            return;
        }

        var tun = EnsureMap(root, "tun");
        foreach (var (key, value) in patchTun.Children)
        {
            tun.Children[CloneNode(key)] = CloneNode(value);
        }
    }

    private static void MergePatch(
        YamlMappingNode target,
        IReadOnlyDictionary<string, object?> patch,
        IReadOnlySet<string>? replaceMappings = null)
    {
        foreach (var (key, value) in patch)
        {
            var keyNode = new YamlScalarNode(key);
            if (value is null)
            {
                target.Children.Remove(keyNode);
                continue;
            }

            if (replaceMappings?.Contains(key) == true)
            {
                target.Children[keyNode] = ToYamlNode(value);
                continue;
            }

            if (value is IReadOnlyDictionary<string, object?> nestedPatch &&
                target.Children.TryGetValue(keyNode, out var existingNode) &&
                existingNode is YamlMappingNode existingMap)
            {
                MergePatch(existingMap, nestedPatch);
                continue;
            }

            target.Children[keyNode] = ToYamlNode(value);
        }
    }

    private static YamlNode ToYamlNode(object? value)
    {
        return value switch
        {
            null => new YamlScalarNode(null),
            bool boolean => new YamlScalarNode(boolean.ToString().ToLowerInvariant()),
            byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal =>
                new YamlScalarNode(Convert.ToString(value, CultureInfo.InvariantCulture)),
            IReadOnlyDictionary<string, object?> map => ToYamlMappingNode(map),
            IDictionary<string, object?> map => ToYamlMappingNode(map),
            IEnumerable enumerable when value is not string => ToYamlSequenceNode(enumerable),
            _ => new YamlScalarNode(Convert.ToString(value, CultureInfo.InvariantCulture) ?? "")
        };
    }

    private static YamlMappingNode ToYamlMappingNode(IEnumerable<KeyValuePair<string, object?>> map)
    {
        var node = new YamlMappingNode();
        foreach (var (key, value) in map)
        {
            if (value is null)
            {
                continue;
            }

            node.Children[new YamlScalarNode(key)] = ToYamlNode(value);
        }

        return node;
    }

    private static YamlSequenceNode ToYamlSequenceNode(IEnumerable values)
    {
        var node = new YamlSequenceNode();
        foreach (var value in values)
        {
            node.Children.Add(ToYamlNode(value));
        }

        return node;
    }

    private static YamlNode CloneNode(YamlNode node)
    {
        return node switch
        {
            YamlScalarNode scalar => new YamlScalarNode(scalar.Value)
            {
                Style = scalar.Style,
                Tag = scalar.Tag
            },
            YamlSequenceNode sequence => CloneSequence(sequence),
            YamlMappingNode mapping => CloneMapping(mapping),
            _ => new YamlScalarNode(node.ToString())
        };
    }

    private static YamlSequenceNode CloneSequence(YamlSequenceNode source)
    {
        var clone = new YamlSequenceNode
        {
            Style = source.Style,
            Tag = source.Tag
        };
        foreach (var child in source.Children)
        {
            clone.Children.Add(CloneNode(child));
        }

        return clone;
    }

    private static YamlMappingNode CloneMapping(YamlMappingNode source)
    {
        var clone = new YamlMappingNode
        {
            Style = source.Style,
            Tag = source.Tag
        };
        foreach (var (key, value) in source.Children)
        {
            clone.Children.Add(CloneNode(key), CloneNode(value));
        }

        return clone;
    }

    public static async Task<List<string>> GetProxyGroupOrderAsync(string configPath, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(configPath))
            return [];

        var yaml = await File.ReadAllTextAsync(configPath, cancellationToken);
        var root = EnsureRoot(LoadDocument(yaml));
        var keyNode = new YamlScalarNode("proxy-groups");
        if (!root.Children.TryGetValue(keyNode, out var node) || node is not YamlSequenceNode seq)
            return [];

        var order = new List<string>();
        foreach (var item in seq.Children)
        {
            if (item is YamlMappingNode map &&
                map.Children.TryGetValue(new YamlScalarNode("name"), out var nameNode) &&
                nameNode is YamlScalarNode nameScalar &&
                !string.IsNullOrWhiteSpace(nameScalar.Value))
            {
                order.Add(nameScalar.Value);
            }
        }
        return order;
    }
}
