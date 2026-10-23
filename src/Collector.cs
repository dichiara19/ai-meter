using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;

namespace AIMeter
{
    /// <summary>
    /// Raccoglie sessioni e limiti da fonti locali. Regole di efficienza:
    ///  - ogni file viene interpretato solo quando cambia (cache per data di modifica e dimensione);
    ///  - dei log grandi si legge solo la coda;
    ///  - WSL non viene mai avviato: lo si legge solo se la sua VM è già accesa;
    ///  - l'unica chiamata di rete (limiti Claude) ha un intervallo lungo e si sospende se non serve.
    /// </summary>
    internal sealed class Collector
    {
        private readonly Settings S;
        private Dictionary<uint, string> procs = new Dictionary<uint, string>();
        private readonly Dictionary<uint, string> pathByPid = new Dictionary<uint, string>();
        private readonly Dictionary<string, CacheEntry> cache = new Dictionary<string, CacheEntry>(StringComparer.OrdinalIgnoreCase);

        private bool wslUp;
        private DateTime wslCheckedAt = DateTime.MinValue;

        private Usage claudeLive;
        private DateTime claudeFetchedAt = DateTime.MinValue;
        private bool claudeNeedsLogin;
        private string claudePlan;

        private static readonly string Home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        private static readonly string AppData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        private static string UsageCacheFile { get { return Path.Combine(Settings.Folder, "claude-usage.json"); } }

        private sealed class CacheEntry { public DateTime Mtime; public long Length; public object Value; }

        public Collector(Settings settings) { S = settings; LoadPersistedClaudeUsage(); }

        public bool WslUp { get { return wslUp; } }

        /// <summary>Forza la prossima lettura dei limiti Claude (pulsante Aggiorna, login appena fatto).</summary>
        public void InvalidateNetwork() { claudeFetchedAt = DateTime.MinValue; }

        public Snapshot Collect(bool allowNetwork, bool savingPower)
        {
            procs = Native.Processes();
            foreach (var pid in pathByPid.Keys.ToList()) if (!procs.ContainsKey(pid)) pathByPid.Remove(pid);
            RefreshWslState();

            var snap = new Snapshot();
            if (S.ClaudeEnabled) snap.Providers.Add(CollectClaude(allowNetwork, savingPower));
            if (S.OpenAIEnabled) snap.Providers.Add(CollectOpenAI());
            return snap;
        }

