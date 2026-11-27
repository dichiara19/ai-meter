using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;

namespace AIMeter
{
    /// <summary>The user chooses which services the taskbar pill and the overlay show; the others stay everywhere else (#111).</summary>
    internal static class PillTests
    {
        internal static void Register(Action<string, Action> test)
        {
            test("A service taken out of the pill leaves the pill and the overlay icon, and stays in the tooltip and the panel", HiddenService);
            test("With every service taken out, the pill shows the app's name and stays clickable", EveryServiceHidden);
            test("Local models leave the pill with the same choice", HiddenLocalModels);
            test("The pill choice survives a restart, and settings saved before it existed show every service", SavedChoice);
            test("Every supported language words the pill choice", Translations);
        }

        private static void Assert(bool condition, string message) { if (!condition) throw new Exception(message); }

        private static ProviderState Service(string id, string name, double used)
        {
            var p = new ProviderState { Id = id, Name = name, Usage = new Usage { Source = "live", At = DateTime.Now } };
            p.Usage.Windows.Add(new UsageWindow { Label = "Session 5 h", Short = UsageWindow.ShortFiveHours, Used = used });
            return p;
        }

        private static Snapshot Snapshot(params ProviderState[] providers)
        {
            var s = new Snapshot { Taken = DateTime.Now };
            s.Providers.AddRange(providers);
            return s;
        }

        private static void HiddenService()
        {
            var snapshot = Snapshot(Service("claude", "Claude", 82), Service("openai", "ChatGPT", 40), Service("copilot", "Copilot", 10));
            var settings = Settings.Current;

            // No choice made, or no dictionary at all in an older settings object: every service is shown.
            Assert(Texts(Ui.PillRow(snapshot, "5h", true)).SequenceEqual(new[] { "18%", "60%", "90%" }), "The pill does not show every service by default.");
            settings.PillHidden = null;
            Assert(Ui.PillServices(snapshot).Count() == 3, "A settings object without the choice hid a service.");

            settings.PillHidden = new Dictionary<string, bool> { { "openai", true }, { "copilot", false } };
            Assert(Texts(Ui.PillRow(snapshot, "5h", true)).SequenceEqual(new[] { "18%", "90%" }), "A hidden service stayed in the pill, or a shown one left it.");
            Assert(Ui.PillServices(snapshot).Select(p => p.Id).SequenceEqual(new[] { "claude", "copilot" }), "The overlay icon does not follow the pill.");
            Assert(Ui.Summary(snapshot).Contains("ChatGPT: 60% left"), "A service hidden from the pill left the tooltip.");
            Assert(snapshot.Shown.Count == 3, "A service hidden from the pill left the panel and the widget.");
        }

        private static void EveryServiceHidden()
        {
            var snapshot = Snapshot(Service("claude", "Claude", 82));
            Settings.Current.PillHidden["claude"] = true;
            Assert(Texts(Ui.PillRow(snapshot, "5h", true)).SequenceEqual(new[] { "AI Meter" }), "An empty pill does not show the app's name.");
        }

        private static void HiddenLocalModels()
        {
            var local = new ProviderState { Id = "local", Name = "Local models", Running = true, Models = new List<LocalModel> { new LocalModel { Name = "qwen", Server = "Ollama" } } };
            var snapshot = Snapshot(Service("claude", "Claude", 82), local);
            Assert(Ui.PillRow(snapshot, "5h", true).Children.OfType<Border>().Any(b => b.Child != null), "Local models are not in the pill by default.");
            Settings.Current.PillHidden["local"] = true;
            Assert(Texts(Ui.PillRow(snapshot, "5h", true)).SequenceEqual(new[] { "18%" }), "Hidden local models stayed in the pill.");
        }

        private static void SavedChoice()
        {
            var saved = new Settings();
            saved.PillHidden["openrouter"] = true;
            Assert(saved.Save(), "The settings were not saved.");
            var loaded = Settings.Load();
            Assert(loaded.HiddenFromPill("openrouter") && !loaded.HiddenFromPill("claude"), "The pill choice did not survive a restart.");

            File.WriteAllText(Path.Combine(Settings.Folder, "settings.json"), "{\"FirstRunDone\":true}", new UTF8Encoding(false));
            var earlier = Settings.Load();
            Assert(earlier.FirstRunDone && earlier.PillHidden != null && earlier.PillHidden.Count == 0, "Settings saved before the choice existed do not show every service.");
        }

        private static void Translations()
        {
            var keys = new[]
            {
                "Show in the taskbar",
                "Turn it off to keep this service out of the pill and the overlay. It stays in the panel, the desktop widget and the pill's tooltip.",
                "With many services the pill can reach the app icons. Each service's page under Services has a switch to keep it out of the pill.",
            };
            try
            {
                foreach (var lang in new[] { "it", "es", "fr", "de", "pt" })
                {
                    L.Init(lang);
                    foreach (var key in keys) Assert(L.T(key) != key, "Missing translation for " + lang + ": " + key);
                    // The hint links the Services page only where the translation names it as the navigation does.
                    Assert(L.T(keys[2]).IndexOf(L.T("Services"), StringComparison.Ordinal) >= 0, "The hint does not name the Services page in " + lang + ".");
                }
            }
            finally { L.Init("en"); }
        }

        /// <summary>The text of every TextBlock in a control tree.</summary>
        private static List<string> Texts(DependencyObject root)
        {
            var list = new List<string>();
            var text = root as TextBlock;
            if (text != null) list.Add(text.Text);
            foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>()) list.AddRange(Texts(child));
            return list;
        }
    }
}
