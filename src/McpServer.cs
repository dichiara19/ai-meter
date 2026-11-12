using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Threading;

namespace AIMeter
{
    /// <summary>
    /// Model Context Protocol server: lets AI apps and coding agents read AI Meter's latest snapshot (limits, running
    /// sessions, free models and today's spending). Nothing secret is returned: no keys, tokens or account identities;
    /// session titles only when the user allows them.
    ///
    /// The lease tools (claim_capacity, renew_capacity, release_capacity) are the only ones that change state, and they
    /// write only AI Meter's lease book (leases.json): they start no process and change no settings, keys or service
    /// data. Every other tool is read-only; describe_dispatch describes a command but runs nothing.
    ///
    /// Tools answer from the latest snapshot and add no network requests unless the caller asks with refresh or
    /// max_age_seconds. Such a request goes through RefreshGate to the usual collection worker, and every result says
    /// how fresh each service's data is and what the request achieved ("freshness").
    ///
    /// Clients start mcp.ps1 (McpRelay), which forwards stdin and stdout to the named pipe served by McpPipeHost.
    /// Over the pipe each JSON-RPC message is one line, and every line receives exactly one line back, empty for
    /// notifications, so the relay can work in lockstep without parsing the protocol.
    /// </summary>
    internal sealed class McpServer
    {
        /// <summary>Protocol revisions this server implements; the newest is offered when the client's is unknown.</summary>
        private static readonly string[] Revisions = { "2025-11-25", "2025-06-18", "2025-03-26", "2024-11-05" };

        /// <summary>What a tool's refresh reads again: usage limits, free-model lists, or local state only.</summary>
        private enum Scope { Usage, Catalogue, Local }

        private readonly Func<Snapshot> snapshot;
        private readonly Func<bool> titlesAllowed;
        private readonly RefreshGate gate;   // Null when refreshing is not possible, as in tests of the plain reads.
        private readonly TimeSpan timeout;
        private readonly Func<RuntimeScan> runtimes;
        private readonly LeaseBook leases;

        public McpServer(Func<Snapshot> snapshot, Func<bool> titlesAllowed, RefreshGate gate = null, TimeSpan? timeout = null,
                         Func<RuntimeScan> runtimes = null, LeaseBook leases = null)
        {
            this.leases = leases ?? new LeaseBook(null);
            this.snapshot = snapshot;
            this.titlesAllowed = titlesAllowed;
            this.gate = gate;
            this.timeout = timeout ?? RefreshGate.DefaultTimeout;
            this.runtimes = runtimes ?? (() => new RuntimeScan());
        }

        /// <summary>Handle one JSON-RPC message; returns the response line, or "" for a notification.</summary>
        public string Handle(string line)
        {
            var message = Json.ParseAny(line) as Dictionary<string, object>;
            if (message == null) return Error(null, -32700, "Parse error: one JSON-RPC object per line is expected.");
            object id;
            message.TryGetValue("id", out id);
            string method = Json.Str(message, "method");
            if (method == null) return id == null ? "" : Error(id, -32600, "Invalid request.");
            if (id == null) return "";   // Notifications, such as notifications/initialized, need no answer.

            try
            {
                var parameters = Json.Obj(message, "params") ?? new Dictionary<string, object>();
                switch (method)
                {
                    case "initialize": return Result(id, Initialise(parameters));
                    case "ping": return Result(id, new Dictionary<string, object>());
                    case "tools/list": return Result(id, new Dictionary<string, object> { { "tools", Tools() } });
                    case "tools/call": return Result(id, Call(parameters));
                    default: return Error(id, -32601, "Method not found: " + method);
                }
            }
            catch (Exception ex)
            {
                Program.LogError("mcp", ex);
                return Error(id, -32603, "Internal error.");
            }
        }

        private static Dictionary<string, object> Initialise(Dictionary<string, object> parameters)
        {
            string asked = Json.Str(parameters, "protocolVersion");
            return new Dictionary<string, object>
            {
                { "protocolVersion", Revisions.Contains(asked) ? asked : Revisions[0] },
                { "capabilities", new Dictionary<string, object> { { "tools", new Dictionary<string, object>() } } },
                { "serverInfo", new Dictionary<string, object> { { "name", "ai-meter" }, { "title", "AI Meter" }, { "version", Controller.Version } } },
                { "instructions", "AI Meter reports the user's AI usage limits, running agent sessions, free models and today's spending, " +
                                  "as read on this PC. Use get_limits or suggest_service before long tasks to check that there is room, " +
                                  "and list_free_models to find a model that costs nothing. Answers come from AI Meter's latest reading; " +
                                  "pass max_age_seconds (or refresh) when you need newer data, and check each service's freshness. " +
                                  "Percentages are 0-100; times are local ISO 8601." },
            };
        }

        // ------------------------------------------------------------ tools

