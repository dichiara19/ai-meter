using System;
using System.Collections;
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
    /// sessions, free models and today's spending). Every tool is read-only and nothing secret is returned: no keys,
    /// tokens or account identities; session titles only when the user allows them.
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

        public McpServer(Func<Snapshot> snapshot, Func<bool> titlesAllowed, RefreshGate gate = null, TimeSpan? timeout = null)
        {
            this.snapshot = snapshot;
            this.titlesAllowed = titlesAllowed;
            this.gate = gate;
            this.timeout = timeout ?? RefreshGate.DefaultTimeout;
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
                     "context window, tool and reasoning support and the last free day when announced. Use the id as the model name.",
                     new Dictionary<string, object>
                     {
                         { "service", service },
                         { "tools", Property("boolean", "Only models that support tool calls.") },
                         { "min_context", Property("integer", "Smallest context window, in tokens.") },
                         { "refresh", refresh("their model lists") },
                         { "max_age_seconds", maxAge("their model lists") },
                     }, true),
                Tool("get_spend", "Today's spending: models used through each service with cost, tokens and replies, and money limits " +
                     "such as budgets and credits.",
                     new Dictionary<string, object> { { "service", service }, { "refresh", refresh("their usage") }, { "max_age_seconds", maxAge("their usage") } }, true),
                Tool("list_local_models", "Models loaded by local servers such as Ollama, LM Studio and llama.cpp, on Windows and WSL, " +
                     "with memory, context and activity.", new Dictionary<string, object> { { "refresh", localRefresh } }, false),
                Tool("suggest_service", "Services ordered by the room left in their tightest limit, to choose where to run the next task, " +
                     "with the number of free models each one offers. Services whose limits are unknown come after those with known room.",
                     new Dictionary<string, object> { { "refresh", refresh("their limits") }, { "max_age_seconds", maxAge("their limits") } }, true),
            };
        }

        /// <summary>A read-only tool; openWorld marks the tools whose refresh contacts the services.</summary>
        private static Dictionary<string, object> Tool(string name, string description, Dictionary<string, object> properties, bool openWorld)
        {
            return new Dictionary<string, object>
            {
                { "name", name },
                { "description", description },
                { "inputSchema", new Dictionary<string, object> { { "type", "object" }, { "properties", properties } } },
                { "annotations", new Dictionary<string, object> { { "readOnlyHint", true }, { "openWorldHint", openWorld } } },
            };
        }

        private static Dictionary<string, object> Property(string type, string description)
        {
            return new Dictionary<string, object> { { "type", type }, { "description", description } };
        }

        private static readonly Dictionary<string, Scope> Scopes = new Dictionary<string, Scope>
        {
            { "get_limits", Scope.Usage }, { "get_spend", Scope.Usage }, { "suggest_service", Scope.Usage },
            { "list_free_models", Scope.Catalogue }, { "list_sessions", Scope.Local }, { "list_local_models", Scope.Local },
        };

        private Dictionary<string, object> Call(Dictionary<string, object> parameters)
        {
            string name = Json.Str(parameters, "name");
            var args = Json.Obj(parameters, "arguments") ?? new Dictionary<string, object>();
            Scope scope;
            if (name == null || !Scopes.TryGetValue(name, out scope)) return ToolResult(new Dictionary<string, object> { { "error", "Unknown tool: " + name } }, true);
            bool refresh = Json.Bool(args, "refresh");
            double? maxAge = Json.Num(args, "max_age_seconds");
            if (maxAge < 0) return ToolResult(new Dictionary<string, object> { { "error", "max_age_seconds cannot be negative." } }, true);

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
                case "list_free_models": result = Free(providers, Json.Bool(args, "tools"), (long)(Json.Num(args, "min_context") ?? 0)); break;
                case "get_spend": result = Spend(providers); break;
                case "list_local_models": result = LocalModels(snap.Providers); break;
                default: result = Suggest(snap.Providers); break;
            }
            var content = new Dictionary<string, object> { { "updated_at", Time(snap.Taken) }, { "sample_data", Program.Demo }, { ResultKey(name), result } };
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
            if (p.Usage != null && (p.Usage.FreeModels != null || p.Usage.FreeModelsAt.HasValue)) return true;
            var d = Definitions.Get(p.Id);
            return d != null && Json.Obj(d.Raw, "freeModels") != null;
        }

        /// <summary>When the data a scope relies on was read from the service; null when it never was.</summary>
        private static DateTime? ReadAt(ProviderState p, Scope scope)
        {
            if (p.Usage == null) return null;
            return scope == Scope.Catalogue ? p.Usage.FreeModelsAt : p.Usage.At;
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

        private static List<object> Free(IEnumerable<ProviderState> providers, bool toolsOnly, long minContext)
        {
            var list = new List<object>();
            foreach (var p in providers.Where(x => x.Usage != null && x.Usage.FreeModels != null))
                foreach (var m in p.Usage.FreeModels.Where(f => (!toolsOnly || f.Tools) && f.Context >= minContext))
                {
                    var d = new Dictionary<string, object> { { "service", p.Id }, { "id", m.Id }, { "name", m.Name }, { "tools", m.Tools }, { "reasoning", m.Reasoning } };
                    if (m.Context > 0) d["context_tokens"] = m.Context;
                    if (!string.IsNullOrEmpty(m.Inputs)) d["inputs"] = m.Inputs;
                    if (m.Expires.HasValue) d["free_until"] = m.Expires.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                    list.Add(d);
                }
            return list;
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
            foreach (var p in providers.Where(x => x.Models == null && x.Usage != null && !x.NeedsLogin))
            {
                var tight = Ui.Tightest(p.Usage);
                int free = p.Usage.FreeModels == null ? 0 : p.Usage.FreeModels.Count;
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
                bool stale = p.Usage.Source == "cache";
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

        private static string Error(object id, int code, string text)
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
    /// </summary>
    internal sealed class McpPipeHost
    {
        public const string PipePrefix = "AIMeter.Mcp.";
        public static string PipeName { get { return PipePrefix + Environment.UserName; } }

        private const int MaxClients = 16;
        private readonly McpServer server;
        private readonly string name;
        private volatile bool stopping;

        public McpPipeHost(McpServer server, string name = null)
        {
            this.server = server;
            this.name = name ?? PipeName;
        }

        public void Start()
        {
            new Thread(AcceptLoop) { IsBackground = true, Name = "AIMeter.Mcp", Priority = ThreadPriority.BelowNormal }.Start();
        }

        public void Stop() { stopping = true; }

        private void AcceptLoop()
        {
            var security = new PipeSecurity();
            security.AddAccessRule(new PipeAccessRule(WindowsIdentity.GetCurrent().User, PipeAccessRights.ReadWrite | PipeAccessRights.CreateNewInstance, AccessControlType.Allow));
            while (!stopping)
            {
                NamedPipeServerStream pipe = null;
                try
                {
                    pipe = new NamedPipeServerStream(name, PipeDirection.InOut, MaxClients, PipeTransmissionMode.Byte, PipeOptions.None, 4096, 4096, security);
                    pipe.WaitForConnection();
                    var connected = pipe;
                    pipe = null;
                    new Thread(() => Serve(connected)) { IsBackground = true, Name = "AIMeter.Mcp.Client" }.Start();
                }
                catch (IOException ex)
                {
                    // All instances busy, or the name is taken by another copy: wait instead of spinning.
                    if (pipe != null) pipe.Dispose();
                    Program.LogError("mcp-pipe", ex);
                    Thread.Sleep(5000);
                }
                catch (Exception ex)
                {
                    // Access refused or anything unexpected: this thread must never end AI Meter.
                    if (pipe != null) try { pipe.Dispose(); } catch (Exception) { }
                    Program.LogError("mcp-pipe", ex);
                    Thread.Sleep(30000);
                }
            }
        }

        /// <summary>
        /// Answer one client until it disconnects. This runs on its own thread, where an unhandled exception would end
        /// AI Meter, so every failure, including the flush when the pipe is closed after the client went away, stays here.
        /// </summary>
        private void Serve(NamedPipeServerStream pipe)
        {
            var utf8 = new UTF8Encoding(false);
            try
            {
                var reader = new StreamReader(pipe, utf8);
                var writer = new StreamWriter(pipe, utf8) { AutoFlush = true, NewLine = "\n" };
                string line;
                while (!stopping && (line = reader.ReadLine()) != null)
                    writer.WriteLine(line.Trim().Length == 0 ? "" : server.Handle(line));
            }
            catch (IOException) { }   // The client went away.
            catch (ObjectDisposedException) { }
            catch (Exception ex) { Program.LogError("mcp-client", ex); }
            finally
            {
                // Disposing the pipe alone, not the reader and writer, avoids flushing into a broken pipe.
                try { pipe.Dispose(); }
                catch (Exception) { }
            }
        }
    }

    /// <summary>Indented JSON for files people read and edit, and for tool text that models read.</summary>
    internal static class JsonText
    {
        public static string Indented(object value)
        {
            var sb = new StringBuilder();
            Write(sb, value, 0);
            return sb.ToString();
        }

        private static void Write(StringBuilder sb, object value, int depth)
        {
            var map = value as IDictionary<string, object>;
            var list = value as IList;
            if (map != null)
            {
                if (map.Count == 0) { sb.Append("{}"); return; }
                sb.Append('{');
                int i = 0;
                foreach (var kv in map)
                {
                    sb.Append(i++ == 0 ? "\n" : ",\n").Append(' ', (depth + 1) * 2).Append(Json.Serialize(kv.Key)).Append(": ");
                    Write(sb, kv.Value, depth + 1);
                }
                sb.Append('\n').Append(' ', depth * 2).Append('}');
            }
            else if (list != null && !(value is string))
            {
                if (list.Count == 0) { sb.Append("[]"); return; }
                sb.Append('[');
                for (int i = 0; i < list.Count; i++)
                {
                    sb.Append(i == 0 ? "\n" : ",\n").Append(' ', (depth + 1) * 2);
                    Write(sb, list[i], depth + 1);
                }
                sb.Append('\n').Append(' ', depth * 2).Append(']');
            }
            else sb.Append(Json.Serialize(value));
        }
    }
}
