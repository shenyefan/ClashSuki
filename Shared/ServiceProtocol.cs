using System.Text.Json;
using System.Text.Json.Serialization;

namespace ClashSuki.ServiceContract;

public static class ServiceProtocol
{
    public const string ServiceName = "ClashSukiService";
    public const string PipeName = "ClashSukiService";
    public const string CoreControlPipePath = @"\\.\pipe\clashsuki-mihomo";
    public const int Version = 5;
    public const int MaxRequestCharacters = 32 * 1024;

    public static JsonSerializerOptions CreateJsonOptions()
    {
        return new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };
    }
}

public static class ServiceCommands
{
    public const string Ping = "ping";
    public const string GetStatus = "get_status";
    public const string StartCore = "start_core";
    public const string SetCorePriority = "set_core_priority";
    public const string StopCore = "stop_core";
    public const string StopService = "stop_service";
}

public sealed class ServiceRequest
{
    public string? Command { get; init; }
    public string? CorePath { get; init; }
    public string? ConfigPath { get; init; }
    public string? ConfigDir { get; init; }
    public string? CoreIpcPath { get; init; }
    public string? CorePriority { get; init; }
}

public sealed class ServiceResponse
{
    public bool Ok { get; init; }
    public string? Error { get; init; }
    public int? ProtocolVersion { get; init; }
    public bool? CoreRunning { get; init; }
    public int? CorePid { get; init; }

    public static ServiceResponse Success() => new() { Ok = true };

    public static ServiceResponse Failure(string error) => new()
    {
        Ok = false,
        Error = error
    };
}
