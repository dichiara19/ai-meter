using System;
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
    /// Punto d'ingresso pubblico per l'avvio da PowerShell (AIMeter.ps1): con Smart App Control attivo un eseguibile
    /// non firmato viene bloccato, mentre PowerShell (firmato da Microsoft) può compilare ed eseguire il sorgente.
    /// </summary>
    public static class Entry
    {
        public static void Run(string[] args)
        {
            // senza il manifest dell'eseguibile: DPI per monitor impostati qui, prima che WPF si inizializzi
            Native.EnablePerMonitorDpi();
            Native.SetAppId("AIMeter.App");
            AppContext.SetSwitch("Switch.System.Windows.DoNotScaleForDpiChanges", false);
            Program.Run(args);
        }
    }

    internal static class Program
    {
        /// <summary>Cartella dell'installazione: quella dell'eseguibile, o quella indicata dal launcher PowerShell.</summary>
        public static string Home
        {
            get { return Environment.GetEnvironmentVariable("AIMETER_HOME") ?? AppDomain.CurrentDomain.BaseDirectory; }
        }

        /// <summary>Comando con cui Windows deve avviare l'app (avvio automatico).</summary>
        public static string LaunchCommand
        {
            get
            {
                return Environment.GetEnvironmentVariable("AIMETER_LAUNCH")
                       ?? "\"" + System.Reflection.Assembly.GetExecutingAssembly().Location + "\"";
            }
        }

        /// <summary>LaunchCommand diviso in programma (percorso completo) e argomenti, per i collegamenti.</summary>
        public static void SplitLaunch(out string target, out string args)
        {
            string cmd = LaunchCommand.Trim();
            if (cmd.StartsWith("\"")) { int q = cmd.IndexOf('"', 1); target = cmd.Substring(1, q - 1); args = cmd.Substring(q + 1).Trim(); }
            else { int sp = cmd.IndexOf(' '); target = sp < 0 ? cmd : cmd.Substring(0, sp); args = sp < 0 ? "" : cmd.Substring(sp + 1); }
            if (!Path.IsPathRooted(target)) target = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), target);
        }

        /// <summary>Risorsa incorporata nell'eseguibile, oppure il file corrispondente in assets\ (avvio da sorgente).</summary>
        /// <summary>Icona dell'app come "percorso,indice": l'eseguibile, o assets\app.ico nell'avvio da sorgente.</summary>
        public static string IconPath
        {
            get
            {
                string ico = Path.Combine(Home, @"assets\app.ico");
                if (File.Exists(ico)) return ico + ",0";
                return System.Reflection.Assembly.GetExecutingAssembly().Location + ",0";
            }
        }

        /// <summary>Le finestre con un pulsante nella barra si presentano come AI Meter, con la sua icona.</summary>
        public static void Brand(Window w)
        {
            if (Native.IsPackaged) return;   // il pacchetto MSIX dà già nome e icona alle finestre
            w.SourceInitialized += (o, e) =>
                Native.SetWindowAppId(new System.Windows.Interop.WindowInteropHelper(w).Handle, "AIMeter.App", IconPath, "AI Meter", LaunchCommand);
        }

        /// <summary>--demo: dati dimostrativi al posto di quelli veri (per provare l'aspetto o fare screenshot), niente rete né notifiche.</summary>
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

        internal static void Run(string[] args)
        {
            bool created;
            using (var mutex = new Mutex(true, @"Local\AIMeter.Instance", out created))
            {
                var showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\AIMeter.Show");
                var settingsSignal = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\AIMeter.Settings");
                if (!created)
                {
                    // già in esecuzione: apre il pannello (o le impostazioni) dell'istanza attiva
                    (args.Contains("--settings") ? settingsSignal : showSignal).Set();
                    return;
                }

                Demo = args.Contains("--demo");
                ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12 | (SecurityProtocolType)12288;
                // Disegno in software: nessun dispositivo Direct3D (circa -60 MB) e nessun lavoro sulla GPU.
                // La composizione (Mica, Acrilico, ombre) resta a carico di Windows. --gpu per il disegno in hardware.
                if (!args.Contains("--gpu")) System.Windows.Media.RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
                var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
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
        private Action balloonAction;   // cosa fa il clic sulla notifica mostrata
        private Dispatcher ui;
        private DesktopWidget desktop;
        private OverlayPill pill;
        private TaskbarPill dock;
        private Flyout flyout;
        private SettingsWindow settingsWindow;
        private Window menuHost;
        private Forms.NotifyIcon tray;
        private string lastSignature;
        private string limitKeys;
        private DateTime flyoutHiddenAt;
        private readonly DispatcherTimer hideDetails = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
        private DispatcherTimer loginWatch;
        private Native.WinEventProc foregroundProc;   // tenuto in vita: il hook lo richiama
        private bool fullscreenBusy;

        private readonly AutoResetEvent wake = new AutoResetEvent(false);
        private volatile bool paused;
        private volatile bool anyVisible = true;
        private volatile bool detailsVisible;   // pannello o impostazioni aperti: dati più freschi
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
                try { Settings.StartsWithWindows = true; } catch (Exception) { }
            }
            else try { Settings.RefreshStartup(); } catch (Exception) { }
            L.Init(Settings.Language);
            collector = new Collector(Settings);
            Theme.Apply(Settings);

            CreateFlyout();
            hideDetails.Tick += (o, e) => { hideDetails.Stop(); if (flyout.HoverMode && !flyout.IsMouseOver && (pill == null || !pill.IsMouseOver)) flyout.Hide(); };

            CreateTray();
            Native.RegisterAppIdentity("AIMeter.App", "AI Meter", NotificationIcon());
            EnsureShortcut();
            ApplySettings();
            // primo avvio con una barra dove la pillola non può stare (es. verticale): si mostra il widget sul desktop
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

        // ================================================================ raccolta dati in background
        private void WorkerLoop()
        {
            Native.EnableEcoQoSForCurrentThread();
            if (startup) Thread.Sleep(15000);   // all'accesso lascia prima partire il desktop
            int ticks = 0;
            while (!stopping)
            {
                if (paused) { wake.WaitOne(); continue; }
                bool saving = Native.SavingPower();
                Snapshot snap = null;
                try
                {
                    if (Program.Demo) snap = DemoData.Build(DateTime.Now);
                    else { snap = collector.Collect(anyVisible || ticks == 0, saving); forecaster.Apply(snap); }
                }
                catch (Exception ex) { Trace.WriteLine(ex); }
                if (snap != null) ui.BeginInvoke(DispatcherPriority.Background, new Action(() => Apply(snap)));
                if (Json.BytesRead > 8 << 20)
                {
                    // dopo letture grosse (primo avvio, log nuovi) restituisce la memoria al sistema
                    Json.BytesRead = 0;
                    System.Runtime.GCSettings.LargeObjectHeapCompactionMode = System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
                    GC.Collect(2, GCCollectionMode.Forced, true, true);
                }
                if (++ticks % 60 == 0) collector.Trim();

                int interval;
                if (!anyVisible) interval = 120;
                else if (Settings.Refresh == "low") interval = saving ? 60 : 30;
                // pannello aperto: ogni 10 s; solo pillola, widget o sovrimpressione: ogni 30 s (bastano per percentuali
                // che cambiano lentamente, e un terzo dei risvegli). A batteria il doppio.
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
            // le impostazioni elencano i limiti dei servizi: se cambiano (primo aggiornamento, servizio acceso) si ridisegnano
            string keys = string.Join(",", s.Providers.Where(p => p.Usage != null).SelectMany(p => p.Usage.Windows.Where(w => w.HasPercent).Select(w => p.Id + "|" + w.Key)));
            if (keys != limitKeys)
            {
                limitKeys = keys;
                if (settingsWindow != null && settingsWindow.IsVisible) settingsWindow.Build();
            }
            string sig = s.Signature() + Settings.ExpandedClaude + Settings.ExpandedOpenAI + Settings.ExpandedCopilot + Settings.ExpandedLocal + string.Join(",", Settings.ExpandedServices.Where(kv => kv.Value).Select(kv => kv.Key)) + Theme.Dark + Settings.OverlayMetric + Settings.OverlayCollapsed + string.Join(",", Settings.PillLimits.Select(kv => kv.Key + kv.Value)) + string.Join(",", Settings.HiddenRows.Where(kv => kv.Value).Select(kv => kv.Key));
            UpdateTrayText(s);
            if (sig == lastSignature) return;   // niente è cambiato: nessun ridisegno
            lastSignature = sig;
            if (desktop != null) desktop.Render(s);
            if (pill != null) pill.Render(s, Settings.OverlayMetric);
            if (dock != null) dock.Render(s, Settings.OverlayMetric);
            if (flyout.IsVisible) flyout.Render(s);
        }

        private void Rerender() { lastSignature = null; if (Last != null) Apply(Last); }

        /// <summary>Ricalcola subito i suggerimenti (dopo un cambio nelle impostazioni), senza aspettare la lettura successiva.</summary>
        public void RefreshTips()
        {
            if (Last == null) return;
            foreach (var p in Last.Providers) p.Tip = Alerts.TipFor(p, Settings, DateTime.Now);
            Rerender();
        }

        public void RefreshNow() { collector.InvalidateNetwork(); wake.Set(); }

        // ================================================================ impostazioni
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
            foreach (Window w in new Window[] { desktop, pill, flyout, settingsWindow })
                if (w != null) Ui.ApplyBackdrop(w, w == flyout ? 3 : 2, false);
            if (settingsWindow != null) settingsWindow.Build();
            Rerender();
        }

        private void CreateFlyout()
        {
            flyout = new Flyout(this);
            flyout.IsVisibleChanged += (o, e) => { if (!flyout.IsVisible) flyoutHiddenAt = DateTime.Now; UpdateVisibility(); };
        }

        /// <summary>Cambio lingua al volo: ricrea finestre e menu con i nuovi testi e rilegge i limiti.</summary>
        public void ApplyLanguage()
        {
            L.Init(Settings.Language);
            if (desktop != null) { desktop.Close(); desktop = null; }
            if (pill != null) { pill.Close(); pill = null; }
            if (dock != null) { dock.Close(); dock = null; }
            flyout.Close();
            CreateFlyout();
            ApplySettings();   // etichette, note e dettagli si traducono al momento di mostrarli: basta ridisegnare
            if (settingsWindow != null) settingsWindow.Build();
            wake.Set();        // gli avvisi dei servizi vengono riscritti alla prossima raccolta, subito
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

        /// <summary>Accesso del servizio indicato: login di Claude Code nel terminale, o accesso a GitHub per Copilot.</summary>
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

        /// <summary>Chiave API di un servizio descritto da una definizione: finestra per incollarla.</summary>
        public void EnterApiKey(ServiceDefinition d) { EnterApiKey(d, null); }

        /// <summary>slot: una chiave aggiuntiva della definizione (es. "management" per OpenRouter); null = quella principale.</summary>
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

        /// <summary>Accesso di Antigravity CLI: la CLI si apre in un terminale (su Windows se c'è, altrimenti in WSL) e lo chiede lei.</summary>
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

        /// <summary>Apre un terminale con il login di Claude Code: l'accesso lo fa l'utente, poi resta valido.</summary>
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

            // controlla a intervalli brevi finché il login non risulta completato (al massimo 5 minuti)
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

        /// <summary>Il login va dove vive la CLI di Claude Code: WSL se configurato (l'avvio qui è voluto dall'utente), altrimenti Windows.</summary>
        private bool UseWslForLogin()
        {
            if (string.IsNullOrEmpty(Settings.WslDistro)) return false;
            if (!collector.WslUp) return true;
            return Directory.Exists(@"\\wsl.localhost\" + Settings.WslDistro + @"\home\" + Settings.WslUser + @"\.claude");
        }

        // ================================================================ pannello e pillola
        public void ToggleFlyout()
        {
            if (flyout.IsVisible && !flyout.HoverMode) { flyout.Hide(); return; }
            if ((DateTime.Now - flyoutHiddenAt).TotalMilliseconds < 300) return; // chiuso proprio da questo clic
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
                         || settingsWindow != null;
            bool details = flyout.IsVisible || settingsWindow != null;
            if (details && !detailsVisible) wake.Set();   // all'apertura del pannello si aggiorna subito
            detailsVisible = details;
        }

        private void OnForegroundChanged(IntPtr hook, uint ev, IntPtr hwnd, int obj, int child, uint thread, uint time)
        {

            if (flyout.IsVisible && flyout.TaskbarMode && !Native.IsOwnWindow(hwnd)) flyout.Hide();   // clic altrove
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
                // la pillola è dentro la barra: a schermo intero o con la barra a scomparsa segue la barra da sola
                dock.Place();
            }
            UpdateVisibility();
        }

        public void ToggleFlyoutFromTaskbar(TaskbarPill anchor)
        {
            if (flyout.IsVisible && !flyout.HoverMode) { flyout.Hide(); return; }
            if ((DateTime.Now - flyoutHiddenAt).TotalMilliseconds < 300) return; // chiuso proprio da questo clic
            if (Last != null) flyout.Render(Last);
            flyout.ShowFromTaskbar(anchor.OnLeft);
            UpdateVisibility();
            RefreshNow();
        }

        /// <summary>Pillola nella barra e sovrimpressione sono alternative: attivarne una spegne l'altra.</summary>
        public void SetTaskbarDock(bool on)
        {
            Settings.TaskbarDock = on;
            if (on) Settings.Overlay = false;
            Settings.Save();
            ApplySettings();
        }

        // ================================================================ area di notifica e menu
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

        /// <summary>
        /// Al primo avvio dice chiaramente quale dato esce dal PC e come spegnerlo
        /// (unico accesso alla rete: i limiti Claude in tempo reale).
        /// </summary>
        /// <summary>Notifiche dei limiti (una alla volta: Windows mostra comunque solo l'ultima). Il clic apre il pannello.</summary>
        private void Notify(Snapshot s)
        {
            if (Program.Demo) { foreach (var p in s.Providers) p.Tip = Alerts.TipFor(p, Settings, DateTime.Now); return; }
            if (alerts == null) alerts = new Alerts();
            var list = alerts.Check(s, Settings);
            if (list.Count == 0) return;
            var n = list[list.Count - 1];
            string text = list.Count > 1 ? n.Item2 + "\n" + L.N(list.Count - 1, "+{0} other notice", "+{0} other notices") : n.Item2;
            balloonAction = () => { if (!flyout.IsVisible) ToggleFlyout(); };
            tray.ShowBalloonTip(15000, n.Item1, text, Forms.ToolTipIcon.None);
        }

        /// <summary>Collegamento "AI Meter" nel menu Start, con l'identità dell'app: senza, le notifiche non hanno nome né icona.</summary>
        private static void EnsureShortcut()
        {
            string lnk = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "AI Meter.lnk");
            string target, args;
            Program.SplitLaunch(out target, out args);
            string icon = Program.IconPath;
            int comma = icon.LastIndexOf(',');
            Native.EnsureStartShortcut(lnk, target, args, Program.Home, icon.Substring(0, comma), 0, "AIMeter.App");
        }

        /// <summary>L'icona dell'app come file, per le notifiche: quella in assets\ o una copia di quella incorporata.</summary>
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

            // dall'area di notifica: serve una finestra in primo piano perché il menu si chiuda cliccando altrove
            if (menuHost == null)
            {
                menuHost = new Window { Width = 0, Height = 0, WindowStyle = WindowStyle.None, ShowInTaskbar = false, Topmost = true, AllowsTransparency = true, Background = Brushes.Transparent, ResizeMode = ResizeMode.NoResize };
                menuHost.SourceInitialized += (o, e) => Native.AddExStyle(new WindowInteropHelper(menuHost).Handle, Native.WS_EX_TOOLWINDOW, 0);
            }
            // la finestra (larga zero) va sotto il cursore, non fuori schermo: il menu prende la scala del monitor
            // su cui si apre. Fuori schermo valeva quella del monitor più vicino, magari un altro a scala diversa.
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

        // ================================================================ eventi di sistema
        private void OnSessionSwitch(object sender, SessionSwitchEventArgs e)
        {
            if (e.Reason == SessionSwitchReason.SessionLock || e.Reason == SessionSwitchReason.ConsoleDisconnect || e.Reason == SessionSwitchReason.RemoteDisconnect)
                paused = true;   // PC bloccato: nessun lavoro finché non torni
            else if (e.Reason == SessionSwitchReason.SessionUnlock || e.Reason == SessionSwitchReason.ConsoleConnect || e.Reason == SessionSwitchReason.RemoteConnect)
            { paused = false; wake.Set(); }
        }

        private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
        {
            if (e.Mode == PowerModes.Suspend) paused = true;
            else if (e.Mode == PowerModes.Resume) { paused = false; collector.InvalidateNetwork(); wake.Set(); }
            else wake.Set();   // cambio alimentazione: ricalcola l'intervallo
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
