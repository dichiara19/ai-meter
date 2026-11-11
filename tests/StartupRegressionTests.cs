using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace AIMeter
{
    internal static class StartupRegressionTests
    {
        internal static void Register(Action<string, Action> test)
        {
            test("Launching another copy preserves an existing startup shortcut", () => Fixture(PreserveShortcut));
            test("First use preserves a startup entry disabled by Windows", () => Fixture(PreserveDisabledShortcut));
            test("Launching another copy preserves the legacy Run command", () => Fixture(PreserveLegacyCommand));
            test("Declining start-up on first use removes any registration", () => Fixture(DeclineOnFirstUse));
            test("First use creates startup and explicit changes select the current copy", () => Fixture(ExplicitChanges));
            test("Explicit startup preserves a script path containing spaces", () => Fixture(ScriptLaunch));
            test("Start-up is registered only after the welcome window, as chosen", () => Fixture(StartupAfterConsent));
            test("The packaged app is launched through its execution alias", () => Fixture(PackagedLaunch));
            test("The Store package asks about start-up once when its settings came from another copy", () => Fixture(PackageAsksAfterMove));
        }

        private static void Fixture(Action test)
        {
            string home = Environment.GetEnvironmentVariable("AIMETER_HOME");
            string launch = Environment.GetEnvironmentVariable("AIMETER_LAUNCH");
            try
            {
                Environment.SetEnvironmentVariable("AIMETER_HOME", Settings.Folder);
                Environment.SetEnvironmentVariable("AIMETER_LAUNCH", "\"C:\\AI Meter Test\\AIMeter.exe\"");
                test();
            }
            finally
            {
                Native.PackagedOverride = null;
                if (File.Exists(Settings.StartupLink)) File.Delete(Settings.StartupLink);
                Registry.CurrentUser.DeleteSubKeyTree(Settings.StartupRegistryRoot, false);
                Environment.SetEnvironmentVariable("AIMETER_HOME", home);
                Environment.SetEnvironmentVariable("AIMETER_LAUNCH", launch);
            }
        }

        private static void Assert(bool condition, string message)
        {
            if (!condition) throw new Exception(message);
        }

        private static byte[] CreateShortcut()
        {
            Native.SaveShortcut(Settings.StartupLink, @"C:\Installed AI Meter\AIMeter.exe", "--startup",
                @"C:\Installed AI Meter", @"C:\Installed AI Meter\AIMeter.exe", 0);
            Assert(File.Exists(Settings.StartupLink), "The fixture shortcut was not created.");
            return File.ReadAllBytes(Settings.StartupLink);
        }

        private static void AssertCurrentShortcut(string expectedTarget = @"C:\AI Meter Test\AIMeter.exe", string expectedArguments = "--startup")
        {
            object shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell"));
            object link = null;
            try
            {
                link = shell.GetType().InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { Settings.StartupLink });
                string target = (string)link.GetType().InvokeMember("TargetPath", BindingFlags.GetProperty, null, link, null);
                string arguments = (string)link.GetType().InvokeMember("Arguments", BindingFlags.GetProperty, null, link, null);
                string directory = (string)link.GetType().InvokeMember("WorkingDirectory", BindingFlags.GetProperty, null, link, null);
                Assert(string.Equals(target, expectedTarget, StringComparison.OrdinalIgnoreCase), "The shortcut targets another executable.");
                Assert(arguments == expectedArguments, "The shortcut arguments changed.");
                Assert(string.Equals(directory, Settings.Folder, StringComparison.OrdinalIgnoreCase), "The shortcut has another working directory.");
            }
            finally
            {
                if (link != null) Marshal.ReleaseComObject(link);
                Marshal.ReleaseComObject(shell);
            }
        }

        private static void PreserveShortcut()
        {
            byte[] original = CreateShortcut();
            Settings.ApplyFirstRunStartup(true);
            Assert(File.ReadAllBytes(Settings.StartupLink).SequenceEqual(original), "A launch replaced another copy's shortcut.");
        }

        private static void PreserveDisabledShortcut()
        {
            byte[] original = CreateShortcut();
            byte[] disabled = new byte[12]; disabled[0] = 3;
            using (var key = Registry.CurrentUser.CreateSubKey(Settings.StartupRegistryRoot + @"\Explorer\StartupApproved\StartupFolder"))
                key.SetValue("AI Meter.lnk", disabled, RegistryValueKind.Binary);
            Settings.ApplyFirstRunStartup(true);
            Assert(!Settings.StartsWithWindows, "A launch re-enabled startup disabled by Windows.");
            Assert(File.ReadAllBytes(Settings.StartupLink).SequenceEqual(original), "A launch replaced a disabled shortcut.");
            using (var key = Registry.CurrentUser.OpenSubKey(Settings.StartupRegistryRoot + @"\Explorer\StartupApproved\StartupFolder"))
                Assert(((byte[])key.GetValue("AI Meter.lnk")).SequenceEqual(disabled), "The Windows disabled state changed.");
        }

        private static void PreserveLegacyCommand()
        {
            const string command = "\"C:\\Installed AI Meter\\AIMeter.exe\" --startup";
            using (var key = Registry.CurrentUser.CreateSubKey(Settings.StartupRegistryRoot + @"\Run"))
                key.SetValue("AI Meter", command);
            Settings.ApplyFirstRunStartup(true);
            using (var key = Registry.CurrentUser.OpenSubKey(Settings.StartupRegistryRoot + @"\Run"))
                Assert((string)key.GetValue("AI Meter") == command, "A launch replaced the legacy command.");
            Assert(!File.Exists(Settings.StartupLink), "A launch migrated the legacy entry to the test copy.");
        }

        private static void DeclineOnFirstUse()
        {
            CreateShortcut();
            using (var key = Registry.CurrentUser.CreateSubKey(Settings.StartupRegistryRoot + @"\Run"))
                key.SetValue("AI Meter", "old command");
            Settings.ApplyFirstRunStartup(false);
            Assert(!File.Exists(Settings.StartupLink) && !Settings.StartsWithWindows, "Declining start-up left a registration.");
        }

        private static void ExplicitChanges()
        {
            Settings.ApplyFirstRunStartup(true);
            Assert(File.Exists(Settings.StartupLink) && Settings.StartsWithWindows, "First use did not enable startup.");
            AssertCurrentShortcut();
            Settings.StartsWithWindows = false;
            Assert(!File.Exists(Settings.StartupLink) && !Settings.StartsWithWindows, "Disabling startup left a registration.");
            byte[] original = CreateShortcut();
            using (var key = Registry.CurrentUser.CreateSubKey(Settings.StartupRegistryRoot + @"\Explorer\StartupApproved\StartupFolder"))
                key.SetValue("AI Meter.lnk", new byte[] { 3 }, RegistryValueKind.Binary);
            using (var key = Registry.CurrentUser.CreateSubKey(Settings.StartupRegistryRoot + @"\Run"))
                key.SetValue("AI Meter", "old command");
            Settings.StartsWithWindows = true;
            Assert(Settings.StartsWithWindows, "Explicitly enabling startup retained Windows' disabled state.");
            Assert(!File.ReadAllBytes(Settings.StartupLink).SequenceEqual(original), "Explicitly enabling startup did not select the current copy.");
            AssertCurrentShortcut();
            using (var key = Registry.CurrentUser.OpenSubKey(Settings.StartupRegistryRoot + @"\Run"))
                Assert(key.GetValue("AI Meter") == null, "Explicitly enabling startup retained a duplicate legacy entry.");
        }

        private static void ScriptLaunch()
        {
            const string arguments = "--headless powershell.exe -NoProfile -ExecutionPolicy Bypass -Sta -File \"C:\\AI Meter Test\\AIMeter.ps1\"";
            Environment.SetEnvironmentVariable("AIMETER_LAUNCH", "conhost.exe " + arguments);
            Settings.StartsWithWindows = true;
            AssertCurrentShortcut(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "conhost.exe"), arguments + " --startup");
        }

        /// <summary>Store policy 10.2.8: nothing registers start-up while the consent window is still open.</summary>
        private static void StartupAfterConsent()
        {
            var settings = Settings.Current;
            bool firstRunDone = settings.FirstRunDone;
            try
            {
                foreach (bool packaged in new[] { false, true })
                {
                    Native.PackagedOverride = packaged;
                    string mode = packaged ? " (packaged)" : "";
                    settings.FirstRunDone = false;
                    Controller.CompleteFirstRun(settings, () =>
                    {
                        Assert(!Settings.StartsWithWindows && !File.Exists(Settings.StartupLink), "Start-up was registered before consent" + mode + ".");
                        Assert(!settings.FirstRunDone, "The first run was recorded before consent" + mode + ".");
                        return true;
                    });
                    // The packaged registration is the manifest's startup task, compiled only into the MSIX build.
                    Assert(settings.FirstRunDone && (packaged || Settings.StartsWithWindows), "Accepting start-up did not register it" + mode + ".");

                    settings.FirstRunDone = false;
                    Controller.CompleteFirstRun(settings, () => false);
                    Assert(settings.FirstRunDone && !Settings.StartsWithWindows && !File.Exists(Settings.StartupLink), "Declining start-up registered it" + mode + ".");
                }
            }
            finally
            {
                settings.FirstRunDone = firstRunDone;
                settings.Save();
            }
        }

        /// <summary>
        /// Settings carried over from a GitHub copy have FirstRunDone set, while the package's startup task starts
        /// disabled: without a second ask the Store copy never started with Windows (seen on 2026-11-11).
        /// </summary>
        private static void PackageAsksAfterMove()
        {
            var settings = Settings.Current;
            bool firstRunDone = settings.FirstRunDone, packageFirstRunDone = settings.PackageFirstRunDone;
            try
            {
                Func<bool?> undecided = () => true, decided = () => false, unreadable = () => null,
                    unreachable = () => { throw new InvalidOperationException("The startup task was queried needlessly."); };
                bool record;

                settings.FirstRunDone = false;
                settings.PackageFirstRunDone = false;
                Assert(Controller.NeedsFirstRunConsent(settings, false, unreachable, out record) && !record, "The first launch did not ask.");
                Assert(Controller.NeedsFirstRunConsent(settings, true, unreachable, out record) && !record, "The first packaged launch did not ask.");

                settings.FirstRunDone = true;
                Assert(!Controller.NeedsFirstRunConsent(settings, false, unreachable, out record) && !record, "The unpackaged copy asked twice.");
                Assert(Controller.NeedsFirstRunConsent(settings, true, undecided, out record) && !record, "The package did not ask after a move from another copy.");
                Assert(!Controller.NeedsFirstRunConsent(settings, true, decided, out record), "The package asked although its startup task was already chosen.");
                Assert(record, "A startup task already chosen did not settle the package's first launch.");
                // Codacy's review of #79: a task that cannot be read must not drop the question for good.
                Assert(!Controller.NeedsFirstRunConsent(settings, true, unreadable, out record), "The package asked on a startup task it could not read.");
                Assert(!record, "An unreadable startup task settled the package's first launch, so it would never ask again.");

                Native.PackagedOverride = true;
                Controller.CompleteFirstRun(settings, () => false);
                Assert(settings.PackageFirstRunDone, "The package's first launch was not recorded.");
                Assert(!Controller.NeedsFirstRunConsent(settings, true, unreachable, out record) && !record, "The package asked again after its own first launch.");

                Native.PackagedOverride = false;
                settings.PackageFirstRunDone = false;
                Controller.CompleteFirstRun(settings, () => false);
                Assert(!settings.PackageFirstRunDone, "The unpackaged copy recorded the package's first launch.");
            }
            finally
            {
                settings.FirstRunDone = firstRunDone;
                settings.PackageFirstRunDone = packageFirstRunDone;
                settings.Save();
            }
        }

        /// <summary>The MCP relay must name a path that survives Store updates, not the versioned package folder.</summary>
        private static void PackagedLaunch()
        {
            Environment.SetEnvironmentVariable("AIMETER_LAUNCH", null);
            string alias = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Microsoft\WindowsApps\AIMeter.exe");
            Native.PackagedOverride = true;
            Assert(Program.LaunchCommand == "\"" + alias + "\"", "The packaged launch command is not the execution alias: " + Program.LaunchCommand);
            string target, args;
            Program.SplitLaunch(out target, out args);
            Assert(target == alias && args == "", "The relay would not start the execution alias.");
            Assert(McpRelay.Script(McpPipeHost.PipePrefix, target, args).Contains("$launch = '" + alias + "'"), "The relay script does not name the execution alias.");

            Native.PackagedOverride = false;
            Assert(Program.LaunchCommand.IndexOf("WindowsApps", StringComparison.OrdinalIgnoreCase) < 0, "An unpackaged copy used the execution alias.");
        }
    }
}
