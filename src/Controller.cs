using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using Forms = System.Windows.Forms;

namespace AIMeter
{
    internal sealed partial class Controller
    {
        public Settings Settings { get; private set; }
        public Snapshot Last { get; private set; }
        public int CurrentInterval { get; private set; }
        public bool SavingPower { get; private set; }

        private readonly bool startup;
        private readonly EventWaitHandle showSignal, settingsSignal;
        private Collector collector;
        private readonly Forecaster forecaster = new Forecaster();
        private Action balloonAction;   // Action for the currently displayed notification.
        private Dispatcher ui;
        private DesktopWidget desktop;
        private OverlayPill pill;
        private TaskbarPill dock;
        private Flyout flyout;
        private SettingsWindow settingsWindow;
        private HistoryWindow historyWindow;
        private HistoryStore historyStore;
        private HistoryCollector historyCollector;
        private McpPipeHost mcpHost;
        internal ConsumptionMonitor Consumption { get; private set; }
        private List<ConsumptionNotice> pendingConsumption = new List<ConsumptionNotice>();   // Guarded by itself.
        private string pendingFeedback;
        public string HistoryError { get; private set; }
        private Forms.NotifyIcon tray;
        private string lastSignature;
        private string limitKeys;
        private DateTime flyoutHiddenAt;
        private readonly DispatcherTimer hideDetails = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
        private Native.WinEventProc foregroundProc;   // Keep the delegate alive whilst the hook can call it.
        private bool fullscreenBusy;

        private readonly AutoResetEvent wake = new AutoResetEvent(false);
        private volatile bool paused;
        private volatile bool sessionLocked, suspended;
        private long collectionPausedAt;
        private volatile bool anyVisible = true;
        private volatile bool detailsVisible;   // Open detail surfaces request fresher data.
        private volatile bool stopping;

        public Controller(bool startup, EventWaitHandle showSignal, EventWaitHandle settingsSignal)
        {
            this.startup = startup;
            this.showSignal = showSignal;
            this.settingsSignal = settingsSignal;
            CurrentInterval = 10;
        }

        public void Start()
        {
            ui = Dispatcher.CurrentDispatcher;
            Settings = Settings.Load();
            Settings.Current = Settings;
            bool firstRun = !Settings.FirstRunDone;
            try { Settings.InitialiseStartup(firstRun); } catch (Exception) { }
            L.Init(Settings.Language);
            collector = new Collector(Settings);
            historyStore = new HistoryStore(Path.Combine(Settings.Folder, "history.sqlite3"));
            historyCollector = new HistoryCollector(historyStore);
            Consumption = new ConsumptionMonitor(historyStore);
            Theme.Apply(Settings);
            bool sampleData = firstRun && AskFirstRunConsent();

            CreateFlyout();
            hideDetails.Tick += (o, e) => { hideDetails.Stop(); if (flyout.HoverMode && !flyout.IsMouseOver && (pill == null || !pill.IsMouseOver)) flyout.Hide(); };

            CreateTray();
            Native.RegisterAppIdentity("AIMeter.App", "AI Meter", NotificationIcon());
            EnsureShortcut();
            ApplySettings();
            // On first launch, use the desktop widget if the taskbar cannot host the pill, for example when vertical.
            if (firstRun && Settings.TaskbarDock && (dock == null || !dock.Usable))
            {
                Settings.DesktopWidget = true;
                Settings.Save();
                ApplySettings();
            }
            if (sampleData) SetSampleData(true);

            SystemEvents.SessionSwitch += OnSessionSwitch;
            SystemEvents.PowerModeChanged += OnPowerModeChanged;
            SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
            SystemEvents.DisplaySettingsChanged += (o, e) => ui.BeginInvoke(new Action(() => { if (desktop != null) desktop.Place(); if (pill != null) pill.Place(); if (dock != null) { dock.Place(); UpdatePillVisibility(); } }));
            foregroundProc = OnForegroundChanged;
            Native.SetWinEventHook(Native.EVENT_SYSTEM_FOREGROUND, Native.EVENT_SYSTEM_FOREGROUND, IntPtr.Zero, foregroundProc, 0, 0, 0);
            ThreadPool.RegisterWaitForSingleObject(showSignal, (s, t) => ui.BeginInvoke(new Action(ToggleFlyout)), null, -1, false);
            ThreadPool.RegisterWaitForSingleObject(settingsSignal, (s, t) => ui.BeginInvoke(new Action(OpenSettings)), null, -1, false);

            // AI apps connected through MCP read the latest snapshot; refresh their relay so it names this copy.
            mcpHost = new McpPipeHost(new McpServer(() => Last, () => Settings.McpTitles));
            mcpHost.Start();
            try { if (File.Exists(McpRelay.FilePath)) McpRelay.Write(); } catch (Exception ex) { Program.LogError("mcp-relay", ex); }

            var worker = new Thread(WorkerLoop) { IsBackground = true, Name = "AIMeter.Collector", Priority = ThreadPriority.BelowNormal };
            worker.Start();
        }

