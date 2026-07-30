using System.Diagnostics;
using CaYaScreenBridge.Core.Algorithm;
using CaYaScreenBridge.Core.Config;
using CaYaScreenBridge.Core.Diagnostics;
using CaYaScreenBridge.Core.Geometry;
using CaYaScreenBridge.Core.Model;
using CaYaScreenBridge.Windows.Native;

namespace CaYaScreenBridge.Windows.Engine;

/// <summary>
/// Keeps a window the same real world size while it is dragged between displays of different
/// scaling, with the point the user grabbed staying under the pointer.
///
/// Windows already resizes a window when it changes monitor, but it does so after the move has
/// completed and it anchors on a rectangle of its own choosing, which is why a window dragged across
/// a scaling boundary appears to jump out from under the cursor. Solving from the physical size
/// captured at the start of the drag removes both problems.
///
/// Two details make this reliable in practice:
///
/// * The window is re-applied a short while after each boundary crossing. A DPI aware application
///   receives WM_DPICHANGED after our SetWindowPos and will resize itself to Windows' suggestion; the
///   delayed second pass puts it back.
/// * Every SetWindowPos is issued off the input thread. Calling into another process' window from a
///   WH_MOUSE_LL callback can block for as long as that process takes to answer, which is exactly
///   how an application gets its hook evicted by Windows.
/// </summary>
public sealed class WindowDragTracker : IDisposable
{
    /// <summary>How long to wait before correcting the application's own DPI change response.</summary>
    private const int SettleDelayMs = 140;

    private readonly ILogSink _log;
    private readonly object _stateLock = new();

    private DragSettings _settings = new();
    private DragState? _drag;

    // Candidate captured on button down, promoted to a real drag once the window actually moves.
    // This is the fallback for applications that move their own window instead of letting
    // DefWindowProc run the modal move loop, which never raises EVENT_SYSTEM_MOVESIZESTART.
    private nint _candidateWindow;
    private RectD _candidateRect;
    private Vec2 _candidateCursor;
    private bool _buttonDown;

    private int _applyScheduled;
    private RectD _pendingRect;
    private nint _pendingWindow;
    private bool _disposed;

    public WindowDragTracker(ILogSink log) => _log = log;

    public bool IsDragging => _drag is not null;

    public string? DraggingProcess => _drag?.ProcessName;

    public void UpdateSettings(DragSettings settings) => _settings = settings;

    public void Reset()
    {
        lock (_stateLock)
        {
            _drag = null;
            _candidateWindow = 0;
            _buttonDown = false;
        }
    }

    // -------------------------------------------------------------------------------------------
    // Input events
    // -------------------------------------------------------------------------------------------

    public void OnButtonDown(Vec2 cursorPixel, ZoneLayout layout)
    {
        if (_settings.Mode == DragScalingMode.Off)
        {
            return;
        }

        lock (_stateLock)
        {
            _buttonDown = true;
            _candidateWindow = 0;

            nint hwnd = ResolveRootWindow(cursorPixel);
            if (hwnd == 0 || !IsEligible(hwnd))
            {
                return;
            }

            if (!TryGetWindowRect(hwnd, out RectD rect))
            {
                return;
            }

            _candidateWindow = hwnd;
            _candidateRect = rect;
            _candidateCursor = cursorPixel;

            _ = layout;
        }
    }

    public void OnButtonUp(Vec2 cursorPixel, ZoneLayout layout)
    {
        DragState? finished;

        lock (_stateLock)
        {
            _buttonDown = false;
            _candidateWindow = 0;
            finished = _drag;
            _drag = null;
        }

        if (finished is null)
        {
            return;
        }

        FinishDrag(finished, cursorPixel, layout);
    }

    /// <summary>Raised by the WinEvent hook when Windows starts its own move/size loop.</summary>
    public void OnMoveSizeStart(nint hwnd, Vec2 cursorPixel, ZoneLayout layout)
    {
        if (_settings.Mode == DragScalingMode.Off || !IsEligible(hwnd))
        {
            return;
        }

        if (!TryGetWindowRect(hwnd, out RectD rect))
        {
            return;
        }

        BeginDrag(hwnd, rect, cursorPixel, layout);
    }

    public void OnMoveSizeEnd(nint hwnd, Vec2 cursorPixel, ZoneLayout layout)
    {
        DragState? finished;

        lock (_stateLock)
        {
            if (_drag is null || _drag.WindowHandle != hwnd)
            {
                return;
            }

            finished = _drag;
            _drag = null;
            _candidateWindow = 0;
        }

        FinishDrag(finished, cursorPixel, layout);
    }

