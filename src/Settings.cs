using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
        /// <summary>The plan's monthly Claude API credits as amount|renewal day; an empty amount follows the plan.</summary>
        public string ClaudeApiCredits { get; set; }
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

        // How every surface states a limit: "left" (default) or "used"; money limits as "amount" (default) or "percent".
        // Notifications always say what is left.
        public string LimitValues { get; set; }
        public string MoneyValues { get; set; }

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
        public bool ConsumptionAlerts { get; set; }             // Opt-in: notify consumption unusual for this user's history.
        public DateTime ConsumptionAlertsSince { get; set; }    // Only episodes starting after opting in are notified.
        public bool ConsumptionSuggested { get; set; }          // The one-time suggestion was shown or dismissed.

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
        public bool McpTitles { get; set; }         // Whether AI apps connected through MCP may read session titles.
        public bool SessionFeedback { get; set; }
        public DateTime FeedbackEnabledSince { get; set; }

        // Updates: a daily GitHub check outside the Microsoft Store, which updates its package by itself.
        public bool UpdateCheck { get; set; }
        public bool UpdatePreReleases { get; set; }     // Opt-in: also report beta and release candidate versions.
        public DateTime UpdateCheckedAt { get; set; }   // UTC time of the last automatic or explicit check.
        public string UpdateAvailable { get; set; }     // Newer version found by the last successful check, or null.
        public string UpdateNotified { get; set; }      // Version already announced with a notification.

        public Settings()
        {
            ClaudeEnabled = ClaudeProvider.Detected(); ClaudeLiveUsage = true; ClaudeShowChat = true; ClaudeShowCode = true; ClaudeShowCowork = true;
            OpenAIEnabled = CodexProvider.Detected(); OpenAIShowChat = true; CopilotEnabled = false; LocalEnabled = true; LocalAgents = true; ExpandedLocal = true;
            WslDistro = DefaultWslDistro(); WslUser = Environment.UserName;
            Services = new System.Collections.Generic.Dictionary<string, bool>(); ExpandedServices = new System.Collections.Generic.Dictionary<string, bool>();
            DesktopWidget = false; DesktopX = double.MinValue; DesktopY = double.MinValue;   // The taskbar pill is the default surface; the desktop widget is optional.
            TaskbarDock = true; TaskbarSide = "auto";
            Overlay = false; OverlayCorner = "tr"; OverlayMetric = "5h"; OverlayHideFullscreen = true;
            LimitValues = "left"; MoneyValues = "amount";
            Theme = "system"; Language = "system"; Refresh = "auto"; NotifyLow = true; NotifyReset = true;
            NotifyResets = new System.Collections.Generic.Dictionary<string, bool>(); PillLimits = new System.Collections.Generic.Dictionary<string, string>();
            Budgets = new System.Collections.Generic.Dictionary<string, string>();
            HiddenRows = new System.Collections.Generic.Dictionary<string, bool>();
            TipsUnused = true; TipsShort = false; TipsWait = true; FindKeys = true;
            HistoryEnabled = true;
            UpdateCheck = true;
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
            return !d.CatalogueOnly && d.Detected();   // A model list alone is not worth a service the user did not ask for.
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

        /// <summary>
        /// The data folder as File Explorer can show it. In the Store build, files that AI Meter creates in AppData land
        /// in the package's private copy of AppData (write virtualisation): AI Meter sees them in Folder, Explorer only
        /// there. Files that existed before, from another build of AI Meter, stay in Folder and are changed in place.
        /// </summary>
        public static string ExplorerFolder
        {
            get
            {
                string family = Native.IsPackaged ? Native.PackageFamilyName : null;
                if (family == null) return Folder;
                string redirected = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Packages", family, @"LocalCache\Roaming\AIMeter");
                return Directory.Exists(redirected) ? redirected : Folder;
            }
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
                        if (string.IsNullOrEmpty(s.WslDistro) || IsDockerDistro(s.WslDistro)) s.WslDistro = DefaultWslDistro();
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

        /// <summary>Windows' default WSL distribution, unless it is Docker Desktop's; "" without one.</summary>
        private static string DefaultWslDistro()
        {
            string def = null;
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Lxss"))
                {
                    var id = k == null ? null : k.GetValue("DefaultDistribution") as string;
                    if (id != null) using (var d = k.OpenSubKey(id)) def = d == null ? null : d.GetValue("DistributionName") as string;
                }
            }
            catch (Exception) { }
            return ChooseWslDistro(def, WslDistros());
        }

        /// <summary>
        /// The default distribution, or the first other one registered when the default is Docker Desktop's, which
        /// holds no tools of the user's and would only be started for nothing; "" when none is left.
        /// </summary>
        internal static string ChooseWslDistro(string defaultName, IEnumerable<string> registered)
        {
            if (!string.IsNullOrEmpty(defaultName) && !IsDockerDistro(defaultName)) return defaultName;
            return registered.FirstOrDefault(n => !IsDockerDistro(n)) ?? "";
        }

        /// <summary>Docker Desktop's distributions ("docker-desktop", "docker-desktop-data").</summary>
        internal static bool IsDockerDistro(string name)
        {
            return name != null && name.StartsWith("docker-desktop", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Registered WSL distributions without Docker Desktop's, in registry order.</summary>
        internal static string[] WslDistros()
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Lxss"))
                {
                    if (k == null) return new string[0];
                    return k.GetSubKeyNames().Select(n =>
                    {
                        using (var d = k.OpenSubKey(n)) return d == null ? null : d.GetValue("DistributionName") as string;
                    }).Where(n => n != null && !IsDockerDistro(n)).ToArray();
                }
            }
            catch (Exception) { return new string[0]; }
        }

        // Automatic startup through the AI Meter shortcut.
        // Windows lists the app's own name and icon under Apps > Startup and opens it at sign-in.
        internal static string StartupRegistryRoot
        {
            get
            {
#if AIMETER_TEST
                return @"Software\" + Path.GetFileName(Path.GetDirectoryName(Folder)) + "-startup-" + Path.GetFileName(Folder);
#else
                return @"Software\Microsoft\Windows\CurrentVersion";
#endif
            }
        }

        private static string RunKey { get { return StartupRegistryRoot + @"\Run"; } }
        private static string ApprovedKey { get { return StartupRegistryRoot + @"\Explorer\StartupApproved\StartupFolder"; } }

        internal static string StartupLink
        {
            get
            {
#if AIMETER_TEST
                return Path.Combine(Folder, "AI Meter.lnk");
#else
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Startup), "AI Meter.lnk");
#endif
            }
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

        /// <summary>
        /// Apply the welcome window's "Start with Windows" choice, once the window has closed. Enabling keeps an existing
        /// registration (another copy's shortcut, the legacy Run command or an entry disabled in Windows); declining
        /// removes any registration, as the same toggle in Settings does.
        /// </summary>
        internal static void ApplyFirstRunStartup(bool enabled)
        {
            if (!enabled || Native.IsPackaged) { StartsWithWindows = enabled; return; }
            if (File.Exists(StartupLink)) return;
            using (var k = Registry.CurrentUser.OpenSubKey(RunKey))
                if (k != null && k.GetValue("AI Meter") != null) return;
            // Only installation and an explicit preference change may replace an existing launch command.
            StartsWithWindows = true;
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
