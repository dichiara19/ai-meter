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
    internal partial class SettingsWindow
    {
        // ------------------------------------------------------------ Settings-style controls
        private void Section(string title)
        {
            var t = Ui.Text(title, 14, "Text", FontWeights.SemiBold);
            t.Margin = new Thickness(0, 28, 0, 8);
            page.Children.Add(t);
        }

        /// <summary>
        /// Keep help text 8 px from its heading and associated card.
        /// After a previous card, use 12 px of combined spacing to separate the groups.
        /// </summary>
        private void Hint(string text)
        {
            var t = Ui.Text(text, 12, "Text3");
            t.TextWrapping = TextWrapping.Wrap;
            bool afterTitle = page.Children.Count > 0 && page.Children[page.Children.Count - 1] is TextBlock;
            t.Margin = new Thickness(2, afterTitle ? 0 : 8, 0, 8);
            page.Children.Add(t);
        }

        private void Group(params FrameworkElement[] rows) { page.Children.Add(Card(rows)); }

        /// <summary>A settings card: rows separated by dividers on the card background.</summary>
        internal static Border Card(params FrameworkElement[] rows)
        {
            var stack = new StackPanel();
            for (int i = 0; i < rows.Length; i++)
            {
                if (i > 0)
                {
                    var div = new Border { Height = 1 };
                    div.SetResourceReference(Border.BackgroundProperty, "Divider");
                    stack.Children.Add(div);
                }
                stack.Children.Add(rows[i]);
            }
            var card = new Border { CornerRadius = new CornerRadius(6), BorderThickness = new Thickness(1), Child = stack, Margin = new Thickness(0, 0, 0, 4) };
            card.SetResourceReference(Border.BackgroundProperty, "Card");
            card.SetResourceReference(Border.BorderBrushProperty, "CardStroke");
            return card;
        }

        /// <summary>
        /// Build a Settings-style navigation row with a logo, name, status, toggle and chevron.
        /// The entire row navigates except for the toggle.
        /// </summary>
        private static FrameworkElement Link(FrameworkElement icon, string title, string description, FrameworkElement control, Action open)
        {
            var chev = Ui.Glyph("", 12, "Text2");
            chev.VerticalAlignment = VerticalAlignment.Center;
            chev.Margin = new Thickness(16, 0, 0, 0);
            var right = new StackPanel { Orientation = Orientation.Horizontal };
            if (control != null) right.Children.Add(control);
            right.Children.Add(chev);
            var host = new Border { Background = Brushes.Transparent, Cursor = Cursors.Hand, CornerRadius = new CornerRadius(5), Child = Row(icon, title, description, right) };
            host.MouseEnter += (o, e) => host.SetResourceReference(Border.BackgroundProperty, "SubtleHover");
            host.MouseLeave += (o, e) => host.Background = Brushes.Transparent;
            host.MouseLeftButtonUp += (o, e) =>
            {
                // Do not navigate when the toggle is clicked.
                var d = e.OriginalSource as DependencyObject;
                while (d != null && d != host) { if (d is CheckBox) return; d = VisualTreeHelper.GetParent(d); }
                open();
            };
            System.Windows.Automation.AutomationProperties.SetName(host, title);
            return host;
        }

        internal static FrameworkElement Row(FrameworkElement icon, string title, string description, FrameworkElement control, FrameworkElement extra = null)
        {
            // Match Windows Settings card heights: 68 px with an icon, 56 px without, including margins.
            var g = new Grid { MinHeight = icon != null ? 48 : 36, Margin = new Thickness(16, 10, 16, 10) };
            // Reserve a 44 px icon column, widened for service-detail logos.
            double iw = icon != null && !double.IsNaN(icon.Width) ? icon.Width + 16 : 44;
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(44, iw)) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            if (icon != null)
            {
                icon.HorizontalAlignment = HorizontalAlignment.Left;
                icon.VerticalAlignment = VerticalAlignment.Center;
                g.Children.Add(icon);
            }
            var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 16, 0) };
            texts.Children.Add(Ui.Text(title, 14, "Text"));
            if (!string.IsNullOrEmpty(description))
            {
                var d = Ui.Text(description, 12, "Text2");
                d.TextWrapping = TextWrapping.Wrap;
                d.TextTrimming = TextTrimming.None;
                texts.Children.Add(d);
            }
            if (extra != null) { extra.Margin = new Thickness(0, 2, 0, 0); texts.Children.Add(extra); }
            Grid.SetColumn(texts, 1);
            g.Children.Add(texts);
            if (control != null)
            {
                control.VerticalAlignment = VerticalAlignment.Center;
                Grid.SetColumn(control, 2);
                g.Children.Add(control);
            }
            return g;
        }

        private static FrameworkElement Pair(FrameworkElement a, FrameworkElement b)
        {
            var sp = new StackPanel { Orientation = Orientation.Horizontal };
            a.Margin = new Thickness(0, 0, 16, 0);
            a.VerticalAlignment = VerticalAlignment.Center;
            sp.Children.Add(a);
            sp.Children.Add(b);
            return sp;
        }

        internal static CheckBox Toggle(bool value, Action<bool> changed)
        {
            var cb = new CheckBox { IsChecked = value };
            cb.SetResourceReference(FrameworkElement.StyleProperty, "ToggleSwitch");
            cb.Checked += (o, e) => changed(true);
            cb.Unchecked += (o, e) => changed(false);
            return cb;
        }

        private static Button Button(string label, Action click, bool accent)
        {
            var b = new Button { Content = label };
            b.SetResourceReference(FrameworkElement.StyleProperty, accent ? "AccentButton" : "StdButton");
            b.Click += (o, e) => click();
            return b;
        }

        private static ComboBox Combo(Tuple<string, string>[] options, string selected, Action<string> changed)
        {
            var cb = new ComboBox();
            cb.SetResourceReference(FrameworkElement.StyleProperty, "Combo");
            foreach (var o in options)
            {
                var item = new ComboBoxItem { Content = o.Item2, Tag = o.Item1 };
                cb.Items.Add(item);
                if (o.Item1 == selected) cb.SelectedItem = item;
            }
            if (cb.SelectedIndex < 0 && cb.Items.Count > 0) cb.SelectedIndex = 0;
            cb.SelectionChanged += (o, e) => { var it = cb.SelectedItem as ComboBoxItem; if (it != null) changed((string)it.Tag); };
            return cb;
        }
    }
}
