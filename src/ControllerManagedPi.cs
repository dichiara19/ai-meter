using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Windows;

namespace AIMeter
{
    internal partial class Controller
    {
        // The managed copies of Pi (ADR 0004, ADR 0005) as Settings drives them: download, removal and which keys they
        // hold. Nothing here runs unless the user acts in Settings › MCP › Delegation agent; the one exception is
        // SyncManagedPiKeys, which keeps the keys already chosen in step with AI Meter's own.

        /// <summary>A download is in progress, for the copy in ManagedPiBusyWhere: one at a time, whichever the place.</summary>
        internal bool ManagedPiBusy { get; private set; }
        internal string ManagedPiBusyWhere { get; private set; }

        /// <summary>
        /// Why the last download, removal or key sync failed, for Settings to show; null after a success or a new
        /// attempt. ManagedPiErrorWhere is the place of the copy it concerns, or null for the keys of every copy.
        /// </summary>
        internal string ManagedPiError { get; private set; }
        internal string ManagedPiErrorWhere { get; private set; }

        /// <summary>
        /// Where Settings shows the bytes downloaded so far. Set by the page that shows the download, on the interface
        /// thread, and called there; the page replaces it each time it is rebuilt.
        /// </summary>
        internal Action<long> ManagedPiProgress;

        private volatile bool managedPiCancelled;