        private static List<object> Tools()
        {
            var service = Property("string", "Optional service id, such as claude, openai, copilot or openrouter. All services when omitted.");
            string minimum = RefreshGate.MinimumSeconds.ToString(CultureInfo.InvariantCulture);
            Func<string, Dictionary<string, object>> refresh = what => Property("boolean",
                "Ask the services for " + what + " now instead of using AI Meter's latest reading. A service is asked at most once every " +
                minimum + " seconds and never before the time it set after a rate limit. Waits up to " + (int)RefreshGate.DefaultTimeout.TotalSeconds + " seconds.");
            Func<string, Dictionary<string, object>> maxAge = what => Property("integer",
                "Ask the services for " + what + " only where AI Meter's reading is older than this many seconds. Preferred over refresh.");
            var localRefresh = Property("boolean", "Read local sessions and servers again instead of using AI Meter's latest reading. No network requests.");
            return new List<object>
            {
                Tool("get_limits", "Usage limits of each service the user turned on in AI Meter: percentage used, when each limit resets, " +
                     "the forecast time it runs out at the current pace, and spending against money limits. freshness tells how recent each service's reading is.",
                     new Dictionary<string, object> { { "service", service }, { "refresh", refresh("their limits") }, { "max_age_seconds", maxAge("their limits") } }, true),
                Tool("list_sessions", "Running and recent sessions of AI apps and coding agents (Claude, Codex, Copilot, Pi, OpenCode and others), " +
                     "with agent, status, model and the provider the session uses.",
                     new Dictionary<string, object> { { "service", service }, { "busy_only", Property("boolean", "Only sessions that are working now.") }, { "refresh", localRefresh } }, false),
                Tool("list_free_models", "Models offered at no cost by OpenRouter and other services the user turned on, with model id, " +
                     "context window, output limit, tool and reasoning support and the last free day when announced. Each model has " +
                     "free (how its price is known) and limits: per-request caps, the pool it shares with other models, rate limits " +
                     "with their source, and headroom (whether a task can start now and how many). Models in the same pool share their " +
                     "limits: never add them up. Unknown values are null. Use the id as the model name.",
                     new Dictionary<string, object>
                     {
                         { "service", service },
                         { "tools", Property("boolean", "Only models known to support tool calls.") },
                         { "min_context", Property("integer", "Smallest context window, in tokens. Models with an unknown window are left out.") },
                         { "min_output_tokens", Property("integer", "Smallest reply size, in tokens. Models with an unknown output limit are left out.") },
                         { "include_unconfirmed", Property("boolean", "Also list models whose price the service does not state, such as NVIDIA and Gemini models, with free.status unknown.") },
                         { "refresh", refresh("their model lists") },
                         { "max_age_seconds", maxAge("their model lists") },
                     }, true),
                Tool("get_spend", "Today's spending: models used through each service with cost, tokens and replies, and money limits " +
                     "such as budgets and credits.",
                     new Dictionary<string, object> { { "service", service }, { "refresh", refresh("their usage") }, { "max_age_seconds", maxAge("their usage") } }, true),
                Tool("list_local_models", "Models loaded by local servers such as Ollama, LM Studio and llama.cpp, on Windows and WSL, " +
                     "with memory, context and activity.", new Dictionary<string, object> { { "refresh", localRefresh } }, false),
                Tool("list_candidates", "Where a task can run now for free: free models that meet the task's needs, that a coding agent " +
                     "on this PC (Pi or OpenCode, on Windows or in any running WSL distribution) can already call with its own key, and whose limits have room. Ordered by suitability, " +
                     "most suitable first: the user's preferred models, pools with room, the model's track record on this PC, a reply size of at least 16k tokens, " +
                     "the model's total weights (unknown first), context, measured speed; for the same model, the agent with the smaller prompt per request and, with from, " +
                     "the agent in the plugin's place. rank says why each candidate stands where it is; the same request gives the same order. pools gives " +
                     "how many new tasks each pool can take now: models in one pool share it. excluded says why every other model " +
                     "cannot be used; runtimes.unsupported lists the agents found that delegation cannot use, with the reason. " +
                     "warnings flags candidates a plugin must watch, such as openrouter_byok_fallback. With include_best_effort, best_effort " +
                     "lists models the service's own list leaves short (price, tool support or limits unstated) that an agent's own model " +
                     "list has at no price: they may fail or stop, so ask the user before using one. With include_free_tier, also models that " +
                     "are priced but free within a daily allocation (Cloudflare Workers AI): a plugin must stop the agent when its " +
                     "priced requests reach the budget claim_capacity returns, so ask the user first. With include_plan and coordinator, also " +
                     "Claude Code or Codex on a subscription plan the user allowed in Settings, drawing on room above the user's reserve, " +
                     "never pay-as-you-go: billing included_in_plan, with the plan's windows. Nothing is started or reserved.",
                     new Dictionary<string, object>
                     {
                         { "service", service },
                         { "tools", Property("boolean", "The task needs tool calls.") },
                         { "min_context", Property("integer", "Smallest context window the task needs, in tokens.") },
                         { "min_output_tokens", Property("integer", "Smallest reply size the task needs, in tokens.") },
                         { "cost_policy", new Dictionary<string, object> { { "type", "string" }, { "enum", new List<object> { Candidates.FreeOnly, Candidates.FreeOrUnconfirmed } },
                             { "description", "free_only (default): only models whose list states a zero price. free_or_unconfirmed: also models whose price the service does not state." } } },
                         { "include_best_effort", Property("boolean", "Also list best-effort candidates, built on the agents' own model lists where the service states nothing. Off by default.") },
                         { "include_free_tier", Property("boolean", "Also accept free-tier models: priced, but free within a daily allocation the plugin must keep to with claim_capacity's budget. Off by default.") },
                         { "from", new Dictionary<string, object> { { "type", "string" }, { "enum", new List<object> { "windows", "wsl" } },
                             { "description", "Where the caller runs. For the same model, an agent in that place comes first, which avoids starting it through wsl.exe." } } },
                         { "include_plan", Property("boolean", "Also accept capacity included in a subscription plan the user allowed in Settings: Claude Code on the Claude plan, Codex on the ChatGPT plan, with their own sign-in. Never pay-as-you-go: a plan with extra usage or credits on, or below the user's reserve, is excluded. Free models come first unless a plan's room is about to be lost at a reset. Ask the user first. Off by default.") },
                         { "coordinator", Property("string", "The service the caller itself runs on, such as claude or openai. A plan on that service saves nothing and is excluded (same_plan_as_coordinator); without it, every plan is excluded.") },
                         { "refresh", refresh("their limits") },
                         { "max_age_seconds", maxAge("their limits") },
                     }, true),
                Tool("suggest_service", "Services ordered by the room left in their tightest limit, to choose where to run the next task, " +
                     "with the number of free models each one offers. Services whose limits are unknown come after those with known room.",
                     new Dictionary<string, object> { { "refresh", refresh("their limits") }, { "max_age_seconds", maxAge("their limits") } }, true),
                Tool("describe_dispatch", "How to start a candidate from list_candidates: the program, its arguments as a list and the " +
                     "environment, for a coding agent and place verified with AI Meter. Start the program without a shell and write the task " +
                     "to its stdin, then close stdin: the task never goes into the arguments. Also says how to tell the end, the outcome, " +
                     "the requests, tokens and per-request cost, and how to stop it. AI Meter starts nothing.",
                     new Dictionary<string, object>
                     {
                         { "service", Property("string", "The candidate's service.") },
                         { "model", Property("string", "The candidate's model id.") },
                         { "runtime", Property("string", "The candidate's runtime, such as pi or opencode.") },
                         { "where", Enum("The candidate's place.", "windows", "wsl") },
                         { "distro", Property("string", "The candidate's WSL distribution, when it has one.") },
                         { "mode", Enum("read_only (default): the agent can read but not change files. modify: it can change files; run it in a new Git worktree.", Dispatch.ReadOnly, Dispatch.Modify) },
                         { "workspace", Property("string", "The folder the task works in, as seen from where the plugin runs. A placeholder when omitted.") },
                         { "from", Enum("Where the plugin that starts the agent runs (default windows).", "windows", "wsl") },
                         { "coordinator", Property("string", "For a plan candidate: the service the coordinator runs on, as in list_candidates. Required for plans.") },
                     }, false),
                Tool("claim_capacity", "Take a slot in a candidate's pool before starting its agent, so that other plugins see it as busy. " +
                     "Refused when the pool has no room. Renew it during long tasks and release it when the task ends; an unreleased " +
                     "lease expires on its own. Leases bind only clients that use them. For a free-tier pool the answer has a budget: " +
                     "stop the agent as soon as its requests, priced as the agent reports them, cost more than budget.max_cost.",
                     new Dictionary<string, object>
                     {
                         { "service", Property("string", "The candidate's service.") },
                         { "model", Property("string", "The candidate's model id.") },
                         { "runtime", Property("string", "The candidate's runtime.") },
                         { "ttl_seconds", Property("integer", "How long the lease lasts without renewal: default 1800, at most 7200.") },
                         { "client", Property("string", "A label for whoever claims, such as claude-code or codex.") },
                         { "coordinator", Property("string", "For a plan candidate: the service the coordinator runs on. Required for plans; the plan's windows are checked again.") },
                     }, false, false),
                Tool("renew_capacity", "Extend an active lease from claim_capacity.",
                     new Dictionary<string, object>
                     {
                         { "lease_id", Property("string", "The lease to extend.") },
                         { "ttl_seconds", Property("integer", "New duration from now: default 1800, at most 7200.") },
                     }, false, false),
                Tool("release_capacity", "End a lease from claim_capacity with the task's outcome. A rate limit the agent met closes the pool " +
                     "until retry_after_seconds, or for a minute. The cost counts against a free tier's budget at once, before the " +
                     "service reports it. Releasing twice changes nothing.",
                     new Dictionary<string, object>
                     {
                         { "lease_id", Property("string", "The lease to end.") },
                         { "outcome", Enum("How the task ended.", "completed", "failed", "cancelled", "timed_out") },
                         { "requests", Property("integer", "Requests the agent made to the model, when known.") },
                         { "rate_limited", Property("integer", "Rate-limit answers (429) the agent met.") },
                         { "retry_after_seconds", Property("integer", "The longest Retry-After the agent saw, when any.") },
                         { "input_tokens", Property("integer", "Input tokens, when known.") },
                         { "output_tokens", Property("integer", "Output tokens, when known.") },
                         { "cost", Property("number", "What the agent priced the task's requests at, in US dollars, when it reports prices.") },
                     }, false, false),
            };
        }

        private static Dictionary<string, object> Enum(string description, params string[] values)
        {
            return new Dictionary<string, object> { { "type", "string" }, { "enum", values.Cast<object>().ToList() }, { "description", description } };
        }

        /// <summary>
        /// A tool; openWorld marks the tools whose refresh contacts the services. Every tool reads, except the lease tools,
        /// which change only AI Meter's own lease book: no process, no network, nothing destructive.
        /// </summary>
        private static Dictionary<string, object> Tool(string name, string description, Dictionary<string, object> properties, bool openWorld, bool readOnly = true)
        {
            var annotations = new Dictionary<string, object> { { "readOnlyHint", readOnly }, { "openWorldHint", openWorld } };
            if (!readOnly) annotations["destructiveHint"] = false;
            return new Dictionary<string, object>
            {
                { "name", name },
                { "description", description },
                { "inputSchema", new Dictionary<string, object> { { "type", "object" }, { "properties", properties } } },
                { "annotations", annotations },
            };
        }

        private static Dictionary<string, object> Property(string type, string description)
        {
            return new Dictionary<string, object> { { "type", type }, { "description", description } };
        }

        private static readonly Dictionary<string, Scope> Scopes = new Dictionary<string, Scope>
        {
            { "get_limits", Scope.Usage }, { "get_spend", Scope.Usage }, { "suggest_service", Scope.Usage }, { "list_candidates", Scope.Usage },
            { "list_free_models", Scope.Catalogue }, { "list_sessions", Scope.Local }, { "list_local_models", Scope.Local },
        };

