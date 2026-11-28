using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;

namespace AIMeter
{
    internal partial class SettingsWindow
    {
        // ------------------------------------------------------------ the managed Pi (ADR 0004, ADR 0005)

        /// <summary>Whether this build can keep a copy of Pi somewhere: the page exists only then.</summary>
        private static bool DelegationAgentOffered
        {
            get
            {
                var kind = Agents.Get(ManagedPi.RuntimeId);
                return ManagedPi.Offered && kind != null && (kind.PlaceFor(ManagedPi.Place) != null || kind.PlaceFor(ManagedPi.PlaceInWsl) != null);
            }
        }

        /// <summary>The page's line on the overview: the installed copy, or that there is none. It reads only what AI Meter already knows.</summary>
        private static string DelegationAgentStatus()
        {
            return ManagedPi.Reachable().Any(c => c.Installed) ? L.F("Pi {0} installed", ManagedPi.Version) : L.T("Pi not installed");
        }

        /// <summary>
        /// The guided setup of the agent that runs delegated tasks (ADR 0005 decision 5): what AI Meter found on this
        /// PC, then a row for each place it can keep a copy of Pi in (inside WSL first, where there is a distribution,
        /// then on Windows) with its download, with what it fetches and from where, and its removal; once a copy is
        /// installed, the keys the user chooses to copy into every copy, and which copy comes first. Nothing is
        /// downloaded or copied except by the controls here. The page reads only what AI Meter already knows: the last
        /// scan of the user's agents, and the copy inside WSL only while its distribution runs.
        /// </summary>
        private void BuildDelegationAgent()
        {
            Breadcrumb("MCP", "apps", L.T("Delegation agent"));
            Hint(L.T("Delegated tasks run in a coding agent: one already on this PC, or Pi, an open-source agent that AI Meter can install. That copy uses only the keys selected below."));

            var kind = Agents.Get(ManagedPi.RuntimeId);
            var windowsPlace = kind.PlaceFor(ManagedPi.Place);
            var wslPlace = kind.PlaceFor(ManagedPi.PlaceInWsl);
            string distro = S.WslDistro;
            var windows = windowsPlace == null ? null : ManagedPi.Windows;
            var inWsl = wslPlace == null ? null : ManagedPi.ReachableIn("wsl");
            bool offersWsl = wslPlace != null && !string.IsNullOrEmpty(distro);
            var installed = new[] { inWsl, windows }.Where(c => c != null && c.Installed).ToList();

            Section(L.T("This PC"));
            Group(ThisPcRows().ToArray());

            Section(L.T("Pi managed by AI Meter"));
            var rows = new List<FrameworkElement>();
            if (offersWsl)
                rows.Add(inWsl == null
                    ? Row(null, L.F("Pi inside WSL ({0})", distro), L.T("Start WSL to install, update or remove Pi there"), null)
                    : CopyRow(inWsl, L.F("Pi inside WSL ({0})", inWsl.Distro)));
            if (windows != null)
            {
                rows.Add(CopyRow(windows, L.T("Pi on Windows")));
                // Without Git Bash the copy on Windows still works, with PowerShell for its commands: say so, and where to get it.
                if (windows.Installed && !(C.ManagedPiBusy && C.ManagedPiBusyWhere == "windows") && windows.BoundShells.Length == 0)
                    rows.Add(Row(null, L.T("Git Bash not found"), L.T("Without Git Bash, tasks that change files run their commands in PowerShell. AI Meter detects Git for Windows at its next start."),
                        Button(L.T("Get Git for Windows"), () => OpenPage(ManagedPi.GitForWindowsPage), false)));
            }
            if (C.ManagedPiError != null && C.ManagedPiErrorWhere == null) rows.Add(Row(null, C.ManagedPiError, null, null));
            Group(rows.ToArray());
            if (installed.Count == 0) return;

            Section(L.T("API keys"));
            Hint(L.T("A selected key is copied to Pi's key file in plain text. Turning the switch off, or removing Pi, deletes the copy."));
            Group(ManagedPiKeyRows(installed).ToArray());

            var preferences = new List<FrameworkElement>();
            if (installed.Count == 2)
                preferences.Add(Row(null, L.T("Preferred copy from Windows"), L.T("Used first when both copies can run the model of a task started from Windows"),
                    Combo(new[] { Tuple.Create("wsl", L.T("Pi inside WSL")), Tuple.Create("windows", L.T("Pi on Windows")) },
                        Candidates.PreferredManagedPlace(S.ManagedPiPrefer), v => { S.ManagedPiPrefer = v; S.Save(); })));
            // The choice exists only where a Pi of the user's can be used for delegation at all, beside an installed copy.
            if (installed.Any(c => kind.PlaceFor(c.Where) != null))
                preferences.Add(Row(null, L.T("Use alongside your own Pi"), L.T("When off, your own Pi is used wherever it can run the task"),
                    Toggle(S.ManagedPiBesideOwn, v => { S.ManagedPiBesideOwn = v; S.Save(); })));
            if (preferences.Count == 0) return;
            Section(L.T("Preferences"));
            Group(preferences.ToArray());
        }

