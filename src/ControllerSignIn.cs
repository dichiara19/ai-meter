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
    internal partial class Controller
    {
        private DispatcherTimer loginWatch;

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
    }
}
