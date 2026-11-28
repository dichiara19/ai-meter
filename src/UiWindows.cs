using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shell;
using Microsoft.Win32;

namespace AIMeter
{
    internal partial class Ui
    {
        // ------------------------------------------------------------ Fluent windows
        /// <summary>Shared chrome for persistent settings and history windows, including the full Mica glass frame.</summary>
        public static void MakeSettingsWindow(Window window)
        {
            window.FontFamily = Theme.Font;
            window.UseLayoutRounding = true;
            WindowChrome.SetWindowChrome(window, new WindowChrome
            {
                CaptionHeight = 48, GlassFrameThickness = new Thickness(-1), ResizeBorderThickness = new Thickness(6),
                UseAeroCaptionButtons = true, CornerRadius = new CornerRadius(0),
            });
            window.SourceInitialized += (sender, args) =>
            {
                var source = HwndSource.FromHwnd(new WindowInteropHelper(window).Handle);
                // Added after WindowChrome's own hook, which it then precedes; the frame is recalculated at once.
                source.AddHook(KeepSideFrames);
                Native.SetWindowPos(source.Handle, IntPtr.Zero, 0, 0, 0, 0, Native.SWP_NOSIZE | Native.SWP_NOMOVE | Native.SWP_NOZORDER | Native.SWP_NOACTIVATE | Native.SWP_FRAMECHANGED);
                ApplyBackdrop(window, 2, false);
            };
        }

        /// <summary>
        /// Keep the system's side and bottom frames and give only the caption to the client area. WindowChrome
        /// makes the whole window client area, while DWM still places the caption buttons inside the frame it
        /// expects, which leaves a gap between the close button and the window edge. Maximised, the top keeps
        /// the frame's inset, as the sides do.
        /// </summary>
        private static IntPtr KeepSideFrames(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg != Native.WM_NCCALCSIZE || wParam == IntPtr.Zero) return IntPtr.Zero;
            // lParam starts with the proposed window rectangle: left, top, right, bottom.
            int left = System.Runtime.InteropServices.Marshal.ReadInt32(lParam, 0), top = System.Runtime.InteropServices.Marshal.ReadInt32(lParam, 4);
            Native.DefWindowProc(hwnd, msg, wParam, lParam);
            int frame = System.Runtime.InteropServices.Marshal.ReadInt32(lParam, 0) - left;
            System.Runtime.InteropServices.Marshal.WriteInt32(lParam, 4, Native.IsZoomed(hwnd) ? top + frame : top);
            handled = true;
            return IntPtr.Zero;
        }

        /// <summary>
        /// Apply native window chrome with DWM corners, shadows and a compositor-managed backdrop.
        /// Fall back to a solid background if Mica or Acrylic is unavailable.
        /// </summary>
        public static void MakeFluent(Window w, int backdrop, bool smallCorners, bool tool, bool noActivate)
        {
            w.WindowStyle = WindowStyle.SingleBorderWindow;
            w.ResizeMode = ResizeMode.NoResize;
            w.FontFamily = Theme.Font;
            w.UseLayoutRounding = true;
            TextOptions.SetTextFormattingMode(w, TextFormattingMode.Ideal);
            WindowChrome.SetWindowChrome(w, new WindowChrome
            {
                CaptionHeight = 0, GlassFrameThickness = new Thickness(-1), ResizeBorderThickness = new Thickness(0),
                CornerRadius = new CornerRadius(0), UseAeroCaptionButtons = false,
            });
            w.SourceInitialized += (o, e) =>
            {
                var hwnd = new WindowInteropHelper(w).Handle;
                if (tool) Native.AddExStyle(hwnd, Native.WS_EX_TOOLWINDOW | (noActivate ? Native.WS_EX_NOACTIVATE : 0), Native.WS_EX_APPWINDOW);
                // Disable the system menu so DWM does not overlay caption buttons on the content.
                int style = Native.GetWindowLong(hwnd, Native.GWL_STYLE);
                Native.SetWindowLong(hwnd, Native.GWL_STYLE, style & ~(Native.WS_SYSMENU | Native.WS_MINIMIZEBOX | Native.WS_MAXIMIZEBOX));
                ApplyBackdrop(w, backdrop, smallCorners);
            };
        }

        public static void ApplyBackdrop(Window w, int backdrop, bool smallCorners)
        {
            var hwnd = new WindowInteropHelper(w).Handle;
            if (hwnd == IntPtr.Zero) return;
            var src = HwndSource.FromHwnd(hwnd);
            Native.SetDwm(hwnd, Native.DWMWA_USE_IMMERSIVE_DARK_MODE, Theme.Dark ? 1 : 0);
            Native.SetDwm(hwnd, Native.DWMWA_WINDOW_CORNER_PREFERENCE, smallCorners ? 3 : 2);
            bool ok = Native.SetDwm(hwnd, Native.DWMWA_SYSTEMBACKDROP_TYPE, backdrop);
            if (ok && src != null && src.CompositionTarget != null)
            {
                src.CompositionTarget.BackgroundColor = Colors.Transparent;
                w.Background = Brushes.Transparent;
            }
            else w.SetResourceReference(Control.BackgroundProperty, "Fallback");
        }
    }
}
