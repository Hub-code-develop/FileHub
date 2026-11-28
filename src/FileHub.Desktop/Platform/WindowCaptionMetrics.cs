using System;
using System.Runtime.InteropServices;

namespace FileHub.Desktop.Platform;

/// <summary>
/// Width of the caption buttons Windows keeps painting on the top right after the client area
/// is extended over the window decorations.
///
/// Avalonia's WindowDecorationMargin only reports the title bar height on Win32 (see
/// Avalonia.Win32 UpdateExtendMargins: left/right are always 0), so the row that replaces the
/// title bar cannot be padded from it. The real rectangle is asked of DWM instead; when that
/// fails (no handle yet, composition off) it falls back to the system metrics for the button
/// width, and finally to the documented 46px per button at 100% scaling.
/// </summary>
internal static class WindowCaptionMetrics
{
    /// <summary>DWMWA_CAPTION_BUTTON_BOUNDS — the caption button area of the window.</summary>
    private const int DwmwaCaptionButtonBounds = 5;

    /// <summary>SM_CXSIZE — width of a caption button.</summary>
    private const int SmCxSize = 30;

    /// <summary>SM_CXPADDEDBORDER — padding around the caption buttons.</summary>
    private const int SmCxPaddedBorder = 92;

    /// <summary>Gap kept between the system buttons and our own title bar content, in DIPs.</summary>
    public const double Gap = 2;

    /// <summary>
    /// Total width of the minimise / maximise / close buttons in physical pixels, or 0 when it
    /// cannot be determined.
    /// </summary>
    public static int GetCaptionButtonsWidth(IntPtr windowHandle, uint dpi)
    {
        // A missing handle (window not created yet) must still fall through to the metric
        // estimate: returning 0 would leave the row unpadded and the buttons would cover it.
        if (windowHandle != IntPtr.Zero)
        {
            try
            {
                if (DwmGetWindowAttribute(windowHandle, DwmwaCaptionButtonBounds, out var bounds,
                        Marshal.SizeOf<NativeRect>()) == 0)
                {
                    var width = bounds.Right - bounds.Left;
                    if (width > 0) return width;
                }
            }
            catch (DllNotFoundException)
            {
                // No dwmapi (a non-Windows host, in theory): use the system metrics.
            }
            catch (EntryPointNotFoundException)
            {
            }
        }

        var button = SystemMetric(SmCxSize, dpi);
        var padding = SystemMetric(SmCxPaddedBorder, dpi);
        var estimated = 3 * (button + padding);
        return estimated > 0 ? estimated : 3 * 46;
    }

    /// <summary>Per-DPI metrics where available, plain GetSystemMetrics on older systems.</summary>
    private static int SystemMetric(int index, uint dpi)
    {
        if (dpi != 0)
        {
            try
            {
                return GetSystemMetricsForDpi(index, dpi);
            }
            catch (EntryPointNotFoundException)
            {
                // Windows 10 1607 and older have no per-DPI metrics.
            }
        }

        return GetSystemMetrics(index);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out NativeRect value, int size);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetricsForDpi(int index, uint dpi);
}
