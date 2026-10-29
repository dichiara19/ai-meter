using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using Microsoft.Win32;

namespace AIMeter
{
    /// <summary>Settings-style history views. Facts, comparisons and optional feedback have distinct presentations.</summary>
    internal sealed class HistoryWindow : Window
    {
        private readonly Controller controller;
        private readonly HistoryStore store;
        private readonly HistoryAnalysis analysis;
        private readonly StackPanel overview = new StackPanel();
        private readonly StackPanel sessions = new StackPanel();
        private readonly StackPanel quotas = new StackPanel();
        private readonly StackPanel activity = new StackPanel();
        private readonly TextBlock status = Ui.Text("", 12, "Text3");
        private readonly ComboBox period = new ComboBox { MinWidth = 150 };
        private readonly ComboBox provider = new ComboBox { MinWidth = 180 };
        private readonly TabControl tabs = new TabControl { Background = Brushes.Transparent, BorderThickness = new Thickness(0) };
        private readonly WrapPanel actions = new WrapPanel { Margin = new Thickness(0, 16, 0, 16) };
        private string selectedSession, selectedQuota;
        private int selectedActivity;
        private bool loading, closed;
        private bool shouldFocusSession;
        private HistoryReport report;

        internal HistoryWindow(Controller controller, HistoryStore store)
        {
            this.controller = controller; this.store = store; analysis = new HistoryAnalysis(store, controller.Consumption);
            Title = L.T("Usage history"); Width = Math.Min(1160, SystemParameters.WorkArea.Width - 32); Height = Math.Min(860, SystemParameters.WorkArea.Height - 32);
            MinWidth = Math.Min(660, Width); MinHeight = Math.Min(480, Height);
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            FontSize = 14; Icon = Controller.AppIconImage(); Program.Brand(this);
            SetResourceReference(BackgroundProperty, "Fallback"); SetResourceReference(ForegroundProperty, "Text");
            ApplyContrast(SystemParameters.HighContrast);
            Ui.MakeSettingsWindow(this);
            ConfigureTabs();
            var root = new DockPanel();
            var titleBar = new StackPanel { Orientation = Orientation.Horizontal, Height = 48, Margin = new Thickness(16, 0, 144, 0) };
            titleBar.Children.Add(new Image { Source = Icon, Width = 16, Height = 16, Margin = new Thickness(0, 0, 12, 0) });
            var caption = Ui.Text("AI Meter", 12, "Text"); caption.VerticalAlignment = VerticalAlignment.Center; titleBar.Children.Add(caption);
            DockPanel.SetDock(titleBar, Dock.Top); root.Children.Add(titleBar);
            var body = new DockPanel { Margin = new Thickness(32, 0, 32, 0) };
            var heading = new StackPanel();
            var title = Ui.Text(Title, 28, "Text", FontWeights.SemiBold); title.FontFamily = Theme.DisplayFont; title.Margin = new Thickness(0, 8, 0, 4);
            heading.Children.Add(title); heading.Children.Add(Note("Understand your usage, spot changes and reflect on the value you get."));
            foreach (int days in new[] { 7, 30, 90, 0 }) period.Items.Add(new ComboBoxItem { Content = days == 0 ? L.T("All history") : L.F("Last {0} days", days), Tag = days });
            period.SelectedIndex = 1; period.SetResourceReference(StyleProperty, "Combo"); provider.SetResourceReference(StyleProperty, "Combo");
            provider.Items.Add(new ComboBoxItem { Content = L.T("All services"), Tag = "" }); provider.SelectedIndex = 0;
            HistoryVisuals.Focusable(period, L.T("Period")); HistoryVisuals.Focusable(provider, L.T("Services"));
            actions.Children.Add(Filter("Period", period)); actions.Children.Add(Filter("Services", provider));
            var refresh = Button("Refresh now", Reload); refresh.VerticalAlignment = VerticalAlignment.Bottom; actions.Children.Add(refresh);
            var export = Button("Export history…", Export); export.VerticalAlignment = VerticalAlignment.Bottom; actions.Children.Add(export);
            export.ToolTip = L.T("Export all retained readings and feedback, including data outside the current filters.");
            heading.Children.Add(actions); DockPanel.SetDock(heading, Dock.Top); body.Children.Add(heading);
            status.Margin = new Thickness(0, 10, 0, 12); DockPanel.SetDock(status, Dock.Bottom); body.Children.Add(status);
            AddTab("Overview", overview); AddTab("Sessions", sessions); AddTab("Quota cycles", quotas); AddTab("Daily activity", activity);
            body.Children.Add(tabs); root.Children.Add(body); Content = root;
            period.SelectionChanged += (sender, args) => Reload(); provider.SelectionChanged += (sender, args) => Reload();
            SystemParameters.StaticPropertyChanged += OnSystemParametersChanged;
            Loaded += (sender, args) => Reload();
            Closed += (sender, args) => { closed = true; SystemParameters.StaticPropertyChanged -= OnSystemParametersChanged; };
        }

        private void OnSystemParametersChanged(object sender, PropertyChangedEventArgs args)
        {
            if (args.PropertyName == "HighContrast") Dispatcher.BeginInvoke(new Action(() => { if (!closed) ApplyContrast(SystemParameters.HighContrast); }));
        }

