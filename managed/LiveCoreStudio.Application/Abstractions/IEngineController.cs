using LiveCoreStudio.Core.Engine;

namespace LiveCoreStudio.Application.Abstractions;

/// <summary>Starts, stops and supervises the native media engine.</summary>
public interface IEngineController : IAsyncDisposable
{
    EngineHealthSnapshot Health { get; }

    /// <summary>Raised on every state change and heartbeat, on a background thread.</summary>
    event Action<EngineHealthSnapshot>? HealthChanged;

    /// <summary>Launches the engine and completes once the handshake succeeded. Throws if it could not start.</summary>
    Task StartAsync(CancellationToken cancellationToken = default);

    /// <summary>Requests a graceful shutdown; kills the process if it does not exit in time.</summary>
    Task StopAsync(CancellationToken cancellationToken = default);

    Task RestartAsync(CancellationToken cancellationToken = default);
}
