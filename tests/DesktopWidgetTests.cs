using System;
using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace AIMeter
{
    /// <summary>
    /// The desktop widget belongs to the desktop: "Show desktop" (Win + D) raises the window holding the desktop
    /// icons above every other, and the widget must go with it instead of staying at the bottom of the z-order.
    /// Hidden always-on-top windows stand for the desktop, the apps and the widget, in a z-order of their own.
    /// </summary>
    internal static class DesktopWidgetTests
    {
        internal static void Register(Action<string, Action> test)
        {
            test("The widget goes just above a desktop raised over it, and back down when the desktop is lowered", FollowsDesktop);
            test("A click on the widget leaves it just above the desktop, beneath the apps", ClickStaysOnDesktop);
            test("The widget's own owner between it and the desktop does not move it", OwnerBetween);
            test("Without File Explorer the widget stays at the bottom", NoDesktop);
        }

        private static void Assert(bool condition, string message) { if (!condition) throw new Exception(message); }

        [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr h, uint cmd);
        private const uint Above = 3;   // GW_HWNDPREV
        private const uint Keep = Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE;

        private static HwndSource Hidden(string name, IntPtr owner)
        {
            return new HwndSource(new HwndSourceParameters(name)
            {
                WindowStyle = Native.WS_POPUP, Width = 10, Height = 10, ParentWindow = owner,
                ExtendedWindowStyle = Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE | 0x8 /* WS_EX_TOPMOST */,
            });
        }

        /// <summary>Stacks the windows from the first, on top, to the last.</summary>
        private static void Stack(params HwndSource[] windows)
        {
            Native.SetWindowPos(windows[0].Handle, Native.HWND_TOPMOST, 0, 0, 0, 0, Keep);
            for (int i = 1; i < windows.Length; i++) Native.SetWindowPos(windows[i].Handle, windows[i - 1].Handle, 0, 0, 0, 0, Keep);
        }

        /// <summary>What the widget does with a request to change its position, here one that brings it to the top.</summary>
        private static Native.WINDOWPOS Asked(IntPtr widget, IntPtr iconsHost)
        {
            var pos = new Native.WINDOWPOS { hwnd = widget, hwndInsertAfter = IntPtr.Zero, flags = Keep };
            DesktopWidget.KeepOnDesktop(widget, iconsHost, ref pos);
            return pos;
        }

        private static void FollowsDesktop()
        {
            using (var cover = Hidden("always on top", IntPtr.Zero))
            using (var app = Hidden("app", IntPtr.Zero))
            using (var desktop = Hidden("desktop", IntPtr.Zero))
            using (var widget = Hidden("widget", IntPtr.Zero))
            {
                Stack(cover, desktop, app, widget);   // Win + D: the desktop is over the apps and the widget.
                IntPtr insertAfter;
                Assert(Native.SlotJustAbove(widget.Handle, desktop.Handle, out insertAfter), "A widget beneath the raised desktop is left there.");
                Assert(insertAfter == cover.Handle, "The widget does not go just above the raised desktop.");
                Native.SetWindowPos(widget.Handle, insertAfter, 0, 0, 0, 0, Keep);
                Assert(GetWindow(desktop.Handle, Above) == widget.Handle && GetWindow(widget.Handle, Above) == cover.Handle, "The widget is not between the desktop and what is above it.");
                Assert(!Native.SlotJustAbove(widget.Handle, desktop.Handle, out insertAfter), "A widget already just above the desktop is moved.");

                Stack(cover, widget, app, desktop);   // The apps are back: the desktop is at the bottom again.
                Assert(Native.SlotJustAbove(widget.Handle, desktop.Handle, out insertAfter), "The widget stays over the apps when the desktop is lowered.");
                Assert(insertAfter == app.Handle, "The widget does not go back beneath the apps.");
            }
        }

        private static void ClickStaysOnDesktop()
        {
            using (var app = Hidden("app", IntPtr.Zero))
            using (var widget = Hidden("widget", IntPtr.Zero))
            using (var desktop = Hidden("desktop", IntPtr.Zero))
            {
                Stack(app, widget, desktop);
                var pos = Asked(widget.Handle, desktop.Handle);
                Assert((pos.flags & Native.SWP_NOZORDER) != 0, "A widget just above the desktop is raised when its position changes.");

                Stack(widget, app, desktop);
                pos = Asked(widget.Handle, desktop.Handle);
                Assert((pos.flags & Native.SWP_NOZORDER) == 0 && pos.hwndInsertAfter == app.Handle, "A widget over an app does not go back just above the desktop.");
            }
        }

        private static void OwnerBetween()
        {
            // WPF gives a window that is not on the taskbar a hidden owner, which Windows keeps beneath it.
            using (var app = Hidden("app", IntPtr.Zero))
            using (var owner = Hidden("owner", IntPtr.Zero))
            using (var widget = Hidden("widget", owner.Handle))
            using (var desktop = Hidden("desktop", IntPtr.Zero))
            {
                Stack(app, widget, owner, desktop);
                IntPtr insertAfter;
                Assert(GetWindow(desktop.Handle, Above) == owner.Handle, "The fixture did not put the owner between the widget and the desktop.");
                Assert(!Native.SlotJustAbove(widget.Handle, desktop.Handle, out insertAfter), "The widget's owner beneath it counts as a window between it and the desktop.");
            }
        }

        private static void NoDesktop()
        {
            using (var widget = Hidden("widget", IntPtr.Zero))
            {
                var pos = Asked(widget.Handle, IntPtr.Zero);
                Assert((pos.flags & Native.SWP_NOZORDER) == 0 && pos.hwndInsertAfter == Native.HWND_BOTTOM, "Without the desktop's window the widget does not stay at the bottom.");
            }
        }
    }
}
