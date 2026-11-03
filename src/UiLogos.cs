using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shell;
using Microsoft.Win32;

namespace AIMeter
{
    internal partial class Ui
    {
        private static readonly Dictionary<string, BitmapSource> Logos = new Dictionary<string, BitmapSource>();

        /// <summary>
        /// Resolve the service logo from an installed app or a bundled provider asset.
        /// Fall back to a monogram in the service's colour when no image is available.
        /// </summary>
        public static FrameworkElement Logo(string id, double size) { return Logo(id, size, Theme.Dark); }

        public static FrameworkElement Logo(string id, double size, bool dark)
        {
            string key = id + (dark ? ":dark" : ":light");
            BitmapSource bmp;
            if (!Logos.TryGetValue(key, out bmp))
            {
                // Prefer the installed app's icon, then the bundled official logo, then the monogram.
                // Antigravity CLI has no icon; API services bundle theirs beside the definition.
                string file = id == "antigravity" ? null : FindInstalledLogo(id, dark);
                if (file != null && file.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                    bmp = IconFromExe(file);
                else if (file != null)
                {
                    try { bmp = FromStream(File.OpenRead(file)); }
                    catch (Exception) { bmp = null; }
                }
                if (bmp == null)
                {
                    var def = Definitions.Get(id);
                    var bundled = BundledLogo(id, dark);
                    bmp = FromStream(bundled ?? (def != null ? def.LogoImage(dark) : null));
                    // OpenAI's Blossom file carries its clear space, a quarter of each side: at 16 pixels the symbol
                    // would be half the size of the other logos. The space around pill and card icons keeps it clear.
                    if (bundled != null && id == "openai" && bmp != null) bmp = Inset(bmp, 0.22);
                }
                Logos[key] = bmp;
            }
            if (bmp == null) return Monogram(id, size);
            var img = new Image { Source = bmp, Width = size, Height = size, VerticalAlignment = VerticalAlignment.Center };
            RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
            // Round square web logos to match Windows app icons.
            img.Clip = new RectangleGeometry(new Rect(0, 0, size, size), size * 0.22, size * 0.22);
            return img;
        }

        /// <summary>
        /// The official logo bundled for a built-in service whose app may be missing, black for the light theme and white
        /// for the dark one: Antigravity's, GitHub's Copilot icon and OpenAI's Blossom (for ChatGPT and Codex), used as
        /// published, only to identify the service. Claude has none: Anthropic allows its logo only with permission, so
        /// without the Claude app the monogram stands in.
        /// </summary>
        private static Stream BundledLogo(string id, bool dark)
        {
            if (id == "antigravity") return Program.OpenAsset("antigravity.png");
            if (id == "openai" || id == "copilot") return Program.OpenAsset(id + (dark ? "-dark" : "") + ".png");
            return null;
        }

        /// <summary>The image without the given share of empty margin on each side.</summary>
        private static BitmapSource Inset(BitmapSource bmp, double share)
        {
            int x = (int)(bmp.PixelWidth * share), y = (int)(bmp.PixelHeight * share);
            var cropped = new CroppedBitmap(bmp, new Int32Rect(x, y, bmp.PixelWidth - 2 * x, bmp.PixelHeight - 2 * y));
            cropped.Freeze();
            return cropped;
        }

        /// <summary>Decode a resource or file stream at 64 pixels; return null for missing or invalid images.</summary>
        private static BitmapSource FromStream(Stream s)
        {
            if (s == null) return null;
            try
            {
                using (s)
                {
                    var bi = new BitmapImage();
                    bi.BeginInit();
                    bi.StreamSource = s;
                    bi.DecodePixelWidth = 64;   // Enough for 16-32 logical pixels at common DPI settings without excessive memory.
                    bi.CacheOption = BitmapCacheOption.OnLoad;
                    bi.EndInit();
                    bi.Freeze();
                    return bi;
                }
            }
            catch (Exception) { return null; }
        }

        /// <summary>Extract an installed executable's main icon when no logo image is available.</summary>
        private static BitmapSource IconFromExe(string exe)
        {
            try
            {
                using (var ico = System.Drawing.Icon.ExtractAssociatedIcon(exe))
                {
                    if (ico == null) return null;
                    var src = Imaging.CreateBitmapSourceFromHIcon(ico.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                    src.Freeze();
                    return src;
                }
            }
            catch (Exception) { return null; }
        }

        /// <summary>
        /// Draw the service's letters on its brand colour, used whenever no image is available (for example when
        /// the Claude app is not installed, or a bundled logo cannot be read). An id without a brand colour in the palette gets a neutral
        /// background, since a missing resource would leave only white letters.
        /// </summary>
        private static FrameworkElement Monogram(string id, double size)
        {
            string text = MonogramText(id);
            var b = new Border { Width = size, Height = size, CornerRadius = new CornerRadius(size * 0.24), VerticalAlignment = VerticalAlignment.Center };
            bool hasBrand = new[] { "claude", "openai", "copilot", "antigravity" }.Contains(id) || Definitions.Get(id) != null;
            if (hasBrand) b.SetResourceReference(Border.BackgroundProperty, BrandBrush(id));
            else b.Background = Brushes.SlateGray;
            b.Child = new TextBlock
            {
                Text = text, Foreground = Brushes.White, FontWeight = FontWeights.Bold, FontSize = size * (text.Length > 1 ? 0.44 : 0.58),
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            };
            return b;
        }

        /// <summary>Letters for a service without an image: the built-in services' initials, a definition's monogram, else the id's initial.</summary>
        internal static string MonogramText(string id)
        {
            switch (id)
            {
                case "claude": return "C";
                case "openai": return "G";
                case "copilot": return "GH";   // "C" and "G" already stand for Claude and ChatGPT.
                case "antigravity": return "A";
            }
            var def = Definitions.Get(id);
            if (def != null && !string.IsNullOrEmpty(def.Monogram)) return def.Monogram;
            return string.IsNullOrEmpty(id) ? "?" : id.Substring(0, 1).ToUpperInvariant();
        }

#if AIMETER_TEST
        /// <summary>Test seam: when set, behave as on a PC without Claude, Codex, Copilot or any definition's app.</summary>
        internal static bool NoInstalledApps;
#endif

        /// <summary>Find the icon in an installed app package registered for the current user.</summary>
        private static string FindInstalledLogo(string id, bool dark)
        {
#if AIMETER_TEST
            if (NoInstalledApps) return null;
#endif
            if (id == "copilot") return FindCopilotLogo();
            var def = Definitions.Get(id);
            if (def != null) return def.LogoFile();
            // Only Claude and ChatGPT/Codex ship as app packages; any other id has no package to search.
            string[] prefixes = id == "openai" ? new[] { "OpenAI.ChatGPT", "OpenAI.Codex_" } : id == "claude" ? new[] { "Claude_" } : null;
            if (prefixes == null) return null;
            string[] names = id == "openai" && !dark
                ? new[] { "Square44x44Logo.targetsize-256_altform-lightunplated.png", "Square44x44Logo.targetsize-256_altform-unplated.png" }
                : new[] { "Square44x44Logo.targetsize-256_altform-unplated.png", "Square44x44Logo.targetsize-256_altform-lightunplated.png" };
            try
            {
                using (var repo = Registry.CurrentUser.OpenSubKey(@"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\Repository\Packages"))
                {
                    if (repo == null) return null;
                    foreach (var pkg in repo.GetSubKeyNames().Where(n => prefixes.Any(p => n.StartsWith(p, StringComparison.OrdinalIgnoreCase))).OrderByDescending(n => n))
                    {
                        string root;
                        using (var k = repo.OpenSubKey(pkg)) root = k == null ? null : k.GetValue("PackageRootFolder") as string;
                        if (root == null) continue;
                        foreach (var dir in new[] { "assets", "Assets", "images", "Images" })
                            foreach (var name in names)
                            {
                                string f = Path.Combine(root, dir, name);
                                if (File.Exists(f)) return f;
                            }
                    }
                }
            }
            catch (Exception) { }
            return null;
        }

        /// <summary>Load Copilot's official icon from its VS Code extension.</summary>
        private static string FindCopilotLogo()
        {
            foreach (var baseDir in new[] { Path.Combine(Probe.LocalAppData, @"Programs\Microsoft VS Code"), Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Microsoft VS Code") })
            {
                try
                {
                    if (!Directory.Exists(baseDir)) continue;
                    foreach (var dir in new[] { baseDir }.Concat(Directory.GetDirectories(baseDir)))
                    {
                        string f = Path.Combine(dir, @"resources\app\extensions\copilot\assets\copilot.png");
                        if (File.Exists(f)) return f;
                    }
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            return null;
        }

        public static string BrandBrush(string id) { return id == "openai" ? "OpenAI" : id == "copilot" ? "Copilot" : id == "claude" ? "Claude" : id == "antigravity" ? "Antigravity" : "Brand." + id; }
    }
}
