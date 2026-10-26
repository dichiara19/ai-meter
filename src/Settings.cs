using System;
using System.IO;
using System.Text;
using Microsoft.Win32;

namespace AIMeter
{
    /// <summary>Preferenze utente, salvate in %APPDATA%\AIMeter\settings.json.</summary>
    public sealed class Settings
    {
        // servizi collegati
        public bool ClaudeEnabled { get; set; }
        public bool ClaudeLiveUsage { get; set; }
        public bool ClaudeShowChat { get; set; }
        public bool ClaudeShowCode { get; set; }
        public bool ClaudeShowCowork { get; set; }
        public bool OpenAIEnabled { get; set; }
        public bool OpenAIShowChat { get; set; }
        public bool CopilotEnabled { get; set; }
        /// <summary>Servizi descritti dai file di definizione: id → attivo. Assente = attivo se trovato sul PC.</summary>
        public System.Collections.Generic.Dictionary<string, bool> Services { get; set; }
        public bool LocalEnabled { get; set; }      // modelli locali (Ollama, LM Studio, llama.cpp…)
        public bool LocalAgents { get; set; }       // sessioni delle CLI (OpenCode, Pi…)
        public string WslDistro { get; set; }
        public string WslUser { get; set; }

        // widget sul desktop
        public bool DesktopWidget { get; set; }
        public double DesktopX { get; set; }
        public double DesktopY { get; set; }
        public bool ExpandedClaude { get; set; }
        public bool ExpandedOpenAI { get; set; }
        public bool ExpandedCopilot { get; set; }
        public bool ExpandedLocal { get; set; }

        public System.Collections.Generic.Dictionary<string, bool> ExpandedServices { get; set; }

        // sovrimpressione
        public bool Overlay { get; set; }
        public string OverlayCorner { get; set; }   // "tr", "tl", "br", "bl"
        public string OverlayMetric { get; set; }   // "5h", "week", "both"
        public bool OverlayHideFullscreen { get; set; }
        public bool OverlayCollapsed { get; set; }

        // pillola nella barra delle applicazioni
        public bool TaskbarDock { get; set; }
        public string TaskbarSide { get; set; }     // "auto", "left", "right"

        // notifiche
        public bool NotifyLow { get; set; }         // limite sotto il 20% e il 5%
        public bool NotifyReset { get; set; }       // limite di nuovo disponibile

        // generale
        public string Theme { get; set; }           // "system", "light", "dark"
        public string Language { get; set; }        // "system" o un codice di L.Languages
        public string Refresh { get; set; }         // "auto", "low"
        public bool FirstRunDone { get; set; }

        public Settings()
        {
            ClaudeEnabled = ClaudeProvider.Detected(); ClaudeLiveUsage = true; ClaudeShowChat = true; ClaudeShowCode = true; ClaudeShowCowork = true;
            OpenAIEnabled = CodexProvider.Detected(); OpenAIShowChat = true; CopilotEnabled = false; LocalEnabled = true; LocalAgents = true; ExpandedLocal = true;
            WslDistro = DefaultWslDistro(); WslUser = Environment.UserName;
            Services = new System.Collections.Generic.Dictionary<string, bool>(); ExpandedServices = new System.Collections.Generic.Dictionary<string, bool>();
            DesktopWidget = false; DesktopX = double.MinValue; DesktopY = double.MinValue;   // la pillola nella barra è il modo principale; il widget si attiva nelle impostazioni
            TaskbarDock = true; TaskbarSide = "auto";
            Overlay = false; OverlayCorner = "tr"; OverlayMetric = "5h"; OverlayHideFullscreen = true;
            Theme = "system"; Language = "system"; Refresh = "auto"; NotifyLow = true; NotifyReset = true;
        }

        internal bool ServiceOn(ServiceDefinition d)
        {
            bool on;
            if (Services.TryGetValue(d.Id, out on)) return on;
            return d.Detected();
        }

        public static string Folder
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AIMeter"); }
        }

        private static string FilePath { get { return Path.Combine(Folder, "settings.json"); } }

