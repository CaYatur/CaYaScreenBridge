using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CaYaScreenBridge.Core.Diagnostics;
using CaYaScreenBridge.Core.Geometry;
using CaYaScreenBridge.Windows.Native;

namespace CaYaScreenBridge.Windows.Services;

/// <summary>Reads the wallpaper assigned to each monitor through Windows' IDesktopWallpaper API.</summary>
public sealed class DesktopWallpaperService
{
    private readonly ILogSink _log;

    public DesktopWallpaperService(ILogSink log) => _log = log;

    public IReadOnlyList<WallpaperSnapshot> Enumerate()
    {
        var result = new List<WallpaperSnapshot>();
        IDesktopWallpaper? desktop = null;

        try
        {
            Type type = Type.GetTypeFromCLSID(DesktopWallpaperClsid, throwOnError: true)!;
            desktop = (IDesktopWallpaper)Activator.CreateInstance(type)!;
            uint count = desktop.GetMonitorDevicePathCount();

            for (uint index = 0; index < count; index++)
            {
                string monitorId = desktop.GetMonitorDevicePathAt(index);
                desktop.GetMonitorRECT(monitorId, out RECT monitorRect);
                string path = desktop.GetWallpaper(monitorId);

                if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                {
                    continue;
                }

                ImageBrush? brush = LoadBrush(path);
                if (brush is null)
                {
                    continue;
                }

                result.Add(new WallpaperSnapshot(
                    new RectD(
                        monitorRect.Left,
                        monitorRect.Top,
                        monitorRect.Right - monitorRect.Left,
                        monitorRect.Bottom - monitorRect.Top),
                    path,
                    brush));
            }
        }
        catch (Exception ex)
        {
            _log.Warn("Wallpaper", $"Could not read per-monitor wallpaper information: {ex.Message}");
        }
        finally
        {
            if (desktop is not null && Marshal.IsComObject(desktop))
            {
                Marshal.FinalReleaseComObject(desktop);
            }
        }

        return result;
    }

    private static ImageBrush? LoadBrush(string path)
    {
        try
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.UriSource = new Uri(path, UriKind.Absolute);
            bitmap.EndInit();
            bitmap.Freeze();

            var brush = new ImageBrush(bitmap)
            {
                Stretch = Stretch.UniformToFill,
                AlignmentX = AlignmentX.Center,
                AlignmentY = AlignmentY.Center,
            };
            brush.Freeze();
            return brush;
        }
        catch
        {
            return null;
        }
    }

    private static readonly Guid DesktopWallpaperClsid =
        new("C2CF3110-460E-4FC1-B9D0-8A1C0C9CC4BD");

    [ComImport]
    [Guid("B92B56A9-8B55-4E14-9A89-0199BBB6F93B")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDesktopWallpaper
    {
        void SetWallpaper(
            [MarshalAs(UnmanagedType.LPWStr)] string? monitorId,
            [MarshalAs(UnmanagedType.LPWStr)] string wallpaper);

        [return: MarshalAs(UnmanagedType.LPWStr)]
        string GetWallpaper([MarshalAs(UnmanagedType.LPWStr)] string? monitorId);

        [return: MarshalAs(UnmanagedType.LPWStr)]
        string GetMonitorDevicePathAt(uint monitorIndex);

        uint GetMonitorDevicePathCount();

        void GetMonitorRECT(
            [MarshalAs(UnmanagedType.LPWStr)] string monitorId,
            out RECT displayRect);

        void SetBackgroundColor(uint color);
        uint GetBackgroundColor();
        void SetPosition(int position);
        int GetPosition();
        void SetSlideshow(nint items);
        nint GetSlideshow();
        void SetSlideshowOptions(int options, uint slideshowTick);
        void GetSlideshowOptions(out int options, out uint slideshowTick);
        void AdvanceSlideshow([MarshalAs(UnmanagedType.LPWStr)] string? monitorId, int direction);
        int GetStatus();
        void Enable([MarshalAs(UnmanagedType.Bool)] bool enable);
    }
}

public sealed record WallpaperSnapshot(RectD PixelBounds, string Path, ImageBrush Brush);
