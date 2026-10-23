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
    /// <summary>Mattoni dell'interfaccia condivisi da widget, pannello e pillola.</summary>
    internal static class Ui
    {
        private static readonly CultureInfo It = new CultureInfo("it-IT");
        private static readonly Dictionary<string, BitmapImage> Logos = new Dictionary<string, BitmapImage>();

        // ------------------------------------------------------------ testo e icone
        public static TextBlock Text(string text, double size, string brush, FontWeight? weight = null)
        {
            var t = new TextBlock { Text = text, FontSize = size, TextTrimming = TextTrimming.CharacterEllipsis };
            t.SetResourceReference(TextBlock.ForegroundProperty, brush);
            if (weight.HasValue) t.FontWeight = weight.Value;
            return t;
        }

        public static TextBlock Glyph(string code, double size, string brush)
        {
            var t = new TextBlock { Text = code, FontFamily = Theme.Icons, FontSize = size, VerticalAlignment = VerticalAlignment.Center };
            t.SetResourceReference(TextBlock.ForegroundProperty, brush);
            return t;
        }

        public static Button IconButton(string glyph, string tip, RoutedEventHandler click)
        {
            var b = new Button { Content = glyph, ToolTip = tip };
            b.SetResourceReference(FrameworkElement.StyleProperty, "IconButton");
            b.Click += click;
            return b;
        }

        /// <summary>
        /// Logo ufficiale del servizio, letto dall'app installata sul PC (i marchi non sono inclusi nel progetto).
        /// Se l'app non c'è, un monogramma con il colore del servizio.
        /// </summary>
        public static FrameworkElement Logo(string id, double size)
        {
            string key = id + (Theme.Dark ? ":dark" : ":light");
            BitmapImage bmp;
            if (!Logos.TryGetValue(key, out bmp))
            {
                string file = FindInstalledLogo(id, Theme.Dark);
                if (file != null)
                {
                    try
                    {
                        bmp = new BitmapImage();
                        bmp.BeginInit();
                        bmp.UriSource = new Uri(file);
                        bmp.DecodePixelWidth = 64;   // basta per 16-32 px a qualsiasi DPI e tiene bassa la memoria
                        bmp.CacheOption = BitmapCacheOption.OnLoad;
                        bmp.EndInit();
                        bmp.Freeze();
                    }
                    catch (Exception) { bmp = null; }
                }
                Logos[key] = bmp;
            }
            if (bmp == null) return Monogram(id, size);
            var img = new Image { Source = bmp, Width = size, Height = size, VerticalAlignment = VerticalAlignment.Center };
            RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
            return img;
        }

        private static FrameworkElement Monogram(string id, double size)
        {
            var b = new Border { Width = size, Height = size, CornerRadius = new CornerRadius(size * 0.24), VerticalAlignment = VerticalAlignment.Center };
            b.SetResourceReference(Border.BackgroundProperty, BrandBrush(id));
            b.Child = new TextBlock
            {
                Text = id == "openai" ? "G" : "C", Foreground = Brushes.White, FontWeight = FontWeights.Bold, FontSize = size * 0.58,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            };
            return b;
        }

        /// <summary>Cerca l'icona nel pacchetto dell'app installata (registro dei pacchetti dell'utente).</summary>
        private static string FindInstalledLogo(string id, bool dark)
        {
            string[] prefixes = id == "openai" ? new[] { "OpenAI.ChatGPT", "OpenAI.Codex_" } : new[] { "Claude_" };
            string[] names = id == "openai" && !dark
                ? new[] { "Square44x44Logo.targetsize-256_altform-lightunplated.png", "Square44x44Logo.targetsize-256_altform-unplated.png" }
                : new[] { "Square44x44Logo.targetsize-256_altform-unplated.png", "Square44x44Logo.targetsize-256_altform-lightunplated.png" };
            try
            {
                using (var repo = Registry.CurrentUser.OpenSubKey(@"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\Repository\Packages"))
                {
                    if (repo == null) return null;
                    foreach (var pkg in repo.GetSubKeyNames().Where(n => prefixes.Any(p => n.StartsWith(p, StringComparison.OrdinalIgnoreCase))).OrderByDescending(n => n))
                    {
                        string root;
                        using (var k = repo.OpenSubKey(pkg)) root = k == null ? null : k.GetValue("PackageRootFolder") as string;
                        if (root == null) continue;
                        foreach (var dir in new[] { "assets", "Assets", "images", "Images" })
                            foreach (var name in names)
                            {
                                string f = Path.Combine(root, dir, name);
                                if (File.Exists(f)) return f;
                            }
                    }
                }
            }
            catch (Exception) { }
            return null;
        }

        public static string BrandBrush(string id) { return id == "openai" ? "OpenAI" : "Claude"; }

        public static string LevelBrush(double remaining, string brand)
        {
            if (remaining <= 15) return "Critical";
            if (remaining <= 40) return "Caution";
            return brand;
        }

        // ------------------------------------------------------------ formattazione
        public static string Ago(DateTime t)
        {
            if (t == DateTime.MinValue) return "";
            var s = (DateTime.Now - t).TotalSeconds;
            if (s < 60) return "ora";
            if (s < 3600) return (int)(s / 60) + " min";
            if (s < 86400) return (int)(s / 3600) + " h";
            return (int)(s / 86400) + " g";
        }

        public static string Until(DateTime t)
        {
            var span = t - DateTime.Now;
            if (span.TotalSeconds < 0) span = TimeSpan.Zero;
            if (span.TotalDays >= 1) return (int)span.TotalDays + " g " + span.Hours + " h";
            if (span.TotalHours >= 1) return (int)span.TotalHours + " h " + span.Minutes.ToString("00") + " min";
            return Math.Max(1, (int)span.TotalMinutes) + " min";
        }

        public static string ResetText(UsageWindow w, Usage u)
        {
            if (w.ResetsAt.HasValue)
            {
                var r = w.ResetsAt.Value;
                if ((r - DateTime.Now).TotalHours < 24 && r.Date == DateTime.Today) return "si azzera tra " + Until(r);
                if ((r - DateTime.Now).TotalHours < 24) return "si azzera domani " + r.ToString("HH:mm", It);
                return "si azzera " + r.ToString("ddd HH:mm", It);
            }
            if (u.Source == "cache") return "rilevato " + (Ago(u.At) == "ora" ? "ora" : Ago(u.At) + " fa");
            return "";
        }

        public static string PrettyModel(string m)
        {
            if (string.IsNullOrEmpty(m)) return null;
            var c = Regex.Match(m, @"^claude-(opus|sonnet|haiku|fable)-(\d+)(?:-(\d{1,2}))?(?:-\d{8})?", RegexOptions.IgnoreCase);
            if (c.Success)
                return char.ToUpper(c.Groups[1].Value[0]) + c.Groups[1].Value.Substring(1) + " " + c.Groups[2].Value + (c.Groups[3].Success ? "." + c.Groups[3].Value : "");
            if (m.StartsWith("gpt-", StringComparison.OrdinalIgnoreCase))
            {
                var parts = m.Substring(4).Split('-');
                return "GPT-" + parts[0] + string.Concat(parts.Skip(1).Select(p => " " + char.ToUpper(p[0]) + p.Substring(1)));
            }
            return m;
        }

        public static string PlanLabel(string plan)
        {
            if (string.IsNullOrEmpty(plan)) return null;
            return plan.ToUpperInvariant();
        }

        // ------------------------------------------------------------ componenti
        public static Border Card(UIElement child, Thickness padding)
        {
            var b = new Border { CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1), Padding = padding, Child = child };
            b.SetResourceReference(Border.BackgroundProperty, "Card");
            b.SetResourceReference(Border.BorderBrushProperty, "CardStroke");
            return b;
        }

        /// <summary>
        /// Icona a anelli concentrici, uno per servizio (dall'esterno: Claude, poi ChatGPT):
        /// l'arco colorato è la quota rimasta, con gli stessi colori di soglia delle barre.
        /// </summary>
        public static FrameworkElement RingIcon(IList<Tuple<ProviderState, UsageWindow>> items, double size)
        {
            var g = new Grid { Width = size, Height = size, Background = Brushes.Transparent };
            double t = size * 0.14, gap = size * 0.06, c = size / 2, r = c - t / 2;
            foreach (var it in items.Take(3))
            {
                var track = new System.Windows.Shapes.Ellipse { Width = 2 * r, Height = 2 * r, StrokeThickness = t, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
                track.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, "Track");
                g.Children.Add(track);
                if (it.Item2 != null && it.Item2.Remaining > 0.5)
                {
                    double frac = it.Item2.Remaining / 100;
                    System.Windows.Shapes.Shape arc;
                    if (frac >= 0.995)
                        arc = new System.Windows.Shapes.Ellipse { Width = 2 * r, Height = 2 * r, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
                    else
                    {
                        double a = frac * 2 * Math.PI;
                        var start = new Point(c, c - r);
                        var end = new Point(c + r * Math.Sin(a), c - r * Math.Cos(a));
                        var fig = new PathFigure { StartPoint = start, IsClosed = false };
                        fig.Segments.Add(new ArcSegment(end, new Size(r, r), 0, frac > 0.5, SweepDirection.Clockwise, true));
                        var geo = new PathGeometry();
                        geo.Figures.Add(fig);
                        geo.Freeze();
                        arc = new System.Windows.Shapes.Path { Data = geo, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round };
                    }
                    arc.StrokeThickness = t;
                    arc.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, LevelBrush(it.Item2.Remaining, BrandBrush(it.Item1.Id)));
                    g.Children.Add(arc);
                }
                r -= t + gap;
                if (r <= t) break;
            }
            return g;
        }

        public static FrameworkElement Bar(double remaining, string brush, double height)
        {
            var g = new Grid { Height = height, VerticalAlignment = VerticalAlignment.Center };
            var track = new Border { CornerRadius = new CornerRadius(height / 2) };
            track.SetResourceReference(Border.BackgroundProperty, "Track");
            g.Children.Add(track);
            var fillHost = new Grid();
            fillHost.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(0.0001, remaining), GridUnitType.Star) });
            fillHost.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(0.0001, 100 - remaining), GridUnitType.Star) });
            var fill = new Border { CornerRadius = new CornerRadius(height / 2), MinWidth = remaining > 0 ? height : 0 };
            fill.SetResourceReference(Border.BackgroundProperty, brush);
            fillHost.Children.Add(fill);
            g.Children.Add(fillHost);
            return g;
        }

        /// <summary>Riga di un limite: etichetta e azzeramento a sinistra, percentuale rimasta a destra, barra sotto.</summary>
        public static FrameworkElement UsageRow(UsageWindow w, Usage u, string brand)
        {
            var root = new StackPanel { Margin = new Thickness(0, 6, 0, 0) };
            var top = new DockPanel { LastChildFill = true };
            var pct = Text(Math.Round(w.Remaining).ToString(It) + "%", 13, "Text", FontWeights.SemiBold);
            pct.Margin = new Thickness(8, 0, 0, 0);
            DockPanel.SetDock(pct, Dock.Right);
            top.Children.Add(pct);
            var left = new TextBlock { FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Bottom };
            var lbl = new System.Windows.Documents.Run(w.Label);
            lbl.SetResourceReference(TextElement_Foreground, "Text2");
            left.Inlines.Add(lbl);
            string reset = ResetText(w, u);
            if (reset.Length > 0)
            {
                var r = new System.Windows.Documents.Run("  ·  " + reset);
                r.SetResourceReference(TextElement_Foreground, "Text3");
                left.Inlines.Add(r);
            }
            top.Children.Add(left);
            root.Children.Add(top);
            var bar = Bar(w.Remaining, LevelBrush(w.Remaining, brand), 4);
            bar.Margin = new Thickness(0, 5, 0, 0);
            root.Children.Add(bar);
            root.ToolTip = string.Format(It, "{0}: usato {1:0}%, rimasto {2:0}%{3}", w.Label, w.Used, w.Remaining,
                w.ResetsAt.HasValue ? "\nSi azzera " + w.ResetsAt.Value.ToString("dddd d MMMM, HH:mm", It) : "");
            return root;
        }

        private static readonly DependencyProperty TextElement_Foreground = System.Windows.Documents.TextElement.ForegroundProperty;

        public static FrameworkElement SessionRow(Session s, string brand)
        {
            var g = new Grid { Margin = new Thickness(0, 2, 0, 2), Background = Brushes.Transparent };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(18) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            FrameworkElement dot;
            if (s.Status == "app")
            {
                var e = new Border { Width = 8, Height = 8, CornerRadius = new CornerRadius(4), BorderThickness = new Thickness(1.5) };
                e.SetResourceReference(Border.BorderBrushProperty, "Info");
                dot = e;
            }
            else
            {
                var e = new Border { Width = 8, Height = 8, CornerRadius = new CornerRadius(4) };
                e.SetResourceReference(Border.BackgroundProperty, s.Status == "busy" ? brand : "Track");
                dot = e;
            }
            dot.VerticalAlignment = VerticalAlignment.Top;
            dot.HorizontalAlignment = HorizontalAlignment.Left;
            dot.Margin = new Thickness(1, 6, 0, 0);
            g.Children.Add(dot);

            var texts = new StackPanel();
            texts.Children.Add(Text(s.Title, 13, s.Status == "idle" && (DateTime.Now - s.Updated).TotalHours > 3 ? "Text2" : "Text"));
            var meta = new List<string> { s.Kind };
            string model = PrettyModel(s.Model);
            if (model != null) meta.Add(model);
            if (s.Status == "busy") meta.Add("al lavoro");
            texts.Children.Add(Text(string.Join(" · ", meta), 11.5, "Text3"));
            Grid.SetColumn(texts, 1);
            g.Children.Add(texts);

            if (s.Status != "app")
            {
                var ago = Text(Ago(s.Updated), 11.5, "Text3");
                ago.VerticalAlignment = VerticalAlignment.Top;
                ago.Margin = new Thickness(8, 1, 0, 0);
                Grid.SetColumn(ago, 2);
                g.Children.Add(ago);
            }
            g.ToolTip = s.Title + "\n" + string.Join(" · ", meta) + (s.Status != "app" ? "\nUltima attività: " + s.Updated.ToString("HH:mm", It) : "");
            return g;
        }

        /// <summary>Scheda di un servizio: intestazione, limiti, avviso, elenco sessioni (se espansa).</summary>
        public static Border ProviderCard(ProviderState p, bool expanded, Action toggle, Action login)
        {
            string brand = BrandBrush(p.Id);
            var stack = new StackPanel();

            // intestazione cliccabile
            var head = new DockPanel { Background = Brushes.Transparent, Cursor = toggle != null ? Cursors.Hand : null };
            var logo = Logo(p.Id, 20);
            logo.Margin = new Thickness(0, 0, 10, 0);
            DockPanel.SetDock(logo, Dock.Left);
            head.Children.Add(logo);

            if (toggle != null)
            {
                var chev = Glyph(expanded ? "" : "", 10, "Text3");
                chev.Margin = new Thickness(10, 0, 2, 0);
                DockPanel.SetDock(chev, Dock.Right);
                head.Children.Add(chev);
                head.MouseLeftButtonUp += (o, e) => { toggle(); e.Handled = true; };
            }

            var count = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            if (p.Running)
            {
                count.Children.Add(Text(p.Active + (p.Active == 1 ? " sessione" : " sessioni"), 12, "Text2"));
                if (p.Busy > 0)
                {
                    var pill = new Border { CornerRadius = new CornerRadius(8), Padding = new Thickness(6, 1, 7, 1), Margin = new Thickness(6, 0, 0, 0) };
                    pill.SetResourceReference(Border.BackgroundProperty, "Track");
                    var sp = new StackPanel { Orientation = Orientation.Horizontal };
                    var d = new Border { Width = 6, Height = 6, CornerRadius = new CornerRadius(3), Margin = new Thickness(0, 0, 5, 0), VerticalAlignment = VerticalAlignment.Center };
                    d.SetResourceReference(Border.BackgroundProperty, brand);
                    sp.Children.Add(d);
                    sp.Children.Add(Text(p.Busy.ToString(It), 11.5, "Text", FontWeights.SemiBold));
                    pill.Child = sp;
                    pill.ToolTip = p.Busy == 1 ? "1 sessione sta lavorando adesso" : p.Busy + " sessioni stanno lavorando adesso";
                    count.Children.Add(pill);
                }
            }
            else count.Children.Add(Text("non attivo", 12, "Text3"));
            DockPanel.SetDock(count, Dock.Right);
            head.Children.Add(count);

            var title = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, ClipToBounds = true };
            var name = Text(p.Name, 14, "Text", FontWeights.SemiBold);
            name.FontFamily = Theme.DisplayFont;
            title.Children.Add(name);
            string plan = PlanLabel(p.Plan);
            if (plan != null)
            {
                var badge = new Border { CornerRadius = new CornerRadius(4), Padding = new Thickness(5, 0, 5, 1), Margin = new Thickness(8, 1, 0, 0), VerticalAlignment = VerticalAlignment.Center };
                badge.SetResourceReference(Border.BackgroundProperty, "Track");
                badge.Child = Text(plan, 10, "Text2", FontWeights.Bold);
                title.Children.Add(badge);
            }
            head.Children.Add(title);
            stack.Children.Add(head);

            // limiti
            var limits = new StackPanel { Margin = new Thickness(0, 4, 0, 0) };
            if (p.Usage != null && p.Usage.Windows.Count > 0)
                foreach (var w in p.Usage.Windows.Take(4)) limits.Children.Add(UsageRow(w, p.Usage, brand));
            else
                limits.Children.Add(new TextBlock { Text = "Nessun dato sui limiti per ora", FontSize = 12, Margin = new Thickness(0, 6, 0, 0), Foreground = Theme.B("Text3") });
            stack.Children.Add(limits);

            if (!string.IsNullOrEmpty(p.Notice))
            {
                var note = new DockPanel { Margin = new Thickness(0, 8, 0, 0) };
                var ic = Glyph("", 12, "Text3");
                ic.Margin = new Thickness(0, 0, 6, 0);
                DockPanel.SetDock(ic, Dock.Left);
                note.Children.Add(ic);
                if (p.NeedsLogin && login != null)
                {
                    var link = new Button { Content = "Accedi", Padding = new Thickness(4, 0, 4, 1), FontSize = 12, Margin = new Thickness(4, 0, 0, 0) };
                    link.SetResourceReference(FrameworkElement.StyleProperty, "LinkButton");
                    link.Click += (o, e) => login();
                    DockPanel.SetDock(link, Dock.Right);
                    note.Children.Add(link);
                }
                var nt = Text(p.Notice, 11.5, "Text3");
                nt.VerticalAlignment = VerticalAlignment.Center;
                note.Children.Add(nt);
                stack.Children.Add(note);
            }

            if (expanded && p.Sessions.Count > 0)
            {
                var div = new Border { Height = 1, Margin = new Thickness(-14, 12, -14, 6) };
                div.SetResourceReference(Border.BackgroundProperty, "Divider");
                stack.Children.Add(div);
                var ordered = p.Sessions
                    .OrderByDescending(s => s.Status == "app")
                    .ThenByDescending(s => s.Status == "busy")
                    .ThenByDescending(s => s.Updated).ToList();
                foreach (var s in ordered.Take(6)) stack.Children.Add(SessionRow(s, brand));
                if (ordered.Count > 6)
                {
                    var more = Text("+" + (ordered.Count - 6) + " altre sessioni", 11.5, "Text3");
                    more.Margin = new Thickness(18, 2, 0, 0);
                    stack.Children.Add(more);
                }
            }
            return Card(stack, new Thickness(14, 12, 14, 12));
        }

        // ------------------------------------------------------------ finestre Fluent
        /// <summary>
        /// Rende la finestra "di sistema": niente barra del titolo, angoli arrotondati e ombra disegnati da DWM,
        /// sfondo Mica (o Acrilico) gestito dal compositore. Se il backdrop non è disponibile usa un colore pieno.
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
                var src = HwndSource.FromHwnd(hwnd);
                if (tool) Native.AddExStyle(hwnd, Native.WS_EX_TOOLWINDOW | (noActivate ? Native.WS_EX_NOACTIVATE : 0), Native.WS_EX_APPWINDOW);
                // niente menu di sistema: DWM non disegna pulsanti di chiusura sopra il contenuto
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
