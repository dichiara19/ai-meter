using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using IOPath = System.IO.Path;

namespace AIMeter
{
    /// <summary>
    /// README images composed from actual UI controls and synthetic data: panels in both themes,
    /// taskbar pill, notifications, social preview and GIF frames. Run tools\render-media.ps1.
    /// </summary>
    public static class MediaRenderer
    {
        private static Settings S;
        internal static BitmapSource wallpaper, wallpaperBlur;
        internal const int DeskW = 1920, DeskH = 1080, BarH = 48;
        // Use the current minute because relative reset times depend on the actual clock.
        private static readonly DateTime Now = new DateTime(DateTime.Now.Year, DateTime.Now.Month, DateTime.Now.Day, DateTime.Now.Hour, DateTime.Now.Minute, 0);

        public static string Run(string outDir, string framesDir, string lang)
        {
            Prepare(lang);
            Directory.CreateDirectory(outDir);
            Directory.CreateDirectory(framesDir);
            var snap = DemoData.Build(Now);

            foreach (var dark in new[] { true, false })
            {
                SetTheme(dark);
                Save(PanelShot(snap, dark), IOPath.Combine(outDir, dark ? "panel-dark.png" : "panel-light.png"), 2);
            }
            SetTheme(true);
            Save(TaskbarShot(snap), IOPath.Combine(outDir, "taskbar.png"), 2);
            Save(HeroTaskbar(snap, 900, 300, true), IOPath.Combine(outDir, "hero-taskbar.png"), 2);
            Save(ToastShot(snap), IOPath.Combine(outDir, "notification.png"), 2);
            Save(SocialPreview(snap), IOPath.Combine(outDir, "social-preview.png"), 1);
            int frames = GifFrames(snap, framesDir);
            int appFrames = AppsMedia(outDir, IOPath.Combine(framesDir, "apps"));
            return "ok, " + frames + " + " + appFrames + " frames";
        }

        /// <summary>Start WPF once, then select the language and the dark theme; the backdrops are generated on first use.</summary>
        internal static void Prepare(string lang)
        {
            if (Application.Current == null) new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            S = new Settings { Language = lang };
            L.Init(lang);
            SetTheme(true);
            if (wallpaper == null) wallpaper = Wallpaper(false);
            if (wallpaperBlur == null) wallpaperBlur = Wallpaper(true);
        }

        internal static void SetTheme(bool dark)
        {
            S.Theme = dark ? "dark" : "light";
            Theme.Apply(S);
        }

        // Desktop backdrop.
        /// <summary>Generate an abstract Windows-style backdrop locally, without third-party imagery.</summary>
        internal static BitmapSource Wallpaper(bool blurred)
        {
            var c = new Canvas { Width = DeskW, Height = DeskH, ClipToBounds = true };
            c.Background = new LinearGradientBrush(Color.FromRgb(0x08, 0x0C, 0x1C), Color.FromRgb(0x14, 0x1B, 0x3A), 35);
            Action<double, double, double, double, Color> blob = (x, y, w, h, col) =>
            {
                var e = new Ellipse { Width = w, Height = h, Fill = new RadialGradientBrush(col, Color.FromArgb(0, col.R, col.G, col.B)) };
                Canvas.SetLeft(e, x); Canvas.SetTop(e, y);
                c.Children.Add(e);
            };
            blob(900, -200, 1300, 1100, Color.FromArgb(0xD0, 0x25, 0x5B, 0xF5));
            blob(1250, 300, 900, 900, Color.FromArgb(0xB0, 0x7B, 0x4D, 0xFF));
            blob(400, 450, 1100, 800, Color.FromArgb(0x90, 0x0F, 0x8F, 0xB8));
            blob(-200, -150, 900, 700, Color.FromArgb(0x70, 0x3A, 0x2A, 0x9E));
            blob(1500, 650, 700, 600, Color.FromArgb(0x90, 0xD9, 0x77, 0x57));
            c.Effect = new BlurEffect { Radius = blurred ? 140 : 60, KernelType = KernelType.Gaussian };
            // Render beyond the crop to avoid light edges introduced by the blur.
            const int pad = 200;
            var host = new Canvas { Width = DeskW + 2 * pad, Height = DeskH + 2 * pad, Background = c.Background };
            c.Width = DeskW + 2 * pad; c.Height = DeskH + 2 * pad;
            foreach (UIElement e in c.Children) { Canvas.SetLeft(e, Canvas.GetLeft(e) + pad); Canvas.SetTop(e, Canvas.GetTop(e) + pad); }
            host.Children.Add(c);
            var big = Render(host, DeskW + 2 * pad, DeskH + 2 * pad, 1);
            var crop = new CroppedBitmap(big, new Int32Rect(pad, pad, DeskW, DeskH));
            crop.Freeze();
            return crop;
        }

        internal static Image Crop(BitmapSource src, Rect r)
        {
            var cb = new CroppedBitmap(src, new Int32Rect((int)r.X, (int)r.Y, (int)r.Width, (int)r.Height));
            return new Image { Source = cb, Width = r.Width, Height = r.Height, Stretch = Stretch.Fill };
        }

