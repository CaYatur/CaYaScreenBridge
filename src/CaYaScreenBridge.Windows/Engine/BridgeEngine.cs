using CaYaScreenBridge.Core.Algorithm;
using CaYaScreenBridge.Core.Config;
using CaYaScreenBridge.Core.Diagnostics;
using CaYaScreenBridge.Core.Geometry;
using CaYaScreenBridge.Core.Model;
using CaYaScreenBridge.Windows.Native;
using Microsoft.Win32;

namespace CaYaScreenBridge.Windows.Engine;

public sealed record EngineStatus(
    bool Running,
    bool HookInstalled,
    bool RawInputActive,
    int DisplayCount,
    string ActiveProfileId,
    string PolicyReason,
    bool CorrectingCursor,
    bool ScalingWindows,
    long Crossings,
    long Recoveries,
    long HookRestarts)
{
    public static readonly EngineStatus Stopped =
        new(false, false, false, 0, string.Empty, "stopped", false, false, 0, 0, 0);
}

/// <summary>
/// Ties everything together: reads the display configuration, keeps the router's layout current,
/// routes every mouse movement, decides when to stand down, and puts itself back together when
/// Windows takes the hook away.
///
/// The supervision is the point. A tool like this is judged on whether it is still working three
/// weeks and forty sleep cycles later, so every external event that is known to break a low level
/// hook (session lock, fast user switch, resume from sleep, display topology change, the hook
/// timeout eviction) has an explicit recovery path rather than relying on the process being
/// restarted.
/// </summary>
public sealed class BridgeEngine : IHookListener, IDisposable
{
    private static readonly TimeSpan WatchdogInterval = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan DisplayDebounce = TimeSpan.FromMilliseconds(700);

    /// <summary>Silence longer than this while the cursor is visibly moving means the hook is gone.</summary>
    private const long HookSilenceThresholdMs = 4000;

    private readonly ILogSink _log;
    private readonly MonitorEnumerator _monitors;
    private readonly CursorRouter _router = new();
    private readonly WindowDragTracker _dragTracker;
    private readonly ForegroundWatcher _foreground;
    private readonly HookHost _hook;
    private readonly PointerSpeedManager _pointerSpeed;
    private readonly object _configLock = new();

    private readonly Timer _watchdog;
    private readonly Timer _displayDebounce;

    private AppConfig _config = new();
    private volatile PolicyDecision _policy = PolicyDecision.Full;
    private ZoneLayout _layout = ZoneLayout.Empty;
    private IReadOnlyList<DisplaySnapshot> _displays = Array.Empty<DisplaySnapshot>();
    private string _activeProfileId = string.Empty;

    private int _rawDeltaX;
    private int _rawDeltaY;

    private POINT _watchdogCursor;
    private long _hookRestarts;
    private bool _started;
    private bool _disposed;

    public BridgeEngine(ILogSink log)
    {
        _log = log;
        _monitors = new MonitorEnumerator(log);
        _dragTracker = new WindowDragTracker(log);
        _foreground = new ForegroundWatcher(log);
        _pointerSpeed = new PointerSpeedManager(log);
        _hook = new HookHost(this, log);

        _watchdog = new Timer(_ => RunWatchdog(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        _displayDebounce = new Timer(_ => RebuildLayout("display change"), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);

        _foreground.StateChanged += OnForegroundChanged;
    }

    public event Action<EngineStatus>? StatusChanged;

    public event Action<ZoneLayout>? LayoutChanged;

    public ZoneLayout Layout => _layout;

    public IReadOnlyList<DisplaySnapshot> Displays => _displays;

    public CursorRouter Router => _router;

    public AppConfig Config
    {
        get
        {
            lock (_configLock)
            {
                return _config;
            }
        }
    }

    public EngineStatus Status => new(
        _started,
        _hook.IsRunning,
        _hook.RawInputAvailable,
        _layout.Count,
        _activeProfileId,
        _policy.Reason,
        _policy.CorrectCursor,
        _policy.ScaleWindows,
        _router.CrossingCount,
        _router.RecoveryCount,
        Interlocked.Read(ref _hookRestarts));

    // -------------------------------------------------------------------------------------------
    // Lifecycle
    // -------------------------------------------------------------------------------------------

    public void Start(AppConfig config)
    {
        if (_started)
        {
            ApplyConfig(config);
            return;
        }

        _started = true;
        ApplyConfig(config);
        RebuildLayout("startup");

        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
        SystemEvents.SessionSwitch += OnSessionSwitch;
        SystemEvents.PowerModeChanged += OnPowerModeChanged;

        _hook.Start();
        _watchdog.Change(WatchdogInterval, WatchdogInterval);

        _log.Info("Engine", "Started.");
        RaiseStatus();
    }

    public void Stop()
    {
        if (!_started)
        {
            return;
        }

        _started = false;

        SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        SystemEvents.SessionSwitch -= OnSessionSwitch;
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;

        _watchdog.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        _hook.Stop();
        _pointerSpeed.Restore();
        _dragTracker.Reset();

        _log.Info("Engine", "Stopped.");
        RaiseStatus();
    }

    public void ApplyConfig(AppConfig config)
    {
        lock (_configLock)
        {
            _config = config;
        }

        _router.SetOptions(RouterOptions.FromSettings(config.Transition));
        _dragTracker.UpdateSettings(config.Drag);
        _foreground.UpdateSettings(config.Games);
        _pointerSpeed.SetEnabled(config.Transition.NormalisePointerSpeed);

        _policy = EnginePolicy.Evaluate(config, _foreground.State);
        _router.Invalidate();

        RaiseStatus();
    }

    /// <summary>
    /// Rebuilds the physical layout from the live display configuration and the saved calibration
    /// for the current arrangement.
    /// </summary>
    public void RebuildLayout(string reason)
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            IReadOnlyList<DisplaySnapshot> displays = _monitors.Enumerate();
            if (displays.Count == 0)
            {
                _log.Warn("Engine", $"Layout rebuild ({reason}) found no displays; keeping the previous layout.");
                return;
            }

            string configurationId = ZoneLayout.ComputeConfigurationId(displays);

            LayoutProfile profile;
            AppConfig config;
            lock (_configLock)
            {
                config = _config;
                profile = config.GetOrCreateProfile(configurationId, BuildProfileName(displays));
            }

            ZoneLayout layout = LayoutBuilder.Build(displays, profile);

            _displays = displays;
            _layout = layout;
            _activeProfileId = configurationId;

            _router.SetLayout(layout);
            _dragTracker.Reset();
            _pointerSpeed.SetLayout(layout);
            _foreground.Refresh();

            _log.Info(
                "Engine",
                $"Layout rebuilt ({reason}): {layout.Count} display(s), profile {configurationId}" +
                (layout.IsUniform ? ", uniform density (no correction needed)" : string.Empty) + ".");

            LayoutChanged?.Invoke(layout);
            RaiseStatus();
        }
        catch (Exception ex)
        {
            _log.Error("Engine", $"Layout rebuild failed ({reason}): {ex.Message}");
        }
    }

