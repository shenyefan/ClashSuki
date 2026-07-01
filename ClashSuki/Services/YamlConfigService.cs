using System.Collections;
using System.Globalization;
using System.IO;
using System.Text;
using YamlDotNet.RepresentationModel;

namespace ClashSuki.Services;

public static class YamlConfigService
{
    public sealed record TunSectionSettings(
        string Stack,
        bool AutoRoute,
        bool AutoDetectInterface,
        bool StrictRoute,
        string Mtu,
        string DeviceName,
        string DnsHijack,
        string RouteExcludeAddress);

    public sealed record DnsSectionSettings(
        bool Enable,
        string EnhancedMode,
        bool Ipv6,
        bool RespectRules,
        bool UseHosts,
        bool UseSystemHosts,
        string FakeIpRange,
        string FakeIpFilter,
        string FakeIpFilterMode,
        string Nameserver,
        string Fallback,
        string DefaultNameserver,
        string DirectNameserver,
        string ProxyServerNameserver,
        string FallbackGeoIp,
        string FallbackGeoIpCode,
        string FallbackIpCidr,
        string FallbackDomain,
        string Hosts);

    public sealed record SnifferSectionSettings(
        bool Enable,
        bool OverrideDestination,
        bool ForceDnsMapping,
        bool ParsePureIp,
        string HttpPorts,
        string TlsPorts,
        string QuicPorts,
        string SkipDomain,
        string ForceDomain,
        string SkipDstAddress,
        string SkipSrcAddress);

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
        string LanAllowedIps,
        string LanDisallowedIps,
        string Authentication,
        string SkipAuthPrefixes,
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

    public static string MergeWithBase(string subscriptionYaml, string baseYaml)
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

            if (scalarKey.Value.Equals("tun", StringComparison.OrdinalIgnoreCase))
            {
                MergeTun(subscriptionRoot, valueNode);
                continue;
            }