        public static Settings Load()
        {
            try
            {
                if (File.Exists(FilePath))
                {
                    var s = Json.Deserialize<Settings>(File.ReadAllText(FilePath, Encoding.UTF8));
                    if (s != null)
                    {
                        if (string.IsNullOrEmpty(s.WslDistro)) s.WslDistro = DefaultWslDistro();
                        if (string.IsNullOrEmpty(s.WslUser)) s.WslUser = Environment.UserName;
                        if (s.Services == null) s.Services = new System.Collections.Generic.Dictionary<string, bool>();
                        if (s.ExpandedServices == null) s.ExpandedServices = new System.Collections.Generic.Dictionary<string, bool>();
                        return s;
                    }
                }
            }
            catch (Exception) { }
            return new Settings();
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(Folder);
                string tmp = FilePath + ".tmp";
                File.WriteAllText(tmp, Json.Serialize(this), new UTF8Encoding(false));
                if (File.Exists(FilePath)) File.Replace(tmp, FilePath, null);
                else File.Move(tmp, FilePath);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        private static string DefaultWslDistro()
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Lxss"))
                {
                    if (k == null) return "";
                    var def = k.GetValue("DefaultDistribution") as string;
                    if (def == null) return "";
                    using (var d = k.OpenSubKey(def)) return d == null ? "" : (d.GetValue("DistributionName") as string ?? "");
                }
            }
            catch (Exception) { return ""; }
        }

        // --- avvio automatico: collegamento "AI Meter" in Esecuzione automatica -------------
        // Windows lo elenca con il nome e l'icona dell'app (Impostazioni > App > Avvio) e lo apre all'accesso.
        // Prima era un valore nella chiave Run: con conhost.exe come programma Windows lo mostrava come
        // "Host finestra console" e all'accesso poteva saltarlo. Il valore vecchio si sposta da solo.
        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string ApprovedKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\StartupFolder";

        private static string StartupLink
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Startup), "AI Meter.lnk"); }
        }

        public static bool StartsWithWindows
        {
            get
            {
#if MSIX
                if (Native.IsPackaged) return PackageStartup.Enabled;
#endif
                if (File.Exists(StartupLink))
                {
                    // spento da Impostazioni > App > Avvio o da Gestione attività: primo byte dispari
                    using (var k = Registry.CurrentUser.OpenSubKey(ApprovedKey))
                    {
                        var v = k == null ? null : k.GetValue("AI Meter.lnk") as byte[];
                        return v == null || v.Length == 0 || (v[0] & 1) == 0;
                    }
                }
                using (var k = Registry.CurrentUser.OpenSubKey(RunKey))
                    return k != null && k.GetValue("AI Meter") != null;
            }
            set
            {
#if MSIX
                if (Native.IsPackaged) { PackageStartup.Enabled = value; return; }
#endif
                using (var k = Registry.CurrentUser.OpenSubKey(RunKey, true))
                    if (k != null) k.DeleteValue("AI Meter", false);
                if (value)
                {
                    WriteStartupLink();
                    // riacceso dall'app: toglie lo stato "disattivato" lasciato da Windows
                    using (var k = Registry.CurrentUser.OpenSubKey(ApprovedKey, true))
                        if (k != null) k.DeleteValue("AI Meter.lnk", false);
                }
                else if (File.Exists(StartupLink)) File.Delete(StartupLink);
            }
        }

        /// <summary>A ogni avvio: sposta la vecchia voce Run nel collegamento e lo allinea all'installazione attuale.</summary>
        public static void RefreshStartup()
        {
            if (Native.IsPackaged) return;
            bool legacy;
            using (var k = Registry.CurrentUser.OpenSubKey(RunKey)) legacy = k != null && k.GetValue("AI Meter") != null;
            if (legacy) StartsWithWindows = true;
            else if (File.Exists(StartupLink)) WriteStartupLink();
        }

        private static void WriteStartupLink()
        {
            string target, args;
            Program.SplitLaunch(out target, out args);
            string icon = Program.IconPath;
            Native.SaveShortcut(StartupLink, target, (args + " --startup").Trim(), Program.Home, icon.Substring(0, icon.LastIndexOf(',')), 0);
        }
    }
}
