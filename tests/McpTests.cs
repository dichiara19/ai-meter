using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;

namespace AIMeter
{
    /// <summary>The MCP server, its pipe and relay, and the edits that connect AI apps to it.</summary>
    internal static class McpTests
    {
        internal static void Register(Action<string, Action> test)
        {
            test("MCP answers the handshake, lists read-only tools and ignores notifications", Protocol);
            test("MCP tools report limits, sessions without titles, free models and room", Tools);
            test("MCP relay and pipe carry requests from a separate process", Relay);
            test("A client that disconnects abruptly leaves the MCP pipe serving", AbruptDisconnect);
            test("Connecting an app adds only AI Meter's JSON entry and keeps the rest", JsonEdits);
            test("Codex TOML gains and loses only AI Meter's tables", TomlEdits);
        }

        private static void Assert(bool condition, string message)
        {
            if (!condition) throw new Exception(message);
        }

        private static Dictionary<string, object> Reply(McpServer server, string request)
        {
            return Json.Parse(server.Handle(request));
        }

        private static Snapshot Fixture()
        {
            var snap = new Snapshot();
            var claude = new ProviderState { Id = "claude", Name = "Claude", Plan = "Max", Usage = new Usage { Source = "live", At = DateTime.Now } };
            claude.Usage.Windows.Add(new UsageWindow { Label = "Session 5 h", Short = UsageWindow.ShortFiveHours, Used = 30, ResetsAt = DateTime.Now.AddHours(2) });
            claude.Usage.Windows.Add(new UsageWindow { Label = "Week", Short = UsageWindow.ShortWeek, Used = 90, ResetsAt = DateTime.Now.AddDays(2) });
            claude.Sessions.Add(new Session { Kind = "Code · CLI", Title = "Fix the private invoice parser", Status = "busy", Model = "claude-opus-5-5", Updated = DateTime.Now });
            claude.Sessions.Add(new Session { Kind = "Chat", Status = "app" });
            var router = new ProviderState { Id = "openrouter", Name = "OpenRouter", Usage = new Usage { Source = "live", At = DateTime.Now } };
            router.Usage.Windows.Add(new UsageWindow { Label = "Free requests today", Short = UsageWindow.ShortDay, Used = 10 });
            router.Usage.FreeModels = new List<FreeModel>
            {
                new FreeModel { Id = "a/small:free", Name = "Small", Context = 32000 },
                new FreeModel { Id = "b/large:free", Name = "Large", Context = 256000, Tools = true, Expires = DateTime.Today.AddDays(3) },
            };
            router.Usage.TodayModels = new List<ModelCost> { new ModelCost { Model = "b/large:free", Cost = 0, Tokens = 1200, Replies = 3 } };
            router.Sessions.Add(new Session { Kind = "Pi · WSL", Title = "Batch job", Status = "idle", Provider = "openrouter" });
            snap.Providers.Add(claude);
            snap.Providers.Add(router);
            return snap;
        }

