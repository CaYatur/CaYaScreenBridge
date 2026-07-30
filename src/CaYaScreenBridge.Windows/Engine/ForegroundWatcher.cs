using System.Diagnostics;
using CaYaScreenBridge.Core.Algorithm;
using CaYaScreenBridge.Core.Config;
using CaYaScreenBridge.Core.Diagnostics;
using CaYaScreenBridge.Windows.Native;

namespace CaYaScreenBridge.Windows.Engine;

/// <summary>
/// Watches what is in the foreground and publishes an immutable <see cref="ForegroundState"/> that
/// the hook callback can read without blocking.
///
/// All the expensive work (opening processes, enumerating running services) happens here on a timer,
/// never on the input path. The hook only ever reads one volatile reference.
/// </summary>
public sealed class ForegroundWatcher : IDisposable
{
    private static readonly TimeSpan ForegroundInterval = TimeSpan.FromMilliseconds(400);
    private static readonly TimeSpan AntiCheatInterval = TimeSpan.FromSeconds(6);

    private readonly ILogSink _log;
    private readonly Timer _timer;
    private readonly Dictionary<uint, string> _processNameCache = new();

    private volatile ForegroundState _state = ForegroundState.Unknown;
    private GameSettings _gameSettings = new();
    private long _lastAntiCheatCheck;
    private bool _antiCheatRunning;
    private string _lastReported = string.Empty;
    private bool _disposed;

    public ForegroundWatcher(ILogSink log)
    {
        _log = log;
        _timer = new Timer(_ => Poll(), null, ForegroundInterval, ForegroundInterval);
    }

    public ForegroundState State => _state;

    public event Action<ForegroundState>? StateChanged;

    public void UpdateSettings(GameSettings settings) => _gameSettings = settings;

    /// <summary>Re-evaluates immediately, e.g. right after a display change.</summary>
    public void Refresh() => Poll();

    private void Poll()
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            nint foreground = Win32.GetForegroundWindow();
            string processName = ResolveProcessName(foreground);
            ForegroundKind kind = ClassifyForeground(foreground);
            bool antiCheat = CheckAntiCheat();

            var state = new ForegroundState(processName, kind, IsElevatedWindow(foreground), antiCheat);