        private Dictionary<string, object> Call(Dictionary<string, object> parameters)
        {
            string name = Json.Str(parameters, "name");
            var args = Json.Obj(parameters, "arguments") ?? new Dictionary<string, object>();
            if (name == "describe_dispatch" || name == "claim_capacity" || name == "renew_capacity" || name == "release_capacity") return Delegation(name, args);
            Scope scope;
            if (name == null || !Scopes.TryGetValue(name, out scope)) return ToolResult(new Dictionary<string, object> { { "error", "Unknown tool: " + name } }, true);
            bool refresh = Json.Bool(args, "refresh");
            double? maxAge = Json.Num(args, "max_age_seconds");
            if (maxAge < 0) return ToolResult(new Dictionary<string, object> { { "error", "max_age_seconds cannot be negative." } }, true);
            string policy = Json.Str(args, "cost_policy") ?? Candidates.FreeOnly;
            if (policy != Candidates.FreeOnly && policy != Candidates.FreeOrUnconfirmed)
                return ToolResult(new Dictionary<string, object> { { "error", "cost_policy must be free_only or free_or_unconfirmed." } }, true);

            string service = Json.Str(args, "service");
            Func<Snapshot, List<ProviderState>> pick = s => s.Providers.Where(p => p.Models == null && (name == "suggest_service" || service == null ||
                string.Equals(p.Id, service, StringComparison.OrdinalIgnoreCase)) && (scope != Scope.Catalogue || HasCatalogue(p))).ToList();
            var snap = snapshot();
            var asked = new List<string>();
            RefreshResult outcome = null;
            if ((refresh || maxAge.HasValue) && gate != null)
            {
                if (scope == Scope.Local || snap == null)
                {
                    double age = snap == null ? double.MaxValue : (DateTime.Now - snap.Taken).TotalSeconds;
                    if (refresh || age > maxAge) outcome = gate.Request(new string[0], false, snap, timeout);
                }
                else
                {
                    // An explicit refresh still skips readings younger than the minimum interval: they are as new as one can get.
                    double limit = refresh ? (maxAge.HasValue ? Math.Min(maxAge.Value, RefreshGate.MinimumSeconds) : RefreshGate.MinimumSeconds) : maxAge.Value;
                    asked = pick(snap).Where(p => Age(p, scope) > limit).Select(p => p.Id).ToList();
                    if (asked.Count > 0) outcome = gate.Request(asked, scope == Scope.Catalogue, snap, timeout);
                }
                if (outcome != null && outcome.Snapshot != null) snap = outcome.Snapshot;
            }
            if (snap == null) return ToolResult(new Dictionary<string, object> { { "status", "AI Meter is still reading the first data; try again in a few seconds." } }, false);

            var providers = snap.Providers.Where(p => service == null || string.Equals(p.Id, service, StringComparison.OrdinalIgnoreCase)).ToList();
            object result;
            switch (name)
            {
                case "get_limits": result = Limits(providers.Where(p => p.Models == null)); break;
                case "list_sessions": result = Sessions(providers, Json.Bool(args, "busy_only")); break;
                case "list_free_models":
                    result = Free(providers, Json.Bool(args, "tools"), (long)(Json.Num(args, "min_context") ?? 0),
                        (long)(Json.Num(args, "min_output_tokens") ?? 0), Json.Bool(args, "include_unconfirmed"));
                    break;
                case "get_spend": result = Spend(providers); break;
                case "list_local_models": result = LocalModels(snap.Providers); break;
                case "list_candidates":
                    var scan = runtimes();   // Once per call: a scan reads every agent's files, in WSL too.
                    result = CandidatesJson(Candidates.Select(snap, scan, new Requirements
                    {
                        Service = service, Tools = Json.Bool(args, "tools"), MinContext = (long)(Json.Num(args, "min_context") ?? 0),
                        MinOutput = (long)(Json.Num(args, "min_output_tokens") ?? 0), CostPolicy = policy, BestEffort = Json.Bool(args, "include_best_effort"),
                        FreeTier = Json.Bool(args, "include_free_tier"), From = KnownPlace(Json.Str(args, "from")),
                        Preferences = Settings.Current == null ? null : Settings.Current.DelegationModels,
                        Plan = Json.Bool(args, "include_plan"), Coordinator = Json.Str(args, "coordinator"),
                        PlansAllowed = Settings.Current == null ? null : Settings.Current.PlansAllowed(),
                        Reserves = Settings.Current == null ? null : Settings.Current.DelegationReserves,
                    }, DateTime.Now, leases), scan);
                    break;
                default: result = Suggest(snap.Providers); break;
            }
            var content = new Dictionary<string, object> { { "updated_at", Time(snap.Taken) }, { "sample_data", Program.Demo } };
            var parts = result as Dictionary<string, object>;
            if (parts != null) foreach (var kv in parts) content[kv.Key] = kv.Value;   // list_candidates has several parts.
            else content[ResultKey(name)] = result;
            bool requested = refresh || maxAge.HasValue;
            if (scope == Scope.Local)
            {
                if (requested) content["refresh"] = LocalStatus(outcome);
            }
            else
            {
                var freshness = new Dictionary<string, object>();
                foreach (var p in pick(snap))
                {
                    DateTime retry;
                    if (outcome == null || !outcome.RetryAt.TryGetValue(p.Id, out retry)) retry = NetworkBackoff.Until(p.Id);
                    freshness[p.Id] = Freshness(p, scope, !requested ? "not_requested" : gate == null ? "unavailable" : Status(p, scope, asked, outcome), retry);
                }
                content["freshness"] = freshness;
            }
            return ToolResult(content, false);
        }

        // ------------------------------------------------------------ freshness

        /// <summary>Whether a service offers a free-model list: one has been read, or its definition names one.</summary>
        private static bool HasCatalogue(ProviderState p)
        {
            if (p.Catalogue != null) return true;
            var d = Definitions.Get(p.Id);
            return d != null && Json.Obj(d.Raw, "freeModels") != null;
        }

        /// <summary>When the data a scope relies on was read from the service; null when it never was.</summary>
        private static DateTime? ReadAt(ProviderState p, Scope scope)
        {
            if (scope == Scope.Catalogue) return p.Catalogue == null ? (DateTime?)null : p.Catalogue.ReadAt;
            return p.Usage == null ? (DateTime?)null : p.Usage.At;
        }

        private static double Age(ProviderState p, Scope scope)
        {
            var at = ReadAt(p, scope);
            return at.HasValue ? (DateTime.Now - at.Value).TotalSeconds : double.MaxValue;
        }

        /// <summary>What a refresh achieved for one service.</summary>
        private static string Status(ProviderState p, Scope scope, List<string> asked, RefreshResult outcome)
        {
            // Recent enough for the caller. With nothing asked but a collection awaited, the first reading was missing.
            if (outcome == null || (asked.Count > 0 && !asked.Contains(p.Id))) return "cached";
            string skipped;
            if (outcome.Skipped.TryGetValue(p.Id, out skipped)) return skipped;
            if (outcome.Paused) return "paused";
            if (outcome.TimedOut) return "timed_out";
            DateTime since;
            if (!outcome.AskedAt.TryGetValue(p.Id, out since)) since = outcome.RequestedAt;
            var at = ReadAt(p, scope);
            if (at.HasValue && at.Value >= since) return "refreshed";
            return NetworkBackoff.Ready(p.Id) ? "unchanged" : "backoff";
        }

        private static string LocalStatus(RefreshResult outcome)
        {
            if (outcome == null) return "cached";
            return outcome.Paused ? "paused" : outcome.TimedOut ? "timed_out" : "refreshed";
        }

        /// <summary>
        /// How recent one service's data is (state: fresh, stale or unavailable, with a reason when not fresh) and what
        /// a refresh achieved, with the earliest time another one can be requested when the service cannot be asked now.
        /// </summary>
        private static Dictionary<string, object> Freshness(ProviderState p, Scope scope, string refresh, DateTime retryAt)
        {
            var d = new Dictionary<string, object>();
            var at = ReadAt(p, scope);
            if (scope == Scope.Catalogue)
            {
                d["state"] = at.HasValue ? "fresh" : "unavailable";
                if (!at.HasValue) d["reason"] = p.NeedsLogin ? "sign_in_needed" : "no_model_list";
            }
            else if (p.Usage == null)
            {
                d["state"] = "unavailable";
                d["reason"] = p.NeedsLogin ? "sign_in_needed" : "no_reading";
            }
            else if (p.Usage.Source == "cache")
            {
                d["state"] = "stale";
                d["reason"] = "cached_reading";
            }
            else d["state"] = "fresh";
            if (at.HasValue) d["read_at"] = Time(at.Value);
            d["refresh"] = refresh;
            if (retryAt > DateTime.Now) d["retry_after"] = Time(retryAt);
            return d;
        }

        private static string ResultKey(string tool)
        {
            if (tool == "list_sessions") return "sessions";
            return tool == "list_free_models" || tool == "list_local_models" ? "models" : "services";
        }

        private static Dictionary<string, object> ToolResult(Dictionary<string, object> content, bool isError)
        {
            return new Dictionary<string, object>
            {
                { "content", new List<object> { new Dictionary<string, object> { { "type", "text" }, { "text", JsonText.Indented(content) } } } },
                { "structuredContent", content },
                { "isError", isError },
            };
        }

        private static List<object> Limits(IEnumerable<ProviderState> providers)
        {
            var list = new List<object>();
            foreach (var p in providers)
            {
                var entry = Service(p);
                if (p.Notice != null) entry["notice"] = p.Notice;
                if (p.NeedsLogin) entry["needs_sign_in"] = true;
                if (p.Usage != null)
                {
                    entry["source"] = p.Usage.Source;
                    entry["read_at"] = Time(p.Usage.At);
                    entry["extra_usage_on"] = p.Usage.ExtraActive;
                    entry["limits"] = p.Usage.Windows.Where(w => !w.Hidden).Select(w => (object)Limit(p.Usage, w)).ToList();
                }
                list.Add(entry);
            }
            return list;
        }

        private static Dictionary<string, object> Limit(Usage usage, UsageWindow w)
        {
            var d = new Dictionary<string, object> { { "name", w.Suffix == null ? w.Label : w.Label + " " + w.Suffix } };
            if (w.Short != null) d["kind"] = w.Short;
            if (w.HasPercent)
            {
                d["used_percent"] = Math.Round(w.Used, 1);
                d["remaining_percent"] = Math.Round(usage.Room(w), 1);
            }
            if (w.ResetsAt.HasValue) d["resets_at"] = Time(w.ResetsAt.Value);
            if (w.RunsOutAt.HasValue) d["runs_out_at"] = Time(w.RunsOutAt.Value);
            if (w.PacePerHour.HasValue) d["percent_per_hour"] = Math.Round(w.PacePerHour.Value, 2);
            if (w.Spent != null) d["money"] = new Dictionary<string, object> { { "used", w.Spent.Used }, { "limit", w.Spent.Limit }, { "currency", w.Spent.Currency } };
            if (w.Amount.HasValue) { d["amount"] = w.Amount.Value; if (w.AmountUnit != null) d["unit"] = w.AmountUnit; }
            if (w.IsEstimated) d["estimated"] = true;
            if (w.Budget) d["budget"] = true;
            if (w.RightText != null) d["value"] = w.RightText;
            if (w.CachedAt.HasValue) d["cached_at"] = Time(w.CachedAt.Value);
            return d;
        }