        private static void Protocol()
        {
            var server = new McpServer(() => null, () => false);
            var init = Json.Obj(Reply(server, "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{\"protocolVersion\":\"2025-06-18\",\"capabilities\":{}}}"), "result");
            Assert(Json.Str(init, "protocolVersion") == "2025-06-18", "The client's protocol revision was not accepted.");
            Assert(Json.Obj(Json.Obj(init, "capabilities"), "tools") != null, "Tools are not advertised.");
            var unknown = Json.Obj(Reply(server, "{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"initialize\",\"params\":{\"protocolVersion\":\"1999-01-01\"}}"), "result");
            Assert(Json.Str(unknown, "protocolVersion") == "2025-11-25", "An unknown revision did not fall back to the newest.");

            Assert(server.Handle("{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}") == "", "A notification received an answer.");
            var tools = (IList)Json.Obj(Reply(server, "{\"jsonrpc\":\"2.0\",\"id\":\"t\",\"method\":\"tools/list\"}"), "result")["tools"];
            Assert(tools.Count == 6, "Unexpected tool count.");
            Assert(tools.OfType<Dictionary<string, object>>().All(t => Json.Bool(Json.Obj(t, "annotations"), "readOnlyHint")), "A tool is not marked read-only.");

            var missing = Json.Obj(Reply(server, "{\"jsonrpc\":\"2.0\",\"id\":3,\"method\":\"resources/list\"}"), "error");
            Assert(Json.Num(missing, "code") == -32601, "An unknown method was not refused.");
            Assert(Json.Num(Json.Obj(Reply(server, "not json"), "error"), "code") == -32700, "Invalid JSON was not refused.");
            var early = Json.Obj(Reply(server, "{\"jsonrpc\":\"2.0\",\"id\":4,\"method\":\"tools/call\",\"params\":{\"name\":\"get_limits\"}}"), "result");
            Assert(Json.Obj(early, "structuredContent").ContainsKey("status"), "A call before the first snapshot gave no explanation.");
        }

        private static Dictionary<string, object> CallTool(McpServer server, string name, string arguments)
        {
            var result = Json.Obj(Reply(server, "{\"jsonrpc\":\"2.0\",\"id\":9,\"method\":\"tools/call\",\"params\":{\"name\":\"" + name + "\",\"arguments\":" + arguments + "}}"), "result");
            var text = (string)((Dictionary<string, object>)((IList)result["content"])[0])["text"];
            Assert(Json.Parse(text) != null, "The text content of " + name + " is not JSON.");
            return Json.Obj(result, "structuredContent");
        }

        private static void Tools()
        {
            bool titles = false;
            var snap = Fixture();
            var server = new McpServer(() => snap, () => titles);

            var limits = (IList)CallTool(server, "get_limits", "{\"service\":\"claude\"}")["services"];
            var claude = (Dictionary<string, object>)limits[0];
            Assert(limits.Count == 1 && Json.Str(claude, "plan") == "Max", "The service filter or plan is wrong.");
            var week = ((IList)claude["limits"]).OfType<Dictionary<string, object>>().Single(w => Json.Str(w, "kind") == "7d");
            Assert(Json.Num(week, "remaining_percent") == 10 && Json.Str(week, "resets_at") != null, "The weekly limit is wrong.");

            var sessions = ((IList)CallTool(server, "list_sessions", "{}")["sessions"]).OfType<Dictionary<string, object>>().ToList();
            Assert(sessions.Count == 2, "App placeholders were listed as sessions.");
            Assert(sessions.All(s => !s.ContainsKey("title")), "Titles were shared without permission.");
            Assert(sessions.Any(s => Json.Str(s, "provider") == "openrouter"), "The session provider is missing.");
            titles = true;
            var busy = ((IList)CallTool(server, "list_sessions", "{\"busy_only\":true}")["sessions"]).OfType<Dictionary<string, object>>().Single();
            Assert(Json.Str(busy, "title") == "Fix the private invoice parser", "Allowed titles are missing.");

            var free = ((IList)CallTool(server, "list_free_models", "{\"tools\":true,\"min_context\":100000}")["models"]).OfType<Dictionary<string, object>>().Single();
            Assert(Json.Str(free, "id") == "b/large:free" && Json.Str(free, "free_until") != null, "The free model filter is wrong.");

            var spend = ((IList)CallTool(server, "get_spend", "{}")["services"]).OfType<Dictionary<string, object>>().Single();
            Assert(Json.Str(spend, "service") == "openrouter", "Spending lists a service without spending.");

            var order = ((IList)CallTool(server, "suggest_service", "{}")["services"]).OfType<Dictionary<string, object>>().Select(s => Json.Str(s, "service")).ToList();
            Assert(order.SequenceEqual(new[] { "openrouter", "claude" }), "Services are not ordered by room.");
            Assert(CallTool(server, "nothing", "{}").ContainsKey("error"), "An unknown tool did not report an error.");
        }

