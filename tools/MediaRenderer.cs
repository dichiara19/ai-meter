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
    /// Immagini del README con i componenti veri dell'interfaccia e i dati dimostrativi: pannello (tema scuro e chiaro),
    /// pillola nella barra, notifica, anteprima per i social e i fotogrammi della GIF. Avvio: tools\render-media.ps1.
    /// </summary>
    public static class MediaRenderer
    {
        private static Settings S;
        private static BitmapSource wallpaper, wallpaperBlur;
        private const int DeskW = 1920, DeskH = 1080, BarH = 48;
        // ora vera al minuto: gli "in 2 h 50 min" dell'interfaccia si calcolano sull'ora attuale
        private static readonly DateTime Now = new DateTime(DateTime.Now.Year, DateTime.Now.Month, DateTime.Now.Day, DateTime.Now.Hour, DateTime.Now.Minute, 0);

        public static string Run(string outDir, string framesDir, string lang)
        {
            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            S = new Settings { Language = lang };
            L.Init(lang);
            Directory.CreateDirectory(outDir);
            Directory.CreateDirectory(framesDir);
            SetTheme(true);
            wallpaper = Wallpaper(false);
            wallpaperBlur = Wallpaper(true);
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
            return "ok, " + frames + " frames";
        }

        private static void SetTheme(bool dark)
        {
            S.Theme = dark ? "dark" : "light";
            Theme.Apply(S);
        }

        // ================================================================ sfondo
        /// <summary>Fondale astratto in stile Windows 11 (forme sfumate su blu notte), generato qui: nessuna immagine di terzi.</summary>
        private static BitmapSource Wallpaper(bool blurred)
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
            // la sfocatura schiarisce i bordi: si disegna più grande e si tiene il centro
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

        private static Image Crop(BitmapSource src, Rect r)
        {
            var cb = new CroppedBitmap(src, new Int32Rect((int)r.X, (int)r.Y, (int)r.Width, (int)r.Height));
            return new Image { Source = cb, Width = r.Width, Height = r.Height, Stretch = Stretch.Fill };
        }

        // ================================================================ componenti
        /// <summary>Il pannello come la finestra vera (Flyout): intestazione, schede, piè di pagina; vetro simulato.</summary>
        private static FrameworkElement Panel(Snapshot s, bool dark, Rect onDesk)
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
            root.Children.Add(body);
            return Glass(root, dark, onDesk, 344);
        }

        /// <summary>Vetro acrilico simulato: lo sfondo sfocato sotto la finestra, una tinta, bordo, angoli e ombra.</summary>
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

        /// <summary>La pillola come nella barra: pulsante alto 40 con la riga di loghi e percentuali, tema della barra.</summary>
        private static FrameworkElement Pill(Snapshot s, bool hover)
        {
            var row = Ui.PillRow(s, "5h", true);
            var chip = new Border { Height = 40, Padding = new Thickness(10, 0, 10, 0), CornerRadius = new CornerRadius(4), Child = row, VerticalAlignment = VerticalAlignment.Center };
            chip.Background = hover ? new SolidColorBrush(Color.FromArgb(0x15, 0xFF, 0xFF, 0xFF)) : Brushes.Transparent;
            return chip;
        }

        /// <summary>L'angolo destro della barra delle applicazioni: pillola, icone di sistema, orologio.</summary>
        private static FrameworkElement Taskbar(Snapshot s, double width, bool hover)
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
            // tema scuro della barra per la pillola (come TaskbarPill, che usa i colori di sistema)
            g2.Resources.MergedDictionaries.Add(Theme.BuildPalette(true));
            return g2;
        }

        private static TextBlock Plain(string t, double size) { return new TextBlock { Text = t, FontSize = size, Foreground = Brushes.White, FontFamily = Theme.Font }; }
        private static TextBlock Icon(string g, double size) { return new TextBlock { Text = g, FontSize = size, Foreground = Brushes.White, FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), VerticalAlignment = VerticalAlignment.Center }; }

        /// <summary>La notifica di Windows come la mostra il sistema (scheda scura con nome e icona dell'app).</summary>
        private static FrameworkElement Toast(Snapshot s, Rect onDesk)
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

        private static BitmapSource AppIcon()
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

        // ================================================================ scene
        /// <summary>Angolo in basso a destra dello schermo: sfondo, barra, e a richiesta notifica, pannello e puntatore.</summary>
        private sealed class Scene
        {
            public const int W = 640, H = 1080;
            public double PanelOffset = double.NaN, PanelOpacity, ToastOffset = double.NaN, ToastOpacity;
            public Point? Mouse;
            public bool Hover;
        }

        private static Rect pillRect;

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
            c.Children.Add(Crop(wallpaper, new Rect(DeskW - 344 - 108, 12, 344 + 96, Math.Min(DeskH - 12, h + 96))));
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

        /// <summary>Il suggerimento della pillola (il testo vero di TaskbarPill), nello stile dei suggerimenti di Windows 11.</summary>
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
        /// La pillola chiusa nella barra, come si vede tutto il giorno: angolo della barra con il puntatore sopra
        /// e il suo suggerimento. È la prima immagine del README e il fondo dell'anteprima per i social.
        /// </summary>
        private static FrameworkElement HeroTaskbar(Snapshot s, double w, double h, bool tooltip)
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

        /// <summary>Anteprima per i link (GitHub, social): 1280×640, la pillola nella barra e la frase.</summary>
        private static FrameworkElement SocialPreview(Snapshot s)
        {
            var c = new Canvas { Width = 1280, Height = 640, ClipToBounds = true };
            // la scena della barra, ingrandita, fa da fondo
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
        /// GIF: la pillola nella barra, una notifica sul limite in esaurimento, il clic sulla pillola e il pannello.
        /// 15 fotogrammi al secondo, circa 9 secondi.
        /// </summary>
        private static int GifFrames(Snapshot s, string dir)
        {
            foreach (var f in Directory.GetFiles(dir, "f*.png")) File.Delete(f);
            var panel = Panel(s, true, new Rect(DeskW - 356, DeskH - BarH - 960, 344, 0));
            var toast = Toast(s, new Rect(DeskW - 376, DeskH - BarH - 160, 364, 0));
            var timeline = new List<Scene>();
            Func<double, double> ease = t => 1 - Math.Pow(1 - Math.Max(0, Math.Min(1, t)), 3);
            // posizione della pillola nella scena (dalla misura della barra)
            var bar = Taskbar(s, Scene.W, false);
            bar.Measure(new Size(Scene.W, BarH)); bar.Arrange(new Rect(0, 0, Scene.W, BarH)); bar.UpdateLayout();
            var pillEl = ((DockPanel)((Grid)bar).Children[0]).Children.OfType<Border>().First();
            var pos = pillEl.TranslatePoint(new Point(0, 0), bar);
            pillRect = new Rect(pos.X, Scene.H - BarH + pos.Y, pillEl.ActualWidth, pillEl.ActualHeight);
            var target = new Point(pillRect.X + pillRect.Width * 0.35, pillRect.Y + pillRect.Height * 0.45);
            var start = new Point(Scene.W * 0.45, Scene.H * 0.55);

            for (int i = 0; i < 15; i++) timeline.Add(new Scene());                                          // 1 s: la barra
            for (int i = 0; i < 8; i++) timeline.Add(new Scene { ToastOffset = 380 * (1 - ease(i / 7.0)), ToastOpacity = ease(i / 7.0) });
            for (int i = 0; i < 40; i++) timeline.Add(new Scene { ToastOffset = 0, ToastOpacity = 1 });     // la notifica si legge
            for (int i = 0; i < 8; i++) timeline.Add(new Scene { ToastOffset = 380 * ease(i / 7.0), ToastOpacity = 1 - ease(i / 7.0) });
            for (int i = 0; i <= 14; i++)                                                                      // il puntatore va sulla pillola
            {
                double t = ease(i / 14.0);
                timeline.Add(new Scene { Mouse = new Point(start.X + (target.X - start.X) * t, start.Y + (target.Y - start.Y) * t), Hover = i > 10 });
            }
            for (int i = 0; i < 4; i++) timeline.Add(new Scene { Mouse = target, Hover = true });
            for (int i = 0; i < 6; i++) timeline.Add(new Scene { Mouse = target, Hover = true, PanelOffset = 40 * (1 - ease(i / 5.0)), PanelOpacity = ease(i / 5.0) });
            for (int i = 0; i < 45; i++) timeline.Add(new Scene { Mouse = i < 8 ? (Point?)target : null, Hover = i < 8, PanelOffset = 0, PanelOpacity = 1 });

            int n = 0;
            foreach (var sc in timeline)
            {
                var frame = (Canvas)Compose(s, sc, panel, toast);
                Save(frame, IOPath.Combine(dir, "f" + (n++).ToString("D3") + ".png"), 1);
                frame.Children.Clear();   // pannello e notifica si riusano nel fotogramma successivo
            }
            return n;
        }

        // ================================================================ disegno
        private static BitmapSource Render(FrameworkElement el, double w, double h, double scale)
        {
            el.Measure(new Size(w, h));
            el.Arrange(new Rect(0, 0, w, h));
            el.UpdateLayout();
            var bmp = new RenderTargetBitmap((int)Math.Round(w * scale), (int)Math.Round(h * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
            bmp.Render(el);
            bmp.Freeze();
            return bmp;
        }

        private static void Save(FrameworkElement el, string path, double scale)
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
