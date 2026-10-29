using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shell;
using System.Windows.Threading;

namespace AIMeter
{
    /// <summary>
    /// Windows 11-style settings with section navigation and one page per section.
    /// Each service has its own page reached from the service list.
    /// </summary>
    internal sealed partial class SettingsWindow : Window
    {
        private readonly Controller C;
        private Settings S { get { return C.Settings; } }
        private readonly StackPanel page = new StackPanel { Margin = new Thickness(36, 0, 36, 36), MaxWidth = 1000 };
        private readonly StackPanel nav = new StackPanel { Margin = new Thickness(8, 4, 8, 8) };
        private readonly ScrollViewer scroll;
        private readonly ColumnDefinition navColumn = new ColumnDefinition { Width = new GridLength(280) };
        private readonly DispatcherTimer stats = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        private TextBlock statsText;
        private TextBlock titleText;
        private TimeSpan lastCpu;
        private DateTime lastCpuAt;
        private bool compact;

        /// <summary>Current page: services, service:&lt;id&gt;, display, notifications, history, general or about.</summary>
        private string current = "services";

        private static readonly string[][] Sections =
        {
            new[] { "services", "", "Services" },
            new[] { "display", "", "Where to see it" },
            new[] { "notifications", "", "Notifications" },
            new[] { "history", "", "Usage history" },
            new[] { "general", "", "General" },
            new[] { "about", "", "About" },
        };

