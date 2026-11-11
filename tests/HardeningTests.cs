using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace AIMeter
{
    /// <summary>
    /// Fixes from the triage of Codacy's security findings (November 2026): settings that cannot be read are never
    /// replaced by defaults, values from outside choose no launcher target, and patterns from definitions cannot hang.
    /// </summary>
    internal static class HardeningTests
    {
        internal static void Register(Action<string, Action> test)
        {
            test("Settings that are not valid are kept aside, and defaults ask the welcome questions again", InvalidSettings);
            test("Settings held by another process are retried, then left untouched for the session", LockedSettings);
            test("The Copilot sign-in opens only an https page on github.com", CopilotPage);
            test("A definition's key pattern that is invalid or too slow matches nothing instead of throwing", KeyPatterns);
            test("Model ids with empty parts get a readable name instead of an error", ModelNames);
        }

        private static void Assert(bool condition, string message) { if (!condition) throw new Exception(message); }

        private static string SettingsFile { get { return Path.Combine(Settings.Folder, "settings.json"); } }

        /// <summary>Run with a settings file as given, putting the previous one back afterwards.</summary>
        private static void WithSettingsFile(string text, Action body)
        {
            string previous = File.Exists(SettingsFile) ? File.ReadAllText(SettingsFile) : null;
            try
            {
                File.WriteAllText(SettingsFile, text, new UTF8Encoding(false));
                body();
            }
            finally
            {
                if (previous == null) File.Delete(SettingsFile); else File.WriteAllText(SettingsFile, previous, new UTF8Encoding(false));
                File.Delete(SettingsFile + ".bad");
                Settings.Load();   // Clears the untouched-file state for the tests that follow.
            }
        }

        private static void InvalidSettings()
        {
            const string broken = "{\"FirstRunDone\":true,\"Theme\":";
            WithSettingsFile(broken, () =>
            {
                var s = Settings.Load();
                Assert(!s.FirstRunDone, "Defaults from a broken file did not ask the welcome questions again.");
                Assert(File.Exists(SettingsFile + ".bad") && File.ReadAllText(SettingsFile + ".bad") == broken, "The broken file was not kept as settings.json.bad.");
                s.Theme = "dark";
                s.Save();
                Assert(File.ReadAllText(SettingsFile).Contains("\"Theme\":\"dark\""), "Settings were not saved after a broken file was set aside.");
            });
        }

        private static void LockedSettings()
        {
            const string real = "{\"FirstRunDone\":true,\"Theme\":\"light\"}";
            WithSettingsFile(real, () =>
            {
                Settings s;
                var watch = Stopwatch.StartNew();
                using (new FileStream(SettingsFile, FileMode.Open, FileAccess.Read, FileShare.None))
                {
                    s = Settings.Load();
                    s.Theme = "dark";
                    s.Save();
                }
                Assert(watch.ElapsedMilliseconds >= 600, "A locked file was not retried before giving up.");
                Assert(s.FirstRunDone, "An unreadable file made the session look like a first run.");
                Assert(File.ReadAllText(SettingsFile) == real, "A session on defaults overwrote the settings it could not read.");
                Assert(Settings.Load().Theme == "light", "The settings were not read once the file was free again.");
            });
        }

        private static void CopilotPage()
        {
            const string fallback = "https://github.com/login/device";
            Assert(GitHubAuth.VerificationPage("https://github.com/login/device") == fallback, "GitHub's own page was refused.");
            Assert(GitHubAuth.VerificationPage("https://GitHub.com/login/device?x=1") == "https://github.com/login/device?x=1", "A github.com page with a query was refused.");
            foreach (var answered in new[] { null, "", "http://github.com/login/device", "file:///C:/Windows/System32/calc.exe", @"\\server\share\run.exe",
                "https://github.com.example.org/login", "https://user@github.com/login", "https://github.com:8443/login", "ms-settings:startupapps", "github.com/login/device" })
                Assert(GitHubAuth.VerificationPage(answered) == fallback, "The sign-in would open " + (answered ?? "null") + ".");
        }

        private static void KeyPatterns()
        {
            Func<string, Dictionary<string, object>> spec = pattern => new Dictionary<string, object> { { "pattern", pattern } };
            Assert(ServiceDefinition.SlotText(spec("[0-9a-f]{32}"), "https://dash.example.com/0123456789abcdef0123456789ABCDEF/home") == "0123456789abcdef0123456789ABCDEF",
                "A valid pattern no longer picks its part out of a pasted address.");
            Assert(ServiceDefinition.SlotText(spec("([a-z"), "abc") == null, "An invalid pattern did not count as no match.");
            var watch = Stopwatch.StartNew();
            Assert(ServiceDefinition.SlotText(spec("^(a+)+$"), new string('a', 40) + "!") == null, "A pattern that backtracks without end matched.");
            Assert(watch.ElapsedMilliseconds < 5000, "A slow pattern held the call for " + watch.ElapsedMilliseconds + " ms.");
        }

        private static void ModelNames()
        {
            Assert(Ui.PrettyModel("gpt-5-") == "GPT-5", "A trailing dash broke the model name: " + Ui.PrettyModel("gpt-5-"));
            Assert(Ui.PrettyModel("gpt--mini") == "GPT-mini", "A double dash broke the model name: " + Ui.PrettyModel("gpt--mini"));
            Assert(Ui.PrettyModel("gpt-") == "gpt-", "An id with nothing after gpt- changed: " + Ui.PrettyModel("gpt-"));
            Assert(Ui.PrettyModel("gpt-5-mini") == "GPT-5 Mini", "The usual model name changed: " + Ui.PrettyModel("gpt-5-mini"));
        }
    }
}
