namespace LiveCoreStudio.Core.Engine;

/// <summary>Immutable point-in-time view of engine health, published on every heartbeat and state change.</summary>
public sealed record EngineHealthSnapshot
{
    public required EngineLifecycleState State { get; init; }
    public int? ProcessId { get; init; }
    public string? EngineVersion { get; init; }
    public string? Platform { get; init; }
    public int? ProtocolVersion { get; init; }
    public long? EngineUptimeMs { get; init; }
    public long? ClockTicks { get; init; }
    public int? ClockRateHz { get; init; }
    public double? LastRoundTripMs { get; init; }
    public int MissedHeartbeats { get; init; }
    public int RestartCount { get; init; }
    public string? LastError { get; init; }
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.Now;

    public static EngineHealthSnapshot Initial { get; } = new() { State = EngineLifecycleState.Stopped };
}
