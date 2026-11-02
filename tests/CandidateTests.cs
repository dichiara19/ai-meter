using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;

namespace AIMeter
{
    /// <summary>Execution candidates: which free models a task can run on now, through which runtime, and why not.</summary>
    internal static class CandidateTests
    {
        internal static void Register(Action<string, Action> test)
        {
            test("Candidates keep confirmed free models a runtime can call, ordered by known room", OrderAndPolicy);
            test("Task needs, pools, backoff and used-up counters exclude candidates with their reasons", Exclusions);
            test("Every agent with its own key is a candidate, sharing the same pool", SeveralAgents);
            test("The same request gives the same order whatever the order of services", Deterministic);
            test("Each agent counts only its own keys; credential ids match the same key and hide it", AgentKeys);
            test("list_candidates reports candidates, pools once, exclusions and runtimes", CandidatesTool);
        }

        private static void Assert(bool condition, string message)
        {
            if (!condition) throw new Exception(message);
        }

        private static FreeModel Model(string id, long context, long output, bool tools, FreeStatus free)
        {
            return new FreeModel { Id = id, Name = id, Context = context, MaxOutput = output, Tools = tools, Free = free };
        }

        private static readonly FreeStatus Unknown = new FreeStatus { Status = "unknown", Note = "no prices" };

        /// <summary>
        /// OpenRouter (two free models sharing a pool with a reported daily counter), Experiential Labs (documented limits
        /// only), NVIDIA (price unknown, features unknown) and Gemini (price unknown, a pool per model).
        /// </summary>
        private static Snapshot Fixture(double routerUsed)
        {
            var snap = new Snapshot();
            var router = new Catalogue { ReadAt = DateTime.Now, Pool = "openrouter:free", Models = new List<FreeModel>
                { Model("z/large:free", 262144, 32768, true, FreeStatus.ZeroPrice), Model("a/small:free", 65536, 8192, true, FreeStatus.ZeroPrice) } };
            router.Rates.Add(new RateLimit { Metric = "requests_per_minute", Limit = 20, Scope = "account", Source = "documented" });
            router.Rates.Add(new RateLimit { Metric = "requests_per_day", Limit = 50, Used = routerUsed, Remaining = Math.Max(0, 50 - routerUsed), Scope = "account",
                Source = "provider_reported", ResetsAt = DateTime.Now.AddHours(5), ResetZone = "UTC" });
            var labs = new Catalogue { ReadAt = DateTime.Now, Pool = "experientiallabs:free", Models = new List<FreeModel>
                { Model("luna", 128000, 16000, true, FreeStatus.ZeroPrice) } };
            labs.Rates.Add(new RateLimit { Metric = "requests_per_minute", Limit = 10, PerModel = true, Scope = "model", Source = "documented" });
            var nvidia = new Catalogue { ReadAt = DateTime.Now, Pool = "nvidia:account", Models = new List<FreeModel>
                { new FreeModel { Id = "meta/llama-4", Name = "meta/llama-4", FeaturesUnknown = true, Free = Unknown } } };
            var gemini = new Catalogue { ReadAt = DateTime.Now, Pool = "gemini:{model}", Models = new List<FreeModel>
                { Model("gemini-2.5-flash", 1048576, 65536, false, Unknown) } };
            snap.Providers.Add(new ProviderState { Id = "openrouter", Name = "OpenRouter", Catalogue = router });
            snap.Providers.Add(new ProviderState { Id = "experientiallabs", Name = "Experiential Labs", Catalogue = labs });
            snap.Providers.Add(new ProviderState { Id = "nvidia", Name = "NVIDIA", Catalogue = nvidia });
            snap.Providers.Add(new ProviderState { Id = "gemini", Name = "Gemini", Catalogue = gemini });
            return snap;
        }

        /// <summary>Pi set up in WSL, with keys for OpenRouter, Experiential Labs and NVIDIA but not Gemini.</summary>
        private static RuntimeScan Pi()
        {
            var scan = new RuntimeScan();
            scan.Found.Add(new RuntimePlace { Runtime = "pi", Where = "wsl" });
            foreach (var s in new[] { "openrouter", "experientiallabs", "nvidia" }) scan.Access.Add(Candidates.Access(s, "pi", "wsl", "key-" + s + "-0123456789"));
            return scan;
        }

        private static List<string> Order(Selection s) { return s.Chosen.Select(c => c.Provider.Id + "/" + c.Model.Id).ToList(); }