            if (!state.Equals(_state))
            {
                _state = state;

                string summary = $"{(processName.Length == 0 ? "?" : processName)}/{kind}" +
                                 (antiCheat ? "/anti-cheat" : string.Empty);

                if (summary != _lastReported)
                {
                    _lastReported = summary;
                    _log.Debug("Foreground", summary);
                }

                StateChanged?.Invoke(state);
            }
        }
        catch (Exception ex)
        {
            _log.Warn("Foreground", $"Poll failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Distinguishes exclusive full screen from borderless full screen from an ordinary window.
    ///
    /// The shell notification state is the authoritative signal for Direct3D exclusive mode, but it
    /// is also raised for presentations and by some overlays, so a geometric check backs it up: a
    /// window that exactly covers one monitor and has no caption or resize frame is borderless full
    /// screen, which needs different handling from a real exclusive swap chain.
    /// </summary>
    private ForegroundKind ClassifyForeground(nint hwnd)
    {
        if (hwnd == 0)
        {
            return ForegroundKind.Normal;
        }

        if (Win32.SHQueryUserNotificationState(out int notificationState) == 0 &&
            notificationState is Win32.QUNS_RUNNING_D3D_FULL_SCREEN or Win32.QUNS_PRESENTATION_MODE)
        {
            return ForegroundKind.ExclusiveFullScreen;
        }

        if (!Win32.GetWindowRect(hwnd, out RECT windowRect))
        {
            return ForegroundKind.Normal;
        }

        nint monitor = Win32.MonitorFromWindow(hwnd, Win32.MONITOR_DEFAULTTONEAREST);
        var info = new MONITORINFOEXW
        {
            cbSize = System.Runtime.InteropServices.Marshal.SizeOf<MONITORINFOEXW>(),
            szDevice = string.Empty,
        };

        if (!Win32.GetMonitorInfoW(monitor, ref info))
        {
            return ForegroundKind.Normal;
        }

        RECT monitorRect = info.rcMonitor;

        // A couple of pixels of slack: some engines size their window from a rounded client area.
        const int Tolerance = 2;
        bool coversMonitor =
            Math.Abs(windowRect.Left - monitorRect.Left) <= Tolerance &&
            Math.Abs(windowRect.Top - monitorRect.Top) <= Tolerance &&
            Math.Abs(windowRect.Right - monitorRect.Right) <= Tolerance &&
            Math.Abs(windowRect.Bottom - monitorRect.Bottom) <= Tolerance;

        if (!coversMonitor)
        {
            return ForegroundKind.Normal;
        }

        long style = Win32.GetWindowStyle(hwnd, Win32.GWL_STYLE);
        bool framed = (style & Win32.WS_CAPTION) == Win32.WS_CAPTION;

        return framed ? ForegroundKind.Normal : ForegroundKind.BorderlessFullScreen;
    }

    private bool CheckAntiCheat()
    {
        if (!_gameSettings.PauseForAntiCheat)
        {
            return false;
        }

        long now = Environment.TickCount64;
        if (now - _lastAntiCheatCheck < AntiCheatInterval.TotalMilliseconds)
        {
            return _antiCheatRunning;
        }

        _lastAntiCheatCheck = now;

        bool found = false;
        foreach (string name in _gameSettings.AntiCheatProcesses)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            try
            {
                Process[] matches = Process.GetProcessesByName(name);
                foreach (Process process in matches)
                {
                    process.Dispose();
                }

                if (matches.Length > 0)
                {
                    found = true;
                    break;
                }
            }
            catch
            {
                // Enumeration can fail transiently while a process is exiting; treat as not found.
            }
        }

        if (found != _antiCheatRunning)
        {
            _antiCheatRunning = found;
            _log.Info("Foreground", found
                ? "An anti-cheat service is running; cursor correction is suspended."
                : "The anti-cheat service is gone; cursor correction resumes.");
        }

        return found;
    }

    private string ResolveProcessName(nint hwnd)
    {
        if (hwnd == 0)
        {
            return string.Empty;
        }

        Win32.GetWindowThreadProcessId(hwnd, out uint processId);
        if (processId == 0)
        {
            return string.Empty;
        }

        lock (_processNameCache)
        {
            if (_processNameCache.TryGetValue(processId, out string? cached))
            {
                return cached;
            }
        }

        string name = string.Empty;
        try
        {
            using Process process = Process.GetProcessById((int)processId);
            name = process.ProcessName;
        }
        catch
        {
            // Access denied for an elevated or protected process. The empty name simply means no
            // per application rule can match, which is the safe default.
        }

        lock (_processNameCache)
        {
            // Process ids are recycled, so the cache is bounded and cleared rather than grown.
            if (_processNameCache.Count > 256)
            {
                _processNameCache.Clear();
            }

            _processNameCache[processId] = name;
        }

        return name;
    }

    /// <summary>
    /// True when the foreground window belongs to a process this one cannot touch. Used to explain
    /// in the UI why corrections appear to stop over, say, Task Manager when running unelevated.
    /// </summary>
    private static bool IsElevatedWindow(nint hwnd)
    {
        if (hwnd == 0)
        {
            return false;
        }

        Win32.GetWindowThreadProcessId(hwnd, out uint processId);
        if (processId == 0)
        {
            return false;
        }

        try
        {
            using Process process = Process.GetProcessById((int)processId);
            _ = process.MainModule;
            return false;
        }
        catch
        {
            return true;
        }
    }

    public void Dispose()
    {
        _disposed = true;
        _timer.Dispose();
    }
}
