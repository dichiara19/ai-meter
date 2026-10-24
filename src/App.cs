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
            w.SourceInitialized += (o, e) =>
                Native.SetWindowAppId(new System.Windows.Interop.WindowInteropHelper(w).Handle, "AIMeter.App", IconPath, "AI Meter", LaunchCommand);
        }

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

                ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12 | (SecurityProtocolType)12288;
                // Disegno in software: nessun dispositivo Direct3D (circa -60 MB) e nessun lavoro sulla GPU.
                // La composizione (Mica, Acrilico, ombre) resta a carico di Windows. --gpu per il disegno in hardware.
                if (!args.Contains("--gpu")) System.Windows.Media.RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
                var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                var controller = new Controller(args.Contains("--startup"), showSignal, settingsSignal);
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
        private Dispatcher ui;
        private DesktopWidget desktop;
        private OverlayPill pill;
        private TaskbarPill dock;
        private Flyout flyout;
        private SettingsWindow settingsWindow;
        private Window menuHost;
        private Forms.NotifyIcon tray;
        private string lastSignature;
        private DateTime flyoutHiddenAt;
        private readonly DispatcherTimer hideDetails = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
        private DispatcherTimer loginWatch;
        private Native.WinEventProc foregroundProc;   // tenuto in vita: il hook lo richiama
        private bool fullscreenBusy;

        private readonly AutoResetEvent wake = new AutoResetEvent(false);
        private volatile bool paused;
        private volatile bool anyVisible = true;
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
            bool firstRun = !Settings.FirstRunDone;
            if (firstRun)
            {
                Settings.FirstRunDone = true;
                Settings.Save();
                try { Settings.StartsWithWindows = true; } catch (Exception) { }
            }
            L.Init(Settings.Language);
            collector = new Collector(Settings);
            Theme.Apply(Settings);

            CreateFlyout();
            hideDetails.Tick += (o, e) => { hideDetails.Stop(); if (flyout.HoverMode && !flyout.IsMouseOver && (pill == null || !pill.IsMouseOver)) flyout.Hide(); };

            CreateTray();
            ApplySettings();
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
                try { snap = collector.Collect(anyVisible || ticks == 0, saving); }
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
                else interval = saving ? 30 : 10;
                CurrentInterval = interval;
                SavingPower = saving;
                wake.WaitOne(interval * 1000);
            }
        }

        private void Apply(Snapshot s)
        {
            Last = s;
            string sig = s.Signature() + Settings.ExpandedClaude + Settings.ExpandedOpenAI + Settings.ExpandedCopilot + Settings.ExpandedCursor + Theme.Dark + Settings.OverlayMetric + Settings.OverlayCollapsed;
            UpdateTrayText(s);
            if (sig == lastSignature) return;   // niente è cambiato: nessun ridisegno
            lastSignature = sig;
            if (desktop != null) desktop.Render(s);
            if (pill != null) pill.Render(s, Settings.OverlayMetric);
            if (dock != null) dock.Render(s, Settings.OverlayMetric);
            if (flyout.IsVisible) flyout.Render(s);
        }

        private void Rerender() { lastSignature = null; if (Last != null) Apply(Last); }

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
            return id == "claude" ? Settings.ExpandedClaude : id == "copilot" ? Settings.ExpandedCopilot : id == "cursor" ? Settings.ExpandedCursor : Settings.ExpandedOpenAI;
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
            else if (id == "cursor") Settings.ExpandedCursor = v;
            else Settings.ExpandedOpenAI = v;
        }

        /// <summary>Accesso del servizio indicato: login di Claude Code nel terminale, o accesso a GitHub per Copilot.</summary>
        public void Login(string id)
        {
            if (id == "copilot") LoginGitHub();
            else LoginClaude();
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

        public void SignOutGitHub()
        {
            GitHubAuth.SignOut();
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
        }

        /// <summary>
        /// Al primo avvio dice chiaramente quale dato esce dal PC e come spegnerlo
        /// (unico accesso alla rete: i limiti Claude in tempo reale).
        /// </summary>
        private void ShowPrivacyNotice()
        {
            tray.BalloonTipClicked += (o, e) => OpenSettings();
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
            var parts = s.Providers.Select(p =>
            {
                var w = Ui.Primary(p.Usage);
                return w == null ? p.Name + " –" : p.Name + " " + Math.Round(w.Remaining) + "%";
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
            var pos = Forms.Cursor.Position;
            menuHost.Left = -10000; menuHost.Top = -10000;
            menuHost.Show();
            menuHost.Activate();
            Native.SetForegroundWindow(new WindowInteropHelper(menuHost).Handle);
            var src = PresentationSource.FromVisual(menuHost);
            var pt = src != null ? src.CompositionTarget.TransformFromDevice.Transform(new Point(pos.X, pos.Y)) : new Point(pos.X, pos.Y);
            menu.PlacementTarget = menuHost;
            menu.Placement = System.Windows.Controls.Primitives.PlacementMode.AbsolutePoint;
            menu.HorizontalOffset = pt.X;
            menu.VerticalOffset = pt.Y;
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