        internal void ApplyContrast(bool enabled)
        {
            foreach (string key in new[] { "Fallback", "Layer", "Card", "CardStroke", "Text", "Text2", "Text3", "Accent", "AccentText", "Info", "Success", "Critical", "OnAccent", "Track", "Control", "ControlStroke", "ControlStrong", "SubtleHover" }) Resources.Remove(key);
            if (!enabled) return;
            foreach (string key in new[] { "Fallback", "Layer", "Card", "Control", "SubtleHover" }) Resources[key] = SystemColors.WindowBrush;
            foreach (string key in new[] { "Text", "Text2", "Text3", "CardStroke", "ControlStroke", "ControlStrong", "Success", "Critical", "Track" }) Resources[key] = SystemColors.WindowTextBrush;
            foreach (string key in new[] { "Accent", "AccentText", "Info" }) Resources[key] = SystemColors.HighlightBrush;
            Resources["OnAccent"] = SystemColors.HighlightTextBrush;
        }

        private void ConfigureTabs()
        {
            tabs.Template = (ControlTemplate)System.Windows.Markup.XamlReader.Parse(@"
                <ControlTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' TargetType='TabControl'>
                  <DockPanel><TabPanel x:Name='HeaderPanel' DockPanel.Dock='Top' IsItemsHost='True' Margin='0,0,0,12'/>
                    <ContentPresenter x:Name='PART_SelectedContentHost' ContentSource='SelectedContent'/>
                  </DockPanel>
                </ControlTemplate>");
            tabs.ItemContainerStyle = (Style)System.Windows.Markup.XamlReader.Parse(@"
                <Style xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' TargetType='TabItem'>
                  <Setter Property='Foreground' Value='{DynamicResource Text2}'/><Setter Property='Background' Value='Transparent'/>
                  <Setter Property='Template'><Setter.Value><ControlTemplate TargetType='TabItem'>
                    <Border xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' x:Name='HeaderBorder' Background='{TemplateBinding Background}'
                        BorderBrush='Transparent' BorderThickness='0,0,0,2' CornerRadius='4,4,0,0' Padding='14,10' Margin='0,0,4,0'>
                      <ContentPresenter x:Name='HeaderContent' ContentSource='Header' RecognizesAccessKey='True'/>
                    </Border>
                    <ControlTemplate.Triggers>
                      <Trigger Property='IsSelected' Value='True'><Setter Property='Foreground' Value='{DynamicResource Text}'/>
                        <Setter TargetName='HeaderContent' Property='TextElement.FontWeight' Value='SemiBold'/>
                        <Setter TargetName='HeaderBorder' Property='BorderBrush' Value='{DynamicResource Accent}'/></Trigger>
                      <Trigger Property='IsMouseOver' Value='True'><Setter Property='Background' Value='{DynamicResource SubtleHover}'/></Trigger>
                    </ControlTemplate.Triggers>
                  </ControlTemplate></Setter.Value></Setter>
                </Style>");
        }

        private void AddTab(string label, StackPanel content)
        {
            var scroll = new ScrollViewer { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Padding = new Thickness(24) };
            var layer = new Border { Child = scroll, CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1) };
            layer.SetResourceReference(Border.BackgroundProperty, "Layer"); layer.SetResourceReference(Border.BorderBrushProperty, "CardStroke");
            var tab = new TabItem { Header = L.T(label), Content = layer };
            HistoryVisuals.Focusable(tab, L.T(label)); tabs.Items.Add(tab);
        }

        private static StackPanel Filter(string label, ComboBox control)
        {
            var panel = new StackPanel { Margin = new Thickness(0, 0, 12, 6) };
            panel.Children.Add(Ui.Text(L.T(label), 12, "Text2")); control.Margin = new Thickness(0, 4, 0, 0); panel.Children.Add(control); return panel;
        }
        private static TextBlock Note(string text) { return HistoryVisuals.Note(text); }
        private static Border Card(string title, string description, params UIElement[] content) { return HistoryVisuals.Card(title, description, content); }
        private static Button Button(string key, Action action)
        {
            var button = new Button { Content = L.T(key), Margin = new Thickness(0, 0, 8, 6), Padding = new Thickness(12, 7, 12, 7), HorizontalAlignment = HorizontalAlignment.Left };
            button.SetResourceReference(StyleProperty, "StdButton"); HistoryVisuals.Focusable(button, L.T(key));
            button.Click += (sender, args) => action(); return button;
        }

        internal void FocusSession(string id) { selectedSession = id; shouldFocusSession = true; tabs.SelectedIndex = 1; if (report != null) RenderSessions(); }
        internal async void Reload()
        {
            if (loading || !IsLoaded || closed) return;
            loading = true; actions.IsEnabled = false; status.Text = L.T("Loading history…");
            int days = (int)((ComboBoxItem)period.SelectedItem).Tag;
            DateTime since = days == 0 ? DateTime.MinValue : DateTime.Now.AddDays(-days);
            string chosen = (string)((ComboBoxItem)provider.SelectedItem).Tag;
            try
            {
                var result = await Task.Run(() => Tuple.Create(analysis.Read(since, chosen), analysis.Providers()));
                if (closed) return;
                report = result.Item1;
                foreach (string id in result.Item2)
                    if (!provider.Items.Cast<ComboBoxItem>().Any(item => Equals(item.Tag, id))) provider.Items.Add(new ComboBoxItem { Content = HistoryVisuals.ProviderName(id), Tag = id });
                status.Text = L.F("History since {0} · view refreshed at {1}", report.Started.ToString("g", L.Culture), DateTime.Now.ToString("t", L.Culture));
                if (!controller.Settings.HistoryEnabled) status.Text += " · " + L.T("Collection disabled");
                if (controller.HistoryError != null) status.Text += " · " + L.T("History collection failed; see the error log.");
                status.ToolTip = status.Text;
                RenderOverview(); RenderSessions(); RenderQuotas(); RenderActivity();
            }
            catch (Exception exception)
            {
                status.Text = L.T("History unavailable; see the error log."); Program.LogError("history-dashboard", exception);
                foreach (var page in new[] { overview, sessions, quotas, activity })
                {
                    if (report == null) page.Children.Clear();
                    page.Children.Insert(0, Empty("History could not be loaded", "Try refreshing the view. If the problem persists, the error log contains the details.", Reload, "Refresh now"));
                }
            }
            finally { loading = false; actions.IsEnabled = true; }
        }

        private Border Empty(string title, string description, Action action, string actionLabel)
        {
            return HistoryVisuals.Message(title, description, action == null ? null : Button(actionLabel, action));
        }
        private void ClearFilters() { provider.SelectedIndex = 0; period.SelectedIndex = 3; }

        private void RenderOverview()
        {
            overview.Children.Clear();
            if (!controller.Settings.HistoryEnabled)
            {
                overview.Children.Add(Empty("History collection is paused", "Turn on local history in Settings to collect new readings. Your saved history is still available.", controller.OpenSettings, "Settings"));
                if (report.Events == 0 && report.Observations == 0) return;
            }
            if (report.Events == 0 && report.Observations == 0)
            {
                overview.Children.Add(report.HasAnyHistory
                    ? Empty("Nothing recorded for these filters", "Try a different period or service to explore your saved history.", ClearFilters, "Show all history")
                    : Empty("Your history starts here", "Use a connected service. Quota readings and supported session metrics will appear as they become available.", controller.OpenSettings, "Open service settings"));
                overview.Children.Add(Card("What you will find here", "As your history grows, this page will show activity, models, quota trends and the value you get from your sessions.",
                    Note("No past usage is invented. Missing data is never treated as zero.")));
                return;
            }
            var counters = new WrapPanel { Margin = new Thickness(0, 0, -12, 0) };
            counters.Children.Add(Counter("Sessions", report.SessionCount > 0 ? Number(report.SessionCount) : "—", "With recorded responses"));
            counters.Children.Add(Counter("Responses", report.Events > 0 ? Number(report.Events) : "—", "From supported tools"));
            counters.Children.Add(Counter("Readings", report.Observations > 0 ? Number(report.Observations) : "—", "Reported by your services"));
            counters.Children.Add(Counter("Sessions rated", report.SessionCount > 0 ? report.Rated + "/" + report.SessionCount : "—", "Optional feedback"));
            overview.Children.Add(counters);
            if (report.Days.Count > 0) overview.Children.Add(ActivityCard(false));
            else if (report.Trend.Count > 0) overview.Children.Add(QuotaCard());
            else if (report.Amounts.Count > 0) overview.Children.Add(AmountsCard());
            else overview.Children.Add(Empty("Session measurements are not available yet", "Quota readings can be available before a supported tool records session metrics.", null, null));

            if (report.Models.Count > 0) overview.Children.Add(ModelsCard());
            if (report.Trend.Count == 0 || report.Trend.All(row => Text(row[6]).Contains("unidentified-account")))
                overview.Children.Add(Empty("No comparable quota readings yet", "Automatic comparisons need percentage readings with an identified account. Session measurements can still be explored separately.", null, null));
            else overview.Children.Add(ConsumptionCard());
            overview.Children.Add(FeedbackCard());
            overview.Children.Add(CoverageCard());
        }

        /// <summary>
        /// Readiness per limit and recorded episodes, from the same stored evidence as notifications.
        /// Overlapping sessions are shown as candidates; the user interprets the cause.
        /// </summary>
        private Border ConsumptionCard()
        {
            var content = new StackPanel();
            foreach (var r in report.Readiness.Where(r => r.Scope.IsIdentified))
            {
                var line = new WrapPanel();
                line.Children.Add(Note(HistoryVisuals.ProviderName(r.Scope.Provider) + " · " + MetricLabel(r.Scope.Metric) + " — " + L.T(StateLabel(r.State)) + ": " + L.F(r.Reason, r.Arguments)));
                if (r.State == ReadinessState.ReferenceChanged)
                {
                    string key = r.Scope.Key;
                    line.Children.Add(Button("Rebuild the reference", () => { controller.Consumption.RebuildReference(key, DateTime.UtcNow); Reload(); }));
                }
                content.Children.Add(line);
            }
            if (report.Readiness.Count == 0) content.Children.Add(Note("Comparisons start once readings with an identified account have been processed."));

            foreach (var e in report.Episodes)
            {
                var block = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };
                block.Children.Add(Ui.Text(L.F("{0} · {1} · {2}, {3}", HistoryVisuals.ProviderName(e.Provider), MetricLabel(e.Metric),
                    e.OpenedUtc.ToLocalTime().ToString("g", L.Culture), L.T(EpisodeLabel(e))), 14, "Text", FontWeights.SemiBold));
                block.Children.Add(Note(ConsumptionMonitor.Explain(e.Evidence)));
                block.Children.Add(Note(L.F("Comparison: {0}, {1} days with consumption between {2} and {3}.", L.T(ContextLabel(Json.Str(e.Baseline, "context"))),
                    Number((long)(Json.Num(e.Baseline, "activeDays") ?? 0)), Day(Json.Str(e.Baseline, "fromUtc")), Day(Json.Str(e.Baseline, "toUtc")))));
                if (!e.WasEligible) block.Children.Add(Note("Recorded without a notification."));
                AddCandidates(block, e);
                if (e.State == AlertEpisode.Open)
                {
                    string id = e.Id;
                    block.Children.Add(Button("This was expected", () => { controller.Consumption.Acknowledge(id); Reload(); }));
                }
                content.Children.Add(block);
            }
            return Card("Unusual consumption", "Compared with your own observed history. A departure shows what changed, not why: providers, task complexity and other devices can all play a part.", content);
        }

