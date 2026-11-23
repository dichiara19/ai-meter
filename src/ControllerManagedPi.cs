using System;
using System.Collections.Generic;
using System.Threading;
using System.Windows;

namespace AIMeter
{
    internal partial class Controller
    {
        // The managed Pi (ADR 0004) as Settings drives it: download, removal and which keys it holds. Nothing here runs
        // unless the user acts in Settings › AI apps › Delegated tasks; the one exception is SyncManagedPiKeys, which
        // keeps the keys already chosen in step with AI Meter's own.

        /// <summary>A download is in progress.</summary>
        internal bool ManagedPiBusy { get; private set; }

        /// <summary>Why the last download or removal failed, for Settings to show; null after a success or a new attempt.</summary>
        internal string ManagedPiError { get; private set; }

        /// <summary>
        /// Where Settings shows the bytes downloaded so far. Set by the page that shows the download, on the interface
        /// thread, and called there; the page replaces it each time it is rebuilt.
        /// </summary>
        internal Action<long> ManagedPiProgress;

        private volatile bool managedPiCancelled;

        /// <summary>Download and unpack the managed Pi in the background, then show the result in Settings.</summary>
        public void InstallManagedPi()
        {
            if (ManagedPiBusy || !ManagedPi.Offered) return;
            ManagedPiError = null;
            // Replacing an older release keeps the user's choices. A new installation starts with nothing ticked: choices
            // left in the settings by a copy removed earlier are cleared on disk before anything is downloaded, and
            // when that cannot be saved nothing is installed, or a restart would copy those keys again unasked.
            // Saved every time, also when the list is already empty in memory: a removal may not have saved it.
            if (ManagedPi.OtherVersionInstalled == null)
            {
                Settings.ManagedPiServices = new List<string>();
                if (!Settings.Save())
                {
                    ManagedPiError = L.T("This choice could not be saved, so it will not last past a restart. Check AI Meter's settings file, then change it again.");
                    RefreshSettingsPage();
                    return;
                }
            }
            ManagedPiBusy = true;
            managedPiCancelled = false;
            RefreshSettingsPage();
            ThreadPool.QueueUserWorkItem(_ =>
            {
                string error = null;
                bool installed = false;
                long shown = 0;
                try
                {
                    ManagedPi.Install(bytes =>
                    {
                        // About two hundred updates for the whole download are enough for a line of text.
                        if (bytes - shown < ManagedPi.Pinned.ArchiveBytes / 200) return;
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
                    ManagedPiError = error;
                    ManagedPiProgress = null;
                    // A copy that replaces an older release gets the keys of the services already ticked.
                    if (installed) SyncManagedPiKeys();
                    RefreshSettingsPage();
                }));
            });
        }

        public void CancelManagedPiInstall() { managedPiCancelled = true; }

        /// <summary>Remove the managed Pi, its configuration and the keys copied into it.</summary>
        public void RemoveManagedPi()
        {
            if (ManagedPiBusy) return;
            ManagedPiError = null;
            try { ManagedPi.Remove(Settings); }
            catch (ManagedPi.InstallException ex) { ManagedPiError = ex.Message; }
            RefreshSettingsPage();
        }

        /// <summary>
        /// Copy a service's key into the managed Pi's key file, or take it out. The settings get a new list, as MCP
        /// threads read the current one.
        /// </summary>
        public void SetManagedPiService(string service, bool on)
        {
            var next = new List<string>(Settings.ManagedPiServices);
            next.Remove(service);
            if (on) next.Add(service);
            Settings.ManagedPiServices = next;
            // A choice that is not on disk would come back at the next start: a withdrawn key would be copied again.
            if (!Settings.Save())
                MessageBox.Show(L.T("This choice could not be saved, so it will not last past a restart. Check AI Meter's settings file, then change it again."), "AI Meter", MessageBoxButton.OK, MessageBoxImage.Warning);
            if (!ManagedPi.Sync(Settings))
                MessageBox.Show(L.T("Pi's key file could not be written. Check that no delegated task is running, then try again."), "AI Meter", MessageBoxButton.OK, MessageBoxImage.Warning);
            RefreshSettingsPage();
        }

        /// <summary>
        /// Bring the managed Pi's key file in step with the keys AI Meter has now and the services chosen, and its shell
        /// with the Git Bash found now: after a key was saved, and after every reading of the services (the first one
        /// soon after the start, when AI Meter knows whether WSL runs), so that a key the user changed in another app,
        /// where AI Meter only finds it, does not stay behind here. In the background, as finding a key can read other
        /// apps' files; one run at a time.
        /// </summary>
        internal void SyncManagedPiKeys()
        {
            var settings = Settings;
            // Also with nothing ticked or no copy installed: a key file left by a failed deletion goes then.
            if (settings == null || Program.Demo || !System.IO.Directory.Exists(ManagedPi.Root)) return;
            if (Interlocked.Exchange(ref managedPiSyncing, 1) == 1) return;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try { SyncManagedPiKeysWithRetries(settings); }
                finally { Interlocked.Exchange(ref managedPiSyncing, 0); }
            });
        }

        private int managedPiSyncing;

        /// <summary>
        /// A file held for a moment by a running task or an antivirus is tried again; a failure that lasts is shown in
        /// Settings, and the next reading of the services tries once more. Meanwhile a key that no longer matches gives
        /// no access (ManagedPi.KeyFor).
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
                RefreshSettingsPage();
            }));
        }

        private static readonly int[] SyncRetrySeconds = { 0, 2, 10, 30, 60 };
    }
}
