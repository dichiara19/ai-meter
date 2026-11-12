using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace AIMeter
{
    /// <summary>
    /// Delegation on subscription plans (ADR 0003): when a plan is offered, why it is not, where it stands against free
    /// models, and how describe_dispatch and claim_capacity describe and guard a plan agent.
    /// </summary>
    internal static class PlanTests
    {
        internal static void Register(Action<string, Action> test)
        {
            test("A plan is offered only when asked for, allowed in Settings and not the coordinator's own", PolicyGate);
            test("A plan needs a fresh reading of a plan sign-in, no paid usage beyond it and room above the reserve", PlanRoomRules);
            test("Free models come first, a plan whose room is about to be lost before them, in the plan's model order", PlanOrder);
            test("Codex's own model list gives the ChatGPT plan's models in its order", CodexModelList);
            test("list_candidates reports a plan candidate with its windows, reserve and reason", PlanCandidateTool);
            test("describe_dispatch starts a plan agent from a clean environment, with a sign-in check", PlanDispatch);
            test("claim_capacity checks the plan again and names the windows it draws on", PlanClaim);
        }

        private static void Assert(bool condition, string message)
        {
            if (!condition) throw new Exception(message);
        }

        /// <summary>A plan's usage: the 5-hour and weekly windows with the share used and their resets.</summary>
        private static Usage PlanUsage(string signIn, double fiveHoursUsed, double weekUsed, DateTime now, TimeSpan? fiveHoursLeft = null, TimeSpan? weekLeft = null)
        {
            var u = new Usage { Source = "live", At = now.AddMinutes(-2), SignIn = signIn };
            u.Windows.Add(new UsageWindow { Label = "Session 5 h", Short = UsageWindow.ShortFiveHours, Used = fiveHoursUsed, ResetsAt = now.Add(fiveHoursLeft ?? TimeSpan.FromHours(3)) });
            u.Windows.Add(new UsageWindow { Label = "Week", Short = UsageWindow.ShortWeek, Used = weekUsed, ResetsAt = now.Add(weekLeft ?? TimeSpan.FromDays(4)) });
            return u;
        }

        /// <summary>Claude and ChatGPT plans with room, and OpenRouter's one free model.</summary>
        private static Snapshot Fixture(DateTime now, Usage claude = null, Usage openai = null)
        {
            var snap = new Snapshot();
            snap.Providers.Add(new ProviderState { Id = "claude", Name = "Claude", Usage = claude ?? PlanUsage("plan", 20, 30, now) });
            snap.Providers.Add(new ProviderState { Id = "openai", Name = "ChatGPT", Usage = openai ?? PlanUsage("chatgpt", 10, 10, now) });
            var router = new Catalogue { ReadAt = now, Pool = "openrouter:free", Models = new List<FreeModel>
                { new FreeModel { Id = "z/large:free", Name = "Large", Context = 262144, MaxOutput = 32768, Tools = true, Free = FreeStatus.ZeroPrice } } };
            snap.Providers.Add(new ProviderState { Id = "openrouter", Name = "OpenRouter", Catalogue = router });
            return snap;
        }

        /// <summary>Claude Code (native) and Codex (npm) in WSL on their plans, and Pi with an OpenRouter key.</summary>
        private static RuntimeScan Scan()
        {
            var scan = new RuntimeScan();
            scan.Found.Add(new RuntimePlace { Runtime = "claude", Where = "wsl", Distro = "Test-1" });
            scan.Found.Add(new RuntimePlace { Runtime = "codex", Where = "wsl", Distro = "Test-1" });
            scan.Found.Add(new RuntimePlace { Runtime = "pi", Where = "wsl", Distro = "Test-1" });
            var claude = Candidates.PlanAccess("claude", "claude", "wsl", "Test-1");
            claude.Program = "/home/me/.local/bin/claude";
            claude.Home = "/home/me";
            scan.Access.Add(claude);
            var codex = Candidates.PlanAccess("openai", "codex", "wsl", "Test-1");
            codex.Program = "/home/me/.npm-global/bin/codex";
            codex.Home = "/home/me";
            codex.NeedsNode = true;
            codex.NodeFolder = "/usr/bin";
            codex.ModelsFile = CodexModels();
            codex.ModelsFormat = "codex";
            scan.Access.Add(codex);
            var pi = Candidates.Access("openrouter", "pi", "wsl", "key-openrouter-0123456789");
            pi.Distro = "Test-1";
            pi.Program = "/home/me/.npm-global/bin/pi";
            scan.Access.Add(pi);
            return scan;
        }

        private static string codexModels;

        /// <summary>A Codex model cache: two models for people to pick, in priority order, and a hidden one.</summary>
        private static string CodexModels()
        {
            if (codexModels != null) return codexModels;
            codexModels = Path.Combine(Settings.Folder, "codex-models-" + Guid.NewGuid().ToString("N") + ".json");
            File.WriteAllText(codexModels, "{ \"identity\": \"not read\", \"models\": [" +
                "{ \"slug\": \"gpt-b\", \"display_name\": \"GPT-B\", \"visibility\": \"list\", \"priority\": 2, \"context_window\": 200000 }," +
                "{ \"slug\": \"gpt-hidden\", \"visibility\": \"hide\", \"priority\": 0 }," +
                "{ \"slug\": \"gpt-a\", \"display_name\": \"GPT-A\", \"visibility\": \"list\", \"priority\": 1, \"context_window\": 400000 } ] }", new UTF8Encoding(false));
            return codexModels;
        }

        private static Requirements Asking(string coordinator, params string[] allowed)
        {
            return new Requirements { Tools = true, Plan = true, Coordinator = coordinator, PlansAllowed = allowed };
        }

        private static List<string> Order(Selection s) { return s.Chosen.Select(c => c.Provider.Id + "/" + c.Model.Id + "@" + c.Access.Runtime).ToList(); }

        private static string ReasonFor(Selection s, string service, string model)
        {
            var e = s.Excluded.FirstOrDefault(x => x.Service == service && x.Models.Contains(model));
            return e == null ? null : e.Reason;
        }

        private static void PolicyGate()
        {
            NetworkBackoff.Clear();
            var now = DateTime.Now;
            var plain = Candidates.Select(Fixture(now), Scan(), new Requirements { Tools = true }, now);
            Assert(Order(plain).SequenceEqual(new[] { "openrouter/z/large:free@pi" }), "A plan was offered without include_plan: " + string.Join(", ", Order(plain)));
            Assert(ReasonFor(plain, "claude", "sonnet") == "plan_not_requested" && ReasonFor(plain, "openai", "gpt-a") == "plan_not_requested", "Plans not asked for were not reported as such.");

            var off = Candidates.Select(Fixture(now), Scan(), Asking("openai"), now);
            Assert(ReasonFor(off, "claude", "sonnet") == "plan_off_in_settings", "A plan the user did not allow was not excluded for it.");
            var noCoordinator = Candidates.Select(Fixture(now), Scan(), Asking(null, "claude", "openai"), now);
            Assert(ReasonFor(noCoordinator, "claude", "opus") == "coordinator_unknown" && ReasonFor(noCoordinator, "openai", "gpt-a") == "coordinator_unknown",
                "Without a coordinator every plan must be excluded.");

            var fromClaude = Candidates.Select(Fixture(now), Scan(), Asking("claude", "claude", "openai"), now);
            Assert(ReasonFor(fromClaude, "claude", "sonnet") == "same_plan_as_coordinator", "The coordinator's own plan was offered.");
            Assert(Order(fromClaude).SequenceEqual(new[] { "openrouter/z/large:free@pi", "openai/gpt-a@codex", "openai/gpt-b@codex" }),
                "A coordinator in Claude Code must get Codex on the ChatGPT plan after free models: " + string.Join(", ", Order(fromClaude)));
            var fromCodex = Candidates.Select(Fixture(now), Scan(), Asking("openai", "claude", "openai"), now);
            Assert(ReasonFor(fromCodex, "openai", "gpt-a") == "same_plan_as_coordinator" &&
                Order(fromCodex).Skip(1).SequenceEqual(new[] { "claude/sonnet@claude", "claude/opus@claude", "claude/fable@claude", "claude/haiku@claude" }),
                "A coordinator in Codex must get Claude Code's models in the plan's order: " + string.Join(", ", Order(fromCodex)));
            var claudeOnly = Candidates.Select(Fixture(now), Scan(), new Requirements { Tools = true, Plan = true, Coordinator = "openai", PlansAllowed = new[] { "claude" }, Service = "claude" }, now);
            Assert(claudeOnly.Chosen.All(c => c.Provider.Id == "claude") && claudeOnly.Chosen.Count == 4, "service did not narrow the plans.");
        }

        private static void PlanRoomRules()
        {
            NetworkBackoff.Clear();
            var now = DateTime.Now;
            Func<Usage, string> reason = u => ReasonFor(Candidates.Select(Fixture(now, u), Scan(), Asking("openai", "claude"), now), "claude", "sonnet");

            var stale = PlanUsage("plan", 10, 10, now);
            stale.At = now.AddMinutes(-16);
            Assert(reason(stale) == "plan_usage_unknown", "A reading older than 15 minutes was trusted.");
            Assert(ReasonFor(Candidates.Select(Fixture(now), Scan(), Asking("openai", "claude"), now), "claude", "sonnet") == null, "A fresh reading was not trusted.");
            var snapNone = Fixture(now);
            snapNone.Providers.First(p => p.Id == "claude").Usage = null;
            Assert(ReasonFor(Candidates.Select(snapNone, Scan(), Asking("openai", "claude"), now), "claude", "sonnet") == "plan_usage_unknown", "A plan without a reading was offered.");

            var appCache = PlanUsage(null, 10, 10, now);   // The Claude app's cache: no word on the sign-in or extra usage.
            Assert(reason(appCache) == "plan_sign_in_not_confirmed", "A reading that does not show a plan sign-in was trusted.");
            var apiKey = PlanUsage("apikey", 10, 10, now);
            Assert(ReasonFor(Candidates.Select(Fixture(now, null, apiKey), Scan(), Asking("claude", "openai"), now), "openai", "gpt-a") == "plan_sign_in_not_confirmed",
                "Codex signed in with an API key was offered.");

            var extra = PlanUsage("plan", 10, 10, now);
            extra.ExtraActive = true;
            Assert(reason(extra) == "plan_extra_usage_on", "Claude with extra usage on was offered.");
            var credits = PlanUsage("chatgpt", 10, 10, now);
            credits.CreditsAvailable = true;
            Assert(ReasonFor(Candidates.Select(Fixture(now, null, credits), Scan(), Asking("claude", "openai"), now), "openai", "gpt-b") == "plan_credits_on",
                "Codex with credits it would spend past the plan was offered.");

            // Default reserves: 40% of the 5-hour window, 25% of the weekly one; the plan reopens when the window resets.
            var low = PlanUsage("plan", 61, 10, now);
            var lowSelection = Candidates.Select(Fixture(now, low), Scan(), Asking("openai", "claude"), now);
            var lowExclusion = lowSelection.Excluded.Single(e => e.Service == "claude");
            Assert(lowExclusion.Reason == "plan_reserve" && lowExclusion.Until == low.Windows[0].ResetsAt && lowExclusion.Models.Count == 4, "The 5-hour reserve did not exclude the plan until its reset.");
            Assert(reason(PlanUsage("plan", 59, 74, now)) == null, "Room above both reserves was refused.");
            Assert(reason(PlanUsage("plan", 10, 76, now)) == "plan_reserve", "The weekly reserve was not kept.");
            var custom = Candidates.Select(Fixture(now, PlanUsage("plan", 61, 10, now)), Scan(),
                new Requirements { Tools = true, Plan = true, Coordinator = "openai", PlansAllowed = new[] { "claude" }, Reserves = new Dictionary<string, int> { { "claude|5h", 30 } } }, now);
            Assert(ReasonFor(custom, "claude", "sonnet") == null, "The user's own reserve was not used.");

            // A weekly window kept for Opus binds Opus only.
            var scoped = PlanUsage("plan", 10, 10, now);
            scoped.Windows.Add(new UsageWindow { Label = "Week", Suffix = "Opus", Short = "m", Used = 90, ResetsAt = now.AddDays(2), StartedAt = now.AddDays(-5) });
            var scopedSelection = Candidates.Select(Fixture(now, scoped), Scan(), Asking("openai", "claude"), now);
            Assert(ReasonFor(scopedSelection, "claude", "opus") == "plan_reserve" && ReasonFor(scopedSelection, "claude", "sonnet") == null, "A model's own window did not bind only that model.");

            PlanRoom plan;
            var room = Candidates.PlanRoomOf(Fixture(now).Providers[0], "claude", "sonnet", null, now, null, out plan);
            Assert(room.MaxNewJobs == 1 && plan.Binding.Name == "7d" && Math.Abs(room.Ratio.Value - (70 - 25) / 75.0) < 1e-9, "The binding window or the room above the reserve is wrong.");
            var book = new LeaseBook(null);
            book.Claim(Candidates.PlanPool("claude"), "claude", "sonnet", "claude", "test", 600, 1, now);
            Assert(Candidates.PlanRoomOf(Fixture(now).Providers[0], "claude", "sonnet", null, now, book, out plan).Blocking == "leased", "A plan must take one task at a time.");
        }

        private static void PlanOrder()
        {
            NetworkBackoff.Clear();
            var now = DateTime.Now;
            // The 5-hour window resets in 40 minutes with room above its reserve: that room is about to be lost.
            var expiring = PlanUsage("plan", 20, 30, now, TimeSpan.FromMinutes(40));
            var s = Candidates.Select(Fixture(now, expiring), Scan(), Asking("openai", "claude"), now);
            Assert(Order(s).First() == "claude/sonnet@claude" && Order(s)[4] == "openrouter/z/large:free@pi", "A plan about to lose its room did not come before free models: " + string.Join(", ", Order(s)));
            Assert(s.Chosen[0].Plan.Expiring && s.Chosen[0].Rank != null, "The candidate does not say its room is about to be lost.");

            var weekEnd = PlanUsage("plan", 20, 30, now, null, TimeSpan.FromHours(20));
            Assert(Order(Candidates.Select(Fixture(now, weekEnd), Scan(), Asking("openai", "claude"), now)).First() == "claude/sonnet@claude", "A week about to reset with room did not come first.");
            var later = PlanUsage("plan", 20, 30, now, TimeSpan.FromMinutes(90), TimeSpan.FromHours(30));
            Assert(Order(Candidates.Select(Fixture(now, later), Scan(), Asking("openai", "claude"), now)).First() == "openrouter/z/large:free@pi", "A plan not about to reset came before free models.");
            var past = PlanUsage("plan", 20, 30, now, TimeSpan.FromMinutes(-5));
            Assert(Order(Candidates.Select(Fixture(now, past), Scan(), Asking("openai", "claude"), now)).First() == "openrouter/z/large:free@pi", "A reset already past counted as about to happen.");
        }

        private static void CodexModelList()
        {
            var access = Scan().Access.Single(a => a.Runtime == "codex");
            var models = Candidates.PlanModels(Agents.Get("codex"), access);
            Assert(models.Select(m => m.Id).SequenceEqual(new[] { "gpt-a", "gpt-b" }) && models[0].Name == "GPT-A" && models[0].Context == 400000,
                "Codex's models were not read in its priority order, without hidden ones.");
            Assert(AgentModels.Parse((Dictionary<string, object>)Json.Parse("{ \"nothing\": 1 }"), "codex") == null, "A file without models was read as a list.");
            var claude = Candidates.PlanModels(Agents.Get("claude"), null);
            Assert(claude.Select(m => m.Id).SequenceEqual(new[] { "sonnet", "opus", "fable", "haiku" }) && claude.All(m => m.Free.Status == "included_in_plan"),
                "Claude Code's plan models are wrong.");
        }

        private static Dictionary<string, object> Call(McpServer server, string tool, string arguments)
        {
            var reply = Json.Parse(server.Handle("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/call\",\"params\":{\"name\":\"" + tool + "\",\"arguments\":" + arguments + "}}"));
            return Json.Obj(Json.Obj(reply, "result"), "structuredContent");
        }

        /// <summary>Run with both plans allowed in Settings, then put the user's settings back.</summary>
        private static void WithPlansAllowed(Action action)
        {
            var plans = Settings.Current.DelegationPlans;
            var reserves = Settings.Current.DelegationReserves;
            try
            {
                Settings.Current.DelegationPlans = new Dictionary<string, bool> { { "claude", true }, { "openai", true } };
                Settings.Current.DelegationReserves = new Dictionary<string, int>();
                action();
            }
            finally
            {
                Settings.Current.DelegationPlans = plans;
                Settings.Current.DelegationReserves = reserves;
            }
        }

        private static void PlanCandidateTool()
        {
            NetworkBackoff.Clear();
            var now = DateTime.Now;
            var snap = Fixture(now);
            var scan = Scan();
            WithPlansAllowed(() =>
            {
                var server = new McpServer(() => snap, () => false, null, null, () => scan);
                var content = Call(server, "list_candidates", "{\"tools\":true,\"include_plan\":true,\"coordinator\":\"openai\"}");
                var candidates = ((IList)content["candidates"]).OfType<Dictionary<string, object>>().ToList();
                var first = candidates.First(c => Json.Str(c, "service") == "claude");
                var plan = Json.Obj(first, "plan");
                var windows = ((IList)plan["windows"]).OfType<Dictionary<string, object>>().ToList();
                Assert(Json.Str(first, "billing") == "included_in_plan" && Json.Str(first, "pool") == "claude:plan" && Json.Str(first, "model") == "sonnet",
                    "A plan candidate lacks its billing, pool or model.");
                Assert(windows.Count == 2 && Json.Str(windows[0], "window") == "5h" && Json.Num(windows[0], "remaining_percent") == 80 && Json.Num(windows[0], "reserve_percent") == 40,
                    "A plan candidate's windows are wrong.");
                Assert(Json.Str(plan, "binding_window") == "7d" && plan.ContainsKey("expiring") && Json.Str(Json.Obj(first, "rank"), "plan") == "fallback",
                    "A plan candidate does not say which window binds or where it stands.");
                Assert(Json.Str(Json.Obj(first, "rank"), "why").Contains("after free models"),
                    "A plan candidate's reason does not say it comes after free models.");
                var excluded = ((IList)content["excluded"]).OfType<Dictionary<string, object>>().ToList();
                Assert(excluded.Any(e => Json.Str(e, "service") == "openai" && Json.Str(e, "reason") == "same_plan_as_coordinator"), "The coordinator's own plan was not excluded through MCP.");
                Assert(((IList)content["pools"]).OfType<Dictionary<string, object>>().Any(p => Json.Str(p, "pool") == "claude:plan" && Json.Num(p, "max_new_jobs_now") == 1),
                    "The plan's pool is missing from pools.");
            });
        }

        private static void PlanDispatch()
        {
            NetworkBackoff.Clear();
            var now = DateTime.Now;
            var snap = Fixture(now);
            var scan = Scan();
            WithPlansAllowed(() =>
            {
                var server = new McpServer(() => snap, () => false, null, null, () => scan);
                var claude = Call(server, "describe_dispatch", "{\"service\":\"claude\",\"model\":\"sonnet\",\"runtime\":\"claude\",\"coordinator\":\"openai\",\"workspace\":\"C:\\\\work\\\\repo\"}");
                var args = ((IList)claude["arguments"]).Cast<object>().Select(a => (string)a).ToList();
                Assert(Json.Bool(claude, "supported") && Json.Str(claude, "program") == "wsl.exe" && Json.Str(claude, "environment_policy") == "clean" &&
                    Json.Str(claude, "billing") == "included_in_plan", "A plan agent's description lacks its clean environment or billing: " + Json.Serialize(claude));
                int env = args.IndexOf("env");
                Assert(env > 0 && args[env + 1] == "-i" && args[env + 2] == "HOME=/home/me" && args.Contains("/home/me/.local/bin/claude"),
                    "Claude Code in WSL must start through env -i with its home: " + string.Join(" ", args));
                Assert(!args.Any(a => a.StartsWith("ANTHROPIC", StringComparison.Ordinal)) && args.Contains("--safe-mode") && args.Contains("--restricted") && args.Contains("sonnet"),
                    "Claude Code's arguments are wrong: " + string.Join(" ", args));
                var check = Json.Obj(claude, "sign_in_check");
                var checkArgs = ((IList)check["arguments"]).Cast<object>().Select(a => (string)a).ToList();
                Assert(Json.Str(check, "program") == "wsl.exe" && checkArgs[checkArgs.IndexOf("--cd") + 1] == "~" && checkArgs.Contains("-i") &&
                    checkArgs.Skip(checkArgs.Count - 3).SequenceEqual(new[] { "auth", "status", "--json" }) && Json.Str(check, "json_field") == "authMethod" && Json.Str(check, "expect") == "claude.ai",
                    "Claude Code's sign-in check is wrong: " + string.Join(" ", checkArgs));

                var codex = Call(server, "describe_dispatch", "{\"service\":\"openai\",\"model\":\"gpt-a\",\"runtime\":\"codex\",\"coordinator\":\"claude\",\"mode\":\"modify\",\"workspace\":\"C:\\\\work\\\\tree\"}");
                var codexArgs = ((IList)codex["arguments"]).Cast<object>().Select(a => (string)a).ToList();
                Assert(Json.Str(codex, "program") == "wsl.exe" && Json.Str(codex, "environment_policy") == "clean" && codexArgs.Contains("workspace-write") &&
                    codexArgs[codexArgs.IndexOf("--cd") + 1] == @"C:\work\tree" && codexArgs[codexArgs.IndexOf("-C") + 1] == "." &&
                    codexArgs.Contains("PATH=/home/me/.npm-global/bin:/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin") &&
                    codexArgs.Contains("forced_login_method=\"chatgpt\"") && codexArgs.Contains("gpt-a"),
                    "Codex in WSL is described wrongly: " + string.Join(" ", codexArgs));
                Assert(Json.Str(Json.Obj(codex, "sign_in_check"), "text") == "Logged in using ChatGPT", "Codex's sign-in check is wrong.");

                // Codex on Windows is not verified: its read-only sandbox refused to read the workspace.
                var onWindows = Candidates.PlanAccess("openai", "codex", "windows", null);
                onWindows.Program = @"C:\Users\me\AppData\Local\OpenAI\Codex\bin\abc\codex.exe";
                var refused = Dispatch.Describe(onWindows, "gpt-a", null, @"C:\work", "windows", null, null);
                Assert(!refused.Supported && refused.Reason.Contains("not been verified on Windows"), "Codex on Windows was described: " + refused.Reason);

                Assert(Json.Str(Call(server, "describe_dispatch", "{\"service\":\"openai\",\"model\":\"gpt-a\",\"runtime\":\"codex\",\"coordinator\":\"openai\"}"), "error").Contains("save nothing"),
                    "describe_dispatch described the coordinator's own plan.");
                Assert(Json.Str(Call(server, "describe_dispatch", "{\"service\":\"openai\",\"model\":\"gpt-a\",\"runtime\":\"codex\"}"), "error").Contains("coordinator"),
                    "describe_dispatch described a plan without a coordinator.");
                Assert(Json.Str(Call(server, "describe_dispatch", "{\"service\":\"openai\",\"model\":\"gpt-zzz\",\"runtime\":\"codex\",\"coordinator\":\"claude\"}"), "error").Contains("does not offer"),
                    "describe_dispatch described a model the plan does not offer.");
            });
            var pi = Call(new McpServer(() => snap, () => false, null, null, () => scan), "describe_dispatch", "{\"service\":\"openrouter\",\"model\":\"z/large:free\",\"runtime\":\"pi\"}");
            Assert(Json.Bool(pi, "supported") && !pi.ContainsKey("environment_policy") && !pi.ContainsKey("sign_in_check"), "A free agent got a plan agent's description.");
            Assert(Json.Str(Call(new McpServer(() => snap, () => false, null, null, () => scan), "describe_dispatch",
                "{\"service\":\"claude\",\"model\":\"sonnet\",\"runtime\":\"claude\",\"coordinator\":\"openai\"}"), "error").Contains("not allowed"), "A plan the user did not allow was described.");
        }

        private static void PlanClaim()
        {
            NetworkBackoff.Clear();
            var now = DateTime.Now;
            var low = PlanUsage("chatgpt", 70, 10, now);
            Snapshot snap = Fixture(now);
            var scan = Scan();
            WithPlansAllowed(() =>
            {
                var book = new LeaseBook(null);
                var server = new McpServer(() => snap, () => false, null, null, () => scan, book);
                const string claim = "{\"service\":\"openai\",\"model\":\"gpt-a\",\"runtime\":\"codex\",\"coordinator\":\"claude\",\"client\":\"test\"}";
                var lease = Call(server, "claim_capacity", claim);
                Assert(Json.Str(lease, "lease_id") != null && Json.Str(lease, "pool") == "openai:plan" && Json.Str(lease, "billing") == "included_in_plan" &&
                    Json.Str(Json.Obj(lease, "plan"), "binding_window") != null, "A plan lease lacks its pool, billing or windows: " + Json.Serialize(lease));
                var second = Call(server, "claim_capacity", claim);
                Assert(Json.Bool(second, "refused") && Json.Str(Json.Obj(Json.Obj(second, "headroom"), "blocking"), "reason") == "leased", "A plan took two tasks at once.");
                Call(server, "release_capacity", "{\"lease_id\":\"" + Json.Str(lease, "lease_id") + "\",\"outcome\":\"completed\",\"requests\":2}");

                snap = Fixture(now, null, low);   // The 5-hour window fell below the reserve since the list was made.
                var refused = Call(server, "claim_capacity", claim);
                Assert(Json.Bool(refused, "refused") && Json.Str(Json.Obj(Json.Obj(refused, "headroom"), "blocking"), "reason") == "plan_reserve", "A reserve reached after the list did not refuse the lease.");
                Assert(book.Record("openai", "gpt-a", now.AddMinutes(1)).Completed == 1, "A plan task was not counted in the model's track record.");
            });
        }
    }
}
