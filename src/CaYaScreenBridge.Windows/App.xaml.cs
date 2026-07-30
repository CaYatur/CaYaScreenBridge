using System.IO;
using System.Windows;
using System.Windows.Threading;
using CaYaScreenBridge.Core.Config;
using CaYaScreenBridge.Core.Diagnostics;
using CaYaScreenBridge.Windows.Engine;
using CaYaScreenBridge.Windows.Services;
using CaYaScreenBridge.Windows.Ui;

// Path is aliased because a WPF project can also have System.Windows.Shapes.Path in scope.
using Path = System.IO.Path;

namespace CaYaScreenBridge.Windows;

public partial class App : Application
{
    private SingleInstance? _instance;
    private RingLog? _memoryLog;
    private FileLogSink? _fileLog;
    private ILogSink _log = NullLogSink.Instance;
    private ConfigStore? _store;
    private AppConfig _config = new();
    private BridgeEngine? _engine;
    private StartupManager? _startup;
    private TrayIcon? _tray;
    private MainWindow? _window;
    private MainViewModel? _viewModel;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        bool background = e.Args.Any(a =>
            a.Equals("--background", StringComparison.OrdinalIgnoreCase) ||
            a.Equals("--tray", StringComparison.OrdinalIgnoreCase) ||
            a.Equals("-b", StringComparison.OrdinalIgnoreCase));

        _instance = SingleInstance.Acquire();

        if (!_instance.IsFirstInstance)
        {
            // A second launch is almost always the user double clicking the shortcut while the
            // application is already in the tray. Surface the existing window instead of refusing.
            _instance.RequestShow();
            _instance.Dispose();
            Shutdown(0);
            return;
        }

        SetUpLogging();
        SetUpGlobalErrorHandling();

        _store = new ConfigStore(ConfigStore.DefaultDirectory, _log);
        _config = _store.Load();

        Loc.Apply(_config.General.Language);
        ApplyLogLevel();

        _startup = new StartupManager(_log);
        _startup.RepairIfNeeded(_config.General.StartWithWindows, _config.General.StartElevated);
        _startup.ApplyHookTimeout(_config.General.RaiseHookTimeout);

        _engine = new BridgeEngine(_log);
        _engine.Start(_config);

        _viewModel = new MainViewModel(_engine, _store, _startup, _memoryLog!, _fileLog!);

        if (_config.General.ShowTrayIcon)
        {
            SetUpTray();
        }

        _instance.ShowRequested += () => Dispatcher.BeginInvoke(ShowMainWindow);
        _instance.ListenForShowRequests();

        if (!background && !_config.General.StartMinimised)
        {
            ShowMainWindow();
        }

        _log.Info("App", $"Ready ({(background ? "background" : "foreground")} start).");
    }

    private void SetUpLogging()
    {
        _memoryLog = new RingLog();
        _fileLog = new FileLogSink(Path.Combine(ConfigStore.DefaultDirectory, "logs"));
        _log = new CompositeLogSink(_memoryLog, _fileLog);
    }

    private void ApplyLogLevel()
    {
        LogLevel level = _config.General.VerboseLogging ? LogLevel.Debug : LogLevel.Info;

        if (_memoryLog is not null)
        {
            _memoryLog.MinimumLevel = level;
        }

        if (_fileLog is not null)
        {
            _fileLog.MinimumLevel = level;
        }
    }

    /// <summary>
    /// A background utility that vanishes on an unhandled exception is worse than one that logs the
    /// failure and carries on, so UI level exceptions are contained rather than fatal.
    /// </summary>
    private void SetUpGlobalErrorHandling()
    {
        DispatcherUnhandledException += (_, args) =>
        {
            _log.Error("App", $"Unhandled UI exception: {args.Exception}");
            args.Handled = true;
        };

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            _log.Error("App", $"Unhandled exception: {args.ExceptionObject}");
            _fileLog?.Flush();
        };

        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            _log.Warn("App", $"Unobserved task exception: {args.Exception.Message}");
            args.SetObserved();
        };
    }

    private void SetUpTray()
    {
        _tray = new TrayIcon(_log);
        _tray.ShowRequested += ShowMainWindow;
        _tray.ExitRequested += () => Dispatcher.BeginInvoke(() => ExitApplication());
        _tray.RebuildRequested += () => _engine?.RebuildLayout("tray");
        _tray.ToggleRequested += ToggleCorrection;

        _engine!.StatusChanged += status =>
            Dispatcher.BeginInvoke(() => _tray?.UpdateState(status.CorrectingCursor));

        _tray.UpdateState(_engine.Status.CorrectingCursor);
    }

    private void ToggleCorrection()
    {
        if (_engine is null || _viewModel is null)
        {
            return;
        }

        Dispatcher.BeginInvoke(() =>
        {
            _viewModel.MasterEnabled = !_viewModel.MasterEnabled;
            _viewModel.Persist();
        });
    }

    private void ShowMainWindow()
    {
        if (_viewModel is null)
        {
            return;
        }

        _window ??= new MainWindow(_viewModel);

        if (!_window.IsVisible)
        {
            _window.Show();
        }

        if (_window.WindowState == WindowState.Minimized)
        {
            _window.WindowState = WindowState.Normal;
        }

        _window.Activate();
        _window.Topmost = true;
        _window.Topmost = false;
    }

    private void ExitApplication()
    {
        if (_window is not null)
        {
            _window.AllowRealClose = true;
            _window.Close();
        }

        Shutdown(0);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _viewModel?.Detach();
        _tray?.Dispose();
        _engine?.Dispose();
        _fileLog?.Dispose();
        _instance?.Dispose();

        base.OnExit(e);
    }
}