        private void AddCandidates(StackPanel block, AlertEpisode e)
        {
            if (e.Candidates.Count == 0)
            {
                block.Children.Add(Note("No locally recorded session overlaps this interval. The consumption may come from another device, a web chat or an unsupported tool."));
                return;
            }
            block.Children.Add(Note(e.Candidates.Count == 1
                ? "Possibly associated, because its responses overlap the interval. Sessions carry no account, so the match is by service only:"
                : "Several sessions overlap the interval; the evidence cannot distinguish them. Sessions carry no account, so the match is by service only:"));
            foreach (var c in e.Candidates)
            {
                var row = new WrapPanel();
                string label = L.F("{0} · {1} · from {2} · {3} responses · ref. {4}", c.Agent, c.Model ?? "—", c.First.ToString("t", L.Culture), Number(c.Replies), c.SessionId.Substring(0, Math.Min(6, c.SessionId.Length)));
                string measures = L.F("Input {0} · output {1} · cache read {2} · cache write {3} · computed API cost {4}",
                    Measure(c.Input), Measure(c.Output), Measure(c.CacheRead), Measure(c.CacheWrite), c.Cost.HasValue ? c.Cost.Value.ToString("0.00", L.Culture) + " USD" : "—");
                var text = Note(label + "\n" + measures); text.Margin = new Thickness(12, 2, 12, 6);
                row.Children.Add(text);
                string id = c.SessionId;
                row.Children.Add(Button("Open session", () => FocusSession(id)));
                block.Children.Add(row);
            }
        }

