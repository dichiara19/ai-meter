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
            test("An ordinary launch does not restore removed startup entries", () => Fixture(PreserveAbsence));
            test("First use creates startup and explicit changes select the current copy", () => Fixture(ExplicitChanges));
            test("Explicit startup preserves a script path containing spaces", () => Fixture(ScriptLaunch));
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
            Settings.InitialiseStartup(false);
            Settings.InitialiseStartup(true);
            Assert(File.ReadAllBytes(Settings.StartupLink).SequenceEqual(original), "A launch replaced another copy's shortcut.");
        }

        private static void PreserveDisabledShortcut()
        {
            byte[] original = CreateShortcut();
            byte[] disabled = new byte[12]; disabled[0] = 3;
            using (var key = Registry.CurrentUser.CreateSubKey(Settings.StartupRegistryRoot + @"\Explorer\StartupApproved\StartupFolder"))
                key.SetValue("AI Meter.lnk", disabled, RegistryValueKind.Binary);
            Settings.InitialiseStartup(true);
            Settings.InitialiseStartup(false);
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
            Settings.InitialiseStartup(false);
            Settings.InitialiseStartup(true);
            using (var key = Registry.CurrentUser.OpenSubKey(Settings.StartupRegistryRoot + @"\Run"))
                Assert((string)key.GetValue("AI Meter") == command, "A launch replaced the legacy command.");
            Assert(!File.Exists(Settings.StartupLink), "A launch migrated the legacy entry to the test copy.");
        }

        private static void PreserveAbsence()
        {
            Settings.InitialiseStartup(false);
            Assert(!File.Exists(Settings.StartupLink) && !Settings.StartsWithWindows, "An ordinary launch restored startup.");
        }

        private static void ExplicitChanges()
        {
            Settings.InitialiseStartup(true);
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
    }
}
