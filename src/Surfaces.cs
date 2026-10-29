using System;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace AIMeter
{
    /// <summary>Desktop widget: always stays below other windows, as part of the wallpaper.</summary>
    internal sealed class DesktopWidget : Window
    {
        private readonly Controller C;
        private readonly StackPanel body = new StackPanel();
        private readonly TextBlock status;

        public DesktopWidget(Controller c)
        {
            C = c;
            Title = "AI Meter - widget";
            Width = 316;
            SizeToContent = SizeToContent.Height;
            ShowInTaskbar = false;
            ShowActivated = false;
            Ui.MakeFluent(this, 2, false, true, false);

            var root = new DockPanel { Margin = new Thickness(10, 4, 10, 10) };
            var header = new DockPanel { Height = 34, Background = Brushes.Transparent, Cursor = Cursors.SizeAll };
            var more = Ui.IconButton("", L.T("More options"), (o, e) => C.ShowMenu(this));
            more.Cursor = Cursors.Arrow;
            DockPanel.SetDock(more, Dock.Right);
            header.Children.Add(more);
            var title = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 0, 0) };
            title.Children.Add(Ui.Text("AI Meter", 12, "Text2", FontWeights.SemiBold));
            status = Ui.Text("", 11.5, "Text3");
            status.Margin = new Thickness(8, 0, 0, 0);
            title.Children.Add(status);
            header.Children.Add(title);
            header.MouseLeftButtonDown += (o, e) =>
            {
                if (e.OriginalSource is TextBlock && ((TextBlock)e.OriginalSource).FontFamily == Theme.Icons) return;
                DragMove();
                C.Settings.DesktopX = Left; C.Settings.DesktopY = Top; C.Settings.Save();
            };
            DockPanel.SetDock(header, Dock.Top);
            root.Children.Add(header);
            root.Children.Add(body);
            Content = root;

            SourceInitialized += (o, e) => HwndSource.FromHwnd(new WindowInteropHelper(this).Handle).AddHook(StayAtBottom);
            Loaded += (o, e) => Place();
        }

        private IntPtr StayAtBottom(IntPtr hwnd, int msg, IntPtr w, IntPtr l, ref bool handled)
        {
            if (msg == Native.WM_WINDOWPOSCHANGING)
            {
                var pos = (Native.WINDOWPOS)Marshal.PtrToStructure(l, typeof(Native.WINDOWPOS));
                pos.hwndInsertAfter = Native.HWND_BOTTOM;
                pos.flags &= ~Native.SWP_NOZORDER;
                Marshal.StructureToPtr(pos, l, false);
            }
            return IntPtr.Zero;
        }

        public void Place()
        {
            var wa = SystemParameters.WorkArea;
            double x = C.Settings.DesktopX, y = C.Settings.DesktopY;
            bool valid = x > -1e9 && x > SystemParameters.VirtualScreenLeft - 50 && x < SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - 80
                         && y > SystemParameters.VirtualScreenTop - 10 && y < SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - 60;
            Left = valid ? x : wa.Right - Width - 24;
            Top = valid ? y : wa.Top + 24;
        }

        public void ResetPosition()
        {
            C.Settings.DesktopX = double.MinValue; C.Settings.DesktopY = double.MinValue; C.Settings.Save();
            Place();
        }

        public void Render(Snapshot s)
        {
            body.Children.Clear();
            if (s.Providers.Count == 0) { body.Children.Add(EmptyState()); return; }
            foreach (var p in s.Providers)
            {
                if (p.IsIdleLocal) continue;   // Local models: show the card only when there is something to display.
                string id = p.Id;
                string list = id + ".sessions";
                var card = Ui.ProviderCard(p, C.IsExpanded(id), () => C.ToggleExpanded(id), C.Login, C.IsExpanded(list), () => C.ToggleExpanded(list));
                card.Margin = new Thickness(0, body.Children.Count == 0 ? 0 : 8, 0, 0);
                body.Children.Add(card);
            }
            status.Text = "· " + s.Taken.ToString("HH:mm");
        }

        public static FrameworkElement EmptyState()
        {
            var t = Ui.Text(L.T("No services enabled. Turn them on in Settings."), 12, "Text2");
            t.TextWrapping = TextWrapping.Wrap;
            return Ui.Card(t, new Thickness(14));
        }
    }

    /// <summary>
    /// Floating pill: tiny, never steals focus, hides during full-screen use.
    /// Two micro-buttons: minimise to an icon (live rings) and close. One click on the icon reopens it.
    /// </summary>
    internal sealed class OverlayPill : Window
    {
        private readonly Controller C;
        private readonly Border host = new Border();
        private readonly DispatcherTimer hover = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(380) };

        public OverlayPill(Controller c)
        {
            C = c;
            Title = "AI Meter - overlay";
            // No SizeToContent: with WindowChrome, WPF adds the system border and leaves an empty strip.
            // The size is computed here from the content (see Fit).
            SizeToContent = SizeToContent.Manual;
            Width = 120; Height = 36;
            Topmost = true;
            ShowInTaskbar = false;
            ShowActivated = false;
            Ui.MakeFluent(this, 2, false, true, true);
            Content = host;
            Cursor = Cursors.Hand;
            host.HorizontalAlignment = HorizontalAlignment.Center;
            host.VerticalAlignment = VerticalAlignment.Center;

            hover.Tick += (o, e) => { hover.Stop(); C.ShowDetails(this); };
            MouseEnter += (o, e) => { C.CancelHideDetails(); hover.Start(); };
            MouseLeave += (o, e) => { hover.Stop(); C.ScheduleHideDetails(); };
            MouseLeftButtonUp += (o, e) => { hover.Stop(); C.ShowDetails(this); };
            MouseRightButtonUp += (o, e) => { hover.Stop(); C.ShowMenu(this); e.Handled = true; };
        }

        public void Render(Snapshot s, string metric)
        {
            host.Child = C.Settings.OverlayCollapsed ? Collapsed(s) : Expanded(s, metric);
            Fit();
        }

        private FrameworkElement Collapsed(Snapshot s)
        {
            var five = s.Providers.Where(p => p.Models == null).Select(p => Tuple.Create(p, Ui.Primary(p))).ToList();
            var icon = Ui.RingIcon(five, 24);
            // A real button: click, keyboard and accessibility tools all expand it again in the same way.
            var b = new Button { Content = icon, Padding = new Thickness(6), Cursor = Cursors.Hand };
            b.SetResourceReference(FrameworkElement.StyleProperty, "LinkButton");
            System.Windows.Automation.AutomationProperties.SetName(b, L.T("Expand AI Meter"));
            b.ToolTip = Ui.Summary(s) + "\n" + L.T("Click to expand");
            b.Click += (o, e) => { hover.Stop(); C.SetOverlayCollapsed(false); };
            return b;
        }

        private FrameworkElement Expanded(Snapshot s, string metric)
        {
            var outer = new StackPanel { Orientation = Orientation.Horizontal };
            var row = Ui.PillRow(s, metric, null);
            row.Margin = new Thickness(10, 6, 8, 6);
            outer.Children.Add(row);

            // Column of two micro-buttons, separated by a thin line.
            var div = new Border { Width = 1, Margin = new Thickness(0, 6, 0, 6) };
            div.SetResourceReference(Border.BackgroundProperty, "Divider");
            outer.Children.Add(div);
            var buttons = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(3, 2, 3, 2) };
            buttons.Children.Add(MiniButton("\uE921", L.T("Minimize to icon"), () => C.SetOverlayCollapsed(true)));
            buttons.Children.Add(MiniButton("\uE8BB", L.T("Close the overlay (turn it back on from the icon menu)"), C.CloseOverlay));
            outer.Children.Add(buttons);
            return outer;
        }

        private Button MiniButton(string glyph, string tip, Action click)
        {
            var b = Ui.IconButton(glyph, tip, (o, e) => { hover.Stop(); click(); });
            b.Width = 18; b.Height = 15; b.FontSize = 8;
            b.Cursor = Cursors.Arrow;
            System.Windows.Automation.AutomationProperties.SetName(b, tip);
            return b;
        }

        /// <summary>
        /// Sizes the window exactly to its content and re-anchors it to the chosen corner.
        /// Windows does not go below a minimum height for framed windows: the minimised icon becomes
        /// a square of that side, with the rings centred.
        /// </summary>
        private void Fit()
        {
            host.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            double w = Math.Ceiling(host.DesiredSize.Width);
            double h = Math.Max(Math.Ceiling(host.DesiredSize.Height), Math.Ceiling(SystemParameters.MinimumWindowHeight));
            if (C.Settings.OverlayCollapsed) w = h = Math.Max(w, h);
            Width = w;
            Height = h;
            Place();
        }

        public void Place()
        {
            var wa = SystemParameters.WorkArea;
            string c = C.Settings.OverlayCorner ?? "tr";
            const double m = 12;
            Left = c.EndsWith("l") ? wa.Left + m : wa.Right - Width - m;
            Top = c.StartsWith("b") ? wa.Bottom - Height - m : wa.Top + m;
        }
    }

    /// <summary>Flyout panel: from the notification area (with focus) or when hovering over the pill (without).</summary>
    internal sealed class Flyout : Window
    {
        private readonly Controller C;
        private readonly StackPanel body = new StackPanel();
        private readonly TextBlock footer;
        private readonly TranslateTransform slide = new TranslateTransform();
        public bool HoverMode { get; private set; }
        /// <summary>Opened from the taskbar pill: without focus, it closes when another app comes to the foreground.</summary>
        public bool TaskbarMode { get; private set; }
        /// <summary>Bottom edge it stays anchored to when opened above the taskbar: if the content changes height,
        /// it grows or shrinks upwards instead of detaching from the taskbar. Null when it opens downwards.</summary>
        private double? bottomEdge;

        public Flyout(Controller c)
        {
            C = c;
            Title = "AI Meter - panel";
            Width = 344;
            SizeToContent = SizeToContent.Height;
            Topmost = true;
            ShowInTaskbar = false;
            Ui.MakeFluent(this, 3, false, true, false);

            var root = new DockPanel { Margin = new Thickness(12, 10, 12, 12), RenderTransform = slide };
            var header = new DockPanel { Height = 36 };
            var settings = Ui.IconButton("", L.T("Settings"), (o, e) => { Hide(); C.OpenSettings(); });
            var refresh = Ui.IconButton("", L.T("Refresh now"), (o, e) => C.RefreshNow());
            DockPanel.SetDock(settings, Dock.Right);
            DockPanel.SetDock(refresh, Dock.Right);
            header.Children.Add(settings);
            header.Children.Add(refresh);
            var title = Ui.Text("AI Meter", 14, "Text", FontWeights.SemiBold);
            title.FontFamily = Theme.DisplayFont;
            title.VerticalAlignment = VerticalAlignment.Center;
            title.Margin = new Thickness(4, 0, 0, 0);
            header.Children.Add(title);
            DockPanel.SetDock(header, Dock.Top);
            root.Children.Add(header);

            footer = Ui.Text("", 11.5, "Text3");
            footer.Margin = new Thickness(4, 10, 4, 0);
            footer.TextWrapping = TextWrapping.Wrap;
            DockPanel.SetDock(footer, Dock.Bottom);
            root.Children.Add(footer);

            var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = body, Focusable = false };
            root.Children.Add(scroll);
            Content = root;

            Deactivated += (o, e) => { if (!HoverMode) Hide(); };
            IsVisibleChanged += (o, e) => { if (!IsVisible) TaskbarMode = false; };
            SizeChanged += (o, e) => { if (IsVisible && e.HeightChanged && bottomEdge.HasValue) Top = bottomEdge.Value - e.NewSize.Height; };
            MouseEnter += (o, e) => C.CancelHideDetails();
            MouseLeave += (o, e) => { if (HoverMode) C.ScheduleHideDetails(); };
        }

        public void Render(Snapshot s)
        {
            body.Children.Clear();
            if (s.Providers.Count == 0) body.Children.Add(DesktopWidget.EmptyState());
            foreach (var p in s.Providers)
            {
                if (p.IsIdleLocal) continue;   // Local models: show the card only when there is something to display.
                var card = Ui.ProviderCard(p, true, null, C.Login);
                card.Margin = new Thickness(0, body.Children.Count == 0 ? 0 : 8, 0, 0);
                body.Children.Add(card);
            }
            // Stale data is flagged on the service card, not here.
            footer.Text = L.F("Updated at {0}", s.Taken.ToString("HH:mm", L.Culture));
        }

        /// <summary>Opening from the notification area, above the taskbar, like the system flyouts.</summary>
        public void ShowFromTray()
        {
            HoverMode = false;
            MaxHeight = SystemParameters.WorkArea.Height - 24;
            ShowActivated = true;
            Show();
            UpdateLayout();
            var wa = SystemParameters.WorkArea;
            int edge = Native.TaskbarEdge();
            Left = edge == 0 ? wa.Left + 12 : wa.Right - ActualWidth - 12;
            bottomEdge = edge == 1 ? (double?)null : wa.Bottom - 12;
            Top = edge == 1 ? wa.Top + 12 : wa.Bottom - ActualHeight - 12;
            Activate();
            Native.SetForegroundWindow(new WindowInteropHelper(this).Handle);
            Animate(edge == 1 ? -16 : 16);
        }

        /// <summary>Opening next to the pill, without taking focus.</summary>
        public void ShowNear(Window anchor)
        {
            HoverMode = true;
            MaxHeight = SystemParameters.WorkArea.Height - 24;
            ShowActivated = false;
            Show();
            UpdateLayout();
            var wa = SystemParameters.WorkArea;
            bool below = anchor.Top < wa.Top + wa.Height / 2;
            Left = Math.Max(wa.Left + 12, Math.Min(anchor.Left + anchor.ActualWidth - ActualWidth, wa.Right - ActualWidth - 12));
            bottomEdge = below ? (double?)null : anchor.Top - 8;
            Top = below ? anchor.Top + anchor.ActualHeight + 8 : anchor.Top - ActualHeight - 8;
            Animate(below ? -8 : 8);
        }

        /// <summary>
        /// Opening from the taskbar pill: anchored above the taskbar, on the pill's side,
        /// it slides in horizontally like the Windows Widgets panel.
        /// </summary>
        public void ShowFromTaskbar(bool fromLeft)
        {
            HoverMode = false;
            MaxHeight = SystemParameters.WorkArea.Height - 24;
            // No activation: the taskbar pill does not take focus, so Windows might deny it to the panel too.
            // It closes when clicking elsewhere (another app comes to the foreground) or on the pill again.
            ShowActivated = false;
            Show();
            TaskbarMode = true;
            UpdateLayout();
            var wa = SystemParameters.WorkArea;
            Left = fromLeft ? wa.Left + 12 : wa.Right - ActualWidth - 12;
            bottomEdge = wa.Bottom - 12;
            Top = wa.Bottom - ActualHeight - 12;
            slide.BeginAnimation(TranslateTransform.YProperty, null);
            slide.Y = 0;
            Animate(fromLeft ? -28 : 28, TranslateTransform.XProperty);
        }

        private void Animate(double from) { slide.BeginAnimation(TranslateTransform.XProperty, null); slide.X = 0; Animate(from, TranslateTransform.YProperty); }

        private void Animate(double from, DependencyProperty axis)
        {
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            slide.BeginAnimation(axis, new DoubleAnimation(from, 0, TimeSpan.FromMilliseconds(240)) { EasingFunction = ease });
            ((UIElement)Content).BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(140)));
        }
    }
}
