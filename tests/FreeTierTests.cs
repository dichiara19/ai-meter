using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace AIMeter
{
    /// <summary>
    /// Free tiers (Cloudflare Workers AI): models that are priced but free within a daily allocation. They are candidates
    /// only with consent, only through an agent whose own list prices them (so the plugin can see what a task spends),
    /// and only while the allocation leaves a known budget beyond the reserve, counting the cost of tasks just released.
    /// </summary>
    internal static class FreeTierTests
    {
        internal static void Register(Action<string, Action> test)
        {
            test("A free tier is a candidate only with consent, through an agent that prices it", ConsentAndPricing);
            test("A free tier's budget keeps a reserve, counts released costs before the service does, and closes when unknown", BudgetRules);
            test("claim_capacity grants a free tier's budget and release_capacity's cost shrinks the next one", BudgetThroughMcp);
        }

        private const string Service = "cloudflare-workers-ai", Pool = "cloudflare-workers-ai:neurons", Coder = "@cf/z/coder";
        private const double UsdPerNeuron = 0.000011;

        private static void Assert(bool condition, string message)
        {
            if (!condition) throw new Exception(message);
        }

        private static readonly FreeStatus FreeTier = new FreeStatus { Status = "confirmed_free", Kind = Candidates.FreeTier, Source = "documented" };

        /// <summary>Cloudflare with one coding model and today's Neurons as reported (null: unknown).</summary>
        private static Snapshot Fixture(double? used)
        {
            var catalogue = new Catalogue
            {
                ReadAt = DateTime.Now, Pool = Pool,
                Models = new List<FreeModel> { new FreeModel { Id = Coder, Name = "Coder", Context = 131072, MaxOutput = 16384, Tools = true, Free = FreeTier } },
                Allowance = new Allowance { Metric = "neurons_per_day", Unit = "neurons", UsdPerUnit = UsdPerNeuron, Reserve = 1000 },
            };
            catalogue.Rates.Add(new RateLimit
            {
                Metric = "neurons_per_day", Limit = 10000, Used = used, Remaining = used.HasValue ? 10000 - used : null, Scope = "account",
                Source = "provider_reported", ResetsAt = DateTime.UtcNow.Date.AddDays(1).ToLocalTime(), ResetZone = "UTC",
            });
            var snap = new Snapshot();
            snap.Providers.Add(new ProviderState { Id = Service, Name = "Cloudflare Workers AI", Catalogue = catalogue });
            return snap;
        }

        /// <summary>Pi in WSL with a Cloudflare key; its own list prices the coding model unless priced is false.</summary>
        private static RuntimeScan Pi(bool listed = true, bool priced = true)
        {
            string home = Path.Combine(Settings.Folder, "free-tier-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(home);
            string list = Path.Combine(home, Service + ".json");
            string entry = !listed ? "" : "\"chat:" + Coder + "\": { \"type\": \"chat\", \"id\": \"" + Coder + "\", \"provider\": \"" + Service + "\", " +
                "\"contextWindow\": 131072, \"maxTokens\": 16384, \"cost\": { \"input\": " + (priced ? "0.35" : "0") + ", \"output\": " + (priced ? "0.75" : "0") + " } }";
            File.WriteAllText(list, "{ \"openai-completions\": { " + entry + " } }", new UTF8Encoding(false));
            var a = Candidates.Access(Service, "pi", "wsl", "cf-key-0123456789abcdef");
            a.Provider = Service;
            a.Program = "/home/me/.npm-global/bin/pi";
            a.ModelsFile = list;
            a.ModelsFormat = "pi";
            var scan = new RuntimeScan();
            scan.Found.Add(new RuntimePlace { Runtime = "pi", Where = "wsl" });
            scan.Access.Add(a);
            return scan;
        }

        private static string Reason(Selection s) { return s.Excluded.Single().Reason; }

        private static void ConsentAndPricing()
        {
            NetworkBackoff.Clear();
            var now = DateTime.Now;
            Assert(Reason(Candidates.Select(Fixture(2500), Pi(), new Requirements { Tools = true }, now)) == "free_tier_not_allowed",
                "A free tier was offered without the request accepting it.");
            Assert(Reason(Candidates.Select(Fixture(2500), Pi(), new Requirements { Tools = true, CostPolicy = Candidates.FreeOrUnconfirmed }, now)) == "free_tier_not_allowed",
                "free_or_unconfirmed accepted a free tier: only include_free_tier does.");

            var chosen = Candidates.Select(Fixture(2500), Pi(), new Requirements { Tools = true, FreeTier = true }, now).Chosen.Single();
            Assert(chosen.Pool == Pool && chosen.Room.CanStart && Math.Abs(chosen.Room.Budget.Spendable.Value - 6500) < 1e-9,
                "With consent, the free tier was not offered with what is left beyond the reserve.");

            // The plugin stops the agent when its priced requests reach the budget: an agent that prices nothing hides spend.
            Assert(Reason(Candidates.Select(Fixture(2500), Pi(priced: false), new Requirements { FreeTier = true }, now)) == "agent_price_unknown",
                "A free tier was offered through an agent that lists it at no price.");
            Assert(Reason(Candidates.Select(Fixture(2500), Pi(listed: false), new Requirements { FreeTier = true }, now)) == "agent_price_unknown",
                "A free tier was offered through an agent that does not list it.");
            var best = Candidates.Select(Fixture(2500), Pi(listed: false), new Requirements { FreeTier = true, BestEffort = true }, now);
            Assert(best.BestEffort.Count == 0 && best.BestEffortAvailable == 0, "A free tier became a best-effort candidate.");
        }

        private static void BudgetRules()
        {
            NetworkBackoff.Clear();
            var now = DateTime.Now;
            var book = new LeaseBook(null);
            var catalogue = Fixture(2500).Providers.Single().Catalogue;
            var budget = Candidates.BudgetOf(catalogue, book, Pool, now);
            Assert(budget.Limit == 10000 && budget.Used == 2500 && budget.Spendable == 6500 && Math.Abs(budget.MaxCost.Value - 6500 * UsdPerNeuron) < 1e-12,
                "The budget is not the allocation left beyond the reserve, in Neurons and dollars.");

            // A task just released spent 7,500 Neurons that the service's count does not show yet.
            var lease = book.Claim(Pool, Service, Coder, "pi", null, 600, 1, now);
            book.Release(lease.Id, new LeaseBook.Outcome { Result = "completed", Requests = 4, Cost = 7500 * UsdPerNeuron }, now);
            budget = Candidates.BudgetOf(catalogue, book, Pool, now);
            Assert(Math.Abs(budget.Used.Value - 7500) < 1e-6 && Math.Abs(budget.Spendable.Value - 1500) < 1e-6, "Released costs did not count before the service's own count.");
            // Once the service has counted it (and more), its count is used, never the two added up.
            budget = Candidates.BudgetOf(Fixture(8000).Providers.Single().Catalogue, book, Pool, now);
            Assert(Math.Abs(budget.Used.Value - 8000) < 1e-6 && Math.Abs(budget.Spendable.Value - 1000) < 1e-6, "The service's count and released costs were added up.");

            var room = Candidates.Room(Service, catalogue.Rates, now, book, Pool, Candidates.BudgetOf(Fixture(9200).Providers.Single().Catalogue, book, Pool, now));
            Assert(room.Blocking == "exhausted" && room.Metric == "neurons_per_day" && room.Until == DateTime.UtcNow.Date.AddDays(1).ToLocalTime() && !room.CanStart,
                "The reserve did not close the pool until the next UTC day.");
            var unknown = Fixture(null).Providers.Single().Catalogue;
            room = Candidates.Room(Service, unknown.Rates, now, book, Pool, Candidates.BudgetOf(unknown, book, Pool, now));
            Assert(room.Blocking == "allowance_unknown" && !room.CanStart, "An unknown use of the allocation left the pool open.");
            Assert(Candidates.BudgetOf(new Catalogue { Models = new List<FreeModel>() }, book, Pool, now) == null, "A catalogue without an allowance has a budget.");
        }

        private static Dictionary<string, object> Call(McpServer server, string tool, string arguments)
        {
            var reply = Json.Parse(server.Handle("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/call\",\"params\":{\"name\":\"" + tool + "\",\"arguments\":" + arguments + "}}"));
            return Json.Obj(Json.Obj(reply, "result"), "structuredContent");
        }

        private static void BudgetThroughMcp()
        {
            NetworkBackoff.Clear();
            Settings.Current.WslDistro = "Ubuntu-24.04";
            var snap = Fixture(2500);
            var scan = Pi();
            var server = new McpServer(() => snap, () => false, null, null, () => scan, new LeaseBook(null));
            string candidate = "{\"service\":\"" + Service + "\",\"model\":\"" + Coder + "\",\"runtime\":\"pi\"";

            var listed = Call(server, "list_candidates", "{\"tools\":true}");
            var excluded = ((IList)listed["excluded"]).OfType<Dictionary<string, object>>().Single();
            Assert(((IList)listed["candidates"]).Count == 0 && Json.Str(excluded, "reason") == "free_tier_not_allowed", "list_candidates offered a free tier without consent.");
            listed = Call(server, "list_candidates", "{\"tools\":true,\"include_free_tier\":true}");
            var offered = ((IList)listed["candidates"]).OfType<Dictionary<string, object>>().Single();
            Assert(((IList)offered["warnings"]).Contains(McpServer.FreeTierBudget), "The free-tier candidate does not warn about its budget.");
            var pool = ((IList)listed["pools"]).OfType<Dictionary<string, object>>().Single();
            Assert(Json.Num(Json.Obj(pool, "budget"), "spendable") == 6500, "The pool does not show its budget.");

            var plan = Call(server, "describe_dispatch", candidate + ",\"where\":\"wsl\",\"workspace\":\"C:\\\\w\"}");
            Assert(Json.Bool(plan, "supported") && ((IList)plan["warnings"]).Contains(McpServer.FreeTierBudget), "describe_dispatch does not warn about the budget.");

            var claim = Call(server, "claim_capacity", candidate + ",\"client\":\"claude-code\"}");
            var budget = Json.Obj(claim, "budget");
            Assert(Json.Str(claim, "lease_id") != null && Json.Num(budget, "max_cost") == Math.Round(6500 * UsdPerNeuron, 6) && Json.Str(budget, "currency") == "USD" &&
                   Json.Str(budget, "unit") == "neurons", "claim_capacity did not grant the budget in dollars.");
            Assert(Call(server, "release_capacity", "{\"lease_id\":\"" + Json.Str(claim, "lease_id") + "\",\"outcome\":\"completed\",\"cost\":-1}").ContainsKey("error"),
                "A negative cost was accepted.");
            var released = Call(server, "release_capacity", "{\"lease_id\":\"" + Json.Str(claim, "lease_id") + "\",\"outcome\":\"completed\",\"requests\":5,\"cost\":0.0825}");
            Assert(Json.Bool(released, "released"), "Release with a cost failed.");

            // 0.0825 USD is 7,500 Neurons, which Cloudflare has not reported yet: 1,500 are left beyond the reserve.
            budget = Json.Obj(Call(server, "claim_capacity", candidate + "}"), "budget");
            Assert(Math.Abs(Json.Num(budget, "spendable").Value - 1500) < 1e-6 && Math.Abs(Json.Num(budget, "leased_cost_today").Value - 0.0825) < 1e-9,
                "The released cost did not shrink the next budget.");

            snap = Fixture(null);
            var refused = Call(server, "claim_capacity", candidate + "}");
            Assert(Json.Bool(refused, "refused") && Json.Str(Json.Obj(Json.Obj(refused, "headroom"), "blocking"), "reason") == "allowance_unknown",
                "A lease was granted while the allocation's use was unknown.");
        }
    }
}