    private static string BuildProfileName(IReadOnlyList<DisplaySnapshot> displays)
    {
        IEnumerable<string> names = displays
            .OrderByDescending(d => d.IsPrimary)
            .Select(d => string.IsNullOrWhiteSpace(d.FriendlyName) ? d.DeviceName : d.FriendlyName);

        return string.Join(" + ", names);
    }

    // -------------------------------------------------------------------------------------------
    // Hook listener
    // -------------------------------------------------------------------------------------------

    bool IHookListener.OnMouseMove(in MouseHookEvent e)
    {
        PolicyDecision policy = _policy;
        if (!policy.CorrectCursor)
        {
            return false;
        }

        var position = new Vec2(e.X, e.Y);

        int rawX = Interlocked.Exchange(ref _rawDeltaX, 0);
        int rawY = Interlocked.Exchange(ref _rawDeltaY, 0);

        var sample = new MouseSample
        {
            Position = position,
            RawDelta = new Vec2(rawX, rawY),
            HasRawDelta = _hook.RawInputAvailable && (rawX != 0 || rawY != 0),
            TimestampMs = e.TimeMs,
            Injected = e.Injected,
        };

        RouterDecision decision = _router.Process(in sample);

        if (decision.Handled)
        {
            Win32.SetCursorPos((int)decision.TargetPixel.X, (int)decision.TargetPixel.Y);

            if (decision.Outcome == RouterOutcome.Crossed && decision.ToZone is not null)
            {
                _pointerSpeed.OnZoneChanged(decision.ToZone);
            }
        }

        DisplayZone? zone = decision.ToZone ?? _router.CurrentZone;
        Vec2 effective = decision.Handled ? decision.TargetPixel : position;
        _dragTracker.OnCursorMoved(effective, zone, _layout, policy.ScaleWindows);

        return decision.Handled;
    }

    void IHookListener.OnMouseButton(in MouseHookEvent e)
    {
        if (e.Injected)
        {
            return;
        }

        var position = new Vec2(e.X, e.Y);

        switch (e.Message)
        {
            case Win32.WM_LBUTTONDOWN:
                _dragTracker.OnButtonDown(position, _layout);
                break;

            case Win32.WM_LBUTTONUP:
                _dragTracker.OnButtonUp(position, _layout);
                break;
        }
    }

    void IHookListener.OnRawMouse(int dx, int dy)
    {
        Interlocked.Add(ref _rawDeltaX, dx);
        Interlocked.Add(ref _rawDeltaY, dy);
    }

    void IHookListener.OnWindowEvent(uint eventType, nint hwnd)
    {
        if (!_policy.ScaleWindows)
        {
            return;
        }

        if (!Win32.GetCursorPos(out POINT cursor))
        {
            return;
        }

        var position = new Vec2(cursor.X, cursor.Y);

        switch (eventType)
        {
            case Win32.EVENT_SYSTEM_MOVESIZESTART:
                _dragTracker.OnMoveSizeStart(hwnd, position, _layout);
                break;

            case Win32.EVENT_SYSTEM_MOVESIZEEND:
                _dragTracker.OnMoveSizeEnd(hwnd, position, _layout);
                break;
        }
    }