        private List<object> Sessions(IEnumerable<ProviderState> providers, bool busyOnly)
        {
            bool titles = titlesAllowed();
            var list = new List<object>();
            foreach (var p in providers)
            {
                var all = p.Sessions.Select(s => Tuple.Create(s, (string)null))
                    .Concat((p.Models ?? new List<LocalModel>()).SelectMany(m => m.Sessions.Select(s => Tuple.Create(s, m.Name))));
                foreach (var pair in all)
                {
                    var s = pair.Item1;
                    if (s.Status == "app" || (busyOnly && s.Status != "busy")) continue;
                    var d = new Dictionary<string, object> { { "service", p.Id }, { "agent", s.Kind }, { "status", s.Status } };
                    if (s.Model != null || pair.Item2 != null) d["model"] = s.Model ?? pair.Item2;
                    if (s.Provider != null) d["provider"] = s.Provider;
                    if (s.Updated > DateTime.MinValue) d["updated_at"] = Time(s.Updated);
                    if (s.Note != null) d["note"] = s.Note;
                    if (titles && !string.IsNullOrEmpty(s.Title)) d["title"] = s.Title;
                    list.Add(d);
                }
            }
            return list;
        }

        private List<object> Free(IEnumerable<ProviderState> providers, bool toolsOnly, long minContext, long minOutput, bool unconfirmed)
        {
            var list = new List<object>();
            foreach (var p in providers.Where(x => x.Catalogue != null))
                foreach (var m in p.Catalogue.Models.Where(f => (unconfirmed || f.Free == null || f.Free.Confirmed) && (!toolsOnly || f.Tools)
                                                                 && f.Context >= minContext && f.MaxOutput >= minOutput))
                {
                    var d = new Dictionary<string, object> { { "service", p.Id }, { "id", m.Id }, { "name", m.Name } };
                    d["tools"] = m.FeaturesUnknown && !m.Tools ? null : (object)m.Tools;
                    d["reasoning"] = m.FeaturesUnknown && !m.Reasoning ? null : (object)m.Reasoning;
                    if (m.Context > 0) d["context_tokens"] = m.Context;
                    if (m.MaxOutput > 0) d["max_output_tokens"] = m.MaxOutput;
                    if (!string.IsNullOrEmpty(m.Inputs)) d["inputs"] = m.Inputs;
                    if (m.Expires.HasValue) d["free_until"] = m.Expires.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                    d["free"] = FreeJson(m.Free);
                    d["limits"] = LimitsJson(p, m);
                    list.Add(d);
                }
            return list;
        }

        private static Dictionary<string, object> FreeJson(FreeStatus f)
        {
            if (f == null) return new Dictionary<string, object> { { "status", "unknown" } };
            var d = new Dictionary<string, object> { { "status", f.Status } };
            if (f.Kind != null) d["kind"] = f.Kind;
            if (f.Source != null) d["source"] = f.Source;
            if (f.Reference != null) d["reference"] = f.Reference;
            if (f.CheckedAt.HasValue) d["checked_at"] = f.CheckedAt.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            if (f.Note != null) d["note"] = f.Note;
            return d;
        }

        /// <summary>
        /// A plan candidate's limits: the context its agent's list states (unknown otherwise), the plan's pool with its
        /// leases' counters, and its headroom. The plan's windows are in "plan" (PlanJson).
        /// </summary>
        private Dictionary<string, object> PlanLimitsJson(Candidate c)
        {
            var context = Cap(c.Model.Context);
            if (c.Model.Context > 0) context["source"] = "agent:" + c.Access.Runtime;
            return new Dictionary<string, object>
            {
                { "per_request", new Dictionary<string, object> { { "context_tokens", context }, { "max_output_tokens", Cap(0) } } },
                { "pool", c.Pool }, { "rates", new List<object>() }, { "observed", Observed(c.Pool) }, { "headroom", RoomJson(c.Room) },
            };
        }

        /// <summary>
        /// The plan a candidate draws on: its service, when its windows were read, each window with the room left, the
        /// user's reserve and the reset, the window that binds, and whether room is about to be lost (expiring).
        /// </summary>
        internal static Dictionary<string, object> PlanJson(PlanRoom plan)
        {
            return new Dictionary<string, object>
            {
                { "service", plan.Service }, { "read_at", Time(plan.ReadAt) },
                { "windows", plan.Windows.Select(w =>
                    {
                        var d = new Dictionary<string, object>
                        {
                            { "window", w.Window }, { "remaining_percent", Math.Round(w.RemainingPercent, 1) }, { "reserve_percent", w.ReservePercent },
                            { "resets_at", w.ResetsAt.HasValue ? Time(w.ResetsAt.Value) : null },
                        };
                        if (w.Model != null) d["models"] = w.Model;
                        return (object)d;
                    }).ToList() },
                { "binding_window", plan.Binding == null ? null : plan.Binding.Name },
                { "expiring", plan.Expiring },
                { "coverage", "the plan's windows count everything the account uses, on every app and PC" },
            };
        }

        /// <summary>A per-request cap: the catalogue value, or null with source "unknown".</summary>
        private static Dictionary<string, object> Cap(long tokens)
        {
            return new Dictionary<string, object> { { "value", tokens > 0 ? (object)tokens : null }, { "source", tokens > 0 ? "catalogue" : "unknown" } };
        }

        /// <summary>
        /// A model's limits (docs/plans/orchestration-contracts.md section 2.3): per-request caps, its pool, the pool's
        /// rate limits and the headroom left. Counters of delegated tasks ("observed") arrive with capacity leases.
        /// </summary>
        private Dictionary<string, object> LimitsJson(ProviderState p, FreeModel m)
        {
            var rates = p.Catalogue.Rates.Select(r =>
            {
                var d = new Dictionary<string, object>
                {
                    { "metric", r.Metric }, { "limit", r.Limit }, { "used", r.Used }, { "remaining", r.Remaining },
                    { "scope", r.Scope }, { "per_model", r.PerModel }, { "source", r.Source },
                };
                if (r.ResetsAt.HasValue) d["resets_at"] = Time(r.ResetsAt.Value);
                if (r.ResetZone != null) d["reset_zone"] = r.ResetZone;
                if (r.Coverage != null) d["coverage"] = r.Coverage;
                if (r.Reference != null) d["reference"] = r.Reference;
                if (r.CheckedAt.HasValue) d["checked_at"] = r.CheckedAt.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                if (r.Note != null) d["note"] = r.Note;
                return (object)d;
            }).ToList();
            return new Dictionary<string, object>
            {
                { "per_request", new Dictionary<string, object> { { "context_tokens", Cap(m.Context) }, { "max_output_tokens", Cap(m.MaxOutput) } } },
                { "pool", p.Catalogue.PoolOf(m) },
                { "rates", rates },
                { "observed", Observed(p.Catalogue.PoolOf(m) ?? p.Id) },
                { "headroom", RoomJson(Candidates.Room(p.Id, p.Catalogue.Rates, DateTime.Now, leases, p.Catalogue.PoolOf(m) ?? p.Id,
                    Candidates.BudgetOf(p.Catalogue, leases, p.Catalogue.PoolOf(m) ?? p.Id, DateTime.Now))) },
            };
        }

        private static Dictionary<string, object> RoomJson(PoolRoom room)
        {
            Dictionary<string, object> blocking = null;
            if (room.Blocking != null)
            {
                blocking = new Dictionary<string, object> { { "reason", room.Blocking } };
                if (room.Metric != null) blocking["metric"] = room.Metric;
                if (room.Until.HasValue) blocking["until"] = Time(room.Until.Value);
            }
            var d = new Dictionary<string, object> { { "blocking", blocking }, { "can_start_now", room.CanStart }, { "max_new_jobs_now", room.MaxNewJobs }, { "leased", room.Leased } };
            if (room.Ratio.HasValue)
            {
                d["remaining_share"] = Math.Round(room.Ratio.Value, 3);
                d["binding_limit"] = room.BindingMetric;
            }
            if (room.Budget != null) d["budget"] = BudgetJson(room.Budget);
            return d;
        }

        /// <summary>A free-tier candidate's warning: the plugin must keep the agent's priced requests within the budget.</summary>
        internal const string FreeTierBudget = "free_tier_budget";
        private const string FreeTierNote = "This model is priced and free only within a daily allocation: stop the agent as soon as its " +
            "requests cost more than the budget claim_capacity returns (max_cost), and release the lease with their cost.";

