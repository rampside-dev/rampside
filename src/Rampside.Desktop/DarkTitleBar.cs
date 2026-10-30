using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace Rampside.Desktop;

/// <summary>Dark window frame to match the theme (Windows 10 20H1+; the caption colour needs Windows 11).</summary>
internal static class DarkTitleBar
{
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaCaptionColor = 35;

    public static void Apply(Window window, Color caption)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        var enabled = 1;
        var colorRef = caption.R | caption.G << 8 | caption.B << 16;

        // Failures are harmless: older Windows versions just keep the light frame.
        _ = DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkMode, ref enabled, sizeof(int));
        _ = DwmSetWindowAttribute(hwnd, DwmwaCaptionColor, ref colorRef, sizeof(int));
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
