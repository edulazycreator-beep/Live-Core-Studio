namespace LiveCoreStudio.Core.Configuration;

/// <summary>Root of the persisted application settings (settings.json).</summary>
public sealed record AppSettings
{
    /// <summary>Bumped when the file layout changes incompatibly.</summary>
    public int SchemaVersion { get; init; } = 1;

    public EngineSettings Engine { get; init; } = new();

    public InterfaceSettings Interface { get; init; } = new();

    public IReadOnlyList<string> Validate() => Engine.Validate();
}

/// <summary>How the application launches and supervises the native engine.</summary>
public sealed record EngineSettings
{
    /// <summary>Full path to LiveCore.MediaEngine. Null means "next to the application".</summary>
    public string? ExecutablePath { get; init; }

    public bool StartWithApplication { get; init; } = true;
    public int HeartbeatIntervalMs { get; init; } = 1000;
    public int HeartbeatTimeoutMs { get; init; } = 3000;
    public int MaxMissedHeartbeats { get; init; } = 3;
    public bool AutoRestart { get; init; } = true;
    public int MaxRestartsPerWindow { get; init; } = 5;
    public int RestartWindowSeconds { get; init; } = 60;
    public int ConnectTimeoutMs { get; init; } = 5000;
    public int ClockRateHz { get; init; } = 60;

    /// <summary>trace | debug | info | warn | error | off</summary>
    public string LogLevel { get; init; } = "info";

    private static readonly string[] LogLevels = ["trace", "debug", "info", "warn", "error", "off"];

    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (HeartbeatIntervalMs is < 100 or > 60_000)
            errors.Add("Heartbeat interval must be between 100 and 60000 ms.");
        if (HeartbeatTimeoutMs < HeartbeatIntervalMs)
            errors.Add("Heartbeat timeout must be at least the heartbeat interval.");
        if (MaxMissedHeartbeats is < 1 or > 100)
            errors.Add("Max missed heartbeats must be between 1 and 100.");
        if (MaxRestartsPerWindow is < 1 or > 100)
            errors.Add("Max restarts per window must be between 1 and 100.");
        if (RestartWindowSeconds is < 1 or > 3600)
            errors.Add("Restart window must be between 1 and 3600 seconds.");
        if (ConnectTimeoutMs is < 500 or > 60_000)
            errors.Add("Connect timeout must be between 500 and 60000 ms.");
        if (ClockRateHz is < 1 or > 1000)
            errors.Add("Clock rate must be between 1 and 1000 Hz.");
        if (!LogLevels.Contains(LogLevel))
            errors.Add($"Log level must be one of: {string.Join(", ", LogLevels)}.");
        return errors;
    }
}

/// <summary>Operator interface preferences.</summary>
public sealed record InterfaceSettings
{
    public string LastPage { get; init; } = "dashboard";
    public bool ConfirmOnExit { get; init; } = true;
}
