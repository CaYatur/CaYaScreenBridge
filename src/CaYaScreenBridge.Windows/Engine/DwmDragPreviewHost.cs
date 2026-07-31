using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using CaYaScreenBridge.Core.Geometry;
using CaYaScreenBridge.Core.Model;

namespace CaYaScreenBridge.Windows.Engine;

/// <summary>
/// Shows one live DWM thumbnail piece per display while a dragged HWND spans displays with different
/// physical densities. The real HWND is cloaked only for that overlap interval; Windows continues to
/// own its move/DPI loop and becomes visible again as soon as the window belongs to one display.
/// </summary>
internal sealed class DwmDragPreviewHost : IDisposable
{
    private const double ActivateSecondAreaMm2 = 180;
    private const double ReleaseSecondAreaMm2 = 35;

    private readonly object _gate = new();
    private readonly Dictionary<string, PreviewWindow> _windows = new(StringComparer.OrdinalIgnoreCase);

    private PendingUpdate? _pending;
    private int _dispatchPending;
    private bool _active;
    private bool _disposed;
    private nint _cloakedWindow;

    public void Update(nint sourceWindow, RectD physicalWindow, ZoneLayout layout)
    {
        if (_disposed || sourceWindow == 0 || physicalWindow.Width <= 0 || physicalWindow.Height <= 0)
        {
            return;
        }

        lock (_gate)
        {
            _pending = new PendingUpdate(sourceWindow, physicalWindow, layout);
        }

        QueueDispatcherUpdate();
    }

    public void Hide()
    {
        lock (_gate)
        {
            _pending = null;
        }

        QueueDispatcherUpdate();
    }

    private void QueueDispatcherUpdate()
    {
        Application? application = Application.Current;
        if (application is null || Interlocked.Exchange(ref _dispatchPending, 1) != 0)
        {
            return;
        }

        application.Dispatcher.BeginInvoke(
            DispatcherPriority.Render,
            new Action(ProcessPending));
    }

    private void ProcessPending()
    {
        Interlocked.Exchange(ref _dispatchPending, 0);

        PendingUpdate? update;
        lock (_gate)
        {
            update = _pending;
        }

        if (_disposed || update is null)
        {
            HideOnDispatcher();
            return;
        }

        List<Piece> pieces = BuildPieces(update.PhysicalWindow, update.Layout);
        double secondArea = pieces.Count >= 2
            ? pieces.OrderByDescending(p => p.PhysicalAreaMm2).Skip(1).First().PhysicalAreaMm2
            : 0;
        double threshold = _active ? ReleaseSecondAreaMm2 : ActivateSecondAreaMm2;

        if (pieces.Count < 2 || secondArea < threshold)
        {
            HideOnDispatcher();
            return;
        }

        if (!EnsureCloaked(update.SourceWindow))
        {
            HideOnDispatcher();
            return;
        }

        _active = true;
        var activeIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (Piece piece in pieces)
        {
            activeIds.Add(piece.Zone.StableId);
            if (!_windows.TryGetValue(piece.Zone.StableId, out PreviewWindow? preview))
            {
                preview = new PreviewWindow();
                _windows.Add(piece.Zone.StableId, preview);
            }

            preview.ShowPiece(update.SourceWindow, update.PhysicalWindow, piece);
        }

        foreach ((string id, PreviewWindow preview) in _windows)
        {
            if (!activeIds.Contains(id))
            {
                preview.HidePiece();
            }
        }

        // Coalesce samples that arrived while the UI dispatcher was rendering this one.
        lock (_gate)
        {
            if (!ReferenceEquals(update, _pending))
            {
                QueueDispatcherUpdate();
            }
        }
    }

    private bool EnsureCloaked(nint sourceWindow)
    {
        if (_cloakedWindow == sourceWindow)
        {
            return true;
        }

        UncloakCurrent();

        int value = 1;
        int hr = DwmSetWindowAttribute(
            sourceWindow,
            DwmwaCloak,
            ref value,
            Marshal.SizeOf<int>());
        if (hr < 0)
        {
            return false;
        }

        _cloakedWindow = sourceWindow;
        return true;
    }

    private void HideOnDispatcher()
    {
        _active = false;
        foreach (PreviewWindow preview in _windows.Values)
        {
            preview.HidePiece();
        }

        UncloakCurrent();
    }