        private static Exclusion Excluded(Selection s, string service) { return s.Excluded.Single(e => e.Service == service); }

        private static void OrderAndPolicy()
        {
            NetworkBackoff.Clear();
            var free = Candidates.Select(Fixture(10), Pi(), new Requirements(), DateTime.Now);
            Assert(Order(free).SequenceEqual(new[] { "openrouter/a/small:free", "openrouter/z/large:free", "experientiallabs/luna" }),
                "Known room must come first, then unknown room, then ids: " + string.Join(", ", Order(free)));
            Assert(Math.Abs(free.Chosen[0].Room.Ratio.Value - 0.8) < 1e-9 && free.Chosen[0].Room.BindingMetric == "requests_per_day", "The binding limit or its share is wrong.");
            Assert(!free.Chosen[2].Room.Ratio.HasValue && free.Chosen[2].Room.MaxNewJobs == 1, "Unknown room must allow exactly one task.");
            Assert(Excluded(free, "nvidia").Reason == "price_not_confirmed" && Excluded(free, "gemini").Reason == "price_not_confirmed", "free_only admitted an unconfirmed price.");

            var open = Candidates.Select(Fixture(10), Pi(), new Requirements { CostPolicy = Candidates.FreeOrUnconfirmed }, DateTime.Now);
            Assert(Order(open).Contains("nvidia/meta/llama-4"), "free_or_unconfirmed left out a model of unknown price.");
            Assert(Excluded(open, "gemini").Reason == "runtime_has_no_key", "A service without a Pi key was not excluded for it.");
        }

        private static void Exclusions()
        {
            NetworkBackoff.Clear();
            var needs = Candidates.Select(Fixture(10), Pi(), new Requirements { Tools = true, MinOutput = 20000, CostPolicy = Candidates.FreeOrUnconfirmed }, DateTime.Now);
            Assert(Order(needs).SequenceEqual(new[] { "openrouter/z/large:free" }), "Per-request caps did not filter: " + string.Join(", ", Order(needs)));
            Assert(needs.Excluded.Any(e => e.Service == "openrouter" && e.Reason == "output_too_small" && e.Models.Single() == "a/small:free"), "A small output limit was not the reason.");
            Assert(Excluded(needs, "nvidia").Reason == "tools_unknown" && Excluded(needs, "gemini").Reason == "no_tools", "Unknown and missing tool support were mixed up.");

            // Two models in one pool share its room: the pool is reported once, with one new task.
            var shared = Candidates.Select(Fixture(10), Pi(), new Requirements(), DateTime.Now);
            var pools = shared.Chosen.GroupBy(c => c.Pool).ToList();
            Assert(pools.Count == 2 && pools.Single(g => g.Key == "openrouter:free").Count() == 2 && pools.All(g => g.First().Room.MaxNewJobs == 1), "Pools were added up.");

            var used = Candidates.Select(Fixture(50), Pi(), new Requirements(), DateTime.Now);
            var exhausted = Excluded(used, "openrouter");
            Assert(exhausted.Reason == "exhausted" && exhausted.Until.HasValue && exhausted.Models.Count == 2, "A used-up daily counter did not exclude its pool until the reset.");

            NetworkBackoff.Set("experientiallabs", DateTime.Now.AddMinutes(3));
            var limited = Excluded(Candidates.Select(Fixture(10), Pi(), new Requirements(), DateTime.Now), "experientiallabs");
            Assert(limited.Reason == "rate_limited" && limited.Until > DateTime.Now, "A 429 backoff did not exclude the service.");
            NetworkBackoff.Clear();

            Assert(Excluded(Candidates.Select(Fixture(10), new RuntimeScan(), new Requirements(), DateTime.Now), "openrouter").Reason == "no_runtime", "A missing runtime was not the reason.");
            var off = new RuntimeScan { KeyDiscoveryOff = true };
            off.Found.Add(new RuntimePlace { Runtime = "opencode", Where = "windows" });
            Assert(Excluded(Candidates.Select(Fixture(10), off, new Requirements(), DateTime.Now), "openrouter").Reason == "key_discovery_off", "Key discovery being off was not the reason.");
        }

