using System.Text.Json;
using System.Text.Json.Serialization;

namespace ClashSuki.Models;

public sealed record VersionInfo(
    [property: JsonPropertyName("version")] string? Version);

public sealed record TrafficSnapshot(
    [property: JsonPropertyName("up")] long Up,
    [property: JsonPropertyName("down")] long Down,
    [property: JsonPropertyName("upTotal")] long? UpTotal,
    [property: JsonPropertyName("downTotal")] long? DownTotal);

public sealed record MemorySnapshot(
    [property: JsonPropertyName("inuse")] long InUse);

public sealed record MihomoLogEvent(
    [property: JsonPropertyName("type")] string? Type,
    [property: JsonPropertyName("payload")] string? Payload);

public sealed record ConfigSnapshot
{
    [JsonPropertyName("mode")]
    public string? Mode { get; init; }

    [JsonPropertyName("mixed-port")]
    public int? MixedPort { get; init; }

    [JsonPropertyName("port")]
    public int? Port { get; init; }

    [JsonPropertyName("socks-port")]
    public int? SocksPort { get; init; }

    [JsonPropertyName("redir-port")]
    public int? RedirPort { get; init; }

    [JsonPropertyName("tproxy-port")]
    public int? TproxyPort { get; init; }

    [JsonPropertyName("external-controller")]
    public string? ExternalController { get; init; }

    [JsonPropertyName("log-level")]
    public string? LogLevel { get; init; }

    [JsonPropertyName("allow-lan")]
    public bool? AllowLan { get; init; }

    [JsonPropertyName("tun")]
    public TunConfig? Tun { get; init; }
}

public sealed record TunConfig
{
    [JsonPropertyName("enable")]
    public bool? Enable { get; init; }

    [JsonPropertyName("stack")]
    public string? Stack { get; init; }

    [JsonPropertyName("device")]
    public string? Device { get; init; }

    [JsonPropertyName("auto-route")]
    public bool? AutoRoute { get; init; }

    [JsonPropertyName("auto-detect-interface")]
    public bool? AutoDetectInterface { get; init; }
}

// --- Proxies ---

public sealed record ProxyGroupsResponse(
    [property: JsonPropertyName("proxies")] Dictionary<string, ProxyGroupDto> Proxies);

public sealed record ProxyGroupDto
{
    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("type")]
    public string? Type { get; init; }

    [JsonPropertyName("now")]
    public string? Now { get; init; }

    [JsonPropertyName("all")]
    public string[]? All { get; init; }

    [JsonPropertyName("testUrl")]
    public string? TestUrl { get; init; }

    [JsonPropertyName("timeout")]
    public int? Timeout { get; init; }

    [JsonPropertyName("icon")]
    public string? Icon { get; init; }

    [JsonPropertyName("hidden")]
    public bool? Hidden { get; init; }

    [JsonPropertyName("fixed")]
    public string? Fixed { get; init; }

    [JsonPropertyName("history")]
    public JsonElement? History { get; init; }

    [JsonPropertyName("alive")]
    public bool? Alive { get; init; }

    public int? LatestDelay => DelayHistoryParser.LatestDelay(History);

    /// <summary>是否是功能性特殊代理（不可被选择切换）</summary>
    public bool IsSpecialProxy => Type is "Direct" or "Reject" or "RejectDrop" or "Compatible" or "Pass" or "dns";

    /// <summary>是否是代理组（可以有子成员）</summary>
    public bool IsGroup => All?.Length > 0;
}

// --- Providers (Proxy) ---

public sealed record ProviderSummary(
    [property: JsonPropertyName("providers")] Dictionary<string, ProviderDetailDto> Providers);

public sealed record ProviderDetailDto
{
    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("type")]
    public string? Type { get; init; }

    [JsonPropertyName("vehicleType")]
    public string? VehicleType { get; init; }

    [JsonPropertyName("behavior")]
    public string? Behavior { get; init; }

    [JsonPropertyName("format")]
    public string? Format { get; init; }

    [JsonPropertyName("updatedAt")]
    public string? UpdatedAt { get; init; }

    [JsonPropertyName("ruleCount")]
    public int? RuleCount { get; init; }

    [JsonPropertyName("subscriptionInfo")]
    public ProviderSubscriptionInfo? SubscriptionInfo { get; init; }

    [JsonPropertyName("proxies")]
    public ProviderProxyNode[]? Proxies { get; init; }
}

public sealed record ProviderSubscriptionInfo
{
    [JsonPropertyName("upload")]
    public long? Upload { get; init; }

    [JsonPropertyName("download")]
    public long? Download { get; init; }

    [JsonPropertyName("total")]
    public long? Total { get; init; }

