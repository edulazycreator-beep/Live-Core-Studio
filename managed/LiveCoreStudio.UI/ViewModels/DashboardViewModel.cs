using System.Reflection;
using LiveCoreStudio.Application.Abstractions;

namespace LiveCoreStudio.UI.ViewModels;

/// <summary>Landing page: engine control and session facts.</summary>
public sealed class DashboardViewModel(EngineStatusViewModel engine, ISettingsStore settings, string logFolder)
    : PageViewModel("dashboard", "Dashboard", "Icon.Dashboard")
{
    public EngineStatusViewModel Engine { get; } = engine;

    public string AppVersion { get; } =
        Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion?.Split('+')[0]
        ?? "0.1.0";

    public string RuntimeVersion { get; } = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription;
    public string SettingsPath { get; } = settings.Location;
    public string LogFolder { get; } = logFolder;
}
