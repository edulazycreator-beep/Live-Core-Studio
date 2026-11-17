using LiveCoreStudio.Application.Abstractions;
using LiveCoreStudio.Core.Diagnostics;
using LiveCoreStudio.Core.Production;
using LiveCoreStudio.EngineClient.Protocol;

namespace LiveCoreStudio.EngineClient;

/// <summary>
/// Production control over the engine connection. Keeps the operator's intended setup
/// (sources, Program, Preview) and replays it after every engine restart, so a crash
/// during a show comes back with the same sources on the same buses.
/// </summary>
public sealed class EngineProductionService : IProductionService
{
    private const string Source = "production";
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    private readonly EngineSupervisor _supervisor;
    private readonly IAppLog _log;
    private readonly Lock _gate = new();
    private readonly List<SourceSpec> _sources = [];
    private uint _program;
    private uint _preview;
    private uint _nextId = 1;

    public EngineProductionService(EngineSupervisor supervisor, IAppLog log)
    {
        _supervisor = supervisor;
        _log = log;
        supervisor.Connected += connection => LastRestore = RestoreAsync(connection);
    }

    /// <summary>The most recent restore after a (re)connection; exposed for tests and diagnostics.</summary>
    public Task LastRestore { get; private set; } = Task.CompletedTask;

    public IReadOnlyList<SourceSpec> Sources
    {
        get { lock (_gate) return [.. _sources]; }
    }

    private EngineConnection Connection => _supervisor.Connection ?? throw new EngineNotRunningException();

    public Task<IReadOnlyList<VideoDeviceInfo>> ListVideoDevicesAsync(CancellationToken ct = default) =>
        Connection.ListVideoDevicesAsync(Timeout, ct);

    public Task<SourceSpec> AddTestPatternAsync(string name, string pattern, string color, CancellationToken ct = default) =>
        AddAsync(id => SourceSpec.TestPattern(id, name, pattern, color), ct);

    public Task<SourceSpec> AddDeviceSourceAsync(string name, string deviceId, CancellationToken ct = default) =>
        AddAsync(id => SourceSpec.Device(id, name, deviceId), ct);

    private async Task<SourceSpec> AddAsync(Func<uint, SourceSpec> create, CancellationToken ct)
    {
        uint id;
        lock (_gate) id = _nextId++;
        var spec = create(id);
        await Connection.AddSourceAsync(spec, Timeout, ct).ConfigureAwait(false);
        lock (_gate) _sources.Add(spec);
        _log.Info(Source, $"Added source {spec.Id} '{spec.Name}' ({spec.Kind}).");
        return spec;
    }

    public async Task RemoveSourceAsync(uint id, CancellationToken ct = default)
    {
        await Connection.RemoveSourceAsync(id, Timeout, ct).ConfigureAwait(false);
        lock (_gate)
        {
            _sources.RemoveAll(s => s.Id == id);
            if (_program == id) _program = 0;
            if (_preview == id) _preview = 0;
        }
    }

    public async Task SetPreviewAsync(uint id, CancellationToken ct = default)
    {
        await Connection.SetPreviewAsync(id, Timeout, ct).ConfigureAwait(false);
        lock (_gate) _preview = id;
    }

    public async Task SetProgramAsync(uint id, CancellationToken ct = default)
    {
        await Connection.SetProgramAsync(id, Timeout, ct).ConfigureAwait(false);
        lock (_gate) _program = id;
    }

    public async Task CutAsync(CancellationToken ct = default)
    {
        await Connection.CutAsync(Timeout, ct).ConfigureAwait(false);
        lock (_gate) (_program, _preview) = (_preview, _program);
    }

    public async Task AutoTransitionAsync(int durationMs, CancellationToken ct = default)
    {
        await Connection.AutoTransitionAsync(durationMs, Timeout, ct).ConfigureAwait(false);
        lock (_gate) (_program, _preview) = (_preview, _program);  // the end state of the mix
    }

    public async Task<SwitcherState> GetStateAsync(CancellationToken ct = default)
    {
        var state = await Connection.GetSwitcherStateAsync(Timeout, ct).ConfigureAwait(false);
        if (!state.Transitioning)
        {
            lock (_gate)
            {
                _program = state.Program;
                _preview = state.Preview;
            }
        }
        return state;
    }

    private async Task RestoreAsync(EngineConnection connection)
    {
        SourceSpec[] sources;
        uint program, preview;
        lock (_gate)
        {
            sources = [.. _sources];
            program = _program;
            preview = _preview;
        }
        if (sources.Length == 0) return;

        var restored = 0;
        foreach (var spec in sources)
        {
            try
            {
                await connection.AddSourceAsync(spec, Timeout, CancellationToken.None).ConfigureAwait(false);
                restored++;
            }
            catch (Exception ex) when (ex is EngineProtocolException or TimeoutException or EngineDisconnectedException)
            {
                _log.Error(Source, $"Could not restore source {spec.Id} '{spec.Name}': {ex.Message}");
            }
        }
        try
        {
            if (program != 0) await connection.SetProgramAsync(program, Timeout, CancellationToken.None).ConfigureAwait(false);
            if (preview != 0) await connection.SetPreviewAsync(preview, Timeout, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is EngineProtocolException or TimeoutException or EngineDisconnectedException)
        {
            _log.Error(Source, $"Could not restore bus selection: {ex.Message}");
        }
        _log.Info(Source, $"Restored {restored}/{sources.Length} sources after engine start (program {program}, preview {preview}).");
    }
}
