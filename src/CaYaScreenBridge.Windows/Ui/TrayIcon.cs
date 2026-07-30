using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using CaYaScreenBridge.Core.Diagnostics;
using CaYaScreenBridge.Windows.Native;

namespace CaYaScreenBridge.Windows.Ui;

/// <summary>
/// The notification area icon and its menu.
///
/// Written against Shell_NotifyIcon directly rather than through Windows Forms interop: it avoids
/// pulling a second UI framework into the process, and it lets the menu be a real WPF menu that
/// matches the rest of the application instead of a grey system popup.
/// </summary>
public sealed class TrayIcon : IDisposable
{
    private const uint TrayId = 1;

    private readonly ILogSink _log;
    private readonly HwndSource _source;
    private readonly ContextMenu _menu;
    private readonly MenuItem _toggleItem;

    private nint _iconHandle;
    private bool _added;
    private bool _disposed;

    public TrayIcon(ILogSink log)
    {
        _log = log;

        var parameters = new HwndSourceParameters("CaYaScreenBridge.Tray")
        {
            Width = 0,
            Height = 0,
            ParentWindow = Win32.HWND_MESSAGE,
            WindowStyle = 0,
        };

        _source = new HwndSource(parameters);
        _source.AddHook(WindowHook);

        _menu = BuildMenu(out _toggleItem);
        _iconHandle = LoadApplicationIcon();

        AddIcon();
    }

    public event Action? ShowRequested;

    public event Action? ExitRequested;

    public event Action? ToggleRequested;

    public event Action? RebuildRequested;

    public void UpdateState(bool active)
    {
        _toggleItem.Header = active ? Loc.Get("tray.pause") : Loc.Get("tray.resume");
        SetTooltip(active ? Loc.Get("tray.tipRunning") : Loc.Get("tray.tipPaused"));
    }

    public void ShowMessage(string title, string message, bool warning = false)
    {
        if (!_added)
        {
            return;
        }

        NOTIFYICONDATAW data = CreateData();
        data.uFlags = Win32.NIF_INFO;
        data.szInfoTitle = Truncate(title, 63);
        data.szInfo = Truncate(message, 255);
        data.dwInfoFlags = warning ? Win32.NIIF_WARNING : Win32.NIIF_INFO;

        Win32.Shell_NotifyIconW(Win32.NIM_MODIFY, ref data);
    }

    // -------------------------------------------------------------------------------------------

    private void AddIcon()
    {
        NOTIFYICONDATAW data = CreateData();
        data.uFlags = Win32.NIF_MESSAGE | Win32.NIF_ICON | Win32.NIF_TIP;
        data.uCallbackMessage = Win32.WM_BRIDGE_TRAY;
        data.hIcon = _iconHandle;
        data.szTip = Loc.Get("tray.tipRunning");

        _added = Win32.Shell_NotifyIconW(Win32.NIM_ADD, ref data);

        if (!_added)
        {
            _log.Warn("Tray", $"Could not add the notification icon ({Marshal.GetLastWin32Error()}).");
            return;
        }

        // Version 4 gives the modern behaviour, including correct positioning of the menu on a
        // secondary display and proper handling of the taskbar being on another edge.
        NOTIFYICONDATAW version = CreateData();
        version.uVersionOrTimeout = Win32.NOTIFYICON_VERSION_4;
        Win32.Shell_NotifyIconW(Win32.NIM_SETVERSION, ref version);
    }

    private void SetTooltip(string tip)
    {
        if (!_added)
        {
            return;
        }

        NOTIFYICONDATAW data = CreateData();
        data.uFlags = Win32.NIF_TIP;
        data.szTip = Truncate(tip, 127);
        Win32.Shell_NotifyIconW(Win32.NIM_MODIFY, ref data);
    }

    private NOTIFYICONDATAW CreateData() => new()
    {
        cbSize = Marshal.SizeOf<NOTIFYICONDATAW>(),
        hWnd = _source.Handle,
        uID = TrayId,
        szTip = string.Empty,
        szInfo = string.Empty,
        szInfoTitle = string.Empty,
    };

    private nint WindowHook(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        if ((uint)msg != Win32.WM_BRIDGE_TRAY)
        {
            return 0;
        }

        // With version 4 the message identifier is in the low word of lParam.
        uint action = (uint)(lParam.ToInt64() & 0xFFFF);

        switch (action)
        {
            case Win32.WM_LBUTTONUP:
                handled = true;
                ShowRequested?.Invoke();
                break;

            case Win32.WM_RBUTTONUP:
                handled = true;
                ShowMenu();
                break;
        }

        return 0;
    }

    private void ShowMenu()
    {
        // Bringing our hidden window forward is what lets the menu dismiss itself when the user
        // clicks elsewhere; without it the popup stays on screen.
        Win32.SetForegroundWindow(_source.Handle);

        _menu.Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint;
        _menu.IsOpen = true;
    }

    private ContextMenu BuildMenu(out MenuItem toggleItem)
    {
        var menu = new ContextMenu
        {
            Background = new SolidColorBrush(Color.FromRgb(0x14, 0x18, 0x26)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x33, 0x3C, 0x55)),
            BorderThickness = new Thickness(1),
            Foreground = Brushes.White,
            Padding = new Thickness(4),
        };

        MenuItem show = CreateItem(Loc.Get("tray.show"), () => ShowRequested?.Invoke());
        show.FontWeight = FontWeights.SemiBold;

        toggleItem = CreateItem(Loc.Get("tray.pause"), () => ToggleRequested?.Invoke());
        MenuItem rebuild = CreateItem(Loc.Get("tray.rebuild"), () => RebuildRequested?.Invoke());
        MenuItem exit = CreateItem(Loc.Get("tray.exit"), () => ExitRequested?.Invoke());

        menu.Items.Add(show);
        menu.Items.Add(new Separator());
        menu.Items.Add(toggleItem);
        menu.Items.Add(rebuild);
        menu.Items.Add(new Separator());
        menu.Items.Add(exit);

        return menu;
    }

    private static MenuItem CreateItem(string header, Action action)
    {
        var item = new MenuItem
        {
            Header = header,
            Foreground = Brushes.White,
            Padding = new Thickness(10, 6, 10, 6),
        };

        item.Click += (_, _) => action();
        return item;
    }

    /// <summary>
    /// Takes the icon straight out of the executable rather than decoding the WPF resource, so the
    /// tray gets a real HICON at the size the shell asked for.
    /// </summary>
    private nint LoadApplicationIcon()
    {
        try
        {
            string path = Environment.ProcessPath ?? string.Empty;
            if (path.Length > 0 && Win32.ExtractIconExW(path, 0, out nint large, out nint small, 1) > 0)
            {
                if (small != 0)
                {
                    if (large != 0)
                    {
                        Win32.DestroyIcon(large);
                    }

                    return small;
                }

                if (large != 0)
                {
                    return large;
                }
            }
        }
        catch (Exception ex)
        {
            _log.Warn("Tray", $"Could not load the application icon: {ex.Message}");
        }

        return 0;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (_added)
        {
            NOTIFYICONDATAW data = CreateData();
            Win32.Shell_NotifyIconW(Win32.NIM_DELETE, ref data);
            _added = false;
        }

        if (_iconHandle != 0)
        {
            Win32.DestroyIcon(_iconHandle);
            _iconHandle = 0;
        }

        _source.RemoveHook(WindowHook);
        _source.Dispose();
    }

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength];
}
