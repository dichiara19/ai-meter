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
            window.SourceInitialized += (sender, args) => ApplyBackdrop(window, 2, false);
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
