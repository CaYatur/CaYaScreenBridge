using CaYaScreenBridge.Core.Diagnostics;
using CaYaScreenBridge.Core.Model;
using CaYaScreenBridge.Windows.Native;

namespace CaYaScreenBridge.Windows.Engine;

/// <summary>
/// Optionally rescales Windows' pointer sensitivity per display so that a given hand movement covers
/// the same physical distance on every screen.
///
/// This is separate from the cursor alignment, and off by default. Alignment fixes <i>where</i> the
/// pointer arrives; this fixes how fast it travels, which only matters when the pixel densities
/// differ enough to be felt. It is a per user system setting, so it is captured on start and put
/// back on exit.
/// </summary>
public sealed class PointerSpeedManager
{
    private const int MinSpeed = 1;
    private const int MaxSpeed = 20;

    private readonly ILogSink _log;
    private readonly object _lock = new();

    private bool _enabled;
    private int _baseSpeed = 10;
    private bool _baseCaptured;
    private int _appliedSpeed;
    private double _referencePixelsPerMm;

    public PointerSpeedManager(ILogSink log) => _log = log;

    public void SetEnabled(bool enabled)
    {
        lock (_lock)
        {
            if (_enabled == enabled)
            {
                return;
            }

            _enabled = enabled;

            if (enabled)
            {
                CaptureBase();
            }
            else
            {
                RestoreCore();
            }
        }
    }

    /// <summary>The primary display defines the reference density; everything scales relative to it.</summary>
    public void SetLayout(ZoneLayout layout)
    {
        lock (_lock)
        {
            DisplayZone? reference = layout.Primary ?? layout.Zones.FirstOrDefault();
            _referencePixelsPerMm = reference?.PixelsPerMm.X ?? 0;
        }
    }

    public void OnZoneChanged(DisplayZone zone)
    {
        lock (_lock)
        {
            if (!_enabled || _referencePixelsPerMm <= 0)
            {
                return;
            }

            CaptureBase();

            double ratio = zone.PixelsPerMm.X / _referencePixelsPerMm;
            int speed = Math.Clamp((int)Math.Round(_baseSpeed * ratio), MinSpeed, MaxSpeed);

            if (speed == _appliedSpeed)
            {
                return;
            }

            _appliedSpeed = speed;

            // Off the input thread: SystemParametersInfo broadcasts WM_SETTINGCHANGE to every
            // top-level window, and waiting for that inside the hook callback would be fatal.
            ThreadPool.UnsafeQueueUserWorkItem(static state => ApplySpeed(state), speed, preferLocal: false);
        }
    }

    public void Restore()
    {
        lock (_lock)
        {
            RestoreCore();
        }
    }

    private void RestoreCore()
    {
        if (!_baseCaptured || _appliedSpeed == _baseSpeed)
        {
            return;
        }

        int baseSpeed = _baseSpeed;
        _appliedSpeed = baseSpeed;
        ThreadPool.UnsafeQueueUserWorkItem(static state => ApplySpeed(state), baseSpeed, preferLocal: false);
        _log.Debug("Pointer", $"Pointer speed restored to {baseSpeed}.");
    }

    private void CaptureBase()
    {
        if (_baseCaptured)
        {
            return;
        }

        nint buffer = System.Runtime.InteropServices.Marshal.AllocHGlobal(sizeof(int));

        try
        {
            System.Runtime.InteropServices.Marshal.WriteInt32(buffer, 10);

            if (Win32.SystemParametersInfoW(Win32.SPI_GETMOUSESPEED, 0, buffer, 0))
            {
                int value = System.Runtime.InteropServices.Marshal.ReadInt32(buffer);
                if (value is >= MinSpeed and <= MaxSpeed)
                {
                    _baseSpeed = value;
                }
            }

            _baseCaptured = true;
            _appliedSpeed = _baseSpeed;
            _log.Debug("Pointer", $"Base pointer speed captured as {_baseSpeed}.");
        }
        catch (Exception ex)
        {
            _log.Warn("Pointer", $"Could not read the current pointer speed: {ex.Message}");
            _baseCaptured = true;
        }
        finally
        {
            System.Runtime.InteropServices.Marshal.FreeHGlobal(buffer);
        }
    }

    private static void ApplySpeed(int speed)
    {
        try
        {
            Win32.SystemParametersInfoW(Win32.SPI_SETMOUSESPEED, 0, speed, Win32.SPIF_SENDCHANGE);
        }
        catch
        {
            // A failure here only means the optional speed normalisation did not take effect.
        }
    }
}
