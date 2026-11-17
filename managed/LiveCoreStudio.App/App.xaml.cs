using System.ComponentModel;
using System.Windows;
using System.Windows.Threading;
using LiveCoreStudio.Core.Configuration;
using LiveCoreStudio.Core.Diagnostics;
using LiveCoreStudio.Core.Engine;
using LiveCoreStudio.EngineClient;
using LiveCoreStudio.Infrastructure;
using LiveCoreStudio.Infrastructure.Configuration;
using LiveCoreStudio.Infrastructure.Diagnostics;
using LiveCoreStudio.UI.Services;
using LiveCoreStudio.UI.ViewModels;
using LiveCoreStudio.UI.Views;

namespace LiveCoreStudio.App;

/// <summary>
/// Composition root. Wires services by hand (no DI container) to keep dependencies minimal
/// and the object graph obvious.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001:Types that own disposable fields should be disposable",
    Justification = "WPF owns the Application lifetime; the fields are disposed in OnExit.")]
public partial class App : System.Windows.Application
{
    private FileAppLog? _log;
    private JsonSettingsStore? _settingsStore;
    private EngineSupervisor? _engine;
    private EngineProductionService? _production;
    private AppSettings _settings = new();
    private bool _shutdownComplete;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var paths = new AppPaths();
        paths.EnsureCreated();
        _log = new FileAppLog(paths.Logs);
        _log.Info("app", $"Live Core Studio starting (pid {Environment.ProcessId}).");
        HookUnhandledExceptions(_log);

        _settingsStore = new JsonSettingsStore(paths.SettingsFile, _log);
        _settings = _settingsStore.Load();

        _engine = new EngineSupervisor(() => _settings.Engine, _log);

        var dispatcher = new WpfUiDispatcher(Dispatcher);
        var engineStatus = new EngineStatusViewModel(_engine, dispatcher, _log);
        _production = new EngineProductionService(_engine, _log);
        var pages = new PageViewModel[]
        {
            new DashboardViewModel(engineStatus, _settingsStore, paths.Logs),
            new ProductionViewModel(_production, _log),
            new DiagnosticsViewModel(_log, dispatcher, paths.Logs, _log.Recent),
            new SettingsViewModel(_settingsStore, _settings, _log, saved => _settings = saved),
        };
        var shell = new ShellViewModel(pages, engineStatus, _settings.Interface.LastPage, RememberPage);

        var window = new ShellWindow { DataContext = shell };
        window.Closing += OnMainWindowClosing;
        MainWindow = window;
        window.Show();

        if (_settings.Engine.StartWithApplication)
            _ = StartEngineAsync();
    }

    private async Task StartEngineAsync()
    {
        try
        {
            await _engine!.StartAsync();
        }
        catch (Exception ex)
        {
            // Already reflected in the engine status; the app stays usable so the operator can fix settings.
            _log!.Error("app", $"Engine did not start: {ex.Message}");
        }
    }

    private void RememberPage(string key)
    {
        _settings = _settings with { Interface = _settings.Interface with { LastPage = key } };
        _ = _settingsStore!.SaveAsync(_settings).ContinueWith(
            t => _log!.Warn("app", $"Could not persist last page: {t.Exception?.GetBaseException().Message}"),
            TaskContinuationOptions.OnlyOnFaulted);
    }

    private void OnMainWindowClosing(object? sender, CancelEventArgs e)
    {
        if (_shutdownComplete) return;

        var running = _engine?.Health.State is EngineLifecycleState.Running or EngineLifecycleState.Starting or EngineLifecycleState.Restarting;
        if (running && _settings.Interface.ConfirmOnExit)
        {
            var answer = MessageBox.Show(MainWindow!, "The media engine is running. Stop it and close Live Core Studio?",
                "Live Core Studio", MessageBoxButton.OKCancel, MessageBoxImage.Question, MessageBoxResult.Cancel);
            if (answer != MessageBoxResult.OK)
            {
                e.Cancel = true;
                return;
            }
        }

        // Stop the engine without blocking the UI thread, then close for real.
        e.Cancel = true;
        MainWindow!.IsEnabled = false;
        _ = ShutdownAsync();
    }

    private async Task ShutdownAsync()
    {
        try
        {
            if (_engine is not null)
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
                await _engine.StopAsync(cts.Token);
            }
        }
        catch (Exception ex)
        {
            _log?.Error("app", $"Engine stop during exit failed: {ex.Message}");
        }
        finally
        {
            _shutdownComplete = true;
            MainWindow?.Close();
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _log?.Info("app", "Live Core Studio exited.");
        // The engine also exits on its own when this process dies (--parent-pid watchdog).
        if (_engine is not null) _engine.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(3));
        _settingsStore?.Dispose();
        _log?.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(2));
        base.OnExit(e);
    }

    private void HookUnhandledExceptions(IAppLog log)
    {
        DispatcherUnhandledException += (_, args) =>
        {
            log.Error("app", $"Unhandled UI exception: {args.Exception}");
            MessageBox.Show(args.Exception.Message, "Live Core Studio — unexpected error", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;  // keep a live production running; the error is logged
        };
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            log.Error("app", $"Unobserved task exception: {args.Exception.GetBaseException()}");
            args.SetObserved();
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            log.Error("app", $"Fatal exception: {args.ExceptionObject}");
    }
}
