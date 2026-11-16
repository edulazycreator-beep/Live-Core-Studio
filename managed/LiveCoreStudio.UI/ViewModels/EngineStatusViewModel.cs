using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LiveCoreStudio.Application.Abstractions;
using LiveCoreStudio.Core.Diagnostics;
using LiveCoreStudio.Core.Engine;
using LiveCoreStudio.UI.Services;

namespace LiveCoreStudio.UI.ViewModels;

/// <summary>
/// Live engine health, shared by the dashboard card and the shell status bar.
/// Updates arrive from the supervisor on a background thread and are marshalled to the UI.
/// </summary>
public sealed partial class EngineStatusViewModel : ObservableObject
{
    private readonly IEngineController _engine;
    private readonly IAppLog _log;

    public EngineStatusViewModel(IEngineController engine, IUiDispatcher dispatcher, IAppLog log)
    {
        _engine = engine;
        _log = log;
        Apply(engine.Health);
        engine.HealthChanged += h => dispatcher.Post(() => Apply(h));
    }

    [ObservableProperty] private EngineLifecycleState _state;
    [ObservableProperty] private string _stateText = "Stopped";
    [ObservableProperty] private string _processId = "—";
    [ObservableProperty] private string _engineVersion = "—";
    [ObservableProperty] private string _platform = "—";
    [ObservableProperty] private string _protocol = "—";
    [ObservableProperty] private string _uptime = "—";
    [ObservableProperty] private string _clock = "—";
    [ObservableProperty] private string _roundTrip = "—";
    [ObservableProperty] private int _missedHeartbeats;
    [ObservableProperty] private int _restartCount;
    [ObservableProperty] private string? _lastError;
    [ObservableProperty] private bool _isBusy;

    public bool CanStart => !IsBusy && State is EngineLifecycleState.Stopped or EngineLifecycleState.Failed;
    public bool CanStop => !IsBusy && State is not (EngineLifecycleState.Stopped or EngineLifecycleState.Failed);

    partial void OnStateChanged(EngineLifecycleState value) => RefreshCommands();
    partial void OnIsBusyChanged(bool value) => RefreshCommands();

    private void RefreshCommands()
    {
        OnPropertyChanged(nameof(CanStart));
        OnPropertyChanged(nameof(CanStop));
        StartCommand.NotifyCanExecuteChanged();
        StopCommand.NotifyCanExecuteChanged();
        RestartCommand.NotifyCanExecuteChanged();
    }

    private void Apply(EngineHealthSnapshot h)
    {
        var inv = CultureInfo.InvariantCulture;
        State = h.State;
        StateText = h.State switch
        {
            EngineLifecycleState.Running => "Running",
            EngineLifecycleState.Starting => "Starting…",
            EngineLifecycleState.Restarting => "Restarting…",
            EngineLifecycleState.Stopping => "Stopping…",
            EngineLifecycleState.Faulted => "Faulted",
            EngineLifecycleState.Failed => "Failed",
            _ => "Stopped",
        };
        ProcessId = h.ProcessId?.ToString(inv) ?? "—";
        EngineVersion = h.EngineVersion ?? "—";
        Platform = h.Platform ?? "—";
        Protocol = h.ProtocolVersion is { } p ? $"v{p}" : "—";
        Uptime = h.EngineUptimeMs is { } ms && h.State == EngineLifecycleState.Running
            ? TimeSpan.FromMilliseconds(ms).ToString(@"hh\:mm\:ss", inv) : "—";
        Clock = h.ClockTicks is { } t && h.ClockRateHz is { } hz ? $"{t:N0} ticks @ {hz} Hz" : "—";
        RoundTrip = h.LastRoundTripMs is { } rtt ? $"{rtt:0.00} ms" : "—";
        MissedHeartbeats = h.MissedHeartbeats;
        RestartCount = h.RestartCount;
        LastError = h.LastError;
    }

    [RelayCommand(CanExecute = nameof(CanStart))]
    private Task StartAsync() => RunAsync("start", ct => _engine.StartAsync(ct));

    [RelayCommand(CanExecute = nameof(CanStop))]
    private Task StopAsync() => RunAsync("stop", ct => _engine.StopAsync(ct));

    [RelayCommand(CanExecute = nameof(CanStop))]
    private Task RestartAsync() => RunAsync("restart", ct => _engine.RestartAsync(ct));

    private async Task RunAsync(string action, Func<CancellationToken, Task> operation)
    {
        IsBusy = true;
        try
        {
            await operation(CancellationToken.None);
        }
        catch (Exception ex)
        {
            _log.Error("ui", $"Engine {action} failed: {ex.Message}");
            LastError = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }
}
