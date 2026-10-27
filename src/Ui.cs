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
        private static CultureInfo It { get { return L.Culture; } }
        private static readonly Dictionary<string, BitmapSource> Logos = new Dictionary<string, BitmapSource>();

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
        public static FrameworkElement Logo(string id, double size) { return Logo(id, size, Theme.Dark); }

        public static FrameworkElement Logo(string id, double size, bool dark)
        {
            string key = id + (dark ? ":dark" : ":light");
            BitmapSource bmp;
            if (!Logos.TryGetValue(key, out bmp) && id == "antigravity")
            {
                // Antigravity CLI non installa icone: il logo ufficiale è incluso nell'app (assets\antigravity.png)
                bmp = null;
                using (var s = Program.OpenAsset("antigravity.png"))
                    if (s != null)
                    {
                        var bi = new BitmapImage();
                        bi.BeginInit();
                        bi.StreamSource = s;
                        bi.DecodePixelWidth = 64;
                        bi.CacheOption = BitmapCacheOption.OnLoad;
                        bi.EndInit();
                        bi.Freeze();
                        bmp = bi;
                    }
                Logos[key] = bmp;
            }
            if (!Logos.TryGetValue(key, out bmp))
            {
                string file = FindInstalledLogo(id, dark);
                if (file != null && file.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                    bmp = IconFromExe(file);
                else if (file != null)
                {
                    try
                    {
                        var bi = new BitmapImage();
                        bi.BeginInit();
                        bi.UriSource = new Uri(file);
                        bi.DecodePixelWidth = 64;   // basta per 16-32 px a qualsiasi DPI e tiene bassa la memoria
                        bi.CacheOption = BitmapCacheOption.OnLoad;
                        bi.EndInit();
                        bi.Freeze();
                        bmp = bi;
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

        /// <summary>Icona principale di un eseguibile installato (per le app che non hanno un'immagine del logo).</summary>
        private static BitmapSource IconFromExe(string exe)
        {
            try
            {
                using (var ico = System.Drawing.Icon.ExtractAssociatedIcon(exe))
                {
                    if (ico == null) return null;
                    var src = Imaging.CreateBitmapSourceFromHIcon(ico.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                    src.Freeze();
                    return src;
                }
            }
            catch (Exception) { return null; }
        }

        private static FrameworkElement Monogram(string id, double size)
        {
            var b = new Border { Width = size, Height = size, CornerRadius = new CornerRadius(size * 0.24), VerticalAlignment = VerticalAlignment.Center };
            b.SetResourceReference(Border.BackgroundProperty, BrandBrush(id));
            b.Child = new TextBlock
            {
                Text = id == "openai" ? "G" : id == "claude" ? "C" : Definitions.Get(id) != null ? Definitions.Get(id).Monogram : "?", Foreground = Brushes.White, FontWeight = FontWeights.Bold, FontSize = size * 0.58,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            };
            return b;
        }

        /// <summary>Cerca l'icona nel pacchetto dell'app installata (registro dei pacchetti dell'utente).</summary>
        private static string FindInstalledLogo(string id, bool dark)
        {
            if (id == "copilot") return FindCopilotLogo();
            var def = Definitions.Get(id);
            if (def != null) return def.LogoFile();
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

        /// <summary>Icona ufficiale di Copilot, dall'estensione inclusa in VS Code.</summary>
        private static string FindCopilotLogo()
        {
            foreach (var baseDir in new[] { Path.Combine(Probe.LocalAppData, @"Programs\Microsoft VS Code"), Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Microsoft VS Code") })
            {
                try
                {
                    if (!Directory.Exists(baseDir)) continue;
                    foreach (var dir in new[] { baseDir }.Concat(Directory.GetDirectories(baseDir)))
                    {
                        string f = Path.Combine(dir, @"resources\app\extensions\copilot\assets\copilot.png");
                        if (File.Exists(f)) return f;
                    }
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            return null;
        }

        public static string BrandBrush(string id) { return id == "openai" ? "OpenAI" : id == "copilot" ? "Copilot" : id == "claude" ? "Claude" : id == "antigravity" ? "Antigravity" : "Brand." + id; }

        /// <summary>Il limite predefinito: la sessione di 5 ore, altrimenti il primo limite in percentuale.</summary>
        public static UsageWindow Primary(Usage u)
        {
            if (u == null) return null;
            return u.Windows.FirstOrDefault(w => w.Short == "5h") ?? u.Windows.FirstOrDefault(w => w.Spent == null && w.HasPercent);
        }

        /// <summary>Il limite da mostrare in pillola, icona e suggerimenti per questo servizio, secondo le impostazioni.</summary>
        public static UsageWindow Primary(ProviderState p)
        {
            var shown = Shown(p, "5h");
            return shown.Length > 0 ? shown[0] : null;
        }

        /// <summary>
        /// I limiti di un servizio da mostrare nella pillola: quello scelto nelle impostazioni ("auto" = il più vicino a
        /// esaurirsi), altrimenti quelli dell'impostazione generale della sovrimpressione (5 ore, settimana o entrambi).
        /// </summary>
        public static UsageWindow[] Shown(ProviderState p, string metric)
        {
            if (p.Usage == null) return new UsageWindow[0];
            var s = Settings.Current;
            string choice;
            if (s != null && s.PillLimits.TryGetValue(p.Id, out choice) && !string.IsNullOrEmpty(choice))
            {
                var w = choice == "auto" ? Tightest(p.Usage) : p.Usage.Windows.FirstOrDefault(x => x.Key == choice);
                if (w != null) return new[] { w };
            }
            var five = Primary(p.Usage);
            var week = p.Usage.Windows.FirstOrDefault(w => w.Short == "7g");
            var list = metric == "week" ? new[] { week ?? five } : metric == "both" ? new[] { five, week } : new[] { five };
            return list.Where(x => x != null).Distinct().ToArray();
        }

        /// <summary>
        /// Il limite più vicino a esaurirsi: prima quelli che a questo ritmo finiscono prima dell'azzeramento (il primo a
        /// finire), poi quello con meno margine. A parità, il più lungo, che è quello che blocca più a lungo.
        /// </summary>
        public static UsageWindow Tightest(Usage u)
        {
            var list = u.Windows.Where(w => w.HasPercent && w.Spent == null).ToList();
            if (list.Count == 0) return null;
            var running = list.Where(w => w.RunsOutAt.HasValue).OrderBy(w => w.RunsOutAt.Value).FirstOrDefault();
            if (running != null) return running;
            return list.OrderBy(w => Math.Round(w.Remaining)).ThenByDescending(w => w.Length ?? TimeSpan.Zero).First();
        }

        /// <summary>Colore della barra: avviso anche con margine se, a questo ritmo, finisce prima dell'azzeramento.</summary>
        public static string LevelBrush(UsageWindow w, string brand)
        {
            string level = LevelBrush(w.Remaining, brand);
            return level == brand && w.RunsOutAt.HasValue ? "Caution" : level;
        }

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
            if (s < 60) return L.T("now");
            if (s < 3600) return L.F("{0} min", (int)(s / 60));
            if (s < 86400) return L.F("{0} h", (int)(s / 3600));
            return L.F("{0} d", (int)(s / 86400));
        }

        /// <summary>"ora" oppure "5 min fa": quanto è vecchio un dato, in una frase.</summary>
        public static string AgoPhrase(DateTime t)
        {
            return (DateTime.Now - t).TotalSeconds < 60 ? L.T("just now") : L.F("{0} ago", Ago(t));
        }

        public static string Until(DateTime t)
        {
            var span = t - DateTime.Now;
            if (span.TotalSeconds < 0) span = TimeSpan.Zero;
            if (span.TotalDays >= 1) return L.F("{0} d {1} h", (int)span.TotalDays, span.Hours);
            if (span.TotalHours >= 1) return L.F("{0} h {1} min", (int)span.TotalHours, span.Minutes.ToString("00"));
            return L.F("{0} min", Math.Max(1, (int)span.TotalMinutes));
        }

        public static string ResetText(UsageWindow w, Usage u)
        {
            if (w.ResetsAt.HasValue && w.StartedAt.HasValue && (w.ResetsAt.Value - w.StartedAt.Value).TotalHours <= 24)
            {
                // finestre brevi (sessione): inizio e fine, più il tempo che manca
                var r = w.ResetsAt.Value;
                string span = w.StartedAt.Value.ToString("HH:mm", It) + " → " + r.ToString("HH:mm", It);
                return r > DateTime.Now ? L.F("{0}, in {1}", span, Until(r)) : span;
            }
            if (w.ResetsAt.HasValue)
            {
                var r = w.ResetsAt.Value;
                if (w.Spent != null) return L.F("renews {0}", r.ToString("d MMM", It));
                if ((r - DateTime.Now).TotalHours < 24 && r.Date == DateTime.Today) return L.F("resets in {0}", Until(r));
                if ((r - DateTime.Now).TotalHours < 24) return L.F("resets tomorrow {0}", r.ToString("HH:mm", It));
                return L.F("resets {0}", r.ToString("ddd HH:mm", It));
            }
            if (u != null && u.Source == "cache") return L.F("updated {0}", AgoPhrase(u.At));
            return "";
        }

        /// <summary>Nome del limite nella lingua attiva: l'etichetta è una chiave inglese, l'eventuale suffisso (modello) resta com'è.</summary>
        public static string LabelOf(UsageWindow w)
        {
            return w.Suffix == null ? L.T(w.Label) : L.T(w.Label) + " · " + w.Suffix;
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

        /// <summary>
        /// Riga compatta "logo + % rimasto + barretta" per ogni servizio: è il contenuto della pillola,
        /// sia in sovrimpressione sia nella barra delle applicazioni. dark: tema per i loghi (null = tema dell'app).
        /// </summary>
        public static StackPanel PillRow(Snapshot s, string metric, bool? dark)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            foreach (var p in s.Providers.Where(x => x.Models == null))
            {
                if (row.Children.Count > 0)
                {
                    var sep = new Border { Width = 1, Margin = new Thickness(10, 2, 10, 2) };
                    sep.SetResourceReference(Border.BackgroundProperty, "Divider");
                    row.Children.Add(sep);
                }
                var item = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
                var logoHost = new Grid { Width = 16, Height = 16, VerticalAlignment = VerticalAlignment.Center };
                logoHost.Children.Add(Logo(p.Id, 16, dark ?? Theme.Dark));
                if (p.Busy > 0)
                {
                    var dot = new Border { Width = 7, Height = 7, CornerRadius = new CornerRadius(3.5), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, -2, -2, 0), BorderThickness = new Thickness(1.2) };
                    dot.SetResourceReference(Border.BackgroundProperty, BrandBrush(p.Id));
                    dot.SetResourceReference(Border.BorderBrushProperty, "Fallback");
                    logoHost.Children.Add(dot);
                }
                item.Children.Add(logoHost);

                var windows = p.Usage == null ? new UsageWindow[0] : p.Usage.Windows.ToArray();
                bool any = false;
                foreach (var w in Shown(p, metric))
                {
                    var stack = new StackPanel { Margin = new Thickness(7, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
                    double room = p.Usage.Room(w);
                    var t = Text(Math.Round(room).ToString(CultureInfo.InvariantCulture) + "%", 12, "Text", FontWeights.SemiBold);
                    t.HorizontalAlignment = HorizontalAlignment.Center;
                    stack.Children.Add(t);
                    var bar = Bar(room, room < w.Remaining ? LevelBrush(room, BrandBrush(p.Id)) : LevelBrush(w, BrandBrush(p.Id)), 2);
                    bar.Width = 26;
                    bar.Margin = new Thickness(0, 1, 0, 0);
                    stack.Children.Add(bar);
                    item.Children.Add(stack);
                    any = true;
                }
                // servizi con un saldo senza limite: l'importo al posto della percentuale
                var balance = windows.FirstOrDefault(w => w.NoBar && w.RightText != null);
                if (!any && balance != null) { var t = Text(balance.RightText, 12, "Text", FontWeights.SemiBold); t.Margin = new Thickness(7, 0, 0, 0); item.Children.Add(t); any = true; }
                if (!any) { var t = Text("–", 12, "Text3"); t.Margin = new Thickness(7, 0, 0, 0); item.Children.Add(t); }
                row.Children.Add(item);
            }
            var local = s.Providers.FirstOrDefault(x => x.Models != null);
            if (local != null && local.Running) row.Children.Add(LocalPill(local, row.Children.Count > 0));
            if (row.Children.Count == 0) row.Children.Add(Text("AI Meter", 12, "Text2"));
            return row;
        }

        /// <summary>
        /// Area dei modelli locali nella pillola, staccata dai servizi cloud: memoria video occupata (barra sulla
        /// capienza della scheda) o, se non nota, il numero di modelli caricati; un punto se stanno lavorando.
        /// </summary>
        private static FrameworkElement LocalPill(ProviderState p, bool afterCloud)
        {
            var chip = new Border { CornerRadius = new CornerRadius(5), Padding = new Thickness(6, 2, 7, 2), Margin = new Thickness(afterCloud ? 12 : 0, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            chip.SetResourceReference(Border.BackgroundProperty, "SubtleHover");
            var item = new StackPanel { Orientation = Orientation.Horizontal };
            var iconHost = new Grid { Width = 16, Height = 16, VerticalAlignment = VerticalAlignment.Center };
            iconHost.Children.Add(Glyph(LocalGlyph, 14, "Text"));
            if (p.Models.Any(m => m.Busy > 0))
            {
                var dot = new Border { Width = 7, Height = 7, CornerRadius = new CornerRadius(3.5), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, -2, -3, 0), BorderThickness = new Thickness(1.2) };
                dot.SetResourceReference(Border.BackgroundProperty, "Success");
                dot.SetResourceReference(Border.BorderBrushProperty, "Fallback");
                iconHost.Children.Add(dot);
            }
            item.Children.Add(iconHost);
            long vram = p.Models.Sum(m => m.Vram), total = p.GpuTotal;
            var stack = new StackPanel { Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            string text = vram > 0 ? GpuMemory.Format(vram) : p.Models.Count.ToString(It);
            var t = Text(text, 12, "Text", FontWeights.SemiBold);
            t.HorizontalAlignment = HorizontalAlignment.Center;
            stack.Children.Add(t);
            if (vram > 0 && total > 0)
            {
                double free = 100 - Math.Min(100, p.GpuUsed * 100.0 / total);
                var bar = Bar(free, LevelBrush(free, "Info"), 2);
                bar.Width = 26;
                bar.Margin = new Thickness(0, 1, 0, 0);
                stack.Children.Add(bar);
            }
            item.Children.Add(stack);
            chip.Child = item;
            chip.ToolTip = LocalSummary(p);
            return chip;
        }

        public const string LocalGlyph = "";

        /// <summary>Riepilogo dei modelli locali, per i suggerimenti.</summary>
        public static string LocalSummary(ProviderState p)
        {
            if (p.Models == null || p.Models.Count == 0) return L.T("Local models: none loaded");
            var lines = new List<string> { L.N(p.Models.Count, "{0} local model loaded", "{0} local models loaded") };
            foreach (var m in p.Models) lines.Add("• " + m.Name + " · " + string.Join(" · ", LocalFacts(m)));
            if (p.GpuTotal > 0) lines.Add(L.F("Video memory: {0} of {1} in use", GpuMemory.Format(p.GpuUsed), GpuMemory.Format(p.GpuTotal)));
            return string.Join("\n", lines);
        }

        /// <summary>Server, dove gira, memoria e contesto di un modello locale, nei testi brevi della scheda.</summary>
        public static List<string> LocalFacts(LocalModel m)
        {
            var f = new List<string> { m.Where == "WSL" ? m.Server + " (WSL)" : m.Server };
            if (m.Vram > 0) f.Add(L.F("{0} VRAM", GpuMemory.Format(m.Vram)));
            if (m.Ram > 0) f.Add(L.F("{0} RAM", GpuMemory.Format(m.Ram)));
            if (m.Context > 0) f.Add(L.F("{0} context", Tokens(m.Context)));
            if (m.Busy > 0) f.Add(L.T("working"));
            return f;
        }

        /// <summary>Token in forma breve: 32768 → 32K, 40000 → 40K.</summary>
        public static string Tokens(int n)
        {
            if (n < 1000) return n.ToString(It);
            double k = n % 1024 == 0 ? n / 1024.0 : n / 1000.0;
            return (k >= 10 ? Math.Round(k).ToString(It) : k.ToString("0.#", It)) + "K";
        }

        /// <summary>Riepilogo testuale dei limiti a 5 ore, per i suggerimenti.</summary>
        public static string Summary(Snapshot s)
        {
            return string.Join("\n", s.Providers.Select(p =>
            {
                if (p.Models != null) return LocalSummary(p);
                var w = Primary(p);
                string n = p.Name;
                return w == null ? L.F("{0}: no data", n) : L.F("{0}: {1}% left ({2})", n, Math.Round(p.Usage.Room(w)), LabelOf(w)) +
                       (w.ResetsAt.HasValue ? L.F(", resets {0}", Alerts.When(w.ResetsAt.Value)) : "") +
                       (w.RunsOutAt.HasValue ? " · " + L.F("runs out {0}", Alerts.When(w.RunsOutAt.Value)) : "");
            }));
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
            // per i crediti in denaro si mostra quanto resta, non la percentuale
            // limite di un modello dentro uno generale con meno margine: si mostra il margine vero
            var parent = u == null ? null : u.ParentOf(w);
            bool capped = parent != null && parent.Remaining < w.Remaining;
            double room = capped ? parent.Remaining : w.Remaining;
            string right = w.RightText != null ? L.T(w.RightText) : (w.Spent != null ? w.Spent.Format(Math.Max(0, w.Spent.Limit - w.Spent.Used)) : Math.Round(room).ToString(It) + "%");
            var pct = Text(right, 13, "Text", FontWeights.SemiBold);
            pct.Margin = new Thickness(8, 0, 0, 0);
            DockPanel.SetDock(pct, Dock.Right);
            top.Children.Add(pct);
            var left = new TextBlock { FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Bottom };
            var lbl = new System.Windows.Documents.Run(LabelOf(w));
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
            if (w.NoBar) { root.ToolTip = LabelOf(w) + ": " + right; return root; }
            var bar = Bar(room, capped ? LevelBrush(room, brand) : LevelBrush(w, brand), 4);
            bar.Margin = new Thickness(0, 5, 0, 0);
            root.Children.Add(bar);
            if (capped)
            {
                var c = Text(L.F("{0:0}% of its own limit, but {1} has {2:0}% left", w.Remaining, LabelOf(parent), parent.Remaining), 11.5, "Text3");
                c.Margin = new Thickness(0, 4, 0, 0);
                c.TextWrapping = TextWrapping.Wrap;
                root.Children.Add(c);
            }
            // previsione: a questo ritmo finisce prima dell'azzeramento
            if (w.RunsOutAt.HasValue)
            {
                var f = Text(L.F("At this pace it runs out {0}.", Alerts.When(w.RunsOutAt.Value)).TrimEnd('.'), 11.5, "Caution");
                f.Margin = new Thickness(0, 4, 0, 0);
                root.Children.Add(f);
            }
            string when = "dddd d MMMM, HH:mm";
            root.ToolTip = (w.Spent != null
                    ? L.F("{0}: {1} used of {2}", LabelOf(w), w.Spent.Format(w.Spent.Used), w.Spent.Format(w.Spent.Limit))
                    : L.F("{0}: {1:0}% used, {2:0}% left", LabelOf(w), w.Used, w.Remaining))
                + (w.Detail != null ? "\n" + w.Detail() : "")
                + (w.RunsOutAt.HasValue ? "\n" + L.F("At this pace it runs out {0}.", Alerts.When(w.RunsOutAt.Value)) : "")
                + (w.StartedAt.HasValue ? "\n" + L.F("Started {0}", w.StartedAt.Value.ToString(when, It)) : "")
                + (w.ResetsAt.HasValue ? "\n" + L.F(w.Spent != null ? "Renews {0}" : "Resets {0}", w.ResetsAt.Value.ToString(when, It)) : "");
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
            texts.Children.Add(Text(L.T(s.Title), 13, s.Status == "idle" && (DateTime.Now - s.Updated).TotalHours > 3 ? "Text2" : "Text"));
            var meta = new List<string> { L.T(s.Kind) };
            string model = PrettyModel(s.Model);
            if (model != null) meta.Add(model);
            if (s.Note != null) meta.Add(s.Note);
            if (s.Status == "busy") meta.Add(L.T("working"));
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
            g.ToolTip = L.T(s.Title) + "\n" + string.Join(" · ", meta) + (s.Status != "app" ? "\n" + L.F("Last activity: {0}", s.Updated.ToString("HH:mm", It)) : "");
            return g;
        }

        /// <summary>Scheda di un servizio: intestazione, limiti, avviso, elenco sessioni (se espansa).</summary>
        public static Border ProviderCard(ProviderState p, bool expanded, Action toggle, Action<string> login)
        {
            if (p.Models != null) return LocalCard(p, expanded, toggle);
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
                count.Children.Add(Text(L.N(p.Active, "{0} session", "{0} sessions"), 12, "Text2"));
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
                    pill.ToolTip = L.N(p.Busy, "{0} session is working right now", "{0} sessions are working right now");
                    count.Children.Add(pill);
                }
            }
            else count.Children.Add(Text(L.T("not active"), 12, "Text3"));
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
                foreach (var w in p.Usage.Windows.Take(6)) limits.Children.Add(UsageRow(w, p.Usage, brand));
            else if (!p.NeedsLogin)
                limits.Children.Add(new TextBlock { Text = L.T("No limit data yet"), FontSize = 12, Margin = new Thickness(0, 6, 0, 0), Foreground = Theme.B("Text3") });
            stack.Children.Add(limits);

            // dato sui limiti non in tempo reale: lo si dice nella scheda, una volta sola
            if (p.Usage != null && p.Usage.Source != "live" && string.IsNullOrEmpty(p.Notice))
            {
                var st = Text(L.F("Limits updated {0}", AgoPhrase(p.Usage.At)), 11.5, "Text3");
                st.Margin = new Thickness(0, 8, 0, 0);
                stack.Children.Add(st);
            }

            // ripartizione della settimana e crediti extra, in piccolo
            if (p.Usage != null && p.Usage.Breakdown.Count > 0)
            {
                var b = Text(L.F("This week: {0}", string.Join(" · ", p.Usage.Breakdown.Select(kv => kv.Key + " " + Math.Round(kv.Value) + "%"))), 11.5, "Text3");
                b.Margin = new Thickness(0, 8, 0, 0);
                b.TextWrapping = TextWrapping.Wrap;
                stack.Children.Add(b);
            }
            if (p.Usage != null && p.Usage.CreditsNote != null)
            {
                var c = Text(p.Usage.CreditsNote(), 11.5, "Text3");
                c.Margin = new Thickness(0, p.Usage.Breakdown.Count > 0 ? 2 : 8, 0, 0);
                c.TextWrapping = TextWrapping.Wrap;
                stack.Children.Add(c);
            }

            // suggerimento attivo (margine che sta per andare perso, o aspettare invece di pagare)
            if (p.Tip != null)
            {
                var tip = new DockPanel { Margin = new Thickness(0, 8, 0, 0) };
                var ic = Glyph("", 12, brand);
                ic.Margin = new Thickness(0, 1, 6, 0);
                ic.VerticalAlignment = VerticalAlignment.Top;
                DockPanel.SetDock(ic, Dock.Left);
                tip.Children.Add(ic);
                var t = Text(p.Tip.Line, 11.5, "Text2");
                t.TextWrapping = TextWrapping.Wrap;
                tip.Children.Add(t);
                tip.ToolTip = p.Tip.Text;
                stack.Children.Add(tip);
            }

            if (!string.IsNullOrEmpty(p.Notice))
            {
                var note = new DockPanel { Margin = new Thickness(0, 8, 0, 0) };
                var ic = Glyph("", 12, "Text3");
                ic.Margin = new Thickness(0, 0, 6, 0);
                DockPanel.SetDock(ic, Dock.Left);
                note.Children.Add(ic);
                if (p.NeedsLogin && p.CanSignIn && login != null)
                {
                    var link = new Button { Content = L.T("Sign in"), Padding = new Thickness(4, 0, 4, 1), FontSize = 12, Margin = new Thickness(4, 0, 0, 0) };
                    link.SetResourceReference(FrameworkElement.StyleProperty, "LinkButton");
                    link.Click += (o, e) => login(p.Id);
                    DockPanel.SetDock(link, Dock.Right);
                    note.Children.Add(link);
                }
                var nt = Text(p.Notice, 11.5, "Text3");
                nt.VerticalAlignment = VerticalAlignment.Center;
                nt.TextWrapping = TextWrapping.Wrap;
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
                    var more = Text(L.F("+{0} more sessions", ordered.Count - 6), 11.5, "Text3");
                    more.Margin = new Thickness(18, 2, 0, 0);
                    stack.Children.Add(more);
                }
            }
            return Card(stack, new Thickness(14, 12, 14, 12));
        }

        /// <summary>
        /// Scheda dei modelli locali: memoria video della scheda, poi ogni modello caricato con server, memoria e contesto,
        /// e sotto le sessioni delle CLI che lo usano. Le altre sessioni delle CLI compaiono in fondo.
        /// </summary>
        public static Border LocalCard(ProviderState p, bool expanded, Action toggle)
        {
            var stack = new StackPanel();
            var head = new DockPanel { Background = Brushes.Transparent, Cursor = toggle != null ? Cursors.Hand : null };
            var icon = Glyph(LocalGlyph, 18, "Text");
            icon.Width = 20;
            icon.Margin = new Thickness(0, 0, 10, 0);
            DockPanel.SetDock(icon, Dock.Left);
            head.Children.Add(icon);
            if (toggle != null)
            {
                var chev = Glyph(expanded ? "" : "", 10, "Text3");
                chev.Margin = new Thickness(10, 0, 2, 0);
                DockPanel.SetDock(chev, Dock.Right);
                head.Children.Add(chev);
                head.MouseLeftButtonUp += (o, e) => { toggle(); e.Handled = true; };
            }
            var count = Text(!p.Running ? L.T("no server running") : p.Models.Count == 0 ? L.T("none loaded") : L.N(p.Models.Count, "{0} loaded", "{0} loaded"), 12, p.Models.Count > 0 ? "Text2" : "Text3");
            count.VerticalAlignment = VerticalAlignment.Center;
            DockPanel.SetDock(count, Dock.Right);
            head.Children.Add(count);
            var name = Text(p.Name, 14, "Text", FontWeights.SemiBold);
            name.FontFamily = Theme.DisplayFont;
            name.VerticalAlignment = VerticalAlignment.Center;
            head.Children.Add(name);
            stack.Children.Add(head);

            // memoria video della scheda: è il vero limite dei modelli locali
            if (p.Running && p.GpuTotal > 0)
            {
                double free = 100 - Math.Min(100, p.GpuUsed * 100.0 / p.GpuTotal);
                var w = new UsageWindow { Label = "Video memory", Used = 100 - free, RightText = L.F("{0} free", GpuMemory.Format(Math.Max(0, p.GpuTotal - p.GpuUsed))) };
                var row = UsageRow(w, null, "Info");
                row.ToolTip = L.F("Video memory: {0} of {1} in use", GpuMemory.Format(p.GpuUsed), GpuMemory.Format(p.GpuTotal));
                stack.Children.Add(row);
            }

            foreach (var m in p.Models)
            {
                var block = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };
                var top = new DockPanel();
                string mem = m.Vram > 0 ? GpuMemory.Format(m.Vram) : m.Ram > 0 ? GpuMemory.Format(m.Ram) : null;
                if (mem != null)
                {
                    var r = Text(mem, 13, "Text", FontWeights.SemiBold);
                    r.Margin = new Thickness(8, 0, 0, 0);
                    DockPanel.SetDock(r, Dock.Right);
                    top.Children.Add(r);
                }
                var dot = new Border { Width = 8, Height = 8, CornerRadius = new CornerRadius(4), Margin = new Thickness(1, 0, 9, 0), VerticalAlignment = VerticalAlignment.Center };
                dot.SetResourceReference(Border.BackgroundProperty, m.Busy > 0 ? "Success" : "Track");
                DockPanel.SetDock(dot, Dock.Left);
                top.Children.Add(dot);
                var title = Text(m.Name, 13, "Text", FontWeights.SemiBold);
                title.TextTrimming = TextTrimming.CharacterEllipsis;
                top.Children.Add(title);
                block.Children.Add(top);

                var facts = LocalFacts(m).Where(f => !f.Contains("VRAM") || mem == null).ToList();
                if (m.Detail != null) facts.Insert(1, m.Detail);
                var meta = Text(string.Join(" · ", facts), 11.5, "Text3");
                meta.Margin = new Thickness(18, 1, 0, 0);
                meta.TextTrimming = TextTrimming.CharacterEllipsis;
                block.Children.Add(meta);

                // contesto occupato dalla richiesta in corso, se il server lo dice
                if (m.Context > 0 && m.ContextUsed > 0)
                {
                    double left = 100 - Math.Min(100, m.ContextUsed * 100.0 / m.Context);
                    var bar = Bar(left, LevelBrush(left, "Info"), 3);
                    bar.Margin = new Thickness(18, 5, 0, 0);
                    bar.ToolTip = L.F("Context in use: {0} of {1} tokens", Tokens(m.ContextUsed), Tokens(m.Context));
                    block.Children.Add(bar);
                }
                block.ToolTip = m.Name + "\n" + string.Join(" · ", LocalFacts(m)) + (m.Slots > 0 ? "\n" + L.F("Requests in progress: {0} of {1}", m.Busy, m.Slots) : "");
                foreach (var s in m.Sessions.Take(4))
                {
                    var sr = SessionRow(s, "Info");
                    sr.Margin = new Thickness(18, 2, 0, 0);
                    block.Children.Add(sr);
                }
                stack.Children.Add(block);
            }

            // sessioni delle CLI che non usano un modello caricato qui (cloud o server non raggiungibile)
            if (expanded && p.Sessions.Count > 0)
            {
                var div = new Border { Height = 1, Margin = new Thickness(-14, 12, -14, 6) };
                div.SetResourceReference(Border.BackgroundProperty, "Divider");
                stack.Children.Add(div);
                var sub = Text(L.T("Coding agents"), 11.5, "Text3", FontWeights.SemiBold);
                sub.Margin = new Thickness(0, 0, 0, 2);
                stack.Children.Add(sub);
                foreach (var s in p.Sessions.OrderByDescending(x => x.Status == "busy").ThenByDescending(x => x.Updated).Take(6)) stack.Children.Add(SessionRow(s, "Info"));
            }
            if (!p.Running && p.Sessions.Count == 0)
            {
                var none = Text(L.T("Ollama, LM Studio, llama.cpp and other local servers appear here while they run, on Windows or in WSL."), 12, "Text3");
                none.TextWrapping = TextWrapping.Wrap;
                none.Margin = new Thickness(0, 6, 0, 0);
                stack.Children.Add(none);
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
