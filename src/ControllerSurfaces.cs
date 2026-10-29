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
        public void ToggleFlyout()
        {
            if (flyout.IsVisible && !flyout.HoverMode) { flyout.Hide(); return; }
            if ((DateTime.Now - flyoutHiddenAt).TotalMilliseconds < 300) return; // This click has just closed the panel.
            if (Last != null) flyout.Render(Last);
            flyout.ShowFromTray();
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

        public void ToggleFlyoutFromTaskbar(TaskbarPill anchor)
        {
            if (flyout.IsVisible && !flyout.HoverMode) { flyout.Hide(); return; }
            if ((DateTime.Now - flyoutHiddenAt).TotalMilliseconds < 300) return; // This click has just closed the panel.
            if (Last != null) flyout.Render(Last);
            flyout.ShowFromTaskbar(anchor.OnLeft);
            UpdateVisibility();
            RefreshNow();
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