        private static void Relay()
        {
            string prefix = "AIMeter.Mcp.Test." + Guid.NewGuid().ToString("N") + ".";
            var snap = Fixture();
            new McpPipeHost(new McpServer(() => snap, () => false), prefix + Environment.UserName).Start();
            // Launching is never needed: the host already listens, so the relay connects on its first attempt.
            string script = Path.Combine(Settings.Folder, "mcp.ps1");
            File.WriteAllText(script, McpRelay.Script(prefix, @"C:\Nothing\AI Meter's.exe", ""), new UTF8Encoding(true));

            var start = new ProcessStartInfo(McpClients.PowerShell(false), string.Join(" ", McpClients.Arguments("\"" + script + "\"")))
            {
                UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                CreateNoWindow = true, StandardOutputEncoding = new UTF8Encoding(false),
            };
            using (var process = Process.Start(start))
            {
                var input = new StreamWriter(process.StandardInput.BaseStream, new UTF8Encoding(false)) { NewLine = "\n" };
                input.WriteLine("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{\"protocolVersion\":\"2025-06-18\"}}");
                input.WriteLine("{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}");
                input.WriteLine("{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/call\",\"params\":{\"name\":\"list_free_models\",\"arguments\":{}}}");
                input.Close();
                var lines = process.StandardOutput.ReadToEnd().Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries);
                process.WaitForExit(20000);
                Assert(lines.Length == 2, "Expected two replies, got " + lines.Length + ": " + process.StandardError.ReadToEnd());
                Assert(Json.Num(Json.Parse(lines[1]), "id") == 2 && lines[1].Contains("b/large:free"), "The tool reply did not come through the relay.");
            }
        }

        private static void AbruptDisconnect()
        {
            string name = "AIMeter.Mcp.Test." + Guid.NewGuid().ToString("N");
            var snap = Fixture();
            new McpPipeHost(new McpServer(() => snap, () => false), name).Start();
            var utf8 = new UTF8Encoding(false);
            // A client that sends a request and leaves before the answer: the host's write and close then fail.
            for (int i = 0; i < 3; i++)
                using (var gone = new System.IO.Pipes.NamedPipeClientStream(".", name, System.IO.Pipes.PipeDirection.InOut))
                {
                    gone.Connect(5000);
                    var bytes = utf8.GetBytes("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/list\"}\n");
                    gone.Write(bytes, 0, bytes.Length);
                }
            System.Threading.Thread.Sleep(500);   // An unhandled exception on a client thread would end this process here.
            using (var client = new System.IO.Pipes.NamedPipeClientStream(".", name, System.IO.Pipes.PipeDirection.InOut))
            {
                client.Connect(5000);
                var writer = new StreamWriter(client, utf8) { AutoFlush = true, NewLine = "\n" };
                var reader = new StreamReader(client, utf8);
                writer.WriteLine("{\"jsonrpc\":\"2.0\",\"id\":7,\"method\":\"ping\"}");
                Assert(Json.Num(Json.Parse(reader.ReadLine()), "id") == 7, "The pipe stopped answering after a client left.");
            }
        }

        private static McpClients.Client Client(McpClients.Format format, bool stdioType = false)
        {
            return new McpClients.Client { Id = "x", Name = "Test", Where = "Windows", Format = format, StdioType = stdioType, ConfigPath = "x.json" };
        }

