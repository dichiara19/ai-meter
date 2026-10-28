using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace AIMeter
{
    internal sealed class HistoryChartPoint
    {
        internal DateTime At;
        internal double? Value;
        internal long? Reset;
    }

    /// <summary>Presentation primitives using the existing settings palette, spacing and native WPF controls.</summary>
    internal static class HistoryVisuals
    {
        private static Style focusStyle;

        internal static void Focusable(Control control, string name)
        {
            if (focusStyle == null) focusStyle = (Style)System.Windows.Markup.XamlReader.Parse(@"
                <Style xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' TargetType='Control'>
                  <Setter Property='Template'><Setter.Value><ControlTemplate>
                    <Border BorderBrush='{DynamicResource Accent}' BorderThickness='2' CornerRadius='4' Margin='-3'/>
                  </ControlTemplate></Setter.Value></Setter>
                </Style>");
            control.FocusVisualStyle = focusStyle;
            AutomationProperties.SetName(control, name);
        }

        internal static TextBlock Note(string text)
        {
            var label = Ui.Text(L.T(text), 13, "Text2");
            label.TextWrapping = TextWrapping.Wrap; label.TextTrimming = TextTrimming.None;
            label.Margin = new Thickness(0, 6, 0, 10); label.MaxWidth = 760; label.HorizontalAlignment = HorizontalAlignment.Left;
            return label;
        }

        internal static Border Card(string title, string description, params UIElement[] content)
        {
            var body = new StackPanel();
            if (title != null)
            {
                var heading = Ui.Text(L.T(title), 18, "Text", FontWeights.SemiBold);
                heading.TextWrapping = TextWrapping.Wrap; body.Children.Add(heading);
            }
            if (description != null) body.Children.Add(Note(description));
            foreach (var element in content) body.Children.Add(element);
            var card = Ui.Card(body, new Thickness(20));
            card.Margin = new Thickness(0, 0, 0, 16);
            return card;
        }

        internal static Border Message(string title, string description, UIElement action)
        {
            var icon = Ui.Glyph("\uE946", 20, "Info"); icon.VerticalAlignment = VerticalAlignment.Top; icon.Margin = new Thickness(0, 2, 14, 0);
            var text = new StackPanel();
            var heading = Ui.Text(L.T(title), 15, "Text", FontWeights.SemiBold); heading.TextWrapping = TextWrapping.Wrap;
            text.Children.Add(heading); text.Children.Add(Note(description));
            if (action != null) text.Children.Add(action);
            var row = new Grid(); row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); row.ColumnDefinitions.Add(new ColumnDefinition());
            row.Children.Add(icon); Grid.SetColumn(text, 1); row.Children.Add(text);
            return Card(null, null, row);
        }

        internal static Expander Details(string title, params UIElement[] content)
        {
            var body = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };
            foreach (var element in content) body.Children.Add(element);
            var expander = new Expander { Header = L.T(title), Content = body, Margin = new Thickness(0, 8, 0, 0) };
            expander.SetResourceReference(Control.ForegroundProperty, "Text");
            expander.Template = (ControlTemplate)System.Windows.Markup.XamlReader.Parse(@"
                <ControlTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' TargetType='Expander'>
                  <StackPanel>
                    <ToggleButton x:Name='HeaderSite' Content='{TemplateBinding Header}' Foreground='{TemplateBinding Foreground}'
                        IsChecked='{Binding IsExpanded,RelativeSource={RelativeSource TemplatedParent},Mode=TwoWay}' FocusVisualStyle='{TemplateBinding FocusVisualStyle}'>
                      <ToggleButton.Template><ControlTemplate TargetType='ToggleButton'>
                        <Border Background='Transparent' Padding='0,10'><Grid>
                          <Grid.ColumnDefinitions><ColumnDefinition/><ColumnDefinition Width='Auto'/></Grid.ColumnDefinitions>
                          <ContentPresenter VerticalAlignment='Center'/>
                          <TextBlock x:Name='Chevron' Grid.Column='1' Margin='12,0,0,0' FontFamily='Segoe Fluent Icons, Segoe MDL2 Assets' Text='&#xE70D;' FontSize='12'/>
                        </Grid></Border>
                        <ControlTemplate.Triggers><Trigger Property='IsChecked' Value='True'><Setter TargetName='Chevron' Property='Text' Value='&#xE70E;'/></Trigger></ControlTemplate.Triggers>
                      </ControlTemplate></ToggleButton.Template>
                    </ToggleButton>
                    <ContentPresenter x:Name='ExpandSite' Visibility='Collapsed' ContentSource='Content'/>
                  </StackPanel>
                  <ControlTemplate.Triggers><Trigger Property='IsExpanded' Value='True'><Setter TargetName='ExpandSite' Property='Visibility' Value='Visible'/></Trigger></ControlTemplate.Triggers>
                </ControlTemplate>");
            Focusable(expander, L.T(title)); return expander;
        }

        internal static string ProviderName(string id)
        {
            if (id == "claude") return "Claude";
            if (id == "openai") return "ChatGPT";
            if (id == "copilot") return "GitHub Copilot";
            var definition = Definitions.Get(id);
            return definition == null ? id : definition.Name;
        }

        internal static string Compact(double value)
        {
            if (Math.Abs(value) >= 1000000) return (value / 1000000).ToString("0.#", L.Culture) + "M";
            if (Math.Abs(value) >= 1000) return (value / 1000).ToString("0.#", L.Culture) + "k";
            return value.ToString(value != 0 && Math.Abs(value) < 0.000001 ? "0.#E+0" : Math.Abs(value) < 0.01 && value != 0 ? "0.######" : Math.Abs(value) < 1 && value != 0 ? "0.##" : "0.#", L.Culture);
        }

        /// <summary>A responsive chart with labelled axes. Missing values and reset/gap boundaries never become interpolated usage.</summary>
        internal static FrameworkElement Chart(List<HistoryChartPoint> points, string unit, bool quota)
        {
            var known = points.Where(point => point.Value.HasValue && HistoryStore.Finite(point.Value.Value)).OrderBy(point => point.At).ToList();
            if (known.Count == 0)
                return Message("No values to plot yet", "This chart will appear when the selected measurement is reported. Missing values are not zero.", null);

            var panel = new StackPanel();
            var canvas = new Canvas { Height = 164, ClipToBounds = true, Margin = new Thickness(0, 14, 0, 0) };
            string summary = L.F("{0} recorded points · {1} to {2}", known.Count, known[0].At.ToString("g", L.Culture), known[known.Count - 1].At.ToString("g", L.Culture));
            AutomationProperties.SetName(canvas, L.T(unit) + ". " + summary);
            panel.Children.Add(canvas);
            if (known.Count == 1) panel.Children.Add(Note(quota
                ? "First reading recorded. A second reading will show change over time."
                : "One day of activity recorded. More days are needed to compare your usage."));
            else if (known.All(point => point.Value == 0)) panel.Children.Add(Note("The reported values are zero. There is not yet a visible consumption trend."));

            Action draw = () =>
            {
                canvas.Children.Clear();
                double width = canvas.ActualWidth;
                if (width < 120) return;
                const double left = 48, top = 14, bottom = 124;
                double right = width - 10, plotWidth = right - left;
                double maximum = quota ? 100 : NiceMaximum(known.Max(point => point.Value.Value));
                if (!quota && unit != "Computed cost (USD)" && maximum <= 5) maximum = Math.Ceiling(maximum / 2) * 2;
                for (int line = 0; line <= 2; line++)
                {
                    double value = maximum * line / 2;
                    double y = bottom - (bottom - top) * line / 2;
                    var rule = new Line { X1 = left, X2 = right, Y1 = y, Y2 = y, StrokeThickness = 1 };
                    rule.SetResourceReference(Shape.StrokeProperty, "Divider"); canvas.Children.Add(rule);
                    Place(canvas, Ui.Text(Compact(value) + (quota ? "%" : ""), 11, "Text2"), 0, y - 7);
                }
                DateTime first = known[0].At, last = known[known.Count - 1].At;
                double duration = Math.Max(1, (last - first).TotalMilliseconds);
                Func<HistoryChartPoint, double> position = point => known.Count == 1 ? left + plotWidth / 2 : left + (point.At - first).TotalMilliseconds / duration * plotWidth;
                Polyline trace = null; HistoryChartPoint previous = null;
                foreach (var point in known)
                {
                    double value = Math.Max(0, Math.Min(maximum, point.Value.Value));
                    double x = position(point), y = bottom - value / maximum * (bottom - top);
                    string caption = point.At.ToString(quota ? "g" : "d MMM", L.Culture) + " · " + point.Value.Value.ToString(quota ? "0.##" : unit == "Computed cost (USD)" ? "0.0000" : "N0", L.Culture) + " " + L.T(unit);
                    if (quota)
                    {
                        if (previous == null || point.Reset != previous.Reset || point.Value < previous.Value || (point.At - previous.At).TotalMinutes > 20)
                        {
                            trace = new Polyline { StrokeThickness = 2.5 };
                            trace.SetResourceReference(Shape.StrokeProperty, "Accent"); canvas.Children.Add(trace);
                        }
                        trace.Points.Add(new Point(x, y));
                        var dot = new Ellipse { Width = 6, Height = 6, ToolTip = caption };
                        dot.SetResourceReference(Shape.FillProperty, "Accent"); Place(canvas, dot, x - 3, y - 3);
                    }
                    else
                    {
                        double barWidth = Math.Min(36, Math.Max(3, plotWidth / Math.Max(2, known.Count) * 0.55));
                        var bar = new Border { Width = barWidth, Height = value == 0 ? 2 : Math.Max(2, bottom - y), CornerRadius = new CornerRadius(3, 3, 0, 0), ToolTip = caption };
                        bar.SetResourceReference(Border.BackgroundProperty, value == 0 ? "Text3" : "Accent");
                        Place(canvas, bar, Math.Max(left, Math.Min(right - barWidth, x - barWidth / 2)), value == 0 ? bottom - 1 : y);
                    }
                    previous = point;
                }
                string format = quota && (last - first).TotalDays < 1 ? "t" : "d MMM";
                Place(canvas, Ui.Text(first.ToString(format, L.Culture), 11, "Text2"), left, bottom + 12);
                if (known.Count > 1)
                {
                    var end = Ui.Text(last.ToString(format, L.Culture), 11, "Text2"); end.Measure(new Size(width, 30));
                    Place(canvas, end, Math.Max(left, right - end.DesiredSize.Width), bottom + 12);
                }
            };
            canvas.SizeChanged += (sender, args) => draw();
            return panel;
        }

        private static double NiceMaximum(double value)
        {
            if (value <= 0) return 1;
            double magnitude = Math.Pow(10, Math.Floor(Math.Log10(value)));
            double scaled = value / magnitude;
            return (scaled <= 1 ? 1 : scaled <= 2 ? 2 : scaled <= 5 ? 5 : 10) * magnitude;
        }

        private static void Place(Canvas canvas, UIElement element, double x, double y)
        {
            Canvas.SetLeft(element, x); Canvas.SetTop(element, y); canvas.Children.Add(element);
        }
    }
}