    // -------------------------------------------------------------------------------------------
    // Supervision
    // -------------------------------------------------------------------------------------------

    /// <summary>
    /// Detects a hook that Windows has quietly evicted.
    ///
    /// There is no API to ask whether a low level hook is still in the chain, and Windows removes a
    /// hook whose callback overruns LowLevelHooksTimeout too often without telling anyone. The one
    /// reliable signal is a contradiction: the cursor has visibly moved, yet no mouse event reached
    /// the callback. That can only mean the hook is gone.
    /// </summary>
    private void RunWatchdog()
    {
        if (_disposed || !_started)
        {
            return;
        }

        try
        {
            if (!_hook.IsRunning)
            {
                _log.Warn("Watchdog", "The hook is not installed; restarting the hook thread.");
                RestartHook();
                return;
            }

            if (!Win32.GetCursorPos(out POINT cursor))
            {
                return;
            }

            bool cursorMoved = cursor.X != _watchdogCursor.X || cursor.Y != _watchdogCursor.Y;
            _watchdogCursor = cursor;

            if (!cursorMoved)
            {
                return;
            }

            long silence = Environment.TickCount64 - _hook.LastMouseEventTicks;
            if (silence > HookSilenceThresholdMs)
            {
                _log.Warn(
                    "Watchdog",
                    $"The cursor moved but no mouse event arrived for {silence} ms; re-arming the hook.");
                RestartHook();
            }
        }
        catch (Exception ex)
        {
            _log.Error("Watchdog", $"Watchdog pass failed: {ex.Message}");
        }
    }

    private void RestartHook()
    {
        Interlocked.Increment(ref _hookRestarts);

        try
        {
            if (_hook.IsRunning)
            {
                _hook.Rearm();
            }
            else
            {
                _hook.Stop();
                _hook.Start();
            }

            _router.Invalidate();
            _dragTracker.Reset();
        }
        catch (Exception ex)
        {
            _log.Error("Watchdog", $"Could not restore the hook: {ex.Message}");
        }

        RaiseStatus();
    }

    private void OnForegroundChanged(ForegroundState state)
    {
        AppConfig config;
        lock (_configLock)
        {
            config = _config;
        }

        PolicyDecision previous = _policy;
        PolicyDecision current = EnginePolicy.Evaluate(config, state);
        _policy = current;

        if (previous.CorrectCursor != current.CorrectCursor)
        {
            // Coming back from a pause, the cursor may be anywhere; force a fresh sync so the first
            // corrected movement is not computed from a stale position.
            _router.Invalidate();

            _log.Info("Policy", current.CorrectCursor
                ? $"Correction resumed ({current.Reason})."
                : $"Correction paused ({current.Reason}).");
        }

        if (!current.ScaleWindows)
        {
            _dragTracker.Reset();
        }

        RaiseStatus();
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs e)
    {
        // Windows raises this several times while a topology change settles. Debouncing avoids
        // rebuilding the layout against an intermediate state that no longer exists a moment later.
        _displayDebounce.Change(DisplayDebounce, Timeout.InfiniteTimeSpan);
    }

    private void OnSessionSwitch(object? sender, SessionSwitchEventArgs e)
    {
        switch (e.Reason)
        {
            case SessionSwitchReason.SessionUnlock:
            case SessionSwitchReason.ConsoleConnect:
            case SessionSwitchReason.SessionLogon:
            case SessionSwitchReason.RemoteConnect:
                _log.Info("Engine", $"Session event ({e.Reason}); re-arming.");
                RestartHook();
                _displayDebounce.Change(DisplayDebounce, Timeout.InfiniteTimeSpan);
                break;

            case SessionSwitchReason.SessionLock:
            case SessionSwitchReason.ConsoleDisconnect:
                _dragTracker.Reset();
                _router.Invalidate();
                break;
        }
    }

    private void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode != PowerModes.Resume)
        {
            return;
        }

        _log.Info("Engine", "Resumed from sleep; re-arming and rebuilding the layout.");
        RestartHook();
        _displayDebounce.Change(DisplayDebounce, Timeout.InfiniteTimeSpan);
    }

    private void RaiseStatus()
    {
        try
        {
            StatusChanged?.Invoke(Status);
        }
        catch (Exception ex)
        {
            _log.Warn("Engine", $"A status listener threw: {ex.Message}");
        }
    }

    public void Dispose()
    {
        _disposed = true;
        Stop();

        _foreground.StateChanged -= OnForegroundChanged;
        _watchdog.Dispose();
        _displayDebounce.Dispose();
        _foreground.Dispose();
        _dragTracker.Dispose();
        _hook.Dispose();
    }
}