        private static string Measure(long? value) { return value.HasValue ? Number(value.Value) : "—"; }
        private static string Day(string iso)
        {
            DateTime at;
            return DateTime.TryParse(iso, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out at) ? at.ToLocalTime().ToString("d", L.Culture) : "—";
        }
        private static string ContextLabel(string context) { return context == "weekday" ? "weekdays" : context == "weekend" ? "weekends" : "all days"; }
        private static string EpisodeLabel(AlertEpisode e) { return e.State == AlertEpisode.Open ? "ongoing" : e.State == AlertEpisode.AcknowledgedExpected ? "marked as expected" : "ended"; }
        private static string StateLabel(ReadinessState state)
        {
            switch (state)
            {
                case ReadinessState.Ready: return "Ready";
                case ReadinessState.Learning: return "Learning";
                case ReadinessState.ReferenceChanged: return "Reference changed";
                default: return "Temporarily unavailable";
            }
        }

        private static Border Counter(string label, string value, string hint)
        {
            var body = new StackPanel(); body.Children.Add(Ui.Text(L.T(label), 13, "Text2"));
            var number = Ui.Text(value, 26, "Text", FontWeights.SemiBold); number.Margin = new Thickness(0, 5, 0, 3); body.Children.Add(number);
            body.Children.Add(Ui.Text(L.T(hint), 11, "Text3"));
            var card = Ui.Card(body, new Thickness(16)); card.MinWidth = 198; card.Margin = new Thickness(0, 0, 12, 16); return card;
        }

