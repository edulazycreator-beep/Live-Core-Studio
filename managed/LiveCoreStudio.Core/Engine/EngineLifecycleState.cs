namespace LiveCoreStudio.Core.Engine;

/// <summary>Lifecycle of the native media engine as seen by the application.</summary>
public enum EngineLifecycleState
{
    /// <summary>No engine process.</summary>
    Stopped,
    /// <summary>Launching the process and performing the handshake.</summary>
    Starting,
    /// <summary>Handshake done and heartbeats are healthy.</summary>
    Running,
    /// <summary>Crash, broken pipe or missed heartbeats detected.</summary>
    Faulted,
    /// <summary>Waiting out the backoff before a supervised restart.</summary>
    Restarting,
    /// <summary>Graceful shutdown in progress.</summary>
    Stopping,
    /// <summary>Supervisor gave up (restart budget exhausted or engine could not start). Operator action needed.</summary>
    Failed,
}