        /// <summary>
        /// A free tier's budget for one task: the allocation in its unit (limit, used, the reserve kept back and what is
        /// spendable) and max_cost, the most the agent's requests may cost, in US dollars, before the plugin stops it.
        /// Unknown values are null, and an unknown budget lets no task start.
        /// </summary>
        private static Dictionary<string, object> BudgetJson(Budget b)
        {
            Func<double?, object> round = v => v.HasValue ? (object)Math.Round(v.Value, 6) : null;
            var d = new Dictionary<string, object>
            {
                { "metric", b.Allowance.Metric }, { "unit", b.Allowance.Unit }, { "limit", b.Limit }, { "used", round(b.Used) },
                { "reserve", b.Allowance.Reserve }, { "spendable", round(b.Spendable) }, { "max_cost", round(b.MaxCost) }, { "currency", "USD" },
                { "usd_per_unit", b.Allowance.UsdPerUnit }, { "leased_cost_today", round(b.LeasedCostToday) },
            };
            if (b.ResetsAt.HasValue) d["resets_at"] = Time(b.ResetsAt.Value);
            return d;
        }

        /// <summary>Today's (UTC) counters of the pool's leases: what plugins reported, so only tasks run with leases count.</summary>
        private Dictionary<string, object> Observed(string pool)
        {
            var o = leases.Today(pool, DateTime.Now);
            return new Dictionary<string, object>
            {
                { "in_flight", o.InFlight }, { "tasks_ended_today", o.Ended }, { "failed_today", o.Failed }, { "unknown_outcome_today", o.Unknown },
                { "requests_today", o.Requests }, { "tasks_without_request_count", o.RequestsUnknown }, { "rate_limited_today", o.RateLimited },
                { "last_rate_limited_at", o.LastRateLimited.HasValue ? Time(o.LastRateLimited.Value) : null },
                { "input_tokens_today", o.InputTokens }, { "output_tokens_today", o.OutputTokens }, { "coverage", "ai_meter_leases_only" },
            };
        }

        // ------------------------------------------------------------ delegation (ADR 0002)

        /// <summary>describe_dispatch and the lease tools. They need a candidate known to the latest snapshot and scan.</summary>
        private Dictionary<string, object> Delegation(string name, Dictionary<string, object> args)
        {
            Func<string, Dictionary<string, object>> error = text => ToolResult(new Dictionary<string, object> { { "error", text } }, true);
            DateTime now = DateTime.Now;
            if (name == "renew_capacity")
            {
                var until = leases.Renew(Json.Str(args, "lease_id") ?? "", (int)(Json.Num(args, "ttl_seconds") ?? 0), now);
                if (!until.HasValue) return error("No active lease with this id: it was released or has expired.");
                return ToolResult(new Dictionary<string, object> { { "lease_id", Json.Str(args, "lease_id") }, { "expires_at", Time(until.Value) } }, false);
            }
            if (name == "release_capacity")
            {
                string outcome = Json.Str(args, "outcome") ?? "";
                if (!new[] { "completed", "failed", "cancelled", "timed_out" }.Contains(outcome)) return error("outcome must be completed, failed, cancelled or timed_out.");
                double? retry = Json.Num(args, "retry_after_seconds"), cost = Json.Num(args, "cost");
                if (cost.HasValue && (double.IsNaN(cost.Value) || double.IsInfinity(cost.Value) || cost.Value < 0)) return error("cost must be a number of US dollars, zero or more.");
                var released = leases.Release(Json.Str(args, "lease_id") ?? "", new LeaseBook.Outcome
                {
                    Result = outcome, Requests = (long?)Json.Num(args, "requests"), RateLimited = (int)(Json.Num(args, "rate_limited") ?? 0),
                    RetryAfter = retry > 0 ? TimeSpan.FromSeconds(Math.Min(retry.Value, 86400)) : (TimeSpan?)null,
                    InputTokens = (long?)Json.Num(args, "input_tokens"), OutputTokens = (long?)Json.Num(args, "output_tokens"), Cost = cost,
                }, now);
                var d = new Dictionary<string, object> { { "lease_id", Json.Str(args, "lease_id") }, { "released", released != null } };
                if (released == null) d["note"] = "No active lease with this id: it was already released or has expired. Nothing changed.";
                else d["pool"] = released.Pool;
                return ToolResult(d, false);
            }

            string service = Json.Str(args, "service"), model = Json.Str(args, "model"), runtime = Json.Str(args, "runtime");
            if (service == null || model == null || runtime == null) return error("service, model and runtime are required.");
            var agent = Agents.Get(runtime);
            if (agent == null)
                return error("Unknown runtime \"" + runtime + "\": AI Meter knows " + string.Join(", ", Agents.All.Select(a => a.Id)) + ". Call list_candidates for the runtimes found here.");
            var snap = snapshot();
            if (snap == null) return error("AI Meter is still reading the first data; try again in a few seconds.");
            if (agent.Plan != null && agent.Plan.Service == service) return PlanDelegation(name, args, agent, snap, model, now);
            var provider = snap.Providers.FirstOrDefault(p => p.Catalogue != null && p.Id == service);
            var free = provider == null ? null : provider.Catalogue.Models.FirstOrDefault(m => m.Id == model);
            if (free == null) return error("This model is not in the service's current model list; call list_candidates first.");
            if (agent.Dispatch == null) return error(agent.Name + " is not supported for delegation yet: no way to start it has been verified.");
            string where = Json.Str(args, "where"), distro = Json.Str(args, "distro");
            var scan = runtimes();
            Func<string, string, bool> here = (w, d) => (where == null || w == where) && (distro == null || d == distro);
            if (!scan.Found.Any(f => f.Runtime == runtime && here(f.Where, f.Distro)))
                return error("No usable " + agent.Name + " is set up here" + (where == "wsl" ? " in WSL" : where == "windows" ? " on Windows" : "") +
                             " (WSL counts only while it runs); list_candidates gives the runtimes found, and runtimes.unsupported why others cannot be used.");
            if (scan.KeyDiscoveryOff) return error("Key discovery is off in AI Meter's settings, so it cannot tell which keys " + agent.Name + " holds.");
            var access = scan.Access.Where(a => !a.Plan && a.Service == service && a.Runtime == runtime && here(a.Where, a.Distro))
                .OrderBy(a => a.Where, StringComparer.Ordinal).ThenBy(a => a.Distro, StringComparer.Ordinal).FirstOrDefault();
            if (access == null) return error(agent.Name + " has no key of its own for " + service + " here; call list_candidates first.");
            string pool = provider.Catalogue.PoolOf(free) ?? provider.Id;

            if (name == "claim_capacity")
            {
                // The room before leases: Claim counts the active ones itself, together with taking the slot.
                var budget = Candidates.BudgetOf(provider.Catalogue, leases, pool, now);
                var room = Candidates.Room(service, provider.Catalogue.Rates, now, null, null, budget);
                if (room.Blocking == null && leases.BackoffUntil(pool) > now) { room.Blocking = "rate_limited"; room.Until = leases.BackoffUntil(pool); room.MaxNewJobs = 0; }
                var lease = room.Blocking != null ? null : leases.Claim(pool, service, model, runtime, Json.Str(args, "client"), (int)(Json.Num(args, "ttl_seconds") ?? 0), room.MaxNewJobs, now);
                if (lease == null)
                    return ToolResult(new Dictionary<string, object> { { "refused", true }, { "pool", pool }, { "headroom", RoomJson(Candidates.Room(service, provider.Catalogue.Rates, now, leases, pool, budget)) } }, false);
                var claimed = new Dictionary<string, object> { { "lease_id", lease.Id }, { "pool", pool }, { "expires_at", Time(lease.ExpiresAtUtc.ToLocalTime()) }, { "coverage", "ai_meter_leases_only" } };
                if (budget != null) claimed["budget"] = BudgetJson(budget);
                return ToolResult(claimed, false);
            }

            var plan = Dispatch.Describe(access, model, Json.Str(args, "mode"), Json.Str(args, "workspace"), Json.Str(args, "from") ?? "windows",
                access.Distro ?? (Settings.Current == null ? null : Settings.Current.WslDistro), access.Version);
            if (!plan.Supported) return ToolResult(new Dictionary<string, object> { { "supported", false }, { "reason", plan.Reason }, { "runtime", runtime }, { "where", access.Where } }, false);
            var described = DispatchJson(plan, access, pool);
            if (Candidates.IsFreeTier(free))
            {
                plan.Warnings.Add(FreeTierBudget);
                described["notes"] = plan.Notes.Concat(new[] { FreeTierNote }).Cast<object>().ToList();
            }
            if (plan.Warnings.Count > 0) described["warnings"] = plan.Warnings.Cast<object>().ToList();
            return ToolResult(described, false);
        }

