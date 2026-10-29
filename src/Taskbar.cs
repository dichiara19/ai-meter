using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Automation;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;

namespace AIMeter
{
    /// <summary>How the primary monitor's taskbar is configured.</summary>
    internal sealed class TaskbarInfo
    {
        public Native.RECT Rect;     // Physical pixels.
        public int Edge;             // 0 left, 1 top, 2 right, 3 bottom.
        public bool Centred;        // Centred icons (the Windows 11 default).
        public bool WidgetsButton;   // Widgets (weather) button in the left corner.
        public bool AutoHide;
        public int TrayLeft;         // Left edge of the notification area, physical pixels (0 if unknown).

        public bool Horizontal { get { return Edge == 1 || Edge == 3; } }
        public bool Usable { get { return Horizontal && Rect.Right > Rect.Left; } }
        /// <summary>Recommended side: the left corner is free only with centred icons and no Widgets button.</summary>
        public string RecommendedSide { get { return Centred && !WidgetsButton ? "left" : "right"; } }

        public static TaskbarInfo Read()
        {
            var t = new TaskbarInfo();
            Native.TaskbarPos(out t.Rect, out t.Edge, out t.AutoHide);
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced"))
                {
                    object al = k == null ? null : k.GetValue("TaskbarAl");
                    object da = k == null ? null : k.GetValue("TaskbarDa");
                    t.Centred = al == null || Convert.ToInt32(al) != 0;      // Absent = centred.
                    t.WidgetsButton = da == null || Convert.ToInt32(da) != 0; // Absent = visible.
                }
            }
            catch (Exception) { t.Centred = true; t.WidgetsButton = true; }
            t.TrayLeft = Native.TrayLeft();
            return t;
        }

