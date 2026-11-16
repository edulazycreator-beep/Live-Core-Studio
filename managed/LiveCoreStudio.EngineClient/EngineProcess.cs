using System.Diagnostics;
using LiveCoreStudio.Core.Configuration;
using LiveCoreStudio.Core.Diagnostics;

namespace LiveCoreStudio.EngineClient;

/// <summary>Launches LiveCore.MediaEngine as a child process and relays its log output.</summary>
public sealed class EngineProcess : IAsyncDisposable
{
    public const string ExecutableBaseName = "LiveCore.MediaEngine";

    private readonly Process _process;

    private EngineProcess(Process process, string pipeName)
    {
        _process = process;
        PipeName = pipeName;
    }

    public string PipeName { get; }
    public int Id => _process.Id;
    public bool HasExited => _process.HasExited;
    public int? ExitCode => _process.HasExited ? _process.ExitCode : null;

    /// <summary>Resolves the engine path: explicit setting, else next to the application.</summary>
    public static string ResolveExecutable(EngineSettings settings)
    {
        if (!string.IsNullOrWhiteSpace(settings.ExecutablePath)) return settings.ExecutablePath;
        var name = OperatingSystem.IsWindows() ? ExecutableBaseName + ".exe" : ExecutableBaseName;
        return Path.Combine(AppContext.BaseDirectory, name);
    }

    /// <summary>A pipe name unique to this application instance and launch.</summary>
    public static string NewPipeName() =>
        $"LiveCore.Engine.{Environment.ProcessId}.{Guid.NewGuid().ToString("N")[..8]}";

    public static EngineProcess Start(string executable, EngineSettings settings, string pipeName, IAppLog log)
    {
        if (!File.Exists(executable))
            throw new FileNotFoundException($"Media engine not found at '{executable}'. Build the native engine or set its path in Settings.", executable);

        var psi = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            WorkingDirectory = Path.GetDirectoryName(executable) ?? AppContext.BaseDirectory,
        };
        psi.ArgumentList.Add("--pipe");
        psi.ArgumentList.Add(pipeName);
        psi.ArgumentList.Add("--parent-pid");
        psi.ArgumentList.Add(Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        psi.ArgumentList.Add("--clock-rate");
        psi.ArgumentList.Add(settings.ClockRateHz.ToString(System.Globalization.CultureInfo.InvariantCulture));
        psi.ArgumentList.Add("--log-level");
        psi.ArgumentList.Add(settings.LogLevel);

        var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        process.ErrorDataReceived += (_, e) => { if (e.Data is { Length: > 0 }) log.Write(AppLogLevel.Debug, "engine", e.Data); };
        process.OutputDataReceived += (_, e) => { if (e.Data is { Length: > 0 }) log.Write(AppLogLevel.Debug, "engine", e.Data); };
        if (!process.Start()) throw new InvalidOperationException("Engine process failed to start.");
        process.BeginErrorReadLine();
        process.BeginOutputReadLine();
        log.Info("supervisor", $"Engine started (pid {process.Id}, pipe {pipeName}).");
        return new EngineProcess(process, pipeName);
    }

    public async Task<bool> WaitForExitAsync(TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        try
        {
            await _process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    public void Kill()
    {
        try
        {
            if (!_process.HasExited) _process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException) { /* already exited */ }
        catch (System.ComponentModel.Win32Exception) { /* exiting */ }
    }

    public async ValueTask DisposeAsync()
    {
        Kill();
        await WaitForExitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        _process.Dispose();
    }
}
