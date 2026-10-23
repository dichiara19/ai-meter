using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AIMeter
{
    /// <summary>
    /// ChatGPT / Codex: thread Codex attivi dai log locali, app ChatGPT aperta, limiti in tempo reale chiedendoli
    /// al programma codex installato (JSON-RPC "app-server", tecnica di CodexBar, MIT). È Codex a usare il proprio
    /// accesso: noi non leggiamo né rinnoviamo token. Se codex non risponde, si ripiega sull'ultimo dato nei log.
    /// </summary>
    internal sealed class CodexProvider : Provider
    {
        private readonly Settings S;
        private Usage live;
        private string livePlan;
        private DateTime fetchedAt = DateTime.MinValue;

        public CodexProvider(Settings s) { S = s; }

        public override string Id { get { return "openai"; } }
        public override bool Enabled(Settings s) { return s.OpenAIEnabled; }
        public override void InvalidateNetwork() { fetchedAt = DateTime.MinValue; }

        private static readonly Dictionary<string, string> OriginLabels = new Dictionary<string, string>
        {
            { "Codex Desktop", "Codex · App" }, { "codex-tui", "Codex · CLI" }, { "codex_exec", "Codex · exec" }, { "codex_vscode", "Codex · VS Code" },
        };

        public override ProviderState Collect(Probe p)
        {
            var st = new ProviderState { Id = "openai", Name = "ChatGPT" };
            bool app = p.IsRunning("chatgpt.exe");
            if (app && S.OpenAIShowChat)
                st.Sessions.Add(new Session { Kind = "Chat", Title = "App ChatGPT aperta", Status = "app" });

            var rollouts = Rollouts(p, 14);
            var names = ThreadNames(p);
            DateTime cutoff = DateTime.Now.AddMinutes(-30);
            foreach (var file in rollouts.Where(f => f.LastWriteTime >= cutoff))
            {
                var head = Head(p, file);
                string kind;
                if (!OriginLabels.TryGetValue(Json.Str(head, "originator") ?? "", out kind)) kind = "Codex";
                string title;
                if (!names.TryGetValue(Json.Str(head, "id") ?? "", out title) || string.IsNullOrEmpty(title))
                    title = Probe.LastSegment(Json.Str(head, "cwd")) ?? "Thread";
                st.Sessions.Add(new Session
                {
                    Kind = kind, Title = title,
                    Status = (DateTime.Now - file.LastWriteTime).TotalSeconds < 90 ? "busy" : "idle",
                    Model = Model(p, file), Updated = file.LastWriteTime,
                });
            }
            st.Running = app || p.IsRunning("codex.exe") || st.Sessions.Count > 0;

            // limiti: in tempo reale da codex, altrimenti l'ultimo evento nei log
            double interval = p.SavingPower ? 15 : 5;
            if (p.AllowNetwork && (DateTime.Now - fetchedAt).TotalMinutes >= interval)
            {
                fetchedAt = DateTime.Now;
                string plan;
                var fresh = FetchLive(out plan);
                if (fresh != null) { live = fresh; livePlan = plan; }
            }
            Tuple<Usage, string> logged = null;
            foreach (var file in rollouts.Take(4)) { logged = RateLimitsFromLog(p, file); if (logged != null) break; }

            if (live != null && (logged == null || live.At >= logged.Item1.At))
            {
                st.Usage = (DateTime.Now - live.At).TotalMinutes > interval + 2 ? live.AsCache() : live;
                st.Plan = livePlan;
            }
            else if (logged != null) { st.Usage = logged.Item1; st.Plan = logged.Item2; }
            return st;
        }

        // ================================================================ tempo reale (codex app-server)
        /// <summary>L'eseguibile codex: quello dell'app ChatGPT/Codex, altrimenti quello nel PATH.</summary>
        public static string CodexPath()
        {
            try
            {
                string bin = Path.Combine(Probe.LocalAppData, @"OpenAI\Codex\bin");
                if (Directory.Exists(bin))
                {
                    var f = Directory.GetFiles(bin, "codex.exe", SearchOption.AllDirectories).OrderByDescending(File.GetLastWriteTime).FirstOrDefault();
                    if (f != null) return f;
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';'))
            {
                try
                {
                    string f = Path.Combine(dir.Trim(), "codex.exe");
                    if (File.Exists(f)) return f;
                }
                catch (ArgumentException) { }
            }
            return null;
        }

        private Usage FetchLive(out string plan)
        {
            plan = null;
            string exe = CodexPath();
            if (exe == null || !File.Exists(Path.Combine(Probe.Home, @".codex\auth.json"))) return null;

            Process proc = null;
            try
            {
                var psi = new ProcessStartInfo(exe, "-s read-only -a never app-server")
                {
                    UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
                    RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                    StandardOutputEncoding = Encoding.UTF8,
                };
                proc = Process.Start(psi);
                proc.StandardError.ReadToEndAsync();
                var input = new StreamWriter(proc.StandardInput.BaseStream, new UTF8Encoding(false)) { NewLine = "\n", AutoFlush = true };
                // .NET Framework apre lo stdin con un BOM UTF-8: una riga vuota lo isola, così codex legge pulito il resto
                input.WriteLine();
                input.WriteLine("{\"id\":1,\"method\":\"initialize\",\"params\":{\"clientInfo\":{\"name\":\"ai-meter\",\"version\":\"" + Controller.Version + "\"}}}");
                if (ReadResponse(proc, 1, 10000) == null) return null;
                input.WriteLine("{\"method\":\"initialized\",\"params\":{}}");
                input.WriteLine("{\"id\":2,\"method\":\"account/rateLimits/read\",\"params\":{}}");
                var result = Json.Obj(ReadResponse(proc, 2, 10000), "result");
                input.Close();
                return Parse(result, out plan);
            }
            catch (System.ComponentModel.Win32Exception) { return null; }
            catch (InvalidOperationException) { return null; }
            catch (IOException) { return null; }
            finally
            {
                if (proc != null)
                {
                    try { if (!proc.WaitForExit(2000)) proc.Kill(); }
                    catch (InvalidOperationException) { }
                    proc.Dispose();
                }
            }
        }

        /// <summary>Legge righe JSON finché arriva la risposta con l'id atteso (le notifiche senza id si saltano).</summary>
        private static Dictionary<string, object> ReadResponse(Process proc, int id, int timeoutMs)
        {
            var deadline = DateTime.Now.AddMilliseconds(timeoutMs);
            while (DateTime.Now < deadline)
            {
                Task<string> t = proc.StandardOutput.ReadLineAsync();
                int left = (int)Math.Max(1, (deadline - DateTime.Now).TotalMilliseconds);
                if (!t.Wait(left) || t.Result == null) return null;
                var j = Json.Parse(t.Result);
                if (Json.Num(j, "id") == id) return j;
            }
            return null;
        }

        private static Usage Parse(Dictionary<string, object> r, out string plan)
        {
            plan = null;
            var rl = Json.Obj(r, "rateLimits");
            if (rl == null) return null;
            plan = Json.Str(rl, "planType");
            var u = new Usage { Source = "live", At = DateTime.Now };
            AddWindows(u, rl, null);

            // limiti aggiuntivi (es. modelli con quota propria), oltre a quello principale "codex"
            var byId = Json.Obj(r, "rateLimitsByLimitId");
            if (byId != null)
                foreach (var kv in byId)
                {
                    var other = kv.Value as Dictionary<string, object>;
                    if (other == null || kv.Key == (Json.Str(rl, "limitId") ?? "codex")) continue;
                    AddWindows(u, other, Json.Str(other, "limitName") ?? kv.Key);
                }

            var notes = new List<string>();
            var credits = Json.Obj(rl, "credits");
            if (credits != null && Json.Bool(credits, "unlimited")) notes.Add("Crediti Codex: illimitati");
            else if (credits != null && Json.Bool(credits, "hasCredits")) notes.Add("Crediti Codex: " + (Json.Str(credits, "balance") ?? "0"));

            var resets = Json.Obj(r, "rateLimitResetCredits");
            var list = resets != null && resets.ContainsKey("credits") ? resets["credits"] as object[] : null;
            if (list != null)
            {
                var available = list.OfType<Dictionary<string, object>>().Where(c => Json.Str(c, "status") == "available").ToList();
                if (available.Count > 0)
                {
                    DateTime? exp = available.Select(c => Json.FromUnix(Json.Num(c, "expiresAt"))).Where(x => x.HasValue).OrderBy(x => x).FirstOrDefault();
                    notes.Add((available.Count == 1 ? "1 reset gratuito dei limiti disponibile" : available.Count + " reset gratuiti dei limiti disponibili")
                              + (exp.HasValue ? ", scade il " + exp.Value.ToString("d MMM", new CultureInfo("it-IT")) : ""));
                }
            }
            if (notes.Count > 0) u.CreditsNote = string.Join(" · ", notes);
            return u.Windows.Count > 0 ? u : null;
        }

        private static void AddWindows(Usage u, Dictionary<string, object> rl, string prefix)
        {
            foreach (var key in new[] { "primary", "secondary" })
            {
                var w = Json.Obj(rl, key);
                if (w == null) continue;
                int mins = (int)(Json.Num(w, "windowDurationMins") ?? Json.Num(w, "window_minutes") ?? 0);
                double used = Json.Num(w, "usedPercent") ?? Json.Num(w, "used_percent") ?? 0;
                DateTime? resets = Json.FromUnix(Json.Num(w, "resetsAt") ?? Json.Num(w, "resets_at"));
                u.Windows.Add(Window(prefix, mins, used, resets));
            }
        }

        private static UsageWindow Window(string prefix, int mins, double used, DateTime? resets)
        {
            string label = mins == 300 ? "Sessione 5 h" : mins == 10080 ? "Settimana" : mins >= 60 ? (mins / 60) + " ore" : mins + " min";
            string sh = prefix != null ? "m" : mins == 300 ? "5h" : mins == 10080 ? "7g" : (mins / 60) + "h";
            return new UsageWindow
            {
                Label = prefix != null ? prefix + " · " + label : label, Short = sh, Used = used, ResetsAt = resets,
                StartedAt = resets.HasValue && mins > 0 ? resets.Value.AddMinutes(-mins) : (DateTime?)null,
            };
        }

        // ================================================================ log locali
        private static IEnumerable<string> Homes(Probe p)
        {
            yield return Path.Combine(Probe.Home, ".codex");
            if (p.WslUp && Directory.Exists(p.WslHome + @"\.codex")) yield return p.WslHome + @"\.codex";
        }

        /// <summary>I log degli ultimi giorni, dal più recente. Solo enumerazione di cartelle: niente letture.</summary>
        private static List<FileInfo> Rollouts(Probe p, int days)
        {
            var files = new List<FileInfo>();
            foreach (var home in Homes(p))
                for (int i = 0; i < days; i++)
                {
                    DateTime d = DateTime.Now.AddDays(-i);
                    files.AddRange(Probe.SafeFiles(Path.Combine(home, "sessions", d.ToString("yyyy"), d.ToString("MM"), d.ToString("dd")), "rollout-*.jsonl"));
                }
            files.Sort((a, b) => b.LastWriteTime.CompareTo(a.LastWriteTime));
            return files;
        }

        private static Dictionary<string, object> Head(Probe p, FileInfo file)
        {
            return p.Once("head:" + file.FullName, () =>
                Probe.Keep(Json.Obj(Json.Parse(Probe.ReadFirstLine(file.FullName, 2 << 20) ?? ""), "payload"), "id", "originator", "cwd", "timestamp"));
        }

        private static string Model(Probe p, FileInfo file)
        {
            return p.Cached("model:" + file.FullName, file, () =>
                Json.Str(Json.Obj(Json.Parse(Probe.LastLineContaining(file.FullName, "\"turn_context\"", 1 << 20) ?? ""), "payload"), "model")) as string;
        }

        private static Tuple<Usage, string> RateLimitsFromLog(Probe p, FileInfo file)
        {
            return p.Cached("rl:" + file.FullName, file, () =>
            {
                string line = Probe.LastLineContaining(file.FullName, "\"rate_limits\"", 512 << 10)
                              ?? Probe.LastLineContaining(file.FullName, "\"rate_limits\"", 2 << 20);
                var d = Json.Parse(line ?? "");
                var rl = Json.Obj(Json.Obj(d, "payload"), "rate_limits");
                if (rl == null) return null;
                var u = new Usage { Source = "cache", At = Json.FromIso(Json.Str(d, "timestamp")) ?? file.LastWriteTime };
                AddWindows(u, rl, null);
                return Tuple.Create(u, Json.Str(rl, "plan_type"));
            }) as Tuple<Usage, string>;
        }

        private static Dictionary<string, string> ThreadNames(Probe p)
        {
            var all = new Dictionary<string, string>();
            foreach (var home in Homes(p))
            {
                var fi = new FileInfo(Path.Combine(home, "session_index.jsonl"));
                if (!fi.Exists) continue;
                var names = p.Cached("idx:" + fi.FullName, fi, () =>
                {
                    var map = new Dictionary<string, string>();
                    foreach (var line in Probe.TailText(fi.FullName, 1 << 20).Split('\n'))
                    {
                        var j = Json.Parse(line);
                        string id = Json.Str(j, "id");
                        if (id != null) map[id] = Json.Str(j, "thread_name");
                    }
                    return map;
                }) as Dictionary<string, string>;
                if (names != null) foreach (var kv in names) all[kv.Key] = kv.Value;
            }
            return all;
        }
    }
}
