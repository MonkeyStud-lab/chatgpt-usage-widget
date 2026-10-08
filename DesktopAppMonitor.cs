using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace UsageWidget;

public sealed class DesktopAppMonitor
{
    IntPtr window;
    int processId;
    [DllImport("user32.dll")] static extern bool IsWindow(IntPtr handle);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr handle);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr handle, out uint processId);

    public bool IsOpen()
    {
        // A valid cached HWND requires only three native checks. Minimized windows
        // remain visible to IsWindowVisible; hidden/closed windows do not.
        if (window != IntPtr.Zero && IsWindow(window) && IsWindowVisible(window))
        {
            GetWindowThreadProcessId(window, out var owner);
            if (owner == processId) return true;
        }
        window = IntPtr.Zero;
        var processes = Process.GetProcessesByName("ChatGPT");
        try
        {
            foreach (var process in processes)
            {
                try {
                    var handle = process.MainWindowHandle;
                    if (handle == IntPtr.Zero) continue;
                    window = handle; processId = process.Id; return true;
                }
                catch (InvalidOperationException) { }
                catch (System.ComponentModel.Win32Exception) { }
            }
            return false;
        }
        finally { foreach (var process in processes) process.Dispose(); }
    }
}

public static class Palette
{
    public static readonly System.Windows.Media.Brush Green = Make(0x34, 0xD3, 0x99);
    public static readonly System.Windows.Media.Brush Yellow = Make(0xFB, 0xBF, 0x24);
    public static readonly System.Windows.Media.Brush Red = Make(0xF8, 0x71, 0x71);
    public static readonly System.Windows.Media.Brush Neutral = Make(0x94, 0xA3, 0xB8);
    public static readonly System.Windows.Media.Brush DarkForeground = Make(0xEE, 0xF2, 0xF8);
    public static readonly System.Windows.Media.Brush LightForeground = Make(0x17, 0x23, 0x38);
    public static readonly System.Windows.Media.Brush DarkBackground = Make(0x14, 0x1C, 0x29);
    public static readonly System.Windows.Media.Brush LightBackground = Make(0xFA, 0xFC, 0xFF);
    public static readonly System.Windows.Media.Brush DarkBorder = Make(0x34, 0x40, 0x55);
    public static readonly System.Windows.Media.Brush LightBorder = Make(0xCB, 0xD5, 0xE1);
    static System.Windows.Media.Brush Make(byte r, byte g, byte b) { var brush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(r, g, b)); brush.Freeze(); return brush; }
    public static System.Windows.Media.Brush Usage(double remaining) => remaining <= 10 ? Red : remaining <= 25 ? Yellow : Green;
}