        /// <summary>A dispatch description as describe_dispatch returns it.</summary>
        private static Dictionary<string, object> DispatchJson(Dispatch.Plan plan, RuntimeAccess access, string pool)
        {
            var described = new Dictionary<string, object>
            {
                { "supported", true }, { "runtime", plan.Runtime }, { "where", plan.Where }, { "version", plan.Version }, { "version_verified", plan.VersionVerified },
                { "mode", plan.Mode }, { "pool", pool }, { "program", plan.Program }, { "arguments", plan.Arguments.Cast<object>().ToList() },
                { "environment", plan.Environment.ToDictionary(kv => kv.Key, kv => (object)kv.Value) }, { "working_directory", plan.WorkingDirectory },
                { "task_input", "stdin" }, { "output", plan.Output.ToDictionary(kv => kv.Key, kv => (object)kv.Value) }, { "stop", plan.Stop },
                { "notes", plan.Notes.Cast<object>().ToList() },
            };
            if (access.Distro != null) described["distro"] = access.Distro;
            if (plan.CleanEnvironment) described["environment_policy"] = "clean";
            if (plan.Billing != null) described["billing"] = plan.Billing;
            if (plan.SignIn != null)
            {
                var check = new Dictionary<string, object> { { "program", plan.SignIn.Program }, { "arguments", plan.SignIn.Arguments.Cast<object>().ToList() } };
                if (plan.SignIn.JsonField != null) { check["json_field"] = plan.SignIn.JsonField; check["expect"] = plan.SignIn.Expect; }
                if (plan.SignIn.Text != null) check["text"] = plan.SignIn.Text;
                described["sign_in_check"] = check;
            }
            return described;
        }

        /// <summary>
        /// describe_dispatch and claim_capacity for a plan agent (ADR 0003). Both check again what list_candidates did:
        /// the plan is allowed in Settings, the coordinator is on another service, the agent is set up here, and the model
        /// is one the plan offers. claim_capacity also checks the plan's windows now, so a reserve reached since the list
        /// was made refuses the lease, and its answer names the windows the task draws on.
        /// </summary>
        private Dictionary<string, object> PlanDelegation(string name, Dictionary<string, object> args, RuntimeKind agent, Snapshot snap, string model, DateTime now)
        {
            Func<string, Dictionary<string, object>> error = text => ToolResult(new Dictionary<string, object> { { "error", text } }, true);
            string service = agent.Plan.Service, runtime = agent.Id;
            var settings = Settings.Current;
            string gate = Candidates.PlanGate(service, new Requirements
            {
                Plan = true, Coordinator = Json.Str(args, "coordinator"), PlansAllowed = settings == null ? null : settings.PlansAllowed(),
            });
            if (gate == "plan_off_in_settings") return error("Delegated tasks may not draw on the " + service + " plan: the user has not allowed it in AI Meter's Settings › AI apps › Delegated tasks.");
            if (gate == "coordinator_unknown") return error("Say which service the coordinator runs on (coordinator), so that its own plan is never used.");
            if (gate != null) return error("The coordinator runs on the " + service + " plan itself: a task there would save nothing (" + gate + ").");
            if (agent.Dispatch == null) return error(agent.Name + " is not supported for delegation yet: no way to start it has been verified.");
            string where = Json.Str(args, "where"), distro = Json.Str(args, "distro");
            var access = runtimes().Access.Where(a => a.Plan && a.Runtime == runtime && (where == null || a.Where == where) && (distro == null || a.Distro == distro))
                .OrderBy(a => a.Where, StringComparer.Ordinal).ThenBy(a => a.Distro, StringComparer.Ordinal).FirstOrDefault();
            if (access == null)
                return error("No usable " + agent.Name + " is set up here" + (where == "wsl" ? " in WSL" : where == "windows" ? " on Windows" : "") +
                             " (WSL counts only while it runs); list_candidates gives the runtimes found, and runtimes.unsupported why others cannot be used.");
            if (!Candidates.PlanModels(agent, access).Any(m => m.Id == model)) return error("The " + service + " plan does not offer this model through " + agent.Name + "; call list_candidates first.");
            var provider = snap.Providers.FirstOrDefault(p => p.Id == service);
            string pool = Candidates.PlanPool(service);
            var reserves = settings == null ? null : settings.DelegationReserves;

            if (name == "claim_capacity")
            {
                // The room before leases: Claim counts the active ones itself, together with taking the slot.
                PlanRoom planRoom;
                var room = Candidates.PlanRoomOf(provider, service, model, reserves, now, null, out planRoom);
                if (room.Blocking == null && leases.BackoffUntil(pool) > now) { room.Blocking = "rate_limited"; room.Until = leases.BackoffUntil(pool); room.MaxNewJobs = 0; }
                var lease = room.Blocking != null ? null : leases.Claim(pool, service, model, runtime, Json.Str(args, "client"), (int)(Json.Num(args, "ttl_seconds") ?? 0), room.MaxNewJobs, now);
                if (lease == null)
                {
                    PlanRoom ignored;
                    return ToolResult(new Dictionary<string, object> { { "refused", true }, { "pool", pool }, { "headroom", RoomJson(Candidates.PlanRoomOf(provider, service, model, reserves, now, leases, out ignored)) } }, false);
                }
                return ToolResult(new Dictionary<string, object>
                {
                    { "lease_id", lease.Id }, { "pool", pool }, { "expires_at", Time(lease.ExpiresAtUtc.ToLocalTime()) }, { "coverage", "ai_meter_leases_only" },
                    { "billing", Candidates.IncludedInPlan }, { "plan", PlanJson(planRoom) },
                }, false);
            }

            var plan = Dispatch.Describe(access, model, Json.Str(args, "mode"), Json.Str(args, "workspace"), Json.Str(args, "from") ?? "windows",
                access.Distro ?? (settings == null ? null : settings.WslDistro), access.Version);
            if (!plan.Supported) return ToolResult(new Dictionary<string, object> { { "supported", false }, { "reason", plan.Reason }, { "runtime", runtime }, { "where", access.Where } }, false);
            return ToolResult(DispatchJson(plan, access, pool), false);
        }

        /// <summary>
        /// list_candidates: the ordered candidates, the best-effort ones when asked for (or how many there would be),
        /// each pool's room once, the exclusions and the runtimes found.
        /// </summary>
        private Dictionary<string, object> CandidatesJson(Selection s, RuntimeScan scan)
        {
            Func<Candidate, Dictionary<string, object>> entry = c =>
            {
                var d = new Dictionary<string, object>
                {
                    { "service", c.Provider.Id }, { "model", c.Model.Id }, { "name", c.Model.Name },
                    { "runtime", Place(c.Access.Runtime, c.Access.Where, c.Access.Distro) },
                    { "credential", c.Access.Credential }, { "pool", c.Pool }, { "free", FreeJson(c.Model.Free) },
                    { "limits", c.Plan != null ? PlanLimitsJson(c) : LimitsJson(c.Provider, c.Model) },
                };
                if (c.Plan != null)
                {
                    d["billing"] = Candidates.IncludedInPlan;
                    d["plan"] = PlanJson(c.Plan);
                }
                var warnings = Dispatch.Warnings(c.Provider.Id);
                if (Candidates.IsFreeTier(c.Model)) warnings.Add(FreeTierBudget);
                if (c.Estimated != null) warnings.Insert(0, "best_effort");
                if (warnings.Count > 0) d["warnings"] = warnings.Cast<object>().ToList();
                if (c.Estimated != null) d["estimated"] = EstimatedJson(c);
                d["rank"] = RankJson(c);
                return d;
            };
            var candidates = s.Chosen.Select(c => (object)entry(c)).ToList();
            var pools = s.Chosen.Concat(s.BestEffort).GroupBy(c => c.Pool).Select(g =>
            {
                var d = RoomJson(g.First().Room);
                d["pool"] = g.Key;
                d["service"] = g.First().Provider.Id;
                d["models"] = g.Select(c => c.Model.Id).Distinct().Count();
                return (object)d;
            }).ToList();
            var excluded = s.Excluded.Select(e =>
            {
                var d = new Dictionary<string, object> { { "service", e.Service }, { "reason", e.Reason }, { "count", e.Models.Count }, { "models", e.Models.Take(10).Cast<object>().ToList() } };
                if (e.Until.HasValue) d["until"] = Time(e.Until.Value);
                return (object)d;
            }).ToList();
            var result = new Dictionary<string, object>
            {
                { "candidates", candidates }, { "pools", pools }, { "excluded", excluded },
                { "runtimes", new Dictionary<string, object>
                    {
                        { "found", scan.Found.Select(r => (object)Place(r.Runtime, r.Where, r.Distro)).ToList() },
                        { "unsupported", scan.Unsupported.Select(u =>
                            {
                                var d = Place(u.Runtime, u.Where, u.Distro);
                                d["display_name"] = u.Name;
                                d["reason"] = u.Reason;
                                d["detail"] = UnsupportedDetail(u);
                                return (object)d;
                            }).ToList() },
                        { "key_discovery", !scan.KeyDiscoveryOff },
                    } },
            };
            if (s.BestEffort.Count > 0) result["best_effort"] = s.BestEffort.Select(c => (object)entry(c)).ToList();
            if (s.BestEffortAvailable > 0) result["best_effort_available"] = s.BestEffortAvailable;
            return result;
        }

