using System.IO;
using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LiveCoreStudio.Core.Diagnostics;
using LiveCoreStudio.UI.Services;

namespace LiveCoreStudio.UI.ViewModels;

/// <summary>Live application and engine log, capped to keep long sessions light.</summary>
public sealed partial class DiagnosticsViewModel : PageViewModel
{
    private const int MaxEntries = 1500;
    private readonly string _logFolder;
    private readonly List<AppLogEntry> _all = [];

    public DiagnosticsViewModel(IAppLog log, IUiDispatcher dispatcher, string logFolder, IEnumerable<AppLogEntry> history)
        : base("diagnostics", "Diagnostics", "Icon.Diagnostics")
    {
        _logFolder = logFolder;
        foreach (var e in history) Add(e);
        log.EntryWritten += e => dispatcher.Post(() => Add(e));
    }

    public ObservableCollection<AppLogEntry> Entries { get; } = [];

    [ObservableProperty] private bool _showEngineOutput = true;
    [ObservableProperty] private bool _showDebug;

    partial void OnShowEngineOutputChanged(bool value) => Rebuild();
    partial void OnShowDebugChanged(bool value) => Rebuild();

    private bool Visible(AppLogEntry e) =>
        (ShowEngineOutput || e.Source != "engine") &&
        (ShowDebug || e.Level != AppLogLevel.Debug || e.Source == "engine");

    private void Add(AppLogEntry e)
    {
        _all.Add(e);
        if (_all.Count > MaxEntries) _all.RemoveAt(0);
        if (!Visible(e)) return;
        Entries.Add(e);
        while (Entries.Count > MaxEntries) Entries.RemoveAt(0);
    }

    private void Rebuild()
    {
        Entries.Clear();
        foreach (var e in _all.Where(Visible)) Entries.Add(e);
    }

    [RelayCommand]
    private void Clear()
    {
        _all.Clear();
        Entries.Clear();
    }

    [RelayCommand]
    private void OpenLogFolder()
    {
        Directory.CreateDirectory(_logFolder);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{_logFolder}\"") { UseShellExecute = true });
    }
}
