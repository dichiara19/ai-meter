using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;

namespace AIMeter
{
    /// <summary>Version comparison, release selection and the guards of the daily update check; no network access.</summary>
    internal static class UpdateTests
    {
        internal static void Register(Action<string, Action> test)
        {
            test("SemVer orders pre-releases before their stable version and rejects malformed tags", Ordering);
            test("The update check picks stable releases and skips drafts, pre-releases and bad tags", Selection);
            test("Offline, rate-limited or unreadable answers report nothing", Failures);
            test("The update check runs at most once a day and never with sample data", Schedule);
            test("The Microsoft Store package never contacts GitHub for updates", PackagedSkip);
            test("Settings from earlier versions turn the update check on and pre-releases off", Defaults);
            test("Every supported language describes the update check", Translations);
        }

        private static void Assert(bool condition, string message)
        {
            if (!condition) throw new Exception(message);
        }

        private static void Ordering()
        {
            string[] ascending =
            {
                "0.9.0-alpha", "0.9.0-alpha.1", "0.9.0-alpha.beta", "0.9.0-beta", "0.9.0-beta.2", "0.9.0-beta.11",
                "0.9.0-rc.1", "0.9.0", "0.9.1", "0.10.0", "1.0.0-rc.1", "1.0.0", "10.0.0",
            };
            for (int i = 0; i < ascending.Length; i++)
                for (int j = 0; j < ascending.Length; j++)
                {
                    int c = SemVersion.Parse(ascending[i]).CompareTo(SemVersion.Parse(ascending[j]));
                    Assert(Math.Sign(c) == Math.Sign(i.CompareTo(j)), ascending[i] + " and " + ascending[j] + " compare as " + c + ".");
                }

            Assert(SemVersion.Parse("v0.9.0").CompareTo(SemVersion.Parse("0.9.0")) == 0, "A leading v changed the version.");
            Assert(SemVersion.Parse("0.9.0+build.7").CompareTo(SemVersion.Parse("0.9.0")) == 0, "Build metadata changed precedence.");
            Assert(SemVersion.Parse("v0.9.0-rc.1").ToString() == "0.9.0-rc.1", "The version did not round-trip.");

            foreach (var bad in new[] { null, "", "v", "latest", "0.9", "0.9.0.0", "01.2.3", "1.02.3", "1.2.3-", "1.2.3-beta..1",
                                        "1.2.3-01", "1.2.3-beta_1", " v1.2.3 extra", "99999999999.0.0", "v1.2.3\n<script>" })
                Assert(SemVersion.Parse(bad) == null, "Accepted the malformed tag " + (bad ?? "null") + ".");

            Assert(UpdateCheck.IsNewer("0.9.0", "0.9.0-beta.3"), "A stable release did not supersede its beta.");
            Assert(UpdateCheck.IsNewer("v0.8.7", "0.8.6"), "A newer patch was not reported.");
            Assert(!UpdateCheck.IsNewer("0.9.0-beta.3", "0.9.0"), "A beta was reported as newer than its stable release.");
            Assert(!UpdateCheck.IsNewer("0.8.6", "0.8.6"), "The running version was reported as newer.");
            Assert(!UpdateCheck.IsNewer("nightly", "0.8.6") && !UpdateCheck.IsNewer(null, "0.8.6"), "A malformed candidate was reported.");
            Assert(!UpdateCheck.IsNewer("0.9.0", "") && !UpdateCheck.IsNewer("0.9.0", "dev"), "An unknown running version was compared.");

            Assert(UpdateCheck.PageFor("0.9.0-rc.1") == "https://github.com/dichiara19/ai-meter/releases/tag/v0.9.0-rc.1", "Unexpected release page.");
            Assert(UpdateCheck.PageFor("javascript:alert(1)") == "https://github.com/dichiara19/ai-meter/releases", "A malformed version reached the link.");
        }

        private const string Stable = "{\"tag_name\":\"v0.9.0\",\"draft\":false,\"prerelease\":false,\"html_url\":\"https://example.invalid/\"}";

        private const string Recent = "[" +
            "{\"tag_name\":\"v1.0.0\",\"draft\":true,\"prerelease\":false}," +
            "{\"tag_name\":\"v0.10.0-beta.1\",\"draft\":false,\"prerelease\":true}," +
            "{\"tag_name\":\"v0.10.0-rc.1\",\"draft\":false,\"prerelease\":false}," +
            "{\"tag_name\":\"nightly\",\"draft\":false,\"prerelease\":false}," +
            "{\"tag_name\":\"v0.9.0\",\"draft\":false,\"prerelease\":false}," +
            "{\"tag_name\":\"v0.8.6\",\"draft\":false,\"prerelease\":false}]";

        private static void Selection()
        {
            string asked = null;
            var found = UpdateCheck.Find(false, url => { asked = url; return Stable; });
            Assert(asked == UpdateCheck.LatestUrl, "The stable channel did not ask for the latest release.");
            Assert(found != null && found.ToString() == "0.9.0", "The latest stable release was not found.");

            found = UpdateCheck.Find(true, url => { asked = url; return Recent; });
            Assert(asked == UpdateCheck.RecentUrl, "The pre-release channel did not list recent releases.");
            Assert(found != null && found.ToString() == "0.10.0-rc.1", "The newest pre-release was not chosen, or a draft was.");

            // Only stable versions on the stable channel, even when a pre-release tag lacks GitHub's flag.
            found = UpdateCheck.Find(false, url => Recent);
            Assert(found != null && found.ToString() == "0.9.0", "The stable channel reported a pre-release or a draft.");
            found = UpdateCheck.Find(false, url => "{\"tag_name\":\"v0.9.0-beta.1\",\"draft\":false,\"prerelease\":false}");
            Assert(found == null, "An unflagged pre-release tag reached the stable channel.");
        }

