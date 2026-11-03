using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;

namespace AIMeter
{
    /// <summary>Touchscreens: a tap on the taskbar pill opens the panel once, and content scrolls with a finger.</summary>
    internal static class TouchTests
    {
        internal static void Register(Action<string, Action> test)
        {
            test("Touch reaches WPF through Windows pointer messages, in the script launcher and in the executable", PointerSupport);
            test("A touch tap that hid the panel when it began does not reopen it, however long the tap lasts", ClosedByLongTap);
            test("Without a recent press, only a panel hidden in the last 300 ms counts as just closed", ClosedWithoutPress);
            test("Every scrolling area scrolls with a finger", ScrollAreasPan);
            test("A press and hold opens the tooltip of the nearest element that has one", HoldToolTip);
        }

        private static void PointerSupport()
        {
            // The older WISP stack did not bring taps and finger drags to WPF; both start-up paths must switch it off.
            string repository = Environment.GetEnvironmentVariable("AIMETER_REPOSITORY") ?? Environment.CurrentDirectory;
            const string Switch = "Switch.System.Windows.Input.Stylus.EnablePointerSupport";
            string entry = File.ReadAllText(Path.Combine(repository, "src", "Program.cs"));
            Assert(entry.Contains("AppContext.SetSwitch(\"" + Switch + "\", true)"), "The script launcher keeps the WISP touch stack.");
            string build = File.ReadAllText(Path.Combine(repository, "build.ps1"));
            Assert(Regex.IsMatch(build, "AppContextSwitchOverrides value=\"[^\"]*" + Regex.Escape(Switch) + "=true"), "The executable keeps the WISP touch stack.");
        }

        private static void ClosedByLongTap()
        {
            var pressed = new DateTime(2026, 11, 3, 12, 0, 0);
            // The press takes focus from the open panel, which hides at once; the tap ends 450 ms later.
            Assert(Controller.ClosedByThisPress(pressed.AddMilliseconds(10), pressed, pressed.AddMilliseconds(450)),
                "A slow tap reopened the panel it had just closed.");
            Assert(!Controller.ClosedByThisPress(pressed.AddSeconds(-5), pressed, pressed.AddMilliseconds(120)),
                "A panel closed before the press stopped the tap from opening it.");
            Assert(!Controller.ClosedByThisPress(pressed.AddMilliseconds(-100), pressed, pressed.AddMilliseconds(120)),
                "A panel closed just before the press stopped the tap from opening it.");
        }

        private static void ClosedWithoutPress()
        {
            var now = new DateTime(2026, 11, 3, 12, 0, 0);
            Assert(Controller.ClosedByThisPress(now.AddMilliseconds(-100), DateTime.MinValue, now), "A tray click that had just closed the panel reopened it.");
            Assert(!Controller.ClosedByThisPress(now.AddSeconds(-1), DateTime.MinValue, now), "A tray click could not open a panel closed a second ago.");
            // An old press (an accessibility tool's Invoke has none) must not block opening a panel closed since.
            Assert(!Controller.ClosedByThisPress(now.AddSeconds(-1), now.AddSeconds(-10), now), "A stale press blocked opening the panel.");
        }

        private static void ScrollAreasPan()
        {
            var scroll = Ui.TouchScroll(new ScrollViewer());
            Assert(scroll.PanningMode == PanningMode.VerticalOnly, "The helper did not turn on finger panning.");

            // A ScrollViewer ignores touch unless panning is on: each one in the app must go through the helper.
            string repository = Environment.GetEnvironmentVariable("AIMETER_REPOSITORY") ?? Environment.CurrentDirectory;
            var missing = Directory.GetFiles(Path.Combine(repository, "src"), "*.cs")
                .SelectMany(f => File.ReadAllLines(f).Select((line, i) => new { File = Path.GetFileName(f), Line = i + 1, Text = line }))
                .Where(l => Regex.IsMatch(l.Text, @"new\s+ScrollViewer\b") && !l.Text.Contains("TouchScroll("))
                .Select(l => l.File + ":" + l.Line).ToList();
            Assert(missing.Count == 0, "Scrolling areas without finger panning: " + string.Join(", ", missing));
        }

        private static void HoldToolTip()
        {
            var text = new TextBlock { Text = "73%" };
            var row = new Border { Child = text, ToolTip = "Weekly limit: resets on Monday" };
            new Border { Child = row, ToolTip = "Card" };
            Assert(Ui.ToolTipOwner(text) == row, "The touched text did not find its row's tooltip.");
            Assert(Ui.ToolTipOwner(new TextBlock()) == null, "An element without any tooltip found one.");
        }

        private static void Assert(bool condition, string message)
        {
            if (!condition) throw new Exception(message);
        }
    }
}