        // ================================================================ WSL
        private string WslRoot { get { return @"\\wsl.localhost\" + S.WslDistro; } }
        private string WslHome { get { return WslRoot + @"\home\" + S.WslUser; } }

        private void RefreshWslState()
        {
            bool vm = procs.ContainsValue("vmmemwsl") || procs.ContainsValue("vmmem");
            if (!vm || string.IsNullOrEmpty(S.WslDistro)) { wslUp = false; return; }
            if ((DateTime.Now - wslCheckedAt).TotalSeconds < 120) return;
            wslCheckedAt = DateTime.Now;
            // la VM può essere accesa per un'altra distribuzione: chiediamo quali sono attive, senza avviarne nessuna
            string output = Run("wsl.exe", "-l --running -q", 8000, Encoding.Unicode);
            wslUp = output != null && output.Split('\n').Any(l => l.Trim().Trim('\0').Equals(S.WslDistro, StringComparison.OrdinalIgnoreCase));
            if (wslUp && !Directory.Exists(WslHome)) DetectWslUser();
        }

        /// <summary>L'utente Linux può chiamarsi diversamente da quello di Windows: prende la home che contiene Claude Code o Codex.</summary>
        private void DetectWslUser()
        {
            foreach (var home in SafeDirs(WslRoot + @"\home"))
            {
                if (!Directory.Exists(home + @"\.claude") && !Directory.Exists(home + @"\.codex")) continue;
                S.WslUser = System.IO.Path.GetFileName(home);
                S.Save();
                return;
            }
        }

        // ================================================================ Claude
        private ProviderState CollectClaude(bool allowNetwork, bool savingPower)
        {
            var p = new ProviderState { Id = "claude", Name = "Claude" };

            bool app = false;
            foreach (var kv in procs)
            {
                if (kv.Value != "claude.exe") continue;
                if (PathOf(kv.Key).IndexOf(@"\WindowsApps\Claude_", StringComparison.OrdinalIgnoreCase) >= 0) { app = true; break; }
            }
            if (app && S.ClaudeShowChat)
                p.Sessions.Add(new Session { Kind = "Chat", Title = "App Claude aperta", Status = "app" });
            if (S.ClaudeShowCode) p.Sessions.AddRange(ClaudeCodeSessions());
            if (S.ClaudeShowCowork) p.Sessions.AddRange(CoworkSessions());
            p.Running = app || p.Sessions.Count > 0;

            ClaudeUsage(p, allowNetwork, savingPower);
            return p;
        }

        private static readonly Dictionary<string, string> EntryLabels = new Dictionary<string, string>
        {
            { "claude-desktop", "Code · Desktop" }, { "cli", "Code · CLI" }, { "claude-vscode", "Code · VS Code" },
            { "sdk-ts", "Agent SDK" }, { "sdk-py", "Agent SDK" }, { "local-agent", "Cowork" },
        };

        private IEnumerable<Session> ClaudeCodeSessions()
        {
            var meta = DesktopSessionMeta();
            var folders = new List<KeyValuePair<string, bool>> { new KeyValuePair<string, bool>(Path.Combine(Home, @".claude\sessions"), false) };
            if (wslUp) folders.Add(new KeyValuePair<string, bool>(WslHome + @"\.claude\sessions", true));

            var list = new List<Session>();
            foreach (var f in folders)
            {
                foreach (var file in SafeFiles(f.Key, "*.json"))
                {
                    var d = CachedFields(file, "pid", "sessionId", "entrypoint", "status", "name", "cwd", "updatedAt", "statusUpdatedAt");
                    var pidN = Json.Num(d, "pid");
                    if (pidN == null) continue;
                    uint pid = (uint)pidN.Value;
                    bool alive = f.Value ? Directory.Exists(WslRoot + @"\proc\" + pid) : procs.ContainsKey(pid);
                    if (!alive) continue;

                    Dictionary<string, object> m;
                    meta.TryGetValue(Json.Str(d, "sessionId") ?? "", out m);
                    string entry = Json.Str(d, "entrypoint") ?? "";
                    string kind;
                    if (!EntryLabels.TryGetValue(entry, out kind)) kind = string.IsNullOrEmpty(entry) ? "Code" : entry;
                    if (f.Value && entry == "cli") kind = "Code · CLI (WSL)";
                    string status = Json.Str(d, "status") ?? "idle";

                    list.Add(new Session
                    {
                        Kind = kind,
                        Title = Json.Str(d, "name") ?? Json.Str(m, "title") ?? LastSegment(Json.Str(d, "cwd")) ?? "Sessione",
                        Status = status == "busy" ? "busy" : "idle",
                        Model = Json.Str(m, "model"),
                        Updated = Json.FromUnixMs(Json.Num(d, "updatedAt") ?? Json.Num(d, "statusUpdatedAt")) ?? file.LastWriteTime,
                    });
                }
            }
            return list;
        }

        /// <summary>cliSessionId → metadati (titolo, modello) delle sessioni Code aperte dall'app desktop.</summary>
        private Dictionary<string, Dictionary<string, object>> DesktopSessionMeta()
        {
            var map = new Dictionary<string, Dictionary<string, object>>();
            string root = Path.Combine(AppData, @"Claude\claude-code-sessions");
            DateTime cutoff = DateTime.Now.AddDays(-3);
            foreach (var dir in SafeDirs(root).SelectMany(SafeDirs))
            {
                foreach (var file in SafeFiles(dir, "local_*.json"))
                {
                    if (file.LastWriteTime < cutoff) continue;
                    var d = CachedFields(file, "cliSessionId", "title", "model");
                    string cli = Json.Str(d, "cliSessionId");
                    if (cli != null) map[cli] = d;
                }
            }
            return map;
        }

        private IEnumerable<Session> CoworkSessions()
        {
            string root = Path.Combine(AppData, @"Claude\local-agent-mode-sessions");
            DateTime cutoff = DateTime.Now.AddMinutes(-30);
            var list = new List<Session>();
            foreach (var dir in SafeDirs(root).SelectMany(SafeDirs))
            {
                foreach (var file in SafeFiles(dir, "local_*.json"))
                {
                    if (file.LastWriteTime < cutoff) continue;
                    var d = CachedFields(file, "isArchived", "lastActivityAt", "title", "model");
                    if (d == null || Json.Bool(d, "isArchived")) continue;
                    DateTime last = Json.FromUnixMs(Json.Num(d, "lastActivityAt")) ?? file.LastWriteTime;
                    if (last < cutoff) continue;
                    list.Add(new Session
                    {
                        Kind = "Cowork",
                        Title = Json.Str(d, "title") ?? "Attività Cowork",
                        Status = (DateTime.Now - last).TotalSeconds < 90 ? "busy" : "idle",
                        Model = Json.Str(d, "model"),
                        Updated = last,
                    });
                }
            }
            return list;
        }

        // ---------------------------------------------------------------- limiti Claude
        private void ClaudeUsage(ProviderState p, bool allowNetwork, bool savingPower)
        {
            string creds = ClaudeCredentialsPath();
            double interval = savingPower ? 15 : 5;

            if (S.ClaudeLiveUsage && allowNetwork && creds != null && (DateTime.Now - claudeFetchedAt).TotalMinutes >= interval)
            {
                claudeFetchedAt = DateTime.Now;
                var fresh = FetchClaudeUsage(creds);
                if (fresh != null) { claudeLive = fresh; claudeNeedsLogin = false; PersistClaudeUsage(fresh); }
            }

            if (creds != null && claudePlan == null)
            {
                var oauth = Json.Obj(Json.ParseFile(creds), "claudeAiOauth");
                claudePlan = Json.Str(oauth, "subscriptionType");
            }
            p.Plan = claudePlan;

            Usage fallback = AppCachedClaudeUsage();
            if (claudeLive != null && (fallback == null || claudeLive.At >= fallback.At)) p.Usage = claudeLive;
            else p.Usage = fallback;
            if (p.Usage == claudeLive && claudeLive != null && (DateTime.Now - claudeLive.At).TotalMinutes > interval + 2)
                p.Usage = new Usage { Windows = claudeLive.Windows, Source = "cache", At = claudeLive.At };

            if (!S.ClaudeLiveUsage) return;
            if (creds == null && !wslUp && WslHasClaude == true)
                p.Notice = "WSL è spento: limiti all'ultimo aggiornamento";
            else if (creds == null || claudeNeedsLogin)
            {
                p.NeedsLogin = true;
                p.Notice = "Collega l'account per i limiti in tempo reale";
            }
        }

        private bool? WslHasClaude;

        private string ClaudeCredentialsPath()
        {
            string win = Path.Combine(Home, @".claude\.credentials.json");
            if (File.Exists(win)) return win;
            if (wslUp)
            {
                string wsl = WslHome + @"\.claude\.credentials.json";
                WslHasClaude = File.Exists(wsl);
                if (WslHasClaude == true) return wsl;
            }
            return null;
        }

        private Usage FetchClaudeUsage(string credsPath)
        {
            var oauth = Json.Obj(Json.ParseFile(credsPath), "claudeAiOauth");
            DateTime? exp = Json.FromUnixMs(Json.Num(oauth, "expiresAt"));
            if (exp == null || exp.Value < DateTime.Now.AddMinutes(2))
            {
                // il token lo rinnova la CLI stessa: non tocchiamo mai il refresh token
                RefreshClaudeLogin(credsPath);
                oauth = Json.Obj(Json.ParseFile(credsPath), "claudeAiOauth");
                exp = Json.FromUnixMs(Json.Num(oauth, "expiresAt"));
                if (exp == null || exp.Value < DateTime.Now) { claudeNeedsLogin = true; return null; }
            }
            string token = Json.Str(oauth, "accessToken");
            if (string.IsNullOrEmpty(token)) { claudeNeedsLogin = true; return null; }

            try
            {
                var req = (HttpWebRequest)WebRequest.Create("https://api.anthropic.com/api/oauth/usage");
                req.Headers["Authorization"] = "Bearer " + token;
                req.Headers["anthropic-beta"] = "oauth-2025-04-20";
                req.UserAgent = "ai-meter/2.0";
                req.Timeout = 15000;
                req.ReadWriteTimeout = 15000;
                using (var resp = (HttpWebResponse)req.GetResponse())
                using (var r = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                    return ParseClaudeUsage(Json.Parse(r.ReadToEnd()));
            }
            catch (WebException e)
            {
                var hr = e.Response as HttpWebResponse;
                if (hr != null && (hr.StatusCode == HttpStatusCode.Unauthorized || hr.StatusCode == HttpStatusCode.Forbidden))
                    claudeNeedsLogin = true;
                return null;
            }
            catch (IOException) { return null; }
        }

        private static Usage ParseClaudeUsage(Dictionary<string, object> d)
        {
            if (d == null) return null;
            var u = new Usage { Source = "live", At = DateTime.Now };
            AddClaudeWindow(u, d, "five_hour", "5 ore", "5h");
            AddClaudeWindow(u, d, "seven_day", "Settimana", "7g");
            AddClaudeWindow(u, d, "seven_day_opus", "Settimana · Opus", "Op");
            AddClaudeWindow(u, d, "seven_day_sonnet", "Settimana · Sonnet", "So");
            return u.Windows.Count > 0 ? u : null;
        }

        private static void AddClaudeWindow(Usage u, Dictionary<string, object> d, string key, string label, string shortLabel)
        {
            var w = Json.Obj(d, key);
            double? used = Json.Num(w, "utilization");
            if (used == null) return;
            u.Windows.Add(new UsageWindow { Label = label, Short = shortLabel, Used = used.Value, ResetsAt = Json.FromIso(Json.Str(w, "resets_at")) });
        }

        private void RefreshClaudeLogin(string credsPath)
        {
            if (credsPath.StartsWith(@"\\wsl", StringComparison.OrdinalIgnoreCase))
                Run("wsl.exe", "-d " + S.WslDistro + " -- bash -lc \"claude auth status >/dev/null 2>&1\"", 40000, null);
            else
            {
                string exe = ClaudeCliPath();
                if (exe != null) Run(exe, "auth status", 40000, null);
            }
        }

        /// <summary>La CLI di Claude Code installata con l'app desktop (la versione più recente).</summary>
        public static string ClaudeCliPath()
        {
            string root = Path.Combine(AppData, @"Claude\claude-code");
            try
            {
                return Directory.Exists(root)
                    ? Directory.GetFiles(root, "claude.exe", SearchOption.AllDirectories).OrderByDescending(File.GetLastWriteTime).FirstOrDefault()
                    : null;
            }
            catch (IOException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
        }

        /// <summary>Ultimo campione salvato dall'app Claude (aggiornato dall'app, non da noi).</summary>
        private Usage AppCachedClaudeUsage()
        {
            string file = Path.Combine(AppData, @"Claude\plan-usage-history.json");
            var fi = new FileInfo(file);
            if (!fi.Exists) return null;
            return Cached(file, fi, () =>
            {
                var d = Json.ParseFile(file);
                var samples = d == null ? null : d["samples"] as object[];
                if (samples == null || samples.Length == 0) return null;
                var last = samples[samples.Length - 1] as Dictionary<string, object>;
                var u = Json.Obj(last, "u");
                var usage = new Usage { Source = "cache", At = Json.FromUnixMs(Json.Num(last, "t")) ?? fi.LastWriteTime };
                if (Json.Num(u, "fh") != null) usage.Windows.Add(new UsageWindow { Label = "5 ore", Short = "5h", Used = Json.Num(u, "fh").Value });
                if (Json.Num(u, "sd") != null) usage.Windows.Add(new UsageWindow { Label = "Settimana", Short = "7g", Used = Json.Num(u, "sd").Value });
                return usage;
            }) as Usage;
        }

        private void PersistClaudeUsage(Usage u)
        {
            try
            {
                Directory.CreateDirectory(Settings.Folder);
                var rows = u.Windows.Select(w => new Dictionary<string, object>
                {
                    { "label", w.Label }, { "short", w.Short }, { "used", w.Used },
                    { "resets", w.ResetsAt.HasValue ? w.ResetsAt.Value.ToUniversalTime().ToString("o") : null },
                }).ToList();
                var doc = new Dictionary<string, object> { { "at", u.At.ToUniversalTime().ToString("o") }, { "plan", claudePlan }, { "windows", rows } };
                File.WriteAllText(UsageCacheFile, Json.Serialize(doc), new UTF8Encoding(false));
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        private void LoadPersistedClaudeUsage()
        {
            var d = Json.ParseFile(UsageCacheFile);
            if (d == null) return;
            var u = new Usage { Source = "cache", At = Json.FromIso(Json.Str(d, "at")) ?? DateTime.MinValue };
            claudePlan = Json.Str(d, "plan");
            var rows = d.ContainsKey("windows") ? d["windows"] as object[] : null;
            if (rows == null) return;
            foreach (Dictionary<string, object> r in rows.OfType<Dictionary<string, object>>())
                u.Windows.Add(new UsageWindow { Label = Json.Str(r, "label"), Short = Json.Str(r, "short"), Used = Json.Num(r, "used") ?? 0, ResetsAt = Json.FromIso(Json.Str(r, "resets")) });
            if (u.Windows.Count > 0) claudeLive = u;
        }

        // ================================================================ ChatGPT · Codex
        private static readonly Dictionary<string, string> OriginLabels = new Dictionary<string, string>
        {
            { "Codex Desktop", "Codex · App" }, { "codex-tui", "Codex · CLI" }, { "codex_exec", "Codex · exec" }, { "codex_vscode", "Codex · VS Code" },
        };

        private ProviderState CollectOpenAI()
        {
            var p = new ProviderState { Id = "openai", Name = "ChatGPT" };
            bool app = procs.ContainsValue("chatgpt.exe");
            if (app && S.OpenAIShowChat)
                p.Sessions.Add(new Session { Kind = "Chat", Title = "App ChatGPT aperta", Status = "app" });

            var rollouts = CodexRollouts(14);
            var names = CodexThreadNames();
            DateTime cutoff = DateTime.Now.AddMinutes(-30);

            foreach (var file in rollouts.Where(f => f.LastWriteTime >= cutoff))
            {
                var head = CodexHead(file);
                string origin = Json.Str(head, "originator") ?? "";
                string kind;
                if (!OriginLabels.TryGetValue(origin, out kind)) kind = "Codex";
                string id = Json.Str(head, "id") ?? "";
                string title;
                if (!names.TryGetValue(id, out title) || string.IsNullOrEmpty(title)) title = LastSegment(Json.Str(head, "cwd")) ?? "Thread";
                p.Sessions.Add(new Session
                {
                    Kind = kind,
                    Title = title,
                    Status = (DateTime.Now - file.LastWriteTime).TotalSeconds < 90 ? "busy" : "idle",
                    Model = CodexModel(file),
                    Updated = file.LastWriteTime,
                });
            }
            p.Running = app || procs.ContainsValue("codex.exe") || p.Sessions.Count > 0;

            foreach (var file in rollouts.Take(4))
            {
                var rl = CodexRateLimits(file);
                if (rl == null) continue;
                p.Usage = rl.Item1;
                p.Plan = rl.Item2;
                break;
            }
            return p;
        }

        private IEnumerable<string> CodexHomes()
        {
            yield return Path.Combine(Home, ".codex");
            if (wslUp && Directory.Exists(WslHome + @"\.codex")) yield return WslHome + @"\.codex";
        }

        /// <summary>I log degli ultimi giorni, dal più recente. Solo enumerazione di cartelle: niente letture.</summary>
        private List<FileInfo> CodexRollouts(int days)
        {
            var files = new List<FileInfo>();
            foreach (var home in CodexHomes())
            {
                for (int i = 0; i < days; i++)
                {
                    DateTime d = DateTime.Now.AddDays(-i);
                    string folder = Path.Combine(home, "sessions", d.ToString("yyyy"), d.ToString("MM"), d.ToString("dd"));
                    files.AddRange(SafeFiles(folder, "rollout-*.jsonl"));
                }
            }
            files.Sort((a, b) => b.LastWriteTime.CompareTo(a.LastWriteTime));
            return files;
        }

        private Dictionary<string, object> CodexHead(FileInfo file)
        {
            string key = "head:" + file.FullName;
            CacheEntry e;
            if (cache.TryGetValue(key, out e)) return (Dictionary<string, object>)e.Value;
            var head = Keep(Json.Obj(Json.Parse(ReadFirstLine(file.FullName, 2 << 20) ?? ""), "payload"), "id", "originator", "cwd", "timestamp");
            cache[key] = new CacheEntry { Value = head };
            return head;
        }

        private string CodexModel(FileInfo file)
        {
            return Cached("model:" + file.FullName, file, () =>
            {
                string line = LastLineContaining(file.FullName, "\"turn_context\"", 1 << 20);
                return Json.Str(Json.Obj(Json.Parse(line ?? ""), "payload"), "model");
            }) as string;
        }

        private Tuple<Usage, string> CodexRateLimits(FileInfo file)
        {
            return Cached("rl:" + file.FullName, file, () =>
            {
                string line = LastLineContaining(file.FullName, "\"rate_limits\"", 512 << 10)
                              ?? LastLineContaining(file.FullName, "\"rate_limits\"", 2 << 20);
                var d = Json.Parse(line ?? "");
                var rl = Json.Obj(Json.Obj(d, "payload"), "rate_limits");
                if (rl == null) return null;
                var u = new Usage { Source = "log", At = Json.FromIso(Json.Str(d, "timestamp")) ?? file.LastWriteTime };
                foreach (var key in new[] { "primary", "secondary" })
                {
                    var w = Json.Obj(rl, key);
                    if (w == null) continue;
                    int mins = (int)(Json.Num(w, "window_minutes") ?? 0);
                    string label = mins == 300 ? "5 ore" : mins == 10080 ? "Settimana" : (mins / 60) + " ore";
                    string sh = mins == 300 ? "5h" : mins == 10080 ? "7g" : (mins / 60) + "h";
                    u.Windows.Add(new UsageWindow { Label = label, Short = sh, Used = Json.Num(w, "used_percent") ?? 0, ResetsAt = Json.FromUnix(Json.Num(w, "resets_at")) });
                }
                return Tuple.Create(u, Json.Str(rl, "plan_type"));
            }) as Tuple<Usage, string>;
        }

        private Dictionary<string, string> CodexThreadNames()
        {
            var all = new Dictionary<string, string>();
            foreach (var home in CodexHomes())
            {
                var fi = new FileInfo(Path.Combine(home, "session_index.jsonl"));
                if (!fi.Exists) continue;
                var names = Cached("idx:" + fi.FullName, fi, () =>
                {
                    var map = new Dictionary<string, string>();
                    foreach (var line in TailText(fi.FullName, 1 << 20).Split('\n'))
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

        // ================================================================ utilità
        private string PathOf(uint pid)
        {
            string p;
            if (!pathByPid.TryGetValue(pid, out p)) { p = Native.ProcessPath(pid); pathByPid[pid] = p; }
            return p;
        }

        private object Cached(string key, FileInfo fi, Func<object> load)
        {
            CacheEntry e;
            if (cache.TryGetValue(key, out e) && e.Mtime == fi.LastWriteTimeUtc && e.Length == fi.Length) return e.Value;
            object v = load();
            cache[key] = new CacheEntry { Mtime = fi.LastWriteTimeUtc, Length = fi.Length, Value = v };
            return v;
        }

        /// <summary>Legge un JSON quando cambia e ne conserva solo i campi indicati (mai il documento intero).</summary>
        private Dictionary<string, object> CachedFields(FileInfo fi, params string[] keys)
        {
            return Cached("json:" + fi.FullName, fi, () => Keep(Json.ParseFile(fi.FullName), keys)) as Dictionary<string, object>;
        }

        private static Dictionary<string, object> Keep(Dictionary<string, object> d, params string[] keys)
        {
            if (d == null) return null;
            var small = new Dictionary<string, object>(keys.Length);
            object v;
            foreach (var k in keys) if (d.TryGetValue(k, out v) && !(v is Dictionary<string, object>) && !(v is object[])) small[k] = v;
            return small;
        }

        /// <summary>Svuota la cache delle voci per file non più esistenti, una volta ogni tanto.</summary>
        public void Trim()
        {
            foreach (var k in cache.Keys.ToList())
            {
                int colon = k.IndexOf(':');
                string path = colon >= 0 ? k.Substring(colon + 1) : k;
                if (!path.StartsWith(@"\\") && !File.Exists(path)) cache.Remove(k);
            }
        }

        private static IEnumerable<FileInfo> SafeFiles(string dir, string pattern)
        {
            try { return Directory.Exists(dir) ? new DirectoryInfo(dir).GetFiles(pattern) : new FileInfo[0]; }
            catch (IOException) { return new FileInfo[0]; }
            catch (UnauthorizedAccessException) { return new FileInfo[0]; }
        }

        private static IEnumerable<string> SafeDirs(string dir)
        {
            try { return Directory.Exists(dir) ? Directory.GetDirectories(dir) : new string[0]; }
            catch (IOException) { return new string[0]; }
            catch (UnauthorizedAccessException) { return new string[0]; }
        }

        private static string LastSegment(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            var parts = path.TrimEnd('/', '\\').Split('/', '\\');
            return parts.Length == 0 ? null : parts[parts.Length - 1];
        }

        private static FileStream OpenShared(string path)
        {
            return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.SequentialScan);
        }

        private static string TailText(string path, int maxBytes)
        {
            try
            {
                using (var fs = OpenShared(path))
                {
                    long start = Math.Max(0, fs.Length - maxBytes);
                    fs.Seek(start, SeekOrigin.Begin);
                    var buf = new byte[fs.Length - start];
                    int read = 0, n;
                    while (read < buf.Length && (n = fs.Read(buf, read, buf.Length - read)) > 0) read += n;
                    Json.BytesRead += read;
                    string text = Encoding.UTF8.GetString(buf, 0, read);
                    if (start > 0) { int nl = text.IndexOf('\n'); text = nl >= 0 ? text.Substring(nl + 1) : ""; }
                    return text;
                }
            }
            catch (IOException) { return ""; }
            catch (UnauthorizedAccessException) { return ""; }
        }

        private static string LastLineContaining(string path, string marker, int maxBytes)
        {
            string text = TailText(path, maxBytes);
            int i = text.LastIndexOf(marker, StringComparison.Ordinal);
            if (i < 0) return null;
            int s = text.LastIndexOf('\n', i) + 1;
            int e = text.IndexOf('\n', i);
            return e < 0 ? text.Substring(s) : text.Substring(s, e - s);
        }

        private static string ReadFirstLine(string path, int maxBytes)
        {
            try
            {
                using (var fs = OpenShared(path))
                {
                    var ms = new MemoryStream();
                    int b;
                    while (ms.Length < maxBytes && (b = fs.ReadByte()) >= 0 && b != '\n') ms.WriteByte((byte)b);
                    return Encoding.UTF8.GetString(ms.ToArray());
                }
            }
            catch (IOException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
        }

        private static string Run(string exe, string args, int timeoutMs, Encoding outputEncoding)
        {
            try
            {
                var psi = new ProcessStartInfo(exe, args)
                {
                    UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
                    RedirectStandardOutput = true, RedirectStandardError = true,
                };
                if (outputEncoding != null) psi.StandardOutputEncoding = outputEncoding;
                using (var p = Process.Start(psi))
                {
                    var outTask = p.StandardOutput.ReadToEndAsync();
                    p.StandardError.ReadToEndAsync();
                    if (!p.WaitForExit(timeoutMs)) { try { p.Kill(); } catch (InvalidOperationException) { } return null; }
                    return outTask.Result;
                }
            }
            catch (System.ComponentModel.Win32Exception) { return null; }
            catch (InvalidOperationException) { return null; }
        }
    }
}