        /// <summary>
        /// What a best-effort candidate rests on: the agent whose own list was read, the values taken from it where the
        /// service's list is silent, and which facts no one has confirmed (unverified; limits whenever none is known).
        /// </summary>
        private static Dictionary<string, object> EstimatedJson(Candidate c)
        {
            var known = c.Estimated;
            var unverified = new List<string>(c.Unverified);
            if (!c.Provider.Catalogue.Rates.Any(r => r.Limit.HasValue)) unverified.Add("limits");
            var d = new Dictionary<string, object>
            {
                { "source", "agent:" + c.Access.Runtime },
                { "tools", known.Tools }, { "reasoning", known.Reasoning },
                { "context_tokens", known.Context > 0 ? (object)known.Context : null }, { "max_output_tokens", known.MaxOutput > 0 ? (object)known.MaxOutput : null },
                { "price_per_million", new Dictionary<string, object> { { "input", known.InputPrice }, { "output", known.OutputPrice } } },
                { "unverified", unverified.Cast<object>().ToList() },
                { "note", "Taken from " + c.Access.Runtime + "'s own model list, not from the service: the task may fail, stop or hit limits no one has stated. Ask the user before using it." },
            };
            return d;
        }

        /// <summary>"windows" or "wsl", or null for anything else.</summary>
        private static string KnownPlace(string where) { return where == "windows" || where == "wsl" ? where : null; }

        /// <summary>
        /// Why a candidate stands where it is (Candidates.Ordered): the values of each key, and a sentence that names
        /// what placed it, for a coordinator that wants to choose by hand.
        /// </summary>
        private static Dictionary<string, object> RankJson(Candidate c)
        {
            var r = c.Rank;
            var record = new Dictionary<string, object>
            {
                { "days", r.Record.Days }, { "completed", r.Record.Completed }, { "failed", r.Record.Failed }, { "rate_limited", r.Record.RateLimited },
                { "score", r.Record.Score }, { "output_tokens_per_second", r.Record.TokensPerSecond },
            };
            var d = new Dictionary<string, object>
            {
                { "why", RankSentence(c) }, { "preference", r.Preference }, { "room", r.LowRoom ? "low" : "enough" }, { "track_record", record },
                { "small_reply", r.SmallReply },
                { "parameters", r.Size == null ? null : (object)r.Size.Parameters }, { "parameters_source", r.Size == null ? null : r.Size.Source },
                { "request_tokens", r.RequestTokens },
            };
            if (r.SamePlace.HasValue) d["same_place"] = r.SamePlace.Value;
            if (c.Plan != null)
            {
                d["plan"] = c.Plan.Expiring ? "expiring" : "fallback";
                d["plan_model_order"] = c.Plan.ModelOrder;
            }
            return d;
        }

        private static string RankSentence(Candidate c)
        {
            var r = c.Rank;
            var parts = new List<string>();
            if (c.Plan != null)
            {
                // A plan candidate stands by its tier and its plan's own model order: say those, not the free models' keys.
                var b = c.Plan.Binding;
                parts.Add("included in the " + c.Plan.Service + " plan" + (c.Plan.Expiring ? "; room about to be lost at a reset, so before free models" : ", after free models"));
                if (b != null)
                    parts.Add(string.Format(CultureInfo.InvariantCulture, "{0} window {1:0}% left above a {2}% reserve{3}", b.Name, b.RemainingPercent, b.ReservePercent,
                        b.ResetsAt.HasValue ? ", resets " + Time(b.ResetsAt.Value) : ""));
                parts.Add(c.Plan.ModelOrder == 0 ? "the plan's first model" : "model " + (c.Plan.ModelOrder + 1) + " in the plan's list");
                if (r.SamePlace.HasValue) parts.Add(r.SamePlace.Value ? "runs where the caller runs" : "runs in another place than the caller");
                string said = string.Join("; ", parts);
                return char.ToUpperInvariant(said[0]) + said.Substring(1) + ".";
            }
            if (r.Preference != "neutral") parts.Add(r.Preference + " by the user");
            if (r.LowRoom) parts.Add("pool low on room (" + Math.Round(r.RoomRatio.Value * 100) + "% left)");
            parts.Add(r.Record.Tried || r.Record.RateLimited > 0
                ? string.Format(CultureInfo.InvariantCulture, "here in {0} days: {1} completed, {2} failed, {3} rate limited", r.Record.Days, r.Record.Completed, r.Record.Failed, r.Record.RateLimited)
                : "not tried here yet");
            if (r.SmallReply) parts.Add("replies capped at " + r.MaxOutput.ToString("N0", CultureInfo.InvariantCulture) + " tokens");
            parts.Add(r.Size == null ? "size unknown" : ModelSizes.Format(r.Size.Parameters) + " weights" + (r.Size.Source == "huggingface" ? " (Hugging Face)" : " (from its id)"));
            if (r.Context > 0) parts.Add(r.Context.ToString("N0", CultureInfo.InvariantCulture) + " context");
            if (r.Record.TokensPerSecond.HasValue) parts.Add(r.Record.TokensPerSecond.Value.ToString("0.#", CultureInfo.InvariantCulture) + " output tokens/s here");
            if (r.RequestTokens.HasValue) parts.Add(c.Access.Runtime + " sends about " + r.RequestTokens.Value.ToString("N0", CultureInfo.InvariantCulture) + " tokens per request");
            if (r.SamePlace.HasValue) parts.Add(r.SamePlace.Value ? "runs where the caller runs" : "runs in another place than the caller");
            string text = string.Join("; ", parts);
            return char.ToUpperInvariant(text[0]) + text.Substring(1) + ".";
        }

        /// <summary>A runtime and its place: name and where, with the distribution in WSL.</summary>
        private static Dictionary<string, object> Place(string runtime, string where, string distro)
        {
            var d = new Dictionary<string, object> { { "name", runtime }, { "where", where } };
            if (distro != null) d["distro"] = distro;
            return d;
        }

        /// <summary>A sentence a plugin can show for an unsupported agent.</summary>
        private static string UnsupportedDetail(UnsupportedRuntime u)
        {
            string place = u.Where == "wsl" ? "in WSL" + (u.Distro != null ? " (" + u.Distro + ")" : "") : "on Windows";
            switch (u.Reason)
            {
                case "no_dispatch": return u.Name + " was found " + place + ", but delegation does not support it yet.";
                case "place_not_verified": return u.Name + " was found " + place + ", but delegation has not been verified there yet.";
                case "program_not_found": return u.Name + "'s settings are " + place + ", but its program was not found in the usual folders.";
                default: return u.Name + " is installed " + place + ", but has no settings folder yet: run it once to set it up.";
            }
        }

        private static List<object> Spend(IEnumerable<ProviderState> providers)
        {
            var list = new List<object>();
            foreach (var p in providers.Where(x => x.Usage != null))
            {
                var models = p.Usage.TodayModels ?? new List<ModelCost>();
                var money = p.Usage.Windows.Where(w => w.Spent != null || (w.Amount.HasValue && w.AmountUnit != null)).ToList();
                if (models.Count == 0 && money.Count == 0) continue;
                var entry = Service(p);
                entry["today_models"] = models.Select(m => (object)new Dictionary<string, object>
                {
                    { "model", m.Model }, { "cost_usd", Math.Round(m.Cost, 4) }, { "tokens", m.Tokens }, { "replies", m.Replies },
                }).ToList();
                entry["money"] = money.Select(w => (object)Limit(p.Usage, w)).ToList();
                list.Add(entry);
            }
            return list;
        }

        private static List<object> LocalModels(IEnumerable<ProviderState> providers)
        {
            var list = new List<object>();
            foreach (var p in providers.Where(x => x.Models != null))
                foreach (var m in p.Models)
                {
                    var d = new Dictionary<string, object> { { "name", m.Name }, { "server", m.Server }, { "where", m.Where } };
                    if (m.Detail != null) d["detail"] = m.Detail;
                    if (m.Vram > 0) d["gpu_memory_bytes"] = m.Vram;
                    if (m.Ram > 0) d["memory_bytes"] = m.Ram;
                    if (m.Context > 0) d["context_tokens"] = m.Context;
                    if (m.Slots > 0) { d["busy_requests"] = m.Busy; d["slots"] = m.Slots; }
                    list.Add(d);
                }
            return list;
        }

        /// <summary>
        /// Services with known room first, most room first and a fresh reading before a stale one; then services with
        /// free models but no readable limit, whose room is reported as unknown (null), never as 100%.
        /// </summary>
        private static List<object> Suggest(IEnumerable<ProviderState> providers)
        {
            var rows = new List<Tuple<double?, bool, string, Dictionary<string, object>>>();
            foreach (var p in providers.Where(x => x.Models == null && (x.Usage != null || x.Catalogue != null) && !x.NeedsLogin))
            {
                var tight = p.Usage == null ? null : Ui.Tightest(p.Usage);
                int free = p.Catalogue == null ? 0 : p.Catalogue.Models.Count(m => m.Free == null || m.Free.Confirmed);
                if (tight == null && free == 0) continue;
                var d = Service(p);
                double? room = tight == null ? (double?)null : p.Usage.Room(tight);
                if (tight != null)
                {
                    d["tightest_limit"] = tight.Suffix == null ? tight.Label : tight.Label + " " + tight.Suffix;
                    d["remaining_percent"] = Math.Round(room.Value, 1);
                    if (tight.ResetsAt.HasValue) d["resets_at"] = Time(tight.ResetsAt.Value);
                    if (tight.RunsOutAt.HasValue) d["runs_out_at"] = Time(tight.RunsOutAt.Value);
                }
                else
                {
                    d["remaining_percent"] = null;
                    d["quota"] = "unknown";
                }
                bool stale = p.Usage != null && p.Usage.Source == "cache";
                if (stale) d["stale"] = true;
                if (free > 0) d["free_models"] = free;
                rows.Add(Tuple.Create(room, stale, p.Id, d));
            }
            return rows.OrderBy(r => r.Item1.HasValue ? 0 : 1).ThenByDescending(r => r.Item1 ?? 0).ThenBy(r => r.Item2)
                .ThenBy(r => r.Item3, StringComparer.Ordinal).Select(r => (object)r.Item4).ToList();
        }

