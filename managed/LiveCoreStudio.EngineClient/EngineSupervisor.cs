using System.Diagnostics;
using LiveCoreStudio.Application.Abstractions;
using LiveCoreStudio.Core.Configuration;
using LiveCoreStudio.Core.Diagnostics;
using LiveCoreStudio.Core.Engine;
using LiveCoreStudio.EngineClient.Protocol;

namespace LiveCoreStudio.EngineClient;

/// <summary>
/// Owns the engine's whole lifecycle in one supervision loop: launch → handshake →
/// heartbeat monitoring → (on fault) backoff and restart, within a restart budget.
/// All state transitions happen on that loop, so there are no competing writers.
/// </summary>
public sealed class EngineSupervisor : IEngineController
{
    private const string Source = "supervisor";
    private static readonly TimeSpan StableRunResetsBackoff = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(10);

    private readonly Func<EngineSettings> _settings;
    private readonly IAppLog _log;
    private readonly SemaphoreSlim _lifecycleLock = new(1, 1);
    private readonly Queue<DateTimeOffset> _restartTimes = new();

    private CancellationTokenSource? _loopCts;
    private Task? _loop;
    private EngineHealthSnapshot _health = EngineHealthSnapshot.Initial;
    private int _restartCount;

    public EngineSupervisor(Func<EngineSettings> settings, IAppLog log)
    {
        _settings = settings;
        _log = log;
    }

    public EngineHealthSnapshot Health => Volatile.Read(ref _health);

    public event Action<EngineHealthSnapshot>? HealthChanged;

