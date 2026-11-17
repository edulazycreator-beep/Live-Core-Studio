namespace LiveCoreStudio.Core.Production;

/// <summary>A video capture device reported by the engine (USB / HDMI capture via Media Foundation).</summary>
public sealed record VideoDeviceInfo(string Name, string Id);

/// <summary>A source currently loaded in the engine.</summary>
public sealed record SourceInfo(uint Id, string Name, string Kind, string Status, int Width, int Height);

/// <summary>What the operator asked for when adding a source. Kept by the app so sources can be restored after an engine restart.</summary>
public sealed record SourceSpec(uint Id, string Kind, string Name, string Pattern = "bars", string Color = "#000000", string? DeviceId = null)
{
    public static SourceSpec TestPattern(uint id, string name, string pattern, string color) => new(id, "testpattern", name, pattern, color);
    public static SourceSpec Device(uint id, string name, string deviceId) => new(id, "device", name, DeviceId: deviceId);
}

/// <summary>Switcher and pipeline state as reported by the engine.</summary>
public sealed record SwitcherState
{
    public uint Program { get; init; }
    public uint Preview { get; init; }
    public bool Transitioning { get; init; }
    public double Position { get; init; }
    public int TransitionMs { get; init; }
    public int OutputWidth { get; init; }
    public int OutputHeight { get; init; }
    public int Fps { get; init; }
    public string Adapter { get; init; } = "";
    public bool SoftwareRendering { get; init; }
    public long FramesRendered { get; init; }
    public long FramesDropped { get; init; }
    public string ProgramMonitor { get; init; } = "";
    public string PreviewMonitor { get; init; } = "";
    public int MonitorWidth { get; init; }
    public int MonitorHeight { get; init; }
    public IReadOnlyList<SourceInfo> Sources { get; init; } = [];
}
