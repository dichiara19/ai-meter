using System;
using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace AIMeter
{
    /// <summary>
    /// The desktop widget belongs to the desktop: "Show desktop" (Win + D) raises the window holding the desktop
    /// icons above every other, and the widget must go with it instead of staying at the bottom of the z-order.
    /// It must also stay a normal window: an always-on-top one would show over full-screen apps.
    /// Small windows outside the screen stand for the desktop, the apps and the widget: visible, since hidden windows
    /// do not count in what is seen, and normal ones, as the real ones are, stacked among the real windows.
    /// </summary>
    internal static class DesktopWidgetTests
    {
        internal static void Register(Action<string, Action> test)
        {
            test("The widget goes just above a desktop raised over it, and back down when the desktop is lowered", FollowsDesktop);
            test("A widget just above a raised desktop, beneath the always-on-top windows, stays a normal window", NeverAlwaysOnTop);
            test("A widget that Windows made always on top is made a normal window again", DemotedWhenPromoted);
            test("A click on the widget leaves it just above the desktop, beneath the apps", ClickStaysOnDesktop);
            test("Hidden windows between the widget and the desktop, as its own owner, do not move it", HiddenBetween);
            test("Without File Explorer the widget stays at the bottom", NoDesktop);
        }

        private static void Assert(bool condition, string message) { if (!condition) throw new Exception(message); }

        [DllImport("user32.dll")] private static extern IntPtr GetTopWindow(IntPtr parent);
        [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr h, uint cmd);
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr h);
        private const uint Next = 2, Previous = 3;   // GW_HWNDNEXT, GW_HWNDPREV
        private const uint Keep = Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE;

        private static HwndSource Window(string name, IntPtr owner, bool alwaysOnTop, bool visible)
        {
            return new HwndSource(new HwndSourceParameters(name)
            {
                WindowStyle = Native.WS_POPUP | (visible ? Native.WS_VISIBLE : 0), PositionX = -32000, PositionY = -32000, Width = 10, Height = 10, ParentWindow = owner,
                ExtendedWindowStyle = Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE | (alwaysOnTop ? Native.WS_EX_TOPMOST : 0),
            });
        }

        private static HwndSource Normal(string name) { return Window(name, IntPtr.Zero, false, true); }

        private static void Insert(HwndSource window, IntPtr after) { Native.SetWindowPos(window.Handle, after, 0, 0, 0, 0, Keep); }

        /// <summary>Stacks normal windows one right beneath the other, from the first, on top, to the last.</summary>
        private static void Stack(params HwndSource[] windows)
        {
            Insert(windows[0], IntPtr.Zero);   // HWND_TOP: among the normal windows, wherever Windows lets it be.
            for (int i = 1; i < windows.Length; i++) Insert(windows[i], windows[i - 1].Handle);
        }

        /// <summary>The nearest visible window above the given one: Windows moves hidden ones of its own between the others.</summary>
        private static IntPtr Above(IntPtr window)
        {
            IntPtr above = GetWindow(window, Previous);
            while (above != IntPtr.Zero && !IsWindowVisible(above)) above = GetWindow(above, Previous);
            return above;
        }

        /// <summary>The lowest always-on-top window of all: they are all above the normal ones.</summary>
        private static IntPtr LowestOnTop()
        {
            IntPtr lowest = IntPtr.Zero;
            for (IntPtr h = GetTopWindow(IntPtr.Zero); h != IntPtr.Zero && Native.IsTopmost(h); h = GetWindow(h, Next)) lowest = h;
            return lowest;
        }

        /// <summary>What the widget does with a request to change its position, here one that brings it to the top.</summary>
        private static Native.WINDOWPOS Asked(IntPtr widget, IntPtr iconsHost)
        {
            var pos = new Native.WINDOWPOS { hwnd = widget, hwndInsertAfter = IntPtr.Zero, flags = Keep };
            DesktopWidget.KeepOnDesktop(widget, iconsHost, ref pos);
            return pos;
        }

        /// <summary>Moves the widget as it moves itself: where KeepOnDesktop sends a request to change its position.</summary>
        private static void Move(IntPtr widget, IntPtr iconsHost)
        {
            var pos = Asked(widget, iconsHost);
            if ((pos.flags & Native.SWP_NOZORDER) == 0) Native.SetWindowPos(widget, pos.hwndInsertAfter, 0, 0, 0, 0, Keep);
        }

        private static void FollowsDesktop()
        {
            using (var app = Normal("app"))
            using (var desktop = Normal("desktop"))
            using (var widget = Normal("widget"))
            {
                Stack(desktop, app, widget);   // Win + D: the desktop is over the apps and the widget.
                IntPtr insertAfter;
                Assert(Native.SlotJustAbove(widget.Handle, desktop.Handle, out insertAfter), "A widget beneath the raised desktop is left there.");
                Move(widget.Handle, desktop.Handle);
                Assert(Above(desktop.Handle) == widget.Handle, "The widget is not just above the raised desktop.");
                Assert(!Native.SlotJustAbove(widget.Handle, desktop.Handle, out insertAfter), "A widget already just above the desktop is moved.");

                Stack(widget, app, desktop);   // The apps are back: the desktop is beneath them again.
                Assert(Native.SlotJustAbove(widget.Handle, desktop.Handle, out insertAfter), "The widget stays over the apps when the desktop is lowered.");
                Assert(insertAfter == app.Handle, "The widget does not go back beneath the apps.");
                Move(widget.Handle, desktop.Handle);
                Assert(Above(desktop.Handle) == widget.Handle && Above(widget.Handle) == app.Handle, "The widget is not between the apps and the desktop.");
            }
        }

        private static void NeverAlwaysOnTop()
        {
            // The raised desktop is the highest normal window, right beneath the always-on-top ones, as the taskbar
            // is. A normal window inserted after an always-on-top window that has another beneath it becomes always
            // on top itself, and would then show over every app, full-screen ones too: it stays normal only after
            // the lowest of them. Here the nearest visible window above the desktop is always on top and has a
            // hidden one beneath it, as real desktops have: the widget must go after the hidden one.
            using (var bar = Window("always on top", IntPtr.Zero, true, true))
            using (var veil = Window("always on top, hidden, lowest", IntPtr.Zero, true, false))
            using (var desktop = Normal("desktop"))
            using (var widget = Normal("widget"))
            {
                Insert(veil, LowestOnTop());
                Insert(bar, GetWindow(veil.Handle, Previous));
                Insert(desktop, veil.Handle);
                Insert(widget, desktop.Handle);
                Assert(Native.IsTopmost(bar.Handle) && Native.IsTopmost(veil.Handle) && GetWindow(bar.Handle, Next) == veil.Handle && LowestOnTop() == veil.Handle,
                    "The fixture did not put a visible always-on-top window over a hidden one, the lowest of all.");
                Assert(!Native.IsTopmost(desktop.Handle) && Above(desktop.Handle) == bar.Handle, "The fixture did not put a normal desktop right beneath the always-on-top windows.");

                IntPtr insertAfter;
                Assert(Native.SlotJustAbove(widget.Handle, desktop.Handle, out insertAfter), "A widget beneath the raised desktop is left there.");
                Assert(insertAfter == veil.Handle, "The widget's place is not after the lowest always-on-top window.");
                Move(widget.Handle, desktop.Handle);
                Assert(Above(desktop.Handle) == widget.Handle && Above(widget.Handle) == bar.Handle, "The widget is not just above the raised desktop.");
                Assert(!Native.IsTopmost(widget.Handle), "The widget became always on top: it would show over full-screen apps.");

                // What the rule avoids: after the visible one, which has the hidden one beneath it, Windows promotes the window.
                Insert(widget, bar.Handle);
                Assert(Native.IsTopmost(widget.Handle), "Windows no longer promotes a window inserted between always-on-top ones: the rule can be simpler.");
                Move(widget.Handle, desktop.Handle);
                Assert(!Native.IsTopmost(widget.Handle), "A promoted widget is not made a normal window again.");
            }
        }

        private static void DemotedWhenPromoted()
        {
            using (var desktop = Normal("desktop"))
            using (var widget = Window("widget", IntPtr.Zero, true, true))   // As Windows leaves a window inserted between always-on-top ones.
            {
                Stack(desktop);
                Assert(Native.IsTopmost(widget.Handle), "The fixture did not make the widget always on top.");

                Assert(Asked(widget.Handle, desktop.Handle).hwndInsertAfter == Native.HWND_NOTOPMOST, "An always-on-top widget is not made a normal window.");
                Move(widget.Handle, desktop.Handle);
                Assert(!Native.IsTopmost(widget.Handle), "The widget is still always on top.");
                Move(widget.Handle, desktop.Handle);
                Assert(Above(desktop.Handle) == widget.Handle && !Native.IsTopmost(widget.Handle), "The demoted widget is not just above the desktop as a normal window.");
            }
        }

        private static void ClickStaysOnDesktop()
        {
            using (var app = Normal("app"))
            using (var widget = Normal("widget"))
            using (var desktop = Normal("desktop"))
            {
                Stack(app, widget, desktop);
                var pos = Asked(widget.Handle, desktop.Handle);
                Assert((pos.flags & Native.SWP_NOZORDER) != 0, "A widget just above the desktop is raised when its position changes.");

                Stack(widget, app, desktop);
                pos = Asked(widget.Handle, desktop.Handle);
                Assert((pos.flags & Native.SWP_NOZORDER) == 0 && pos.hwndInsertAfter == app.Handle, "A widget over an app does not go back just above the desktop.");
            }
        }

        private static void HiddenBetween()
        {
            // WPF gives a window that is not on the taskbar a hidden owner, which Windows keeps beneath it.
            using (var app = Normal("app"))
            using (var owner = Window("owner", IntPtr.Zero, false, false))
            using (var widget = Window("widget", owner.Handle, false, true))
            using (var desktop = Normal("desktop"))
            {
                Stack(app, widget, owner, desktop);
                IntPtr insertAfter;
                Assert(GetWindow(widget.Handle, Next) != desktop.Handle, "The fixture did not put a hidden window between the widget and the desktop.");
                Assert(!Native.SlotJustAbove(widget.Handle, desktop.Handle, out insertAfter), "A hidden window beneath the widget counts as a window between it and the desktop.");
            }
        }

        private static void NoDesktop()
        {
            using (var widget = Normal("widget"))
            {
                var pos = Asked(widget.Handle, IntPtr.Zero);
                Assert((pos.flags & Native.SWP_NOZORDER) == 0 && pos.hwndInsertAfter == Native.HWND_BOTTOM, "Without the desktop's window the widget does not stay at the bottom.");
            }
        }
    }
}