    /// <summary>Process id of the current engine, for tests and diagnostics.</summary>
    public int? CurrentProcessId => Health.ProcessId;

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycleLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_loop is { IsCompleted: false }) return;

            _restartTimes.Clear();
            _restartCount = 0;
            _loopCts?.Dispose();
            _loopCts = new CancellationTokenSource();
            var firstStart = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _loop = Task.Run(() => SuperviseAsync(firstStart, _loopCts.Token), CancellationToken.None);
            await firstStart.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycleLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_loop is null) return;
            await _loopCts!.CancelAsync().ConfigureAwait(false);
            await _loop.ConfigureAwait(false);  // loop performs the graceful shutdown in its finally path
            _loopCts.Dispose();
            _loopCts = null;
            _loop = null;
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    public async Task RestartAsync(CancellationToken cancellationToken = default)
    {
        _log.Info(Source, "Manual engine restart requested.");
        await StopAsync(cancellationToken).ConfigureAwait(false);
        await StartAsync(cancellationToken).ConfigureAwait(false);
    }

    private sealed record Session(EngineProcess Process, EngineConnection Connection, HelloAck Hello) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            await Connection.DisposeAsync().ConfigureAwait(false);
            await Process.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async Task SuperviseAsync(TaskCompletionSource firstStart, CancellationToken ct)
    {
        var consecutiveFailures = 0;
        var failed = false;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var settings = _settings();
                Publish(Health with { State = EngineLifecycleState.Starting, MissedHeartbeats = 0 });

                Session? session = null;
                string? fault;
                try
                {
                    session = await LaunchAsync(settings, ct).ConfigureAwait(false);
                    firstStart.TrySetResult();
                    var runStarted = Stopwatch.StartNew();
                    fault = await MonitorAsync(session, settings, ct).ConfigureAwait(false);
                    if (fault is null)
                    {
                        await ShutdownGracefullyAsync(session).ConfigureAwait(false);
                        return;
                    }
                    if (runStarted.Elapsed >= StableRunResetsBackoff) consecutiveFailures = 0;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    if (session is not null) await ShutdownGracefullyAsync(session).ConfigureAwait(false);
                    firstStart.TrySetCanceled(ct);
                    return;
                }
                catch (Exception ex)
                {
                    fault = $"Engine failed to start: {ex.Message}";
                    if (session is not null) await session.DisposeAsync().ConfigureAwait(false);
                    session = null;
                }

                if (session is not null)
                {
                    await session.DisposeAsync().ConfigureAwait(false);  // kills a hung engine
                }

                _log.Error(Source, fault);
                Publish(Health with { State = EngineLifecycleState.Faulted, LastError = fault });

                if (!firstStart.Task.IsCompleted)
                {
                    // Never came up at all: report to the caller instead of looping on a broken install.
                    failed = true;
                    Publish(Health with { State = EngineLifecycleState.Failed, ProcessId = null });
                    firstStart.TrySetException(new InvalidOperationException(fault));
                    return;
                }

                if (!settings.AutoRestart)
                {
                    failed = true;
                    Publish(Health with { State = EngineLifecycleState.Failed, ProcessId = null });
                    return;
                }

                var now = DateTimeOffset.Now;
                while (_restartTimes.Count > 0 && now - _restartTimes.Peek() > TimeSpan.FromSeconds(settings.RestartWindowSeconds))
                    _restartTimes.Dequeue();
                if (_restartTimes.Count >= settings.MaxRestartsPerWindow)
                {
                    failed = true;
                    var msg = $"Engine restarted {_restartTimes.Count} times within {settings.RestartWindowSeconds} s; automatic restart stopped. Last fault: {fault}";
                    _log.Error(Source, msg);
                    Publish(Health with { State = EngineLifecycleState.Failed, ProcessId = null, LastError = msg });
                    return;
                }

                _restartTimes.Enqueue(now);
                _restartCount++;
                var delay = TimeSpan.FromMilliseconds(Math.Min(MaxBackoff.TotalMilliseconds, 500 * Math.Pow(2, consecutiveFailures)));
                consecutiveFailures++;
                _log.Warn(Source, $"Restarting engine in {delay.TotalMilliseconds:0} ms (restart #{_restartCount}).");
                Publish(Health with { State = EngineLifecycleState.Restarting, ProcessId = null, RestartCount = _restartCount });
                await Task.Delay(delay, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            firstStart.TrySetCanceled(ct);
        }
        finally
        {
            if (!failed) Publish(Health with { State = EngineLifecycleState.Stopped, ProcessId = null, LastRoundTripMs = null });
        }
    }

    private async Task<Session> LaunchAsync(EngineSettings settings, CancellationToken ct)
    {
        var exe = EngineProcess.ResolveExecutable(settings);
        var process = EngineProcess.Start(exe, settings, EngineProcess.NewPipeName(), _log);
        try
        {
            var (connection, hello) = await EngineConnection.ConnectAsync(
                process.PipeName, TimeSpan.FromMilliseconds(settings.ConnectTimeoutMs), ct).ConfigureAwait(false);
            _log.Info(Source, $"Connected to {hello.Engine} {hello.EngineVersion} (pid {hello.Pid}, {hello.Platform}, protocol v{hello.ProtocolVersion}).");
            Publish(Health with
            {
                State = EngineLifecycleState.Running,
                ProcessId = hello.Pid,
                EngineVersion = hello.EngineVersion,
                Platform = hello.Platform,
                ProtocolVersion = hello.ProtocolVersion,
                MissedHeartbeats = 0,
                RestartCount = _restartCount,
            });
            return new Session(process, connection, hello);
        }
        catch
        {
            await process.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>Returns a fault description, or null when cancelled (normal stop).</summary>
    private async Task<string?> MonitorAsync(Session session, EngineSettings settings, CancellationToken ct)
    {
        var timeout = TimeSpan.FromMilliseconds(settings.HeartbeatTimeoutMs);
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(settings.HeartbeatIntervalMs));
        var missed = 0;
        long seq = 0;

        while (true)
        {
            try
            {
                if (!await timer.WaitForNextTickAsync(ct).ConfigureAwait(false)) return null;
            }
            catch (OperationCanceledException)
            {
                return null;
            }

            if (session.Process.HasExited)
                return $"Engine process exited unexpectedly (exit code {session.Process.ExitCode}).";
            if (!session.Connection.IsConnected)
                return "Engine control connection was lost.";

            try
            {
                var sw = Stopwatch.StartNew();
                await session.Connection.PingAsync(++seq, timeout, ct).ConfigureAwait(false);
                var rtt = sw.Elapsed.TotalMilliseconds;
                var status = await session.Connection.GetStatusAsync(timeout, ct).ConfigureAwait(false);
                missed = 0;
                Publish(Health with
                {
                    State = EngineLifecycleState.Running,
                    LastRoundTripMs = rtt,
                    EngineUptimeMs = status.UptimeMs,
                    ClockTicks = status.ClockTicks,
                    ClockRateHz = status.ClockRateHz,
                    MissedHeartbeats = 0,
                });
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return null;
            }
            catch (TimeoutException)
            {
                missed++;
                _log.Warn(Source, $"Heartbeat timed out ({missed}/{settings.MaxMissedHeartbeats}).");
                Publish(Health with { MissedHeartbeats = missed });
                if (missed >= settings.MaxMissedHeartbeats)
                    return $"Engine stopped responding ({missed} missed heartbeats).";
            }
            catch (EngineDisconnectedException)
            {
                await session.Process.WaitForExitAsync(TimeSpan.FromMilliseconds(500)).ConfigureAwait(false);
                return session.Process.HasExited
                    ? $"Engine process exited unexpectedly (exit code {session.Process.ExitCode})."
                    : "Engine control connection was lost.";
            }
            catch (EngineProtocolException ex)
            {
                return $"Engine protocol error: {ex.Code} — {ex.Message}";
            }
        }
    }

    private async Task ShutdownGracefullyAsync(Session session)
    {
        Publish(Health with { State = EngineLifecycleState.Stopping });
        try
        {
            if (session.Connection.IsConnected)
                await session.Connection.ShutdownAsync("application request", TimeSpan.FromSeconds(2), CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is TimeoutException or EngineDisconnectedException or EngineProtocolException or IOException)
        {
            _log.Warn(Source, $"Graceful shutdown request failed: {ex.Message}");
        }

        if (await session.Process.WaitForExitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false))
            _log.Info(Source, $"Engine exited (code {session.Process.ExitCode}).");
        else
            _log.Warn(Source, "Engine did not exit in time; terminating it.");
        await session.DisposeAsync().ConfigureAwait(false);
    }

    private void Publish(EngineHealthSnapshot snapshot)
    {
        snapshot = snapshot with { Timestamp = DateTimeOffset.Now };
        Volatile.Write(ref _health, snapshot);
        try
        {
            HealthChanged?.Invoke(snapshot);
        }
        catch (Exception ex)
        {
            _log.Error(Source, $"HealthChanged handler threw: {ex.Message}");
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _lifecycleLock.Dispose();
    }
}
