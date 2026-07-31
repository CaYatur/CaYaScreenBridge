using System.Runtime.InteropServices;
using System.Text;

namespace CaYaScreenBridge.Windows.Native;

[StructLayout(LayoutKind.Sequential)]
internal struct POINT
{
    public int X;
    public int Y;
}

[StructLayout(LayoutKind.Sequential)]
internal struct RECT
{
    public int Left;
    public int Top;
    public int Right;
    public int Bottom;

    public readonly int Width => Right - Left;

    public readonly int Height => Bottom - Top;
}

[StructLayout(LayoutKind.Sequential)]
internal struct MSG
{
    public nint hwnd;
    public uint message;
    public nint wParam;
    public nint lParam;
    public uint time;
    public POINT pt;
}

[StructLayout(LayoutKind.Sequential)]
internal struct MSLLHOOKSTRUCT
{
    public POINT pt;
    public uint mouseData;
    public uint flags;
    public uint time;
    public nuint dwExtraInfo;
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal struct MONITORINFOEXW
{
    public int cbSize;
    public RECT rcMonitor;
    public RECT rcWork;
    public uint dwFlags;

    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
    public string szDevice;
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal struct DISPLAY_DEVICEW
{
    public int cb;

    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
    public string DeviceName;

    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
    public string DeviceString;

    public uint StateFlags;

    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
    public string DeviceID;

    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
    public string DeviceKey;
}

[StructLayout(LayoutKind.Sequential)]
internal struct RAWINPUTDEVICE
{
    public ushort usUsagePage;
    public ushort usUsage;
    public uint dwFlags;
    public nint hwndTarget;
}

[StructLayout(LayoutKind.Sequential)]
internal struct RAWINPUTHEADER
{
    public uint dwType;
    public uint dwSize;
    public nint hDevice;
    public nint wParam;
}

[StructLayout(LayoutKind.Sequential)]
internal struct RAWMOUSE
{
    public ushort usFlags;
    public uint ulButtons;
    public uint ulRawButtons;
    public int lLastX;
    public int lLastY;
    public uint ulExtraInformation;
}

[StructLayout(LayoutKind.Sequential)]
internal struct RAWINPUT
{
    public RAWINPUTHEADER header;
    public RAWMOUSE mouse;
}

[StructLayout(LayoutKind.Sequential)]
internal struct WNDCLASSEXW
{
    public int cbSize;
    public uint style;
    public nint lpfnWndProc;
    public int cbClsExtra;
    public int cbWndExtra;
    public nint hInstance;
    public nint hIcon;
    public nint hCursor;
    public nint hbrBackground;
    public nint lpszMenuName;
    public nint lpszClassName;
    public nint hIconSm;
}

[StructLayout(LayoutKind.Sequential)]
internal struct NOTIFYICONDATAW
{
    public int cbSize;
    public nint hWnd;
    public uint uID;
    public uint uFlags;
    public uint uCallbackMessage;
    public nint hIcon;

    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
    public string szTip;

    public uint dwState;
    public uint dwStateMask;

    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
    public string szInfo;

    public uint uVersionOrTimeout;

    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
    public string szInfoTitle;

    public uint dwInfoFlags;
    public Guid guidItem;
    public nint hBalloonIcon;
}

internal delegate nint HookProc(int nCode, nint wParam, nint lParam);

internal delegate bool MonitorEnumProc(nint hMonitor, nint hdc, ref RECT rect, nint data);

internal delegate void WinEventProc(
    nint hWinEventHook,
    uint eventType,
    nint hwnd,
    int idObject,
    int idChild,
    uint idEventThread,
    uint dwmsEventTime);

internal delegate nint WndProc(nint hwnd, uint msg, nint wParam, nint lParam);

internal static class Win32
{
    // ---- messages -------------------------------------------------------------------------
    public const uint WM_DESTROY = 0x0002;
    public const uint WM_CLOSE = 0x0010;
    public const uint WM_QUIT = 0x0012;
    public const uint WM_INPUT = 0x00FF;
    public const uint WM_DISPLAYCHANGE = 0x007E;
    public const uint WM_SETTINGCHANGE = 0x001A;
    public const uint WM_DPICHANGED = 0x02E0;
    public const uint WM_POWERBROADCAST = 0x0218;
    public const uint WM_WTSSESSION_CHANGE = 0x02B1;
    public const uint WM_APP = 0x8000;

    public const uint WM_MOUSEMOVE = 0x0200;
    public const uint WM_LBUTTONDOWN = 0x0201;
    public const uint WM_LBUTTONUP = 0x0202;
    public const uint WM_RBUTTONDOWN = 0x0204;
    public const uint WM_RBUTTONUP = 0x0205;
    public const uint WM_MBUTTONDOWN = 0x0207;
    public const uint WM_MBUTTONUP = 0x0208;
    public const uint WM_MOUSEWHEEL = 0x020A;

    /// <summary>Custom message used to ask the hook thread to run a queued action.</summary>
    public const uint WM_BRIDGE_INVOKE = WM_APP + 0x21;

    /// <summary>Custom message used to ask the hook thread to reinstall its hooks.</summary>
    public const uint WM_BRIDGE_REARM = WM_APP + 0x22;

    /// <summary>Tray icon callback message.</summary>
    public const uint WM_BRIDGE_TRAY = WM_APP + 0x23;

    // ---- hooks ----------------------------------------------------------------------------
    public const int WH_MOUSE_LL = 14;
    public const uint LLMHF_INJECTED = 0x00000001;
    public const uint LLMHF_LOWER_IL_INJECTED = 0x00000002;
    public const int HC_ACTION = 0;

    // ---- WinEvents ------------------------------------------------------------------------
    public const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
    public const uint EVENT_SYSTEM_MOVESIZESTART = 0x000A;
    public const uint EVENT_SYSTEM_MOVESIZEEND = 0x000B;
    public const uint EVENT_OBJECT_LOCATIONCHANGE = 0x800B;
    public const uint WINEVENT_OUTOFCONTEXT = 0x0000;
    public const uint WINEVENT_SKIPOWNPROCESS = 0x0002;
    public const int OBJID_WINDOW = 0;

    // ---- window styles --------------------------------------------------------------------
    public const int GWL_STYLE = -16;
    public const int GWL_EXSTYLE = -20;
    public const long WS_CAPTION = 0x00C00000;
    public const long WS_THICKFRAME = 0x00040000;
    public const long WS_CHILD = 0x40000000;
    public const long WS_MAXIMIZE = 0x01000000;
    public const long WS_POPUP = 0x80000000;
    public const long WS_EX_TOOLWINDOW = 0x00000080;
    public const long WS_EX_NOACTIVATE = 0x08000000;

    public const uint SWP_NOZORDER = 0x0004;
    public const uint SWP_NOACTIVATE = 0x0010;
    public const uint SWP_NOOWNERZORDER = 0x0200;
    public const uint SWP_ASYNCWINDOWPOS = 0x4000;
    public const uint SWP_NOSENDCHANGING = 0x0400;
    public const uint SWP_SHOWWINDOW = 0x0040;

    public const uint GA_ROOT = 2;

    // ---- raw input ------------------------------------------------------------------------
    public const ushort HID_USAGE_PAGE_GENERIC = 0x01;
    public const ushort HID_USAGE_GENERIC_MOUSE = 0x02;
    public const uint RIDEV_INPUTSINK = 0x00000100;
    public const uint RID_INPUT = 0x10000003;
    public const uint RIM_TYPEMOUSE = 0;
    public const ushort MOUSE_MOVE_RELATIVE = 0x00;
    public const ushort MOUSE_MOVE_ABSOLUTE = 0x01;

    // ---- monitors -------------------------------------------------------------------------
    public const uint MONITOR_DEFAULTTONEAREST = 2;
    public const uint MONITORINFOF_PRIMARY = 1;
    public const uint EDD_GET_DEVICE_INTERFACE_NAME = 0x00000001;
    public const uint DISPLAY_DEVICE_ATTACHED_TO_DESKTOP = 0x00000001;

    public const int MDT_EFFECTIVE_DPI = 0;
    public const int MDT_RAW_DPI = 1;

    // ---- shell / notification -------------------------------------------------------------
    public const int QUNS_NOT_PRESENT = 1;
    public const int QUNS_BUSY = 2;
    public const int QUNS_RUNNING_D3D_FULL_SCREEN = 3;
    public const int QUNS_PRESENTATION_MODE = 4;
    public const int QUNS_ACCEPTS_NOTIFICATIONS = 5;
    public const int QUNS_QUIET_TIME = 6;
    public const int QUNS_APP = 7;

    public const uint NIM_ADD = 0x00000000;
    public const uint NIM_MODIFY = 0x00000001;
    public const uint NIM_DELETE = 0x00000002;
    public const uint NIM_SETVERSION = 0x00000004;
    public const uint NIF_MESSAGE = 0x00000001;
    public const uint NIF_ICON = 0x00000002;
    public const uint NIF_TIP = 0x00000004;
    public const uint NIF_INFO = 0x00000010;
    public const uint NOTIFYICON_VERSION_4 = 4;
    public const uint NIIF_NONE = 0x00000000;
    public const uint NIIF_INFO = 0x00000001;
    public const uint NIIF_WARNING = 0x00000002;

    // ---- session / power ------------------------------------------------------------------
    public const int NOTIFY_FOR_THIS_SESSION = 0;
    public const int WTS_SESSION_LOCK = 0x7;
    public const int WTS_SESSION_UNLOCK = 0x8;
    public const int WTS_CONSOLE_CONNECT = 0x1;
    public const int PBT_APMRESUMEAUTOMATIC = 0x0012;
    public const int PBT_APMRESUMESUSPEND = 0x0007;

    // ---- DPI awareness --------------------------------------------------------------------
    public static readonly nint DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 = -4;
    public static readonly nint DPI_AWARENESS_CONTEXT_UNAWARE = -1;
    public static readonly nint DPI_AWARENESS_CONTEXT_SYSTEM_AWARE = -2;
    public static readonly nint DPI_AWARENESS_CONTEXT_UNAWARE_GDISCALED = -5;

    private const string User32 = "user32.dll";
    private const string Kernel32 = "kernel32.dll";
    private const string Shcore = "shcore.dll";
    private const string Shell32 = "shell32.dll";
    private const string Wtsapi32 = "wtsapi32.dll";

    // ---- hooks ----------------------------------------------------------------------------
    [DllImport(User32, SetLastError = true)]
    public static extern nint SetWindowsHookExW(int idHook, HookProc lpfn, nint hMod, uint dwThreadId);

    [DllImport(User32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool UnhookWindowsHookEx(nint hhk);

    [DllImport(User32)]
    public static extern nint CallNextHookEx(nint hhk, int nCode, nint wParam, nint lParam);

    [DllImport(User32, SetLastError = true)]
    public static extern nint SetWinEventHook(
        uint eventMin,
        uint eventMax,
        nint hmodWinEventProc,
        WinEventProc lpfnWinEventProc,
        uint idProcess,
        uint idThread,
        uint dwFlags);

    [DllImport(User32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool UnhookWinEvent(nint hWinEventHook);

    // ---- message loop ---------------------------------------------------------------------
    [DllImport(User32, SetLastError = true)]
    public static extern int GetMessageW(out MSG lpMsg, nint hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [DllImport(User32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool TranslateMessage(ref MSG lpMsg);

    [DllImport(User32)]
    public static extern nint DispatchMessageW(ref MSG lpMsg);

    [DllImport(User32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool PostMessageW(nint hWnd, uint msg, nint wParam, nint lParam);

    [DllImport(User32)]
    public static extern void PostQuitMessage(int exitCode);

    [DllImport(User32)]
    public static extern nint DefWindowProcW(nint hwnd, uint msg, nint wParam, nint lParam);

    [DllImport(User32, SetLastError = true)]
    public static extern ushort RegisterClassExW(ref WNDCLASSEXW lpwcx);

    [DllImport(User32, SetLastError = true)]
    public static extern nint CreateWindowExW(
        uint dwExStyle,
        nint lpClassName,
        nint lpWindowName,
        uint dwStyle,
        int x,
        int y,
        int nWidth,
        int nHeight,
        nint hWndParent,
        nint hMenu,
        nint hInstance,
        nint lpParam);

    [DllImport(User32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DestroyWindow(nint hwnd);

    public static readonly nint HWND_MESSAGE = -3;

    // ---- cursor ---------------------------------------------------------------------------
    [DllImport(User32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetCursorPos(int x, int y);

    [DllImport(User32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport(User32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool ClipCursor(nint lpRect);

    [DllImport(User32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool ClipCursor(ref RECT lpRect);

    private const uint DESKTOP_SWITCHDESKTOP = 0x0100;

    [DllImport(User32, SetLastError = true)]
    private static extern nint OpenInputDesktop(uint dwFlags, bool fInherit, uint dwDesiredAccess);

    [DllImport(User32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseDesktop(nint hDesktop);

    /// <summary>
    /// Returns false while Windows is showing a secure input desktop such as Ctrl+Alt+Delete,
    /// sign-in, or a secure UAC prompt. Normal desktop applications cannot install or service
    /// input hooks on that desktop by design.
    /// </summary>
    public static bool CanAccessInputDesktop()
    {
        nint desktop = OpenInputDesktop(0, false, DESKTOP_SWITCHDESKTOP);
        if (desktop == 0)
        {
            return false;
        }

        CloseDesktop(desktop);
        return true;
    }

    // ---- monitors -------------------------------------------------------------------------
    [DllImport(User32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool EnumDisplayMonitors(nint hdc, nint lprcClip, MonitorEnumProc lpfnEnum, nint dwData);

    [DllImport(User32, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetMonitorInfoW(nint hMonitor, ref MONITORINFOEXW lpmi);

    [DllImport(User32)]
    public static extern nint MonitorFromPoint(POINT pt, uint dwFlags);

    [DllImport(User32)]
    public static extern nint MonitorFromWindow(nint hwnd, uint dwFlags);

    [DllImport(User32, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool EnumDisplayDevicesW(
        string? lpDevice,
        uint iDevNum,
        ref DISPLAY_DEVICEW lpDisplayDevice,
        uint dwFlags);

    [DllImport(Shcore)]
    public static extern int GetDpiForMonitor(nint hmonitor, int dpiType, out uint dpiX, out uint dpiY);

    [DllImport(User32)]
    public static extern uint GetDpiForWindow(nint hwnd);

    [DllImport(User32)]
    public static extern nint GetWindowDpiAwarenessContext(nint hwnd);

    [DllImport(User32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool AreDpiAwarenessContextsEqual(nint a, nint b);

    [DllImport(User32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetProcessDpiAwarenessContext(nint value);

    // ---- windows --------------------------------------------------------------------------
    [DllImport(User32)]
    public static extern nint GetForegroundWindow();

    [DllImport(User32)]
    public static extern nint WindowFromPoint(POINT point);

    [DllImport(User32)]
    public static extern nint GetAncestor(nint hwnd, uint flags);

    [DllImport(User32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetWindowRect(nint hwnd, out RECT lpRect);

    [DllImport(User32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetWindowPos(
        nint hwnd,
        nint hWndInsertAfter,
        int x,
        int y,
        int cx,
        int cy,
        uint uFlags);

    [DllImport(User32, EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern nint GetWindowLongPtr64(nint hwnd, int nIndex);

    [DllImport(User32, EntryPoint = "GetWindowLongW", SetLastError = true)]
    private static extern int GetWindowLong32(nint hwnd, int nIndex);

    public static long GetWindowStyle(nint hwnd, int index) =>
        nint.Size == 8 ? GetWindowLongPtr64(hwnd, index) : GetWindowLong32(hwnd, index);

    [DllImport(User32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsWindow(nint hwnd);

    [DllImport(User32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsWindowVisible(nint hwnd);

    [DllImport(User32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsZoomed(nint hwnd);

    [DllImport(User32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsIconic(nint hwnd);

    [DllImport(User32)]
    public static extern uint GetWindowThreadProcessId(nint hwnd, out uint processId);

    [DllImport(User32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetForegroundWindow(nint hwnd);

    [DllImport(User32, CharSet = CharSet.Unicode)]
    public static extern int GetClassNameW(nint hwnd, StringBuilder buffer, int maxCount);

    // ---- raw input ------------------------------------------------------------------------
    [DllImport(User32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool RegisterRawInputDevices(
        [In] RAWINPUTDEVICE[] devices,
        uint numDevices,
        uint size);

    [DllImport(User32, SetLastError = true)]
    public static extern uint GetRawInputData(
        nint hRawInput,
        uint command,
        nint pData,
        ref uint pcbSize,
        uint cbSizeHeader);

    // ---- system parameters ----------------------------------------------------------------
    [DllImport(User32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SystemParametersInfoW(uint uiAction, uint uiParam, nint pvParam, uint fWinIni);

    public const uint SPI_GETMOUSESPEED = 0x0070;
    public const uint SPI_SETMOUSESPEED = 0x0071;
    public const uint SPIF_SENDCHANGE = 0x02;

    // ---- shell ----------------------------------------------------------------------------
    [DllImport(Shell32)]
    public static extern int SHQueryUserNotificationState(out int state);

    [DllImport(Shell32, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool Shell_NotifyIconW(uint dwMessage, ref NOTIFYICONDATAW lpData);

    // ---- session --------------------------------------------------------------------------
    [DllImport(Wtsapi32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool WTSRegisterSessionNotification(nint hwnd, uint dwFlags);

    [DllImport(Wtsapi32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool WTSUnRegisterSessionNotification(nint hwnd);

    [DllImport(Shell32, CharSet = CharSet.Unicode)]
    public static extern uint ExtractIconExW(
        string lpszFile,
        int nIconIndex,
        out nint phiconLarge,
        out nint phiconSmall,
        uint nIcons);

    [DllImport(User32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DestroyIcon(nint hIcon);

    // ---- kernel ---------------------------------------------------------------------------
    [DllImport(Kernel32, CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern nint GetModuleHandleW(string? lpModuleName);

    [DllImport(Kernel32)]
    public static extern uint GetCurrentThreadId();

    [DllImport(Kernel32)]
    public static extern ulong GetTickCount64();
}
