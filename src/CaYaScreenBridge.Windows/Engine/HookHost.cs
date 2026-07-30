using System.Runtime.InteropServices;
using CaYaScreenBridge.Core.Diagnostics;
using CaYaScreenBridge.Windows.Native;

namespace CaYaScreenBridge.Windows.Engine;

public readonly struct MouseHookEvent
{
    public required int X { get; init; }

    public required int Y { get; init; }

    public required uint Message { get; init; }

    public required uint Flags { get; init; }

    public required uint TimeMs { get; init; }

    public bool Injected => (Flags & (Win32.LLMHF_INJECTED | Win32.LLMHF_LOWER_IL_INJECTED)) != 0;
}

public interface IHookListener
{
    /// <summary>Returns true to swallow the event so Windows does not also apply it.</summary>
    bool OnMouseMove(in MouseHookEvent e);

    void OnMouseButton(in MouseHookEvent e);

    /// <summary>Raw HID movement, in device units, before pointer acceleration.</summary>
    void OnRawMouse(int dx, int dy);

    void OnWindowEvent(uint eventType, nint hwnd);
}

/// <summary>
/// Owns the input plumbing: a dedicated high priority thread running its own message pump, with the
/// low level mouse hook, a raw input sink and the move/size WinEvent hooks attached to it.
///
/// It runs on its own thread on purpose. A WH_MOUSE_LL callback is dispatched on the thread that
/// installed the hook, and Windows drops any hook whose callback overruns LowLevelHooksTimeout. If
/// the hook lived on the UI thread, a slow XAML layout pass or a modal dialog would be enough to get
/// the application silently unhooked, which is exactly the "it just stops working" failure this
/// design has to avoid.
/// </summary>
public sealed class HookHost : IDisposable
{
    private readonly ILogSink _log;
    private readonly IHookListener _listener;
    private readonly ManualResetEventSlim _ready = new(false);
    private readonly object _lifecycleLock = new();

    // These delegates are handed to native code. They must stay reachable for as long as the hooks
    // are installed; letting the GC collect them produces a hard crash inside user32.
    private readonly HookProc _mouseProc;
    private readonly WinEventProc _winEventProc;
    private readonly WndProc _wndProc;

    private Thread? _thread;
    private nint _hwnd;
    private nint _mouseHook;
    private nint _moveSizeHook;
    private nint _classNamePtr;
    private nint _rawInputBuffer;
    private uint _rawInputBufferSize;
    private volatile bool _stopping;

    private long _lastMouseEventTicks;
    private long _installedTicks;

    public HookHost(IHookListener listener, ILogSink log)
    {
        _listener = listener;
        _log = log;
        _mouseProc = MouseHookCallback;
        _winEventProc = WinEventCallback;
        _wndProc = WindowProcedure;
    }

    public bool IsRunning => _thread is { IsAlive: true } && _mouseHook != 0;

    /// <summary>Environment tick count of the most recent mouse event, for the watchdog.</summary>
    public long LastMouseEventTicks => Interlocked.Read(ref _lastMouseEventTicks);

    public long InstalledTicks => Interlocked.Read(ref _installedTicks);

    public bool RawInputAvailable { get; private set; }

    public void Start()
    {
        lock (_lifecycleLock)
        {
            if (_thread is { IsAlive: true })
            {
                return;
            }

            _stopping = false;
            _ready.Reset();

            _thread = new Thread(ThreadMain)
            {
                Name = "CaYaScreenBridge.Hook",
                IsBackground = true,
                Priority = ThreadPriority.Highest,
            };

            _thread.SetApartmentState(ApartmentState.STA);
            _thread.Start();

            if (!_ready.Wait(TimeSpan.FromSeconds(10)))
            {
                _log.Error("Hook", "The hook thread did not become ready within 10 seconds.");
            }
        }
    }

    public void Stop()
    {
        lock (_lifecycleLock)
        {
            Thread? thread = _thread;
            if (thread is null)
            {
                return;
            }

            _stopping = true;

            if (_hwnd != 0)
            {
                Win32.PostMessageW(_hwnd, Win32.WM_CLOSE, 0, 0);
            }

            if (!thread.Join(TimeSpan.FromSeconds(5)))
            {
                _log.Warn("Hook", "The hook thread did not shut down cleanly.");
            }

            _thread = null;
        }
    }

    /// <summary>
    /// Tears the hooks down and puts them back. The recovery path when the watchdog decides Windows
    /// has evicted us, and after a session unlock or a resume from sleep.
    /// </summary>
    public void Rearm()
    {
        nint hwnd = _hwnd;
        if (hwnd != 0)
        {
            Win32.PostMessageW(hwnd, Win32.WM_BRIDGE_REARM, 0, 0);
        }
        else
        {
            _log.Warn("Hook", "Cannot re-arm: the hook window does not exist. Restarting the thread.");
            Stop();
            Start();
        }
    }

