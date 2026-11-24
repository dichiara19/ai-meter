using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;

namespace AIMeter
{
    internal partial class SettingsWindow
    {
        // ------------------------------------------------------------ the managed Pi (ADR 0004, ADR 0005)

        /// <summary>
        /// The guided setup of delegation, at the end of "Delegated tasks" (ADR 0005 decision 5): what AI Meter found on
        /// this PC, then a row for each place it can keep a copy of Pi in (inside WSL first, where there is a
        /// distribution, then on Windows) with its download, with what it fetches and from where, and its removal; once
        /// a copy is installed, the keys the user chooses to copy into every copy, and which copy comes first. Nothing
        /// is downloaded or copied except by the controls here. The page reads only what AI Meter already knows: the
        /// last scan of the user's agents, and the copy inside WSL only while its distribution runs.
        /// </summary>
        private void BuildManagedPi()
        {
            var kind = Agents.Get(ManagedPi.RuntimeId);
            if (!ManagedPi.Offered || kind == null) return;
            var windowsPlace = kind.PlaceFor(ManagedPi.Place);
            var wslPlace = kind.PlaceFor(ManagedPi.PlaceInWsl);
            if (windowsPlace == null && wslPlace == null) return;
            Hint(L.T("AI Meter can download Pi, an open-source coding agent, and keep it for delegated tasks: inside WSL, with a Linux shell, or on Windows. It is used only where you have no Pi of your own, with the keys you choose here, and never on a subscription plan."));

            string distro = S.WslDistro;
            var windows = windowsPlace == null ? null : ManagedPi.Windows;
            var inWsl = wslPlace == null ? null : ManagedPi.ReachableIn("wsl");
            bool offersWsl = wslPlace != null && !string.IsNullOrEmpty(distro);
            var installed = new[] { inWsl, windows }.Where(c => c != null && c.Installed).ToList();

            var rows = new List<FrameworkElement> { Row(null, L.T("On this PC"), FoundOnThisPc(installed.Count > 0), null) };
            if (offersWsl)
                rows.Add(inWsl == null
                    ? Row(null, L.F("Pi inside WSL ({0})", distro), L.T("Start WSL to install Pi there, or to change the copy already there. AI Meter does not start WSL itself."), null)
                    : CopyRow(inWsl, L.F("Pi inside WSL ({0})", inWsl.Distro)));
            if (windows != null)
            {
                rows.Add(CopyRow(windows, L.T("Pi on Windows")));
                // Without Git Bash the copy on Windows still works, with PowerShell for its commands: say so, and where to get it.
                if (windows.Installed && !(C.ManagedPiBusy && C.ManagedPiBusyWhere == "windows") && windows.BoundShells.Length == 0)
                    rows.Add(Row(null, L.T("Git Bash was not found"), L.T("Tasks that change files will run their commands in PowerShell instead of bash. Install Git for Windows to give Pi bash; AI Meter finds it at its next start."),
                        Button(L.T("Get Git for Windows"), () => OpenPage(ManagedPi.GitForWindowsPage), false)));
            }
            if (C.ManagedPiError != null && C.ManagedPiErrorWhere == null) rows.Add(Row(null, C.ManagedPiError, null, null));

            if (installed.Count > 0)
            {
                rows.AddRange(ManagedPiKeyRows(installed));
                if (installed.Count == 2)
                    rows.Add(Row(null, L.T("From Windows, try first"), L.T("When both copies can run a model, tasks started from Windows use this one first."),
                        Combo(new[] { Tuple.Create("wsl", L.T("Pi inside WSL")), Tuple.Create("windows", L.T("Pi on Windows")) },
                            Candidates.PreferredManagedPlace(S.ManagedPiPrefer), v => { S.ManagedPiPrefer = v; S.Save(); })));
                // The choice exists only where a Pi of the user's can be used for delegation at all, beside an installed copy.
                if (installed.Any(c => kind.PlaceFor(c.Where) != null))
                    rows.Add(Row(null, L.T("Also offer it beside your own Pi"), L.T("When off, delegated tasks use your own Pi wherever it can run them."),
                        Toggle(S.ManagedPiBesideOwn, v => { S.ManagedPiBesideOwn = v; S.Save(); })));
            }
            Group(rows.ToArray());
            if (installed.Count > 0 && !C.ManagedPiBusy)
                Hint(L.T("A key you turn on is copied into Pi's own key file as plain text, as in any Pi installation; AI Meter's own copy stays encrypted. Turning it off, or removing Pi, deletes it from there."));
        }

        /// <summary>
        /// One sentence per fact AI Meter has about this PC: the user's agents that delegation can run (from the last
        /// scan; the first is started in the background), whether WSL runs and whether Git for Windows is found, then
        /// the next step when nothing can run a task yet.
        /// </summary>
        private string FoundOnThisPc(bool copyInstalled)
        {
            var facts = new List<string>();
            var scan = Candidates.LastScan;
            bool ownAgents = false;
            if (scan == null)
            {
                C.ScanAgentsForSettings();
                facts.Add(L.T("Looking for your coding agents…"));
            }
            else
            {
                var agents = scan.Found.Where(f => !f.Managed).Select(f =>
                {
                    var kind = Agents.Get(f.Runtime);
                    string name = kind == null ? f.Runtime : kind.Name;
                    return f.Where == "wsl" ? L.F("{0} in WSL ({1})", name, f.Distro) : L.F("{0} on Windows", name);
                }).Distinct().ToList();
                ownAgents = agents.Count > 0;
                facts.Add(ownAgents ? L.F("Your agents that delegated tasks can use: {0}.", string.Join(", ", agents)) : L.T("None of your coding agents can run delegated tasks."));
            }
            string wsl = ManagedPi.WslState;
            facts.Add(wsl == "running" ? L.F("WSL ({0}) is running.", S.WslDistro) : wsl == "stopped" ? L.F("WSL ({0}) is not running.", S.WslDistro) : L.T("No WSL distribution was found."));
            facts.Add(ManagedPi.GitForWindowsFound ? L.T("Git for Windows is installed.") : L.T("Git for Windows was not found."));
            if (scan != null && !ownAgents && !copyInstalled) facts.Add(L.T("Install Pi below to start delegating tasks."));
            return string.Join(" ", facts);
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
                return Row(null, title, error ?? L.F("Pi {0} is installed in {1}.", ManagedPi.Version, copy.AgentPath(copy.Root) ?? copy.Root),
                    C.ManagedPiBusy ? null : Button(L.T("Remove"), () => RemoveManagedPi(where), false));
            string other = copy.OtherVersionInstalled;
            string state = other == null
                ? L.F("Not installed. Downloads Pi {0} from its project on GitHub ({1} MB) and checks the file before unpacking it.", ManagedPi.Version, megabytes)
                : L.F("Pi {0} is installed, and this version of AI Meter works with Pi {1}. Download it to keep delegating ({2} MB).", other, ManagedPi.Version, megabytes);
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
                return new[] { Row(null, L.T("No keys to copy yet"), L.T("Turn on a service with free models, such as OpenRouter, and give AI Meter its key: it then appears here."), null) };
            return services.Select(d =>
            {
                string id = d.Id;
                return Row(null, L.F("{0} key", d.Name), L.T("Lets Pi run delegated tasks on this service's free models."),
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
            if (MessageBox.Show(this, L.T("Remove Pi, its settings and the keys copied into it? Your own agents and AI Meter's keys are not touched."), "AI Meter",
                    MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
            C.RemoveManagedPi(where);
        }
    }
}