        // ================================================================ background collection
        private void WorkerLoop()
        {
            Native.EnableEcoQoSForCurrentThread();
            if (startup) Thread.Sleep(15000);   // Allow the desktop to settle after sign-in.
            int ticks = 0;
            while (!stopping)
            {
                if (paused) { wake.WaitOne(); continue; }
                bool saving = Native.SavingPower();
                Snapshot snap = null;
                try
                {
                    if (Program.Demo) snap = DemoData.Build(DateTime.Now);
                    else
                    {
                        snap = collector.Collect(anyVisible || Settings.HistoryEnabled || ticks == 0, saving);
                        forecaster.Apply(snap);
                        if (Settings.HistoryEnabled)
                        {
                            try
                            {
                                long pauseTicks = paused ? 0 : Interlocked.Read(ref collectionPausedAt);
                                historyCollector.Collect(snap, Settings, pauseTicks == 0 ? (DateTime?)null : new DateTime(pauseTicks, DateTimeKind.Utc));
                                Interlocked.CompareExchange(ref collectionPausedAt, 0, pauseTicks);
                                HistoryError = null;
                                if (!paused && Settings.SessionFeedback && pendingFeedback == null && !snap.Providers.Any(provider => provider.Busy > 0))
                                    pendingFeedback = historyStore.ClaimFeedback(snap.Taken, Settings.FeedbackEnabledSince);
                            }
                            catch (Exception exception)
                            {
                                if (HistoryError == null) Program.LogError("history-collection", exception);
                                HistoryError = exception.Message;
                            }
                            // Analysis failures never stop collection or the existing quota alerts.
                            try
                            {
                                var notices = Consumption.Process(DateTime.UtcNow,
                                    Settings.ConsumptionAlerts ? Settings.ConsumptionAlertsSince.ToUniversalTime() : (DateTime?)null, TimeZoneInfo.Local);
                                lock (pendingConsumption) pendingConsumption.AddRange(notices);
                            }
                            catch (Exception exception) { Program.LogError("consumption-analysis", exception); }
                        }
                    }
                }
                catch (Exception ex) { Trace.WriteLine(ex); }
                if (snap != null) ui.BeginInvoke(DispatcherPriority.Background, new Action(() => Apply(snap)));
                if (Json.BytesRead > 8 << 20)
                {
                    // Return memory after large initial or changed-log reads.
                    Json.BytesRead = 0;
                    System.Runtime.GCSettings.LargeObjectHeapCompactionMode = System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
                    GC.Collect(2, GCCollectionMode.Forced, true, true);
                }
                if (++ticks % 60 == 0) collector.Trim();

                int interval;
                if (!anyVisible) interval = 120;
                else if (Settings.Refresh == "low") interval = saving ? 60 : 30;
                // Refresh open details every 10 seconds; pills and widgets need only 30 seconds.
                // Double the interval on battery to reduce wake-ups.
                else if (detailsVisible) interval = saving ? 30 : 10;
                else interval = saving ? 60 : 30;
                CurrentInterval = interval;
                SavingPower = saving;
                wake.WaitOne(interval * 1000);
            }
        }

