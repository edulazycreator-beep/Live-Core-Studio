using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LiveCoreStudio.Application.Abstractions;
using LiveCoreStudio.Core.Diagnostics;
using LiveCoreStudio.Core.Production;
using LiveCoreStudio.EngineClient;

namespace LiveCoreStudio.UI.ViewModels;

/// <summary>One live monitor (Program or Preview) fed from the engine's shared-memory feed.</summary>
public sealed partial class MonitorViewModel(string label) : ObservableObject, IDisposable
{
    private MonitorFrameReader? _reader;
    private byte[] _buffer = [];
    private ulong _lastFrame;

    public string Label { get; } = label;
    [ObservableProperty] private WriteableBitmap? _image;
    [ObservableProperty] private bool _hasSignal;

    /// <summary>Opens the feed if needed and copies the newest frame into the bitmap. UI thread only.</summary>
    public void Refresh(string feedName)
    {
        if (_reader is null || _reader.Name != feedName)
        {
            _reader?.Dispose();
            _reader = MonitorFrameReader.TryOpen(feedName);
            if (_reader is null) { HasSignal = false; return; }
            _buffer = new byte[_reader.BufferSize];
            Image = new WriteableBitmap(_reader.Width, _reader.Height, 96, 96, PixelFormats.Bgra32, null);
        }
        if (!_reader.TryRead(_buffer, out var frame) || frame == _lastFrame) return;
        _lastFrame = frame;
        Image!.WritePixels(new Int32Rect(0, 0, _reader.Width, _reader.Height), _buffer, _reader.Stride, 0);
        HasSignal = true;
    }

    public void Disconnect()
    {
        _reader?.Dispose();
        _reader = null;
        HasSignal = false;
    }

    public void Dispose() => _reader?.Dispose();
}

/// <summary>A source as shown on the bus rows and in the source list.</summary>
public sealed partial class SourceItemViewModel(SourceInfo info) : ObservableObject
{
    public uint Id { get; } = info.Id;
    public int Index { get; set; }
    [ObservableProperty] private string _name = info.Name;
    [ObservableProperty] private string _kind = info.Kind;
    [ObservableProperty] private string _status = info.Status;
    [ObservableProperty] private string _resolution = $"{info.Width}×{info.Height}";
    [ObservableProperty] private bool _isProgram;
    [ObservableProperty] private bool _isPreview;

    public void Update(SourceInfo i, uint program, uint preview)
    {
        Name = i.Name;
        Kind = i.Kind == "device" ? "Camera" : "Test pattern";
        Status = i.Status;
        Resolution = i.Width > 0 ? $"{i.Width}×{i.Height}" : "—";
        IsProgram = i.Id == program;
        IsPreview = i.Id == preview;
    }
}

/// <summary>Live switching page: monitors, Program/Preview buses, transitions and sources.</summary>
public sealed partial class ProductionViewModel : PageViewModel, IDisposable
{
    private readonly IProductionService _production;
    private readonly IAppLog _log;
    private readonly DispatcherTimer _monitorTimer;
    private readonly DispatcherTimer _stateTimer;
    private SwitcherState? _state;
    private bool _polling;