    /// <summary>
    /// Called for every corrected cursor movement. Promotes a candidate into a real drag, and issues
    /// the live rescale when the window crosses onto a display with a different scale.
    /// </summary>
    public void OnCursorMoved(Vec2 cursorPixel, DisplayZone? zone, ZoneLayout layout, bool scalingAllowed)
    {
        if (zone is null || _settings.Mode == DragScalingMode.Off)
        {
            return;
        }

        DragState? drag;

        lock (_stateLock)
        {
            if (_drag is null && _buttonDown && _candidateWindow != 0)
            {
                TryPromoteCandidate(cursorPixel, layout);
            }

            drag = _drag;
        }

        if (drag is null || !scalingAllowed || _settings.Mode != DragScalingMode.Live)
        {
            return;
        }

        if (drag.CurrentZoneId == zone.StableId)
        {
            return;
        }

        drag.CurrentZoneId = zone.StableId;

        if (!drag.Resizable)
        {
            return;
        }

        RectD target = WindowDragSolver.SolveTargetRect(drag, zone, cursorPixel, _settings.PreserveGrabPoint);
        long now = Environment.TickCount64;

        if (!WindowDragSolver.ShouldApply(drag, target, now, _settings.LiveThrottleMs))
        {
            return;
        }

        drag.LastAppliedMs = now;
        drag.LastAppliedRect = target;

        ScheduleApply(drag.WindowHandle, target, reapplyAfterSettle: true);
    }

    // -------------------------------------------------------------------------------------------
    // Internals
    // -------------------------------------------------------------------------------------------

    private void TryPromoteCandidate(Vec2 cursorPixel, ZoneLayout layout)
    {
        nint hwnd = _candidateWindow;
        if (!TryGetWindowRect(hwnd, out RectD rect))
        {
            _candidateWindow = 0;
            return;
        }

        // Only a real move counts. A click inside a window must not start a drag.
        if (Math.Abs(rect.X - _candidateRect.X) < 2 && Math.Abs(rect.Y - _candidateRect.Y) < 2)
        {
            return;
        }

        BeginDragLocked(hwnd, _candidateRect, _candidateCursor, layout);
        _candidateWindow = 0;
    }

    private void BeginDrag(nint hwnd, RectD rect, Vec2 cursorPixel, ZoneLayout layout)
    {
        lock (_stateLock)
        {
            BeginDragLocked(hwnd, rect, cursorPixel, layout);
        }
    }

    private void BeginDragLocked(nint hwnd, RectD rect, Vec2 cursorPixel, ZoneLayout layout)
    {
        DisplayZone? zone = layout.FindByPixel(new Vec2(rect.Center.X, rect.Top + 1))
                            ?? layout.FindByPixel(cursorPixel)
                            ?? layout.NearestByPixel(cursorPixel);

        if (zone is null)
        {
            return;
        }

        bool resizable = IsResizable(hwnd) && !(Win32.IsZoomed(hwnd) && _settings.SkipMaximisedWindows);

        _drag = new DragState
        {
            WindowHandle = hwnd,
            ProcessName = ResolveProcessName(hwnd),
            StartRect = rect,
            PhysicalSizeMm = WindowDragSolver.ComputePhysicalSize(rect, zone),
            GrabFraction = WindowDragSolver.ComputeGrabFraction(rect, cursorPixel),
            StartZoneId = zone.StableId,
            Resizable = resizable,
            CurrentZoneId = zone.StableId,
            LastAppliedRect = rect,
        };

        _log.Debug(
            "Drag",
            $"Tracking '{_drag.ProcessName}' {rect} " +
            $"({_drag.PhysicalSizeMm.X:0.#}x{_drag.PhysicalSizeMm.Y:0.#} mm) from {zone.DisplayName}.");
    }

    private void FinishDrag(DragState drag, Vec2 cursorPixel, ZoneLayout layout)
    {
        if (!drag.Resizable || _settings.Mode == DragScalingMode.Off)
        {
            return;
        }

        DisplayZone? zone = layout.FindByPixel(cursorPixel) ?? layout.NearestByPixel(cursorPixel);
        if (zone is null || zone.StableId == drag.StartZoneId)
        {
            return;
        }

        RectD target = WindowDragSolver.SolveTargetRect(drag, zone, cursorPixel, _settings.PreserveGrabPoint);
        target = WindowDragSolver.KeepReachable(target, layout);

        ScheduleApply(drag.WindowHandle, target, reapplyAfterSettle: true);

        _log.Debug("Drag", $"Settled '{drag.ProcessName}' onto {zone.DisplayName} at {target}.");
    }

    /// <summary>
    /// Queues a SetWindowPos off the input thread, coalescing bursts so only the most recent
    /// rectangle is ever applied.
    /// </summary>
    private void ScheduleApply(nint hwnd, RectD rect, bool reapplyAfterSettle)
    {
        _pendingWindow = hwnd;
        _pendingRect = rect;

        if (Interlocked.Exchange(ref _applyScheduled, 1) != 0)
        {
            return;
        }

        ThreadPool.UnsafeQueueUserWorkItem(
            static state =>
            {
                (WindowDragTracker tracker, bool settle) = state;
                tracker.RunApply(settle);
            },
            (this, reapplyAfterSettle),
            preferLocal: false);
    }