        public SettingsWindow(Controller c)
        {
            C = c;
            Title = L.T("AI Meter Settings");
            Width = 1000; Height = 740; MinWidth = 560; MinHeight = 480;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            FontFamily = Theme.Font;
            UseLayoutRounding = true;
            Icon = Controller.AppIconImage();
            Program.Brand(this);
            Ui.MakeSettingsWindow(this);

            var root = new DockPanel();
            var titleBar = new StackPanel { Orientation = Orientation.Horizontal, Height = 48, Margin = new Thickness(16, 0, 0, 0) };
            var ico = new Image { Source = Icon, Width = 16, Height = 16, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
            titleBar.Children.Add(ico);
            var tt = titleText = Ui.Text(Title, 12, "Text");
            tt.VerticalAlignment = VerticalAlignment.Center;
            titleBar.Children.Add(tt);
            DockPanel.SetDock(titleBar, Dock.Top);
            root.Children.Add(titleBar);

            var body = new Grid();
            body.ColumnDefinitions.Add(navColumn);
            body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var navScroll = new ScrollViewer { Content = nav, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Focusable = false };
            body.Children.Add(navScroll);
            scroll = new ScrollViewer { Content = page, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Focusable = false };
            Grid.SetColumn(scroll, 1);
            body.Children.Add(scroll);
            root.Children.Add(body);
            Content = root;

            // Collapse navigation to icons in narrow windows, matching Windows Settings.
            SizeChanged += (o, e) =>
            {
                bool c2 = e.NewSize.Width < 820;
                if (c2 == compact) return;
                compact = c2;
                navColumn.Width = new GridLength(compact ? 64 : 280);
                BuildNav();
            };

            stats.Tick += (o, e) => UpdateStats();
            Loaded += (o, e) => { lastCpu = Process.GetCurrentProcess().TotalProcessorTime; lastCpuAt = DateTime.Now; stats.Start(); };
            Closed += (o, e) => stats.Stop();
            Build();
        }

        /// <summary>Open a page and scroll to its top.</summary>
        internal void Open(string id)
        {
            current = id;
            Build();
            scroll.ScrollToTop();
        }

        public void Build()
        {
            Title = L.T("AI Meter Settings");
            if (titleText != null) titleText.Text = Title;
            BuildNav();
            page.Children.Clear();
            statsText = null;
            if (current.StartsWith("service:", StringComparison.Ordinal)) BuildService(current.Substring(8));
            else if (current == "display") BuildDisplay();
            else if (current == "notifications") BuildNotifications();
            else if (current == "history") BuildHistory();
            else if (current == "general") BuildGeneral();
            else if (current == "about") BuildAbout();
            else BuildServices();
        }

        // ------------------------------------------------------------ section navigation
        private void BuildNav()
        {
            nav.Children.Clear();
            string section = current.StartsWith("service:", StringComparison.Ordinal) ? "services" : current;
            foreach (var s in Sections)
            {
                string id = s[0];
                bool selected = id == section;
                var item = new Border { Height = 36, CornerRadius = new CornerRadius(4), Margin = new Thickness(0, 1, 0, 1), Background = Brushes.Transparent, Cursor = Cursors.Hand };
                if (selected) item.SetResourceReference(Border.BackgroundProperty, "SubtleHover");
                var g = new Grid();
                // Mark the active section with an accent bar on the left.
                var mark = new Border { Width = 3, Height = 16, CornerRadius = new CornerRadius(1.5), HorizontalAlignment = HorizontalAlignment.Left, Visibility = selected ? Visibility.Visible : Visibility.Hidden };
                mark.SetResourceReference(Border.BackgroundProperty, "Accent");
                g.Children.Add(mark);
                var glyph = Ui.Glyph(s[1], 16, "Text");
                glyph.HorizontalAlignment = HorizontalAlignment.Left;
                glyph.VerticalAlignment = VerticalAlignment.Center;
                glyph.Margin = new Thickness(compact ? 16 : 16, 0, 0, 0);
                g.Children.Add(glyph);
                if (!compact)
                {
                    var label = Ui.Text(L.T(s[2]), 14, "Text", selected ? FontWeights.SemiBold : FontWeights.Normal);
                    label.VerticalAlignment = VerticalAlignment.Center;
                    label.Margin = new Thickness(48, 0, 8, 0);
                    g.Children.Add(label);
                }
                else item.ToolTip = L.T(s[2]);
                item.Child = g;
                System.Windows.Automation.AutomationProperties.SetName(item, L.T(s[2]));
                if (!selected)
                {
                    item.MouseEnter += (o, e) => item.SetResourceReference(Border.BackgroundProperty, "SubtleHover");
                    item.MouseLeave += (o, e) => item.Background = Brushes.Transparent;
                }
                item.MouseLeftButtonUp += (o, e) => Open(id);
                item.Focusable = true;
                item.KeyDown += (o, e) =>
                {
                    if (e.Key == Key.Enter || e.Key == Key.Space) { Open(id); e.Handled = true; }
                };
                item.GotKeyboardFocus += (o, e) => { item.SetResourceReference(Border.BorderBrushProperty, "Accent"); item.BorderThickness = new Thickness(1); };
                item.LostKeyboardFocus += (o, e) => item.BorderThickness = new Thickness(0);
                nav.Children.Add(item);
            }
        }

        // ------------------------------------------------------------ page headings
        private void Header(string title)
        {
            var h = Ui.Text(title, 28, "Text", FontWeights.SemiBold);
            h.FontFamily = Theme.DisplayFont;
            h.Margin = new Thickness(0, 8, 0, 20);
            page.Children.Add(h);
        }

        /// <summary>The Services breadcrumb returns to the list, matching Windows Settings navigation.</summary>
        private void Breadcrumb(string parent, string parentId, string title)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 20) };
            var back = Ui.Text(parent, 28, "Text2", FontWeights.SemiBold);
            back.FontFamily = Theme.DisplayFont;
            back.Cursor = Cursors.Hand;
            back.MouseEnter += (o, e) => back.SetResourceReference(TextBlock.ForegroundProperty, "Text");
            back.MouseLeave += (o, e) => back.SetResourceReference(TextBlock.ForegroundProperty, "Text2");
            back.MouseLeftButtonUp += (o, e) => Open(parentId);
            row.Children.Add(back);
            var sep = Ui.Glyph("", 16, "Text2");
            sep.VerticalAlignment = VerticalAlignment.Center;
            sep.Margin = new Thickness(14, 4, 14, 0);
            row.Children.Add(sep);
            var h = Ui.Text(title, 28, "Text", FontWeights.SemiBold);
            h.FontFamily = Theme.DisplayFont;
            h.TextTrimming = TextTrimming.CharacterEllipsis;
            row.Children.Add(h);
            page.Children.Add(row);
        }

        private void Changed() { S.Save(); C.ApplySettings(); }

        /// <summary>Rebuild the page when a setting changes other controls.</summary>
        private void Rebuild() { Dispatcher.BeginInvoke(new Action(Build), System.Windows.Threading.DispatcherPriority.Background); }

        private void UpdateStats()
        {
            if (statsText == null) return;
            var proc = Process.GetCurrentProcess();
            proc.Refresh();
            var now = DateTime.Now;
            var cpu = proc.TotalProcessorTime;
            double pct = (cpu - lastCpu).TotalMilliseconds / Math.Max(1, (now - lastCpuAt).TotalMilliseconds) / Environment.ProcessorCount * 100;
            lastCpu = cpu; lastCpuAt = now;
            statsText.Text = L.F("Version {5} · memory in use {0:0} MB (private {1:0} MB) · CPU {2:0.0}% · refresh every {3} s{4}",
                proc.WorkingSet64 / 1048576.0, proc.PrivateMemorySize64 / 1048576.0, pct, C.CurrentInterval,
                C.SavingPower ? " " + L.T("(energy saving)") : "", Controller.Version);
        }
    }
}
