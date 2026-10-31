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
    /// <summary>UI components shared by the widget, panel and taskbar pill.</summary>
    internal static partial class Ui
    {
        private static CultureInfo It { get { return L.Culture; } }

        // ------------------------------------------------------------ text and icons
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
        /// Default to the five-hour session, then a main non-monetary limit, then a monetary service limit,
        /// then a secondary limit.
        /// </summary>
        public static UsageWindow Primary(Usage u)
        {
            if (u == null) return null;
            return u.Windows.FirstOrDefault(w => w.Short == UsageWindow.ShortFiveHours) ?? u.Windows.FirstOrDefault(w => w.Spent == null && w.HasPercent && w.Short != "m")
                   // Use the service's monetary limit by default; a user budget requires an explicit choice.
                   ?? u.Windows.FirstOrDefault(w => w.HasPercent && !w.Budget && !w.Hidden && w.Short != "m")
                   ?? u.Windows.FirstOrDefault(w => w.HasPercent && !w.Budget && !w.Hidden);
        }

        /// <summary>Resolve the service's chosen limit for the pill, icon and tooltips.</summary>
        public static UsageWindow Primary(ProviderState p)
        {
            var shown = Shown(p, "5h");
            return shown.Length > 0 ? shown[0] : null;
        }

        /// <summary>
        /// Use the explicit limit choice, or select the tightest limit in automatic mode.
        /// Otherwise follow the global five-hour, weekly or combined display setting.
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
            var week = p.Usage.Windows.FirstOrDefault(w => w.Short == UsageWindow.ShortWeek);
            var list = metric == "week" ? new[] { week ?? five } : metric == "both" ? new[] { five, week } : new[] { five };
            return list.Where(x => x != null).Distinct().ToArray();
        }

        /// <summary>
        /// Select the smallest remaining percentage. A forecast only breaks ties: a five-hour session forecast to
        /// run out must not hide a weekly limit with less room left. Then prefer the longest window, which blocks work for longer.
        /// </summary>
        public static UsageWindow Tightest(Usage u)
        {
            var list = u.Windows.Where(w => w.HasPercent).ToList();
            if (list.Count == 0) return null;
            return list.OrderBy(w => Math.Round(w.Remaining)).ThenBy(w => w.RunsOutAt ?? DateTime.MaxValue)
                       .ThenByDescending(w => w.Length ?? TimeSpan.Zero).First();
        }

        /// <summary>Warn about forecast exhaustion even when the current remaining percentage is high.</summary>
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

        // ------------------------------------------------------------ formatting
        public static string Ago(DateTime t)
        {
            if (t == DateTime.MinValue) return "";
            var s = (DateTime.Now - t).TotalSeconds;
            if (s < 60) return L.T("now");
            if (s < 3600) return L.F("{0} min", (int)(s / 60));
            if (s < 86400) return L.F("{0} h", (int)(s / 3600));
            return L.F("{0} d", (int)(s / 86400));
        }

        /// <summary>Format the age of a reading, such as "now" or "5 min ago".</summary>
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
                // For short sessions, show the start, end and remaining time.
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

        /// <summary>Translate the limit label while preserving an optional model suffix.</summary>
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

        // ------------------------------------------------------------ components
        public static Border Card(UIElement child, Thickness padding)
        {
            var b = new Border { CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1), Padding = padding, Child = child };
            b.SetResourceReference(Border.BackgroundProperty, "Card");
            b.SetResourceReference(Border.BorderBrushProperty, "CardStroke");
            return b;
        }

        /// <summary>Summarise local models for tooltips.</summary>
        public static string LocalSummary(ProviderState p)
        {
            if (p.Models == null || p.Models.Count == 0) return L.T("Local models: none loaded");
            var lines = new List<string> { L.N(p.Models.Count, "{0} local model loaded", "{0} local models loaded") };
            foreach (var m in p.Models) lines.Add("• " + m.Name + " · " + string.Join(" · ", LocalFacts(m)));
            if (p.GpuTotal > 0) lines.Add(L.F("Video memory: {0} of {1} in use", GpuMemory.Format(p.GpuUsed), GpuMemory.Format(p.GpuTotal)));
            return string.Join("\n", lines);
        }

        /// <summary>Format a local model's server, location, memory and context for compact card text.</summary>
        public static List<string> LocalFacts(LocalModel m)
        {
            var f = new List<string> { m.Where == "WSL" ? m.Server + " (WSL)" : m.Server };
            if (m.Vram > 0) f.Add(L.F("{0} VRAM", GpuMemory.Format(m.Vram)));
            if (m.Ram > 0) f.Add(L.F("{0} RAM", GpuMemory.Format(m.Ram)));
            if (m.Context > 0) f.Add(L.F("{0} context", Tokens(m.Context)));
            if (m.Busy > 0) f.Add(L.T("working"));
            return f;
        }

        /// <summary>Shorten a model name by removing its provider prefix.</summary>
        public static string ShortModel(string model)
        {
            if (string.IsNullOrEmpty(model)) return model;
            int slash = model.LastIndexOf('/');
            return slash >= 0 && slash < model.Length - 1 ? model.Substring(slash + 1) : model;
        }

        /// <summary>Format token counts compactly: 32768 becomes 32K, 40000 becomes 40K.</summary>
        public static string Tokens(int n)
        {
            if (n < 1000) return n.ToString(It);
            double k = n % 1024 == 0 ? n / 1024.0 : n / 1000.0;
            return (k >= 10 ? Math.Round(k).ToString(It) : k.ToString("0.#", It)) + "K";
        }

        /// <summary>Summarise five-hour limits for tooltips.</summary>
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

        /// <summary>
        /// Draw remaining quota with an optional faint fill behind the effective room.
        /// The faint fill shows a model's own quota when an enclosing limit constrains it.
        /// </summary>
        public static FrameworkElement Bar(double remaining, string brush, double height, double ghost = -1)
        {
            var g = new Grid { Height = height, VerticalAlignment = VerticalAlignment.Center };
            var track = new Border { CornerRadius = new CornerRadius(height / 2) };
            track.SetResourceReference(Border.BackgroundProperty, "Track");
            g.Children.Add(track);
            if (ghost > remaining) g.Children.Add(Fill(ghost, brush, height, 0.3));
            g.Children.Add(Fill(remaining, brush, height, 1));
            return g;
        }

        private static FrameworkElement Fill(double value, string brush, double height, double opacity)
        {
            var host = new Grid();
            host.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(0.0001, value), GridUnitType.Star) });
            host.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(0.0001, 100 - value), GridUnitType.Star) });
            var fill = new Border { CornerRadius = new CornerRadius(height / 2), MinWidth = value > 0 ? height : 0, Opacity = opacity };
            fill.SetResourceReference(Border.BackgroundProperty, brush);
            host.Children.Add(fill);
            return host;
        }
    }
}