        private void Apply(Snapshot s)
        {
            Last = s;
            Notify(s);
            // Rebuild settings when the available service limits change.
            string keys = string.Join(",", s.Providers.Where(p => p.Usage != null).SelectMany(p => p.Usage.Windows.Where(w => w.HasPercent).Select(w => p.Id + "|" + w.Key)));
            if (keys != limitKeys)
            {
                limitKeys = keys;
                if (settingsWindow != null && settingsWindow.IsVisible) settingsWindow.Build();
            }
            string sig = s.Signature() + Settings.ExpandedClaude + Settings.ExpandedOpenAI + Settings.ExpandedCopilot + Settings.ExpandedLocal + string.Join(",", Settings.ExpandedServices.Where(kv => kv.Value).Select(kv => kv.Key)) + Theme.Dark + Settings.OverlayMetric + Settings.OverlayCollapsed + string.Join(",", Settings.PillLimits.Select(kv => kv.Key + kv.Value)) + string.Join(",", Settings.HiddenRows.Where(kv => kv.Value).Select(kv => kv.Key));
            UpdateTrayText(s);
            if (sig == lastSignature) return;   // Skip rendering when nothing has changed.
            lastSignature = sig;
            if (desktop != null) desktop.Render(s);
            if (pill != null) pill.Render(s, Settings.OverlayMetric);
            if (dock != null) dock.Render(s, Settings.OverlayMetric);
            if (flyout.IsVisible) flyout.Render(s);
        }

        private void Rerender() { lastSignature = null; if (Last != null) Apply(Last); }

        /// <summary>Recalculate tips immediately after settings change, without waiting for another collection.</summary>
        public void RefreshTips()
        {
            if (Last == null) return;
            foreach (var p in Last.Providers) p.Tip = Alerts.TipFor(p, Settings, DateTime.Now);
            Rerender();
        }

        public void RefreshNow() { collector.InvalidateNetwork(); wake.Set(); }

        // Settings and surface lifecycle.
        public void ApplySettings()
        {
            if (Settings.DesktopWidget)
            {
                if (desktop == null) { desktop = new DesktopWidget(this); if (Last != null) desktop.Render(Last); }
                if (!desktop.IsVisible) desktop.Show();
            }
            else if (desktop != null) { desktop.Close(); desktop = null; }

            if (Settings.Overlay)
            {
                if (pill == null) { pill = new OverlayPill(this); if (Last != null) pill.Render(Last, Settings.OverlayMetric); }
                UpdatePillVisibility();
                pill.Place();
            }
            else if (pill != null) { pill.Close(); pill = null; }

            if (Settings.TaskbarDock)
            {
                if (dock == null)
                {
                    dock = new TaskbarPill(this);
                    if (Last != null) dock.Render(Last, Settings.OverlayMetric);
                    dock.Show();
                }
                dock.Place();
                UpdatePillVisibility();
            }
            else if (dock != null) { dock.Close(); dock = null; }

            UpdateVisibility();
            Rerender();
            wake.Set();
        }

        public void ApplyTheme()
        {
            Theme.Apply(Settings);
            foreach (Window w in new Window[] { desktop, pill, flyout, settingsWindow, historyWindow })
                if (w != null) Ui.ApplyBackdrop(w, w == flyout ? 3 : 2, false);
            if (settingsWindow != null) settingsWindow.Build();
            if (historyWindow != null) historyWindow.Reload();
            Rerender();
        }