    private void ThreadMain()
    {
        try
        {
            if (!CreateSinkWindow())
            {
                _ready.Set();
                return;
            }

            InstallHooks();
            RegisterRawInput();

            _ready.Set();
            PumpMessages();
        }
        catch (Exception ex)
        {
            _log.Error("Hook", $"The hook thread stopped unexpectedly: {ex}");
            _ready.Set();
        }
        finally
        {
            RemoveHooks();
            DestroySinkWindow();
        }
    }

    private void PumpMessages()
    {
        while (!_stopping)
        {
            int result = Win32.GetMessageW(out MSG msg, 0, 0, 0);

            if (result == 0)
            {
                break;
            }

            if (result == -1)
            {
                _log.Error("Hook", $"GetMessage failed ({Marshal.GetLastWin32Error()}); stopping the pump.");
                break;
            }

            Win32.TranslateMessage(ref msg);
            Win32.DispatchMessageW(ref msg);
        }
    }

    // ---------------------------------------------------------------------------------------
    // Window
    // ---------------------------------------------------------------------------------------

    private bool CreateSinkWindow()
    {
        nint instance = Win32.GetModuleHandleW(null);
        string className = "CaYaScreenBridge.HookSink." + Environment.ProcessId.ToString("X");
        _classNamePtr = Marshal.StringToHGlobalUni(className);

        var wndClass = new WNDCLASSEXW
        {
            cbSize = Marshal.SizeOf<WNDCLASSEXW>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
            hInstance = instance,
            lpszClassName = _classNamePtr,
        };

        if (Win32.RegisterClassExW(ref wndClass) == 0)
        {
            int error = Marshal.GetLastWin32Error();

            // 1410 is ERROR_CLASS_ALREADY_EXISTS, which is harmless on a restart of the thread.
            if (error != 1410)
            {
                _log.Error("Hook", $"RegisterClassEx failed ({error}).");
                return false;
            }
        }

        _hwnd = Win32.CreateWindowExW(
            0,
            _classNamePtr,
            _classNamePtr,
            0,
            0,
            0,
            0,
            0,
            Win32.HWND_MESSAGE,
            0,
            instance,
            0);

        if (_hwnd == 0)
        {
            _log.Error("Hook", $"CreateWindowEx failed ({Marshal.GetLastWin32Error()}).");
            return false;
        }

        return true;
    }

    private void DestroySinkWindow()
    {
        if (_hwnd != 0)
        {
            Win32.DestroyWindow(_hwnd);
            _hwnd = 0;
        }

        if (_classNamePtr != 0)
        {
            Marshal.FreeHGlobal(_classNamePtr);
            _classNamePtr = 0;
        }

        if (_rawInputBuffer != 0)
        {
            Marshal.FreeHGlobal(_rawInputBuffer);
            _rawInputBuffer = 0;
            _rawInputBufferSize = 0;
        }
    }

    private nint WindowProcedure(nint hwnd, uint msg, nint wParam, nint lParam)
    {
        switch (msg)
        {
            case Win32.WM_INPUT:
                HandleRawInput(lParam);
                break;

            case Win32.WM_BRIDGE_REARM:
                RemoveHooks();
                InstallHooks();
                return 0;

            case Win32.WM_CLOSE:
                Win32.DestroyWindow(hwnd);
                return 0;

            case Win32.WM_DESTROY:
                Win32.PostQuitMessage(0);
                return 0;
        }

        return Win32.DefWindowProcW(hwnd, msg, wParam, lParam);
    }

    // ---------------------------------------------------------------------------------------
    // Hooks
    // ---------------------------------------------------------------------------------------

    private void InstallHooks()
    {
        nint instance = Win32.GetModuleHandleW(null);

        _mouseHook = Win32.SetWindowsHookExW(Win32.WH_MOUSE_LL, _mouseProc, instance, 0);
        if (_mouseHook == 0)
        {
            _log.Error("Hook", $"SetWindowsHookEx(WH_MOUSE_LL) failed ({Marshal.GetLastWin32Error()}).");
        }
        else
        {
            _log.Info("Hook", "Low level mouse hook installed.");
        }

        _moveSizeHook = Win32.SetWinEventHook(
            Win32.EVENT_SYSTEM_MOVESIZESTART,
            Win32.EVENT_SYSTEM_MOVESIZEEND,
            0,
            _winEventProc,
            0,
            0,
            Win32.WINEVENT_OUTOFCONTEXT | Win32.WINEVENT_SKIPOWNPROCESS);

        if (_moveSizeHook == 0)
        {
            _log.Warn("Hook", "Could not install the window move/size hook; drag correction will be limited.");
        }

        Interlocked.Exchange(ref _installedTicks, Environment.TickCount64);
        Interlocked.Exchange(ref _lastMouseEventTicks, Environment.TickCount64);
    }