        private static void JsonEdits()
        {
            var args = McpClients.Arguments(@"C:\Users\o'neil\mcp.ps1");
            string existing = "{\"theme\":\"dark\",\"mcpServers\":{\"other\":{\"command\":\"node\",\"args\":[\"x.js\"],\"env\":{\"N\":1}}}}";
            var code = Json.Parse(McpClients.WithJson(Client(McpClients.Format.McpServers, true), existing, "ps.exe", args));
            var servers = Json.Obj(code, "mcpServers");
            Assert(Json.Str(code, "theme") == "dark" && servers.ContainsKey("other"), "Other settings or servers were lost.");
            Assert(Json.Str(Json.Obj(servers, "ai-meter"), "type") == "stdio", "Claude Code's entry lacks its type.");
            Assert(Json.Num(Json.Obj(Json.Obj(servers, "other"), "env"), "N") == 1, "Another server's values changed.");

            var again = McpClients.WithJson(Client(McpClients.Format.McpServers), JsonText.Indented(code), "ps.exe", args);
            Assert(Json.Obj(Json.Parse(again), "mcpServers").Count == 2, "Connecting twice duplicated the entry.");
            var removed = Json.Parse(McpClients.WithoutJson(Client(McpClients.Format.McpServers), again));
            Assert(Json.Obj(removed, "mcpServers").Keys.Single() == "other", "Disconnecting removed more than AI Meter.");
            Assert(McpClients.WithoutJson(Client(McpClients.Format.McpServers), existing) == null, "Disconnecting rewrote a file without AI Meter.");

            var vscode = Json.Obj(Json.Obj(Json.Parse(McpClients.WithJson(Client(McpClients.Format.VsCode), null, "ps.exe", args)), "servers"), "ai-meter");
            Assert(Json.Str(vscode, "type") == "stdio" && Json.Str(vscode, "command") == "ps.exe", "Wrong VS Code entry.");
            var openCode = Json.Obj(Json.Obj(Json.Parse(McpClients.WithJson(Client(McpClients.Format.OpenCode), "{}", "ps.exe", args)), "mcp"), "ai-meter");
            var command = ((IList)openCode["command"]).OfType<string>().ToList();
            Assert(Json.Str(openCode, "type") == "local" && command[0] == "ps.exe" && command.Last() == args.Last(), "Wrong OpenCode entry.");

            bool refused = false;
            try { McpClients.WithJson(Client(McpClients.Format.VsCode), "{ // comment\n \"servers\": {} }", "ps.exe", args); }
            catch (McpClients.ConfigException) { refused = true; }
            Assert(refused, "A file with comments was rewritten.");
            Assert(McpClients.ToWsl(@"C:\Windows\System32\x.exe") == "/mnt/c/Windows/System32/x.exe", "Wrong WSL path.");
        }

        private static void TomlEdits()
        {
            var args = McpClients.Arguments(@"C:\Users\a ""b""\mcp.ps1");
            string existing = "model = \"gpt-6\"\n\n[mcp_servers.other]\ncommand = \"node\"\n\n[mcp_servers.ai-meter]\ncommand = \"old\"\n\n[mcp_servers.ai-meter.env]\nX = \"1\"\n\n[profiles.fast]\nmodel = \"mini\"\n";
            string added = McpClients.WithToml(existing, @"C:\Windows\powershell.exe", args);
            Assert(added.Split('\n').Count(l => l.StartsWith("[mcp_servers.ai-meter", StringComparison.Ordinal)) == 1, "The old entry was not replaced.");
            Assert(added.Contains("command = \"C:\\\\Windows\\\\powershell.exe\"") && added.Contains("\\\"b\\\""), "Paths are not escaped as TOML strings.");
            Assert(added.Contains("[mcp_servers.other]") && added.Contains("[profiles.fast]\nmodel = \"mini\"") && !added.Contains("X = \"1\""), "Other tables were lost or ours kept.");
            string removed = McpClients.WithoutToml(added);
            Assert(!removed.Contains("ai-meter") && removed.Contains("model = \"gpt-6\"") && removed.Contains("[profiles.fast]"), "Disconnecting removed the wrong tables.");
            Assert(McpClients.WithoutToml(removed) == null, "Disconnecting changed a file without AI Meter.");
            Assert(McpClients.WithToml(null, "ps.exe", args).StartsWith("[mcp_servers.ai-meter]\n", StringComparison.Ordinal), "A new file is wrong.");
        }
    }
}