        private static void Failures()
        {
            foreach (var body in new[] { null, "", "not json", "{}", "[]", "{\"message\":\"API rate limit exceeded\"}", "[1,\"x\",null]" })
                Assert(UpdateCheck.Find(false, url => body) == null && UpdateCheck.Find(true, url => body) == null,
                    "An unusable answer reported a version: " + (body ?? "null"));
            // Real requests to a closed loopback port fail quietly instead of throwing.
            Assert(UpdateCheck.Download("http://127.0.0.1:9/") == null, "A failed request did not return null.");
        }

        private static void Schedule()
        {
            var now = new DateTime(2026, 11, 2, 12, 0, 0, DateTimeKind.Utc);
            Assert(UpdateCheck.Due(DateTime.MinValue, now), "The first check was not due.");
            Assert(!UpdateCheck.Due(now.AddHours(-23), now), "A second check ran within a day.");
            Assert(UpdateCheck.Due(now.AddHours(-24), now), "The check was not due after a day.");
            Assert(!UpdateCheck.Due(now.AddHours(1), now), "A clock moved back slightly allowed an early check.");
            Assert(UpdateCheck.Due(now.AddDays(3), now), "A clock moved back by days blocked checks until that date.");

            var s = Settings.Current;
            s.UpdateCheck = false;
            Assert(!UpdateCheck.Enabled(s), "The check ran with the switch off.");
            s.UpdateCheck = true;
            Assert(UpdateCheck.Enabled(s), "The check did not run with the switch on.");
            bool demo = Program.Demo;
            try
            {
                Program.Demo = true;
                Assert(!UpdateCheck.Enabled(s), "The check ran with sample data, which promises no network access.");
            }
            finally { Program.Demo = demo; }
        }

        private static void PackagedSkip()
        {
            var field = typeof(Native).GetField("packaged", BindingFlags.Static | BindingFlags.NonPublic);
            object previous = field.GetValue(null);
            try
            {
                field.SetValue(null, 1);
                Assert(Native.IsPackaged, "The fixture did not simulate the package.");
                bool asked = false;
                Assert(UpdateCheck.Find(false, url => { asked = true; return Stable; }) == null, "The package reported a GitHub release.");
                Assert(UpdateCheck.Find(true, url => { asked = true; return Recent; }) == null, "The package reported a GitHub pre-release.");
                Assert(!asked, "The package contacted GitHub for updates.");
            }
            finally { field.SetValue(null, previous); }
        }

        private static void Defaults()
        {
            File.WriteAllText(Path.Combine(Settings.Folder, "settings.json"), "{\"FirstRunDone\":true,\"Theme\":\"dark\"}", new UTF8Encoding(false));
            var s = Settings.Load();
            Assert(s.Theme == "dark", "The fixture settings were not read.");
            Assert(s.UpdateCheck && !s.UpdatePreReleases, "Earlier settings did not get the update defaults.");
            Assert(UpdateCheck.Due(s.UpdateCheckedAt, DateTime.UtcNow) && s.UpdateAvailable == null, "Earlier settings did not start without a check.");

            s.UpdateCheck = false;
            s.UpdateCheckedAt = new DateTime(2026, 11, 2, 8, 30, 0, DateTimeKind.Utc);
            s.Save();
            var again = Settings.Load();
            Assert(!again.UpdateCheck, "Turning the check off did not persist.");
            Assert(again.UpdateCheckedAt.ToUniversalTime() == s.UpdateCheckedAt, "The last check time changed when saved.");
        }

        private static void Translations()
        {
            var keys = new[]
            {
                "Check for updates", "Include pre-releases", "AI Meter {0} is available", "You are using {0}.", "Release page",
                "Once a day, ask GitHub whether a newer stable version exists and show a notice. Nothing is downloaded or installed, and the request includes no account or identifier.",
                "Also tell me about beta and release candidate versions, which can be less stable.",
                "You are using {0}. Click to open the release page: nothing is downloaded automatically.",
                "The Microsoft Store installs updates by itself. Once a day AI Meter also asks the Store whether one is waiting, to show it here; it never contacts GitHub.",
                "An update is waiting in the Microsoft Store", "Open the Store",
                "The update check below asks the Microsoft Store whether an update is waiting.",
                "The update check below also asks GitHub for the latest version.",
            };
            try
            {
                foreach (var lang in new[] { "it", "es", "fr", "de", "pt" })
                {
                    L.Init(lang);
                    foreach (var key in keys)
                    {
                        Assert(L.T(key) != key, "Missing update translation for " + lang + ": " + key);
                        Assert(key.Contains("{0}") == L.T(key).Contains("{0}"), "The placeholder is missing in " + lang + ": " + key);
                    }
                }
            }
            finally { L.Init("en"); }
        }
    }
}
