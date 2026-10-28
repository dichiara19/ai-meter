using System;
using System.IO;
using System.Text;
using Microsoft.Win32;

namespace AIMeter
{
    /// <summary>User preferences stored in %APPDATA%\AIMeter\settings.json.</summary>
    public sealed class Settings
    {
        // Connected services.
        public bool ClaudeEnabled { get; set; }
        public bool ClaudeLiveUsage { get; set; }
        public bool ClaudeShowChat { get; set; }
        public bool ClaudeShowCode { get; set; }
        public bool ClaudeShowCowork { get; set; }
        public bool OpenAIEnabled { get; set; }
        public bool OpenAIShowChat { get; set; }
        public bool CopilotEnabled { get; set; }
        public bool AntigravityEnabled { get; set; } // Google Antigravity quotas using the CLI's authentication.
        /// <summary>Definition-based services: id to enabled; missing entries follow local detection.</summary>
        public System.Collections.Generic.Dictionary<string, bool> Services { get; set; }
        public bool FindKeys { get; set; }          // Discover existing API keys in local apps on Windows and WSL.
        public bool LocalEnabled { get; set; }      // Local models, including Ollama, LM Studio and llama.cpp.
        public bool LocalAgents { get; set; }       // CLI sessions, including OpenCode and Pi.
        public string WslDistro { get; set; }
        public string WslUser { get; set; }

        // Desktop widget.
        public bool DesktopWidget { get; set; }
        public double DesktopX { get; set; }
        public double DesktopY { get; set; }
        public bool ExpandedClaude { get; set; }
        public bool ExpandedOpenAI { get; set; }
        public bool ExpandedCopilot { get; set; }
        public bool ExpandedLocal { get; set; }

        public System.Collections.Generic.Dictionary<string, bool> ExpandedServices { get; set; }

        // Overlay.
        public bool Overlay { get; set; }
        public string OverlayCorner { get; set; }   // "tr", "tl", "br", "bl"
        public string OverlayMetric { get; set; }   // "5h", "week", "both"
        public bool OverlayHideFullscreen { get; set; }
        public bool OverlayCollapsed { get; set; }

        // Taskbar pill.
        public bool TaskbarDock { get; set; }
        public string TaskbarSide { get; set; }     // "auto", "left", "right"

        // Notifications.
        public bool NotifyLow { get; set; }         // Notify below 20% and 5% remaining.
        public bool NotifyReset { get; set; }       // Notify when a limit becomes available again.
        /// <summary>Reset notifications keyed by provider and limit identity.</summary>
        public System.Collections.Generic.Dictionary<string, bool> NotifyResets { get; set; }
        public bool TipsUnused { get; set; }        // Suggest quota that would otherwise expire unused.
        public bool TipsShort { get; set; }         // Also suggest short-session quota, which consumes enclosing long-window limits.
        public bool TipsWait { get; set; }          // Suggest waiting for a reset instead of paying for extra usage.

        /// <summary>Hidden service-card rows keyed by limit identity, *spend or *models.</summary>
        public System.Collections.Generic.Dictionary<string, bool> HiddenRows { get; set; }

        internal bool RowHidden(string providerId, string key)
        {
            bool h;
            return HiddenRows != null && HiddenRows.TryGetValue(providerId + "|" + key, out h) && h;
        }

        /// <summary>Credit-service budgets keyed by id, stored as amount|period.</summary>
        public System.Collections.Generic.Dictionary<string, string> Budgets { get; set; }

        /// <summary>Per-service pill limit key or "auto"; a missing entry uses Default.</summary>
        public System.Collections.Generic.Dictionary<string, string> PillLimits { get; set; }

        /// <summary>Current preferences for UI components that do not receive them directly.</summary>
        internal static Settings Current;