        /// <summary>Download and unpack the managed copy for a place ("windows" or "wsl") in the background, then show the result in Settings.</summary>
        public void InstallManagedPi(string where)
        {
            if (ManagedPiBusy || !ManagedPi.Offered) return;
            ManagedPiError = null;
            ManagedPiErrorWhere = where;
            var copy = ManagedPi.ReachableIn(where);
            if (copy == null)
            {
                ManagedPiError = L.T("WSL is not running. Start it to install Pi there; AI Meter does not start it.");
                RefreshSettingsPage();
                return;
            }
            // Replacing an older release, or adding a copy beside one already installed, keeps the user's choices. A
            // first installation starts with nothing ticked: choices left in the settings by a copy removed earlier are
            // cleared on disk before anything is downloaded, and when that cannot be saved nothing is installed, or a
            // restart would copy those keys again unasked. Saved every time, also when the list is already empty in
            // memory: a removal may not have saved it.
            if (copy.OtherVersionInstalled == null && !ManagedPi.Reachable().Any(c => c.Installed))
            {
                Settings.ManagedPiServices = new List<string>();
                Settings.ManagedPiServicesAwaitingKey = new List<string>();
                if (!Settings.Save())
                {
                    ManagedPiError = L.T("This choice could not be saved, so it will not last past a restart. Check AI Meter's settings file, then change it again.");
                    RefreshSettingsPage();
                    return;
                }
            }
            ManagedPiBusy = true;
            ManagedPiBusyWhere = where;
            managedPiCancelled = false;
            RefreshSettingsPage();
            long archiveBytes = copy.Release.ArchiveBytes;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                string error = null;
                bool installed = false;
                long shown = 0;
                try
                {
                    copy.Install(bytes =>
                    {
                        // About two hundred updates for the whole download are enough for a line of text.
                        if (bytes - shown < archiveBytes / 200) return;
                        shown = bytes;
                        ui.BeginInvoke(new Action(() => { if (ManagedPiProgress != null) ManagedPiProgress(bytes); }));
                    }, () => managedPiCancelled);
                    installed = true;
                }
                catch (OperationCanceledException) { }
                catch (ManagedPi.InstallException ex) { error = ex.Message; }
                ui.BeginInvoke(new Action(() =>
                {
                    ManagedPiBusy = false;
                    ManagedPiBusyWhere = null;
                    ManagedPiError = error;
                    ManagedPiProgress = null;
                    // A copy that replaces an older release, or joins another, gets the keys of the services already ticked.
                    if (installed) SyncManagedPiKeys();
                    RefreshSettingsPage();
                }));
            });
        }

        public void CancelManagedPiInstall() { managedPiCancelled = true; }

        /// <summary>Remove the managed copy in a place, its configuration and the keys copied into it.</summary>
        public void RemoveManagedPi(string where)
        {
            if (ManagedPiBusy) return;
            ManagedPiError = null;
            ManagedPiErrorWhere = where;
            var copy = ManagedPi.ReachableIn(where);
            try
            {
                if (copy == null) throw new ManagedPi.InstallException(L.T("WSL is not running. Start it to remove Pi from there; AI Meter does not start it."));
                copy.Remove(Settings);
            }
            catch (ManagedPi.InstallException ex) { ManagedPiError = ex.Message; }
            RefreshSettingsPage();
        }

        /// <summary>
        /// The user's switch in Settings: copy a service's key into the key file of every managed copy AI Meter can
        /// reach, or take it out.
        /// </summary>
        public void SetManagedPiService(string service, bool on)
        {
            ManagedPi.Choose(Settings, service, on);
            ApplyManagedPiChoices();
        }

        /// <summary>Save the ticked services as they are now and bring the copies' key files in step, saying what failed.</summary>
        private void ApplyManagedPiChoices()
        {
            // A choice that is not on disk would come back at the next start: a withdrawn key would be copied again.
            if (!Settings.Save())
                MessageBox.Show(L.T("This choice could not be saved, so it will not last past a restart. Check AI Meter's settings file, then change it again."), "AI Meter", MessageBoxButton.OK, MessageBoxImage.Warning);
            if (!ManagedPi.Sync(Settings))
                MessageBox.Show(L.T("Pi's key file could not be written. Check that no delegated task is running, then try again."), "AI Meter", MessageBoxButton.OK, MessageBoxImage.Warning);
            RefreshSettingsPage();
        }

        /// <summary>
        /// Bring the key files of the managed copies AI Meter can reach in step with the keys it has now and the services
        /// chosen, and the Windows copy's shell with the Git Bash found now: after a key was saved, and after every
        /// reading of the services (the first one soon after the start, when AI Meter knows whether WSL runs), so that a
        /// key the user changed in another app, where AI Meter only finds it, does not stay behind there. A copy inside
        /// a WSL distribution that is not running is left as it is until it runs again. In the background, as finding a
        /// key can read other apps' files and the copy inside WSL is reached through the distribution's file share;
        /// one run at a time.
        /// </summary>
        internal void SyncManagedPiKeys()
        {
            var settings = Settings;
            if (settings == null || Program.Demo) return;
            if (Interlocked.Exchange(ref managedPiSyncing, 1) == 1) return;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    // Also with nothing ticked or no copy installed: a key file left by a failed deletion goes then.
                    if (ManagedPi.Reachable().Any(c => System.IO.Directory.Exists(c.Root))) SyncManagedPiKeysWithRetries(settings);
                }
                finally { Interlocked.Exchange(ref managedPiSyncing, 0); }
            });
        }

        private int managedPiSyncing;

        /// <summary>
        /// A file held for a moment by a running task or an antivirus is tried again; a failure that lasts is shown in
        /// Settings, and the next reading of the services tries once more. Meanwhile a key that no longer matches gives
        /// no access (ManagedPi.Copy.KeyFor).
        /// </summary>
        private void SyncManagedPiKeysWithRetries(Settings settings)
        {
            // Another AI Meter of the same user (a second Windows session) may have changed the choices: its are newer.
            var elsewhere = Settings.ManagedPiServicesSavedElsewhere();
            if (elsewhere != null) settings.ManagedPiServices = elsewhere;
            foreach (int seconds in SyncRetrySeconds)
            {
                if (seconds > 0) Thread.Sleep(seconds * 1000);
                if (ManagedPi.Sync(settings)) return;
            }
            ui.BeginInvoke(new Action(() =>
            {
                ManagedPiError = L.T("Pi's key file could not be written. Check that no delegated task is running, then try again.");
                ManagedPiErrorWhere = null;
                RefreshSettingsPage();
            }));
        }

        private static readonly int[] SyncRetrySeconds = { 0, 2, 10, 30, 60 };

        /// <summary>
        /// Scan the user's agents once in the background, for Settings to say which of them delegation can run when no
        /// scan has been made yet (Candidates.LastScan); the page is shown again with the result.
        /// </summary>
        internal void ScanAgentsForSettings()
        {
            if (Interlocked.Exchange(ref agentsScanning, 1) == 1) return;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try { Candidates.ScanRuntimes(); }
                catch (Exception ex) { Program.LogError("scan-agents", ex); }
                ui.BeginInvoke(new Action(RefreshSettingsPage));
            });
        }

        /// <summary>Set once a scan for Settings has started: one is enough, later ones come from AI apps' requests.</summary>
        private int agentsScanning;
    }
}
