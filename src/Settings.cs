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
            DesktopWidget = true; DesktopX = double.MinValue; DesktopY = double.MinValue;
            TaskbarDock = false; TaskbarSide = "auto";
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

        // --- avvio automatico (chiave Run dell'utente, come le app di Windows) -------------
        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

        public static bool StartsWithWindows
        {
            get
            {
#if MSIX
                if (Native.IsPackaged) return PackageStartup.Enabled;
#endif
                using (var k = Registry.CurrentUser.OpenSubKey(RunKey))
                    return k != null && k.GetValue("AI Meter") != null;
            }
            set
            {
#if MSIX
                if (Native.IsPackaged) { PackageStartup.Enabled = value; return; }
#endif
                using (var k = Registry.CurrentUser.CreateSubKey(RunKey))
                {
                    if (value) k.SetValue("AI Meter", Program.LaunchCommand + " --startup");
                    else k.DeleteValue("AI Meter", false);
                }
            }
        }
    }
}
