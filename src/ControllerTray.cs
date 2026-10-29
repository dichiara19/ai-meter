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
        private Alerts alerts;

        private Window menuHost;

        // ================================================================ notification area and menu
        private void CreateTray()
        {
            tray = new Forms.NotifyIcon { Text = "AI Meter", Visible = true };
            using (var s = Program.OpenAsset("app.ico"))
                tray.Icon = new System.Drawing.Icon(s, Forms.SystemInformation.SmallIconSize);
            tray.MouseUp += (o, e) =>
            {
                if (e.Button == Forms.MouseButtons.Left) ToggleFlyout();
                else if (e.Button == Forms.MouseButtons.Right) ShowMenu(null);
            };
            tray.BalloonTipClicked += (o, e) => { var a = balloonAction; balloonAction = null; if (a != null) a(); };
        }

        /// <summary>Show limit notifications together because Windows displays only the latest balloon. Clicking opens the panel.</summary>
        private void Notify(Snapshot s)
        {
            if (Program.Demo) { foreach (var p in s.Providers) p.Tip = Alerts.TipFor(p, Settings, DateTime.Now); return; }
            if (alerts == null) alerts = new Alerts();
            var list = alerts.Check(s, Settings);
            // Consumption notices join the same balloon, so they never compete with low-quota warnings.
            List<ConsumptionNotice> consumption;
            lock (pendingConsumption) { consumption = pendingConsumption.ToList(); pendingConsumption.Clear(); }
            // A notice delayed past its relevance stays visible in the history instead of interrupting late.
            consumption = consumption.Where(c => Settings.ConsumptionAlerts && (DateTime.UtcNow - c.EventUtc).TotalMinutes <= 30).ToList();
            if (list.Count == 0 && consumption.Count == 0) { if (!SuggestConsumptionAlerts()) NotifyFeedback(); return; }
            Action open = () => { if (!flyout.IsVisible) ToggleFlyout(); };
            if (list.Count == 0)
            {
                var c = consumption[consumption.Count - 1];
                list.Add(Tuple.Create(c.Title, c.Text));
                open = () => OpenHistory();
                consumption.RemoveAt(consumption.Count - 1);
            }
            int others = list.Count - 1 + consumption.Count;
            var n = list[list.Count - 1];
            string text = others > 0 ? n.Item2 + "\n" + L.N(others, "+{0} other notice", "+{0} other notices") : n.Item2;
            balloonAction = open;
            tray.ShowBalloonTip(15000, n.Item1, text, Forms.ToolTipIcon.None);
        }

        /// <summary>Suggest the opt-in once, when at least one scope is ready. Returns whether a balloon was shown.</summary>
        private bool SuggestConsumptionAlerts()
        {
            if (Settings.ConsumptionAlerts || Settings.ConsumptionSuggested || !Settings.HistoryEnabled) return false;
            if (!Consumption.Readiness.Any(r => r.State == ReadinessState.Ready)) return false;
            Settings.ConsumptionSuggested = true;
            Settings.Save();
            balloonAction = () => OpenSettings("history");
            tray.ShowBalloonTip(15000, L.T("Unusual consumption alerts are available"),
                L.T("Your history is now enough to compare consumption with your usual range. Open Settings to turn on these notifications."), Forms.ToolTipIcon.None);
            return true;
        }

        private void NotifyFeedback()
        {
            if (!Settings.SessionFeedback || !Settings.HistoryEnabled) { pendingFeedback = null; return; }
            if (pendingFeedback == null || Native.UserIsBusyFullscreen()) return;
            string session = Interlocked.Exchange(ref pendingFeedback, null);
            if (session == null) return;
            balloonAction = () => { OpenHistory(); if (historyWindow != null) historyWindow.FocusSession(session); };
            tray.ShowBalloonTip(15000, L.T("Was the result worth this session's consumption?"),
                L.T("Rate the useful work obtained relative to the quota or money consumed.") + "\n" + L.T("Optional feedback · open to choose Yes, Partly or No."), Forms.ToolTipIcon.None);
        }

        /// <summary>Create a Start shortcut with the app identity so notifications have a name and icon.</summary>
        private static void EnsureShortcut()
        {
            string lnk = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "AI Meter.lnk");
            string target, args;
            Program.SplitLaunch(out target, out args);
            string icon = Program.IconPath;
            int comma = icon.LastIndexOf(',');
            Native.EnsureStartShortcut(lnk, target, args, Program.Home, icon.Substring(0, comma), 0, "AIMeter.App");
        }

        /// <summary>Notification icon file, taken from assets or extracted from the embedded resource.</summary>
        private static string NotificationIcon()
        {
            try
            {
                string f = Path.Combine(Program.Home, @"assets\app.ico");
                if (File.Exists(f)) return f;
                f = Path.Combine(Settings.Folder, "app.ico");
                if (!File.Exists(f))
                    using (var s = Program.OpenAsset("app.ico"))
                    {
                        if (s == null) return null;
                        Directory.CreateDirectory(Settings.Folder);
                        using (var o = File.Create(f)) s.CopyTo(o);
                    }
                return f;
            }
            catch (IOException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
        }

        /// <summary>
        /// On first launch, explain which data leaves this PC and how to disable network collection.
        /// Only enabled services' own usage endpoints are contacted.
        /// </summary>
        private void ShowPrivacyNotice()
        {
            balloonAction = OpenSettings;
            tray.ShowBalloonTip(20000, L.T("AI Meter is running"),
                L.T("It reads sessions and limits from local files. Only real-time Claude limits contact Anthropic with your Claude Code login: you can turn them off in Settings."),
                Forms.ToolTipIcon.Info);
        }

        public static string Version
        {
            get
            {
                var a = (System.Reflection.AssemblyInformationalVersionAttribute)Attribute.GetCustomAttribute(
                    typeof(Controller).Assembly, typeof(System.Reflection.AssemblyInformationalVersionAttribute));
                return a == null ? "" : a.InformationalVersion;
            }
        }

        private void UpdateTrayText(Snapshot s)
        {
            var parts = s.Providers.Where(p => p.Models == null).Select(p =>
            {
                var w = Ui.Primary(p);
                return w == null ? p.Name + " –" : p.Name + " " + Math.Round(p.Usage.Room(w)) + "%";
            });
            string text = "AI Meter · " + string.Join(" · ", parts);
            tray.Text = text.Length > 63 ? text.Substring(0, 63) : text;
        }

        public void ShowMenu(Window anchor)
        {
            var menu = new ContextMenu();
            menu.Items.Add(Item(L.T("Open panel"), "\uE8A7", () => ToggleFlyout()));
            if (!Program.Demo) menu.Items.Add(Item(L.T("Usage history"), "\uE9D9", OpenHistory));
            menu.Items.Add(new Separator());
            menu.Items.Add(Item(L.T("Desktop widget"), "\uE7F4", () => { Settings.DesktopWidget = !Settings.DesktopWidget; Settings.Save(); ApplySettings(); }, Settings.DesktopWidget));
            menu.Items.Add(Item(L.T("In the taskbar"), "\uE7C4", () => SetTaskbarDock(!Settings.TaskbarDock), Settings.TaskbarDock));
            menu.Items.Add(Item(L.T("Compact overlay"), "\uE718", () =>
            {
                Settings.Overlay = !Settings.Overlay;
                if (Settings.Overlay) Settings.TaskbarDock = false;
                Settings.Save(); ApplySettings();
            }, Settings.Overlay));
            if (anchor == desktop && desktop != null)
            {
                var ids = Last == null ? new string[0] : Last.Providers.Select(p => p.Id).ToArray();
                bool all = ids.Length > 0 && ids.All(IsExpanded);
                menu.Items.Add(Item(L.T("Expand sessions"), "\uE8C4", () =>
                {
                    foreach (var id in ids) SetExpanded(id, !all);
                    Settings.Save(); Rerender();
                }, all));
            }
            menu.Items.Add(new Separator());
            menu.Items.Add(Item(L.T("Refresh now"), "\uE72C", RefreshNow));
            menu.Items.Add(Item(L.T("Settings"), "\uE713", OpenSettings));
            menu.Items.Add(new Separator());
            menu.Items.Add(Item(L.T("Exit"), "\uE7E8", Exit));

            if (anchor != null)
            {
                menu.PlacementTarget = anchor;
                menu.Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint;
                menu.IsOpen = true;
                return;
            }

            // Tray menus need a foreground owner to dismiss when the user clicks elsewhere.
            if (menuHost == null)
            {
                menuHost = new Window { Width = 0, Height = 0, WindowStyle = WindowStyle.None, ShowInTaskbar = false, Topmost = true, AllowsTransparency = true, Background = Brushes.Transparent, ResizeMode = ResizeMode.NoResize };
                menuHost.SourceInitialized += (o, e) => Native.AddExStyle(new WindowInteropHelper(menuHost).Handle, Native.WS_EX_TOOLWINDOW, 0);
            }
            // Place the zero-width owner under the pointer so the menu uses that monitor's DPI.
            // An off-screen owner may select a different monitor's scale.
            var pos = Forms.Cursor.Position;
            var h = new WindowInteropHelper(menuHost).EnsureHandle();
            Native.SetWindowPos(h, IntPtr.Zero, pos.X, pos.Y, 0, 0, Native.SWP_NOSIZE | Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
            menuHost.Show();
            menuHost.Activate();
            Native.SetForegroundWindow(h);
            menu.PlacementTarget = menuHost;
            menu.Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint;
            menu.Closed += (o, e) => menuHost.Hide();
            menu.IsOpen = true;
        }

        private static MenuItem Item(string header, string glyph, Action click, bool? check = null)
        {
            var mi = new MenuItem { Header = header, Tag = glyph };
            if (check.HasValue) { mi.IsCheckable = true; mi.IsChecked = check.Value; }
            mi.Click += (o, e) => click();
            return mi;
        }

        public static ImageSource AppIconImage()
        {
            using (var s = Program.OpenAsset("app.ico"))
            {
                var dec = new IconBitmapDecoder(s, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
                return dec.Frames.OrderByDescending(f => f.PixelWidth).First();
            }
        }
    }
}
