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
        public string WslDistro { get; set; }
        public string WslUser { get; set; }

        // widget sul desktop
        public bool DesktopWidget { get; set; }
        public double DesktopX { get; set; }
        public double DesktopY { get; set; }
        public bool ExpandedClaude { get; set; }
        public bool ExpandedOpenAI { get; set; }

        // sovrimpressione
        public bool Overlay { get; set; }
        public string OverlayCorner { get; set; }   // "tr", "tl", "br", "bl"
        public string OverlayMetric { get; set; }   // "5h", "week", "both"
        public bool OverlayHideFullscreen { get; set; }
        public bool OverlayCollapsed { get; set; }

        // generale
        public string Theme { get; set; }           // "system", "light", "dark"
        public string Refresh { get; set; }         // "auto", "low"
        public bool FirstRunDone { get; set; }

        public Settings()
        {
            ClaudeEnabled = true; ClaudeLiveUsage = true; ClaudeShowChat = true; ClaudeShowCode = true; ClaudeShowCowork = true;
            OpenAIEnabled = true; OpenAIShowChat = true;
            WslDistro = DefaultWslDistro(); WslUser = Environment.UserName;
            DesktopWidget = true; DesktopX = double.MinValue; DesktopY = double.MinValue;
            Overlay = false; OverlayCorner = "tr"; OverlayMetric = "5h"; OverlayHideFullscreen = true;
            Theme = "system"; Refresh = "auto";
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
                using (var k = Registry.CurrentUser.OpenSubKey(RunKey))
                    return k != null && k.GetValue("AI Meter") != null;
            }
            set
            {
                using (var k = Registry.CurrentUser.CreateSubKey(RunKey))
                {
                    if (value) k.SetValue("AI Meter", "\"" + System.Reflection.Assembly.GetExecutingAssembly().Location + "\" --startup");
                    else k.DeleteValue("AI Meter", false);
                }
            }
        }
    }
}
