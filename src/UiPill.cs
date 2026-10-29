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
        /// <summary>
        /// Draw concentric service rings, with Claude outside ChatGPT.
        /// Coloured arcs show remaining quota using the same thresholds as the bars.
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
        /// Build compact logo, value and bar rows for the overlay and taskbar pill.
        /// The optional dark flag controls logo variants; null follows the app theme.
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
                    // Show standalone spending values and cached readings without a live percentage bar.
                    if ((w.NoBar && w.RightText != null) || w.CachedAt.HasValue)
                    {
                        string value = w.RightText ?? (w.Spent != null ? w.Spent.Format(Math.Max(0, w.Spent.Limit - w.Spent.Used)) : Math.Round(w.Remaining).ToString(It) + "%");
                        var v = Text(w.CachedAt.HasValue ? L.F("{0} (cached)", value) : L.T(value), 12, "Text", FontWeights.SemiBold);
                        v.Margin = new Thickness(7, 0, 0, 0);
                        v.VerticalAlignment = VerticalAlignment.Center;
                        item.Children.Add(v);
                        any = true;
                        continue;
                    }
                    var stack = new StackPanel { Margin = new Thickness(7, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
                    double room = p.Usage.Room(w);
                    var t = Text(Math.Round(room).ToString(CultureInfo.InvariantCulture) + "%", 12, "Text", FontWeights.SemiBold);
                    t.HorizontalAlignment = HorizontalAlignment.Center;
                    stack.Children.Add(t);
                    var bar = Bar(room, room < w.Remaining ? LevelBrush(room, BrandBrush(p.Id)) : LevelBrush(w, BrandBrush(p.Id)), 2, w.Remaining);
                    bar.Width = 26;
                    bar.Margin = new Thickness(0, 1, 0, 0);
                    stack.Children.Add(bar);
                    item.Children.Add(stack);
                    any = true;
                }
                // Show a standalone balance instead of a percentage.
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
        /// Keep local models separate from cloud services in the pill; show GPU memory usage when known.
        /// Otherwise show the loaded model count, with an activity dot while models are working.
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
    }
}
