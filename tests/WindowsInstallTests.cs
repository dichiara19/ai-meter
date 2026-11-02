using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;

namespace AIMeter
{
    /// <summary>
    /// What a typical Windows 11 PC has: coding agents installed by their own installers or npm, Docker Desktop's WSL
    /// distribution, and services that fail. AI Meter must find the agents and say what it could not do.
    /// </summary>
    internal static class WindowsInstallTests
    {
        internal static void Register(Action<string, Action> test)
        {
            test("Claude Code, Codex and Antigravity CLI are found where their installers and npm put them", AgentPrograms);
            test("Docker Desktop's distribution is never chosen as the WSL distribution", DockerDistribution);
            test("Codex without its program says so on its card", CodexWithoutProgram);
            test("A service that fails every cycle keeps its card with a notice and is logged once", FailingService);
        }

        private static void Assert(bool condition, string message)
        {
            if (!condition) throw new Exception(message);
        }

        private static string Touch(string root, string relativePath)
        {
            string file = Path.Combine(root, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(file));
            File.WriteAllText(file, "");
            return file;
        }

        private static void AgentPrograms()
        {
            string home = Path.Combine(Settings.Folder, "home"), appData = Path.Combine(Settings.Folder, "roaming"), local = Path.Combine(Settings.Folder, "local");
            string onPath = Path.Combine(Settings.Folder, "path");
            Directory.CreateDirectory(onPath);
            string savedPath = Environment.GetEnvironmentVariable("PATH");
            try
            {
                // This process's PATH comes before the user's: a program there is found even with no installer folder.
                Environment.SetEnvironmentVariable("PATH", onPath);
                string claudeOnPath = Touch(onPath, "claude.exe");
                Assert(ClaudeProvider.CliPath(home, appData) == claudeOnPath, "Claude Code on PATH was not found.");
                string npmClaude = Touch(appData, @"npm\claude.cmd");
                Assert(ClaudeProvider.CliPath(home, appData) == npmClaude, "npm's Claude Code shim was not found.");
                string nativeClaude = Touch(home, @".local\bin\claude.exe");
                Assert(ClaudeProvider.CliPath(home, appData) == nativeClaude, "The native installer's Claude Code did not come before npm's.");
                string bundledClaude = Touch(appData, @"Claude\claude-code\2.1.316\claude.exe");
                Assert(ClaudeProvider.CliPath(home, appData) == bundledClaude, "The desktop app's Claude Code did not come first.");

                // npm's Codex: the executable it unpacks, not the shim, so that app-server talks to Codex directly.
                if (ServiceDefinition.OnPath("codex.exe") == null)
                {
                    string shim = Touch(appData, @"npm\codex.cmd");
                    Assert(CodexProvider.CodexPath(local, appData) == shim, "npm's Codex shim was not found.");
                }
                string npmCodex = Touch(appData, @"npm\node_modules\@openai\codex\vendor\x86_64-pc-windows-msvc\codex\codex.exe");
                Assert(CodexProvider.CodexPath(local, appData) == npmCodex, "npm's Codex executable was not found or came after the shim.");
                string appCodex = Touch(local, @"OpenAI\Codex\bin\codex.exe");
                Assert(CodexProvider.CodexPath(local, appData) == appCodex, "The ChatGPT app's Codex did not come first.");

                string agyOnPath = Touch(onPath, "agy.exe");
                Assert(AntigravityProvider.CliPath(home, local) == agyOnPath, "Antigravity CLI on PATH was not found.");
                string programs = Touch(local, @"Programs\Antigravity CLI\agy.exe");
                Assert(AntigravityProvider.CliPath(home, local) == programs, "Antigravity CLI in Programs was not found.");
                string agy = Touch(home, @".local\bin\agy.exe");
                Assert(AntigravityProvider.CliPath(home, local) == agy, "Antigravity CLI in .local\\bin did not come first.");
            }
            finally { Environment.SetEnvironmentVariable("PATH", savedPath); }
        }