        private Border ModelsCard()
        {
            var ranked = new StackPanel(); double maximum = report.Models.Max(row => Convert.ToDouble(row[3]));
            foreach (var row in report.Models.Take(5))
            {
                var line = new Grid { Margin = new Thickness(0, 10, 0, 0) };
                line.ColumnDefinitions.Add(new ColumnDefinition()); line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                var labels = new StackPanel(); labels.Children.Add(Ui.Text(Text(row[2]), 14, "Text", FontWeights.SemiBold));
                labels.Children.Add(Ui.Text(HistoryVisuals.ProviderName(Text(row[0])) + " · " + Text(row[1]), 12, "Text2")); line.Children.Add(labels);
                var count = Ui.Text(Number(row[3]), 14, "Text", FontWeights.SemiBold); Grid.SetColumn(count, 1); line.Children.Add(count); ranked.Children.Add(line);
                var track = new Grid { Height = 4, Margin = new Thickness(0, 8, 0, 4) };
                var fill = new ColumnDefinition { Width = new GridLength(Convert.ToDouble(row[3]) / Math.Max(1, maximum), GridUnitType.Star) };
                track.ColumnDefinitions.Add(fill); track.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(0, 1 - fill.Width.Value), GridUnitType.Star) });
                var rail = new Border { CornerRadius = new CornerRadius(2) }; rail.SetResourceReference(Border.BackgroundProperty, "Track"); Grid.SetColumnSpan(rail, 2); track.Children.Add(rail);
                var bar = new Border { CornerRadius = new CornerRadius(2) }; bar.SetResourceReference(Border.BackgroundProperty, "Accent"); track.Children.Add(bar); ranked.Children.Add(track);
            }
            return Card("Models and tools", "Recorded responses by model and tool. This shows activity, not the quality of the results.", ranked,
                HistoryVisuals.Details("Show all model measurements", ModelsTable(), Note("Unreported counters and costs are excluded from totals. A dash means unavailable.")));
        }

        private Border FeedbackCard()
        {
            if (report.Rated == 0) return Empty("No sessions rated yet", "Feedback is optional. Rate whether the useful result was worth the quota or money consumed; ignoring a request leaves the session unrated.",
                report.SessionCount == 0 ? (Action)null : () => { tabs.SelectedIndex = 1; }, "View sessions");
            var distribution = new WrapPanel { Margin = new Thickness(0, 10, 0, 6) };
            foreach (var value in new[] { Tuple.Create("Yes", report.Positive, "Success"), Tuple.Create("Partly", report.Neutral, "Text2"), Tuple.Create("No", report.Negative, "Critical") })
            {
                var text = Ui.Text(L.T(value.Item1) + "  " + Number(value.Item2), 18, value.Item3, FontWeights.SemiBold); text.Margin = new Thickness(0, 0, 28, 0); distribution.Children.Add(text);
            }
            return Card("Value for consumption", L.F("{0}/{1} sessions rated · {2} yes · {3} partly · {4} no", report.Rated, report.SessionCount, report.Positive, report.Neutral, report.Negative),
                distribution, Note("Rate the useful work obtained relative to the quota or money consumed."), Button("View sessions", () => { tabs.SelectedIndex = 1; }));
        }

        private Border CoverageCard()
        {
            return Card("About this history", L.F("Collected since {0} · {1} MB on this PC", report.Started.ToString("g", L.Culture), (report.Bytes / 1048576.0).ToString("0.0", L.Culture)),
                HistoryVisuals.Details("Collection coverage and data limits", Note("Quota is measured for the account. Logged tokens and computed API costs belong to responses; they do not measure subscription charges."),
                    Note("Collection pauses while Windows is locked or asleep. Unsupported tools and provider-internal activity may have no local token records."),
                    Note(L.F("{0} collection gaps or source limitations recorded", report.Gaps)),
                    report.Coverage.Count == 0 ? (UIElement)new Border() : Table(new[] { "From", "To", "Reason" }, report.Coverage.Select(row => new[] { HistoryStore.Time(row[0]).ToString("g", L.Culture), HistoryStore.Time(row[1]).ToString("g", L.Culture), L.T(Text(row[2])) }))),
                HistoryVisuals.Details("Manage saved history", Note("Sessions are identified locally without saving prompts or titles. Feedback is optional and can be changed or cleared."), Button("Delete history…", Delete)));
        }

        private void RenderSessions()
        {
            sessions.Children.Clear();
            if (report.Sessions.Count == 0)
            {
                sessions.Children.Add(Empty("No session measurements yet", "Supported tools need to record a response before a session appears. Quota readings from other activity can still be available.",
                    report.Trend.Count == 0 ? (Action)null : () => { tabs.SelectedIndex = 2; }, "View quota readings")); return;
            }
            var list = new ListBox { MaxHeight = 380, MinHeight = 120, BorderThickness = new Thickness(0), Background = Brushes.Transparent, Padding = new Thickness(0) };
            ScrollViewer.SetHorizontalScrollBarVisibility(list, ScrollBarVisibility.Disabled);
            list.ItemContainerStyle = (Style)System.Windows.Markup.XamlReader.Parse(@"
              <Style xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' TargetType='ListBoxItem'>
                <Setter Property='Foreground' Value='{DynamicResource Text}'/><Setter Property='HorizontalContentAlignment' Value='Stretch'/>
                <Setter Property='Padding' Value='12,10'/><Setter Property='Margin' Value='0,2'/>
                <Setter Property='Template'><Setter.Value><ControlTemplate TargetType='ListBoxItem'>
                  <Border xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' x:Name='Row' Background='Transparent' CornerRadius='4' BorderBrush='Transparent' BorderThickness='2,0,0,0' Padding='{TemplateBinding Padding}'>
                    <ContentPresenter/>
                  </Border><ControlTemplate.Triggers>
                    <Trigger Property='IsSelected' Value='True'><Setter TargetName='Row' Property='Background' Value='{DynamicResource SubtleHover}'/><Setter TargetName='Row' Property='BorderBrush' Value='{DynamicResource Accent}'/></Trigger>
                    <Trigger Property='IsMouseOver' Value='True'><Setter TargetName='Row' Property='Background' Value='{DynamicResource SubtleHover}'/></Trigger>
                  </ControlTemplate.Triggers>
                </ControlTemplate></Setter.Value></Setter>
              </Style>");
            foreach (var session in report.Sessions)
            {
                var label = new StackPanel(); label.Children.Add(Ui.Text(session.Model ?? HistoryVisuals.ProviderName(session.Provider), 14, "Text", FontWeights.SemiBold));
                label.Children.Add(Ui.Text(session.Last.ToString("g", L.Culture) + " · " + session.Agent, 12, "Text2"));
                label.Children.Add(Ui.Text(Rating(session.Feedback), 12, "Text3"));
                var item = new ListBoxItem { Tag = session, Content = label }; HistoryVisuals.Focusable(item, session.Agent + " · " + (session.Model ?? "") + " · " + session.Last.ToString("g", L.Culture));
                list.Items.Add(item); if (session.Id == selectedSession) list.SelectedItem = item;
            }
            if (list.SelectedItem == null) list.SelectedIndex = 0;
            var detail = new StackPanel();
            Action show = () =>
            {
                detail.Children.Clear(); if (list.SelectedItem == null) return;
                var selected = (HistorySession)((ListBoxItem)list.SelectedItem).Tag; selectedSession = selected.Id;
                var choices = new WrapPanel { Margin = new Thickness(0, 12, 0, 8) };
                foreach (int value in new[] { 1, 0, -1 })
                {
                    int rating = value; var button = Button(Rating(value), () => Rate(selected.Id, rating));
                    if (selected.Feedback == value) button.SetResourceReference(StyleProperty, "AccentButton");
                    choices.Children.Add(button);
                }
                if (selected.Feedback.HasValue) choices.Children.Add(Button("Clear rating", () => Rate(selected.Id, null)));
                detail.Children.Add(Card("Was the result worth this session's consumption?", "Rate the useful work obtained relative to the quota or money consumed.", choices));
                var metrics = new StackPanel();
                foreach (var metric in new[] { Tuple.Create("Input", (object)selected.Input), Tuple.Create("Output", (object)selected.Output), Tuple.Create("Cache read", (object)selected.CacheRead), Tuple.Create("Cache write", (object)selected.CacheWrite) })
                    metrics.Children.Add(MetricRow(metric.Item1, Number(metric.Item2)));
                metrics.Children.Add(MetricRow("Computed cost (USD)", Amount(selected.Cost)));
                detail.Children.Add(Card((selected.Model ?? "—") + " · " + selected.Agent,
                    L.F("Observed {0}–{1} · {2} responses", selected.First.ToString("g", L.Culture), selected.Last.ToString("g", L.Culture), selected.Replies), metrics,
                    HistoryVisuals.Details("About these measurements", Note("Account quota is not attributed to this session. Token and cost totals include only observed responses in the selected period."), Note("Unreported counters and costs are excluded from totals. A dash means unavailable."))));
            };
            list.SelectionChanged += (sender, args) => show(); HistoryVisuals.Focusable(list, L.T("Sessions"));
            var master = Card("Recent sessions", "Select a session to review its measurements and feedback.", list);
            var layout = new Grid(); layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(310) }); layout.ColumnDefinitions.Add(new ColumnDefinition());
            layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            layout.Children.Add(master); layout.Children.Add(detail);
            Action<double> arrange = width =>
            {
                bool wide = width >= 820; layout.ColumnDefinitions[0].Width = wide ? new GridLength(310) : new GridLength(1, GridUnitType.Star);
                layout.ColumnDefinitions[1].Width = wide ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
                Grid.SetColumn(detail, wide ? 1 : 0); Grid.SetRow(detail, wide ? 0 : 1); detail.Margin = new Thickness(wide ? 20 : 0, 0, 0, 0);
                list.MaxHeight = wide ? 400 : 200;
            };
            layout.SizeChanged += (sender, args) => arrange(args.NewSize.Width); arrange(900);
            sessions.Children.Add(layout); show();
            if (shouldFocusSession) Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, new Action(() =>
            {
                if (!closed && shouldFocusSession) { detail.BringIntoView(); shouldFocusSession = false; }
            }));
            sessions.Children.Add(Note("Up to 200 recent sessions are shown. Export includes the complete retained history."));
        }

        private static Grid MetricRow(string label, string value)
        {
            var row = new Grid { Margin = new Thickness(0, 9, 0, 9) }; row.ColumnDefinitions.Add(new ColumnDefinition()); row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.Children.Add(Ui.Text(L.T(label), 14, "Text2")); var number = Ui.Text(value, 14, "Text", FontWeights.SemiBold); Grid.SetColumn(number, 1); row.Children.Add(number); return row;
        }

        private async void Rate(string id, int? value)
        {
            if (loading) return; loading = true; sessions.IsEnabled = false;
            try { await Task.Run(() => store.Rate(id, value)); }
            catch (Exception exception) { Program.LogError("history-feedback", exception); status.Text = L.T("History unavailable; see the error log."); }
            finally { loading = false; sessions.IsEnabled = true; }
            Reload();
        }
        private static string Rating(int? value) { return !value.HasValue ? L.T("Not rated") : value == 1 ? "👍 " + L.T("Yes") : value == 0 ? "😐 " + L.T("Partly") : "👎 " + L.T("No"); }

        private Border QuotaCard()
        {
            var selector = new ComboBox { MinWidth = 220, MaxWidth = 600, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 10, 0, 8) };
            selector.SetResourceReference(StyleProperty, "Combo"); HistoryVisuals.Focusable(selector, L.T("Limit"));
            foreach (var group in report.Trend.GroupBy(row => new { Provider = row[0], Account = row[1], Metric = row[2] }))
            {
                var first = group.First(); string key = Text(first[0]) + ":" + first[1] + ":" + first[2];
                var item = new ComboBoxItem { Content = HistoryVisuals.ProviderName(Text(first[0])) + " · " + MetricLabel(Text(first[2])) + " · " + AccountLabel(first[1], Text(first[6]).Contains("unidentified-account")), Tag = Tuple.Create(key, group.OrderBy(row => Convert.ToInt64(row[4])).ToList()) };
                selector.Items.Add(item); if (key == selectedQuota) selector.SelectedItem = item;
            }
            var plot = new StackPanel();
            Action draw = () =>
            {
                plot.Children.Clear(); if (selector.SelectedItem == null) return;
                var data = (Tuple<string, List<object[]>>)((ComboBoxItem)selector.SelectedItem).Tag; selectedQuota = data.Item1;
                var rows = data.Item2.Skip(Math.Max(0, data.Item2.Count - 300)).ToList(); var last = rows[rows.Count - 1];
                plot.Children.Add(MetricRow("Used at last reading", Convert.ToDouble(last[5]).ToString("0.##", L.Culture) + "% · " + HistoryStore.Time(last[4]).ToString("g", L.Culture)));
                plot.Children.Add(HistoryVisuals.Chart(rows.Select(row => new HistoryChartPoint { At = HistoryStore.Time(row[4]), Value = Convert.ToDouble(row[5]), Reset = row[3] == null ? (long?)null : Convert.ToInt64(row[3]) }).ToList(), "Quota used (%)", true));
            };
            selector.SelectionChanged += (sender, args) => draw(); if (selector.SelectedItem == null && selector.Items.Count > 0) selector.SelectedIndex = 0; else draw();
            return Card("Quota over time", "Readings apply to the account. Lines break at resets and collection gaps.", selector, plot);
        }

        private void RenderQuotas()
        {
            quotas.Children.Clear();
            if (report.Trend.Count > 0) quotas.Children.Add(QuotaCard());
            else quotas.Children.Add(Empty("No quota trend yet", "Connect a service that reports quota readings. A first reading will appear here before a trend can be drawn.", controller.OpenSettings, "Open service settings"));
            if (report.Cycles.Count > 0) quotas.Children.Add(Card("Quota cycles", "Each account and limit stays separate. Remaining quota is the last observed value, not proof of what expired unused.",
                Table(new[] { "Services", "Account", "Limit", "Reset", "Peak used", "Last remaining", "Readings" }, report.Cycles.Select(row => new[] {
                    HistoryVisuals.ProviderName(Text(row[0])), AccountLabel(row[1], Convert.ToInt64(row[12]) != 0), MetricLabel(Text(row[2])), HistoryStore.Time(row[3]).ToString("g", L.Culture),
                    Convert.ToDouble(row[7]).ToString("0.##", L.Culture) + "%", Math.Max(0, Math.Min(100, 100 - Convert.ToDouble(row[11]))).ToString("0.##", L.Culture) + "%", Number(row[8]) })),
                HistoryVisuals.Details("About account identities", Note("Account fingerprints identify credentials when available; otherwise account identity is unknown and comparisons are disabled. A credential change starts a separate history."))));
            if (report.Amounts.Count > 0) quotas.Children.Add(AmountsCard());
        }

        private Border AmountsCard()
        {
            return Card("Spending and balances", "Amounts retain the unit and data quality reported by their source.",
                Table(new[] { "Services", "Account", "Limit", "Amount", "Unit", "Quality" }, report.Amounts.Select(row => new[] {
                    HistoryVisuals.ProviderName(Text(row[0])), AccountLabel(row[9], Text(row[10]).Contains("unidentified-account")), MetricLabel(Text(row[1])), Amount(row[3] ?? row[5]), Text(row[4] ?? row[7]), L.T(Text(row[8])) })));
        }

        private Border ActivityCard(bool details)
        {
            string[] metrics = { "Responses", "Input", "Output", "Cache read", "Cache write", "Computed cost (USD)" };
            var selector = new ComboBox { MinWidth = 180, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 10, 0, 0) };
            selector.SetResourceReference(StyleProperty, "Combo"); HistoryVisuals.Focusable(selector, L.T("Measurement"));
            foreach (string metric in metrics) selector.Items.Add(L.T(metric));
            var plot = new StackPanel();
            Action draw = () =>
            {
                if (selector.SelectedIndex < 0) return; selectedActivity = selector.SelectedIndex; plot.Children.Clear();
                int column = selectedActivity + 1;
                var points = report.Days.Skip(Math.Max(0, report.Days.Count - 90)).Select(row => new HistoryChartPoint { At = DateTime.ParseExact(Text(row[0]), "yyyy-MM-dd", CultureInfo.InvariantCulture), Value = row[column] == null ? (double?)null : Convert.ToDouble(row[column]) }).ToList();
                plot.Children.Add(HistoryVisuals.Chart(points, metrics[selectedActivity], false));
            };
            selector.SelectionChanged += (sender, args) => draw(); selector.SelectedIndex = Math.Min(selectedActivity, metrics.Length - 1);
            return Card("Activity over time", "Daily measurements from supported tools. Only recorded days are shown.", selector, plot,
                details ? HistoryVisuals.Details("Show daily measurements", DaysTable(), Note("Daily totals use your local time zone and include only logged responses in the selected period.")) : (UIElement)Button("View daily activity", () => { tabs.SelectedIndex = 3; }));
        }
        private void RenderActivity()
        {
            activity.Children.Clear();
            if (report.Days.Count == 0) { activity.Children.Add(Empty("No daily activity yet", "Daily charts need recorded responses from a supported tool. Quota-only activity is shown under Quota cycles.", report.Trend.Count > 0 ? (Action)(() => { tabs.SelectedIndex = 2; }) : null, "View quota readings")); return; }
            activity.Children.Add(ActivityCard(true)); activity.Children.Add(ModelsCard());
            activity.Children.Add(Note("Unreported counters and costs are excluded from totals. A dash means unavailable."));
        }
        private DataGrid ModelsTable() { return Table(new[] { "Services", "Tool", "Model", "Responses", "Input", "Output", "Cache read", "Cache write", "Computed cost (USD)", "Cost coverage" }, report.Models.Select(row => new[] { HistoryVisuals.ProviderName(Text(row[0])), Text(row[1]), Text(row[2]), Number(row[3]), Number(row[4]), Number(row[5]), Number(row[6]), Number(row[7]), Amount(row[8]), Number(row[9]) + "/" + Number(row[3]) })); }
        private DataGrid DaysTable() { return Table(new[] { "Date", "Responses", "Input", "Output", "Cache read", "Cache write", "Computed cost (USD)", "Cost coverage" }, report.Days.Select(row => new[] { Text(row[0]), Number(row[1]), Number(row[2]), Number(row[3]), Number(row[4]), Number(row[5]), Amount(row[6]), Number(row[7]) + "/" + Number(row[1]) })); }
        private static string Text(object value) { return value == null ? "—" : Convert.ToString(value, L.Culture); }
        private static string Number(object value) { return value == null ? "—" : Convert.ToInt64(value).ToString("N0", L.Culture); }
        private static string Amount(object value) { return value == null ? "—" : Convert.ToDouble(value).ToString("0.0000", L.Culture); }
        private static string AccountLabel(object account, bool unknown) { string text = Text(account); return unknown ? L.T("Unknown account") : text.Substring(0, Math.Min(8, text.Length)); }
        private static string MetricLabel(string metric) { return HistoryVisuals.MetricLabel(metric); }

        private static DataGrid Table(string[] columns, IEnumerable<string[]> rows)
        {
            var grid = new DataGrid { AutoGenerateColumns = false, IsReadOnly = true, CanUserAddRows = false, CanUserDeleteRows = false, CanUserSortColumns = false,
                HeadersVisibility = DataGridHeadersVisibility.Column, GridLinesVisibility = DataGridGridLinesVisibility.None, MaxHeight = 360, Margin = new Thickness(0, 10, 0, 6), EnableRowVirtualization = true, FontSize = 13, BorderThickness = new Thickness(0), RowHeight = 36, Background = Brushes.Transparent, RowBackground = Brushes.Transparent };
            grid.SetResourceReference(ForegroundProperty, "Text"); grid.SetResourceReference(DataGrid.HorizontalGridLinesBrushProperty, "Divider");
            var header = new Style(typeof(DataGridColumnHeader)); header.Setters.Add(new Setter(BackgroundProperty, Brushes.Transparent)); header.Setters.Add(new Setter(ForegroundProperty, new DynamicResourceExtension("Text2"))); header.Setters.Add(new Setter(PaddingProperty, new Thickness(8, 12, 8, 12))); grid.ColumnHeaderStyle = header;
            var cell = new Style(typeof(DataGridCell)); cell.Setters.Add(new Setter(PaddingProperty, new Thickness(8, 6, 8, 6))); cell.Setters.Add(new Setter(BorderThicknessProperty, new Thickness(0))); grid.CellStyle = cell;
            for (int i = 0; i < columns.Length; i++) grid.Columns.Add(new DataGridTextColumn { Header = L.T(columns[i]), Binding = new System.Windows.Data.Binding("[" + i + "]"), MinWidth = 100 });
            grid.ItemsSource = rows.ToList(); return grid;
        }

        private async void Export()
        {
            var dialog = new SaveFileDialog { Filter = "JSON Lines (*.jsonl)|*.jsonl", FileName = "ai-meter-history-" + DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ".jsonl" };
            if (dialog.ShowDialog(this) != true) return;
            actions.IsEnabled = false; loading = true;
            try { await Task.Run(() => store.Export(dialog.FileName)); status.Text = L.T("History exported."); }
            catch (Exception exception) { Program.LogError("history-export", exception); status.Text = L.T("Export failed; see the error log."); }
            finally { actions.IsEnabled = true; loading = false; }
        }

        private async void Delete()
        {
            if (MessageBox.Show(this, L.T("Delete all retained readings, session metrics and ratings? Collection will continue if enabled. This cannot be undone."), L.T("Delete history…"), MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
            actions.IsEnabled = false; loading = true;
            try { await Task.Run(() => store.Clear()); }
            catch (Exception exception) { Program.LogError("history-delete", exception); status.Text = L.T("History unavailable; see the error log."); }
            finally { actions.IsEnabled = true; loading = false; }
            Reload();
        }
    }
}
