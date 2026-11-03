using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace AIMeter
{
    /// <summary>Service logos on a PC without the Claude, Codex or Copilot apps: every service still draws something readable.</summary>
    internal static class LogoTests
    {
        internal static void Register(Action<string, Action> test)
        {
            test("Without installed apps every service shows a readable logo or monogram", NoInstalledApps);
            test("An unknown service gets its own initial, not another app's icon", UnknownService);
            test("Without the apps, Copilot and ChatGPT show their bundled official logos and Claude its initial", BundledLogos);
        }

        private static void BundledLogos()
        {
            // The assets live in the repository; the tests otherwise run from PowerShell's folder.
            string previous = Environment.GetEnvironmentVariable("AIMETER_HOME");
            ClearCache();
            Ui.NoInstalledApps = true;
            try
            {
                Environment.SetEnvironmentVariable("AIMETER_HOME", Environment.GetEnvironmentVariable("AIMETER_REPOSITORY") ?? Environment.CurrentDirectory);
                foreach (bool dark in new[] { true, false })
                {
                    foreach (string id in new[] { "openai", "copilot" })
                    {
                        var image = Ui.Logo(id, 16, dark) as Image;
                        Assert(image != null && image.Source != null, "The bundled " + id + " logo was not used (" + (dark ? "dark" : "light") + ").");
                    }
                    var claude = Ui.Logo("claude", 16, dark) as Border;
                    Assert(claude != null && ((TextBlock)claude.Child).Text == "C", "Claude did not fall back to its initial.");
                }
            }
            finally
            {
                Environment.SetEnvironmentVariable("AIMETER_HOME", previous);
                Ui.NoInstalledApps = false;
                ClearCache();
            }
        }

        private static void Assert(bool condition, string message)
        {
            if (!condition) throw new Exception(message);
        }

        private static void NoInstalledApps()
        {
            ClearCache();
            Ui.NoInstalledApps = true;
            try
            {
                foreach (bool dark in new[] { true, false })
                    foreach (string id in new[] { "claude", "openai", "copilot", "antigravity" })
                        AssertReadable(id, dark);
                var letters = new[] { "claude", "openai", "copilot" }.Select(Ui.MonogramText).ToList();
                Assert(letters.Distinct().Count() == letters.Count, "Two built-in services share a monogram: " + string.Join(", ", letters));
            }
            finally
            {
                Ui.NoInstalledApps = false;
                ClearCache();
            }
        }

        private static void UnknownService()
        {
            ClearCache();
            try
            {
                var logo = Ui.Logo("fixture", 16, true) as Border;
                Assert(logo != null && ((TextBlock)logo.Child).Text == "F", "An unknown service did not fall back to its initial.");
                AssertReadable("fixture", true);
            }
            finally { ClearCache(); }
        }

        /// <summary>The logo must be an image with a source or a monogram with real letters, and cover most of its square once rendered.</summary>
        private static void AssertReadable(string id, bool dark)
        {
            string where = id + (dark ? " (dark)" : " (light)");
            var logo = Ui.Logo(id, 16, dark);
            var image = logo as Image;
            var monogram = logo as Border;
            if (image != null) Assert(image.Source != null, "The " + where + " logo has no image.");
            else
            {
                var text = monogram == null ? null : monogram.Child as TextBlock;
                Assert(text != null && !string.IsNullOrWhiteSpace(text.Text) && text.Text != "?", "The " + where + " monogram is not readable.");
            }

            // Render with the same palette the taskbar pill uses: a missing brand colour would leave only white letters.
            var host = new Border { Child = logo };
            host.Resources.MergedDictionaries.Add(Theme.BuildPalette(dark));
            host.Measure(new Size(16, 16));
            host.Arrange(new Rect(0, 0, 16, 16));
            var bitmap = new RenderTargetBitmap(16, 16, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(host);
            var pixels = new byte[16 * 16 * 4];
            bitmap.CopyPixels(pixels, 16 * 4, 0);
            int opaque = 0;
            for (int i = 3; i < pixels.Length; i += 4) if (pixels[i] > 0) opaque++;
            Assert(opaque >= 128, "The " + where + " logo is almost empty when drawn (" + opaque + " of 256 pixels).");
        }

        private static void ClearCache()
        {
            ((IDictionary)typeof(Ui).GetField("Logos", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null)).Clear();
        }
    }
}
