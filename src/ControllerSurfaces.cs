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
        public void ResetDesktopPosition()
        {
            if (desktop != null) desktop.ResetPosition();
            else { Settings.DesktopX = double.MinValue; Settings.DesktopY = double.MinValue; Settings.Save(); }
        }

        public void OpenHistory()
        {
            if (Program.Demo) return;
            if (historyWindow == null)
            {
                historyWindow = new HistoryWindow(this, historyStore);
                historyWindow.Closed += (sender, args) => { historyWindow = null; UpdateVisibility(); };
                historyWindow.Show();
            }
            if (historyWindow.WindowState == WindowState.Minimized) historyWindow.WindowState = WindowState.Normal;
            historyWindow.Activate();
            UpdateVisibility();
        }

        /// <summary>
        /// Show the first-launch consent window before any surface or network request starts.
        /// The launch counts as done only once the window closes. Returns whether sample data was requested.
        /// </summary>
        private bool AskFirstRunConsent()
        {
            bool sampleData = false;
            CompleteFirstRun(Settings, () =>
            {
                var welcome = new WelcomeWindow(Settings);
                welcome.ShowDialog();
                sampleData = welcome.SampleDataRequested;
                return welcome.StartWithWindowsRequested;
            });
            return sampleData;
        }

        /// <summary>
        /// Run the consent step (<paramref name="ask"/> returns the "Start with Windows" choice) and only then record it,
        /// so start-up is never registered before the user has seen the choice (Store policy 10.2.8).
        /// </summary>
        internal static void CompleteFirstRun(Settings settings, Func<bool> ask)
        {
            bool startWithWindows = ask();
            settings.FirstRunDone = true;
            settings.Save();
            try { Settings.ApplyFirstRunStartup(startWithWindows); } catch (Exception ex) { Program.LogError("startup", ex); }
        }

        /// <summary>
        /// Switch between invented sample data and real readings without restarting. The choice is not saved;
        /// sample data never reaches the history, notifications or the alert state.
        /// </summary>
        public void SetSampleData(bool on)
        {
            if (Program.Demo == on) return;
            Program.Demo = on;
            if (on && historyWindow != null) historyWindow.Close();
            if (on)
            {
                balloonAction = null;
                tray.ShowBalloonTip(15000, L.T("Showing sample data"),
                    L.T("The figures are invented. Choose Leave sample data from the notification area menu, or turn it off in Settings → About."), Forms.ToolTipIcon.Info);
            }
            if (settingsWindow != null) settingsWindow.Dispatcher.BeginInvoke(new Action(settingsWindow.Build), DispatcherPriority.Background);
            RefreshNow();
        }

        public void OpenSettings()
        {
            if (settingsWindow == null)
            {
                settingsWindow = new SettingsWindow(this);
                settingsWindow.Closed += (o, e) => settingsWindow = null;
                settingsWindow.Show();
            }
            if (settingsWindow.WindowState == WindowState.Minimized) settingsWindow.WindowState = WindowState.Normal;
            settingsWindow.Activate();
        }

        public void OpenSettings(string page)
        {
            OpenSettings();
            settingsWindow.Open(page);
        }

        // ================================================================ panel and pill
        public void ToggleFlyout() { ToggleFlyout(() => flyout.ShowFromTray(), DateTime.MinValue); }

        public void ToggleFlyoutFromTaskbar(TaskbarPill anchor) { ToggleFlyout(() => flyout.ShowFromTaskbar(anchor.OnLeft), pillPressedAt); }

        /// <summary>A mouse button, finger or pen went down on the taskbar pill; its click or tap follows on release.</summary>
        public void PillPressed() { pillPressedAt = DateTime.Now; }

        /// <summary>How long a press on the taskbar pill explains what follows it: the panel hiding, the taskbar coming to the foreground.</summary>
        private static readonly TimeSpan PressEffects = TimeSpan.FromSeconds(2);

        /// <summary>
        /// Whether the click releasing a press is the one that just hid the panel: pressing the pill takes focus from an
        /// open panel, which hides it before the click arrives. A touch tap can last longer than a mouse click, so the
        /// press time counts, not a fixed delay. Without a recent press (the notification area icon reports only
        /// clicks) a panel hidden in the last 300 ms counts.
        /// </summary>
        internal static bool ClosedByThisPress(DateTime hiddenAt, DateTime pressedAt, DateTime now)
        {
            if (now - pressedAt > PressEffects) pressedAt = now.AddMilliseconds(-300);
            return hiddenAt >= pressedAt;
        }

        /// <summary>Hide a pinned panel, or render and show it with the given placement.</summary>
        private void ToggleFlyout(Action show, DateTime pressedAt)
        {
            if (flyout.IsVisible && !flyout.HoverMode) { flyout.Hide(); return; }
            if (ClosedByThisPress(flyoutHiddenAt, pressedAt, DateTime.Now)) return;
            if (Last != null) flyout.Render(Last);
            show();
            UpdateVisibility();
            RefreshNow();
        }

        public void ShowDetails(Window anchor)
        {
            CancelHideDetails();
            if (flyout.IsVisible) return;
            if (Last != null) flyout.Render(Last);
            flyout.ShowNear(anchor, false);
            UpdateVisibility();
        }

        /// <summary>
        /// A tap on the overlay. Touch has no hover, so the panel opens next to it with focus: it stays while being read
        /// or scrolled and closes on a tap elsewhere. A second tap on the overlay closes it.
        /// </summary>
        public void PinDetails(Window anchor)
        {
            CancelHideDetails();
            if (flyout.IsVisible && !flyout.HoverMode) { flyout.Hide(); return; }
            if (Last != null) flyout.Render(Last);
            flyout.ShowNear(anchor, true);
            UpdateVisibility();
        }

        public void SetOverlayCollapsed(bool collapsed)
        {
            Settings.OverlayCollapsed = collapsed;
            Settings.Save();
            if (flyout.HoverMode && flyout.IsVisible) flyout.Hide();
            Rerender();
        }

        public void CloseOverlay()
        {
            if (flyout.HoverMode && flyout.IsVisible) flyout.Hide();
            Settings.Overlay = false;
            Settings.Save();
            ApplySettings();
        }

        public void ScheduleHideDetails() { hideDetails.Stop(); hideDetails.Start(); }
        public void CancelHideDetails() { hideDetails.Stop(); }

        private void UpdateVisibility()
        {
            anyVisible = (desktop != null && desktop.IsVisible) || (pill != null && pill.IsVisible) || (dock != null && dock.IsVisible) || flyout.IsVisible
                         || settingsWindow != null || historyWindow != null;
            bool details = flyout.IsVisible || settingsWindow != null || historyWindow != null;
            if (details && !detailsVisible) wake.Set();   // Refresh immediately when details open.
            detailsVisible = details;
        }

        private void OnForegroundChanged(IntPtr hook, uint ev, IntPtr hwnd, int obj, int child, uint thread, uint time)
        {

            // A click outside the app closes the taskbar flyout. Should pressing the pill bring the taskbar to the
            // foreground, this asynchronous event can arrive after the tap has opened the panel: that is not outside.
            bool pillPress = hwnd == Native.FindTaskbar() && DateTime.Now - pillPressedAt < PressEffects;
            if (flyout.IsVisible && flyout.TaskbarMode && !pillPress && !Native.IsOwnWindow(hwnd)) flyout.Hide();
            bool busy = Native.UserIsBusyFullscreen();
            if (busy == fullscreenBusy) return;
            fullscreenBusy = busy;
            UpdatePillVisibility();
        }

        private void UpdatePillVisibility()
        {
            if (pill != null)
            {
                bool hide = Settings.OverlayHideFullscreen && fullscreenBusy;
                if (hide && pill.IsVisible) pill.Hide();
                else if (!hide && !pill.IsVisible) pill.Show();
            }
            if (dock != null)
            {
                // The docked pill follows the taskbar automatically in fullscreen and auto-hide modes.
                dock.Place();
            }
            UpdateVisibility();
        }

        /// <summary>The taskbar pill and floating overlay are mutually exclusive.</summary>
        public void SetTaskbarDock(bool on)
        {
            Settings.TaskbarDock = on;
            if (on) Settings.Overlay = false;
            Settings.Save();
            ApplySettings();
        }
    }
}
