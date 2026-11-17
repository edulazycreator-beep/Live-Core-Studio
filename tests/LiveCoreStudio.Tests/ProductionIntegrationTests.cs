using System.Diagnostics;
using System.Runtime.Versioning;
using LiveCoreStudio.Core.Configuration;
using LiveCoreStudio.Core.Diagnostics;
using LiveCoreStudio.Core.Engine;
using LiveCoreStudio.Core.Production;
using LiveCoreStudio.EngineClient;
using LiveCoreStudio.EngineClient.Protocol;

namespace LiveCoreStudio.Tests;

/// <summary>
/// Phase 4 end-to-end: the real engine renders sources with Direct3D 11 (WARP on GPU-less CI),
/// and the tests read the actual Program / Preview pixels from the shared-memory monitor feeds.
/// </summary>
public static class ProductionIntegrationTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(15);

    private sealed class NullLog : IAppLog
    {
        public event Action<AppLogEntry>? EntryWritten;
        public void Write(AppLogLevel level, string source, string message) => EntryWritten?.Invoke(new(DateTimeOffset.Now, level, source, message));
    }

    private static EngineSettings Settings() => new()
    {
        ExecutablePath = EngineIntegrationTests.EnginePathForTests(),
        HeartbeatIntervalMs = 250,
        HeartbeatTimeoutMs = 2000,
        MaxMissedHeartbeats = 4,
        LogLevel = "warn",
    };

    private readonly record struct Rgb(int R, int G, int B)
    {
        public bool IsRed => R > 200 && G < 50 && B < 50;
        public bool IsBlue => B > 200 && R < 50 && G < 50;
        public override string ToString() => $"rgb({R},{G},{B})";
    }

    [SupportedOSPlatform("windows")]
    private static Rgb Center(MonitorFrameReader reader, byte[] buffer)
    {
        if (!reader.TryRead(buffer, out _)) return new Rgb(-1, -1, -1);
        var i = (reader.Height / 2) * reader.Stride + (reader.Width / 2) * 4;
        return new Rgb(buffer[i + 2], buffer[i + 1], buffer[i]);  // BGRA
    }

    [SupportedOSPlatform("windows")]
    private sealed class Monitors : IDisposable
    {
        public required MonitorFrameReader Program { get; init; }
        public required MonitorFrameReader Preview { get; init; }
        private readonly byte[] _p = new byte[8 << 20];
        private readonly byte[] _v = new byte[8 << 20];
        public Rgb ProgramCenter => Center(Program, _p);
        public Rgb PreviewCenter => Center(Preview, _v);
        public void Dispose() { Program.Dispose(); Preview.Dispose(); }

        public static async Task<Monitors> OpenAsync(EngineProductionService production)
        {
            var state = await production.GetStateAsync();
            MonitorFrameReader? program = null, preview = null;
            await Assert.EventuallyAsync(() =>
            {
                program ??= MonitorFrameReader.TryOpen(state.ProgramMonitor);
                preview ??= MonitorFrameReader.TryOpen(state.PreviewMonitor);
                return program is not null && preview is not null;
            }, Wait, $"monitor feeds '{state.ProgramMonitor}' / '{state.PreviewMonitor}' open");
            return new Monitors { Program = program!, Preview = preview! };
        }
    }

    private static void RequireWindows()
    {
        if (!OperatingSystem.IsWindows()) throw new SkipException("video pipeline is Windows-only (Direct3D 11)");
        Environment.SetEnvironmentVariable("LIVECORE_FORCE_WARP", "1");  // deterministic on GPU-less CI
    }

    [Test("production")]
    public static Task Switching_renders_real_pixels_on_program_and_preview()
    {
        RequireWindows();
        return OperatingSystem.IsWindows() ? SwitchingCoreAsync() : Task.CompletedTask;
    }

    [SupportedOSPlatform("windows")]
    private static async Task SwitchingCoreAsync()
    {
        var log = new NullLog();
        await using var engine = new EngineSupervisor(Settings, log);
        var production = new EngineProductionService(engine, log);
        await engine.StartAsync();

        var red = await production.AddTestPatternAsync("Red", "solid", "#FF0000");
        var blue = await production.AddTestPatternAsync("Blue", "solid", "#0000FF");
        await production.SetProgramAsync(red.Id);
        await production.SetPreviewAsync(blue.Id);

        var state = await production.GetStateAsync();
        Assert.Equal(2, state.Sources.Count, "sources");
        Assert.True(!string.IsNullOrEmpty(state.Adapter), "adapter reported");
        Console.WriteLine($"         renderer: {state.Adapter}, {state.OutputWidth}x{state.OutputHeight}@{state.Fps}");

        using var monitors = await Monitors.OpenAsync(production);
        await Assert.EventuallyAsync(() => monitors.ProgramCenter.IsRed && monitors.PreviewCenter.IsBlue, Wait,
            $"program red / preview blue (got {monitors.ProgramCenter} / {monitors.PreviewCenter})");

        await production.CutAsync();
        await Assert.EventuallyAsync(() => monitors.ProgramCenter.IsBlue && monitors.PreviewCenter.IsRed, Wait,
            $"after CUT program blue / preview red (got {monitors.ProgramCenter} / {monitors.PreviewCenter})");

        // AUTO: a 1.5 s mix. We must observe the engine mid-transition and a blended program picture.
        await production.AutoTransitionAsync(1500);
        var sawTransition = false;
        var sawBlend = false;
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < TimeSpan.FromSeconds(1.4))
        {
            if ((await production.GetStateAsync()).Transitioning) sawTransition = true;
            var c = monitors.ProgramCenter;
            if (c.R > 40 && c.B > 40) sawBlend = true;
            await Task.Delay(40);
        }
        Assert.True(sawTransition, "switcher reported a running transition");
        Assert.True(sawBlend, "program showed a red/blue blend during the mix");
        await Assert.EventuallyAsync(() => monitors.ProgramCenter.IsRed && monitors.PreviewCenter.IsBlue, Wait,
            $"after AUTO program red / preview blue (got {monitors.ProgramCenter} / {monitors.PreviewCenter})");
        state = await production.GetStateAsync();
        Assert.Equal(red.Id, state.Program, "program after auto");
        Assert.Equal(blue.Id, state.Preview, "preview after auto");

        // The pipeline keeps real time.
        var before = state.FramesRendered;
        await Task.Delay(1000);
        var after = (await production.GetStateAsync()).FramesRendered;
        Console.WriteLine($"         {after - before} frames/s rendered (target {state.Fps}), dropped {state.FramesDropped}");
        Assert.True(after - before >= state.Fps / 2, $"renders at least half the target rate ({after - before} fps)");

        // Removing the on-air source clears Program to black.
        await production.RemoveSourceAsync(red.Id);
        await Assert.EventuallyAsync(() => monitors.ProgramCenter is { R: < 20, G: < 20, B: < 20 }, Wait,
            $"program black after removing its source (got {monitors.ProgramCenter})");

        var devices = await production.ListVideoDevicesAsync();
        Console.WriteLine($"         {devices.Count} capture device(s) on this machine");
    }

    [Test("production")]
    public static Task Setup_is_restored_after_an_engine_crash()
    {
        RequireWindows();
        return OperatingSystem.IsWindows() ? RestoreCoreAsync() : Task.CompletedTask;
    }

    [SupportedOSPlatform("windows")]
    private static async Task RestoreCoreAsync()
    {
        var log = new NullLog();
        await using var engine = new EngineSupervisor(Settings, log);
        var production = new EngineProductionService(engine, log);
        await engine.StartAsync();

        var red = await production.AddTestPatternAsync("Red", "solid", "#FF0000");
        var blue = await production.AddTestPatternAsync("Blue", "solid", "#0000FF");
        var bars = await production.AddTestPatternAsync("Bars", "bars", "#000000");
        await production.SetProgramAsync(red.Id);
        await production.SetPreviewAsync(blue.Id);

        var firstPid = engine.Health.ProcessId!.Value;
        using (var p = Process.GetProcessById(firstPid)) { p.Kill(); p.WaitForExit(3000); }

        await Assert.EventuallyAsync(() => engine.Health is { State: EngineLifecycleState.Running, ProcessId: { } pid } && pid != firstPid,
            Wait, "engine restarted");
        await production.LastRestore;

        var state = await production.GetStateAsync();
        Assert.Equal(3, state.Sources.Count, "sources restored");
        Assert.True(state.Sources.Select(s => s.Id).Order().SequenceEqual(new[] { red.Id, blue.Id, bars.Id }.Order()), "same source ids");
        Assert.Equal(red.Id, state.Program, "program restored");
        Assert.Equal(blue.Id, state.Preview, "preview restored");

        using var monitors = await Monitors.OpenAsync(production);
        await Assert.EventuallyAsync(() => monitors.ProgramCenter.IsRed && monitors.PreviewCenter.IsBlue, Wait,
            $"restored picture (got {monitors.ProgramCenter} / {monitors.PreviewCenter})");
    }

    [Test("production")]
    public static async Task Engine_without_video_reports_it_clearly()
    {
        if (OperatingSystem.IsWindows()) throw new SkipException("covered on platforms without the video pipeline");
        var log = new NullLog();
        await using var engine = new EngineSupervisor(Settings, log);
        var production = new EngineProductionService(engine, log);
        await engine.StartAsync();
        var ex = await Assert.ThrowsAsync<EngineProtocolException>(() => production.GetStateAsync());
        Assert.Equal("video_unavailable", ex.Code);
        await Assert.ThrowsAsync<LiveCoreStudio.Application.Abstractions.EngineNotRunningException>(async () =>
        {
            await engine.StopAsync();
            await production.CutAsync();
        });
    }
}