            subscriptionRoot.Children[new YamlScalarNode(scalarKey.Value)] = CloneNode(valueNode);
        }

        SetScalar(subscriptionRoot, "secret", "", overwrite: true);
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

    public static async Task PersistTunSettingAsync(bool enabled, CancellationToken cancellationToken)
    {
        await PersistTunSettingAsync(enabled, enableDns: enabled, cancellationToken);
    }

    public static async Task PersistTunSettingAsync(bool enabled, bool enableDns, CancellationToken cancellationToken)
    {
        await PersistTunStateAsync(
            enabled,
            dnsEnabled: enableDns ? true : null,
            cancellationToken);
    }

    public static async Task PersistTunStateAsync(
        bool enabled,
        bool? dnsEnabled,
        CancellationToken cancellationToken)
    {
        await AppPaths.BootstrapAsync(cancellationToken);
        foreach (var path in new[]
                 {
                     AppPaths.BaseConfigPath,
                     AppPaths.ConfigPath,
                     AppPaths.RuntimeConfigPath
                 }.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!File.Exists(path))
            {
                continue;
            }

            var yaml = await File.ReadAllTextAsync(path, cancellationToken);
            var doc = LoadDocument(yaml);
            var root = EnsureRoot(doc);
            var tun = EnsureMap(root, "tun");
            SetScalar(tun, "enable", enabled.ToString().ToLowerInvariant(), overwrite: true);
            if (dnsEnabled.HasValue)
            {
                var dns = EnsureMap(root, "dns");
                SetScalar(
                    dns,
                    "enable",
                    dnsEnabled.Value.ToString().ToLowerInvariant(),
                    overwrite: true);
            }

            await File.WriteAllTextAsync(path, SaveDocument(doc), cancellationToken);
        }
    }

    public static async Task PersistPatchAsync(
        IReadOnlyDictionary<string, object?> patch,
        CancellationToken cancellationToken,
        IReadOnlySet<string>? replaceRootMappings = null)
    {
        if (patch.Count == 0)
        {
            return;
        }

        await AppPaths.BootstrapAsync(cancellationToken);
        foreach (var path in new[] { AppPaths.BaseConfigPath, AppPaths.ConfigPath })
        {
            if (!File.Exists(path))
            {
                continue;
            }

            var yaml = await File.ReadAllTextAsync(path, cancellationToken);
            var doc = LoadDocument(yaml);
            var root = EnsureRoot(doc);
            MergePatch(root, patch, replaceRootMappings);
            await File.WriteAllTextAsync(path, SaveDocument(doc), cancellationToken);
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
        return SaveDocument(doc);
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

    public static string SetTunEnabled(string yaml, bool enabled)
    {
        var doc = LoadDocument(yaml);
        var tun = EnsureMap(EnsureRoot(doc), "tun");
        SetScalar(tun, "enable", enabled.ToString().ToLowerInvariant(), overwrite: true);
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
            TryGetString(tun, "mtu") ?? "9000",
            TryGetString(tun, "device-name") ?? TryGetString(tun, "device") ?? "",
            ReadLines(tun, "dns-hijack", "any:53"),
            ReadLines(tun, "route-exclude-address", ""));
    }

    public static async Task<DnsSectionSettings> LoadDnsSettingsAsync(string path, CancellationToken cancellationToken)
    {
        var root = await LoadRootFromFileAsync(path, cancellationToken);
        var dns = TryGetMap(root, "dns");
        return new DnsSectionSettings(
            TryGetBool(dns, "enable") ?? true,
            TryGetString(dns, "enhanced-mode") ?? "fake-ip",
            TryGetBool(dns, "ipv6") ?? false,
            TryGetBool(dns, "respect-rules") ?? false,
            TryGetBool(dns, "use-hosts") ?? false,
            TryGetBool(dns, "use-system-hosts") ?? true,
            TryGetString(dns, "fake-ip-range") ?? "198.18.0.0/15",
            ReadLines(dns, "fake-ip-filter", "*.lan\nlocalhost.ptlogin2.qq.com"),
            TryGetString(dns, "fake-ip-filter-mode") ?? "blacklist",
            ReadLines(dns, "nameserver", "114.114.114.114\n8.8.8.8"),
            ReadLines(dns, "fallback", "tls://1.1.1.1\ntls://8.8.4.4"),
            ReadLines(dns, "default-nameserver", "114.114.114.114\n8.8.8.8"),
            ReadLines(dns, "direct-nameserver", ""),
            ReadLines(dns, "proxy-server-nameserver", ""),
            TryGetBool(TryGetMap(dns, "fallback-filter"), "geoip")?.ToString().ToLowerInvariant() ?? "true",
            TryGetString(TryGetMap(dns, "fallback-filter"), "geoip-code") ?? "CN",
            ReadLines(TryGetMap(dns, "fallback-filter"), "ipcidr", ""),
            ReadLines(TryGetMap(dns, "fallback-filter"), "domain", ""),
            FormatSimpleMapping(TryGetMap(root, "hosts")));
    }

    public static async Task<SnifferSectionSettings> LoadSnifferSettingsAsync(string path, CancellationToken cancellationToken)
    {
        var root = await LoadRootFromFileAsync(path, cancellationToken);
        var sniffer = TryGetMap(root, "sniffer");
        var sniff = TryGetMap(sniffer, "sniff");
        return new SnifferSectionSettings(
            TryGetBool(sniffer, "enable") ?? true,
            TryGetBool(sniffer, "override-destination") ?? false,
            TryGetBool(sniffer, "force-dns-mapping") ?? true,
            TryGetBool(sniffer, "parse-pure-ip") ?? false,
            ReadPorts(sniff, "HTTP", "80"),
            ReadPorts(sniff, "TLS", "443"),
            ReadPorts(sniff, "QUIC", "443"),
            ReadLines(sniffer, "skip-domain", ""),
            ReadLines(sniffer, "force-domain", ""),
            ReadLines(sniffer, "skip-dst-address", ""),
            ReadLines(sniffer, "skip-src-address", ""));
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
            ReadLines(root, "lan-allowed-ips", ""),
            ReadLines(root, "lan-disallowed-ips", ""),
            ReadLines(root, "authentication", ""),
            ReadLines(root, "skip-auth-prefixes", "127.0.0.1/8\n::1/128"),
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

    public static async Task PersistGeoDataSettingsAsync(GeoDataSettings settings, CancellationToken cancellationToken)
    {
        await AppPaths.BootstrapAsync(cancellationToken);
        foreach (var path in new[] { AppPaths.BaseConfigPath, AppPaths.ConfigPath })
        {
            if (!File.Exists(path))
            {
                continue;
            }

            var yaml = await File.ReadAllTextAsync(path, cancellationToken);
            var doc = LoadDocument(yaml);
            var root = EnsureRoot(doc);
            var geoxUrl = EnsureMap(root, "geox-url");
            SetScalar(geoxUrl, "geoip", settings.GeoIpUrl, overwrite: true);
            SetScalar(geoxUrl, "geosite", settings.GeoSiteUrl, overwrite: true);
            SetScalar(geoxUrl, "mmdb", settings.MmdbUrl, overwrite: true);
            SetScalar(geoxUrl, "asn", settings.AsnUrl, overwrite: true);
            SetScalar(root, "geodata-mode", settings.GeoDataMode.ToString().ToLowerInvariant(), overwrite: true);
            SetScalar(root, "geo-auto-update", settings.AutoUpdate.ToString().ToLowerInvariant(), overwrite: true);
            SetScalar(root, "geo-update-interval", settings.UpdateInterval.ToString(CultureInfo.InvariantCulture), overwrite: true);
            await File.WriteAllTextAsync(path, SaveDocument(doc), cancellationToken);
        }
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

        SetScalar(root, "mixed-port", replacement.ToString(CultureInfo.InvariantCulture), overwrite: true);
        await File.WriteAllTextAsync(configPath, SaveDocument(doc), cancellationToken);
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

    private static int? TryGetInt(YamlMappingNode map, string key)
    {
        var keyNode = new YamlScalarNode(key);
        if (map.Children.TryGetValue(keyNode, out var node) &&
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

    private static string SaveNode(YamlNode node)
    {
        var document = new YamlDocument(CloneNode(node));
        return SaveDocument(document);
    }

    private static string ReadLines(YamlMappingNode? map, string key, string fallback) =>
        TryGetNode(map, key) is null
            ? fallback
            : string.Join(Environment.NewLine, TryGetStringList(map, key));

    private static string ReadPorts(YamlMappingNode? sniff, string key, string fallback) =>
        TryGetNode(TryGetMap(sniff, key), "ports") is null
            ? fallback
            : string.Join(',', TryGetPorts(sniff, key));

    private static string FormatSimpleMapping(YamlMappingNode? map)
    {
        if (map is null || map.Children.Count == 0)
        {
            return "";
        }

        var lines = new List<string>();
        foreach (var (keyNode, valueNode) in map.Children)
        {
            if (keyNode is not YamlScalarNode { Value: { Length: > 0 } key })
            {
                continue;
            }

            var value = valueNode switch
            {
                YamlSequenceNode sequence => string.Join(',', sequence.Children
                    .OfType<YamlScalarNode>()
                    .Select(item => item.Value ?? "")
                    .Where(item => !string.IsNullOrWhiteSpace(item))),
                YamlScalarNode scalar => scalar.Value ?? "",
                _ => SaveNode(valueNode).Trim()
            };

            lines.Add($"{key}={value}");
        }

        return string.Join(Environment.NewLine, lines);
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