        private static void SeveralAgents()
        {
            NetworkBackoff.Clear();
            var scan = Pi();
            scan.Found.Add(new RuntimePlace { Runtime = "opencode", Where = "windows" });
            scan.Access.Add(Candidates.Access("openrouter", "opencode", "windows", "key-openrouter-0123456789"));
            var s = Candidates.Select(Fixture(10), scan, new Requirements { Service = "openrouter" }, DateTime.Now);
            var places = s.Chosen.Select(c => c.Model.Id + "@" + c.Access.Runtime + "/" + c.Access.Where).ToList();
            Assert(places.SequenceEqual(new[] { "a/small:free@opencode/windows", "a/small:free@pi/wsl", "z/large:free@opencode/windows", "z/large:free@pi/wsl" }),
                "Each agent with a key should be a candidate, in a fixed order: " + string.Join(", ", places));
            Assert(s.Chosen.Select(c => c.Pool).Distinct().Count() == 1 && s.Chosen.All(c => c.Room.MaxNewJobs == 1), "Two agents doubled the pool's room.");
        }

        private static void Deterministic()
        {
            NetworkBackoff.Clear();
            var first = Order(Candidates.Select(Fixture(10), Pi(), new Requirements { CostPolicy = Candidates.FreeOrUnconfirmed }, DateTime.Now));
            var reversed = Fixture(10);
            reversed.Providers.Reverse();
            foreach (var p in reversed.Providers) p.Catalogue.Models.Reverse();
            var scan = Pi();
            scan.Access.Reverse();
            var second = Order(Candidates.Select(reversed, scan, new Requirements { CostPolicy = Candidates.FreeOrUnconfirmed }, DateTime.Now));
            Assert(first.SequenceEqual(second), "The order depends on input order: " + string.Join(", ", second));
        }

        private static string Home(params string[] pathsAndContents)
        {
            string home = Path.Combine(Settings.Folder, "home-" + Guid.NewGuid().ToString("N"));
            for (int i = 0; i < pathsAndContents.Length; i += 2)
            {
                string file = Path.Combine(home, pathsAndContents[i]);
                Directory.CreateDirectory(Path.GetDirectoryName(file));
                File.WriteAllText(file, pathsAndContents[i + 1], new UTF8Encoding(false));
            }
            return home;
        }

        private static void AgentKeys()
        {
            var d = ServiceDefinition.FromJson("{ \"id\": \"openrouter\", \"name\": \"OpenRouter\", \"auth\": { \"type\": \"key\", \"ids\": [\"openrouter\"], \"env\": [\"OPENROUTER_API_KEY\"] } }", "test");
            var runtimeKey = typeof(Candidates).GetMethod("RuntimeKey", BindingFlags.Static | BindingFlags.NonPublic);
            var pi = Candidates.Kinds.Single(k => k.Id == "pi");
            var openCode = Candidates.Kinds.Single(k => k.Id == "opencode");
            Func<RuntimeKind, string, object> piKeyIn = (kind, home) => runtimeKey.Invoke(null, new object[] { kind, d, home, true, null });
            // Both apps keep a key: every key is listed with its app, in the order Find prefers.
            var both = Home(@".local\share\opencode\auth.json", "{ \"openrouter\": { \"type\": \"api\", \"key\": \"sk-or-v1-opencode-0123456789\" } }",
                            @".pi\agent\auth.json", "{ \"openrouter\": { \"type\": \"api_key\", \"key\": \"sk-or-v1-pi-0123456789\" } }");
            var all = KeyFinder.SearchAll(d, both, true).ToList();
            Assert(all.Count == 2 && all[0].App == "OpenCode" && all[1].App == "Pi", "Keys were not listed with their apps.");
            Assert((string)piKeyIn(pi, both) == "sk-or-v1-pi-0123456789", "Pi was given another app's key.");
            Assert((string)piKeyIn(openCode, both) == "sk-or-v1-opencode-0123456789", "OpenCode was not given its own key.");

            var openCodeOnly = Home(@".local\share\opencode\auth.json", "{ \"openrouter\": { \"type\": \"api\", \"key\": \"sk-or-v1-opencode-0123456789\" } }");
            Assert(piKeyIn(pi, openCodeOnly) == null, "A key Pi does not have counted as Pi's.");
            Assert(piKeyIn(openCode, openCodeOnly) != null, "OpenCode's own key was not found.");
            var profile = Home(".bashrc", "export OPENROUTER_API_KEY=sk-or-v1-profile-0123456789\n");
            Assert((string)piKeyIn(pi, profile) == "sk-or-v1-profile-0123456789" && (string)piKeyIn(openCode, profile) == "sk-or-v1-profile-0123456789",
                "A WSL profile export every agent inherits was not counted.");
            Assert(Candidates.Kinds.Select(k => k.Id).SequenceEqual(new[] { "opencode", "pi", "crush", "antigravity", "claude", "codex", "cursor", "gemini" }),
                "The known agents changed without updating this test.");
            Assert(Candidates.Kinds.Where(k => k.Dispatch != null).Select(k => k.Id).SequenceEqual(new[] { "opencode", "pi" }), "The agents delegation runs changed without updating this test.");

            // A configuration folder can outlive its agent: the program must be installed too (checked without starting WSL).
            string root = Path.Combine(Settings.Folder, "distro-" + Guid.NewGuid().ToString("N"));
            string user = Path.Combine(root, @"home\someone");
            Directory.CreateDirectory(Path.Combine(user, @".config\opencode"));
            Assert(!Candidates.Installed(openCode, user, true), "A leftover folder counted as an installed agent.");
            Directory.CreateDirectory(Path.Combine(root, @"usr\local\bin"));
            File.WriteAllText(Path.Combine(root, @"usr\local\bin\opencode"), "");
            Assert(Candidates.Installed(openCode, user, true), "A program in /usr/local/bin was not found.");
            Directory.CreateDirectory(Path.Combine(user, @".npm-global\bin"));
            File.WriteAllText(Path.Combine(user, @".npm-global\bin\pi"), "");
            Assert(Candidates.Installed(pi, user, true), "A program in the npm global folder was not found.");

            var a = Candidates.Access("openrouter", "pi", "wsl", "same-key-0123456789");
            var b = Candidates.Access("openrouter", "pi", "windows", "same-key-0123456789");
            var c = Candidates.Access("openrouter", "pi", "wsl", "other-key-0123456789");
            Assert(a.Credential == b.Credential && a.Credential != c.Credential && a.Credential.StartsWith("cred_"), "Credential ids do not follow the key.");
            Assert(!a.Credential.Contains("same-key"), "A credential id shows the key.");
        }

