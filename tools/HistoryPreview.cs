using System;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace AIMeter
{
    /// <summary>Render the real dashboard with isolated synthetic data, without showing windows or launching the installed app.</summary>
    public static class HistoryPreview
    {
        public static void Run(string output, string storage, string language)
        {
            var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            Settings.TestFolder = storage;
            Directory.CreateDirectory(storage); Directory.CreateDirectory(output);
            L.Init(language);
            var settings = (Settings)FormatterServices.GetUninitializedObject(typeof(Settings));
            settings.HistoryEnabled = true;
            Settings.Current = settings;
            var controller = new Controller(false, null, null);
            typeof(Controller).GetProperty("Settings").SetValue(controller, settings, null);
            var store = new HistoryStore(Path.Combine(storage, "history.sqlite3"));
            store.Query("SELECT 1");
            DateTime now = DateTime.Now;
            DateTime first = now.AddDays(-5);
            store.Query("UPDATE metadata SET value=? WHERE key='started'", HistoryStore.Stamp(first).ToString(CultureInfo.InvariantCulture));
            store = new HistoryStore(store.FilePath);
            for (int i = 0; i < 20; i++)
            {
                DateTime at = now.AddMinutes((i - 20) * 5);
                var snapshot = new Snapshot { Taken = at };
                var usage = new Usage { Source = "live", Identity = "synthetic-account", At = at };
                usage.Windows.Add(new UsageWindow { Label = "Session 5 h", Short = UsageWindow.ShortFiveHours, Used = 10 + i * 2.1, ResetsAt = now.AddHours(2), StartedAt = now.AddHours(-3) });
                usage.Windows.Add(new UsageWindow { Label = "Week", Short = UsageWindow.ShortWeek, Used = 24 + i * 0.5, ResetsAt = now.AddDays(3), StartedAt = now.AddDays(-4) });
                usage.Windows.Add(new UsageWindow { Label = "Spent today", Amount = 2.35 + i * 0.12, AmountUnit = "USD", IsEstimated = true, NoBar = true });
                snapshot.Providers.Add(new ProviderState { Id = "claude", Name = "Claude", Plan = "Max", Usage = usage });
                int index = i;
                store.Record(snapshot, db => HistoryStore.AddEvent(db, new HistoryEvent { Id = "synthetic-event-" + index, SessionId = "synthetic-session-" + index / 4,
                    Provider = "claude", Agent = index / 4 % 2 == 0 ? "Pi" : "Claude Code", Model = index / 4 % 2 == 0 ? "Sonnet" : "Opus",
                    At = now.Date.AddDays(-4 + index / 4).AddHours(9).AddMinutes(index % 4 * 15),
                    Input = 1200 + index * 175, Output = 350 + index * 30, CacheRead = 24000 + index * 280, CacheWrite = index * 500, Cost = index / 4 % 2 == 0 ? (double?)(0.08 + index * 0.004) : null }));
            }
            store.Rate("synthetic-session-0", 1); store.Rate("synthetic-session-1", 0); store.Rate("synthetic-session-2", -1);
            var report = new HistoryAnalysis(store).Read(first, "");
            AddConsumption(report, now);
            var emptyStore = new HistoryStore(Path.Combine(storage, "empty.sqlite3"));
            var empty = new HistoryAnalysis(emptyStore).Read(first, "");
            var firstStore = new HistoryStore(Path.Combine(storage, "first.sqlite3"));
            var firstSnapshot = new Snapshot { Taken = now };
            var firstUsage = new Usage { At = now, Source = "live", Identity = "fixture" };
            firstUsage.Windows.Add(new UsageWindow { Label = "Session 5 h", Used = 0, ResetsAt = now.AddHours(5) });
            firstSnapshot.Providers.Add(new ProviderState { Id = "claude", Usage = firstUsage }); firstStore.Record(firstSnapshot, null);
            var firstReading = new HistoryAnalysis(firstStore).Read(first, "");
            var zeroStore = new HistoryStore(Path.Combine(storage, "zero.sqlite3")); zeroStore.Query("SELECT 1");
            zeroStore.Query("UPDATE metadata SET value=? WHERE key='started'", HistoryStore.Stamp(first).ToString(CultureInfo.InvariantCulture));
            zeroStore = new HistoryStore(zeroStore.FilePath);
            for (int day = 0; day < 3; day++)
            {
                int index = day;
                zeroStore.Record(new Snapshot { Taken = now.AddDays(-day) }, db => HistoryStore.AddEvent(db,
                    new HistoryEvent { Id = "zero" + index, SessionId = "zero-session", Agent = "Pi", Model = "fixture", Provider = "claude", At = now.AddDays(-index), Input = 0, Output = 0, CacheRead = 0, CacheWrite = 0, Cost = 0 }));
            }
            var zero = new HistoryAnalysis(zeroStore).Read(first, "");
            var filtered = new HistoryAnalysis(store).Read(first, "openai");
            settings.Theme = "light"; Theme.Apply(settings);
            var nativeWindow = new HistoryWindow(controller, emptyStore) { ShowActivated = false };
            new System.Windows.Interop.WindowInteropHelper(nativeWindow).EnsureHandle();
            var chrome = System.Windows.Shell.WindowChrome.GetWindowChrome(nativeWindow);
            if (chrome == null || chrome.GlassFrameThickness.Left != -1 || chrome.CaptionHeight != 48)
                throw new InvalidOperationException("History did not initialise the shared settings chrome.");
            nativeWindow.Close();
            Console.WriteLine("Native history chrome initialised in a hidden window; the running app was not touched.");
            foreach (bool dark in new[] { false, true })
            {
                settings.Theme = dark ? "dark" : "light"; Theme.Apply(settings);
                string theme = dark ? "dark" : "light";
                Render(controller, store, report, output, "history-" + theme, false);
                Render(controller, emptyStore, empty, output, "empty-" + theme, false);
                Render(controller, firstStore, firstReading, output, "first-" + theme, false);
                Render(controller, zeroStore, zero, output, "zero-" + theme, true);
                Render(controller, store, filtered, output, "filtered-" + theme, false);
            }
            Render(controller, store, report, output, "contrast", false, true);
            application.Shutdown();
        }

        /// <summary>Synthetic detector state: one ready limit with an ongoing episode, one still learning.</summary>
        private static void AddConsumption(HistoryReport report, DateTime now)
        {
            Func<string, ConsumptionScope> scope = metric => new ConsumptionScope { Provider = "claude", Account = "synthetic", Metric = metric, Source = "live", Plan = "Max" };
            report.Readiness.Add(new ReadinessResult { Scope = scope("Week|"), State = ReadinessState.Ready, Reason = "Compared with {0} days with consumption.", Arguments = new object[] { 23 } });
            report.Readiness.Add(new ReadinessResult { Scope = scope("Session 5 h|"), State = ReadinessState.Learning, Reason = "{0} of {1} observed days.", Arguments = new object[] { 9, 14 } });
            DateTime start = now.AddMinutes(-40).ToUniversalTime();
            var baseline = new BaselineSummary { Context = "weekday", TimeZone = TimeZoneInfo.Local.Id, FromUtc = start.AddDays(-30), ToUtc = start.Date, ActiveDays = 23 };
            report.Episodes.Add(new AlertEpisode
            {
                Id = "synthetic", Provider = "claude", Metric = "Week|", State = AlertEpisode.Open, OpenedUtc = start, LastUtc = start.AddMinutes(30), WasEligible = true,
                Evidence = new AnomalyEvidence { Kind = EvidenceKind.Sustained, StartUtc = start, EndUtc = start.AddMinutes(30), Points = 6, Minutes = 31, RatePerHour = 11.6, ThresholdPerHour = 4.2 },
                Baseline = Json.Parse(Json.Serialize(baseline.ToJson())),
                Candidates = { new SessionCandidate { SessionId = "synthetic-session-4", Agent = "Claude Code", Model = "Opus", First = now.AddMinutes(-45), Last = now.AddMinutes(-12), Replies = 38, Input = 41200, Output = 9800, CacheRead = 1840000, CacheWrite = 96000 } },
            });
        }

        private static void Render(Controller controller, HistoryStore store, HistoryReport report, string output, string prefix, bool zero, bool contrast = false)
        {
            var window = new HistoryWindow(controller, store);
            if (contrast) window.ApplyContrast(true);
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            typeof(HistoryWindow).GetField("report", flags).SetValue(window, report);
            if (zero) typeof(HistoryWindow).GetField("selectedActivity", flags).SetValue(window, 1);
            foreach (string method in new[] { "RenderOverview", "RenderSessions", "RenderQuotas", "RenderActivity" })
                typeof(HistoryWindow).GetMethod(method, flags).Invoke(window, null);
            if (report.Episodes.Count > 0)
            {
                var card = (Border)typeof(HistoryWindow).GetMethod("ConsumptionCard", flags).Invoke(window, null);
                var host = new Border { Width = 900, Padding = new Thickness(16), Child = card };
                host.SetResourceReference(Border.BackgroundProperty, "Fallback");
                foreach (object key in window.Resources.Keys) host.Resources[key] = window.Resources[key];
                MediaRenderer.Save(host, Path.Combine(output, prefix + "-consumption.png"), 1);
            }
            var status = (TextBlock)typeof(HistoryWindow).GetField("status", flags).GetValue(window);
            status.Text = L.F("History since {0} · view refreshed at {1}", report.Started.ToString("g", L.Culture), DateTime.Now.ToString("t", L.Culture));
            var tabs = (TabControl)typeof(HistoryWindow).GetField("tabs", flags).GetValue(window);
            if (prefix.StartsWith("filtered-", StringComparison.Ordinal))
            {
                var providers = (ComboBox)typeof(HistoryWindow).GetField("provider", flags).GetValue(window);
                providers.Items.Add(new ComboBoxItem { Content = "ChatGPT", Tag = "openai" }); providers.SelectedIndex = 1;
            }
            var content = (FrameworkElement)window.Content; window.Content = null;
            var frame = new Border { Child = content }; frame.SetResourceReference(Border.BackgroundProperty, "Fallback");
            foreach (object key in window.Resources.Keys) frame.Resources[key] = window.Resources[key];
            System.Windows.Documents.TextElement.SetFontFamily(frame, Theme.Font); System.Windows.Documents.TextElement.SetFontSize(frame, 14);
            foreach (int tab in new[] { 0, 1, 2, 3 })
            {
                tabs.SelectedIndex = tab;
                Save(frame, Path.Combine(output, prefix + "-" + tab + ".png"), 1160, 860);
            }
            tabs.SelectedIndex = prefix.StartsWith("history-", StringComparison.Ordinal) ? 1 : 0;
            Save(frame, Path.Combine(output, prefix + "-narrow.png"), 720, 640);
            if (prefix == "history-dark" || prefix == "history-light")
            {
                tabs.SelectedIndex = 0; frame.Background = Brushes.Transparent;
                var scene = MediaRenderer.HistoryShot(frame, prefix == "history-dark");
                MediaRenderer.Save(scene, Path.Combine(output, prefix + "-release.png"), 2);
            }
            window.Close();
        }

        private static void Save(FrameworkElement view, string path, int width, int height)
        {
            view.Width = width; view.Height = height;
            view.Measure(new Size(width, height)); view.Arrange(new Rect(0, 0, width, height)); view.UpdateLayout();
            var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32); bitmap.Render(view);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var stream = File.Create(path)) encoder.Save(stream);
        }
    }
}
