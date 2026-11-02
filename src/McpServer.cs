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
    /// Clients start mcp.ps1 (McpRelay), which forwards stdin and stdout to the named pipe served by McpPipeHost.
    /// Over the pipe each JSON-RPC message is one line, and every line receives exactly one line back, empty for
    /// notifications, so the relay can work in lockstep without parsing the protocol.
    /// </summary>
    internal sealed class McpServer
    {
        /// <summary>Protocol revisions this server implements; the newest is offered when the client's is unknown.</summary>
        private static readonly string[] Revisions = { "2025-11-25", "2025-06-18", "2025-03-26", "2024-11-05" };

        private readonly Func<Snapshot> snapshot;
        private readonly Func<bool> titlesAllowed;

        public McpServer(Func<Snapshot> snapshot, Func<bool> titlesAllowed)
        {
            this.snapshot = snapshot;
            this.titlesAllowed = titlesAllowed;
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
                                  "and list_free_models to find a model that costs nothing. Percentages are 0-100; times are local ISO 8601." },
            };
        }

        // ------------------------------------------------------------ tools

        private static List<object> Tools()
        {
            var service = Property("string", "Optional service id, such as claude, openai, copilot or openrouter. All services when omitted.");
            return new List<object>
            {
                Tool("get_limits", "Usage limits of each service the user turned on in AI Meter: percentage used, when each limit resets, " +
                     "the forecast time it runs out at the current pace, and spending against money limits.",
                     new Dictionary<string, object> { { "service", service } }),
                Tool("list_sessions", "Running and recent sessions of AI apps and coding agents (Claude, Codex, Copilot, Pi, OpenCode and others), " +
                     "with agent, status, model and the provider the session uses.",
                     new Dictionary<string, object> { { "service", service }, { "busy_only", Property("boolean", "Only sessions that are working now.") } }),
                Tool("list_free_models", "Models offered at no cost by OpenRouter and other services the user turned on, with model id, " +
                     "context window, tool and reasoning support and the last free day when announced. Use the id as the model name.",
                     new Dictionary<string, object>
                     {
                         { "service", service },
                         { "tools", Property("boolean", "Only models that support tool calls.") },
                         { "min_context", Property("integer", "Smallest context window, in tokens.") },
                     }),
                Tool("get_spend", "Today's spending: models used through each service with cost, tokens and replies, and money limits " +
                     "such as budgets and credits.", new Dictionary<string, object> { { "service", service } }),
                Tool("list_local_models", "Models loaded by local servers such as Ollama, LM Studio and llama.cpp, on Windows and WSL, " +
                     "with memory, context and activity.", new Dictionary<string, object>()),
                Tool("suggest_service", "Services ordered by the room left in their tightest limit, to choose where to run the next task, " +
                     "with the number of free models each one offers.", new Dictionary<string, object>()),
            };
        }

        private static Dictionary<string, object> Tool(string name, string description, Dictionary<string, object> properties)
        {
            return new Dictionary<string, object>
            {
                { "name", name },
                { "description", description },
                { "inputSchema", new Dictionary<string, object> { { "type", "object" }, { "properties", properties } } },
                { "annotations", new Dictionary<string, object> { { "readOnlyHint", true }, { "openWorldHint", false } } },
            };
        }

        private static Dictionary<string, object> Property(string type, string description)
        {
            return new Dictionary<string, object> { { "type", type }, { "description", description } };
        }

        private Dictionary<string, object> Call(Dictionary<string, object> parameters)
        {
            string name = Json.Str(parameters, "name");
            var args = Json.Obj(parameters, "arguments") ?? new Dictionary<string, object>();
            var snap = snapshot();
            if (snap == null) return ToolResult(new Dictionary<string, object> { { "status", "AI Meter is still reading the first data; try again in a few seconds." } }, false);

            string service = Json.Str(args, "service");
            var providers = snap.Providers.Where(p => service == null || string.Equals(p.Id, service, StringComparison.OrdinalIgnoreCase)).ToList();
            object result;
            switch (name)
            {
                case "get_limits": result = Limits(providers.Where(p => p.Models == null)); break;
                case "list_sessions": result = Sessions(providers, Json.Bool(args, "busy_only")); break;
                case "list_free_models": result = Free(providers, Json.Bool(args, "tools"), (long)(Json.Num(args, "min_context") ?? 0)); break;
                case "get_spend": result = Spend(providers); break;
                case "list_local_models": result = LocalModels(snap.Providers); break;
                case "suggest_service": result = Suggest(snap.Providers); break;
                default: return ToolResult(new Dictionary<string, object> { { "error", "Unknown tool: " + name } }, true);
            }
            var content = new Dictionary<string, object> { { "updated_at", Time(snap.Taken) }, { "sample_data", Program.Demo }, { ResultKey(name), result } };
            return ToolResult(content, false);
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

        private static List<object> Suggest(IEnumerable<ProviderState> providers)
        {
            var rows = new List<Tuple<double, Dictionary<string, object>>>();
            foreach (var p in providers.Where(x => x.Models == null && x.Usage != null && !x.NeedsLogin))
            {
                var tight = Ui.Tightest(p.Usage);
                int free = p.Usage.FreeModels == null ? 0 : p.Usage.FreeModels.Count;
                if (tight == null && free == 0) continue;
                var d = Service(p);
                double room = tight == null ? 100 : p.Usage.Room(tight);
                if (tight != null)
                {
                    d["tightest_limit"] = tight.Suffix == null ? tight.Label : tight.Label + " " + tight.Suffix;
                    d["remaining_percent"] = Math.Round(room, 1);
                    if (tight.ResetsAt.HasValue) d["resets_at"] = Time(tight.ResetsAt.Value);
                    if (tight.RunsOutAt.HasValue) d["runs_out_at"] = Time(tight.RunsOutAt.Value);
                }
                if (free > 0) d["free_models"] = free;
                rows.Add(Tuple.Create(room, d));
            }
            return rows.OrderByDescending(r => r.Item1).Select(r => (object)r.Item2).ToList();
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
