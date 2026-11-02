using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace AIMeter
{
    /// <summary>The agent table as data (agents/*.json): loading, user overrides, detection and honest refusals.</summary>
    internal static class AgentTests
    {
        internal static void Register(Action<string, Action> test)
        {
            test("Agent definitions load from agents/*.json with folders, keys, programs and dispatch", BuiltInAgents);
            test("A user agents folder replaces and adds agents, fills placeholders once and ignores broken files", UserAgents);
            test("Agents found that delegation cannot use are reported with their reason and distribution", UnsupportedAgents);
            test("Node version managers are searched for agents, their packages and node", NodeManagers);
            test("Dispatch refuses an agent without node, a Windows wrapper and an unknown runtime", DispatchRefusals);
            test("describe_dispatch tells unknown runtimes, first data, missing agents and missing keys apart", DescribeMessages);
        }

        private static void Assert(bool condition, string message)
        {
            if (!condition) throw new Exception(message);
        }

        private const string Model = "nvidia/nemotron-3.5-lightning:free";

        /// <summary>Files under a new folder; returns the folder.</summary>
        private static string Tree(params string[] pathsAndContents)
        {
            string root = Path.Combine(Settings.Folder, "tree-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            for (int i = 0; i < pathsAndContents.Length; i += 2)
            {
                string path = Path.Combine(root, pathsAndContents[i]);
                if (pathsAndContents[i + 1] == null) { Directory.CreateDirectory(path); continue; }
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllText(path, pathsAndContents[i + 1], new UTF8Encoding(false));
            }
            return root;
        }

        private static void BuiltInAgents()
        {
            var pi = Agents.Get("pi");
            Assert(pi != null && pi.Origin == "built-in" && pi.Name == "Pi" && pi.Folders.SequenceEqual(new[] { @".pi\agent" }), "Pi's definition did not load.");
            Assert(pi.Keys.Count == 2 && pi.Keys[0].File == @".pi\agent\auth.json" && pi.Keys[0].Path == "{id}.key", "Pi's key sources are wrong.");
            Assert(pi.ProgramsIn("wsl").Single() == "pi" && pi.Package == "@earendil-works/pi-coding-agent", "Pi's program or package is wrong.");
            Assert(pi.PlaceFor("wsl") != null && pi.PlaceFor("windows") == null, "Pi's verified places are wrong.");
            Assert(pi.PlaceFor("wsl").Verifies("1.4.0") && !pi.PlaceFor("wsl").Verifies("2.0.0") && !pi.PlaceFor("wsl").Verifies(null), "1.x does not mean every 1.* version.");
            var openCode = Agents.Get("opencode");
            Assert(openCode.PlaceFor("windows").Executables.Length == 3 && openCode.Dispatch.Environment["OPENCODE_CONFIG_CONTENT"] is Dictionary<string, object>,
                "OpenCode's native programs or structured environment did not load.");
            Assert(Agents.Get("crush").Dispatch == null && Agents.Get("codex").Dispatch == null && Agents.Get("crush").Keys.Count == 2,
                "Agents without a verified dispatch must have none, and keep their key sources.");
            Assert(Agents.Get("pi").Dispatch.Output.ContainsKey("cost") && Agents.Get("opencode").Dispatch.Output["cost"].Contains("step_finish.part.cost"),
                "The per-request cost is not described.");
        }

        private static void UserAgents()
        {
            string folder = Agents.UserFolder;
            Directory.CreateDirectory(folder);
            try
            {
                File.WriteAllText(Path.Combine(folder, "pi.json"), "{ \"id\": \"pi\", \"name\": \"My Pi\", \"programs\": { \"wsl\": [\"pi\"] }, \"folders\": [\".pi/agent\"] }");
                File.WriteAllText(Path.Combine(folder, "aider.json"),
                    "{ \"id\": \"aider\", \"name\": \"Aider\", \"programs\": { \"wsl\": [\"aider\"] }, \"folders\": [\".aider\"],\n" +
                    "  \"dispatch\": { \"places\": { \"wsl\": { \"versions\": [\"0.9.x\"] } },\n" +
                    "    \"arguments\": { \"read_only\": [\"--model\", \"{provider}/{model}\", \"--dir\", \"{workspace}\"] },\n" +
                    "    \"environment\": { \"CONF\": { \"model\": \"{model}\", \"list\": [\"{provider}\"] }, \"PLAIN\": \"{provider}\" },\n" +
                    "    \"output\": { \"format\": \"text\" }, \"notes\": [\"Aider note.\"] } }");
                File.WriteAllText(Path.Combine(folder, "broken.json"), "{ \"id\": ");
                File.WriteAllText(Path.Combine(folder, "evil.json"), "{ \"id\": \"evil\", \"programs\": { \"wsl\": [\"../../bin/sh\"] } }");
                Agents.Reload();
                Assert(Agents.Get("pi").Name == "My Pi" && Agents.Get("pi").Origin.EndsWith("pi.json", StringComparison.Ordinal), "A user definition did not replace the built-in one.");
                Assert(Agents.Get("opencode") != null && Agents.Get("evil") == null, "Built-in agents were lost, or a program path was accepted as a name.");
                var aider = Agents.Get("aider");
                Assert(aider != null && aider.Folders.Single() == ".aider" && aider.Order == 1000, "A new user agent did not load.");

                // A model id is a value: it cannot add JSON keys or be read as another placeholder.
                var access = Candidates.Access("openrouter", "aider", "wsl", "key-0123456789abcdef");
                access.Provider = "openrouter";
                access.Program = "/home/me/.local/bin/aider";
                string hostile = "{workspace}\",\"mcp\":\"x";
                var plan = Dispatch.Describe(access, hostile, null, "/w", "wsl", "Ubuntu", "0.9.1");
                Assert(plan.Supported && plan.VersionVerified, "A user dispatch was not used: " + plan.Reason);
                Assert(plan.Arguments.SequenceEqual(new[] { "--model", "openrouter/" + hostile, "--dir", "/w" }), "Placeholders were not filled once: " + string.Join(" | ", plan.Arguments));
                var conf = Json.Parse(plan.Environment["CONF"]);
                Assert(conf.Count == 2 && Json.Str(conf, "model") == hostile && plan.Environment["PLAIN"] == "openrouter", "A structured environment value was not serialised safely.");
                Assert(plan.Output["format"] == "text" && plan.Notes.First() == "Aider note." && plan.Warnings.Single() == Dispatch.ByokFallback, "Output, notes or warnings are wrong.");
            }
            finally
            {
                Directory.Delete(folder, true);
                Agents.Reload();
            }
            Assert(Agents.Get("pi").Name == "Pi" && Agents.Get("aider") == null, "Removing the user folder did not restore the built-in agents.");
        }

        private static void UnsupportedAgents()
        {
            string root = Tree(@"home\me\.codex", null, @"home\me\.npm-global\bin\codex", "",
                               @"home\me\.config\crush", null, @"usr\local\bin\crush", "",
                               @"home\me\.pi\agent", null,
                               @"home\me\.local\bin\opencode", "");
            string home = Path.Combine(root, @"home\me");
            var scan = Candidates.Scan(null, new[] { new WslPlace { Distro = "Test-1", Home = home } }, Candidates.Kinds, false);
            Func<string, UnsupportedRuntime> of = id => scan.Unsupported.SingleOrDefault(u => u.Runtime == id);
            Assert(scan.Found.Count == 0, "An agent delegation cannot use was reported as found.");
            Assert(of("codex") != null && of("codex").Reason == "no_dispatch" && of("codex").Where == "wsl" && of("codex").Distro == "Test-1", "Codex was not reported as unsupported.");
            Assert(of("crush").Reason == "no_dispatch" && of("pi").Reason == "program_not_found" && of("opencode").Reason == "not_set_up",
                "Unsupported reasons are wrong: " + string.Join(", ", scan.Unsupported.Select(u => u.Runtime + "=" + u.Reason)));
            Assert(of("claude") == null && of("gemini") == null, "An agent that is not there was reported.");

            // Pi set up on Windows: verified only in WSL.
            string windows = Tree(@".pi\agent", null, @".local\bin\pi.exe", "");
            var onWindows = Candidates.Scan(windows, new WslPlace[0], Candidates.Kinds, false).Unsupported.Single(u => u.Runtime == "pi");
            Assert(onWindows.Reason == "place_not_verified" && onWindows.Where == "windows" && onWindows.Distro == null, "Pi on Windows was not reported as unverified there.");

            // Through MCP, with a sentence a plugin can show, and no candidate for an unsupported agent.
            var snap = new Snapshot();
            snap.Providers.Add(new ProviderState { Id = "openrouter", Name = "OpenRouter", Catalogue = new Catalogue { ReadAt = DateTime.Now, Pool = "openrouter:free",
                Models = new List<FreeModel> { new FreeModel { Id = Model, Name = "Nemotron", Tools = true, Free = FreeStatus.ZeroPrice } } } });
            scan.KeyDiscoveryOff = false;   // As if keys had been read: the reason is then the missing runtime.
            var server = new McpServer(() => snap, () => false, null, null, () => scan);
            var content = Call(server, "list_candidates", "{}");
            var unsupported = ((IList)Json.Obj(content, "runtimes")["unsupported"]).OfType<Dictionary<string, object>>().ToList();
            var codex = unsupported.Single(u => Json.Str(u, "name") == "codex");
            Assert(Json.Str(codex, "where") == "wsl" && Json.Str(codex, "distro") == "Test-1" && Json.Str(codex, "display_name") == "Codex" && Json.Str(codex, "reason") == "no_dispatch"
                   && Json.Str(codex, "detail").Contains("delegation does not support it yet"), "runtimes.unsupported lacks Codex's place, name, reason or detail.");
            Assert(((IList)content["candidates"]).Count == 0 && ((IList)content["excluded"]).OfType<Dictionary<string, object>>().Single().ContainsValue("no_runtime"),
                "An unsupported agent produced a candidate.");
        }

        private static void NodeManagers()
        {
            const string package = @"lib\node_modules\@earendil-works\pi-coding-agent\package.json";
            string root = Tree(@"home\me\.pi\agent", null,
                               @"home\me\.nvm\versions\node\v9.0.0\bin\pi", "",
                               @"home\me\.nvm\versions\node\v22.11.0\bin\pi", "",
                               @"home\me\.nvm\versions\node\v22.11.0\bin\node", "",
                               @"home\me\.nvm\versions\node\v22.11.0\" + package, "{ \"version\": \"1.4.0\" }",
                               @"home\me\.local\share\fnm\node-versions\v20.1.0\installation\bin\opencode", "",
                               @"home\me\.asdf\shims\crush", "");
            string home = Path.Combine(root, @"home\me");
            var pi = Agents.Get("pi");
            string program = Candidates.ProgramPath(pi, home, true);
            Assert(program != null && Candidates.LinuxPath(program, home) == "/home/me/.nvm/versions/node/v22.11.0/bin/pi", "nvm's newest version was not found first: " + program);
            Assert(Candidates.PackageVersion(Candidates.PackageFolder(pi, home, true, program)) == "1.4.0", "The version was not read from nvm's global packages.");
            Assert(Candidates.Installed(Agents.Get("opencode"), home, true) && Candidates.Installed(Agents.Get("crush"), home, true), "fnm or asdf folders were not searched.");
            var scan = Candidates.Scan(null, new[] { new WslPlace { Distro = "Test-1", Home = home } }, Candidates.Kinds, false);
            Assert(scan.Found.Any(f => f.Runtime == "pi" && f.Distro == "Test-1"), "Pi under nvm was not found.");

            // The agent in npm's prefix and node under nvm: node's folder goes next to the agent's in the PATH.
            string split = Tree(@"home\me\.npm-global\bin\pi", "", @"home\me\.nvm\versions\node\v22.11.0\bin\node", "", @"usr\bin\node", "");
            string splitHome = Path.Combine(split, @"home\me");
            string node = Candidates.NodeFolder(Path.Combine(splitHome, @".npm-global\bin\pi"), Candidates.SearchDirs(splitHome, true));
            Assert(Candidates.LinuxPath(node, splitHome) == "/home/me/.nvm/versions/node/v22.11.0/bin", "node under nvm was not found: " + node);
            var access = Candidates.Access("openrouter", "pi", "wsl", "key-0123456789abcdef");
            access.Provider = "openrouter";
            access.Program = "/home/me/.npm-global/bin/pi";
            access.NeedsNode = true;
            access.NodeFolder = "/home/me/.nvm/versions/node/v22.11.0/bin";
            var plan = Dispatch.Describe(access, Model, null, null, "wsl", "Ubuntu", "1.4.0");
            Assert(plan.Environment["PATH"] == "/home/me/.npm-global/bin:/home/me/.nvm/versions/node/v22.11.0/bin:/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin",
                "node's folder is not next to the agent's: " + plan.Environment["PATH"]);
            access.NodeFolder = "/usr/bin";
            Assert(Dispatch.Describe(access, Model, null, null, "wsl", "Ubuntu", "1.4.0").Environment["PATH"] == "/home/me/.npm-global/bin:/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin",
                "A system node was added twice.");
        }

        private static void DispatchRefusals()
        {
            var pi = Candidates.Access("openrouter", "pi", "wsl", "key-0123456789abcdef");
            pi.Provider = "openrouter";
            pi.Program = "/home/me/.npm-global/bin/pi";
            pi.NeedsNode = true;
            var noNode = Dispatch.Describe(pi, Model, null, null, "windows", "Ubuntu", "1.4.0");
            Assert(!noNode.Supported && noNode.Reason.Contains("node was not found"), "An agent without node was described: " + noNode.Reason);

            var wrapper = Candidates.Access("openrouter", "opencode", "windows", "key-0123456789abcdef");
            wrapper.Provider = "openrouter";
            wrapper.Program = @"C:\Users\me\AppData\Roaming\npm\opencode.cmd";
            var refused = Dispatch.Describe(wrapper, Model, null, null, "windows", null, "1.19.5");
            Assert(!refused.Supported && refused.Reason.Contains(".cmd"), "A shell wrapper was described as the program.");
            var unknown = Candidates.Access("openrouter", "nope", "wsl", "key-0123456789abcdef");
            Assert(Dispatch.Describe(unknown, Model, null, null, "wsl", "Ubuntu", null).Reason == "unknown runtime nope", "An unknown runtime was not named.");

            // npm's wrapper on the PATH leads to the native program inside its package.
            string npm = Tree(@"opencode.cmd", "", @"node_modules\opencode-ai\package.json", "{ \"version\": \"1.19.5\" }",
                              @"node_modules\opencode-ai\node_modules\opencode-windows-x64\bin\opencode.exe", "");
            var openCode = Agents.Get("opencode");
            string cmd = Path.Combine(npm, "opencode.cmd");
            string package = Candidates.PackageFolder(openCode, Settings.Folder, false, cmd);
            Assert(Candidates.PackageVersion(package) == "1.19.5", "The package next to the wrapper was not found.");
            Assert(Candidates.NativeProgram(openCode, cmd, package) == Path.Combine(npm, @"node_modules\opencode-ai\node_modules\opencode-windows-x64\bin\opencode.exe"),
                "The native program was not found inside the package.");
            Assert(Candidates.NativeProgram(openCode, @"C:\bin\opencode.exe", null) == @"C:\bin\opencode.exe", "A native program on the PATH was not preferred.");

            var nvidia = Candidates.Access("nvidia", "pi", "wsl", "key-0123456789abcdef");
            nvidia.Provider = "nvidia";
            nvidia.Program = "/home/me/.npm-global/bin/pi";
            Assert(Dispatch.Describe(nvidia, Model, null, null, "wsl", "Ubuntu", "1.4.0").Warnings.Count == 0, "A service without the BYOK fallback was warned about it.");
        }

        private static Dictionary<string, object> Call(McpServer server, string tool, string arguments)
        {
            var reply = Json.Parse(server.Handle("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/call\",\"params\":{\"name\":\"" + tool + "\",\"arguments\":" + arguments + "}}"));
            return Json.Obj(Json.Obj(reply, "result"), "structuredContent");
        }

        private static string Error(McpServer server, string runtime)
        {
            return Json.Str(Call(server, "describe_dispatch", "{\"service\":\"openrouter\",\"model\":\"" + Model + "\",\"runtime\":\"" + runtime + "\"}"), "error") ?? "";
        }

        private static void DescribeMessages()
        {
            NetworkBackoff.Clear();
            var snap = new Snapshot();
            snap.Providers.Add(new ProviderState { Id = "openrouter", Name = "OpenRouter", Catalogue = new Catalogue { ReadAt = DateTime.Now, Pool = "openrouter:free",
                Models = new List<FreeModel> { new FreeModel { Id = Model, Name = "Nemotron", Tools = true, Free = FreeStatus.ZeroPrice } } } });
            var scan = new RuntimeScan();
            Func<Snapshot> current = () => null;
            var server = new McpServer(() => current(), () => false, null, null, () => scan);

            Assert(Error(server, "nope").StartsWith("Unknown runtime \"nope\"", StringComparison.Ordinal), "An unknown runtime was not named: " + Error(server, "nope"));
            Assert(Error(server, "pi").Contains("still reading the first data"), "The first reading was not told apart: " + Error(server, "pi"));
            current = () => snap;
            Assert(Error(server, "crush").Contains("not supported for delegation"), "An unsupported agent was not told apart: " + Error(server, "crush"));
            Assert(Error(server, "pi").StartsWith("No usable Pi is set up here", StringComparison.Ordinal), "A missing agent was not told apart: " + Error(server, "pi"));

            scan.Found.Add(new RuntimePlace { Runtime = "pi", Where = "wsl", Distro = "Debian" });
            scan.KeyDiscoveryOff = true;
            Assert(Error(server, "pi").Contains("Key discovery is off"), "Key discovery being off was not told apart: " + Error(server, "pi"));
            scan.KeyDiscoveryOff = false;
            Assert(Error(server, "pi").StartsWith("Pi has no key of its own for openrouter", StringComparison.Ordinal), "A missing key was not told apart: " + Error(server, "pi"));

            // The access's own distribution is used, and the BYOK warning and the cost are given.
            var access = Candidates.Access("openrouter", "pi", "wsl", "key-0123456789abcdef");
            access.Provider = "openrouter";
            access.Program = "/home/me/.npm-global/bin/pi";
            access.Distro = "Debian";
            access.Version = "1.4.0";
            scan.Access.Add(access);
            var plan = Call(server, "describe_dispatch", "{\"service\":\"openrouter\",\"model\":\"" + Model + "\",\"runtime\":\"pi\",\"distro\":\"Debian\"}");
            var args = ((IList)plan["arguments"]).OfType<string>().ToList();
            Assert(Json.Bool(plan, "supported") && args[1] == "Debian" && Json.Str(plan, "distro") == "Debian" && Json.Bool(plan, "version_verified"), "The access's distribution or version was not used.");
            Assert(((IList)plan["warnings"]).OfType<string>().Single() == "openrouter_byok_fallback" && Json.Str(Json.Obj(plan, "output"), "cost").Contains("usage.cost"),
                "The BYOK warning or the cost is missing.");
            Assert(Error(server, "pi") == "" && Call(server, "describe_dispatch", "{\"service\":\"openrouter\",\"model\":\"" + Model + "\",\"runtime\":\"pi\",\"distro\":\"Other\"}").ContainsKey("error"),
                "A distribution filter was ignored.");
            var candidate = ((IList)Call(server, "list_candidates", "{}")["candidates"]).OfType<Dictionary<string, object>>().Single();
            Assert(((IList)candidate["warnings"]).OfType<string>().Single() == "openrouter_byok_fallback" && Json.Str(Json.Obj(candidate, "runtime"), "distro") == "Debian",
                "A candidate lacks its warning or distribution.");
        }
    }
}