        // Shared scene components.
        /// <summary>Compose the actual flyout controls with a simulated compositor backdrop.</summary>
        private static FrameworkElement Panel(Snapshot s, bool dark, Rect onDesk)
        {
            TranslateTransform scroll;
            double maxScroll;
            return Panel(s, dark, onDesk, double.PositiveInfinity, out scroll, out maxScroll);
        }

        /// <summary>
        /// Like the real flyout, a panel taller than maxHeight keeps its header and footer and scrolls its cards:
        /// moving <paramref name="scroll"/> up by at most <paramref name="maxScroll"/> pixels reveals the rest.
        /// </summary>
        internal static FrameworkElement Panel(Snapshot s, bool dark, Rect onDesk, double maxHeight, out TranslateTransform scroll, out double maxScroll)
        {
            var root = new DockPanel { Margin = new Thickness(12, 10, 12, 12) };
            var header = new DockPanel { Height = 36 };
            var settings = Ui.IconButton("", L.T("Settings"), (o, e) => { });
            var refresh = Ui.IconButton("", L.T("Refresh now"), (o, e) => { });
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
            var footer = Ui.Text(L.F("Updated at {0}", Now.ToString("HH:mm", L.Culture)), 11.5, "Text3");
            footer.Margin = new Thickness(4, 10, 4, 0);
            DockPanel.SetDock(footer, Dock.Bottom);
            root.Children.Add(footer);
            var body = new StackPanel();
            foreach (var p in s.Providers)
            {
                var card = Ui.ProviderCard(p, true, null, x => { });
                card.Margin = new Thickness(0, body.Children.Count == 0 ? 0 : 8, 0, 0);
                body.Children.Add(card);
            }
            scroll = new TranslateTransform();
            body.RenderTransform = scroll;
            var viewport = new Border { Child = body, ClipToBounds = true };
            root.Children.Add(viewport);
            root.Measure(new Size(344, double.PositiveInfinity));
            maxScroll = Math.Max(0, root.DesiredSize.Height - maxHeight);
            if (maxScroll > 0)
            {
                viewport.Height = body.DesiredSize.Height - maxScroll;
                // A canvas arranges the cards at their full height, so WPF's layout clip does not cut them at the viewport.
                double bodyWidth = body.DesiredSize.Width;
                viewport.Child = null;
                body.Width = bodyWidth;
                var canvas = new Canvas();
                canvas.Children.Add(body);
                viewport.Child = canvas;
                // Measure would otherwise reuse the cached size: the root's constraint is unchanged.
                root.InvalidateMeasure();
            }
            return Glass(root, dark, onDesk, 344);
        }

        /// <summary>Simulate acrylic with the blurred desktop, tint, border, rounded corners and shadow.</summary>
        private static FrameworkElement Glass(FrameworkElement content, bool dark, Rect onDesk, double width)
        {
            content.Measure(new Size(width, double.PositiveInfinity));
            double h = content.DesiredSize.Height;
            var grid = new Grid { Width = width, Height = h };
            var clip = new RectangleGeometry(new Rect(0, 0, width, h), 8, 8);
            var behind = Crop(wallpaperBlur, new Rect(Math.Max(0, onDesk.X), Math.Max(0, Math.Min(DeskH - h, onDesk.Y)), width, Math.Min(h, DeskH)));
            behind.Height = h;
            behind.Clip = clip;
            grid.Children.Add(behind);
            grid.Children.Add(new Border { Background = new SolidColorBrush(dark ? Color.FromArgb(0xE0, 0x2C, 0x2C, 0x2C) : Color.FromArgb(0xE0, 0xF3, 0xF3, 0xF3)), CornerRadius = new CornerRadius(8) });
            grid.Children.Add(content);
            grid.Children.Add(new Border { BorderBrush = new SolidColorBrush(dark ? Color.FromArgb(0x60, 0x00, 0x00, 0x00) : Color.FromArgb(0x1F, 0x00, 0x00, 0x00)), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8) });
            grid.Effect = new DropShadowEffect { BlurRadius = 32, ShadowDepth = 8, Direction = 270, Opacity = dark ? 0.55 : 0.28, Color = Colors.Black };
            return grid;
        }

        /// <summary>The taskbar pill uses the actual logo and percentage row in a 40-pixel container.</summary>
        private static FrameworkElement Pill(Snapshot s, bool hover)
        {
            var row = Ui.PillRow(s, "5h", true);
            var chip = new Border { Height = 40, Padding = new Thickness(10, 0, 10, 0), CornerRadius = new CornerRadius(4), Child = row, VerticalAlignment = VerticalAlignment.Center };
            chip.Background = hover ? new SolidColorBrush(Color.FromArgb(0x15, 0xFF, 0xFF, 0xFF)) : Brushes.Transparent;
            return chip;
        }