    private void UncloakCurrent()
    {
        if (_cloakedWindow == 0)
        {
            return;
        }

        int value = 0;
        _ = DwmSetWindowAttribute(
            _cloakedWindow,
            DwmwaCloak,
            ref value,
            Marshal.SizeOf<int>());
        _cloakedWindow = 0;
    }

    private static List<Piece> BuildPieces(RectD physicalWindow, ZoneLayout layout)
    {
        var pieces = new List<Piece>();

        foreach (DisplayZone zone in layout.Zones)
        {
            RectD physicalClip = Intersect(physicalWindow, zone.PhysicalBounds);
            if (physicalClip.Width <= 0 || physicalClip.Height <= 0)
            {
                continue;
            }

            var destination = new RectD(
                zone.PixelBounds.Left + ((physicalClip.Left - zone.PhysicalBounds.Left) * zone.PixelsPerMm.X),
                zone.PixelBounds.Top + ((physicalClip.Top - zone.PhysicalBounds.Top) * zone.PixelsPerMm.Y),
                physicalClip.Width * zone.PixelsPerMm.X,
                physicalClip.Height * zone.PixelsPerMm.Y);

            pieces.Add(new Piece(
                zone,
                physicalClip,
                destination,
                physicalClip.Width * physicalClip.Height));
        }

        return pieces;
    }

    private static RectD Intersect(RectD a, RectD b)
    {
        double left = Math.Max(a.Left, b.Left);
        double top = Math.Max(a.Top, b.Top);
        double right = Math.Min(a.Right, b.Right);
        double bottom = Math.Min(a.Bottom, b.Bottom);

        return right <= left || bottom <= top
            ? RectD.Empty
            : RectD.FromEdges(left, top, right, bottom);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        lock (_gate)
        {
            _pending = null;
        }

        Application? application = Application.Current;
        if (application is null)
        {
            return;
        }

        application.Dispatcher.Invoke(() =>
        {
            HideOnDispatcher();
            foreach (PreviewWindow preview in _windows.Values)
            {
                preview.Dispose();
            }

            _windows.Clear();
        });
    }

    private sealed record PendingUpdate(nint SourceWindow, RectD PhysicalWindow, ZoneLayout Layout);

    private sealed record Piece(
        DisplayZone Zone,
        RectD PhysicalClip,
        RectD DestinationPixel,
        double PhysicalAreaMm2);

    private sealed class PreviewWindow : IDisposable
    {
        private nint _destinationWindow;
        private nint _sourceWindow;
        private nint _thumbnail;
        private bool _disposed;

        public PreviewWindow()
        {
            _destinationWindow = CreateWindowExW(
                WsExTransparent | WsExNoActivate | WsExToolWindow | WsExLayered,
                "STATIC",
                string.Empty,
                WsPopup,
                -32000,
                -32000,
                1,
                1,
                0,
                0,
                GetModuleHandleW(null),
                0);

            if (_destinationWindow != 0)
            {
                int disableTransitions = 1;
                _ = DwmSetWindowAttribute(
                    _destinationWindow,
                    DwmwaTransitionsForcedDisabled,
                    ref disableTransitions,
                    Marshal.SizeOf<int>());
            }
        }

        public void ShowPiece(nint sourceWindow, RectD physicalWindow, Piece piece)
        {
            if (_disposed || _destinationWindow == 0 || !EnsureThumbnail(sourceWindow))
            {
                return;
            }

            RectD destination = piece.DestinationPixel;
            int width = Math.Max(1, (int)Math.Round(destination.Width));
            int height = Math.Max(1, (int)Math.Round(destination.Height));

            _ = SetWindowPos(
                _destinationWindow,
                HwndTopmost,
                (int)Math.Round(destination.Left),
                (int)Math.Round(destination.Top),
                width,
                height,
                SwpNoActivate | SwpShowWindow);

            _ = DwmQueryThumbnailSourceSize(_thumbnail, out NativeSize sourceSize);
            double u0 = (piece.PhysicalClip.Left - physicalWindow.Left) / physicalWindow.Width;
            double v0 = (piece.PhysicalClip.Top - physicalWindow.Top) / physicalWindow.Height;
            double u1 = (piece.PhysicalClip.Right - physicalWindow.Left) / physicalWindow.Width;
            double v1 = (piece.PhysicalClip.Bottom - physicalWindow.Top) / physicalWindow.Height;

            int sourceLeft = ClampSource(u0, sourceSize.Width);
            int sourceTop = ClampSource(v0, sourceSize.Height);
            int sourceRight = Math.Max(sourceLeft + 1, ClampSource(u1, sourceSize.Width));
            int sourceBottom = Math.Max(sourceTop + 1, ClampSource(v1, sourceSize.Height));

            var properties = new DwmThumbnailProperties
            {
                Flags = DwmTnpRectDestination | DwmTnpRectSource | DwmTnpVisible | DwmTnpOpacity,
                Destination = new NativeRect(0, 0, width, height),
                Source = new NativeRect(sourceLeft, sourceTop, sourceRight, sourceBottom),
                Opacity = byte.MaxValue,
                Visible = true,
                SourceClientAreaOnly = false,
            };

            _ = DwmUpdateThumbnailProperties(_thumbnail, ref properties);
        }