        private static Dictionary<string, object> Service(ProviderState p)
        {
            var d = new Dictionary<string, object> { { "service", p.Id }, { "name", p.Name } };
            if (!string.IsNullOrEmpty(p.Plan)) d["plan"] = p.Plan;
            return d;
        }

        private static string Time(DateTime t) { return t.ToString("yyyy-MM-ddTHH:mm:sszzz", CultureInfo.InvariantCulture); }

        // ------------------------------------------------------------ JSON-RPC envelopes

        private static string Result(object id, object result)
        {
            return Json.Serialize(new Dictionary<string, object> { { "jsonrpc", "2.0" }, { "id", id }, { "result", result } });
        }

        internal static string Error(object id, int code, string text)
        {
            return Json.Serialize(new Dictionary<string, object>
            {
                { "jsonrpc", "2.0" }, { "id", id }, { "error", new Dictionary<string, object> { { "code", code }, { "message", text } } },
            });
        }
    }

    /// <summary>
    /// Serves McpServer on a named pipe that only the current Windows user can open. Each connection is one client
    /// session, handled on its own background thread; the pipe name includes the user name, so several signed-in
    /// users each reach their own AI Meter.
    ///
    /// The pipes are opened for overlapped I/O so that Stop can end them: disposing such a pipe cancels a pending
    /// WaitForConnection or read, whereas a synchronous call would keep waiting on the handle.
    /// </summary>
    internal sealed class McpPipeHost
    {
        public const string PipePrefix = "AIMeter.Mcp.";
        public static string PipeName { get { return PipePrefix + Environment.UserName; } }

        /// <summary>
        /// The longest request line accepted, in UTF-8 bytes without the newline. MCP requests to AI Meter are a few
        /// hundred bytes, so 1 MiB leaves ample room while a client that never ends its line cannot make AI Meter hold
        /// an unbounded string. A longer line receives a JSON-RPC error and the session continues (see LineReader).
        /// </summary>
        public const int MaxRequestBytes = 1 << 20;

        private const int MaxClients = 16;

        /// <summary>Connections in a row whose client left before they were established, retried at once before backing off.</summary>
        private const int QuickRetries = 8;

        private readonly McpServer server;
        private readonly string name;
        private readonly ManualResetEvent stopped = new ManualResetEvent(false);
        private readonly object sync = new object();
        private readonly HashSet<NamedPipeServerStream> open = new HashSet<NamedPipeServerStream>();   // Listening and connected pipes, for Stop.

        public McpPipeHost(McpServer server, string name = null)
        {
            this.server = server;
            this.name = name ?? PipeName;
        }

        public void Start()
        {
            new Thread(AcceptLoop) { IsBackground = true, Name = "AIMeter.Mcp", Priority = ThreadPriority.BelowNormal }.Start();
        }

        /// <summary>
        /// Stop accepting and end every session: the listening pipe and each client's pipe are disposed, so blocked waits
        /// and reads return and their threads finish; clients see the pipe close. A host cannot be started again.
        /// </summary>
        public void Stop()
        {
            List<NamedPipeServerStream> closing;
            lock (sync)
            {
                stopped.Set();
                closing = open.ToList();
                open.Clear();
            }
            foreach (var pipe in closing)
                try { pipe.Dispose(); }
                catch (Exception) { }
        }

        private void AcceptLoop()
        {
            var security = new PipeSecurity();
            security.AddAccessRule(new PipeAccessRule(WindowsIdentity.GetCurrent().User, PipeAccessRights.ReadWrite | PipeAccessRights.CreateNewInstance, AccessControlType.Allow));
            int vanished = 0;
            while (!stopped.WaitOne(0))
            {
                NamedPipeServerStream pipe;
                try
                {
                    pipe = new NamedPipeServerStream(name, PipeDirection.InOut, MaxClients, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 4096, 4096, security);
                }
                catch (IOException ex)
                {
                    // All instances busy, or the name is taken by another copy: wait instead of spinning.
                    Program.LogError("mcp-pipe", ex);
                    stopped.WaitOne(5000);
                    continue;
                }
                catch (Exception ex)
                {
                    // Access refused or anything unexpected: this thread must never end AI Meter.
                    Program.LogError("mcp-pipe", ex);
                    stopped.WaitOne(30000);
                    continue;
                }
                if (!Track(pipe)) return;

                try
                {
                    pipe.WaitForConnection();
                    vanished = 0;
                    var connected = pipe;
                    new Thread(() => Serve(connected)) { IsBackground = true, Name = "AIMeter.Mcp.Client" }.Start();
                }
                catch (Exception ex)
                {
                    Close(pipe);
                    if (stopped.WaitOne(0)) return;   // Stop disposed the listening pipe.

                    // "The pipe is being closed": a client connected and left before the connection was established. Others
                    // may be waiting for an instance, so offer a new one at once; only a long run of these is logged.
                    bool clientLeft = ex is IOException;
                    if (clientLeft && ++vanished < QuickRetries) continue;
                    vanished = 0;
                    Program.LogError("mcp-pipe", ex);
                    stopped.WaitOne(clientLeft ? 1000 : 30000);
                }
            }
        }

        /// <summary>Record an open pipe for Stop; false, with the pipe disposed, when the host is already stopping.</summary>
        private bool Track(NamedPipeServerStream pipe)
        {
            lock (sync)
            {
                if (!stopped.WaitOne(0)) { open.Add(pipe); return true; }
            }
            pipe.Dispose();
            return false;
        }

        private void Close(NamedPipeServerStream pipe)
        {
            lock (sync) open.Remove(pipe);
            try { pipe.Dispose(); }
            catch (Exception) { }
        }

        /// <summary>
        /// Answer one client until it disconnects or Stop closes its pipe. This runs on its own thread, where an unhandled
        /// exception would end AI Meter, so every failure, including the flush when the pipe is closed after the client went
        /// away, stays here.
        /// </summary>
        private void Serve(NamedPipeServerStream pipe)
        {
            try
            {
                var reader = new LineReader(pipe, MaxRequestBytes);
                var writer = new StreamWriter(pipe, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
                string line;
                bool tooLong;
                while (reader.Next(out line, out tooLong))
                {
                    if (tooLong)
                        writer.WriteLine(McpServer.Error(null, -32600, "Request too large: at most " + MaxRequestBytes + " bytes per line."));
                    else
                        writer.WriteLine(line.Trim().Length == 0 ? "" : server.Handle(line));
                }
            }
            catch (IOException) { }   // The client went away, or Stop closed the pipe.
            catch (ObjectDisposedException) { }
            catch (OperationCanceledException) { }
            catch (Exception ex) { Program.LogError("mcp-client", ex); }
            finally
            {
                // Disposing the pipe alone, not the writer, avoids flushing into a broken pipe.
                Close(pipe);
            }
        }

        /// <summary>
        /// Reads newline-terminated UTF-8 lines while holding at most a fixed number of bytes of each. A longer line is
        /// still consumed up to its newline, its content discarded, and reported as too long: the caller answers it with
        /// one error line, so every line still receives exactly one reply and the relay stays in step. A final line
        /// without a newline counts as a line, and a trailing carriage return is removed, as StreamReader.ReadLine does.
        /// </summary>
        internal sealed class LineReader
        {
            private static readonly Encoding Utf8 = new UTF8Encoding(false);
            private readonly Stream stream;
            private readonly int limit;
            private readonly byte[] buffer = new byte[4096];
            private readonly MemoryStream current = new MemoryStream();
            private int position, length;

            public LineReader(Stream stream, int limit)
            {
                this.stream = stream;
                this.limit = limit;
            }

            /// <summary>Read the next line; false at the end of the stream. A line over the limit comes back as tooLong with a null line.</summary>
            public bool Next(out string line, out bool tooLong)
            {
                line = null;
                tooLong = false;
                current.SetLength(0);
                bool started = false;   // Whether any byte of this line, or its newline, has arrived.
                while (true)
                {
                    if (position == length)
                    {
                        position = 0;
                        length = stream.Read(buffer, 0, buffer.Length);
                        if (length == 0)
                        {
                            if (!started) return false;
                            break;
                        }
                    }
                    started = true;
                    int newline = Array.IndexOf(buffer, (byte)'\n', position, length - position);
                    int end = newline < 0 ? length : newline;
                    if (!tooLong && current.Length + (end - position) > limit)
                    {
                        tooLong = true;
                        current.SetLength(0);
                    }
                    if (!tooLong) current.Write(buffer, position, end - position);
                    position = newline < 0 ? length : newline + 1;
                    if (newline >= 0) break;
                }
                if (tooLong) return true;

                int count = (int)current.Length;
                if (count > 0 && current.GetBuffer()[count - 1] == '\r') count--;
                line = Utf8.GetString(current.GetBuffer(), 0, count);
                return true;
            }
        }
    }

}
