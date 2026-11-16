using System.Text.Json;

namespace LiveCoreStudio.EngineClient.Protocol;

public sealed record HelloRequest(string Client, int ProtocolVersion);
public sealed record HelloAck(string Engine, string EngineVersion, int ProtocolVersion, int Pid, string Platform);
public sealed record PingRequest(long Seq, long ClientTimeMs);
public sealed record PongResponse(long Seq, long ClientTimeMs, long EngineUptimeMs);
public sealed record EngineStatus(string State, long UptimeMs, int Pid, long ClockTicks, int ClockRateHz, int Connections);
public sealed record ShutdownRequest(string Reason);
public sealed record ErrorResponse(string Code, string Message);

internal static class ProtocolJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
    public static readonly byte[] EmptyObject = "{}"u8.ToArray();
}

/// <summary>The engine replied with an Error message or sent something that violates the protocol.</summary>
public sealed class EngineProtocolException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

/// <summary>The control connection closed while a request was outstanding.</summary>
public sealed class EngineDisconnectedException(string message, Exception? inner = null) : Exception(message, inner);
