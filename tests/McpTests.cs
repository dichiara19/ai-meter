using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace AIMeter
{
    /// <summary>The MCP server, its pipe and relay, and the edits that connect AI apps to it.</summary>
    internal static class McpTests
    {
        internal static void Register(Action<string, Action> test)
        {
            test("MCP answers the handshake, marks only the lease tools as writing and ignores notifications", Protocol);
            test("MCP tools report limits, sessions without titles, free models and room", Tools);
            test("An MCP refresh asks only for old readings and reports each service's freshness", RefreshTargets);
            test("Concurrent MCP refreshes share one collection", RefreshShared);
            test("A call after a refresh sees the new reading before the surfaces apply it", RefreshThenRead);
            test("An MCP refresh respects Retry-After, the minimum interval, pauses and its timeout", RefreshLimits);
            test("suggest_service reports unknown room as unknown and ranks it after known room", SuggestUnknown);
            test("Retry-After is read as seconds or an HTTP date and capped", RetryAfter);
            test("MCP relay and pipe carry requests from a separate process", Relay);
            test("A client that disconnects abruptly leaves the MCP pipe serving", AbruptDisconnect);
            test("MCP request lines are read up to a limit, and a longer one is refused without ending the session", LongLines);
            test("Stopping the MCP pipe ends waiting sessions and accepts no one", PipeStop);
            test("Connecting an app adds only AI Meter's JSON entry and keeps the rest", JsonEdits);
            test("Codex TOML gains and loses only AI Meter's tables", TomlEdits);
            test("Codex TOML headers inside strings, comments and multi-line arrays are left alone", TomlLexical);
            test("Codex TOML that the editor cannot follow is refused, not rewritten", TomlRefusals);
            test("A save that fails keeps the previous file and removes the temporary one", FailedSave);
            test("Antigravity CLI is found by its own folder and its empty MCP file is connected", AntigravityCli);
            test("Command-line installation quotes paths and places -- for each agent", Commands);
            test("A copy button confirms the copy on itself, then returns to its label", CopiedFeedback);
        }

        private static void CopiedFeedback()
        {
            var button = new System.Windows.Controls.Button { Content = "WSL", MinWidth = 10 };
            SettingsWindow.ShowCopied(button, TimeSpan.FromMilliseconds(150));
            Assert(button.Content is System.Windows.Controls.Panel, "The button does not show the confirmation.");
            Assert(System.Windows.Automation.AutomationProperties.GetName(button) == L.T("Copied"), "Screen readers are not told about the copy.");
            Assert(System.Windows.Automation.AutomationProperties.GetLiveSetting(button) != System.Windows.Automation.AutomationLiveSetting.Off, "The confirmation is not a live region.");
            SettingsWindow.ShowCopied(button, TimeSpan.FromMilliseconds(150));   // A second click while it shows "Copied".

            // Let the dispatcher run the timer.
            var frame = new System.Windows.Threading.DispatcherFrame();
            var stop = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
            stop.Tick += (o, e) => { stop.Stop(); frame.Continue = false; };
            stop.Start();
            System.Windows.Threading.Dispatcher.PushFrame(frame);

            Assert(button.Content as string == "WSL", "The label did not come back, or a second click replaced it.");
            Assert(button.MinWidth == 10 && string.IsNullOrEmpty(System.Windows.Automation.AutomationProperties.GetName(button)), "The button kept its temporary width or name.");

            // The reserved width fits the wider confirmation, so showing it does not move the buttons beside it.
            var narrow = new System.Windows.Controls.Button { Content = "WSL" };
            var infinite = new System.Windows.Size(double.PositiveInfinity, double.PositiveInfinity);
            narrow.Measure(infinite);
            double labelWidth = narrow.DesiredSize.Width;
            double reserved = SettingsWindow.CopiedWidth(narrow);
            Assert(narrow.Content as string == "WSL", "Measuring changed the label.");
            SettingsWindow.ShowCopied(narrow, TimeSpan.FromSeconds(5));
            narrow.UpdateLayout();
            Assert(reserved > labelWidth && reserved >= narrow.DesiredSize.Width - 0.5,
                "The reserved width does not fit the confirmation: label " + labelWidth + ", reserved " + reserved + ", shown " + narrow.DesiredSize.Width + ".");
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
            router.Catalogue = new Catalogue
            {
                ReadAt = DateTime.Now.AddHours(-1),
                Models = new List<FreeModel>
                {
                    new FreeModel { Id = "a/small:free", Name = "Small", Context = 32000 },
                    new FreeModel { Id = "b/large:free", Name = "Large", Context = 256000, MaxOutput = 8192, Tools = true, Expires = DateTime.Today.AddDays(3) },
                },
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
            Assert(tools.Count == 11, "Unexpected tool count.");
            var writers = new[] { "claim_capacity", "renew_capacity", "release_capacity" };
            Assert(tools.OfType<Dictionary<string, object>>().All(t => Json.Bool(Json.Obj(t, "annotations"), "readOnlyHint") != writers.Contains(Json.Str(t, "name"))),
                "Only the lease tools may change state.");

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
            Assert(Json.Num(free, "max_output_tokens") == 8192, "The output limit is missing.");

            var spend = ((IList)CallTool(server, "get_spend", "{}")["services"]).OfType<Dictionary<string, object>>().Single();
            Assert(Json.Str(spend, "service") == "openrouter", "Spending lists a service without spending.");

            var order = ((IList)CallTool(server, "suggest_service", "{}")["services"]).OfType<Dictionary<string, object>>().Select(s => Json.Str(s, "service")).ToList();
            Assert(order.SequenceEqual(new[] { "openrouter", "claude" }), "Services are not ordered by room.");
            Assert(CallTool(server, "nothing", "{}").ContainsKey("error"), "An unknown tool did not report an error.");
        }

        /// <summary>Stands in for the collection worker: after each wake it waits briefly, then publishes collect().</summary>
        private sealed class FakeWorker
        {
            public readonly List<string> Invalidated = new List<string>();
            public int Collections;
            public readonly RefreshGate Gate;
            private readonly AutoResetEvent wake = new AutoResetEvent(false);

            public FakeWorker(Func<Snapshot> collect, bool paused = false, bool publishes = true)
            {
                Gate = new RefreshGate((id, catalogue) => { lock (Invalidated) Invalidated.Add(catalogue ? id + "#catalogue" : id); }, () => wake.Set(), () => paused);
                new Thread(() =>
                {
                    while (true)
                    {
                        wake.WaitOne();
                        Thread.Sleep(200);
                        if (!publishes) continue;
                        var s = collect();
                        Interlocked.Increment(ref Collections);
                        Gate.Publish(s);
                    }
                }) { IsBackground = true }.Start();
            }
        }

        /// <summary>The fixture with Claude's reading taken ten minutes ago.</summary>
        private static Snapshot OldClaude()
        {
            var snap = Fixture();
            snap.Providers[0].Usage.At = DateTime.Now.AddMinutes(-10);
            return snap;
        }

        private static Dictionary<string, object> Fresh(Dictionary<string, object> content, string service)
        {
            return Json.Obj(Json.Obj(content, "freshness"), service);
        }

        private static void RefreshTargets()
        {
            NetworkBackoff.Clear();
            var worker = new FakeWorker(() =>
            {
                var s = Fixture();
                s.Providers[1].Catalogue.ReadAt = DateTime.Now;
                return s;
            });
            var snap = OldClaude();
            var server = new McpServer(() => snap, () => false, worker.Gate, TimeSpan.FromSeconds(5));

            var plain = CallTool(server, "get_limits", "{}");
            Assert(worker.Invalidated.Count == 0 && Json.Str(Fresh(plain, "claude"), "refresh") == "not_requested", "A plain read asked for a refresh.");
            Assert(Json.Str(Fresh(plain, "claude"), "read_at") != null && Json.Str(Fresh(plain, "claude"), "state") == "fresh", "Freshness lacks the reading time.");

            var limits = CallTool(server, "get_limits", "{\"max_age_seconds\":300}");
            Assert(worker.Invalidated.SequenceEqual(new[] { "claude" }), "Only Claude's old reading should be asked for: " + string.Join(",", worker.Invalidated));
            Assert(Json.Str(Fresh(limits, "claude"), "refresh") == "refreshed", "The refreshed service is not marked so.");
            Assert(Json.Str(Fresh(limits, "openrouter"), "refresh") == "cached", "A recent reading was not kept.");

            var models = CallTool(server, "list_free_models", "{\"refresh\":true}");
            Assert(worker.Invalidated.Last() == "openrouter#catalogue" && worker.Invalidated.Count == 2, "The free-model list was not asked for on its own.");
            Assert(Json.Str(Fresh(models, "openrouter"), "refresh") == "refreshed" && Fresh(models, "claude") == null, "Model-list freshness is wrong.");

            var sessions = CallTool(server, "list_sessions", "{\"refresh\":true}");
            Assert(Json.Str(sessions, "refresh") == "refreshed" && worker.Invalidated.Count == 2, "A local refresh contacted a service or did not wait.");
            Assert(CallTool(server, "get_limits", "{\"max_age_seconds\":-1}").ContainsKey("error"), "A negative age was accepted.");
        }

        private static void RefreshShared()
        {
            NetworkBackoff.Clear();
            var worker = new FakeWorker(Fixture);
            var snap = OldClaude();
            var server = new McpServer(() => snap, () => false, worker.Gate, TimeSpan.FromSeconds(5));
            var results = new Dictionary<string, object>[2];
            var threads = Enumerable.Range(0, 2).Select(i => new Thread(() => results[i] = CallTool(server, "get_limits", "{\"refresh\":true}"))).ToList();
            threads[0].Start();
            Thread.Sleep(50);
            threads[1].Start();
            foreach (var t in threads) t.Join(10000);
            Assert(results.All(r => r != null && Json.Str(Fresh(r, "claude"), "refresh") == "refreshed"), "A waiting caller did not receive the shared reading.");
            Assert(worker.Collections == 1 && worker.Invalidated.Count == 1, "Concurrent callers started " + worker.Collections + " collections.");
        }

        private static void RefreshThenRead()
        {
            NetworkBackoff.Clear();
            var worker = new FakeWorker(Fixture);
            var shown = OldClaude();   // What the surfaces still show: the controller applies snapshots after history.
            var server = new McpServer(() => worker.Gate.Newest(shown), () => false, worker.Gate, TimeSpan.FromSeconds(5));
            Assert(Json.Str(Fresh(CallTool(server, "get_limits", "{\"refresh\":true}"), "claude"), "refresh") == "refreshed", "The first refresh failed.");
            var next = Fresh(CallTool(server, "get_limits", "{\"refresh\":true}"), "claude");
            Assert(Json.Str(next, "refresh") == "cached", "The next call used the older snapshot: " + Json.Str(next, "refresh"));
        }

        private static void RefreshLimits()
        {
            NetworkBackoff.Clear();
            // A service that keeps failing: the new reading is as old as before.
            var worker = new FakeWorker(OldClaude);
            var snap = OldClaude();
            var server = new McpServer(() => snap, () => false, worker.Gate, TimeSpan.FromSeconds(5));

            NetworkBackoff.Set("claude", DateTime.Now.AddMinutes(10));
            var blocked = Fresh(CallTool(server, "get_limits", "{\"service\":\"claude\",\"refresh\":true}"), "claude");
            Assert(Json.Str(blocked, "refresh") == "backoff" && Json.Str(blocked, "retry_after") != null && worker.Invalidated.Count == 0, "Retry-After was not respected.");
            NetworkBackoff.Clear();

            var failed = Fresh(CallTool(server, "get_limits", "{\"service\":\"claude\",\"refresh\":true}"), "claude");
            Assert(Json.Str(failed, "refresh") == "unchanged", "A failed read was reported as refreshed.");
            var again = Fresh(CallTool(server, "get_limits", "{\"service\":\"claude\",\"refresh\":true}"), "claude");
            Assert(Json.Str(again, "refresh") == "too_soon" && Json.Str(again, "retry_after") != null && worker.Invalidated.Count == 1, "A second refresh within a minute reached the service.");

            var paused = new McpServer(() => snap, () => false, new FakeWorker(Fixture, true).Gate, TimeSpan.FromSeconds(5));
            Assert(Json.Str(Fresh(CallTool(paused, "get_limits", "{\"refresh\":true}"), "claude"), "refresh") == "paused", "A paused collection was awaited.");

            var silent = new McpServer(() => snap, () => false, new FakeWorker(Fixture, false, false).Gate, TimeSpan.FromMilliseconds(400));
            var late = CallTool(silent, "get_limits", "{\"refresh\":true}");
            Assert(Json.Str(Fresh(late, "claude"), "refresh") == "timed_out" && ((IList)late["services"]).Count == 2, "A timeout did not return the previous reading.");
        }

        private static void SuggestUnknown()
        {
            var snap = Fixture();
            var stale = new ProviderState { Id = "aaa", Name = "Stale", Usage = new Usage { Source = "cache", At = DateTime.Now.AddHours(-1) } };
            stale.Usage.Windows.Add(new UsageWindow { Label = "Day", Short = UsageWindow.ShortDay, Used = 10 });
            // A service with a model list but no readable usage at all.
            var unknown = new ProviderState { Id = "nvidia", Name = "NVIDIA" };
            unknown.Catalogue = new Catalogue { ReadAt = DateTime.Now, Models = new List<FreeModel> { new FreeModel { Id = "n/free", Name = "Free" } } };
            snap.Providers.Add(unknown);
            snap.Providers.Add(stale);
            var rows = ((IList)CallTool(new McpServer(() => snap, () => false), "suggest_service", "{}")["services"]).OfType<Dictionary<string, object>>().ToList();
            Assert(rows.Select(r => Json.Str(r, "service")).SequenceEqual(new[] { "openrouter", "aaa", "claude", "nvidia" }),
                "Wrong order: " + string.Join(",", rows.Select(r => Json.Str(r, "service"))));
            var last = rows.Last();
            Assert(last.ContainsKey("remaining_percent") && last["remaining_percent"] == null && Json.Str(last, "quota") == "unknown", "Unknown room was not reported as unknown.");
            Assert(Json.Bool(rows[1], "stale"), "A stale reading is not marked.");
        }

        private static void RetryAfter()
        {
            var now = new DateTime(2026, 11, 2, 12, 0, 0, DateTimeKind.Utc);
            Assert(NetworkBackoff.Parse("120", now) == TimeSpan.FromSeconds(120), "Seconds were misread.");
            Assert(NetworkBackoff.Parse("Mon, 02 Nov 2026 12:01:30 GMT", now) == TimeSpan.FromSeconds(90), "An HTTP date was misread.");
            Assert(NetworkBackoff.Parse("Mon, 02 Nov 2026 11:00:00 GMT", now) == TimeSpan.Zero, "A past date gave a negative delay.");
            Assert(NetworkBackoff.Parse("9999999", now) == TimeSpan.FromHours(24), "A huge delay was not capped.");
            Assert(NetworkBackoff.Parse("soon", now) == null && NetworkBackoff.Parse("-5", now) == null, "An invalid value was accepted.");
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

        private static List<string> ReadAll(McpPipeHost.LineReader reader)
        {
            var lines = new List<string>();
            string line;
            bool tooLong;
            while (reader.Next(out line, out tooLong)) lines.Add(tooLong ? "<too long>" : line);
            return lines;
        }

        private static void LongLines()
        {
            // The reader alone, with a small limit and lines that cross its 4096-byte buffer.
            var utf8 = new UTF8Encoding(false);
            string exact = new string('a', 5000), over = new string('b', 5001);
            var input = new MemoryStream(utf8.GetBytes(exact + "\n" + over + "\r\n\nnext\r\n" + over + "\n\u00e8 last"));
            var lines = ReadAll(new McpPipeHost.LineReader(input, 5000));
            Assert(lines.SequenceEqual(new[] { exact, "<too long>", "", "next", "<too long>", "\u00e8 last" }),
                "Wrong lines: " + string.Join(" | ", lines.Select(l => l.Length > 20 ? l.Substring(0, 5) + "..." + l.Length : l)));
            Assert(ReadAll(new McpPipeHost.LineReader(new MemoryStream(utf8.GetBytes("one\n")), 10)).SequenceEqual(new[] { "one" }),
                "A stream that ends after a newline gained an empty line.");
            Assert(ReadAll(new McpPipeHost.LineReader(new MemoryStream(), 10)).Count == 0, "An empty stream produced a line.");

            // Through the pipe: an over-long request receives one error with a null id, and the session goes on.
            string name = "AIMeter.Mcp.Test." + Guid.NewGuid().ToString("N");
            var snap = Fixture();
            var host = new McpPipeHost(new McpServer(() => snap, () => false), name);
            host.Start();
            try
            {
                using (var client = new System.IO.Pipes.NamedPipeClientStream(".", name, System.IO.Pipes.PipeDirection.InOut))
                {
                    client.Connect(5000);
                    var writer = new StreamWriter(client, utf8) { AutoFlush = true, NewLine = "\n" };
                    var reader = new StreamReader(client, utf8);
                    string padding = new string(' ', McpPipeHost.MaxRequestBytes);
                    writer.WriteLine("{\"jsonrpc\":\"2.0\",\"id\":5,\"method\":\"ping\"" + padding + "}");
                    var refused = Json.Parse(reader.ReadLine());
                    Assert(Json.Num(Json.Obj(refused, "error"), "code") == -32600 && refused.ContainsKey("id") && refused["id"] == null,
                        "An over-long request was not refused with a null id.");
                    writer.WriteLine("{\"jsonrpc\":\"2.0\",\"id\":6,\"method\":\"ping\"}");
                    Assert(Json.Num(Json.Parse(reader.ReadLine()), "id") == 6, "The session ended after an over-long request.");
                }
            }
            finally { host.Stop(); }
        }

        private static void PipeStop()
        {
            string name = "AIMeter.Mcp.Test." + Guid.NewGuid().ToString("N");
            var snap = Fixture();
            var host = new McpPipeHost(new McpServer(() => snap, () => false), name);
            host.Start();
            var utf8 = new UTF8Encoding(false);
            using (var client = new System.IO.Pipes.NamedPipeClientStream(".", name, System.IO.Pipes.PipeDirection.InOut))
            {
                client.Connect(5000);
                var writer = new StreamWriter(client, utf8) { AutoFlush = true, NewLine = "\n" };
                var reader = new StreamReader(client, utf8);
                writer.WriteLine("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"ping\"}");
                Assert(Json.Num(Json.Parse(reader.ReadLine()), "id") == 1, "The pipe did not answer before stopping.");
                Thread.Sleep(200);   // Let the host wait for the next request and for the next client.

                // The session's server thread is blocked reading: stopping must close the pipe, so the client sees the end.
                bool ended = false;
                var waiting = new Thread(() =>
                {
                    try { ended = reader.ReadLine() == null; }
                    catch (IOException) { ended = true; }
                }) { IsBackground = true };
                waiting.Start();
                host.Stop();
                Assert(waiting.Join(5000) && ended, "Stopping left a connected session open.");
            }

            bool accepted = true;
            using (var late = new System.IO.Pipes.NamedPipeClientStream(".", name, System.IO.Pipes.PipeDirection.InOut))
            {
                try { late.Connect(500); }
                catch (TimeoutException) { accepted = false; }
                catch (IOException) { accepted = false; }
            }
            Assert(!accepted, "A stopped pipe still accepted a client.");
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

        private static void TomlLexical()
        {
            var args = McpClients.Arguments(@"C:\x\mcp.ps1");
            Func<string, int> ownLines = t => t.Split('\n').Count(l => l == "[mcp_servers.ai-meter]");

            // A table shown inside a """ string is text: it stays as it is, and the real table goes after the string.
            string instructions = "model = \"gpt-6\"\ndeveloper_instructions = \"\"\"\nTo connect, add:\n[mcp_servers.ai-meter]\ncommand = \"fake\"\n\"\"\"\n\n[profiles.fast]\nmodel = \"mini\"\n";
            Assert(McpClients.WithoutToml(instructions) == null && !McpClients.HasTomlEntry(instructions), "A header inside a \"\"\" string counts as AI Meter's table.");
            string added = McpClients.WithToml(instructions, "ps.exe", args);
            Assert(added.StartsWith(instructions.TrimEnd('\n') + "\n\n[mcp_servers.ai-meter]\ncommand = \"ps.exe\"\n", StringComparison.Ordinal) && ownLines(added) == 2, "The \"\"\" string changed or the entry was not appended.");
            Assert(McpClients.HasTomlEntry(added) && McpClients.WithoutToml(added) == instructions, "Disconnecting did not restore the file around a \"\"\" string.");

            // Headers inside ''' strings, after \""", across a line-ending backslash and after closing quotes.
            const string kept = "[profiles.fast]\nmodel = \"mini\"\n";
            foreach (var body in new[]
            {
                "notes = '''\n[profiles.fast]\nmodel = \"inside\"\n'''\n",
                "notes = \"\"\"quote \\\"\"\" still open \\\n[profiles.fast]\n\"\"\"\n",
                "notes = \"\"\"ends with two quotes\"\"\"\"\"\nsingle = 'C:\\x [profiles.fast]'\n",
            })
            {
                string text = "[mcp_servers.ai-meter]\ncommand = \"old\"\n" + body + "\n" + kept;
                Assert(McpClients.WithoutToml(text) == kept, "A string ended AI Meter's table early: " + body);
            }

            // Comments are not structure, nor is # or [ inside a string.
            string comments = "# [mcp_servers.ai-meter]\nx = \"a # [b]\" # [mcp_servers.ai-meter]\n  # [mcp_servers.ai-meter]\n";
            Assert(McpClients.WithoutToml(comments) == null && !McpClients.HasTomlEntry(comments), "A commented header counts as AI Meter's table.");
            Assert(McpClients.WithoutToml(McpClients.WithToml(comments, "ps.exe", args)) == comments, "Comments were changed.");

            // Lines that start with [ inside multi-line arrays, nested arrays and inline tables are values.
            string others = "[mcp_servers.other]\ncommand = \"node\"\nargs = [\n  \"[x]\",\n[\"nested\"],\n]\n\n";
            string arrays = others + "[mcp_servers.ai-meter]\ncommand = \"old\"\nargs = [\n[1, 2],\n  [3], # [profiles.fast]\n]\nenv = { A = \"[1]\", B = \"}\" }\n\n" + kept;
            Assert(McpClients.WithoutToml(arrays) == others + kept, "A line in a multi-line array was taken for a header.");
            Assert(ownLines(McpClients.WithToml(arrays, "ps.exe", args)) == 1, "Reconnecting around multi-line arrays duplicated the entry.");

            // An array of tables after AI Meter's table ends it.
            string hooks = "[[hooks.start]]\nname = \"a\"\n[[hooks.start]]\nname = \"b\"\n";
            Assert(McpClients.WithoutToml("[mcp_servers.ai-meter]\ncommand = \"old\"\n" + hooks) == hooks, "An array of tables was removed with AI Meter's table.");

            // Quoted and spaced forms of AI Meter's headers are recognised; similar names and Windows path keys are kept.
            string rest = "[mcp_servers.\"ai-meter-2\"]\ncommand = \"x\"\n[projects.'C:\\Users\\x']\ntrust_level = \"trusted\"\n[projects.\"C:\\\\Users\\\\y\"]\ntrust_level = \"trusted\"\n";
            foreach (var header in new[] { "[\"mcp_servers\".\"ai-meter\"]", "[ mcp_servers . 'ai-meter' ]  # ours", "[mcp_servers.\"ai\\u002Dmeter\"]", "[\"mcp_servers\".ai-meter.env]" })
            {
                string text = "a = 1\n" + header + "\ncommand = \"old\"\n" + rest;
                Assert(McpClients.HasTomlEntry(text) && McpClients.WithoutToml(text) == "a = 1\n" + rest, "A quoted header was not recognised: " + header);
            }
            Assert(McpClients.WithoutToml("a = 1\n" + rest) == null, "A similar name or a path key was taken for AI Meter's table.");
        }

        private static void TomlRefusals()
        {
            var args = McpClients.Arguments(@"C:\x\mcp.ps1");
            foreach (var broken in new[]
            {
                "x = \"\"\"\nnever closed\n[mcp_servers.ai-meter]\ncommand = \"old\"\n",
                "x = '''\nnever closed\n",
                "x = \"never closed\n[profiles.fast]\n",
                "args = [\n  \"a\",\n",
                "x = ]\n",
                "x = \"\"\"too many quotes\"\"\"\"\"\"\n",
                "mcp_servers.ai-meter.command = \"x\"\n",
                "[mcp_servers]\nai-meter = { command = \"x\" }\n",
                "mcp_servers = { other = { command = \"x\" } }\n",
                "[[mcp_servers.ai-meter]]\ncommand = \"x\"\n",
                "[profiles.fast] model = \"mini\"\n",
                "just some words\n",
                "[\"bad \\q escape\"]\n",
            })
            {
                bool refusedAdd = false, refusedRemove = false;
                try { McpClients.WithToml(broken, "ps.exe", args); }
                catch (McpClients.ConfigException) { refusedAdd = true; }
                try { McpClients.WithoutToml(broken); }
                catch (McpClients.ConfigException) { refusedRemove = true; }
                Assert(refusedAdd && refusedRemove, "A file the editor cannot follow was edited: " + broken);
            }

            try
            {
                foreach (var lang in new[] { "it", "es", "fr", "de", "pt" })
                {
                    L.Init(lang);
                    foreach (var key in new[] { McpClients.UnreadableToml, McpClients.UncheckedToml })
                        Assert(L.T(key) != key, "Missing TOML refusal translation for " + lang + ": " + key);
                }
            }
            finally { L.Init("en"); }
        }

        private static void FailedSave()
        {
            string folder = Path.Combine(Path.GetTempPath(), "aimeter-save-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            try
            {
                var codex = new McpClients.Client { Id = "codex", Name = "Codex", Where = "Windows", Format = McpClients.Format.CodexToml, ConfigPath = Path.Combine(folder, "config.toml") };
                const string previous = "model = \"gpt-6\"\r\n";
                File.WriteAllText(codex.ConfigPath, previous);
                bool failed = false;
                // Another program holding the file open for reading only lets AI Meter read it, not write over it.
                using (new FileStream(codex.ConfigPath, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    try { McpClients.Connect(codex, @"C:\x\mcp.ps1"); }
                    catch (IOException) { failed = true; }
                }
                Assert(failed, "Writing over a locked file did not fail.");
                Assert(File.ReadAllText(codex.ConfigPath) == previous && File.ReadAllText(codex.ConfigPath + ".aimeter-backup") == previous, "The previous content was lost.");
                Assert(!File.Exists(codex.ConfigPath + ".aimeter-new"), "The temporary file was left behind.");

                McpClients.Connect(codex, @"C:\x\mcp.ps1");
                Assert(McpClients.IsConnected(codex) && !File.Exists(codex.ConfigPath + ".aimeter-new"), "Connecting after the failure did not work.");
            }
            finally { Directory.Delete(folder, true); }
        }

        private static void AntigravityCli()
        {
            string home = Path.Combine(Path.GetTempPath(), "aimeter-agy-" + Guid.NewGuid().ToString("N"));
            try
            {
                Func<McpClients.Client> find = () => McpClients.Known(home, Path.Combine(home, "AppData"), null).Single(c => c.Id == "antigravity-cli");
                Directory.CreateDirectory(Path.Combine(home, ".gemini"));
                Assert(!find().Installed, "A bare .gemini folder, left by Gemini CLI, counts as Antigravity CLI.");
                Assert(McpClients.Known(home, Path.Combine(home, "AppData"), null).All(c => c.Id != "gemini"), "Gemini CLI is still listed.");

                Directory.CreateDirectory(Path.Combine(home, @".gemini\antigravity-cli"));
                var agy = find();
                Assert(agy.Installed && agy.ConfigPath == Path.Combine(home, @".gemini\config\mcp_config.json"), "Antigravity CLI is not found or edits the wrong file.");

                // agy creates its MCP file empty: it must read as no servers, not as a file AI Meter refuses.
                Directory.CreateDirectory(Path.GetDirectoryName(agy.ConfigPath));
                File.WriteAllText(agy.ConfigPath, "");
                Assert(!McpClients.IsConnected(agy), "An empty file reads as connected.");
                string relay = @"C:\Users\x\AppData\Roaming\AIMeter\mcp.ps1";
                McpClients.Connect(agy, relay);
                var entry = Json.Obj(Json.Obj(Json.Parse(File.ReadAllText(agy.ConfigPath)), "mcpServers"), "ai-meter");
                Assert(entry != null && Json.Str(entry, "command") == McpClients.PowerShell(false) && !entry.ContainsKey("type"), "Wrong Antigravity CLI entry.");
                Assert(McpClients.IsConnected(agy) && File.ReadAllText(agy.ConfigPath + ".aimeter-backup") == "", "The empty file was not kept as a backup.");

                File.WriteAllText(agy.ConfigPath, "{\"mcpServers\":{\"other\":{\"command\":\"node\",\"args\":[],\"disabled\":true},\"ai-meter\":{\"command\":\"old\",\"args\":[],\"disabled\":false}}}");
                McpClients.Disconnect(agy);
                var servers = Json.Obj(Json.Parse(File.ReadAllText(agy.ConfigPath)), "mcpServers");
                Assert(servers.Keys.Single() == "other" && Json.Obj(servers, "other")["disabled"] is bool, "Disconnecting changed agy's other servers.");
            }
            finally { if (Directory.Exists(home)) Directory.Delete(home, true); }
        }

        private static void Commands()
        {
            // A user name with an apostrophe must survive both shells' single quotes.
            string relay = @"C:\Users\O'Brien\AppData\Roaming\AIMeter\mcp.ps1";
            string flags = "-NoProfile -NonInteractive -ExecutionPolicy Bypass -File ";
            string windows = "'" + McpClients.PowerShell(false) + "'";
            Assert(McpClients.Command("claude-code", relay, false) == "claude mcp add --scope user ai-meter '--' " + windows + " " + flags + @"'C:\Users\O''Brien\AppData\Roaming\AIMeter\mcp.ps1'",
                "The Claude Code command for PowerShell is wrong.");
            Assert(McpClients.Command("codex", relay, true) == "codex mcp add ai-meter -- '" + McpClients.PowerShell(true) + "' " + flags + @"'C:\Users\O'\''Brien\AppData\Roaming\AIMeter\mcp.ps1'",
                "The Codex command for WSL is wrong.");
            Assert(McpClients.Command("antigravity-cli", relay, true) == "agy mcp add ai-meter '" + McpClients.PowerShell(true) + "' -- " + flags + @"'C:\Users\O'\''Brien\AppData\Roaming\AIMeter\mcp.ps1'",
                "Antigravity CLI does not get the command before -- and its flags after.");
            Assert(McpClients.Command("antigravity-cli", relay, false) == "agy mcp add ai-meter " + windows + " '--' " + flags + @"'C:\Users\O''Brien\AppData\Roaming\AIMeter\mcp.ps1'",
                "The Antigravity CLI command for PowerShell is wrong.");
        }
    }
}