    private void RunApply(bool reapplyAfterSettle)
    {
        try
        {
            nint hwnd = _pendingWindow;
            RectD rect = _pendingRect;
            Interlocked.Exchange(ref _applyScheduled, 0);

            if (!Apply(hwnd, rect) || !reapplyAfterSettle || _disposed)
            {
                return;
            }

            // The application will have answered WM_DPICHANGED by now with a size of its own
            // choosing; put our rectangle back so the physical size actually holds.
            Task.Delay(SettleDelayMs).ContinueWith(
                _ =>
                {
                    if (!_disposed)
                    {
                        Apply(hwnd, rect);
                    }
                },
                TaskScheduler.Default);
        }
        catch (Exception ex)
        {
            Interlocked.Exchange(ref _applyScheduled, 0);
            _log.Warn("Drag", $"Failed to apply the window rectangle: {ex.Message}");
        }
    }

    private bool Apply(nint hwnd, RectD rect)
    {
        if (hwnd == 0 || !Win32.IsWindow(hwnd))
        {
            return false;
        }

        bool ok = Win32.SetWindowPos(
            hwnd,
            0,
            (int)Math.Round(rect.X),
            (int)Math.Round(rect.Y),
            (int)Math.Round(rect.Width),
            (int)Math.Round(rect.Height),
            Win32.SWP_NOZORDER | Win32.SWP_NOACTIVATE | Win32.SWP_NOOWNERZORDER | Win32.SWP_ASYNCWINDOWPOS);

        if (!ok)
        {
            _log.Debug("Drag", $"SetWindowPos was rejected for 0x{hwnd:X}.");
        }

        return ok;
    }

    // -------------------------------------------------------------------------------------------
    // Window inspection
    // -------------------------------------------------------------------------------------------

    private nint ResolveRootWindow(Vec2 cursorPixel)
    {
        var point = new POINT { X = (int)Math.Round(cursorPixel.X), Y = (int)Math.Round(cursorPixel.Y) };
        nint hwnd = Win32.WindowFromPoint(point);
        return hwnd == 0 ? 0 : Win32.GetAncestor(hwnd, Win32.GA_ROOT);
    }

    private bool IsEligible(nint hwnd)
    {
        if (hwnd == 0 || !Win32.IsWindow(hwnd) || !Win32.IsWindowVisible(hwnd) || Win32.IsIconic(hwnd))
        {
            return false;
        }

        long style = Win32.GetWindowStyle(hwnd, Win32.GWL_STYLE);
        if ((style & Win32.WS_CHILD) != 0)
        {
            return false;
        }

        long exStyle = Win32.GetWindowStyle(hwnd, Win32.GWL_EXSTYLE);
        if ((exStyle & Win32.WS_EX_TOOLWINDOW) != 0)
        {
            return false;
        }

        if (_settings.SkipMaximisedWindows && Win32.IsZoomed(hwnd))
        {
            return false;
        }

        if (_settings.SkipDpiUnawareWindows && IsDpiUnaware(hwnd))
        {
            return false;
        }

        return true;
    }

    private static bool IsResizable(nint hwnd) =>
        (Win32.GetWindowStyle(hwnd, Win32.GWL_STYLE) & Win32.WS_THICKFRAME) != 0;

    /// <summary>
    /// Windows bitmap stretches windows that are not per-monitor aware. Resizing one of those from
    /// the outside produces a blurry, mispositioned result, so they are left alone by default.
    /// </summary>
    private static bool IsDpiUnaware(nint hwnd)
    {
        try
        {
            nint context = Win32.GetWindowDpiAwarenessContext(hwnd);
            if (context == 0)
            {
                return false;
            }

            return Win32.AreDpiAwarenessContextsEqual(context, Win32.DPI_AWARENESS_CONTEXT_UNAWARE) ||
                   Win32.AreDpiAwarenessContextsEqual(context, Win32.DPI_AWARENESS_CONTEXT_UNAWARE_GDISCALED);
        }
        catch (EntryPointNotFoundException)
        {
            return false;
        }
    }

    private bool TryGetWindowRect(nint hwnd, out RectD rect)
    {
        rect = RectD.Empty;

        if (!Win32.GetWindowRect(hwnd, out RECT native))
        {
            return false;
        }

        rect = RectD.FromEdges(native.Left, native.Top, native.Right, native.Bottom);
        return rect.Width > 1 && rect.Height > 1;
    }

    private static string ResolveProcessName(nint hwnd)
    {
        try
        {
            Win32.GetWindowThreadProcessId(hwnd, out uint processId);
            if (processId == 0)
            {
                return string.Empty;
            }

            using Process process = Process.GetProcessById((int)processId);
            return process.ProcessName;
        }
        catch
        {
            return string.Empty;
        }
    }

    public void Dispose()
    {
        _disposed = true;
        Reset();
    }
}