        /// <summary>Compose the taskbar corner with the pill, system icons and clock.</summary>
        internal static FrameworkElement Taskbar(Snapshot s, double width, bool hover)
        {
            var bar = new DockPanel { Width = width, Height = BarH, LastChildFill = false };
            bar.Background = new SolidColorBrush(Color.FromArgb(0xF0, 0x1C, 0x1C, 0x1C));
            var clock = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 6, 0) };
            var time = Plain(Now.ToString("HH:mm", L.Culture), 12); time.HorizontalAlignment = HorizontalAlignment.Right;
            var date = Plain(Now.ToString("d", L.Culture), 12); date.HorizontalAlignment = HorizontalAlignment.Right;
            clock.Children.Add(time); clock.Children.Add(date);
            var bell = Icon("", 15); bell.Margin = new Thickness(6, 0, 12, 0);
            DockPanel.SetDock(bell, Dock.Right); bar.Children.Add(bell);
            DockPanel.SetDock(clock, Dock.Right); bar.Children.Add(clock);
            var tray = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 4, 0) };
            foreach (var g in new[] { "", "", "" }) { var i = Icon(g, 15); i.Margin = new Thickness(5, 0, 5, 0); tray.Children.Add(i); }
            DockPanel.SetDock(tray, Dock.Right); bar.Children.Add(tray);
            var chev = Icon("", 12); chev.Margin = new Thickness(6, 0, 8, 0);
            DockPanel.SetDock(chev, Dock.Right); bar.Children.Add(chev);
            var pill = Pill(s, hover); pill.Margin = new Thickness(0, 0, 4, 0);
            DockPanel.SetDock(pill, Dock.Right); bar.Children.Add(pill);
            var top = new Border { Height = 1, VerticalAlignment = VerticalAlignment.Top, Background = new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF)) };
            var g2 = new Grid { Width = width, Height = BarH };
            g2.Children.Add(bar);
            g2.Children.Add(top);
            // Match the dark taskbar palette used by TaskbarPill.
            g2.Resources.MergedDictionaries.Add(Theme.BuildPalette(true));
            return g2;
        }

        internal static TextBlock Plain(string t, double size) { return new TextBlock { Text = t, FontSize = size, Foreground = Brushes.White, FontFamily = Theme.Font }; }
        private static TextBlock Icon(string g, double size) { return new TextBlock { Text = g, FontSize = size, Foreground = Brushes.White, FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), VerticalAlignment = VerticalAlignment.Center }; }

        /// <summary>Reconstruct a Windows notification card with the app name and icon.</summary>
        internal static FrameworkElement Toast(Snapshot s, Rect onDesk)
        {
            var claude = s.Providers[0].Usage.Windows[0];
            var other = Ui.Primary(s.Providers[1].Usage);
            var st = new StackPanel { Margin = new Thickness(16, 12, 16, 16) };
            var head = new DockPanel { Margin = new Thickness(0, 0, 0, 10) };
            var x = Icon("", 11); x.Opacity = 0.7; DockPanel.SetDock(x, Dock.Right); head.Children.Add(x);
            var more = Icon("", 13); more.Opacity = 0.7; more.Margin = new Thickness(0, 0, 18, 0); DockPanel.SetDock(more, Dock.Right); head.Children.Add(more);
            var logo = new Image { Source = AppIcon(), Width = 16, Height = 16, Margin = new Thickness(0, 0, 10, 0) };
            DockPanel.SetDock(logo, Dock.Left); head.Children.Add(logo);
            var name = Plain("AI Meter", 12); name.Opacity = 0.85; head.Children.Add(name);
            st.Children.Add(head);
            var title = Plain(L.F("{0} · {1}: {2}% left", "Claude", Ui.LabelOf(claude), Math.Round(claude.Remaining)), 14); title.FontWeight = FontWeights.SemiBold;
            st.Children.Add(title);
            var body = Plain(L.F("Resets {0}.", Alerts.When(claude.ResetsAt.Value)) + " " + L.F("At this pace it runs out {0}.", Alerts.When(claude.RunsOutAt.Value)) + " " + L.F("{0} still has {1}% left.", "ChatGPT", Math.Round(other.Remaining)), 13);
            body.Opacity = 0.75; body.TextWrapping = TextWrapping.Wrap; body.Margin = new Thickness(0, 4, 0, 0);
            st.Children.Add(body);
            return Glass(st, true, onDesk, 364);
        }

        internal static BitmapSource AppIcon()
        {
            var dec = new IconBitmapDecoder(new Uri(IOPath.Combine(Program.Home, @"assets\app.ico")), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            return dec.Frames.OrderByDescending(f => f.PixelWidth).First();
        }

        private static System.Windows.Shapes.Path Cursor()
        {
            var p = new System.Windows.Shapes.Path
            {
                Data = Geometry.Parse("M0,0 L0,17 L4.5,13 L7.5,20 L10,19 L7,12 L12.5,12 Z"),
                Fill = Brushes.White, Stroke = Brushes.Black, StrokeThickness = 1, StrokeLineJoin = PenLineJoin.Round,
            };
            p.Effect = new DropShadowEffect { BlurRadius = 4, ShadowDepth = 1, Opacity = 0.4 };
            return p;
        }

        // Scene composition.
        /// <summary>Place the actual dashboard on the same synthetic desktop used by the existing release images.</summary>
        internal static FrameworkElement HistoryShot(FrameworkElement content, bool dark)
        {
            return WindowShot(content, dark, 1160, 860, "Usage history · Sample data");
        }

        /// <summary>
        /// Frame a window's content with a Mica-like backdrop and caption buttons on the synthetic desktop,
        /// with a caption below it naming the screen and stating that the data is invented.
        /// </summary>
        private static Canvas WindowShot(FrameworkElement content, bool dark, int windowWidth, int windowHeight, string what)
        {
            if (wallpaper == null) wallpaper = Wallpaper(false);
            if (wallpaperBlur == null) wallpaperBlur = Wallpaper(true);
            int sceneWidth = windowWidth + 120, sceneHeight = windowHeight + 140;
            var scene = new Canvas { Width = sceneWidth, Height = sceneHeight, ClipToBounds = true };
            scene.Children.Add(Crop(wallpaper, new Rect(320, 40, sceneWidth, sceneHeight)));
            content.Width = windowWidth; content.Height = windowHeight;
            var frame = new Grid { Width = windowWidth, Height = windowHeight, Clip = new RectangleGeometry(new Rect(0, 0, windowWidth, windowHeight), 8, 8) };
            frame.Children.Add(Crop(wallpaperBlur, new Rect(380, 100, windowWidth, windowHeight)));
            frame.Children.Add(new Border { Background = new SolidColorBrush(dark ? Color.FromArgb(0xE8, 0x20, 0x20, 0x20) : Color.FromArgb(0xED, 0xF3, 0xF3, 0xF3)), CornerRadius = new CornerRadius(8) });
            frame.Children.Add(content);
            // Caption buttons are drawn by DWM in the app, outside WPF's render target.
            var captionButtons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Height = 48 };
            foreach (string glyph in new[] { "\uE921", "\uE922", "\uE8BB" })
            {
                var icon = Ui.Glyph(glyph, 10, "Text"); icon.Width = 46; icon.TextAlignment = TextAlignment.Center; captionButtons.Children.Add(icon);
            }
            frame.Children.Add(captionButtons);
            var outline = new Border { CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1), Child = frame };
            outline.SetResourceReference(Border.BorderBrushProperty, "CardStroke");
            outline.Effect = new DropShadowEffect { BlurRadius = 32, ShadowDepth = 8, Direction = 270, Opacity = dark ? 0.55 : 0.28, Color = Colors.Black };
            Canvas.SetLeft(outline, 60); Canvas.SetTop(outline, 60); scene.Children.Add(outline);
            var caption = Plain("AI Meter " + Controller.Version + " · " + what, 13); caption.Opacity = 0.8;
            Canvas.SetLeft(caption, 64); Canvas.SetTop(caption, sceneHeight - 42); scene.Children.Add(caption);
            return scene;
        }

        /// <summary>Compose the desktop corner, optionally adding the notification, panel and pointer.</summary>
        private sealed class Scene
        {
            public const int W = 640, H = 1080;
            public double PanelOffset = double.NaN, PanelOpacity, PanelScroll, ToastOffset = double.NaN, ToastOpacity;
            public Point? Mouse;
            public bool Hover;
        }

        private static Rect pillRect;
        private static TranslateTransform panelScroll;

        private static FrameworkElement Compose(Snapshot s, Scene sc, FrameworkElement panel, FrameworkElement toast)
        {
            var c = new Canvas { Width = Scene.W, Height = Scene.H, ClipToBounds = true };
            c.Children.Add(Crop(wallpaper, new Rect(DeskW - Scene.W, DeskH - Scene.H, Scene.W, Scene.H)));
            var bar = Taskbar(s, Scene.W, sc.Hover);
            Canvas.SetTop(bar, Scene.H - BarH);
            c.Children.Add(bar);
            if (panel != null && !double.IsNaN(sc.PanelOffset))
            {
                panel.Opacity = sc.PanelOpacity;
                if (panelScroll != null) panelScroll.Y = -sc.PanelScroll;
                Canvas.SetLeft(panel, Scene.W - 344 - 12);
                Canvas.SetTop(panel, Scene.H - BarH - 12 - panel.Height + sc.PanelOffset);
                c.Children.Add(panel);
            }
            if (toast != null && !double.IsNaN(sc.ToastOffset))
            {
                toast.Opacity = sc.ToastOpacity;
                Canvas.SetLeft(toast, Scene.W - 364 - 12 + sc.ToastOffset);
                Canvas.SetTop(toast, Scene.H - BarH - 12 - toast.Height);
                c.Children.Add(toast);
            }
            if (sc.Mouse.HasValue)
            {
                var cur = Cursor();
                Canvas.SetLeft(cur, sc.Mouse.Value.X);
                Canvas.SetTop(cur, sc.Mouse.Value.Y);
                c.Children.Add(cur);
            }
            return c;
        }

        private static FrameworkElement PanelShot(Snapshot s, bool dark)
        {
            var onDesk = new Rect(DeskW - 344 - 60, 60, 344, 0);
            var panel = Panel(s, dark, onDesk);
            double h = ((Grid)panel).Height;
            var c = new Canvas { Width = 344 + 96, Height = h + 96, ClipToBounds = true };
            // A panel listing every card can be taller than the desktop: stretch the abstract backdrop to fit.
            var backdrop = Crop(wallpaper, new Rect(DeskW - 344 - 108, 12, 344 + 96, Math.Min(DeskH - 12, h + 96)));
            backdrop.Height = h + 96;
            c.Children.Add(backdrop);
            Canvas.SetLeft(panel, 48); Canvas.SetTop(panel, 40);
            c.Children.Add(panel);
            return c;
        }

        private static FrameworkElement TaskbarShot(Snapshot s)
        {
            var c = new Canvas { Width = 760, Height = BarH + 40, ClipToBounds = true };
            c.Children.Add(Crop(wallpaper, new Rect(DeskW - 760, DeskH - BarH - 40, 760, BarH + 40)));
            var bar = Taskbar(s, 760, false);
            Canvas.SetTop(bar, 40);
            c.Children.Add(bar);
            return c;
        }

        private static FrameworkElement ToastShot(Snapshot s)
        {
            var toast = Toast(s, new Rect(DeskW - 420, DeskH - 300, 364, 0));
            double h = ((Grid)toast).Height;
            var c = new Canvas { Width = 364 + 80, Height = h + 80, ClipToBounds = true };
            c.Children.Add(Crop(wallpaper, new Rect(DeskW - 444, DeskH - h - 140, 444, h + 80)));
            Canvas.SetLeft(toast, 40); Canvas.SetTop(toast, 32);
            c.Children.Add(toast);
            return c;
        }

        /// <summary>Use the actual TaskbarPill tooltip text in a Windows-style container.</summary>
        private static FrameworkElement Tooltip(Snapshot s)
        {
            var text = Plain(Ui.Summary(s) + "\n" + L.T("Click for details · right-click for options"), 12);
            text.LineHeight = 18;
            var b = new Border
            {
                Child = text, Padding = new Thickness(10, 7, 10, 8), CornerRadius = new CornerRadius(7), BorderThickness = new Thickness(1),
                Background = new SolidColorBrush(Color.FromArgb(0xF5, 0x2B, 0x2B, 0x2B)), BorderBrush = new SolidColorBrush(Color.FromArgb(0x30, 0x00, 0x00, 0x00)),
                Effect = new DropShadowEffect { BlurRadius = 16, ShadowDepth = 4, Direction = 270, Opacity = 0.35 },
            };
            b.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            return b;
        }

        /// <summary>
        /// Show the collapsed taskbar pill with its pointer and tooltip for the README hero
        /// and the background of the social preview.
        /// </summary>
        internal static FrameworkElement HeroTaskbar(Snapshot s, double w, double h, bool tooltip)
        {
            var c = new Canvas { Width = w, Height = h, ClipToBounds = true };
            c.Children.Add(Crop(wallpaper, new Rect(DeskW - w, DeskH - h, w, h)));
            var bar = Taskbar(s, w, true);
            bar.Measure(new Size(w, BarH)); bar.Arrange(new Rect(0, 0, w, BarH)); bar.UpdateLayout();
            var pill = ((DockPanel)((Grid)bar).Children[0]).Children.OfType<Border>().First();
            var pos = pill.TranslatePoint(new Point(0, 0), bar);
            Canvas.SetTop(bar, h - BarH);
            c.Children.Add(bar);
            if (tooltip)
            {
                var tip = Tooltip(s);
                double x = Math.Min(pos.X, w - tip.DesiredSize.Width - 12);
                Canvas.SetLeft(tip, x);
                Canvas.SetTop(tip, h - BarH - 10 - tip.DesiredSize.Height);
                c.Children.Add(tip);
            }
            var cur = Cursor();
            Canvas.SetLeft(cur, pos.X + 6);
            Canvas.SetTop(cur, h - BarH + pos.Y + pill.ActualHeight * 0.5);
            c.Children.Add(cur);
            return c;
        }

        /// <summary>Compose a 1280-by-640 social preview with the taskbar scene and product description.</summary>
        private static FrameworkElement SocialPreview(Snapshot s)
        {
            var c = new Canvas { Width = 1280, Height = 640, ClipToBounds = true };
            // Use an enlarged taskbar scene as the background.
            var hero = HeroTaskbar(s, 800, 400, true);
            hero.RenderTransform = new ScaleTransform(1.6, 1.6);
            c.Children.Add(hero);
            var left = new StackPanel { Width = 700 };
            var logoRow = new StackPanel { Orientation = Orientation.Horizontal };
            logoRow.Children.Add(new Image { Source = AppIcon(), Width = 72, Height = 72, Margin = new Thickness(0, 0, 18, 0) });
            var name = Plain("AI Meter", 66); name.FontWeight = FontWeights.SemiBold; name.FontFamily = Theme.DisplayFont; name.VerticalAlignment = VerticalAlignment.Center;
            logoRow.Children.Add(name);
            left.Children.Add(logoRow);
            var tag = Plain("AI limits, local models and agents,\nquietly in the Windows 11 taskbar.", 30); tag.Margin = new Thickness(0, 18, 0, 0); tag.Opacity = 0.95;
            left.Children.Add(tag);
            var list = Plain("Native Fluent design · ~0.02% CPU · Windows + WSL", 21); list.Margin = new Thickness(0, 14, 0, 0); list.Opacity = 0.72;
            left.Children.Add(list);
            Canvas.SetLeft(left, 64); Canvas.SetTop(left, 44);
            c.Children.Add(left);
            return c;
        }

        /// <summary>
        /// Animate the taskbar pill, a low-quota notification, the pointer click and the open panel, which scrolls
        /// to its last card when it is taller than the work area. Render at 15 frames per second for about 13 seconds.
        /// </summary>
        private static int GifFrames(Snapshot s, string dir)
        {
            foreach (var f in Directory.GetFiles(dir, "f*.png")) File.Delete(f);
            // The real flyout is at most the work area's height minus 24 pixels and sits 12 pixels above the taskbar.
            double maxScroll;
            var panel = Panel(s, true, new Rect(DeskW - 356, 12, 344, 0), Scene.H - BarH - 24, out panelScroll, out maxScroll);
            var toast = Toast(s, new Rect(DeskW - 376, DeskH - BarH - 160, 364, 0));
            var timeline = new List<Scene>();
            Func<double, double> ease = t => 1 - Math.Pow(1 - Math.Max(0, Math.Min(1, t)), 3);
            // Derive the pill position from the taskbar measurement.
            var bar = Taskbar(s, Scene.W, false);
            bar.Measure(new Size(Scene.W, BarH)); bar.Arrange(new Rect(0, 0, Scene.W, BarH)); bar.UpdateLayout();
            var pillEl = ((DockPanel)((Grid)bar).Children[0]).Children.OfType<Border>().First();
            var pos = pillEl.TranslatePoint(new Point(0, 0), bar);
            pillRect = new Rect(pos.X, Scene.H - BarH + pos.Y, pillEl.ActualWidth, pillEl.ActualHeight);
            var target = new Point(pillRect.X + pillRect.Width * 0.35, pillRect.Y + pillRect.Height * 0.45);
            var start = new Point(Scene.W * 0.45, Scene.H * 0.55);

            for (int i = 0; i < 15; i++) timeline.Add(new Scene());                                          // Hold the taskbar for one second.
            for (int i = 0; i < 8; i++) timeline.Add(new Scene { ToastOffset = 380 * (1 - ease(i / 7.0)), ToastOpacity = ease(i / 7.0) });
            for (int i = 0; i < 40; i++) timeline.Add(new Scene { ToastOffset = 0, ToastOpacity = 1 });     // Keep the notification readable.
            for (int i = 0; i < 8; i++) timeline.Add(new Scene { ToastOffset = 380 * ease(i / 7.0), ToastOpacity = 1 - ease(i / 7.0) });
            for (int i = 0; i <= 14; i++)                                                                      // Move the pointer towards the pill.
            {
                double t = ease(i / 14.0);
                timeline.Add(new Scene { Mouse = new Point(start.X + (target.X - start.X) * t, start.Y + (target.Y - start.Y) * t), Hover = i > 10 });
            }
            for (int i = 0; i < 4; i++) timeline.Add(new Scene { Mouse = target, Hover = true });
            for (int i = 0; i < 6; i++) timeline.Add(new Scene { Mouse = target, Hover = true, PanelOffset = 40 * (1 - ease(i / 5.0)), PanelOpacity = ease(i / 5.0) });
            for (int i = 0; i < 22; i++) timeline.Add(new Scene { Mouse = i < 8 || maxScroll > 0 ? (Point?)target : null, Hover = i < 8 || maxScroll > 0, PanelOffset = 0, PanelOpacity = 1 });
            if (maxScroll > 0)
            {
                // Bring the pointer over the cards, scroll to the last one, then hold.
                var over = new Point(Scene.W - 356 + 220, Scene.H * 0.5);
                Func<double, double> smooth = t => t * t * (3 - 2 * t);
                for (int i = 0; i <= 10; i++)
                {
                    double t = ease(i / 10.0);
                    timeline.Add(new Scene { Mouse = new Point(target.X + (over.X - target.X) * t, target.Y + (over.Y - target.Y) * t), Hover = i < 2, PanelOffset = 0, PanelOpacity = 1 });
                }
                for (int i = 0; i <= 30; i++) timeline.Add(new Scene { Mouse = over, PanelOffset = 0, PanelOpacity = 1, PanelScroll = maxScroll * smooth(i / 30.0) });
                for (int i = 0; i < 40; i++) timeline.Add(new Scene { Mouse = i < 10 ? (Point?)over : null, PanelOffset = 0, PanelOpacity = 1, PanelScroll = maxScroll });
            }
            else for (int i = 0; i < 23; i++) timeline.Add(new Scene { PanelOffset = 0, PanelOpacity = 1 });

            int n = 0;
            foreach (var sc in timeline)
            {
                var frame = (Canvas)Compose(s, sc, panel, toast);
                Save(frame, IOPath.Combine(dir, "f" + (n++).ToString("D3") + ".png"), 1);
                frame.Children.Clear();   // Reuse the panel and notification in the following frame.
            }
            return n;
        }

        // Settings → AI apps.
        // The page is the real one, listing sample clients from fixture folders: which apps it finds and what their
        // files contain come from the fixture, never from the PC that renders the images.
        private const string SampleHome = @"C:\Users\you", SampleWslHome = @"\\wsl.localhost\Ubuntu\home\you";
        private const string SampleRelay = @"C:\Users\you\AppData\Roaming\AIMeter\mcp.ps1";
        private const int AppsWidth = 1000, AppsHeight = 740;

        /// <summary>Render the AI apps page in both themes and the frames of a GIF that connects two apps and copies a command.</summary>
        private static int AppsMedia(string outDir, string framesDir)
        {
            string root = IOPath.Combine(IOPath.GetTempPath(), "aimeter-apps-" + Guid.NewGuid().ToString("N"));
            string home = IOPath.Combine(root, "home"), appData = IOPath.Combine(home, @"AppData\Roaming"), wsl = IOPath.Combine(root, "wsl");
            var previous = SettingsWindow.KnownApps;
            try
            {
                // Installed: Claude Desktop, VS Code, Claude Code, Codex and OpenCode on Windows; Claude Code and Pi in WSL.
                Directory.CreateDirectory(IOPath.Combine(appData, "Claude"));
                Directory.CreateDirectory(IOPath.Combine(appData, @"Code\User"));
                Directory.CreateDirectory(IOPath.Combine(home, ".codex"));
                Directory.CreateDirectory(IOPath.Combine(home, @".config\opencode"));
                Directory.CreateDirectory(IOPath.Combine(wsl, @".pi\agent"));
                File.WriteAllText(IOPath.Combine(home, ".claude.json"), "{}");
                File.WriteAllText(IOPath.Combine(wsl, ".claude.json"), "{}");
                SettingsWindow.KnownApps = () => McpClients.Known(home, appData, wsl);
                Func<string, string, McpClients.Client> client = (id, where) => SettingsWindow.KnownApps().First(c => c.Id == id && c.Where == where);
                McpClients.Connect(client("claude-desktop", "Windows"), SampleRelay);
                Action<FrameworkElement> sample = el => { ReplaceText(el, wsl, SampleWslHome); ReplaceText(el, home, SampleHome); };

                var controller = new Controller(false, null, null);
                typeof(Controller).GetProperty("Settings").SetValue(controller, S, null);
                foreach (var dark in new[] { true, false })
                {
                    SetTheme(dark);
                    var content = AppsPage(controller, sample);
                    Save(WindowShot(content, dark, AppsWidth, AppsHeight, "AI apps · Sample apps and paths"), IOPath.Combine(outDir, dark ? "ai-apps-dark.png" : "ai-apps-light.png"), 2);
                }
                SetTheme(true);
                return AppsFrames(controller, sample, framesDir, () => McpClients.Connect(client("claude-code", "Windows"), SampleRelay),
                                  () => McpClients.Connect(client("codex", "Windows"), SampleRelay));
            }
            finally
            {
                SettingsWindow.KnownApps = previous;
                if (Directory.Exists(root)) Directory.Delete(root, true);
            }
        }

        /// <summary>The Settings window's content open on AI apps, detached from its window so that it can be framed.</summary>
        private static FrameworkElement AppsPage(Controller controller, Action<FrameworkElement> sample)
        {
            var window = new SettingsWindow(controller);
            window.Open("apps");
            var content = (FrameworkElement)window.Content;
            window.Content = null;
            content.Tag = window;
            sample(content);
            return content;
        }

        /// <summary>
        /// Connect Claude Code and Codex with their switches, then scroll to the command line and copy Codex's WSL command.
        /// Each click rebuilds the page from the fixture files, as the real switch does after editing the app's file.
        /// </summary>
        private static int AppsFrames(Controller controller, Action<FrameworkElement> sample, string dir, Action connectClaudeCode, Action connectCodex)
        {
            Directory.CreateDirectory(dir);
            foreach (var f in Directory.GetFiles(dir, "f*.png")) File.Delete(f);
            var content = AppsPage(controller, sample);
            var window = (SettingsWindow)content.Tag;
            var scene = WindowShot(content, true, AppsWidth, AppsHeight, "AI apps · Sample apps and paths");
            var scroll = (ScrollViewer)typeof(SettingsWindow).GetField("scroll", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).GetValue(window);
            var cursor = Cursor();
            scene.Children.Add(cursor);
            Func<double, double> ease = t => t * t * (3 - 2 * t);
            Point mouse = new Point(560, 300);
            int n = 0;
            Action frame = () =>
            {
                Canvas.SetLeft(cursor, mouse.X); Canvas.SetTop(cursor, mouse.Y);
                Save(scene, IOPath.Combine(dir, "f" + (n++).ToString("D3") + ".png"), 1);
            };
            Action<int> hold = count => { for (int i = 0; i < count; i++) frame(); };
            Action<Point, int> move = (to, count) =>
            {
                var from = mouse;
                for (int i = 1; i <= count; i++) { double t = ease(i / (double)count); mouse = new Point(from.X + (to.X - from.X) * t, from.Y + (to.Y - from.Y) * t); frame(); }
            };
            // The scene places the window's content 60 pixels from its top-left corner.
            Func<FrameworkElement, Point> centre = el =>
            {
                scene.UpdateLayout();
                var p = el.TranslatePoint(new Point(el.ActualWidth / 2, el.ActualHeight / 2), content);
                return new Point(60 + p.X - 2, 60 + p.Y - 4);
            };
            Action rebuild = () =>
            {
                double offset = scroll.VerticalOffset;
                window.Build(); sample(content); scene.UpdateLayout();
                scroll.ScrollToVerticalOffset(offset); scene.UpdateLayout();
            };

            hold(15);
            move(centre(RowControl(content, "Claude Code", 0)), 14);
            hold(3); connectClaudeCode(); rebuild(); hold(14);
            move(centre(RowControl(content, "Codex", 0)), 10);
            hold(3); connectCodex(); rebuild(); hold(14);

            // Scroll to "From the command line", as a mouse wheel would.
            move(new Point(mouse.X - 120, mouse.Y + 40), 6);
            scene.UpdateLayout();
            double end = scroll.ScrollableHeight;
            for (int i = 1; i <= 18; i++) { scroll.ScrollToVerticalOffset(end * ease(i / 18.0)); scene.UpdateLayout(); frame(); }
            hold(4);
            var copy = (Button)((Panel)RowControl(content, "Codex", 1)).Children[1];
            move(centre(copy), 10);
            hold(3); SettingsWindow.ShowCopied(copy, TimeSpan.FromSeconds(30)); scene.UpdateLayout(); hold(4);
            // Step aside so that the confirmation on the button can be read.
            move(new Point(mouse.X + 40, mouse.Y + 46), 6);
            hold(26);
            return n;
        }

        /// <summary>The control of the index-th Settings row titled <paramref name="title"/>, in page order.</summary>
        private static FrameworkElement RowControl(DependencyObject root, string title, int index)
        {
            var rows = new List<Grid>();
            FindRows(root, title, rows);
            return rows[index].Children.OfType<FrameworkElement>().First(c => Grid.GetColumn(c) == 2);
        }

        private static void FindRows(DependencyObject node, string title, List<Grid> rows)
        {
            var text = node as TextBlock;
            if (text != null && text.Text == title)
            {
                var texts = LogicalTreeHelper.GetParent(text) as StackPanel;
                var row = texts == null ? null : LogicalTreeHelper.GetParent(texts) as Grid;
                if (row != null && row.Children.OfType<FrameworkElement>().Any(c => Grid.GetColumn(c) == 2)) rows.Add(row);
            }
            foreach (var child in LogicalTreeHelper.GetChildren(node).OfType<DependencyObject>()) FindRows(child, title, rows);
        }

        /// <summary>Replace a fixture path in every text of the page with the sample path shown in its place.</summary>
        private static void ReplaceText(DependencyObject node, string from, string to)
        {
            var text = node as TextBlock;
            if (text != null && text.Text.Contains(from)) text.Text = text.Text.Replace(from, to);
            foreach (var child in LogicalTreeHelper.GetChildren(node).OfType<DependencyObject>()) ReplaceText(child, from, to);
        }

        // Rendering and encoding.
        internal static BitmapSource Render(FrameworkElement el, double w, double h, double scale)
        {
            el.Measure(new Size(w, h));
            el.Arrange(new Rect(0, 0, w, h));
            el.UpdateLayout();
            var bmp = new RenderTargetBitmap((int)Math.Round(w * scale), (int)Math.Round(h * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
            bmp.Render(el);
            bmp.Freeze();
            return bmp;
        }

        internal static void Save(FrameworkElement el, string path, double scale)
        {
            el.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            double w = double.IsNaN(el.Width) ? el.DesiredSize.Width : el.Width;
            double h = double.IsNaN(el.Height) ? el.DesiredSize.Height : el.Height;
            var bmp = Render(el, w, h, scale);
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(bmp));
            using (var f = File.Create(path)) enc.Save(f);
        }
    }
}