    private void RemoveHooks()
    {
        if (_mouseHook != 0)
        {
            Win32.UnhookWindowsHookEx(_mouseHook);
            _mouseHook = 0;
        }

        if (_moveSizeHook != 0)
        {
            Win32.UnhookWinEvent(_moveSizeHook);
            _moveSizeHook = 0;
        }
    }

    private nint MouseHookCallback(int nCode, nint wParam, nint lParam)
    {
        if (nCode != Win32.HC_ACTION)
        {
            return Win32.CallNextHookEx(0, nCode, wParam, lParam);
        }

        try
        {
            MSLLHOOKSTRUCT data = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
            uint message = (uint)wParam;

            var e = new MouseHookEvent
            {
                X = data.pt.X,
                Y = data.pt.Y,
                Message = message,
                Flags = data.flags,
                TimeMs = data.time,
            };

            Interlocked.Exchange(ref _lastMouseEventTicks, Environment.TickCount64);

            if (message == Win32.WM_MOUSEMOVE)
            {
                if (_listener.OnMouseMove(in e))
                {
                    // Swallowing the move is what stops Windows from applying the uncorrected
                    // position on top of the corrected one, which would show up as a visible flicker.
                    return 1;
                }
            }
            else
            {
                _listener.OnMouseButton(in e);
            }
        }
        catch (Exception ex)
        {
            // An exception escaping into user32 would terminate the process. Swallow, log and let
            // the event through untouched: a missed correction is always better than a crash.
            _log.Error("Hook", $"Mouse hook callback failed: {ex.Message}");
        }

        return Win32.CallNextHookEx(0, nCode, wParam, lParam);
    }

    private void WinEventCallback(
        nint hWinEventHook,
        uint eventType,
        nint hwnd,
        int idObject,
        int idChild,
        uint idEventThread,
        uint dwmsEventTime)
    {
        if (idObject != Win32.OBJID_WINDOW || hwnd == 0)
        {
            return;
        }

        try
        {
            _listener.OnWindowEvent(eventType, hwnd);
        }
        catch (Exception ex)
        {
            _log.Error("Hook", $"Window event callback failed: {ex.Message}");
        }
    }

    // ---------------------------------------------------------------------------------------
    // Raw input
    // ---------------------------------------------------------------------------------------

    private void RegisterRawInput()
    {
        var devices = new[]
        {
            new RAWINPUTDEVICE
            {
                usUsagePage = Win32.HID_USAGE_PAGE_GENERIC,
                usUsage = Win32.HID_USAGE_GENERIC_MOUSE,

                // INPUTSINK keeps the deltas flowing while another application has focus, which is
                // the only situation that matters here.
                dwFlags = Win32.RIDEV_INPUTSINK,
                hwndTarget = _hwnd,
            },
        };

        RawInputAvailable = Win32.RegisterRawInputDevices(
            devices,
            (uint)devices.Length,
            (uint)Marshal.SizeOf<RAWINPUTDEVICE>());

        if (!RawInputAvailable)
        {
            _log.Warn(
                "Hook",
                $"Raw input registration failed ({Marshal.GetLastWin32Error()}); edge assisted " +
                "crossings will be unavailable.");
            return;
        }

        _rawInputBufferSize = (uint)Marshal.SizeOf<RAWINPUT>() + 32;
        _rawInputBuffer = Marshal.AllocHGlobal((int)_rawInputBufferSize);
        _log.Info("Hook", "Raw input sink registered.");
    }

    private void HandleRawInput(nint hRawInput)
    {
        if (_rawInputBuffer == 0)
        {
            return;
        }

        uint size = _rawInputBufferSize;
        uint written = Win32.GetRawInputData(
            hRawInput,
            Win32.RID_INPUT,
            _rawInputBuffer,
            ref size,
            (uint)Marshal.SizeOf<RAWINPUTHEADER>());

        if (written == unchecked((uint)-1) || written == 0)
        {
            return;
        }

        RAWINPUT input = Marshal.PtrToStructure<RAWINPUT>(_rawInputBuffer);
        if (input.header.dwType != Win32.RIM_TYPEMOUSE)
        {
            return;
        }

        // Absolute devices (tablets, some virtual machine pointers) report a position rather than a
        // delta; feeding that into the delta calibrator would poison it.
        if ((input.mouse.usFlags & Win32.MOUSE_MOVE_ABSOLUTE) != 0)
        {
            return;
        }

        if (input.mouse.lLastX == 0 && input.mouse.lLastY == 0)
        {
            return;
        }

        try
        {
            _listener.OnRawMouse(input.mouse.lLastX, input.mouse.lLastY);
        }
        catch (Exception ex)
        {
            _log.Error("Hook", $"Raw input handler failed: {ex.Message}");
        }
    }

    public void Dispose()
    {
        Stop();
        _ready.Dispose();
    }
}
