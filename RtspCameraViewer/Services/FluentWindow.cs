using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;

namespace RtspCameraViewer.Services
{
    /// <summary>
    /// Gives a WPF window the Windows 11 look through DWM: dark title bar, rounded corners, and
    /// the Mica backdrop, plus the user's own accent colour for the theme.
    ///
    /// Mica is applied by extending the DWM frame across the whole window and making the WPF
    /// render target transparent - NOT with AllowsTransparency, which switches WPF to
    /// layered-window rendering and cannot host the native video windows at all. Every step
    /// checks its result: on a Windows build without system backdrops the window simply keeps
    /// its solid dark background instead of going black or see-through.
    /// </summary>
    public static class FluentWindow
    {
        private const int DWMWA_USE_IMMERSIVE_DARK_MODE_BEFORE_20H1 = 19;
        private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
        private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
        private const int DWMWA_SYSTEMBACKDROP_TYPE = 38;
        private const int DWMWCP_ROUND = 2;
        private const int DWMSBT_MAINWINDOW = 2; // Mica

        [StructLayout(LayoutKind.Sequential)]
        private struct Margins
        {
            public int Left, Right, Top, Bottom;
        }

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

        [DllImport("dwmapi.dll")]
        private static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref Margins margins);

        [StructLayout(LayoutKind.Sequential)]
        private struct NativeRect
        {
            public int Left, Top, Right, Bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MonitorInfo
        {
            public int Size;
            public NativeRect Monitor;
            public NativeRect Work;
            public uint Flags;
        }

        private const uint MONITOR_DEFAULTTONEAREST = 2;

        [DllImport("user32.dll")]
        private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

        [DllImport("user32.dll")]
        private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

        /// <summary>
        /// The full bounds of the monitor the window is on, taskbar included, in WPF units.
        /// Full screen has to size the window to this explicitly: a maximized window with custom
        /// chrome is confined to the work area, so maximizing left the taskbar showing.
        /// </summary>
        public static Rect GetMonitorBounds(Window window)
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
            if (hwnd == IntPtr.Zero || !GetMonitorInfo(MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST), ref info))
                return new Rect(0, 0, SystemParameters.PrimaryScreenWidth, SystemParameters.PrimaryScreenHeight);

            var pixels = new Rect(info.Monitor.Left, info.Monitor.Top,
                                  info.Monitor.Right - info.Monitor.Left, info.Monitor.Bottom - info.Monitor.Top);
            var toDip = PresentationSource.FromVisual(window)?.CompositionTarget?.TransformFromDevice;
            return toDip.HasValue ? Rect.Transform(pixels, toDip.Value) : pixels;
        }

        /// <summary>System backdrops (DWMWA_SYSTEMBACKDROP_TYPE) arrived in Windows 11 22H2.</summary>
        private static bool SupportsSystemBackdrop => Environment.OSVersion.Version.Build >= 22621;

        /// <summary>Applies the look once the window has a handle (immediately if it already has one).</summary>
        public static void Attach(Window window)
        {
            if (new WindowInteropHelper(window).Handle != IntPtr.Zero) Apply(window);
            else window.SourceInitialized += (_, _) => Apply(window);
        }

        private static void Apply(Window window)
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero) return;

            int on = 1;
            if (DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref on, sizeof(int)) != 0)
                DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE_BEFORE_20H1, ref on, sizeof(int));

            // Custom-chrome windows can lose the rounded corners Windows 11 gives normal ones.
            int corners = DWMWCP_ROUND;
            DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref corners, sizeof(int));

            if (!SupportsSystemBackdrop) return;

            var source = HwndSource.FromHwnd(hwnd);
            if (source?.CompositionTarget == null) return;

            var margins = new Margins { Left = -1, Right = -1, Top = -1, Bottom = -1 };
            if (DwmExtendFrameIntoClientArea(hwnd, ref margins) != 0) return;

            int backdrop = DWMSBT_MAINWINDOW;
            if (DwmSetWindowAttribute(hwnd, DWMWA_SYSTEMBACKDROP_TYPE, ref backdrop, sizeof(int)) != 0) return;

            // Only now, with the backdrop confirmed, let it show through.
            source.CompositionTarget.BackgroundColor = Colors.Transparent;
            window.Background = Brushes.Transparent;
        }

        /// <summary>
        /// Replaces the theme's accent brushes with shades of the user's Windows accent colour.
        /// Must run before any window loads; styles reference these with DynamicResource so the
        /// replacement reaches them.
        /// </summary>
        public static void ApplyAccent(ResourceDictionary resources)
        {
            var accent = ReadSystemAccent() ?? Color.FromRgb(0x00, 0x78, 0xD4);

            // Dark-mode Fluent fills with a LIGHTER tint of the accent and puts dark text on it;
            // the raw accent is too dark to read against a dark window.
            var fill = Mix(accent, Colors.White, 0.45);
            resources["AccentBrush"] = Frozen(fill);
            resources["AccentFill"] = Frozen(fill);
            resources["AccentFillHover"] = Frozen(Mix(accent, Colors.White, 0.36));
            resources["AccentFillPressed"] = Frozen(Mix(accent, Colors.White, 0.28));
            resources["AccentForeground"] = Frozen(Colors.Black);
        }

        private static Color? ReadSystemAccent()
        {
            try
            {
                // Stored as ABGR.
                if (Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\DWM", "AccentColor", null) is int abgr)
                {
                    return Color.FromRgb((byte)(abgr & 0xFF), (byte)((abgr >> 8) & 0xFF), (byte)((abgr >> 16) & 0xFF));
                }
            }
            catch
            {
                // Policy-restricted registry: fall back to the default Windows blue.
            }
            return null;
        }

        private static Color Mix(Color a, Color b, double amount) => Color.FromRgb(
            (byte)(a.R + (b.R - a.R) * amount),
            (byte)(a.G + (b.G - a.G) * amount),
            (byte)(a.B + (b.B - a.B) * amount));

        private static SolidColorBrush Frozen(Color c)
        {
            var brush = new SolidColorBrush(c);
            brush.Freeze();
            return brush;
        }
    }
}
