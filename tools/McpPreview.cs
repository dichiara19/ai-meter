using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Windows;

namespace AIMeter
{
    /// <summary>
    /// Render Settings › MCP › Delegation agent with an isolated fixture, without showing windows or reading the PC
    /// that renders it: a stand-in copy of Pi inside a sample WSL distribution, made-up keys, and a
    /// sample scan of the user's agents. Nothing is downloaded and no key leaves the fixture's folder.
    /// </summary>
    public static class McpPreview
    {
        private const string Distro = "Ubuntu", SampleLocal = @"C:\Users\you\AppData\Local\AIMeter", SampleLinuxHome = "/home/you";

        public static void Run(string output, string storage, string language)
        {
            Settings.TestFolder = storage;
            Directory.CreateDirectory(storage); Directory.CreateDirectory(output);
            MediaRenderer.Prepare(language);
            var settings = (Settings)typeof(MediaRenderer).GetField("S", BindingFlags.NonPublic | BindingFlags.Static).GetValue(null);
            settings.FindKeys = false;
            settings.WslDistro = Distro;
            Settings.Current = settings;
            var controller = new Controller(false, null, null);
            typeof(Controller).GetProperty("Settings").SetValue(controller, settings, null);

            // The PC as the page reads it: a running distribution, Git for Windows, and two agents of the user's own.
            var place = new WslPlace { Distro = Distro, Home = Path.Combine(storage, "wsl-home") };
            Probe.WslPlacesUp = new[] { place };
            ManagedPi.LocateGitBash = () => @"C:\Program Files\Git\bin\bash.exe";
            var scan = new RuntimeScan();
            scan.Found.Add(new RuntimePlace { Runtime = "claude", Where = "wsl", Distro = Distro });
            scan.Found.Add(new RuntimePlace { Runtime = "codex", Where = "wsl", Distro = Distro });
            typeof(Candidates).GetField("lastScan", BindingFlags.NonPublic | BindingFlags.Static).SetValue(null, scan);

            // The copy inside WSL installed, the one on Windows on offer, and the keys of two services ticked.
            var inWsl = ManagedPi.InWsl(place);
            Directory.CreateDirectory(Path.GetDirectoryName(inWsl.ProgramPath));
            File.WriteAllText(inWsl.ProgramPath, "stand-in");
            foreach (string service in new[] { "openrouter", "nvidia" }) ApiKeys.Save(service, "made-up-key-0000000000000000");
            settings.ManagedPiServices = new List<string> { "openrouter", "nvidia" };
            ManagedPi.Sync(settings);

            foreach (var dark in new[] { true, false })
            {
                MediaRenderer.SetTheme(dark);
                var window = new SettingsWindow(controller);
                window.Open("apps:agent");
                var content = (FrameworkElement)window.Content;
                window.Content = null;
                // The fixture's Linux home is a Windows folder, shown as a path below "/": show a Linux home in its place.
                MediaRenderer.ReplaceText(content, "/" + Path.GetFileName(storage) + "/wsl-home", SampleLinuxHome);
                MediaRenderer.ReplaceText(content, storage, SampleLocal);
                MediaRenderer.Save(MediaRenderer.McpShot(content, dark), Path.Combine(output, dark ? "delegation-dark.png" : "delegation-light.png"), 2);
            }
        }
    }
}
