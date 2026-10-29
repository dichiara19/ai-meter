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
            var welcome = new WelcomeWindow(Settings);
            welcome.ShowDialog();
            Settings.FirstRunDone = true;
            Settings.Save();
            return welcome.SampleDataRequested;
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
        public void ToggleFlyout() { ToggleFlyout(() => flyout.ShowFromTray()); }

        public void ToggleFlyoutFromTaskbar(TaskbarPill anchor) { ToggleFlyout(() => flyout.ShowFromTaskbar(anchor.OnLeft)); }

        /// <summary>Hide a pinned panel, or render and show it with the given placement.</summary>
        private void ToggleFlyout(Action show)
        {
            if (flyout.IsVisible && !flyout.HoverMode) { flyout.Hide(); return; }
            if ((DateTime.Now - flyoutHiddenAt).TotalMilliseconds < 300) return; // This click has just closed the panel.
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
            flyout.ShowNear(anchor);
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

            if (flyout.IsVisible && flyout.TaskbarMode && !Native.IsOwnWindow(hwnd)) flyout.Hide();   // A click outside the app closes the taskbar flyout.
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
