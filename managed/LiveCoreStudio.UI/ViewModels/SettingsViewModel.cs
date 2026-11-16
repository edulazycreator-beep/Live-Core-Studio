using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LiveCoreStudio.Application.Abstractions;
using LiveCoreStudio.Core.Configuration;
using LiveCoreStudio.Core.Diagnostics;

namespace LiveCoreStudio.UI.ViewModels;

/// <summary>
/// Edits a working copy of the settings. Nothing is applied until Save succeeds;
/// engine settings take effect on the next engine start.
/// </summary>
public sealed partial class SettingsViewModel : PageViewModel
{
    private readonly ISettingsStore _store;
    private readonly IAppLog _log;
    private readonly Action<AppSettings> _onSaved;
    private AppSettings _saved;

    public SettingsViewModel(ISettingsStore store, AppSettings current, IAppLog log, Action<AppSettings> onSaved)
        : base("settings", "Settings", "Icon.Settings")
    {
        _store = store;
        _log = log;
        _onSaved = onSaved;
        _saved = current;
        Load(current);
    }

    public string SettingsPath => _store.Location;
    public ObservableCollection<string> Errors { get; } = [];

    [ObservableProperty] private string _executablePath = "";
    [ObservableProperty] private bool _startWithApplication;
    [ObservableProperty] private bool _autoRestart;
    [ObservableProperty] private int _heartbeatIntervalMs;
    [ObservableProperty] private int _heartbeatTimeoutMs;
    [ObservableProperty] private int _maxMissedHeartbeats;
    [ObservableProperty] private int _maxRestartsPerWindow;
    [ObservableProperty] private int _restartWindowSeconds;
    [ObservableProperty] private int _clockRateHz;
    [ObservableProperty] private string _logLevel = "info";
    [ObservableProperty] private bool _confirmOnExit;
    [ObservableProperty] private string? _statusMessage;

    public IReadOnlyList<string> LogLevels { get; } = ["trace", "debug", "info", "warn", "error", "off"];

    private void Load(AppSettings s)
    {
        ExecutablePath = s.Engine.ExecutablePath ?? "";
        StartWithApplication = s.Engine.StartWithApplication;
        AutoRestart = s.Engine.AutoRestart;
        HeartbeatIntervalMs = s.Engine.HeartbeatIntervalMs;
        HeartbeatTimeoutMs = s.Engine.HeartbeatTimeoutMs;
        MaxMissedHeartbeats = s.Engine.MaxMissedHeartbeats;
        MaxRestartsPerWindow = s.Engine.MaxRestartsPerWindow;
        RestartWindowSeconds = s.Engine.RestartWindowSeconds;
        ClockRateHz = s.Engine.ClockRateHz;
        LogLevel = s.Engine.LogLevel;
        ConfirmOnExit = s.Interface.ConfirmOnExit;
        Errors.Clear();
    }

    private AppSettings Build() => _saved with
    {
        Engine = _saved.Engine with
        {
            ExecutablePath = string.IsNullOrWhiteSpace(ExecutablePath) ? null : ExecutablePath.Trim(),
            StartWithApplication = StartWithApplication,
            AutoRestart = AutoRestart,
            HeartbeatIntervalMs = HeartbeatIntervalMs,
            HeartbeatTimeoutMs = HeartbeatTimeoutMs,
            MaxMissedHeartbeats = MaxMissedHeartbeats,
            MaxRestartsPerWindow = MaxRestartsPerWindow,
            RestartWindowSeconds = RestartWindowSeconds,
            ClockRateHz = ClockRateHz,
            LogLevel = LogLevel,
        },
        Interface = _saved.Interface with { ConfirmOnExit = ConfirmOnExit },
    };

    [RelayCommand]
    private async Task SaveAsync()
    {
        Errors.Clear();
        StatusMessage = null;
        var candidate = Build();
        try
        {
            await _store.SaveAsync(candidate);
            _saved = candidate;
            _onSaved(candidate);
            StatusMessage = "Saved. Engine changes apply the next time the engine starts.";
        }
        catch (SettingsValidationException ex)
        {
            foreach (var e in ex.Errors) Errors.Add(e);
        }
        catch (IOException ex)
        {
            _log.Error("settings", ex.Message);
            Errors.Add($"Could not write settings: {ex.Message}");
        }
    }

    [RelayCommand]
    private void Revert()
    {
        Load(_saved);
        StatusMessage = null;
    }

    [RelayCommand]
    private void RestoreDefaults()
    {
        Load(new AppSettings());
        StatusMessage = "Defaults loaded. Press Save to keep them.";
    }
}
