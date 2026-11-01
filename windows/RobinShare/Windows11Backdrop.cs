using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace RobinShare;

/// <summary>Opt-in Windows 11 DWM material; no third-party theme renderer.</summary>
public static class Windows11Backdrop
{
    private const int DwmSystemBackdrop = 38;
    private const int DwmImmersiveDarkMode = 20;
    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    public static void Apply(Window window)
    {
        if(!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22621)) return;
        var handle = new WindowInteropHelper(window).Handle;
        if(handle == IntPtr.Zero) return;
        try
        {
            int mica = 2; // DWMSBT_MAINWINDOW
            _ = DwmSetWindowAttribute(handle, DwmSystemBackdrop, ref mica, sizeof(int));
            var dark = System.Windows.SystemParameters.HighContrast ? 0 :
                System.Windows.Media.SystemColors.WindowColor.R < 80 ? 1 : 0;
            _ = DwmSetWindowAttribute(handle, DwmImmersiveDarkMode, ref dark, sizeof(int));
        }
        catch(DllNotFoundException) { }
        catch(EntryPointNotFoundException) { }
    }
}
