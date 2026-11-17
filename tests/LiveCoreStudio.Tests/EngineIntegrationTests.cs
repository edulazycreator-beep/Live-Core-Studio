using System.Diagnostics;
using System.IO.Pipes;
using System.Text.Json;
using LiveCoreStudio.Core.Configuration;
using LiveCoreStudio.Core.Diagnostics;
using LiveCoreStudio.Core.Engine;
using LiveCoreStudio.EngineClient;
using LiveCoreStudio.EngineClient.Protocol;

namespace LiveCoreStudio.Tests;

/// <summary>Real C# ↔ C++ tests: these launch the native engine binary and talk to it over the pipe.</summary>
public static class EngineIntegrationTests
{
    private static readonly TimeSpan Timeout5 = TimeSpan.FromSeconds(5);

    internal static string EnginePathForTests() => EnginePath();

    private static string EnginePath()
    {
        var env = Environment.GetEnvironmentVariable("LIVECORE_ENGINE_PATH");
        if (!string.IsNullOrEmpty(env)) return File.Exists(env) ? env : throw new SkipException($"LIVECORE_ENGINE_PATH '{env}' does not exist");

        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "CMakePresets.json"))) dir = dir.Parent;
        var buildRoot = dir is null ? null : Path.Combine(dir.FullName, "out", "build");
        if (buildRoot is null || !Directory.Exists(buildRoot)) throw new SkipException("native engine not built (no out/build)");

        var name = OperatingSystem.IsWindows() ? "LiveCore.MediaEngine.exe" : "LiveCore.MediaEngine";
        var candidates = Directory.GetFiles(buildRoot, name, SearchOption.AllDirectories)
            .Where(p => OperatingSystem.IsWindows() || !p.Contains("mingw", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .ToList();
        return candidates.FirstOrDefault() ?? throw new SkipException($"no {name} under {buildRoot}");
    }

    private static Process StartEngineRaw(string pipeName, params string[] extra)
    {
        var psi = new ProcessStartInfo(EnginePath()) { UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true };
        psi.ArgumentList.Add("--pipe");
        psi.ArgumentList.Add(pipeName);
        foreach (var a in extra) psi.ArgumentList.Add(a);
        var p = Process.Start(psi)!;
        p.ErrorDataReceived += (_, _) => { };
        p.BeginErrorReadLine();
        p.BeginOutputReadLine();
        return p;
    }

    private static string UniquePipe() => $"LiveCore.Test.{Guid.NewGuid().ToString("N")[..10]}";

    private sealed class ListLog : IAppLog
    {
        public List<AppLogEntry> Entries { get; } = [];
        public event Action<AppLogEntry>? EntryWritten;
        public void Write(AppLogLevel level, string source, string message)
        {
            var e = new AppLogEntry(DateTimeOffset.Now, level, source, message);
            lock (Entries) Entries.Add(e);
            EntryWritten?.Invoke(e);
        }
    }

    private static EngineSettings FastSettings(int maxRestarts = 5) => new()
    {
        ExecutablePath = EnginePath(),
        HeartbeatIntervalMs = 200,
        HeartbeatTimeoutMs = 600,
        MaxMissedHeartbeats = 3,
        MaxRestartsPerWindow = maxRestarts,
        RestartWindowSeconds = 60,
        LogLevel = "warn",
    };

    private static void KillPid(int pid)
    {
        using var p = Process.GetProcessById(pid);
        p.Kill();
        p.WaitForExit(3000);
    }

    [Test("integration")]
    public static async Task Handshake_ping_status_and_shutdown()
    {
        var pipe = UniquePipe();
        using var engine = StartEngineRaw(pipe, "--clock-rate", "100");
        try
        {
            var (conn, hello) = await EngineConnection.ConnectAsync(pipe, Timeout5, CancellationToken.None);
            await using (conn)
            {
                Assert.Equal("LiveCore.MediaEngine", hello.Engine);
                Assert.Equal(1, hello.ProtocolVersion);
                Assert.Equal(engine.Id, hello.Pid, "pid in HelloAck");

                var pong = await conn.PingAsync(7, Timeout5, CancellationToken.None);
                Assert.Equal(7L, pong.Seq);

                var s1 = await conn.GetStatusAsync(Timeout5, CancellationToken.None);
                Assert.Equal("running", s1.State);
                Assert.Equal(100, s1.ClockRateHz);
                await Task.Delay(300);
                var s2 = await conn.GetStatusAsync(Timeout5, CancellationToken.None);
                Assert.True(s2.ClockTicks - s1.ClockTicks >= 15, $"master clock advances (Δ={s2.ClockTicks - s1.ClockTicks} ticks in 300 ms at 100 Hz)");

                // Several concurrent requests must be correlated correctly.
                var pings = await Task.WhenAll(Enumerable.Range(100, 20).Select(i => conn.PingAsync(i, Timeout5, CancellationToken.None)));
                Assert.True(pings.Select(p => p.Seq).SequenceEqual(Enumerable.Range(100, 20).Select(i => (long)i)), "concurrent replies matched to requests");

                await conn.ShutdownAsync("test", Timeout5, CancellationToken.None);
            }
            Assert.True(engine.WaitForExit(3000), "engine exits after Shutdown");
            Assert.Equal(0, engine.ExitCode, "clean exit code");
        }
        finally { if (!engine.HasExited) engine.Kill(); }
    }

    [Test("integration")]
    public static async Task Engine_requires_handshake_and_rejects_wrong_protocol()
    {
        var pipe = UniquePipe();
        using var engine = StartEngineRaw(pipe);
        try
        {
            var raw = new NamedPipeClientStream(".", pipe, PipeDirection.InOut, PipeOptions.Asynchronous);
            await raw.ConnectAsync(Timeout5, CancellationToken.None);
            await using (var conn = EngineConnection.FromStream(raw))
            {
                var ex = await Assert.ThrowsAsync<EngineProtocolException>(() =>
                    conn.SendRawAsync(MessageType.Ping, "{}"u8.ToArray(), MessageType.Pong, Timeout5, CancellationToken.None));
                Assert.Equal("handshake_required", ex.Code);

                var bad = JsonSerializer.SerializeToUtf8Bytes(new { client = "test", protocolVersion = 99 });
                ex = await Assert.ThrowsAsync<EngineProtocolException>(() =>
                    conn.SendRawAsync(MessageType.Hello, bad, MessageType.HelloAck, Timeout5, CancellationToken.None));
                Assert.Equal("protocol_mismatch", ex.Code);
            }

            // The engine survives a bad client and accepts a new, correct one.
            var (good, _) = await EngineConnection.ConnectAsync(pipe, Timeout5, CancellationToken.None);
            await using (good)
            {
                var status = await good.GetStatusAsync(Timeout5, CancellationToken.None);
                Assert.Equal(2, status.Connections, "connections served");
                await good.ShutdownAsync("test", Timeout5, CancellationToken.None);
            }
            Assert.True(engine.WaitForExit(3000), "engine exits");
        }
        finally { if (!engine.HasExited) engine.Kill(); }
    }

    [Test("integration")]
    public static async Task Second_engine_cannot_take_an_owned_pipe()
    {
        var pipe = UniquePipe();
        using var first = StartEngineRaw(pipe);
        try
        {
            var (conn, _) = await EngineConnection.ConnectAsync(pipe, Timeout5, CancellationToken.None);
            await using var _c = conn;
            using var second = StartEngineRaw(pipe);
            Assert.True(second.WaitForExit(3000), "second engine exits");
            Assert.Equal(2, second.ExitCode, "exit code for endpoint in use");
            Assert.True(!first.HasExited, "first engine unaffected");
        }
        finally { if (!first.HasExited) first.Kill(); }
    }

    [Test("integration")]
    public static async Task Engine_exits_when_parent_dies()
    {
        var parentPsi = OperatingSystem.IsWindows()
            ? new ProcessStartInfo("cmd.exe", "/c ping -n 30 127.0.0.1 >nul") { UseShellExecute = false, CreateNoWindow = true }
            : new ProcessStartInfo("sleep", "30") { UseShellExecute = false };
        using var parent = Process.Start(parentPsi)!;
        using var engine = StartEngineRaw(UniquePipe(), "--parent-pid", parent.Id.ToString(System.Globalization.CultureInfo.InvariantCulture));
        try
        {
            await Task.Delay(500);
            Assert.True(!engine.HasExited, "engine runs while parent alive");
            parent.Kill(entireProcessTree: true);
            parent.WaitForExit();
            Assert.True(engine.WaitForExit(3000), "engine exits within 3 s of parent death");
            Assert.Equal(0, engine.ExitCode, "clean shutdown");
        }
        finally
        {
            if (!engine.HasExited) engine.Kill();
            if (!parent.HasExited) parent.Kill();
        }
    }

    [Test("integration")]
    public static async Task Supervisor_starts_monitors_and_stops()
    {
        var log = new ListLog();
        await using var sup = new EngineSupervisor(() => FastSettings(), log);
        await sup.StartAsync();
        Assert.Equal(EngineLifecycleState.Running, sup.Health.State);
        var pid = sup.Health.ProcessId!.Value;

        await Assert.EventuallyAsync(() => sup.Health.LastRoundTripMs is not null && sup.Health.ClockTicks > 0, Timeout5, "heartbeat telemetry");
        Assert.True(sup.Health.LastRoundTripMs < 100, $"round trip is local-fast ({sup.Health.LastRoundTripMs:0.00} ms)");

        await sup.StopAsync();
        Assert.Equal(EngineLifecycleState.Stopped, sup.Health.State);
        Assert.True(!Process.GetProcesses().Any(p => p.Id == pid && !p.HasExited), "engine process gone");
        Assert.True(log.Entries.Any(e => e.Message.Contains("Engine exited (code 0)", StringComparison.Ordinal)), "graceful exit logged");
    }

    [Test("integration")]
    public static async Task Supervisor_restarts_a_crashed_engine()
    {
        var log = new ListLog();
        await using var sup = new EngineSupervisor(() => FastSettings(), log);
        await sup.StartAsync();
        var firstPid = sup.Health.ProcessId!.Value;

        var states = new List<EngineLifecycleState>();
        sup.HealthChanged += h => { lock (states) if (states.Count == 0 || states[^1] != h.State) states.Add(h.State); };

        KillPid(firstPid);  // simulate a crash

        await Assert.EventuallyAsync(() => sup.Health is { State: EngineLifecycleState.Running, ProcessId: { } p } && p != firstPid,
            TimeSpan.FromSeconds(10), "engine relaunched with a new pid");
        Assert.Equal(1, sup.Health.RestartCount, "restart count");
        lock (states)
        {
            Assert.True(states.Contains(EngineLifecycleState.Faulted) && states.Contains(EngineLifecycleState.Restarting),
                "went through Faulted and Restarting: " + string.Join(" → ", states));
        }
        await sup.StopAsync();
    }

    [Test("integration")]
    public static async Task Supervisor_gives_up_after_restart_budget()
    {
        var log = new ListLog();
        await using var sup = new EngineSupervisor(() => FastSettings(maxRestarts: 1), log);
        await sup.StartAsync();

        KillPid(sup.Health.ProcessId!.Value);
        await Assert.EventuallyAsync(() => sup.Health is { State: EngineLifecycleState.Running, RestartCount: 1 }, TimeSpan.FromSeconds(10), "first restart");
        KillPid(sup.Health.ProcessId!.Value);
        await Assert.EventuallyAsync(() => sup.Health.State == EngineLifecycleState.Failed, TimeSpan.FromSeconds(10), "supervisor reports Failed");
        Assert.True(sup.Health.LastError?.Contains("automatic restart stopped", StringComparison.Ordinal) == true, "explains why");

        // Operator can still start it again manually.
        await sup.StartAsync();
        Assert.Equal(EngineLifecycleState.Running, sup.Health.State);
        await sup.StopAsync();
    }

    [Test("integration")]
    public static async Task Supervisor_reports_missing_engine_clearly()
    {
        var log = new ListLog();
        var settings = new EngineSettings { ExecutablePath = Path.Combine(Path.GetTempPath(), "does-not-exist", "LiveCore.MediaEngine") };
        await using var sup = new EngineSupervisor(() => settings, log);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => sup.StartAsync());
        Assert.True(ex.Message.Contains("not found", StringComparison.Ordinal), ex.Message);
        Assert.Equal(EngineLifecycleState.Failed, sup.Health.State);
    }
}
