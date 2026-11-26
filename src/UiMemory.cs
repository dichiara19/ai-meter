using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace AIMeter
{
    /// <summary>
    /// The memory map of the local models card: for the graphics card, this PC and the WSL virtual machine, a bar
    /// split into what holds the memory (the models, WSL, everything else) with the free part left empty. The models
    /// keep one colour in every bar; a memory that is running out is told by the colour of its value.
    /// The WSL machine is part of the PC's memory, so its share is a segment of the PC's bar and its own bar,
    /// indented below, shows what happens inside it.
    /// </summary>
    internal partial class Ui
    {
        /// <summary>One holder of a memory: a segment of its bar and, when named, an entry of its legend.</summary>
        internal sealed class MemoryPart
        {
            public string Name;      // Legend text; null for a segment the legend does not list.
            public long Bytes;
            public string Brush;     // Theme resource.
            public double Opacity = 1;
            public bool Wsl;         // Carries the WSL logo in the legend.
            public bool Room;        // Not in use: memory its holder may still take, drawn as an outline.
        }

        private const string OtherBrush = "Text3";
        private const double WslOpacity = 0.42;

        private const string ModelsBrush = "Info";

        /// <summary>The brush of a memory's value: it warns, like a limit's bar, when little of the memory is left.</summary>
        internal static string MemoryLevel(long used, long total)
        {
            return LevelBrush(total > 0 ? 100 - Math.Min(100, used * 100.0 / total) : 100, "Text");
        }

        /// <summary>Video memory in use: the local models, then every other program.</summary>
        internal static List<MemoryPart> VideoMemoryParts(ProviderState p)
        {
            long models = Math.Min(p.Models.Sum(m => m.Vram), p.GpuUsed);
            return new List<MemoryPart>
            {
                new MemoryPart { Bytes = models, Brush = ModelsBrush },
                new MemoryPart { Bytes = p.GpuUsed - models, Brush = OtherBrush },
            };
        }

        /// <summary>
        /// The PC's memory in use: Windows programs, the models among them, then the WSL virtual machine with its
        /// models first. A model's figure can exceed what Windows counts for its place (memory paged out): it is
        /// cut to that, so the segments never add up to more than is in use. After WSL comes its room: how much more
        /// the virtual machine may take before its own ceiling, as far as the PC has it free.
        /// </summary>
        internal static List<MemoryPart> SystemMemoryParts(ProviderState p)
        {
            long wsl = Math.Min(p.WslHeld, p.RamUsed), windows = p.RamUsed - wsl;
            long modelsInWsl = Math.Min(p.Models.Where(m => m.Where == "WSL").Sum(m => m.Ram), wsl);
            long modelsOnWindows = Math.Min(p.Models.Where(m => m.Where != "WSL").Sum(m => m.Ram), windows);
            long room = p.WslTotal > wsl ? Math.Min(p.WslTotal - wsl, Math.Max(0, p.RamTotal - p.RamUsed)) : 0;
            string held = p.WslTotal > 0 ? GpuMemory.Pair(wsl, p.WslTotal) : GpuMemory.Format(wsl);
            return new List<MemoryPart>
            {
                new MemoryPart { Name = "Windows " + GpuMemory.Format(windows), Bytes = windows - modelsOnWindows, Brush = OtherBrush },
                new MemoryPart { Bytes = modelsOnWindows, Brush = ModelsBrush },
                new MemoryPart { Name = modelsInWsl + modelsOnWindows > 0 ? L.F("models {0}", GpuMemory.Format(modelsInWsl + modelsOnWindows)) : null, Bytes = modelsInWsl, Brush = ModelsBrush },
                new MemoryPart { Name = wsl > 0 ? "WSL " + held : null, Bytes = wsl - modelsInWsl, Brush = "Info", Opacity = WslOpacity, Wsl = true },
                new MemoryPart { Bytes = room, Brush = "Info", Opacity = WslOpacity, Room = true },
            };
        }

        /// <summary>Memory in use inside WSL: its models, then everything else running there.</summary>
        internal static List<MemoryPart> WslMemoryParts(ProviderState p)
        {
            long models = Math.Min(p.Models.Where(m => m.Where == "WSL").Sum(m => m.Ram), p.WslUsed);
            return new List<MemoryPart>
            {
                new MemoryPart { Bytes = models, Brush = ModelsBrush },
                new MemoryPart { Bytes = p.WslUsed - models, Brush = "Info", Opacity = WslOpacity },
            };
        }

        /// <summary>The memory rows of the local models card, for the memories that are known.</summary>
        private static IEnumerable<FrameworkElement> MemoryMap(ProviderState p)
        {
            if (p.GpuTotal > 0)
                yield return MemoryRow(null, L.T("Video memory"), p.GpuUsed, p.GpuTotal, VideoMemoryParts(p), 5, LocalMemoryLines(p).Take(1), 0);
            if (p.RamTotal > 0)
            {
                var parts = SystemMemoryParts(p);
                var row = MemoryRow(null, L.T("System memory"), p.RamUsed, p.RamTotal, parts, 5, SystemMemoryLines(p), 0);
                row.Children.Add(Legend(parts));
                yield return row;
            }
            if (p.WslTotal > 0)
                yield return MemoryRow(WslLogo(13), L.T("Inside WSL"), p.WslUsed, p.WslTotal, WslMemoryParts(p), 4, WslMemoryLines(p), 18);
        }

        /// <summary>A memory as a limit reads (free or in use out of the total), over the bar of what holds it.</summary>
        private static StackPanel MemoryRow(FrameworkElement icon, string label, long used, long total, List<MemoryPart> parts, double height, IEnumerable<string> lines, double indent)
        {
            var row = new StackPanel { Margin = new Thickness(indent, 6, 0, 0), ToolTip = string.Join("\n", lines) };
            var top = new DockPanel();
            var value = Text(GpuMemory.Value(used, total, ValueStyle.Long), indent > 0 ? 12 : 13, MemoryLevel(used, total), FontWeights.SemiBold);
            value.Margin = new Thickness(8, 0, 0, 0);
            DockPanel.SetDock(value, Dock.Right);
            top.Children.Add(value);
            if (icon != null)
            {
                icon.Margin = new Thickness(0, 0, 5, 0);
                DockPanel.SetDock(icon, Dock.Left);
                top.Children.Add(icon);
            }
            var name = Text(label, 12, "Text2");
            name.VerticalAlignment = VerticalAlignment.Bottom;
            name.TextTrimming = TextTrimming.CharacterEllipsis;
            top.Children.Add(name);
            row.Children.Add(top);
            var bar = SegmentedBar(parts, total, height);
            bar.Margin = new Thickness(0, 5, 0, 0);
            row.Children.Add(bar);
            return row;
        }

        /// <summary>
        /// One rounded segment per holder, as wide as its share of the total, over the track that stands for what is
        /// free. A holder too small to see still gets a dot, so that it is not taken for absent. A holder's room is
        /// an outline: free memory, up to the ceiling of the holder before it.
        /// </summary>
        internal static Grid SegmentedBar(List<MemoryPart> parts, long total, double height)
        {
            var bar = new Grid { Height = height };
            var track = new Border { CornerRadius = new CornerRadius(height / 2) };
            track.SetResourceReference(Border.BackgroundProperty, "Track");
            bar.Children.Add(track);
            var segments = new Grid();
            long free = Math.Max(0, total - parts.Sum(x => x.Bytes));
            foreach (var part in parts.Where(x => x.Bytes > 0))
            {
                segments.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(part.Bytes, GridUnitType.Star), MinWidth = height + 1.5 });
                var segment = new Border { CornerRadius = new CornerRadius(height / 2), Margin = new Thickness(0, 0, 1.5, 0), Opacity = part.Opacity };
                if (part.Room) segment.BorderThickness = new Thickness(1);
                segment.SetResourceReference(part.Room ? Border.BorderBrushProperty : Border.BackgroundProperty, part.Brush);
                Grid.SetColumn(segment, segments.ColumnDefinitions.Count - 1);
                segments.Children.Add(segment);
            }
            segments.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(free, 1), GridUnitType.Star) });
            bar.Children.Add(segments);
            return bar;
        }

        /// <summary>What the colours of a bar stand for, with each named holder's size; WSL carries its logo.</summary>
        private static FrameworkElement Legend(List<MemoryPart> parts)
        {
            var legend = new StackPanel { Orientation = Orientation.Horizontal };
            var named = parts.Where(x => x.Name != null).ToList();
            foreach (var part in named)
            {
                var entry = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, part == named[named.Count - 1] ? 0 : 9, 0) };
                var swatch = new Border { Width = 7, Height = 7, CornerRadius = new CornerRadius(2), Opacity = part.Opacity, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 1, 4, 0) };
                swatch.SetResourceReference(Border.BackgroundProperty, part.Brush);
                entry.Children.Add(swatch);
                var logo = part.Wsl ? WslLogo(12) : null;
                if (logo != null)
                {
                    logo.Margin = new Thickness(0, 0, 3, 0);
                    entry.Children.Add(logo);
                }
                entry.Children.Add(Text(part.Name, 11, "Text3"));
                legend.Children.Add(entry);
            }
            // Always one line: on a narrow surface, or in a language with longer words, the whole legend shrinks to fit.
            return new Viewbox { Child = legend, Stretch = Stretch.Uniform, StretchDirection = StretchDirection.DownOnly, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 4, 0, 0) };
        }

        /// <summary>
        /// The WSL logo, from the icon of the WSL installed on this PC: the one Microsoft ships with it, so it is not
        /// bundled. Windows' own wsl.exe stands in for a PC without the WSL package; null when neither has an icon.
        /// </summary>
        internal static FrameworkElement WslLogo(double size)
        {
            BitmapSource icon;
            if (!Logos.TryGetValue("wsl", out icon))
            {
                var programs = new[]
                {
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"WSL\wsl.exe"),
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "wsl.exe"),
                };
                string program = programs.FirstOrDefault(File.Exists);
                icon = program == null ? null : IconFromExe(program);
                Logos["wsl"] = icon;
            }
            if (icon == null) return null;
            var image = new Image { Source = icon, Width = size, Height = size, VerticalAlignment = VerticalAlignment.Center };
            RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
            return image;
        }
    }
}
