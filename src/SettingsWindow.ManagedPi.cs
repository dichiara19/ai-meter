using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;

namespace AIMeter
{
    internal partial class SettingsWindow
    {
        // ------------------------------------------------------------ the managed Pi (ADR 0004)

        /// <summary>
        /// The copy of Pi that AI Meter can keep for delegated tasks, at the end of "Delegated tasks": its download,
        /// with what it fetches and from where, and, once installed, its removal and the keys the user chooses to copy
        /// into it. Nothing is downloaded or copied except by the controls here.
        /// </summary>
        private void BuildManagedPi()
        {
            var kind = Agents.Get(ManagedPi.RuntimeId);
            if (!ManagedPi.Offered || kind == null || kind.PlaceFor(ManagedPi.Place) == null) return;
            Hint(L.T("No coding agent on Windows? AI Meter can download Pi, an open-source coding agent, and keep it for delegated tasks on Windows folders. It is used only where you have no Pi of your own, with the keys you choose here, and never on a subscription plan."));

            string megabytes = Math.Round(ManagedPi.Pinned.ArchiveBytes / 1048576.0).ToString(L.Culture);
            var rows = new List<FrameworkElement>();
            if (C.ManagedPiBusy)
            {
                // The line is updated in place: rebuilding the page for every block would lose the scroll position.
                var line = Ui.Text(L.F("Downloading {0} of {1} MB…", "0", megabytes), 12, "Text2");
                C.ManagedPiProgress = bytes => line.Text = L.F("Downloading {0} of {1} MB…", Math.Round(bytes / 1048576.0).ToString(L.Culture), megabytes);
                rows.Add(Row(null, L.T("Pi for delegated tasks"), null, Button(L.T("Cancel"), C.CancelManagedPiInstall, false), line));
            }
            else if (!ManagedPi.Installed)
            {
                string other = ManagedPi.OtherVersionInstalled;
                string state = other == null
                    ? L.F("Not installed. Downloads Pi {0} from its project on GitHub ({1} MB) and checks the file before unpacking it.", ManagedPi.Pinned.Version, megabytes)
                    : L.F("Pi {0} is installed, and this version of AI Meter works with Pi {1}. Download it to keep delegating ({2} MB).", other, ManagedPi.Pinned.Version, megabytes);
                // An older release keeps its configuration, and with it the keys copied there: it can be removed too.
                var download = Button(L.T("Download"), C.InstallManagedPi, false);
                rows.Add(Row(null, L.T("Pi for delegated tasks"), C.ManagedPiError ?? state, other == null ? download : Pair(download, Button(L.T("Remove"), RemoveManagedPi, false))));
            }
            else
            {
                rows.Add(Row(null, L.T("Pi for delegated tasks"), C.ManagedPiError ?? L.F("Pi {0} is installed in {1}.", ManagedPi.Pinned.Version, ManagedPi.Root),
                    Button(L.T("Remove"), RemoveManagedPi, false)));
                rows.AddRange(ManagedPiKeyRows());
                // The choice exists only where a Pi of the user's on Windows can be used for delegation at all.
                if (kind.PlaceFor("windows") != null)
                    rows.Add(Row(null, L.T("Also offer it beside your own Pi"), L.T("When off, delegated tasks on Windows use your own Pi wherever it can run them."),
                        Toggle(S.ManagedPiBesideOwn, v => { S.ManagedPiBesideOwn = v; S.Save(); })));
            }
            Group(rows.ToArray());
            if (ManagedPi.Installed && !C.ManagedPiBusy)
                Hint(L.T("A key you turn on is copied into Pi's own key file as plain text, as in any Pi installation; AI Meter's own copy stays encrypted. Turning it off, or removing Pi, deletes it from there."));
        }

        /// <summary>
        /// One switch for each service whose key can be copied to some use: a service that AI Meter reads with a key,
        /// that Pi knows, and whose list has a free model with tool support, which is what a delegated task runs on
        /// (free-tier models do not count: they are not offered through the copy). A service already ticked is always listed, whatever became of it since (turned off,
        /// its key gone), so that its switch is there to take the key back.
        /// </summary>
        private IEnumerable<FrameworkElement> ManagedPiKeyRows()
        {
            var snap = C.Last;
            var ticked = S.ManagedPiServices.Select(Definitions.Get).Where(d => d != null);
            var services = ManagedPi.ServicesWithKey()
                .Where(d => snap != null && snap.Providers.Any(p => p.Id == d.Id && p.Catalogue != null &&
                    p.Catalogue.Models.Any(m => m.Tools && (m.Free == null || m.Free.Confirmed) && !Candidates.IsFreeTier(m))))
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

        private void RemoveManagedPi()
        {
            if (MessageBox.Show(this, L.T("Remove Pi, its settings and the keys copied into it? Your own agents and AI Meter's keys are not touched."), "AI Meter",
                    MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
            C.RemoveManagedPi();
        }
    }
}
