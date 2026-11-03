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

        /// <summary>Ask for the Admin key that reads how much of the Claude plan's API credits has been used.</summary>
        public void EnterClaudeAdminKey()
        {
            var w = new ApiKeyWindow(ClaudeProvider.AdminKeyId, "Claude Console", "Admin key",
                "An Admin key (sk-ant-admin01-…) of the Console organisation linked to your plan. Only an organisation admin can create it.",
                "https://platform.claude.com/settings/admin-keys");
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

        /// <summary>
        /// Open Antigravity CLI sign-in in a terminal, preferring Windows when installed. WSL is used only when a
        /// distribution is configured and, while it runs, has the CLI; otherwise a message says nothing was found.
        /// </summary>
        public void LoginAntigravity()
        {
            try
            {
                string exe = AntigravityProvider.CliPath();
                if (exe != null)
                    Process.Start(new ProcessStartInfo("cmd.exe", "/c \"\"" + exe + "\"\"") { UseShellExecute = true });
                else if (MayBeInWsl(@".local\bin\agy"))
                    Process.Start(new ProcessStartInfo("wsl.exe", "-d " + Settings.WslDistro + " -- bash -lc \"~/.local/bin/agy\"") { UseShellExecute = true });
                else
                {
                    MessageBox.Show(L.T("Antigravity CLI was not found on this PC."), "AI Meter");
                    return;
                }
            }
            catch (System.ComponentModel.Win32Exception) { return; }
            RefreshNow();
        }

        private ClaudeLoginWindow claudeLogin;

        /// <summary>Sign in through Claude Code in AI Meter's own window, without a console; the CLI handles authentication.</summary>
        public void LoginClaude()
        {
            if (claudeLogin != null) { claudeLogin.Activate(); return; }
            claudeLogin = new ClaudeLoginWindow(ClaudeLoginCommand);
            claudeLogin.SignedIn += () => { RefreshNow(); if (settingsWindow != null) settingsWindow.Build(); };
            claudeLogin.Closed += (o, e) => claudeLogin = null;
            claudeLogin.Show();
            claudeLogin.Activate();
        }

        /// <summary>The sign-in command where Claude Code is used, or null when it is found neither there nor on Windows.</summary>
        private ProcessStartInfo ClaudeLoginCommand()
        {
            if (UseWslForLogin()) return new ProcessStartInfo("wsl.exe", "-d " + Settings.WslDistro + " -- bash -lc \"claude auth login\"");
            string cli = ClaudeProvider.CliPath();
            return cli == null ? null : new ProcessStartInfo(cli, "auth login");
        }

        /// <summary>
        /// Sign in where Claude Code is used. Windows when it holds the sign-in, or when WSL is not running and Claude
        /// Code is installed on Windows, so that a stopped distribution is not started for nothing; otherwise the
        /// configured distribution when it may have Claude Code.
        /// </summary>
        private bool UseWslForLogin()
        {
            if (File.Exists(Path.Combine(Probe.Home, @".claude\.credentials.json"))) return false;
            if (!collector.WslUp && ClaudeProvider.CliPath() != null) return false;
            return MayBeInWsl(".claude");
        }

        /// <summary>
        /// Whether the configured distribution may hold this home-relative path: never without a distribution; when WSL
        /// is not running it cannot be checked without starting it, so the explicit configuration is trusted.
        /// </summary>
        private bool MayBeInWsl(string homePath)
        {
            if (string.IsNullOrEmpty(Settings.WslDistro)) return false;
            if (!collector.WslUp) return true;
            string path = @"\\wsl.localhost\" + Settings.WslDistro + @"\home\" + Settings.WslUser + @"\" + homePath;
            return Directory.Exists(path) || File.Exists(path);
        }
    }
}
