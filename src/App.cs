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
    /// <summary>
    /// Public entry point for the PowerShell launcher (AIMeter.ps1). Smart App Control blocks unsigned
    /// executables, whilst Microsoft-signed PowerShell can compile and execute the sources.
    /// </summary>
    public static class Entry
    {
        public static void Run(string[] args)
        {
            // Configure per-monitor DPI before WPF initialises when the executable manifest is unavailable.
            Native.EnablePerMonitorDpi();
            Native.SetAppId("AIMeter.App");
            AppContext.SetSwitch("Switch.System.Windows.DoNotScaleForDpiChanges", false);
            Program.Run(args);
        }
    }

    internal static class Program
    {
        /// <summary>Installation directory, provided by the PowerShell launcher or containing the executable.</summary>
        public static string Home
        {
            get { return Environment.GetEnvironmentVariable("AIMETER_HOME") ?? AppDomain.CurrentDomain.BaseDirectory; }
        }

        /// <summary>Command Windows uses to launch the app at sign-in.</summary>
        public static string LaunchCommand
        {
            get
            {
                return Environment.GetEnvironmentVariable("AIMETER_LAUNCH")
                       ?? "\"" + System.Reflection.Assembly.GetExecutingAssembly().Location + "\"";
            }
        }

        /// <summary>Split LaunchCommand into an absolute executable path and shortcut arguments.</summary>
        public static void SplitLaunch(out string target, out string args)
        {
            string cmd = LaunchCommand.Trim();
            if (cmd.StartsWith("\"")) { int q = cmd.IndexOf('"', 1); target = cmd.Substring(1, q - 1); args = cmd.Substring(q + 1).Trim(); }
            else { int sp = cmd.IndexOf(' '); target = sp < 0 ? cmd : cmd.Substring(0, sp); args = sp < 0 ? "" : cmd.Substring(sp + 1); }
            if (!Path.IsPathRooted(target)) target = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), target);
        }

        /// <summary>App icon as path,index: the executable or assets\app.ico when running from source.</summary>
        public static string IconPath
        {
            get
            {
                string ico = Path.Combine(Home, @"assets\app.ico");
                if (File.Exists(ico)) return ico + ",0";
                return System.Reflection.Assembly.GetExecutingAssembly().Location + ",0";
            }
        }

        /// <summary>Windows shown in the taskbar use the AI Meter name and icon.</summary>
        public static void Brand(Window w)
        {
            if (Native.IsPackaged) return;   // The MSIX package already supplies the window identity.
            w.SourceInitialized += (o, e) =>
                Native.SetWindowAppId(new System.Windows.Interop.WindowInteropHelper(w).Handle, "AIMeter.App", IconPath, "AI Meter", LaunchCommand);
        }

        /// <summary>--demo uses sample data for previews and screenshots, without network access or notifications.</summary>
        public static bool Demo { get; private set; }

        public static Stream OpenAsset(string name)
        {
            var s = typeof(Program).Assembly.GetManifestResourceStream(name);
            if (s != null) return s;
            string f = Path.Combine(Home, "assets", name);
            return File.Exists(f) ? File.OpenRead(f) : null;
        }

        [STAThread]
        private static void Main(string[] args) { Native.SetAppId("AIMeter.App"); Run(args); }

        /// <summary>Handled-error log (%APPDATA%\AIMeter\errors.log, rotated at 256 KB), including exception stacks.</summary>
        internal static void LogError(string where, Exception ex)
        {
            try
            {
                string f = Path.Combine(Settings.Folder, "errors.log");
                Directory.CreateDirectory(Settings.Folder);
                if (File.Exists(f) && new FileInfo(f).Length > 256 * 1024) File.Delete(f);
                File.AppendAllText(f, DateTime.Now.ToString("s", System.Globalization.CultureInfo.InvariantCulture) + "  [" + where + "] " + ex + Environment.NewLine + Environment.NewLine);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        internal static void Run(string[] args)
        {
            bool created;
            using (var mutex = new Mutex(true, @"Local\AIMeter.Instance", out created))
            {
                var showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\AIMeter.Show");
                var settingsSignal = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\AIMeter.Settings");
                if (!created)
                {
                    // An existing instance handles requests to open the panel or settings.
                    (args.Contains("--settings") ? settingsSignal : showSignal).Set();
                    return;
                }

                Demo = args.Contains("--demo");
                ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12 | (SecurityProtocolType)12288;
                // Software rendering avoids a Direct3D device (roughly 60 MB) and GPU work.
                // Windows still composites Mica, acrylic and shadows. --gpu enables hardware rendering.
                if (!args.Contains("--gpu")) System.Windows.Media.RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
                var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                // Log unexpected UI errors, such as Windows refusing a window during taskbar recreation,
                // and keep the app running.
                app.DispatcherUnhandledException += (o, e) => { LogError("ui", e.Exception); e.Handled = true; };
                bool atLogin = args.Contains("--startup");
#if MSIX
                atLogin = atLogin || (Native.IsPackaged && PackageStartup.LaunchedAtLogin);
#endif
                var controller = new Controller(atLogin, showSignal, settingsSignal);
                app.Startup += (o, e) => controller.Start();
                app.Run();
                GC.KeepAlive(mutex);
            }
        }
    }

    internal sealed class Controller
    {
        public Settings Settings { get; private set; }
        public Snapshot Last { get; private set; }
        public int CurrentInterval { get; private set; }
        public bool SavingPower { get; private set; }

        private readonly bool startup;
        private readonly EventWaitHandle showSignal, settingsSignal;
        private Collector collector;
        private readonly Forecaster forecaster = new Forecaster();
        private Alerts alerts;
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
        internal ConsumptionMonitor Consumption { get; private set; }
        private List<ConsumptionNotice> pendingConsumption = new List<ConsumptionNotice>();   // Guarded by itself.
        private string pendingFeedback;
        public string HistoryError { get; private set; }
        private Window menuHost;
        private Forms.NotifyIcon tray;
        private string lastSignature;
        private string limitKeys;
        private DateTime flyoutHiddenAt;
        private readonly DispatcherTimer hideDetails = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
        private DispatcherTimer loginWatch;
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
            if (firstRun)
            {
                Settings.FirstRunDone = true;
                Settings.Save();
            }
            try { Settings.InitialiseStartup(firstRun); } catch (Exception) { }
            L.Init(Settings.Language);
            collector = new Collector(Settings);
            historyStore = new HistoryStore(Path.Combine(Settings.Folder, "history.sqlite3"));
            historyCollector = new HistoryCollector(historyStore);
            Consumption = new ConsumptionMonitor(historyStore);
            Theme.Apply(Settings);

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
            if (firstRun) ShowPrivacyNotice();

            SystemEvents.SessionSwitch += OnSessionSwitch;
            SystemEvents.PowerModeChanged += OnPowerModeChanged;
            SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
            SystemEvents.DisplaySettingsChanged += (o, e) => ui.BeginInvoke(new Action(() => { if (desktop != null) desktop.Place(); if (pill != null) pill.Place(); if (dock != null) { dock.Place(); UpdatePillVisibility(); } }));
            foregroundProc = OnForegroundChanged;
            Native.SetWinEventHook(Native.EVENT_SYSTEM_FOREGROUND, Native.EVENT_SYSTEM_FOREGROUND, IntPtr.Zero, foregroundProc, 0, 0, 0);
            ThreadPool.RegisterWaitForSingleObject(showSignal, (s, t) => ui.BeginInvoke(new Action(ToggleFlyout)), null, -1, false);
            ThreadPool.RegisterWaitForSingleObject(settingsSignal, (s, t) => ui.BeginInvoke(new Action(OpenSettings)), null, -1, false);

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

        /// <summary>Sign in through Claude Code, GitHub for Copilot, or the selected service's own mechanism.</summary>
        public void Login(string id)
        {
            var def = Definitions.Get(id);
            if (id == "copilot") LoginGitHub();
            else if (id == "antigravity") LoginAntigravity();
            else if (id == "claude") LoginClaude();
            else if (def != null && def.UsesKey) EnterApiKey(def);
        }

        private GitHubLoginWindow githubLogin;

        public void LoginGitHub()
        {
            if (githubLogin != null) { githubLogin.Activate(); return; }
            githubLogin = new GitHubLoginWindow();
            githubLogin.SignedIn += () => { RefreshNow(); if (settingsWindow != null) settingsWindow.Build(); };
            githubLogin.Closed += (o, e) => githubLogin = null;
            githubLogin.Show();
            githubLogin.Activate();
        }

        /// <summary>Open the API key entry window for a definition-based service.</summary>
        public void EnterApiKey(ServiceDefinition d) { EnterApiKey(d, null); }

        /// <summary>An optional slot selects an extra key, such as OpenRouter management; null selects the primary key.</summary>
        public void EnterApiKey(ServiceDefinition d, string slot)
        {
            var w = new ApiKeyWindow(d, slot);
            w.Saved += () => { RefreshNow(); if (settingsWindow != null) settingsWindow.Build(); };
            w.Show();
            w.Activate();
        }

        public void RemoveApiKey(ServiceDefinition d)
        {
            ApiKeys.Remove(d.Id);
            RefreshNow();
        }

        public void SignOutGitHub()
        {
            GitHubAuth.SignOut();
            RefreshNow();
        }

        /// <summary>Open Antigravity CLI sign-in in a terminal, preferring Windows when installed.</summary>
        public void LoginAntigravity()
        {
            try
            {
                string exe = Path.Combine(Probe.Home, @".local\bin\agy.exe");
                if (File.Exists(exe))
                    Process.Start(new ProcessStartInfo("cmd.exe", "/c \"\"" + exe + "\"\"") { UseShellExecute = true });
                else
                    Process.Start(new ProcessStartInfo("wsl.exe", "-d " + Settings.WslDistro + " -- bash -lc \"~/.local/bin/agy\"") { UseShellExecute = true });
            }
            catch (System.ComponentModel.Win32Exception) { return; }
            RefreshNow();
        }

        public void ResetDesktopPosition()
        {
            if (desktop != null) desktop.ResetPosition();
            else { Settings.DesktopX = double.MinValue; Settings.DesktopY = double.MinValue; Settings.Save(); }
        }

        public void OpenHistory()
        {
            if (Program.Demo) return;
            if (historyWindow == null)
            {
                historyWindow = new HistoryWindow(this, historyStore);
                historyWindow.Closed += (sender, args) => { historyWindow = null; UpdateVisibility(); };
                historyWindow.Show();
            }
            if (historyWindow.WindowState == WindowState.Minimized) historyWindow.WindowState = WindowState.Normal;
            historyWindow.Activate();
            UpdateVisibility();
        }

        public void OpenSettings()
        {
            if (settingsWindow == null)
            {
                settingsWindow = new SettingsWindow(this);
                settingsWindow.Closed += (o, e) => settingsWindow = null;
                settingsWindow.Show();
            }
            if (settingsWindow.WindowState == WindowState.Minimized) settingsWindow.WindowState = WindowState.Normal;
            settingsWindow.Activate();
        }

        public void OpenSettings(string page)
        {
            OpenSettings();
            settingsWindow.Open(page);
        }

        /// <summary>Open Claude Code sign-in in a terminal; the CLI handles authentication.</summary>
        public void LoginClaude()
        {
            try
            {
                if (UseWslForLogin())
                    Process.Start(new ProcessStartInfo("wsl.exe", "-d " + Settings.WslDistro + " -- bash -lc \"claude auth login; echo; read -p '" + L.T("Done. Press Enter to close.").Replace("'", "") + "'\"") { UseShellExecute = true });
                else
                {
                    string cli = ClaudeProvider.CliPath();
                    if (cli == null) { MessageBox.Show(L.T("Claude Code was not found on this PC."), "AI Meter"); return; }
                    Process.Start(new ProcessStartInfo("cmd.exe", "/c \"\"" + cli + "\" auth login & pause\"") { UseShellExecute = true });
                }
            }
            catch (System.ComponentModel.Win32Exception) { return; }

            // Check briefly for completed sign-in, for at most five minutes.
            if (loginWatch != null) loginWatch.Stop();
            int checks = 0;
            loginWatch = new DispatcherTimer { Interval = TimeSpan.FromSeconds(15) };
            loginWatch.Tick += (o, e) =>
            {
                var c = Last == null ? null : Last.Providers.FirstOrDefault(p => p.Id == "claude");
                if (++checks > 20 || (c != null && c.Usage != null && c.Usage.Source == "live")) { loginWatch.Stop(); if (settingsWindow != null) settingsWindow.Build(); return; }
                RefreshNow();
            };
            loginWatch.Start();
        }

        /// <summary>Sign in where Claude Code is installed: configured WSL (explicitly requested here), otherwise Windows.</summary>
        private bool UseWslForLogin()
        {
            if (string.IsNullOrEmpty(Settings.WslDistro)) return false;
            if (!collector.WslUp) return true;
            return Directory.Exists(@"\\wsl.localhost\" + Settings.WslDistro + @"\home\" + Settings.WslUser + @"\.claude");
        }

        // ================================================================ panel and pill
        public void ToggleFlyout()
        {
            if (flyout.IsVisible && !flyout.HoverMode) { flyout.Hide(); return; }
            if ((DateTime.Now - flyoutHiddenAt).TotalMilliseconds < 300) return; // This click has just closed the panel.
            if (Last != null) flyout.Render(Last);
            flyout.ShowFromTray();
            UpdateVisibility();
            RefreshNow();
        }

        public void ShowDetails(Window anchor)
        {
            CancelHideDetails();
            if (flyout.IsVisible) return;
            if (Last != null) flyout.Render(Last);
            flyout.ShowNear(anchor);
            UpdateVisibility();
        }

        public void SetOverlayCollapsed(bool collapsed)
        {
            Settings.OverlayCollapsed = collapsed;
            Settings.Save();
            if (flyout.HoverMode && flyout.IsVisible) flyout.Hide();
            Rerender();
        }

        public void CloseOverlay()
        {
            if (flyout.HoverMode && flyout.IsVisible) flyout.Hide();
            Settings.Overlay = false;
            Settings.Save();
            ApplySettings();
        }

        public void ScheduleHideDetails() { hideDetails.Stop(); hideDetails.Start(); }
        public void CancelHideDetails() { hideDetails.Stop(); }

        private void UpdateVisibility()
        {
            anyVisible = (desktop != null && desktop.IsVisible) || (pill != null && pill.IsVisible) || (dock != null && dock.IsVisible) || flyout.IsVisible
                         || settingsWindow != null || historyWindow != null;
            bool details = flyout.IsVisible || settingsWindow != null || historyWindow != null;
            if (details && !detailsVisible) wake.Set();   // Refresh immediately when details open.
            detailsVisible = details;
        }

        private void OnForegroundChanged(IntPtr hook, uint ev, IntPtr hwnd, int obj, int child, uint thread, uint time)
        {

            if (flyout.IsVisible && flyout.TaskbarMode && !Native.IsOwnWindow(hwnd)) flyout.Hide();   // A click outside the app closes the taskbar flyout.
            bool busy = Native.UserIsBusyFullscreen();
            if (busy == fullscreenBusy) return;
            fullscreenBusy = busy;
            UpdatePillVisibility();
        }

        private void UpdatePillVisibility()
        {
            if (pill != null)
            {
                bool hide = Settings.OverlayHideFullscreen && fullscreenBusy;
                if (hide && pill.IsVisible) pill.Hide();
                else if (!hide && !pill.IsVisible) pill.Show();
            }
            if (dock != null)
            {
                // The docked pill follows the taskbar automatically in fullscreen and auto-hide modes.
                dock.Place();
            }
            UpdateVisibility();
        }

        public void ToggleFlyoutFromTaskbar(TaskbarPill anchor)
        {
            if (flyout.IsVisible && !flyout.HoverMode) { flyout.Hide(); return; }
            if ((DateTime.Now - flyoutHiddenAt).TotalMilliseconds < 300) return; // This click has just closed the panel.
            if (Last != null) flyout.Render(Last);
            flyout.ShowFromTaskbar(anchor.OnLeft);
            UpdateVisibility();
            RefreshNow();
        }

        /// <summary>The taskbar pill and floating overlay are mutually exclusive.</summary>
        public void SetTaskbarDock(bool on)
        {
            Settings.TaskbarDock = on;
            if (on) Settings.Overlay = false;
            Settings.Save();
            ApplySettings();
        }

        // ================================================================ notification area and menu
        private void CreateTray()
        {
            tray = new Forms.NotifyIcon { Text = "AI Meter", Visible = true };
            using (var s = Program.OpenAsset("app.ico"))
                tray.Icon = new System.Drawing.Icon(s, Forms.SystemInformation.SmallIconSize);
            tray.MouseUp += (o, e) =>
            {
                if (e.Button == Forms.MouseButtons.Left) ToggleFlyout();
                else if (e.Button == Forms.MouseButtons.Right) ShowMenu(null);
            };
            tray.BalloonTipClicked += (o, e) => { var a = balloonAction; balloonAction = null; if (a != null) a(); };
        }

        /// <summary>Show limit notifications together because Windows displays only the latest balloon. Clicking opens the panel.</summary>
        private void Notify(Snapshot s)
        {
            if (Program.Demo) { foreach (var p in s.Providers) p.Tip = Alerts.TipFor(p, Settings, DateTime.Now); return; }
            if (alerts == null) alerts = new Alerts();
            var list = alerts.Check(s, Settings);
            // Consumption notices join the same balloon, so they never compete with low-quota warnings.
            List<ConsumptionNotice> consumption;
            lock (pendingConsumption) { consumption = pendingConsumption.ToList(); pendingConsumption.Clear(); }
            // A notice delayed past its relevance stays visible in the history instead of interrupting late.
            consumption = consumption.Where(c => Settings.ConsumptionAlerts && (DateTime.UtcNow - c.EventUtc).TotalMinutes <= 30).ToList();
            if (list.Count == 0 && consumption.Count == 0) { if (!SuggestConsumptionAlerts()) NotifyFeedback(); return; }
            Action open = () => { if (!flyout.IsVisible) ToggleFlyout(); };
            if (list.Count == 0)
            {
                var c = consumption[consumption.Count - 1];
                list.Add(Tuple.Create(c.Title, c.Text));
                open = () => OpenHistory();
                consumption.RemoveAt(consumption.Count - 1);
            }
            int others = list.Count - 1 + consumption.Count;
            var n = list[list.Count - 1];
            string text = others > 0 ? n.Item2 + "\n" + L.N(others, "+{0} other notice", "+{0} other notices") : n.Item2;
            balloonAction = open;
            tray.ShowBalloonTip(15000, n.Item1, text, Forms.ToolTipIcon.None);
        }

        /// <summary>Suggest the opt-in once, when at least one scope is ready. Returns whether a balloon was shown.</summary>
        private bool SuggestConsumptionAlerts()
        {
            if (Settings.ConsumptionAlerts || Settings.ConsumptionSuggested || !Settings.HistoryEnabled) return false;
            if (!Consumption.Readiness.Any(r => r.State == ReadinessState.Ready)) return false;
            Settings.ConsumptionSuggested = true;
            Settings.Save();
            balloonAction = () => OpenSettings("history");
            tray.ShowBalloonTip(15000, L.T("Unusual consumption alerts are available"),
                L.T("Your history is now enough to compare consumption with your usual range. Open Settings to turn on these notifications."), Forms.ToolTipIcon.None);
            return true;
        }

        private void NotifyFeedback()
        {
            if (!Settings.SessionFeedback || !Settings.HistoryEnabled) { pendingFeedback = null; return; }
            if (pendingFeedback == null || Native.UserIsBusyFullscreen()) return;
            string session = Interlocked.Exchange(ref pendingFeedback, null);
            if (session == null) return;
            balloonAction = () => { OpenHistory(); if (historyWindow != null) historyWindow.FocusSession(session); };
            tray.ShowBalloonTip(15000, L.T("Was the result worth this session's consumption?"),
                L.T("Rate the useful work obtained relative to the quota or money consumed.") + "\n" + L.T("Optional feedback · open to choose Yes, Partly or No."), Forms.ToolTipIcon.None);
        }

        /// <summary>Create a Start shortcut with the app identity so notifications have a name and icon.</summary>
        private static void EnsureShortcut()
        {
            string lnk = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "AI Meter.lnk");
            string target, args;
            Program.SplitLaunch(out target, out args);
            string icon = Program.IconPath;
            int comma = icon.LastIndexOf(',');
            Native.EnsureStartShortcut(lnk, target, args, Program.Home, icon.Substring(0, comma), 0, "AIMeter.App");
        }

        /// <summary>Notification icon file, taken from assets or extracted from the embedded resource.</summary>
        private static string NotificationIcon()
        {
            try
            {
                string f = Path.Combine(Program.Home, @"assets\app.ico");
                if (File.Exists(f)) return f;
                f = Path.Combine(Settings.Folder, "app.ico");
                if (!File.Exists(f))
                    using (var s = Program.OpenAsset("app.ico"))
                    {
                        if (s == null) return null;
                        Directory.CreateDirectory(Settings.Folder);
                        using (var o = File.Create(f)) s.CopyTo(o);
                    }
                return f;
            }
            catch (IOException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
        }

        /// <summary>
        /// On first launch, explain which data leaves this PC and how to disable network collection.
        /// Only enabled services' own usage endpoints are contacted.
        /// </summary>
        private void ShowPrivacyNotice()
        {
            balloonAction = OpenSettings;
            tray.ShowBalloonTip(20000, L.T("AI Meter is running"),
                L.T("It reads sessions and limits from local files. Only real-time Claude limits contact Anthropic with your Claude Code login: you can turn them off in Settings."),
                Forms.ToolTipIcon.Info);
        }

        public static string Version
        {
            get
            {
                var a = (System.Reflection.AssemblyInformationalVersionAttribute)Attribute.GetCustomAttribute(
                    typeof(Controller).Assembly, typeof(System.Reflection.AssemblyInformationalVersionAttribute));
                return a == null ? "" : a.InformationalVersion;
            }
        }

        private void UpdateTrayText(Snapshot s)
        {
            var parts = s.Providers.Where(p => p.Models == null).Select(p =>
            {
                var w = Ui.Primary(p);
                return w == null ? p.Name + " –" : p.Name + " " + Math.Round(p.Usage.Room(w)) + "%";
            });
            string text = "AI Meter · " + string.Join(" · ", parts);
            tray.Text = text.Length > 63 ? text.Substring(0, 63) : text;
        }

        public void ShowMenu(Window anchor)
        {
            var menu = new ContextMenu();
            menu.Items.Add(Item(L.T("Open panel"), "\uE8A7", () => ToggleFlyout()));
            if (!Program.Demo) menu.Items.Add(Item(L.T("Usage history"), "\uE9D9", OpenHistory));
            menu.Items.Add(new Separator());
            menu.Items.Add(Item(L.T("Desktop widget"), "\uE7F4", () => { Settings.DesktopWidget = !Settings.DesktopWidget; Settings.Save(); ApplySettings(); }, Settings.DesktopWidget));
            menu.Items.Add(Item(L.T("In the taskbar"), "\uE7C4", () => SetTaskbarDock(!Settings.TaskbarDock), Settings.TaskbarDock));
            menu.Items.Add(Item(L.T("Compact overlay"), "\uE718", () =>
            {
                Settings.Overlay = !Settings.Overlay;
                if (Settings.Overlay) Settings.TaskbarDock = false;
                Settings.Save(); ApplySettings();
            }, Settings.Overlay));
            if (anchor == desktop && desktop != null)
            {
                var ids = Last == null ? new string[0] : Last.Providers.Select(p => p.Id).ToArray();
                bool all = ids.Length > 0 && ids.All(IsExpanded);
                menu.Items.Add(Item(L.T("Expand sessions"), "\uE8C4", () =>
                {
                    foreach (var id in ids) SetExpanded(id, !all);
                    Settings.Save(); Rerender();
                }, all));
            }
            menu.Items.Add(new Separator());
            menu.Items.Add(Item(L.T("Refresh now"), "\uE72C", RefreshNow));
            menu.Items.Add(Item(L.T("Settings"), "\uE713", OpenSettings));
            menu.Items.Add(new Separator());
            menu.Items.Add(Item(L.T("Exit"), "\uE7E8", Exit));

            if (anchor != null)
            {
                menu.PlacementTarget = anchor;
                menu.Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint;
                menu.IsOpen = true;
                return;
            }

            // Tray menus need a foreground owner to dismiss when the user clicks elsewhere.
            if (menuHost == null)
            {
                menuHost = new Window { Width = 0, Height = 0, WindowStyle = WindowStyle.None, ShowInTaskbar = false, Topmost = true, AllowsTransparency = true, Background = Brushes.Transparent, ResizeMode = ResizeMode.NoResize };
                menuHost.SourceInitialized += (o, e) => Native.AddExStyle(new WindowInteropHelper(menuHost).Handle, Native.WS_EX_TOOLWINDOW, 0);
            }
            // Place the zero-width owner under the pointer so the menu uses that monitor's DPI.
            // An off-screen owner may select a different monitor's scale.
            var pos = Forms.Cursor.Position;
            var h = new WindowInteropHelper(menuHost).EnsureHandle();
            Native.SetWindowPos(h, IntPtr.Zero, pos.X, pos.Y, 0, 0, Native.SWP_NOSIZE | Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
            menuHost.Show();
            menuHost.Activate();
            Native.SetForegroundWindow(h);
            menu.PlacementTarget = menuHost;
            menu.Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint;
            menu.Closed += (o, e) => menuHost.Hide();
            menu.IsOpen = true;
        }

        private static MenuItem Item(string header, string glyph, Action click, bool? check = null)
        {
            var mi = new MenuItem { Header = header, Tag = glyph };
            if (check.HasValue) { mi.IsCheckable = true; mi.IsChecked = check.Value; }
            mi.Click += (o, e) => click();
            return mi;
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

        public static ImageSource AppIconImage()
        {
            using (var s = Program.OpenAsset("app.ico"))
            {
                var dec = new IconBitmapDecoder(s, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
                return dec.Frames.OrderByDescending(f => f.PixelWidth).First();
            }
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