        /// <summary>
        /// One row per fact AI Meter has about this PC: the user's agents that delegation can run (from the last scan;
        /// the first is started in the background), whether WSL runs and whether Git for Windows is found.
        /// </summary>
        private IEnumerable<FrameworkElement> ThisPcRows()
        {
            var scan = Candidates.LastScan;
            string agents;
            if (scan == null)
            {
                C.ScanAgentsForSettings();
                agents = L.T("Looking for your coding agents…");
            }
            else
            {
                var found = scan.Found.Where(f => !f.Managed).Select(f =>
                {
                    var kind = Agents.Get(f.Runtime);
                    string name = kind == null ? f.Runtime : kind.Name;
                    return f.Where == "wsl" ? L.F("{0} in WSL ({1})", name, f.Distro) : L.F("{0} on Windows", name);
                }).Distinct().ToList();
                agents = found.Count > 0 ? string.Join(", ", found) : L.T("None that delegated tasks can use. Install Pi below to start delegating.");
            }
            yield return Row(null, L.T("Your coding agents"), agents, null);
            string wsl = ManagedPi.WslState;
            yield return Row(null, "WSL", wsl == "running" ? L.F("{0}, running", S.WslDistro) : wsl == "stopped" ? L.F("{0}, not running", S.WslDistro) : L.T("No distribution found"), null);
            yield return Row(null, "Git for Windows", ManagedPi.GitForWindowsFound ? L.T("Installed") : L.T("Not found"), null);
        }

        /// <summary>
        /// A copy's row: its state, with Download, Cancel and the progress, or Remove. One download runs at a time, so
        /// while one runs the other copy offers no download.
        /// </summary>
        private FrameworkElement CopyRow(ManagedPi.Copy copy, string title)
        {
            string where = copy.Where;
            string megabytes = Math.Round(copy.Release.ArchiveBytes / 1048576.0).ToString(L.Culture);
            string error = C.ManagedPiErrorWhere == where ? C.ManagedPiError : null;
            if (C.ManagedPiBusy && C.ManagedPiBusyWhere == where)
            {
                // The line is updated in place: rebuilding the page for every block would lose the scroll position.
                var line = Ui.Text(L.F("Downloading {0} of {1} MB…", "0", megabytes), 12, "Text2");
                C.ManagedPiProgress = bytes => line.Text = L.F("Downloading {0} of {1} MB…", Math.Round(bytes / 1048576.0).ToString(L.Culture), megabytes);
                return Row(null, title, null, Button(L.T("Cancel"), C.CancelManagedPiInstall, false), line);
            }
            if (copy.Installed)
                return Row(null, title, error ?? L.F("Pi {0}, installed in {1}", ManagedPi.Version, copy.AgentPath(copy.Root) ?? copy.Root),
                    C.ManagedPiBusy ? null : Button(L.T("Remove"), () => RemoveManagedPi(where), false));
            string other = copy.OtherVersionInstalled;
            string state = other == null
                ? L.F("Not installed. Pi {0} is downloaded from GitHub ({1} MB).", ManagedPi.Version, megabytes)
                : L.F("Pi {0} is installed; this version of AI Meter requires Pi {1}. Download it to keep delegating ({2} MB).", other, ManagedPi.Version, megabytes);
            if (C.ManagedPiBusy) return Row(null, title, error ?? state, null);
            // An older release keeps its configuration, and with it the keys copied there: it can be removed too.
            var download = Button(L.T("Download"), () => C.InstallManagedPi(where), false);
            return Row(null, title, error ?? state, other == null ? download : Pair(download, Button(L.T("Remove"), () => RemoveManagedPi(where), false)));
        }

        /// <summary>
        /// One switch for each service whose key can be copied to some use: a service that AI Meter reads with a key,
        /// that Pi knows and has a model list for, and that lists free or free-tier models, which is what a delegated
        /// task runs on (where the service's list leaves out tool support or prices, Pi's own list fills them in). A
        /// service already ticked is always listed, whatever became of it since (turned off,
        /// its key gone), so that its switch is there to take the key back. A switch applies to every copy.
        /// </summary>
        private IEnumerable<FrameworkElement> ManagedPiKeyRows(List<ManagedPi.Copy> installed)
        {
            var snap = C.Last;
            var ticked = S.ManagedPiServices.Select(Definitions.Get).Where(d => d != null);
            var services = ManagedPi.ServicesWithKey()
                .Where(d => snap != null && installed.Any(c => c.ModelsFile(ManagedPi.ProviderOf(d, c.PlaceKey)) != null) &&
                    snap.Providers.Any(p => p.Id == d.Id && p.Catalogue != null && p.Catalogue.Models.Count > 0))
                .Concat(ticked).GroupBy(d => d.Id).Select(g => g.First())
                .OrderBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
            if (services.Count == 0)
                return new[] { Row(null, L.T("No key available"), L.T("Turn on a service with free models, such as OpenRouter, and add its key"), null) };
            return services.Select(d =>
            {
                string id = d.Id;
                return Row(null, d.Name, L.T("Delegated tasks can use this service's free models"),
                    Toggle(S.ManagedPiServices.Contains(id), v => C.SetManagedPiService(id, v)));
            });
        }

        private static void OpenPage(string address)
        {
            try { System.Diagnostics.Process.Start(address); }
            catch (System.ComponentModel.Win32Exception) { }   // No browser is registered for web addresses.
        }

        private void RemoveManagedPi(string where)
        {
            if (MessageBox.Show(this, L.T("Remove Pi, its settings and the keys copied to it?"), "AI Meter",
                    MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
            C.RemoveManagedPi(where);
        }
    }
}