    [JsonPropertyName("expire")]
    public long? Expire { get; init; }
}

public sealed record ProviderProxyNode
{
    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("type")]
    public string? Type { get; init; }

    [JsonPropertyName("alive")]
    public bool? Alive { get; init; }

    [JsonPropertyName("history")]
    public JsonElement? History { get; init; }

    public int? LatestDelay => DelayHistoryParser.LatestDelay(History);
}

// --- Connections ---

public sealed record ConnectionsSnapshot(
    [property: JsonPropertyName("connections")] ConnectionDto[]? Connections,
    [property: JsonPropertyName("uploadTotal")] long? UploadTotal,
    [property: JsonPropertyName("downloadTotal")] long? DownloadTotal);

public sealed record ConnectionDto
{
    [JsonPropertyName("id")]
    public string? Id { get; init; }

    [JsonPropertyName("metadata")]
    public ConnectionMetadata? Metadata { get; init; }

    [JsonPropertyName("chains")]
    public string[]? Chains { get; init; }

    [JsonPropertyName("rule")]
    public string? Rule { get; init; }

    [JsonPropertyName("rulePayload")]
    public string? RulePayload { get; init; }

    [JsonPropertyName("upload")]
    public long? Upload { get; init; }

    [JsonPropertyName("download")]
    public long? Download { get; init; }

    [JsonPropertyName("start")]
    public string? Start { get; init; }
}

public sealed record ConnectionMetadata
{
    [JsonPropertyName("host")]
    public string? Host { get; init; }

    [JsonPropertyName("destinationIP")]
    public string? DestinationIP { get; init; }

    [JsonPropertyName("destinationPort")]
    public string? DestinationPort { get; init; }

    [JsonPropertyName("sourceIP")]
    public string? SourceIP { get; init; }

    [JsonPropertyName("sourcePort")]
    public string? SourcePort { get; init; }

    [JsonPropertyName("network")]
    public string? Network { get; init; }

    [JsonPropertyName("type")]
    public string? Type { get; init; }

    [JsonPropertyName("process")]
    public string? Process { get; init; }

    [JsonPropertyName("processPath")]
    public string? ProcessPath { get; init; }
}

// --- Rules ---

public sealed record RulesResponse(
    [property: JsonPropertyName("rules")] RuleDto[]? Rules);

public sealed record RuleDto
{
    [JsonPropertyName("index")]
    public int? Index { get; init; }

    [JsonPropertyName("type")]
    public string? Type { get; init; }

    [JsonPropertyName("payload")]
    public string? Payload { get; init; }

    [JsonPropertyName("proxy")]
    public string? Proxy { get; init; }

    [JsonPropertyName("size")]
    public long? Size { get; init; }

    [JsonPropertyName("extra")]
    public RuleExtraDto? Extra { get; init; }
}

public sealed record RuleExtraDto
{
    [JsonPropertyName("disabled")]
    public bool Disabled { get; init; }

    [JsonPropertyName("hitCount")]
    public int HitCount { get; init; }

    [JsonPropertyName("hitAt")]
    public string? HitAt { get; init; }

    [JsonPropertyName("missCount")]
    public int MissCount { get; init; }

    [JsonPropertyName("missAt")]
    public string? MissAt { get; init; }
}

// Rule providers reuse ProviderSummary / ProviderDetailDto (fields: name, type, vehicleType, behavior, updatedAt, ruleCount)
public sealed record RuleProviderSummary(
    [property: JsonPropertyName("providers")] Dictionary<string, ProviderDetailDto>? Providers);

// --- Delay test result ---
// PUT /proxies/{name}/delay => {"delay": 123}  or errors
public sealed record DelayResult(
    [property: JsonPropertyName("delay")] int? Delay,
    [property: JsonPropertyName("meanDelay")] int? MeanDelay,
    [property: JsonPropertyName("message")] string? Message);

// --- Helpers ---

internal static class DelayHistoryParser
{
    public static int? LatestDelay(JsonElement? history)
    {
        if (history is not { ValueKind: JsonValueKind.Array } element)
        {
            return null;
        }

        foreach (var item in element.EnumerateArray().Reverse())
        {
            if (item.TryGetProperty("delay", out var delay))
            {
                if (delay.ValueKind == JsonValueKind.Number && delay.TryGetInt32(out var numeric))
                {
                    return numeric;
                }

                if (delay.ValueKind == JsonValueKind.String && int.TryParse(delay.GetString(), out var textDelay))
                {
                    return textDelay;
                }
            }

            if (item.TryGetProperty("meanDelay", out var mean))
            {
                if (mean.ValueKind == JsonValueKind.Number && mean.TryGetInt32(out var numeric))
                {
                    return numeric;
                }
            }
        }

        return null;
    }
}