    public ProductionViewModel(IProductionService production, IAppLog log)
        : base("production", "Production", "Icon.Production")
    {
        _production = production;
        _log = log;
        _monitorTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(33), DispatcherPriority.Render, (_, _) => RefreshMonitors(), Dispatcher.CurrentDispatcher);
        _stateTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(250), DispatcherPriority.Background, async (_, _) => await PollStateAsync(), Dispatcher.CurrentDispatcher);
        _monitorTimer.Start();
        _stateTimer.Start();
    }

    public MonitorViewModel Program { get; } = new("PROGRAM");
    public MonitorViewModel Preview { get; } = new("PREVIEW");
    public ObservableCollection<SourceItemViewModel> Sources { get; } = [];
    public ObservableCollection<VideoDeviceInfo> Devices { get; } = [];
    public IReadOnlyList<string> PatternKinds { get; } = ["bars", "solid"];

    [ObservableProperty] private bool _engineReady;
    [ObservableProperty] private bool _transitioning;
    [ObservableProperty] private double _transitionPosition;
    [ObservableProperty] private int _transitionMs = 1000;
    [ObservableProperty] private string _pipelineInfo = "Engine not running";
    [ObservableProperty] private string? _message;

    [ObservableProperty] private string _newPatternName = "Colour bars";
    [ObservableProperty] private string _newPatternKind = "bars";
    [ObservableProperty] private string _newPatternColor = "#FF7A1A";
    [ObservableProperty] private VideoDeviceInfo? _selectedDevice;
    [ObservableProperty] private string? _deviceHint;

    public override void OnNavigatedTo() => _ = RefreshDevicesAsync();

    private void RefreshMonitors()
    {
        if (_state is null || !EngineReady)
        {
            Program.Disconnect();
            Preview.Disconnect();
            return;
        }
        Program.Refresh(_state.ProgramMonitor);
        Preview.Refresh(_state.PreviewMonitor);
    }

    private async Task PollStateAsync()
    {
        if (_polling) return;
        _polling = true;
        try
        {
            var state = await _production.GetStateAsync();
            _state = state;
            EngineReady = true;
            Transitioning = state.Transitioning;
            TransitionPosition = state.Position;
            PipelineInfo = string.Create(CultureInfo.InvariantCulture,
                $"{state.OutputWidth}×{state.OutputHeight} @ {state.Fps} fps · {state.Adapter}{(state.SoftwareRendering ? " (software)" : "")} · dropped {state.FramesDropped}");
            SyncSources(state);
        }
        catch (Exception ex) when (ex is EngineNotRunningException or TimeoutException or LiveCoreStudio.EngineClient.Protocol.EngineDisconnectedException)
        {
            EngineReady = false;
            _state = null;
            PipelineInfo = "Engine not running";
        }
        catch (LiveCoreStudio.EngineClient.Protocol.EngineProtocolException ex)
        {
            EngineReady = false;
            PipelineInfo = ex.Message;
        }
        finally
        {
            _polling = false;
        }
    }

    private void SyncSources(SwitcherState state)
    {
        var byId = state.Sources.ToDictionary(s => s.Id);
        for (var i = Sources.Count - 1; i >= 0; i--)
            if (!byId.ContainsKey(Sources[i].Id)) Sources.RemoveAt(i);
        foreach (var info in state.Sources.OrderBy(s => s.Id))
        {
            var item = Sources.FirstOrDefault(s => s.Id == info.Id);
            if (item is null) Sources.Add(item = new SourceItemViewModel(info));
            item.Update(info, state.Program, state.Preview);
        }
        for (var i = 0; i < Sources.Count; i++) Sources[i].Index = i + 1;
    }

    private async Task RunAsync(string what, Func<Task> action)
    {
        Message = null;
        try
        {
            await action();
            await PollStateAsync();
        }
        catch (Exception ex)
        {
            Message = $"{what} failed: {ex.Message}";
            _log.Warn("production", Message);
        }
    }

    [RelayCommand]
    private Task SetProgramAsync(SourceItemViewModel source) => RunAsync("Take to program", () => _production.SetProgramAsync(source.Id));

    [RelayCommand]
    private Task SetPreviewAsync(SourceItemViewModel source) => RunAsync("Select preview", () => _production.SetPreviewAsync(source.Id));

    [RelayCommand]
    private Task CutAsync() => RunAsync("Cut", () => _production.CutAsync());

    [RelayCommand]
    private Task AutoAsync() => RunAsync("Auto", () => _production.AutoTransitionAsync(Math.Clamp(TransitionMs, 0, 10_000)));

    [RelayCommand]
    private Task RemoveSourceAsync(SourceItemViewModel source) => RunAsync("Remove source", () => _production.RemoveSourceAsync(source.Id));

    [RelayCommand]
    private Task AddTestPatternAsync() => RunAsync("Add test pattern", async () =>
    {
        var color = NewPatternColor.Trim();
        if (color.Length != 7 || color[0] != '#' || !int.TryParse(color.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out _))
            throw new ArgumentException("Colour must look like #RRGGBB.");
        await _production.AddTestPatternAsync(string.IsNullOrWhiteSpace(NewPatternName) ? "Test pattern" : NewPatternName.Trim(), NewPatternKind, color);
    });

    [RelayCommand]
    private Task AddDeviceAsync() => RunAsync("Add camera", async () =>
    {
        if (SelectedDevice is null) throw new InvalidOperationException("Choose a capture device first.");
        await _production.AddDeviceSourceAsync(SelectedDevice.Name, SelectedDevice.Id);
    });

    [RelayCommand]
    private Task RefreshDevicesAsync() => RunAsync("List capture devices", async () =>
    {
        var devices = await _production.ListVideoDevicesAsync();
        Devices.Clear();
        foreach (var d in devices) Devices.Add(d);
        SelectedDevice ??= Devices.FirstOrDefault();
        DeviceHint = Devices.Count == 0 ? "No capture devices found. Connect a USB/HDMI capture device, then Refresh." : null;
    });

    public void Dispose()
    {
        _monitorTimer.Stop();
        _stateTimer.Stop();
        Program.Dispose();
        Preview.Dispose();
    }
}