        /// <summary>Readable description for Settings.</summary>
        public string Describe()
        {
            if (!Horizontal) return L.T("Detected: taskbar on the side of the screen. The taskbar pill works only with the taskbar at the bottom or top.");

            return L.F("Detected: taskbar {0}, icons {1}, Widgets button {2}. Recommended position: {3}.",
                L.T(Edge == 3 ? "at the bottom" : "at the top"), L.T(Centred ? "centered" : "on the left"), L.T(WidgetsButton ? "visible" : "hidden"),
                L.T(RecommendedSide == "left" ? "left corner" : "next to the notification area"));
        }
    }

    /// <summary>
    /// Pill inside the taskbar. Windows 11 no longer has "deskbands", so the pill becomes a child window of the
    /// taskbar (Shell_TrayWnd): it rises and falls with an auto-hiding taskbar, disappears with it in full screen
    /// and never ends up beneath the taskbar when clicked. It is recreated if File Explorer restarts.
    /// Uses the Windows colour mode (the taskbar's), not the app mode.
    /// </summary>
    internal sealed class TaskbarPill
    {
        private readonly Controller C;
        private readonly Border root = new Border { Background = Brushes.Transparent };
        private readonly Button chip = new Button { Padding = new Thickness(10, 0, 10, 0), Cursor = Cursors.Hand, Focusable = false };
        private readonly DispatcherTimer reconcile = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        private HwndSource source;
        private IntPtr bar;
        private Native.RECT lastRect;
        private string placedKey;      // Taskbar, notification area and side at the last placement.
        private int px, py, pw, ph;    // Last applied position.
        private bool visible = true;
        public bool Dark { get; private set; }
        public bool OnLeft { get; private set; }
        public bool Usable { get; private set; }
        public bool IsVisible { get { return visible && source != null && Usable; } }

        public TaskbarPill(Controller c)
        {
            C = c;
            root.Child = chip;
            TextOptions.SetTextFormattingMode(root, TextFormattingMode.Display);
            ApplySystemTheme();
            // A real button, with hover highlighting like the taskbar buttons.
            chip.SetResourceReference(FrameworkElement.StyleProperty, "LinkButton");
            chip.FontFamily = Theme.Font;
            AutomationProperties.SetName(chip, L.T("AI Meter: open the panel"));
            chip.Click += (o, e) => C.ToggleFlyoutFromTaskbar(this);
            chip.MouseRightButtonUp += (o, e) => { C.ShowMenu(null); e.Handled = true; };
            // Lightweight check: taskbar recreated (File Explorer restarted), moved or resized.
            reconcile.Tick += (o, e) => Reconcile();
        }

        public void Show() { visible = true; Reconcile(); reconcile.Start(); }

        public void Hide()
        {
            visible = false;
            reconcile.Stop();
            if (source != null) Native.ShowWindow(source.Handle, 0);
        }

        public void Close()
        {
            reconcile.Stop();
            if (source != null) { source.RootVisual = null; source.Dispose(); source = null; }
        }

        public void ApplySystemTheme()
        {
            Dark = Theme.TaskbarIsDark();
            var pal = Theme.BuildPalette(Dark);
            var hover = new SolidColorBrush(Dark ? Color.FromArgb(0x15, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x0F, 0, 0, 0));
            hover.Freeze();
            pal["SubtleHover"] = hover;   // Same highlighting as the taskbar buttons.
            root.Resources.MergedDictionaries.Clear();
            root.Resources.MergedDictionaries.Add(pal);
        }

        public void Render(Snapshot s, string metric)
        {
            chip.Content = Ui.PillRow(s, metric, Dark);
            chip.ToolTip = Ui.Summary(s) + "\n" + L.T("Click for details · right-click for options");
            Place();
        }

        /// <summary>Creates the child window inside the current taskbar (also after File Explorer restarts).</summary>
        private void Attach()
        {
            Close();
            bar = Native.FindTaskbar();
            if (bar == IntPtr.Zero) return;
            var prm = new HwndSourceParameters("AI Meter - taskbar")
            {
                ParentWindow = bar,
                WindowStyle = Native.WS_CHILD | Native.WS_VISIBLE | Native.WS_CLIPSIBLINGS,
                UsesPerPixelTransparency = true,   // Layered child window: transparent background over the taskbar.
                PositionX = 0, PositionY = 0,
            };
            // The taskbar may refuse the child window while it is being recreated (sign-in, resume, File Explorer
            // restart): "Access denied". It is retried at the next check, 2 seconds later, without closing the app.
            try { source = new HwndSource(prm) { SizeToContent = SizeToContent.WidthAndHeight, RootVisual = root }; }
            catch (System.ComponentModel.Win32Exception ex)
            {
                Program.LogError("taskbar pill", ex);
                source = null;
                bar = IntPtr.Zero;
                return;
            }
            source.CompositionTarget.BackgroundColor = Colors.Transparent;
            source.SizeToContentChanged += (o, e) => Place();
            lastRect = default(Native.RECT);
        }

        private void Reconcile()
        {
            if (!visible) return;
            if (source == null || bar == IntPtr.Zero || !Native.IsWindow(bar) || bar != Native.FindTaskbar()) { Attach(); placedKey = null; }
            // A check of a few microseconds: the pill moves only if the taskbar or notification area changed.
            if (Key() != placedKey) Place();
        }

        private string Key() { return Native.RectKey(bar) + "|" + Native.TrayLeft() + "|" + C.Settings.TaskbarSide; }

        /// <summary>Places the pill in the free space of the primary monitor's taskbar.</summary>
        public void Place()
        {
            if (source == null || !visible) return;
            var info = TaskbarInfo.Read();
            Usable = info.Usable;
            if (!Usable) { Native.ShowWindow(source.Handle, 0); placedKey = null; return; }
            double scale = source.CompositionTarget.TransformToDevice.M11;

            int barH = info.Rect.Bottom - info.Rect.Top;
            double chipH = Math.Max(24, Math.Floor(barH / scale) - 8);   // Like the taskbar buttons: 40 out of 48.
            if (double.IsNaN(chip.Height) || Math.Abs(chip.Height - chipH) > 0.5) { chip.Height = chipH; root.UpdateLayout(); }

            int w = (int)Math.Round(root.ActualWidth * scale), h = (int)Math.Round(root.ActualHeight * scale);
            string side = C.Settings.TaskbarSide == "left" || C.Settings.TaskbarSide == "right" ? C.Settings.TaskbarSide : info.RecommendedSide;
            OnLeft = side == "left";
            int gap = (int)Math.Round(8 * scale);
            int x;
            if (OnLeft) x = (int)Math.Round(12 * scale);
            else if (info.TrayLeft > info.Rect.Left) x = info.TrayLeft - info.Rect.Left - w - gap;
            else x = info.Rect.Right - info.Rect.Left - w - (int)Math.Round(320 * scale);   // Notification area not found: conservative estimate.
            int y = (barH - h) / 2;
            // Coordinates relative to the taskbar; on top of the taskbar's other child windows.
            if (x != px || y != py || w != pw || h != ph || placedKey == null)
            {
                Native.SetWindowPos(source.Handle, IntPtr.Zero, x, y, w, h, Native.SWP_NOACTIVATE);
                Native.ShowWindow(source.Handle, 4 /* SW_SHOWNOACTIVATE */);
                px = x; py = y; pw = w; ph = h;
            }
            lastRect = info.Rect;
            placedKey = Key();
        }

        /// <summary>Theme or taskbar changed (Windows mode, alignment, Widgets button, resolution).</summary>
        public void Refresh(Snapshot s, string metric)
        {
            ApplySystemTheme();
            placedKey = null;
            if (s != null) Render(s, metric);
            Reconcile();
        }
    }
}