        // General.
        public string Theme { get; set; }           // "system", "light", "dark"
        public string Language { get; set; }        // "system" or a language code from L.Languages.
        public string Refresh { get; set; }         // "auto", "low"
        public bool FirstRunDone { get; set; }
        public bool HistoryEnabled { get; set; }
        public bool SessionFeedback { get; set; }
        public DateTime FeedbackEnabledSince { get; set; }

        public Settings()
        {
            ClaudeEnabled = ClaudeProvider.Detected(); ClaudeLiveUsage = true; ClaudeShowChat = true; ClaudeShowCode = true; ClaudeShowCowork = true;
            OpenAIEnabled = CodexProvider.Detected(); OpenAIShowChat = true; CopilotEnabled = false; LocalEnabled = true; LocalAgents = true; ExpandedLocal = true;
            WslDistro = DefaultWslDistro(); WslUser = Environment.UserName;
            Services = new System.Collections.Generic.Dictionary<string, bool>(); ExpandedServices = new System.Collections.Generic.Dictionary<string, bool>();
            DesktopWidget = false; DesktopX = double.MinValue; DesktopY = double.MinValue;   // The taskbar pill is the default surface; the desktop widget is optional.
            TaskbarDock = true; TaskbarSide = "auto";
            Overlay = false; OverlayCorner = "tr"; OverlayMetric = "5h"; OverlayHideFullscreen = true;
            Theme = "system"; Language = "system"; Refresh = "auto"; NotifyLow = true; NotifyReset = true;
            NotifyResets = new System.Collections.Generic.Dictionary<string, bool>(); PillLimits = new System.Collections.Generic.Dictionary<string, string>();
            Budgets = new System.Collections.Generic.Dictionary<string, string>();
            HiddenRows = new System.Collections.Generic.Dictionary<string, bool>();
            TipsUnused = true; TipsShort = false; TipsWait = true; FindKeys = true;
            HistoryEnabled = true;
        }

        /// <summary>Whether every reset of this limit should be announced.</summary>
        internal bool NotifiesReset(string providerId, UsageWindow w)
        {
            bool on;
            return NotifyResets.TryGetValue(providerId + "|" + w.Key, out on) && on;
        }

        internal bool ServiceOn(ServiceDefinition d)
        {
            bool on;
            if (Services.TryGetValue(d.Id, out on)) return on;
            return d.Detected();
        }

        public static string Folder
        {
            get
            {
#if AIMETER_TEST
                if (TestFolder == null) throw new InvalidOperationException("Tests require an isolated settings folder.");
                return TestFolder;
#else
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AIMeter");
#endif
            }
        }

#if AIMETER_TEST
        internal static string TestFolder;
#endif

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
                        if (s.NotifyResets == null) s.NotifyResets = new System.Collections.Generic.Dictionary<string, bool>();
                        if (s.PillLimits == null) s.PillLimits = new System.Collections.Generic.Dictionary<string, string>();
                        if (s.Budgets == null) s.Budgets = new System.Collections.Generic.Dictionary<string, string>();
                        if (s.HiddenRows == null) s.HiddenRows = new System.Collections.Generic.Dictionary<string, bool>();
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

        // --- automatic startup through the AI Meter shortcut ------------------------------
        // Windows lists the app's own name and icon under Apps > Startup and opens it at sign-in.
        // The legacy Run entry showed conhost.exe instead of the app and could be skipped at sign-in.
        // Migrate that entry to the startup shortcut automatically.
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
                    // An odd first byte indicates startup was disabled through Windows Settings or Task Manager.
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
                    // Re-enabling startup in the app clears Windows' disabled state.
                    using (var k = Registry.CurrentUser.OpenSubKey(ApprovedKey, true))
                        if (k != null) k.DeleteValue("AI Meter.lnk", false);
                }
                else if (File.Exists(StartupLink)) File.Delete(StartupLink);
            }
        }

        /// <summary>At launch, migrate the legacy Run entry and align the shortcut with the current installation.</summary>
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
