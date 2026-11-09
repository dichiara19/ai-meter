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
        // ------------------------------------------------------------ display surfaces
        private void BuildDisplay()
        {
            Header(L.T("Where to see it"));
            // How every surface states a limit: the pill, the panel, the widget, the overlay and tooltips.
            Group(
                Row(Ui.Glyph("", 20, "Text"), L.T("Limits show"), L.T("In the taskbar, the panel, the widget and the overlay. Video memory follows the same choice."), Combo(new[]
                {
                    Tuple.Create("left", L.T("What is left")), Tuple.Create("used", L.T("What is used")),
                }, S.LimitValues ?? "left", v => { S.LimitValues = v; Changed(); })),
                Row(null, L.T("Credit limits show"), L.T("For limits in money, such as credits, key limits and budgets."), Combo(new[]
                {
                    Tuple.Create("amount", L.T("The amount")), Tuple.Create("percent", L.T("A percentage")),
                }, S.MoneyValues ?? "amount", v => { S.MoneyValues = v; Changed(); })));
            Hint(L.T("Notifications always say what is left, since they warn that a limit is running out."));
            Group(
                Row(Ui.Glyph("", 20, "Text"), L.T("Desktop widget"), L.T("Stays below your windows, like part of the wallpaper. Drag it by its header."),
                    Pair(Button(L.T("Reset position"), () => C.ResetDesktopPosition(), false), Toggle(S.DesktopWidget, v => { S.DesktopWidget = v; Changed(); }))));
            var bar = TaskbarInfo.Read();
            string rec = L.T(bar.RecommendedSide == "left" ? "left corner" : "next to the notification area");
            Group(
                Row(Ui.Glyph("", 20, "Text"), L.T("In the taskbar"),
                    L.T("The pill sits in the free space of the main monitor's taskbar; a click opens the full panel.") + " " + bar.Describe(),
                    Toggle(S.TaskbarDock, v => { C.SetTaskbarDock(v); Rebuild(); })),
                Row(null, L.T("Position in the taskbar"), null, Combo(new[]
                {
                    Tuple.Create("auto", L.F("Automatic ({0})", rec)), Tuple.Create("left", L.T("Left corner")),
                    Tuple.Create("right", L.T("Next to the notification area")),
                }, S.TaskbarSide, v => { S.TaskbarSide = v; Changed(); })));
            Hint(L.T("Taskbar and overlay are alternatives: turning one on turns the other off."));
            Group(
                Row(Ui.Glyph("", 20, "Text"), L.T("Compact overlay"), L.T("A tiny pill, always on top. Hover over it to see the details."),
                    Toggle(S.Overlay, v => { S.Overlay = v; if (v) S.TaskbarDock = false; Changed(); Rebuild(); })),
                Row(null, L.T("Position"), null, Combo(new[]
                {
                    Tuple.Create("tr", L.T("Top right")), Tuple.Create("tl", L.T("Top left")),
                    Tuple.Create("br", L.T("Bottom right")), Tuple.Create("bl", L.T("Bottom left")),
                }, S.OverlayCorner, v => { S.OverlayCorner = v; Changed(); })),
                Row(null, L.T("Values shown"), L.T("Remaining share of the chosen limit, for services whose limit in the taskbar is set to Default."), Combo(new[]
                {
                    Tuple.Create("5h", L.T("5-hour limit")), Tuple.Create("week", L.T("Weekly limit")), Tuple.Create("both", L.T("Both")),
                }, S.OverlayMetric, v => { S.OverlayMetric = v; Changed(); })),
                Row(null, L.T("Hide in full screen"), L.T("During games, videos and presentations the pill disappears by itself."),
                    Toggle(S.OverlayHideFullscreen, v => { S.OverlayHideFullscreen = v; Changed(); })));
            Hint(L.T("The full panel is always one click away from the AI Meter icon in the notification area."));
        }

        // ------------------------------------------------------------ notifications
        private void BuildNotifications()
        {
            Header(L.T("Notifications"));
            Group(
                Row(Ui.Glyph("", 20, "Text"), L.T("When a limit runs low"),
                    L.T("A Windows notification when less than 20% and 5% is left, with when it runs out at this pace and which other service still has room."),
                    Toggle(S.NotifyLow, v => { S.NotifyLow = v; S.Save(); })),
                Row(Ui.Glyph("", 20, "Text"), L.T("When a limit is available again"),
                    L.T("After a limit you had almost used up resets."),
                    Toggle(S.NotifyReset, v => { S.NotifyReset = v; S.Save(); })));

            // Group reset-notification choices by service.
            Section(L.T("When a limit resets"));
            Hint(L.T("Every time these limits reset, at the time the service announces:"));
            var providers = C.Last == null ? new List<ProviderState>() : C.Last.Providers
                .Where(p => p.Models == null && p.Usage != null && p.Usage.Windows.Any(w => w.HasPercent && (w.ResetsAt.HasValue || w.Length.HasValue))).ToList();
            if (providers.Count == 0) Group(Row(null, L.T("No limits yet"), L.T("They appear here after the first update of the services you turned on."), null));
            foreach (var p in providers)
            {
                var pp = p;
                var rows = new List<FrameworkElement> { Row(Ui.Logo(pp.Id, 24), pp.Name, null, null) };
                foreach (var w in pp.Usage.Windows.Where(x => x.HasPercent && (x.ResetsAt.HasValue || x.Length.HasValue)))
                {
                    var ww = w;
                    string key = pp.Id + "|" + ww.Key;
                    rows.Add(Row(null, Ui.LabelOf(ww), null,
                        Toggle(S.NotifiesReset(pp.Id, ww), v => { if (v) S.NotifyResets[key] = true; else S.NotifyResets.Remove(key); S.Save(); })));
                }
                Group(rows.ToArray());
            }

            Section(L.T("Tips"));
            Hint(L.T("Tips, in the panel and as a notification once per limit window:"));
            Group(
                Row(Ui.Glyph("", 20, "Text"), L.T("Unused limit before it resets"),
                    L.T("When a limit resets soon and, at your usual pace, a good part of it would go unused. For the longest limit of each service and for credits that expire: what is left is lost."),
                    Toggle(S.TipsUnused, v => { S.TipsUnused = v; Changed(); C.RefreshTips(); })),
                Row(null, L.T("Also for short limits"),
                    L.T("The same for shorter limits such as the 5-hour session, only when the longer limit has room to absorb them."),
                    Toggle(S.TipsShort, v => { S.TipsShort = v; Changed(); C.RefreshTips(); })),
                Row(Ui.Glyph("", 20, "Text"), L.T("Wait instead of paying"),
                    L.T("When a limit is used up, extra usage is on and the limit resets soon."),
                    Toggle(S.TipsWait, v => { S.TipsWait = v; Changed(); C.RefreshTips(); })));
            Hint(L.T("Do not disturb and Focus in Windows hold these notifications like any other."));
        }

        // ------------------------------------------------------------ history
        private void BuildHistory()
        {
            Header(L.T("Usage history"));
            Group(Row(null, L.T("Keep local usage history"), L.T("Save quota readings and supported session metrics on this PC, including while the panel is closed. No prompts, titles or credentials are archived."),
                Toggle(S.HistoryEnabled, value => { S.HistoryEnabled = value; Changed(); })),
                Row(null, L.T("Usage history"), L.T("Explore consumption, quota cycles, models and value for consumption. Export or delete the archive from the dashboard."),
                    Button(L.T("Open dashboard"), C.OpenHistory, true)));
            // Readiness is the detector's latest result; this page never queries the archive itself.
            var ready = C.Consumption == null ? new List<ReadinessResult>() : C.Consumption.Readiness.Where(r => r.Scope.IsIdentified).ToList();
            var rows = new List<FrameworkElement>
            {
                Row(null, L.T("Notify unusual consumption"), L.T("Compare quota consumption with your own observed history and notify once per episode. Each limit needs at least 14 days of history; until then it keeps learning. Readings are always analysed locally, and episodes appear in the dashboard even with notifications off."),
                    Toggle(S.ConsumptionAlerts, value => { S.ConsumptionAlerts = value; S.ConsumptionSuggested = true; if (value) S.ConsumptionAlertsSince = DateTime.Now; S.Save(); })),
            };
            foreach (var r in ready)
                rows.Add(Row(null, HistoryVisuals.ProviderName(r.Scope.Provider) + " · " + HistoryVisuals.MetricLabel(r.Scope.Metric),
                    L.T(r.State == ReadinessState.Ready ? "Ready" : r.State == ReadinessState.Learning ? "Learning" : r.State == ReadinessState.ReferenceChanged ? "Reference changed" : "Temporarily unavailable")
                    + ": " + L.F(r.Reason, r.Arguments), null));
            if (ready.Count == 0) rows.Add(Row(null, L.T("Learning"), L.T("Comparisons start once readings with an identified account have been processed."), null));
            Group(rows.ToArray());
            Group(Row(null, L.T("Ask for session feedback"), L.T("Optional: after 30 minutes without a logged response, ask whether the result was worth the consumption. At most once per hour; ignoring a request leaves the session unrated."),
                Toggle(S.SessionFeedback, value => { S.SessionFeedback = value; S.FeedbackEnabledSince = DateTime.Now; S.Save(); })),
                Row(null, L.T("Value for consumption"), L.T("Rate the useful work obtained relative to the quota or money consumed."), null));
        }

        // ------------------------------------------------------------ general
        private void BuildGeneral()
        {
            Header(L.T("General"));
            var langs = new[] { Tuple.Create("system", L.T("Use the Windows language")) }
                .Concat(L.Languages.Select(l => Tuple.Create(l.Key, l.Value))).ToArray();
            Group(
                Row(Ui.Glyph("", 20, "Text"), L.T("Start with Windows"), L.T("AI Meter starts at sign-in, silently, once the desktop is ready."),
                    Toggle(Settings.StartsWithWindows, v => { Settings.StartsWithWindows = v; })),
                Row(Ui.Glyph("", 20, "Text"), L.T("Language"), null,
                    Combo(langs, S.Language ?? "system", v => { S.Language = v; S.Save(); C.ApplyLanguage(); })),
                Row(Ui.Glyph("", 20, "Text"), L.T("Theme"), null, Combo(new[]
                {
                    Tuple.Create("system", L.T("Use the Windows theme")), Tuple.Create("light", L.T("Light")), Tuple.Create("dark", L.T("Dark")),
                }, S.Theme, v => { S.Theme = v; Changed(); C.ApplyTheme(); })),
                Row(Ui.Glyph("", 20, "Text"), L.T("Refresh rate"), L.T("Automatic: every 10 seconds, every 30 on battery or with Energy Saver. Stops while the PC is locked."),
                    Combo(new[] { Tuple.Create("auto", L.T("Automatic")), Tuple.Create("low", L.T("Maximum saving")) }, S.Refresh, v => { S.Refresh = v; Changed(); })));
            Section(L.T("Windows Subsystem for Linux"));
            Group(Row(Ui.Glyph("", 20, "Text"), L.T("WSL distribution"), L.T("Where to look for Claude Code and Codex installed in Linux. AI Meter never starts WSL: it reads it only if it is already running."),
                Combo(new[] { S.WslDistro }.Concat(Settings.WslDistros()).Where(x => !string.IsNullOrEmpty(x)).Distinct().Select(x => Tuple.Create(x, x)).ToArray(), S.WslDistro,
                      v => { S.WslDistro = v; Changed(); })));
        }

        // ------------------------------------------------------------ about
        private void BuildAbout()
        {
            Header(L.T("About"));
            statsText = Ui.Text("", 12, "Text2");
            statsText.TextWrapping = TextWrapping.Wrap;
            Group(
                Row(Ui.Glyph("", 20, "Text"), "AI Meter", null, Button(L.T("Open data folder"), () => Program.OpenFolder(Settings.ExplorerFolder), false), statsText),
                Row(Ui.Glyph("", 20, "Text"), L.T("Privacy"),
                    L.T("Sessions and limits are read from local files. Only usage requests leave this PC: Claude (Anthropic), Codex (through the codex program), Copilot (GitHub) and each service you turn on, to its own site. Local models are read only on this PC. Each one can be turned off.")
                    + " " + L.T(Native.IsPackaged ? "The update check below asks the Microsoft Store whether an update is waiting." : "The update check below also asks GitHub for the latest version."),
                    Button(L.T("Source code"), () => Process.Start("https://github.com/dichiara19/ai-meter#privacy-policy"), false)),
                Row(Ui.Glyph("", 20, "Text"), L.T("Sample data"),
                    L.T("Show invented figures to preview AI Meter without signing in to any service. Not saved: real data returns at the next start."),
                    Toggle(Program.Demo, v => C.SetSampleData(v))));
            BuildUpdates();
            UpdateStats();
        }

        /// <summary>
        /// Update check: GitHub for the zip and script builds; in the Microsoft Store package only the Store's own answer,
        /// because the Store installs updates by itself.
        /// </summary>
        private void BuildUpdates()
        {
            Section(L.T("Updates"));
            // Changing a choice allows one immediate check instead of waiting up to a day.
            Action recheck = () => { S.UpdateCheckedAt = DateTime.MinValue; S.Save(); C.CheckForUpdate(); Rebuild(); };
            var rows = new List<FrameworkElement>();
            if (Native.IsPackaged)
            {
                rows.Add(Row(Ui.Glyph("\uE895", 20, "Text"), L.T("Check for updates"),
                    L.T("The Microsoft Store installs updates by itself. Once a day AI Meter also asks the Store whether one is waiting, to show it here; it never contacts GitHub."),
                    Toggle(S.UpdateCheck, v => { S.UpdateCheck = v; recheck(); })));
                if (S.UpdateCheck && C.StoreUpdateWaiting)
                    rows.Add(Row(null, L.T("An update is waiting in the Microsoft Store"), null,
                        Button(L.T("Open the Store"), () => Process.Start("ms-windows-store://downloadsandupdates"), true)));
            }
            else
            {
                rows.Add(Row(Ui.Glyph("\uE895", 20, "Text"), L.T("Check for updates"),
                    L.T("Once a day, ask GitHub whether a newer stable version exists and show a notice. Nothing is downloaded or installed, and the request includes no account or identifier."),
                    Toggle(S.UpdateCheck, v => { S.UpdateCheck = v; recheck(); })));
                rows.Add(Row(null, L.T("Include pre-releases"), L.T("Also tell me about beta and release candidate versions, which can be less stable."),
                    Toggle(S.UpdatePreReleases, v => { S.UpdatePreReleases = v; S.UpdateAvailable = null; recheck(); })));
                string latest = S.UpdateAvailable;
                if (S.UpdateCheck && UpdateCheck.IsNewer(latest, Controller.Version))
                    rows.Add(Row(null, L.F("AI Meter {0} is available", latest), L.F("You are using {0}.", Controller.Version),
                        Button(L.T("Release page"), () => Controller.OpenReleasePage(latest), true)));
            }
            Group(rows.ToArray());
        }
    }
}
