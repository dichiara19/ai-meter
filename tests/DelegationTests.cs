using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace AIMeter
{
    /// <summary>Dispatch descriptions and capacity leases: what coordinator plugins get from AI Meter to run agents (ADR 0002).</summary>
    internal static class DelegationTests
    {
        internal static void Register(Action<string, Action> test)
        {
            test("Dispatch describes verified agents as argument lists, never with the task or a shell string", DispatchRows);
            test("Unverified agents, places and versions are refused or flagged, never guessed", DispatchLimits);
            test("Leases count against a pool, expire, close it after a rate limit and keep only counters", LeaseBookRules);
            test("Leases survive a restart of AI Meter", LeasePersistence);
            test("Candidates and headroom count active leases", LeasedRoom);
            test("The delegation tools claim, describe and release through MCP", DelegationTools);
        }

        private static void Assert(bool condition, string message)
        {
            if (!condition) throw new Exception(message);
        }

        private const string Model = "nvidia/nemotron-3.5-lightning:free";

        private static RuntimeAccess Access(string runtime, string where, string provider = "openrouter")
        {
            var a = Candidates.Access("openrouter", runtime, where, "key-0123456789abcdef");
            a.Provider = provider;
            a.Program = where == "wsl" ? "/home/me/.npm-global/bin/" + runtime : null;
            return a;
        }

        private static void DispatchRows()
        {
            // Pi in WSL, started by a plugin on Windows: wsl.exe -e (no Linux shell), env with a PATH, the agent by its full path.
            var pi = Dispatch.Describe(Access("pi", "wsl"), Model, null, @"C:\src\project", "windows", "Ubuntu-24.04", "1.4.0");
            Assert(pi.Supported && pi.VersionVerified && pi.Program == "wsl.exe", "Pi in WSL from Windows is not described.");
            var head = new[] { "-d", "Ubuntu-24.04", "--cd", @"C:\src\project", "-e", "env",
                "PATH=/home/me/.npm-global/bin:/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin", "/home/me/.npm-global/bin/pi" };
            Assert(pi.Arguments.Take(head.Length).SequenceEqual(head), "The wsl.exe wrapper is wrong: " + string.Join(" | ", pi.Arguments));
            Assert(pi.Arguments.Contains("--mode") && pi.Arguments.Contains("--no-session") && pi.Arguments.Contains("--no-context-files"), "Pi is not isolated.");
            int tools = pi.Arguments.IndexOf("--tools");
            Assert(tools > 0 && pi.Arguments[tools + 1] == "read,grep,find,ls", "A read-only Pi task can write.");
            Assert(pi.Arguments.Last() == Model && pi.Arguments[pi.Arguments.IndexOf("--provider") + 1] == "openrouter", "Pi's model or provider is wrong.");
            Assert(pi.Output["end"].Contains("agent_settled") && pi.Output["outcome"].Contains("stopReason"), "Pi's end or outcome is not explained.");
            Assert(pi.Notes.Any(n => n.Contains("BYOK")), "The OpenRouter BYOK warning is missing.");

            var piInWsl = Dispatch.Describe(Access("pi", "wsl"), Model, Dispatch.Modify, "/home/me/project", "wsl", "Ubuntu-24.04", "1.4.0");
            Assert(piInWsl.Program == "/home/me/.npm-global/bin/pi" && piInWsl.Arguments[0] == "-p" && piInWsl.WorkingDirectory == "/home/me/project"
                   && piInWsl.Environment["PATH"].StartsWith("/home/me/.npm-global/bin:", StringComparison.Ordinal), "Pi started from WSL is wrong.");
            Assert(!piInWsl.Arguments.Contains("--tools"), "A modifying Pi task kept the read-only tools.");

            // OpenCode on Windows: the native program, isolation through the environment, plan or build agent.
            var oc = Dispatch.Describe(Access("opencode", "windows"), Model, null, @"C:\src\project", "windows", null, "1.19.5");
            Assert(oc.Supported && oc.Program.EndsWith("opencode.exe", StringComparison.OrdinalIgnoreCase) && oc.Arguments[0] == "run", "OpenCode on Windows is not described.");
            Assert(oc.Arguments[oc.Arguments.IndexOf("--agent") + 1] == "plan" && oc.Arguments[oc.Arguments.IndexOf("-m") + 1] == "openrouter/" + Model, "OpenCode's agent or model is wrong.");
            Assert(oc.Environment["OPENCODE_DISABLE_EXTERNAL_SKILLS"] == "1" && oc.Environment["OPENCODE_CONFIG_CONTENT"].Contains("openrouter/" + Model),
                "OpenCode is not isolated, or its small model is not the free model.");
            Assert(oc.Notes.Any(n => n.Contains("stdin")), "The stdin warning is missing.");
            var build = Dispatch.Describe(Access("opencode", "windows"), Model, Dispatch.Modify, null, "windows", null, "1.19.5");
            Assert(build.Arguments[build.Arguments.IndexOf("--agent") + 1] == "build" && build.Arguments.Contains("{workspace}"), "Modify mode or the workspace placeholder is wrong.");

            // OpenCode in WSL from Windows: the variables go inside WSL through env.
            var ocWsl = Dispatch.Describe(Access("opencode", "wsl"), Model, null, @"C:\w", "windows", "Ubuntu-24.04", "1.19.5");
            Assert(ocWsl.Environment.Count == 0 && ocWsl.Arguments.Any(a => a.StartsWith("OPENCODE_CONFIG_CONTENT=", StringComparison.Ordinal)), "OpenCode's variables do not reach WSL.");
            Assert(ocWsl.Arguments[ocWsl.Arguments.IndexOf("--dir") + 1] == "." && ocWsl.Arguments.Contains(@"C:\w"), "A Windows folder was given to OpenCode inside WSL.");

            // The provider is the alias the agent keeps the key under.
            var gemini = Dispatch.Describe(Access("pi", "wsl", "google"), "gemini-2.5-flash", null, null, "wsl", "Ubuntu-24.04", "1.4.0");
            Assert(gemini.Arguments[gemini.Arguments.IndexOf("--provider") + 1] == "google", "The agent's provider alias was not used.");
        }

        private static void DispatchLimits()
        {
            Assert(!Dispatch.Describe(Access("pi", "windows"), Model, null, null, "windows", null, "1.4.0").Supported, "Pi on Windows was described without being verified.");
            Assert(!Dispatch.Describe(Access("crush", "wsl"), Model, null, null, "windows", "Ubuntu-24.04", "0.1").Supported, "Crush was described without being verified.");
            Assert(!Dispatch.Describe(Access("opencode", "windows"), Model, null, null, "wsl", null, "1.19.5").Supported, "A Windows agent from WSL was described without being verified.");
            Assert(!Dispatch.Describe(Access("pi", "wsl"), Model, "anything", null, "windows", "Ubuntu-24.04", "1.4.0").Supported, "An unknown mode was accepted.");
            Assert(!Dispatch.Describe(Access("pi", "wsl"), Model, null, null, "windows", null, "1.4.0").Supported, "A WSL agent without a distribution was described.");
            var lost = Access("pi", "wsl");
            lost.Program = null;
            Assert(!Dispatch.Describe(lost, Model, null, null, "windows", "Ubuntu-24.04", "1.4.0").Supported, "An agent whose program was not found was described.");
            Assert(Candidates.LinuxPath(@"\\wsl.localhost\Ubuntu\home\me\.npm-global\bin\pi", @"\\wsl.localhost\Ubuntu\home\me") == "/home/me/.npm-global/bin/pi",
                "A WSL path was not converted.");
            var newer = Dispatch.Describe(Access("pi", "wsl"), Model, null, null, "windows", "Ubuntu-24.04", "2.0.0");
            Assert(newer.Supported && !newer.VersionVerified && newer.Notes.Any(n => n.Contains("2.0.0")), "A newer major version was not flagged.");
            var unknown = Dispatch.Describe(Access("pi", "wsl"), Model, null, null, "windows", "Ubuntu-24.04", null);
            Assert(!unknown.VersionVerified && unknown.Notes.Any(n => n.Contains("unknown")), "An unknown version was not flagged.");
        }

        private static void LeaseBookRules()
        {
            var book = new LeaseBook(null);
            var now = DateTime.Now;
            var first = book.Claim("p", "openrouter", Model, "pi", "claude-code", 0, 1, now);
            Assert(first != null && first.ExpiresAt == now.AddSeconds(LeaseBook.DefaultTtlSeconds), "The default lease length is wrong.");
            Assert(book.Claim("p", "openrouter", Model, "pi", "codex", 0, 1, now) == null, "A full pool took a second lease.");
            Assert(book.Claim("q", "openrouter", Model, "pi", "codex", 99999, 1, now).ExpiresAt == now.AddSeconds(LeaseBook.MaxTtlSeconds), "A lease outlived the maximum.");
            Assert(book.Renew(first.Id, 60, now) == now.AddSeconds(60), "Renewal did not move the expiry.");

            var released = book.Release(first.Id, new LeaseBook.Outcome { Result = "completed", Requests = 3, RateLimited = 1, RetryAfter = TimeSpan.FromSeconds(30), InputTokens = 9000, OutputTokens = 40 }, now);
            Assert(released != null && book.Release(first.Id, new LeaseBook.Outcome { Result = "failed" }, now) == null, "A second release was not ignored.");
            Assert(book.BackoffUntil("p") == now.AddSeconds(30), "A rate limit did not close the pool until Retry-After.");
            var today = book.Today("p", now);
            Assert(today.Ended == 1 && today.Requests == 3 && today.RateLimited == 1 && today.InputTokens == 9000 && today.InFlight == 0, "Outcome counters are wrong.");

            // An unreleased lease expires with an unknown outcome and frees the pool.
            var lost = book.Claim("r", "openrouter", Model, "pi", null, 60, 1, now);
            Assert(book.Active("r", now.AddSeconds(30)) == 1 && book.Active("r", now.AddSeconds(61)) == 0, "An expired lease still counts.");
            var later = book.Today("r", now.AddSeconds(61));
            Assert(later.Unknown == 1 && later.RequestsUnknown == 1 && book.Renew(lost.Id, 60, now.AddSeconds(61)) == null, "Expiry did not end the lease as unknown.");

            book.Release(book.Claim("s", "openrouter", Model, "pi", null, 60, 1, now).Id, new LeaseBook.Outcome { Result = "failed", RateLimited = 2 }, now);
            Assert(book.BackoffUntil("s") == now + LeaseBook.DefaultRateLimitBackoff, "A rate limit without Retry-After did not close the pool for a minute.");
        }

        private static void LeasePersistence()
        {
            string file = Path.Combine(Settings.Folder, "leases.json");
            var now = DateTime.Now;
            var book = new LeaseBook(file);
            var lease = book.Claim("p", "openrouter", Model, "pi", "claude-code", 600, 1, now);
            book.Release(book.Claim("q", "openrouter", Model, "pi", null, 600, 1, now).Id, new LeaseBook.Outcome { Result = "completed", Requests = 2, RateLimited = 1 }, now);
            string text = File.ReadAllText(file);
            Assert(!text.Contains("key-") && text.Contains("\"version\": 1"), "The lease file holds something it should not, or lacks its version.");

            var reloaded = new LeaseBook(file);
            Assert(reloaded.Active("p", now) == 1 && reloaded.Renew(lease.Id, 60, now).HasValue, "An active lease was lost on restart.");
            Assert(reloaded.Today("q", now).Requests == 2 && reloaded.BackoffUntil("q") > now, "Counters or backoff were lost on restart.");
            File.WriteAllText(file, "{\"version\": 99, \"leases\": [{\"id\": \"x\", \"pool\": \"p\"}]}");
            Assert(new LeaseBook(file).Active("p", now) == 0, "A file of an unknown version was trusted.");
        }

        private static Snapshot Fixture()
        {
            var snap = new Snapshot();
            var catalogue = new Catalogue { ReadAt = DateTime.Now, Pool = "openrouter:free", Models = new List<FreeModel>
                { new FreeModel { Id = Model, Name = "Nemotron", Context = 1000000, MaxOutput = 65536, Tools = true, Free = FreeStatus.ZeroPrice } } };
            catalogue.Rates.Add(new RateLimit { Metric = "requests_per_minute", Limit = 20, Scope = "account", Source = "documented" });
            snap.Providers.Add(new ProviderState { Id = "openrouter", Name = "OpenRouter", Catalogue = catalogue });
            return snap;
        }

        private static RuntimeScan Scan()
        {
            var scan = new RuntimeScan();
            scan.Found.Add(new RuntimePlace { Runtime = "pi", Where = "wsl" });
            scan.Access.Add(Access("pi", "wsl"));
            return scan;
        }

        private static void LeasedRoom()
        {
            NetworkBackoff.Clear();
            var book = new LeaseBook(null);
            var now = DateTime.Now;
            var lease = book.Claim("openrouter:free", "openrouter", Model, "pi", null, 600, 1, now);
            var selection = Candidates.Select(Fixture(), Scan(), new Requirements(), now, book);
            var excluded = selection.Excluded.Single();
            Assert(selection.Chosen.Count == 0 && excluded.Reason == "leased" && excluded.Until == lease.ExpiresAt, "A fully leased pool was offered.");
            book.Release(lease.Id, new LeaseBook.Outcome { Result = "completed", Requests = 1 }, now);
            Assert(Candidates.Select(Fixture(), Scan(), new Requirements(), now, book).Chosen.Count == 1, "A released pool stayed closed.");
        }

        private static Dictionary<string, object> Call(McpServer server, string tool, string arguments)
        {
            var reply = Json.Parse(server.Handle("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/call\",\"params\":{\"name\":\"" + tool + "\",\"arguments\":" + arguments + "}}"));
            return Json.Obj(Json.Obj(reply, "result"), "structuredContent");
        }

        private static void DelegationTools()
        {
            NetworkBackoff.Clear();
            Settings.Current.WslDistro = "Ubuntu-24.04";
            var snap = Fixture();
            var server = new McpServer(() => snap, () => false, null, null, Scan, new LeaseBook(null));
            string candidate = "{\"service\":\"openrouter\",\"model\":\"" + Model + "\",\"runtime\":\"pi\"";

            var plan = Call(server, "describe_dispatch", candidate + ",\"where\":\"wsl\",\"workspace\":\"C:\\\\w\"}");
            var args = ((IList)plan["arguments"]).OfType<string>().ToList();
            Assert(Json.Bool(plan, "supported") && Json.Str(plan, "program") == "wsl.exe" && Json.Str(plan, "task_input") == "stdin" && args.Contains("--model"), "describe_dispatch did not describe Pi.");
            Assert(Json.Str(plan, "pool") == "openrouter:free" && !JsonText.Indented(plan).Contains("key-0123"), "The pool is missing or a key leaked.");
            Assert(Call(server, "describe_dispatch", "{\"service\":\"openrouter\",\"model\":\"nope\",\"runtime\":\"pi\"}").ContainsKey("error"), "An unknown model was described.");

            var claim = Call(server, "claim_capacity", candidate + ",\"client\":\"claude-code\"}");
            string id = Json.Str(claim, "lease_id");
            Assert(id != null && Json.Str(claim, "pool") == "openrouter:free" && Json.Str(claim, "coverage") == "ai_meter_leases_only", "A lease was not granted.");
            var again = Call(server, "claim_capacity", candidate + "}");
            Assert(Json.Bool(again, "refused") && Json.Str(Json.Obj(Json.Obj(again, "headroom"), "blocking"), "reason") == "leased", "A full pool granted a second lease.");

            var limits = Json.Obj(((IList)Call(server, "list_free_models", "{}")["models"]).OfType<Dictionary<string, object>>().Single(), "limits");
            Assert(Json.Num(Json.Obj(limits, "headroom"), "leased") == 1 && Json.Num(Json.Obj(limits, "observed"), "in_flight") == 1, "list_free_models does not show the lease.");

            Assert(Json.Str(Call(server, "renew_capacity", "{\"lease_id\":\"" + id + "\",\"ttl_seconds\":120}"), "expires_at") != null, "Renewal failed.");
            var released = Call(server, "release_capacity", "{\"lease_id\":\"" + id + "\",\"outcome\":\"completed\",\"requests\":3,\"input_tokens\":9000}");
            Assert(Json.Bool(released, "released"), "Release failed.");
            Assert(!Json.Bool(Call(server, "release_capacity", "{\"lease_id\":\"" + id + "\",\"outcome\":\"completed\"}"), "released"), "A second release was not ignored.");
            Assert(Call(server, "release_capacity", "{\"lease_id\":\"" + id + "\",\"outcome\":\"great\"}").ContainsKey("error"), "An unknown outcome was accepted.");
            limits = Json.Obj(((IList)Call(server, "list_free_models", "{}")["models"]).OfType<Dictionary<string, object>>().Single(), "limits");
            Assert(Json.Num(Json.Obj(limits, "observed"), "requests_today") == 3 && Json.Num(Json.Obj(limits, "headroom"), "leased") == 0, "The release did not reach the counters.");
        }
    }
}