        private static void CandidatesTool()
        {
            NetworkBackoff.Clear();
            var snap = Fixture(10);
            var server = new McpServer(() => snap, () => false, null, null, Pi);
            var reply = Json.Parse(server.Handle("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/call\",\"params\":{\"name\":\"list_candidates\",\"arguments\":{\"tools\":true}}}"));
            var content = Json.Obj(Json.Obj(reply, "result"), "structuredContent");
            var candidates = ((IList)content["candidates"]).OfType<Dictionary<string, object>>().ToList();
            Assert(candidates.Count == 3 && Json.Str(candidates[0], "model") == "a/small:free", "Candidates are missing or out of order.");
            var first = candidates[0];
            Assert(Json.Str(Json.Obj(first, "runtime"), "where") == "wsl" && Json.Str(first, "credential").StartsWith("cred_") && Json.Obj(first, "limits") != null, "A candidate lacks its runtime, credential or limits.");
            Assert(!JsonText.Indented(content).Contains("key-openrouter"), "A key reached the answer.");
            var pools = ((IList)content["pools"]).OfType<Dictionary<string, object>>().ToList();
            var router = pools.Single(p => Json.Str(p, "pool") == "openrouter:free");
            Assert(Json.Num(router, "models") == 2 && Json.Num(router, "max_new_jobs_now") == 1 && Json.Num(router, "remaining_share") == 0.8, "The shared pool is wrong.");
            var excluded = ((IList)content["excluded"]).OfType<Dictionary<string, object>>().ToList();
            Assert(excluded.Any(e => Json.Str(e, "service") == "nvidia" && Json.Str(e, "reason") == "price_not_confirmed" && Json.Num(e, "count") == 1), "An exclusion lacks its reason.");
            var found = ((IList)Json.Obj(content, "runtimes")["found"]).OfType<Dictionary<string, object>>().Single();
            Assert(Json.Obj(content, "freshness") != null && Json.Str(found, "name") == "pi" && Json.Str(found, "where") == "wsl", "Freshness or runtimes are missing.");

            var bad = Json.Obj(Json.Obj(Json.Parse(server.Handle("{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/call\",\"params\":{\"name\":\"list_candidates\",\"arguments\":{\"cost_policy\":\"anything\"}}}")), "result"), "structuredContent");
            Assert(bad.ContainsKey("error"), "An unknown cost policy was accepted.");
        }
    }
}
