using LiveCoreStudio.Core.Production;

namespace LiveCoreStudio.Application.Abstractions;

/// <summary>
/// Video production control: sources, Preview/Program buses and transitions.
/// The service remembers the operator's setup and restores it automatically if the engine restarts.
/// </summary>
public interface IProductionService
{
    /// <summary>Sources the operator has added, in order (the app's view, valid even while the engine restarts).</summary>
    IReadOnlyList<SourceSpec> Sources { get; }

    Task<IReadOnlyList<VideoDeviceInfo>> ListVideoDevicesAsync(CancellationToken ct = default);
    Task<SourceSpec> AddTestPatternAsync(string name, string pattern, string color, CancellationToken ct = default);
    Task<SourceSpec> AddDeviceSourceAsync(string name, string deviceId, CancellationToken ct = default);
    Task RemoveSourceAsync(uint id, CancellationToken ct = default);
    Task SetPreviewAsync(uint id, CancellationToken ct = default);
    /// <summary>Hot cut: put a source on Program directly.</summary>
    Task SetProgramAsync(uint id, CancellationToken ct = default);
    /// <summary>Swap Preview and Program instantly.</summary>
    Task CutAsync(CancellationToken ct = default);
    /// <summary>Mix from Program to Preview over the given duration.</summary>
    Task AutoTransitionAsync(int durationMs, CancellationToken ct = default);
    Task<SwitcherState> GetStateAsync(CancellationToken ct = default);
}

/// <summary>A production command was issued while the engine is not running.</summary>
public sealed class EngineNotRunningException() : InvalidOperationException("The media engine is not running.");
