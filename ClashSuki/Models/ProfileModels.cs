using System.Text.Json.Serialization;

namespace ClashSuki.Models;

/// <summary>订阅配置文件（对应 Clash Verge 的 IProfileItem）</summary>
public sealed class ProfileItem
{
    [JsonPropertyName("uid")]
    public string Uid { get; set; } = Guid.NewGuid().ToString("N")[..8];

    /// <summary>remote = 远程订阅 URL；local = 本地文件</summary>
    [JsonPropertyName("type")]
    public string Type { get; set; } = "remote";

    [JsonPropertyName("name")]
    public string Name { get; set; } = "未命名";

    [JsonPropertyName("url")]
    public string? Url { get; set; }

    /// <summary>配置文件名（相对 profiles 目录）</summary>
    [JsonPropertyName("file")]
    public string? File { get; set; }

    /// <summary>备注</summary>
    [JsonPropertyName("desc")]
    public string? Desc { get; set; }

    /// <summary>最后更新时间（Unix 秒）</summary>
    [JsonPropertyName("updated")]
    public long? Updated { get; set; }

    /// <summary>流量信息（来自 subscription-userinfo 响应头）</summary>
    [JsonPropertyName("extra")]
    public ProfileExtra? Extra { get; set; }

    /// <summary>User-Agent 覆盖，null 则用默认值</summary>
    [JsonPropertyName("user_agent")]
    public string? UserAgent { get; set; }

    [JsonPropertyName("auth_token")]
    public string? AuthToken { get; set; }

    /// <summary>age 加密订阅的私钥。</summary>
    [JsonPropertyName("age_secret_key")]
    public string? AgeSecretKey { get; set; }

    /// <summary>自动更新间隔，单位分钟。</summary>
    [JsonPropertyName("interval")]
    public int? Interval { get; set; }

    [JsonPropertyName("auto_update")]
    public bool AutoUpdate { get; set; }

}

/// <summary>流量信息（来自 subscription-userinfo 响应头）</summary>
public sealed class ProfileExtra
{
    [JsonPropertyName("upload")]
    public long Upload { get; set; }

    [JsonPropertyName("download")]
    public long Download { get; set; }

    [JsonPropertyName("total")]
    public long Total { get; set; }

    /// <summary>过期时间（Unix 秒）</summary>
    [JsonPropertyName("expire")]
    public long? Expire { get; set; }

    public long Used => Upload + Download;
    public long Remaining => Math.Max(0, Total - Used);
    public double UsedRatio => Total > 0 ? Math.Clamp((double)Used / Total, 0.0, 1.0) : 0;
}

/// <summary>profiles.json 根结构</summary>
public sealed class ProfilesConfig
{
    /// <summary>当前激活的 uid</summary>
    [JsonPropertyName("current")]
    public string? Current { get; set; }

    [JsonPropertyName("items")]
    public List<ProfileItem> Items { get; set; } = [];
}
