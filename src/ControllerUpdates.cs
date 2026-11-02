using System;
using System.Diagnostics;
using System.Threading;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace AIMeter
{
    internal partial class Controller
    {
        // The first tick waits for the desktop to settle; later ticks only test whether a day has passed.
        private readonly DispatcherTimer updateTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(2) };
        private bool updateRunning;

        /// <summary>Microsoft Store package only: the Store reported a pending update at the last check.</summary>
        internal bool StoreUpdateWaiting { get; private set; }

        private void StartUpdateChecks()
        {
            updateTimer.Tick += (o, e) =>
            {
                updateTimer.Interval = TimeSpan.FromHours(1);
                CheckForUpdate();
                NotifyUpdate();
            };
            updateTimer.Start();
        }

        /// <summary>
        /// Check once a day, or after a Settings change that reset UpdateCheckedAt. The attempt is recorded before the
        /// request, so being offline or rate-limited never causes retries within the same day.
        /// </summary>
        internal void CheckForUpdate()
        {
            if (updateRunning || !UpdateCheck.Enabled(Settings) || !UpdateCheck.Due(Settings.UpdateCheckedAt, DateTime.UtcNow)) return;
            Settings.UpdateCheckedAt = DateTime.UtcNow;
            Settings.Save();
            updateRunning = true;
            bool preReleases = Settings.UpdatePreReleases;
            ThreadPool.QueueUserWorkItem(state =>
            {
#if MSIX
                if (Native.IsPackaged)
                {
                    bool waiting = StoreUpdates.Waiting();
                    ui.BeginInvoke(new Action(() => { updateRunning = false; StoreUpdateWaiting = waiting; RefreshSettingsPage(); }));
                    return;
                }
#endif
                var found = UpdateCheck.Find(preReleases, UpdateCheck.Download);
                ui.BeginInvoke(new Action(() => { updateRunning = false; ApplyUpdate(found); }));
            });
        }

        private void ApplyUpdate(SemVersion found)
        {
            if (found == null) return;   // Offline, rate-limited or unreadable: keep what the last check found.
            string latest = found.ToString();
            Settings.UpdateAvailable = UpdateCheck.IsNewer(latest, Version) ? latest : null;
            Settings.Save();
            NotifyUpdate();
            RefreshSettingsPage();
        }

        /// <summary>One quiet notification per new version, held back while a full-screen app is in front.</summary>
        private void NotifyUpdate()
        {
            string latest = Settings.UpdateAvailable;
            if (!UpdateCheck.Enabled(Settings) || !UpdateCheck.IsNewer(latest, Version) || latest == Settings.UpdateNotified) return;
            if (Native.UserIsBusyFullscreen()) return;
            Settings.UpdateNotified = latest;
            Settings.Save();
            balloonAction = () => OpenReleasePage(latest);
            tray.ShowBalloonTip(15000, L.F("AI Meter {0} is available", latest),
                L.F("You are using {0}. Click to open the release page: nothing is downloaded automatically.", Version), Forms.ToolTipIcon.None);
        }

        internal static void OpenReleasePage(string version)
        {
            try { Process.Start(UpdateCheck.PageFor(version)); }
            catch (Exception ex) { Program.LogError("release-page", ex); }
        }

        private void RefreshSettingsPage()
        {
            if (settingsWindow != null && settingsWindow.IsVisible) settingsWindow.Build();
        }
    }
}