        public void HidePiece()
        {
            if (_destinationWindow != 0)
            {
                _ = ShowWindow(_destinationWindow, SwHide);
            }
        }

        private bool EnsureThumbnail(nint sourceWindow)
        {
            if (_thumbnail != 0 && _sourceWindow == sourceWindow)
            {
                return true;
            }

            if (_thumbnail != 0)
            {
                _ = DwmUnregisterThumbnail(_thumbnail);
                _thumbnail = 0;
            }

            _sourceWindow = sourceWindow;
            return DwmRegisterThumbnail(_destinationWindow, sourceWindow, out _thumbnail) >= 0 &&
                   _thumbnail != 0;
        }

        private static int ClampSource(double fraction, int sourceLength) =>
            Math.Clamp((int)Math.Round(Math.Clamp(fraction, 0, 1) * sourceLength), 0, sourceLength);

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            if (_thumbnail != 0)
            {
                _ = DwmUnregisterThumbnail(_thumbnail);
                _thumbnail = 0;
            }

            if (_destinationWindow != 0)
            {
                _ = DestroyWindow(_destinationWindow);
                _destinationWindow = 0;
            }
        }
    }

    private const uint WsExTransparent = 0x00000020;
    private const uint WsExToolWindow = 0x00000080;
    private const uint WsExLayered = 0x00080000;
    private const uint WsExNoActivate = 0x08000000;
    private const uint WsPopup = 0x80000000;
    private static readonly nint HwndTopmost = new(-1);
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpShowWindow = 0x0040;
    private const int SwHide = 0;

    private const uint DwmTnpRectDestination = 0x00000001;
    private const uint DwmTnpRectSource = 0x00000002;
    private const uint DwmTnpOpacity = 0x00000004;
    private const uint DwmTnpVisible = 0x00000008;
    private const int DwmwaTransitionsForcedDisabled = 3;
    private const int DwmwaCloak = 13;

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;

        public NativeRect(int left, int top, int right, int bottom)
        {
            Left = left;
            Top = top;
            Right = right;
            Bottom = bottom;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeSize
    {
        public int Width;
        public int Height;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DwmThumbnailProperties
    {
        public uint Flags;
        public NativeRect Destination;
        public NativeRect Source;
        public byte Opacity;
        [MarshalAs(UnmanagedType.Bool)] public bool Visible;
        [MarshalAs(UnmanagedType.Bool)] public bool SourceClientAreaOnly;
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmRegisterThumbnail(nint destination, nint source, out nint thumbnail);

    [DllImport("dwmapi.dll")]
    private static extern int DwmUnregisterThumbnail(nint thumbnail);

    [DllImport("dwmapi.dll")]
    private static extern int DwmUpdateThumbnailProperties(nint thumbnail, ref DwmThumbnailProperties properties);

    [DllImport("dwmapi.dll")]
    private static extern int DwmQueryThumbnailSourceSize(nint thumbnail, out NativeSize size);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(nint hwnd, int attribute, ref int value, int valueSize);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(
        nint hwnd,
        nint insertAfter,
        int x,
        int y,
        int width,
        int height,
        uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CreateWindowExW(
        uint exStyle,
        string className,
        string windowName,
        uint style,
        int x,
        int y,
        int width,
        int height,
        nint parent,
        nint menu,
        nint instance,
        nint parameter);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyWindow(nint hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(nint hwnd, int command);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern nint GetModuleHandleW(string? moduleName);

}
