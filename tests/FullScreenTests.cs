using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Windows.Interop;

namespace AIMeter
{
    /// <summary>Full-screen apps: the taskbar pill, always on top with the taskbar, must step aside like the taskbar.</summary>
    internal static class FullScreenTests
    {
        internal static void Register(Action<string, Action> test)
        {
            test("Only a window without a title bar that reaches every edge of the monitor is full screen", CoversMonitor);
            test("The desktop and the taskbars in the foreground are never a full-screen app", ShellSurfaces);
            test("The taskbar pill hides when covered and is never raised in the z-order when it moves", PillStaysBehind);
            test("A pill the taskbar was raised over goes back just above it, still beneath what covers the taskbar", PillBackAboveTaskbar);
        }

        private static void Assert(bool condition, string message) { if (!condition) throw new Exception(message); }

        private static Native.RECT Rect(int left, int top, int right, int bottom) { return new Native.RECT { Left = left, Top = top, Right = right, Bottom = bottom }; }

        private static void CoversMonitor()
        {
            var monitor = Rect(0, 0, 1920, 1080);
            Assert(Native.CoversMonitor(Rect(0, 0, 1920, 1080), monitor, false), "A borderless window over the whole monitor (video, game) is not full screen.");
            Assert(Native.CoversMonitor(Rect(-1, -1, 1921, 1081), monitor, false), "A borderless window slightly larger than the monitor is not full screen.");
            // A maximised window reaches past the edges by its border, over an auto-hiding taskbar too, but keeps its title bar.
            Assert(!Native.CoversMonitor(Rect(-8, -8, 1928, 1088), monitor, true), "A maximised window counts as full screen.");
            Assert(!Native.CoversMonitor(Rect(0, 0, 1920, 1032), monitor, false), "A borderless window above the taskbar counts as full screen.");
            // On a second monitor to the right, the first monitor's coordinates are not covered.
            Assert(!Native.CoversMonitor(Rect(1920, 0, 3840, 1080), monitor, false), "A window on another monitor counts as full screen on this one.");
        }

        private static void ShellSurfaces()
        {
            foreach (var name in new[] { "Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd" })
                Assert(Native.IsShellSurface(name), name + " in the foreground hides the pill.");
            Assert(!Native.IsShellSurface("Chrome_WidgetWin_1"), "A browser in full screen is treated as the desktop.");
        }

        private static void PillStaysBehind()
        {
            string repository = Environment.GetEnvironmentVariable("AIMETER_REPOSITORY") ?? Environment.CurrentDirectory;
            string pill = File.ReadAllText(Path.Combine(repository, "src", "Taskbar.cs"));
            // Every move of the pill keeps its place in the z-order: without SWP_NOZORDER it rises above a full-screen app.
            var moves = Regex.Matches(pill, @"SetWindowPos\(source\.Handle[^;]*;");
            Assert(moves.Count > 0, "The pill's placement was not found.");
            foreach (Match m in moves) Assert(m.Value.Contains("SWP_NOZORDER"), "The pill is raised in the z-order when it moves: " + m.Value);
            Assert(Regex.IsMatch(pill, @"if \(covered\)\s*\{\s*Native\.ShowWindow\(source\.Handle, 0\); return; \}"), "Placing the pill shows it over a full-screen app.");
        }

        [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr h, uint cmd);
        private const uint Above = 3;   // GW_HWNDPREV

        private static HwndSource Hidden(string name)
        {
            return new HwndSource(new HwndSourceParameters(name)
            {
                WindowStyle = Native.WS_POPUP, Width = 10, Height = 10,
                ExtendedWindowStyle = Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE | 0x8 /* WS_EX_TOPMOST */,
            });
        }

        private static void PillBackAboveTaskbar()
        {
            // Hidden always-on-top windows standing for a window over the taskbar, the taskbar and the pill.
            using (var cover = Hidden("cover"))
            using (var bar = Hidden("taskbar"))
            using (var pill = Hidden("pill"))
            {
                const uint Keep = Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE;
                Native.SetWindowPos(cover.Handle, Native.HWND_TOPMOST, 0, 0, 0, 0, Keep);
                Native.SetWindowPos(bar.Handle, cover.Handle, 0, 0, 0, 0, Keep);
                Native.SetWindowPos(pill.Handle, bar.Handle, 0, 0, 0, 0, Keep);   // File Explorer raised the taskbar over the pill.
                Assert(GetWindow(pill.Handle, Above) == bar.Handle, "The fixture did not put the taskbar over the pill.");

                Assert(Native.KeepJustAbove(pill.Handle, bar.Handle), "The pill beneath the taskbar was left there.");
                Assert(GetWindow(bar.Handle, Above) == pill.Handle, "The pill is not just above the taskbar.");
                Assert(GetWindow(pill.Handle, Above) == cover.Handle, "The pill went above the window over the taskbar.");
                Assert(!Native.KeepJustAbove(pill.Handle, bar.Handle), "A pill already above the taskbar was moved.");
            }
        }
    }
}