        private void CreateFlyout()
        {
            flyout = new Flyout(this);
            flyout.IsVisibleChanged += (o, e) => { if (!flyout.IsVisible) flyoutHiddenAt = DateTime.Now; UpdateVisibility(); };
        }

        /// <summary>Recreate surfaces and menus after a language change and request fresh limits.</summary>
        public void ApplyLanguage()
        {
            L.Init(Settings.Language);
            if (historyWindow != null) historyWindow.Close();
            if (desktop != null) { desktop.Close(); desktop = null; }
            if (pill != null) { pill.Close(); pill = null; }
            if (dock != null) { dock.Close(); dock = null; }
            flyout.Close();
            CreateFlyout();
            ApplySettings();   // Labels and details resolve translations when rendered.
            if (settingsWindow != null) settingsWindow.Build();
            wake.Set();        // Refresh translated provider notices immediately.
        }

        public bool IsExpanded(string id)
        {
            bool v;
            if (id == "claude") return Settings.ExpandedClaude;
            if (id == "copilot") return Settings.ExpandedCopilot;
            if (id == "openai") return Settings.ExpandedOpenAI;
            if (id == "local") return Settings.ExpandedLocal;
            return Settings.ExpandedServices.TryGetValue(id, out v) && v;
        }

        public void ToggleExpanded(string id)
        {
            SetExpanded(id, !IsExpanded(id));
            Settings.Save();
            Rerender();
        }

        private void SetExpanded(string id, bool v)
        {
            if (id == "claude") Settings.ExpandedClaude = v;
            else if (id == "copilot") Settings.ExpandedCopilot = v;
            else if (id == "openai") Settings.ExpandedOpenAI = v;
            else if (id == "local") Settings.ExpandedLocal = v;
            else Settings.ExpandedServices[id] = v;
        }

        public void Exit()
        {
            stopping = true;
            wake.Set();
            SystemEvents.SessionSwitch -= OnSessionSwitch;
            SystemEvents.PowerModeChanged -= OnPowerModeChanged;
            SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
            tray.Visible = false;
            tray.Dispose();
            Application.Current.Shutdown();
        }

        // ================================================================ system events
        private void OnSessionSwitch(object sender, SessionSwitchEventArgs e)
        {
            if (e.Reason == SessionSwitchReason.SessionLock || e.Reason == SessionSwitchReason.ConsoleDisconnect || e.Reason == SessionSwitchReason.RemoteDisconnect)
                sessionLocked = true;
            else if (e.Reason == SessionSwitchReason.SessionUnlock || e.Reason == SessionSwitchReason.ConsoleConnect || e.Reason == SessionSwitchReason.RemoteConnect)
                sessionLocked = false;
            UpdateCollectionPause();
        }

        private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
        {
            if (e.Mode == PowerModes.Suspend) { suspended = true; UpdateCollectionPause(); }
            else if (e.Mode == PowerModes.Resume) { suspended = false; collector.InvalidateNetwork(); UpdateCollectionPause(); }
            else wake.Set();   // Recalculate the interval when the power source changes.
        }

        private void UpdateCollectionPause()
        {
            bool shouldPause = sessionLocked || suspended;
            if (shouldPause) Interlocked.CompareExchange(ref collectionPausedAt, DateTime.UtcNow.Ticks, 0);
            paused = shouldPause;
            if (!paused) wake.Set();
        }

        private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
        {
            if (e.Category != UserPreferenceCategory.General && e.Category != UserPreferenceCategory.Color && e.Category != UserPreferenceCategory.VisualStyle) return;
            ui.BeginInvoke(new Action(() =>
            {
                bool wasDark = Theme.Dark;
                Theme.Apply(Settings);
                if (wasDark != Theme.Dark || e.Category == UserPreferenceCategory.Color) ApplyTheme();
                if (dock != null) { dock.Refresh(Last, Settings.OverlayMetric); UpdatePillVisibility(); }
            }));
        }
    }
}