        private static void DockerDistribution()
        {
            Assert(Settings.ChooseWslDistro("Ubuntu-24.04", new[] { "docker-desktop", "Ubuntu-24.04" }) == "Ubuntu-24.04", "The default distribution was not kept.");
            Assert(Settings.ChooseWslDistro("docker-desktop", new[] { "Ubuntu" }) == "Ubuntu", "Docker Desktop's default distribution was chosen over Ubuntu.");
            Assert(Settings.ChooseWslDistro("docker-desktop", new string[0]) == "", "Docker Desktop's distribution was chosen when it was the only one.");
            Assert(Settings.ChooseWslDistro(null, new string[0]) == "", "A distribution was invented.");
            Assert(Settings.IsDockerDistro("Docker-Desktop-Data") && !Settings.IsDockerDistro("Ubuntu"), "Docker Desktop's distributions were not recognised.");
        }

        private static void CodexWithoutProgram()
        {
            Settings.Current.OpenAIEnabled = true;
            var codex = new CodexProvider(Settings.Current);
            const BindingFlags field = BindingFlags.Instance | BindingFlags.NonPublic;
            // As after a live read that found no program; the next read is not due, so nothing is started.
            typeof(CodexProvider).GetField("programFound", field).SetValue(codex, false);
            typeof(CodexProvider).GetField("fetchedAt", field).SetValue(codex, DateTime.Now);
            var card = new Collector(Settings.Current, new List<Provider> { codex }).Collect(true, false).Providers.Single();
            Assert(card.Notice == L.T("Codex was not found on this PC: limits from local logs only"), "Codex without its program gave no notice.");
        }

        /// <summary>A provider whose reads are given by the test.</summary>
        private sealed class Scripted : Provider
        {
            private readonly string id;
            public Func<ProviderState> Read;
            public Scripted(string id) { this.id = id; }
            public override string Id { get { return id; } }
            public override bool Enabled(Settings s) { return true; }
            public override ProviderState Collect(Probe p) { return Read(); }
        }

        private static void FailingService()
        {
            var failing = new Scripted("fixture-broken") { Read = () => { throw new InvalidOperationException("broken answer"); } };
            var healthy = new Scripted("fixture-fine") { Read = () => new ProviderState { Id = "fixture-fine", Name = "Fine" } };
            var collector = new Collector(Settings.Current, new List<Provider> { failing, healthy });
            string log = Path.Combine(Settings.Folder, "errors.log");
            Func<int> entries = () => File.Exists(log) ? File.ReadAllText(log).Split(new[] { "[collect-fixture-broken]" }, StringSplitOptions.None).Length - 1 : 0;

            for (int i = 0; i < 3; i++)
            {
                var snap = collector.Collect(false, false);
                var card = snap.Providers.FirstOrDefault(p => p.Id == "fixture-broken");
                Assert(card != null && card.Notice == L.T("Could not read this service: see errors.log"), "A failing service vanished or had no notice.");
                Assert(snap.Providers.Any(p => p.Id == "fixture-fine"), "A failing service stopped the others.");
            }
            Assert(entries() == 1, "A service failing every cycle was logged " + entries() + " times, not once.");

            // A good read clears the failure and gives the card its name; the next failure is logged again, under that name.
            failing.Read = () => new ProviderState { Id = "fixture-broken", Name = "Broken" };
            Assert(collector.Collect(false, false).Providers.Single(p => p.Id == "fixture-broken").Notice == null, "A recovered service kept its notice.");
            failing.Read = () => { throw new IOException("broken again"); };
            Assert(collector.Collect(false, false).Providers.Single(p => p.Id == "fixture-broken").Name == "Broken", "A failing card lost its name.");
            Assert(entries() == 2, "A new failure after a recovery was not logged.");
        }
    }
}
