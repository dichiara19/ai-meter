using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;

namespace AIMeter
{
    /// <summary>
    /// Claude Code and Cowork sessions on Windows and running WSL, app presence and usage API limits.
    /// Claude Code manages sign-in and token renewal; this provider never touches the refresh token.
    /// API: GET /api/oauth/usage with anthropic-beta oauth-2025-04-20.
    /// </summary>
    internal sealed class ClaudeProvider : Provider
    {
        private readonly Settings S;
        private Usage live;
        private DateTime fetchedAt = DateTime.MinValue;
        private bool needsLogin;
        private string plan;
        private bool? wslHasClaude;

        private static string PlanFile { get { return Path.Combine(Settings.Folder, "claude-usage.json"); } }
        private static string RawFile { get { return Path.Combine(Settings.Folder, "claude-usage-raw.json"); } }

        public ClaudeProvider(Settings s) { S = s; LoadPersisted(); }

        public override string Id { get { return "claude"; } }

        /// <summary>Whether Claude Code or the Claude app is installed on this PC.</summary>
        public static bool Detected()
        {
            return Directory.Exists(Path.Combine(Probe.Home, ".claude")) || Directory.Exists(Path.Combine(Probe.AppData, "Claude"));
        }
        public override bool Enabled(Settings s) { return s.ClaudeEnabled; }
        public override void InvalidateNetwork() { fetchedAt = DateTime.MinValue; }

        public override ProviderState Collect(Probe p)
        {
            var st = new ProviderState { Id = "claude", Name = "Claude", CanSignIn = true };
            bool app = false;
            foreach (var kv in p.Procs)
            {
                if (kv.Value != "claude.exe") continue;
                if (p.PathOf(kv.Key).IndexOf(@"\WindowsApps\Claude_", StringComparison.OrdinalIgnoreCase) >= 0) { app = true; break; }
            }
            if (app && S.ClaudeShowChat)
                st.Sessions.Add(new Session { Kind = "Chat", Title = "Claude app open", Status = "app" });
            if (S.ClaudeShowCode) st.Sessions.AddRange(CodeSessions(p));
            if (S.ClaudeShowCowork) st.Sessions.AddRange(CoworkSessions(p));
            st.Running = app || st.Sessions.Count > 0;
            Limits(p, st);
            return st;
        }

        // Session discovery.
        private static readonly Dictionary<string, string> EntryLabels = new Dictionary<string, string>
        {
            { "claude-desktop", "Code · Desktop" }, { "cli", "Code · CLI" }, { "claude-vscode", "Code · VS Code" },
            { "sdk-ts", "Agent SDK" }, { "sdk-py", "Agent SDK" }, { "local-agent", "Cowork" },
        };

        private IEnumerable<Session> CodeSessions(Probe p)
        {
            var meta = DesktopSessionMeta(p);
            var folders = new List<KeyValuePair<string, bool>> { new KeyValuePair<string, bool>(Path.Combine(Probe.Home, @".claude\sessions"), false) };
            if (p.WslUp) folders.Add(new KeyValuePair<string, bool>(p.WslHome + @"\.claude\sessions", true));

            var list = new List<Session>();
            HashSet<uint> wslPids = null;
            foreach (var f in folders)
            {
                foreach (var file in Probe.SafeFiles(f.Key, "*.json"))
                {
                    var d = p.CachedFields(file, "pid", "sessionId", "entrypoint", "status", "name", "cwd", "updatedAt", "statusUpdatedAt");
                    var pidN = Json.Num(d, "pid");
                    if (pidN == null) continue;
                    uint pid = (uint)pidN.Value;
                    // Read WSL process IDs once per collection, avoiding a share request for every session file.
                    if (f.Value && wslPids == null) wslPids = WslPids(p);
                    bool alive = f.Value ? wslPids.Contains(pid) : p.Procs.ContainsKey(pid);
                    if (!alive) continue;

                    Dictionary<string, object> m;
                    meta.TryGetValue(Json.Str(d, "sessionId") ?? "", out m);
                    string entry = Json.Str(d, "entrypoint") ?? "";
                    string kind;
                    if (!EntryLabels.TryGetValue(entry, out kind)) kind = string.IsNullOrEmpty(entry) ? "Code" : entry;
                    if (f.Value && entry == "cli") kind = "Code · CLI (WSL)";

                    list.Add(new Session
                    {
                        Kind = kind,
                        Title = Json.Str(d, "name") ?? Json.Str(m, "title") ?? Probe.LastSegment(Json.Str(d, "cwd")) ?? "Session",
                        Status = (Json.Str(d, "status") ?? "idle") == "busy" ? "busy" : "idle",
                        Model = Json.Str(m, "model"),
                        Updated = Json.FromUnixMs(Json.Num(d, "updatedAt") ?? Json.Num(d, "statusUpdatedAt")) ?? file.LastWriteTime,
                    });
                }
            }
            return list;
        }

        private static HashSet<uint> WslPids(Probe p)
        {
            var set = new HashSet<uint>();
            foreach (var d in Probe.SafeDirs(p.WslRoot + @"\proc"))
            {
                uint pid;
                if (uint.TryParse(Path.GetFileName(d), out pid)) set.Add(pid);
            }
            return set;
        }

        /// <summary>Map cliSessionId to the title and model of Code sessions opened by the desktop app.</summary>
        private static Dictionary<string, Dictionary<string, object>> DesktopSessionMeta(Probe p)
        {
            var map = new Dictionary<string, Dictionary<string, object>>();
            string root = Path.Combine(Probe.AppData, @"Claude\claude-code-sessions");
            DateTime cutoff = DateTime.Now.AddDays(-3);
            foreach (var dir in Probe.SafeDirs(root).SelectMany(Probe.SafeDirs))
                foreach (var file in Probe.SafeFiles(dir, "local_*.json"))
                {
                    if (file.LastWriteTime < cutoff) continue;
                    var d = p.CachedFields(file, "cliSessionId", "title", "model");
                    string cli = Json.Str(d, "cliSessionId");
                    if (cli != null) map[cli] = d;
                }
            return map;
        }

        private static IEnumerable<Session> CoworkSessions(Probe p)
        {
            string root = Path.Combine(Probe.AppData, @"Claude\local-agent-mode-sessions");
            DateTime cutoff = DateTime.Now.AddMinutes(-30);
            var list = new List<Session>();
            foreach (var dir in Probe.SafeDirs(root).SelectMany(Probe.SafeDirs))
                foreach (var file in Probe.SafeFiles(dir, "local_*.json"))
                {
                    if (file.LastWriteTime < cutoff) continue;
                    var d = p.CachedFields(file, "isArchived", "lastActivityAt", "title", "model");
                    if (d == null || Json.Bool(d, "isArchived")) continue;
                    DateTime last = Json.FromUnixMs(Json.Num(d, "lastActivityAt")) ?? file.LastWriteTime;
                    if (last < cutoff) continue;
                    list.Add(new Session
                    {
                        Kind = "Cowork",
                        Title = Json.Str(d, "title") ?? "Cowork task",
                        Status = (DateTime.Now - last).TotalSeconds < 90 ? "busy" : "idle",
                        Model = Json.Str(d, "model"),
                        Updated = last,
                    });
                }
            return list;
        }

        // Usage limits.
        private void Limits(Probe p, ProviderState st)
        {
            string creds = CredentialsPath(p);
            double interval = p.SavingPower ? 15 : 5;

            if (S.ClaudeLiveUsage && p.AllowNetwork && creds != null && (DateTime.Now - fetchedAt).TotalMinutes >= interval)
            {
                fetchedAt = DateTime.Now;
                var fresh = Fetch(creds);
                if (fresh != null) { live = fresh; needsLogin = false; }
            }

            if (creds != null && plan == null)
            {
                plan = Json.Str(Json.Obj(Json.ParseFile(creds), "claudeAiOauth"), "subscriptionType");
                SavePlan();
            }
            st.Plan = plan;

            Usage fallback = AppCachedUsage(p);
            st.Usage = live != null && (fallback == null || live.At >= fallback.At) ? live : fallback;
            if (st.Usage == live && live != null && (DateTime.Now - live.At).TotalMinutes > interval + 2)
                st.Usage = live.AsCache();

            if (!S.ClaudeLiveUsage) return;
            if (creds == null && !p.WslUp && wslHasClaude == true)
                st.Notice = L.T("WSL is off: limits from the last update");
            else if (creds == null || needsLogin)
            {
                st.NeedsLogin = true;
                st.Notice = L.T("Connect your account for real-time limits");
            }
        }

        private string CredentialsPath(Probe p)
        {
            string win = Path.Combine(Probe.Home, @".claude\.credentials.json");
            if (File.Exists(win)) return win;
            if (p.WslUp)
            {
                string wsl = p.WslHome + @"\.claude\.credentials.json";
                wslHasClaude = File.Exists(wsl);
                if (wslHasClaude == true) return wsl;
            }
            return null;
        }

        private Usage Fetch(string credsPath)
        {
            var oauth = Json.Obj(Json.ParseFile(credsPath), "claudeAiOauth");
            DateTime? exp = Json.FromUnixMs(Json.Num(oauth, "expiresAt"));
            if (exp == null || exp.Value < DateTime.Now.AddMinutes(2))
            {
                RefreshLogin(credsPath);
                oauth = Json.Obj(Json.ParseFile(credsPath), "claudeAiOauth");
                exp = Json.FromUnixMs(Json.Num(oauth, "expiresAt"));
                if (exp == null || exp.Value < DateTime.Now) { needsLogin = true; return null; }
            }
            string token = Json.Str(oauth, "accessToken");
            if (string.IsNullOrEmpty(token)) { needsLogin = true; return null; }

            try
            {
                var req = (HttpWebRequest)WebRequest.Create("https://api.anthropic.com/api/oauth/usage");
                req.Headers["Authorization"] = "Bearer " + token;
                req.Headers["anthropic-beta"] = "oauth-2025-04-20";
                req.UserAgent = "ai-meter/" + Controller.Version;
                req.Timeout = 15000;
                req.ReadWriteTimeout = 15000;
                using (var resp = (HttpWebResponse)req.GetResponse())
                using (var r = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                {
                    string body = r.ReadToEnd();
                    // Keep the latest response (usage and dates, no authentication tokens) for diagnostics and restart.
                    try { File.WriteAllText(RawFile, body, new UTF8Encoding(false)); }
                    catch (IOException) { }
                    var usage = Parse(Json.Parse(body));
                    if (usage != null)
                    {
                        string home = Path.GetDirectoryName(Path.GetDirectoryName(credsPath));
                        var account = Json.Obj(Json.ParseFile(Path.Combine(home, ".claude.json")), "oauthAccount");
                        string accountId = Json.Str(account, "accountUuid"), organisationId = Json.Str(account, "organizationUuid");
                        // Profile IDs survive token renewal. Without them, isolate this credential instead.
                        usage.Identity = HistoryStore.Hash(accountId != null ? accountId + "|" + organisationId : token);
                    }
                    return usage;
                }
            }
            catch (WebException e)
            {
                var hr = e.Response as HttpWebResponse;
                if (hr != null && (hr.StatusCode == HttpStatusCode.Unauthorized || hr.StatusCode == HttpStatusCode.Forbidden))
                    needsLogin = true;
                return null;
            }
            catch (IOException) { return null; }
        }

        /// <summary>
        /// Parse /api/oauth/usage using limits for session, week and model-specific weekly windows,
        /// falling back to legacy five_hour / seven_day fields. Include cloud credits and usage breakdowns.
        /// </summary>
        private static Usage Parse(Dictionary<string, object> d)
        {
            if (d == null) return null;
            var u = new Usage { Source = "live", At = DateTime.Now };
            var limits = d.ContainsKey("limits") ? d["limits"] as object[] : null;
            if (limits != null)
            {
                foreach (var l in limits.OfType<Dictionary<string, object>>())
                {
                    string kind = Json.Str(l, "kind");
                    double? pct = Json.Num(l, "percent");
                    if (pct == null) continue;
                    DateTime? resets = Json.FromIso(Json.Str(l, "resets_at"));
                    if (kind == "session")
                        u.Windows.Add(new UsageWindow { Label = "Session 5 h", Short = UsageWindow.ShortFiveHours, Used = pct.Value, ResetsAt = resets, StartedAt = resets.HasValue ? resets.Value.AddHours(-5) : (DateTime?)null });
                    else if (kind == "weekly_all")
                        u.Windows.Add(new UsageWindow { Label = "Week", Short = UsageWindow.ShortWeek, Used = pct.Value, ResetsAt = resets, StartedAt = resets.HasValue ? resets.Value.AddDays(-7) : (DateTime?)null });
                    else if (kind == "weekly_scoped")
                    {
                        string model = Json.Str(Json.Obj(Json.Obj(l, "scope"), "model"), "display_name");
                        u.Windows.Add(new UsageWindow { Label = "Week", Suffix = model ?? "model", Short = "m", Used = pct.Value, ResetsAt = resets, StartedAt = resets.HasValue ? resets.Value.AddDays(-7) : (DateTime?)null });
                    }
                }
            }
            if (u.Windows.Count == 0)
            {
                AddWindow(u, d, "five_hour", "Session 5 h", null, UsageWindow.ShortFiveHours, TimeSpan.FromHours(5));
                AddWindow(u, d, "seven_day", "Week", null, UsageWindow.ShortWeek, TimeSpan.FromDays(7));
                AddWindow(u, d, "seven_day_opus", "Week", "Opus", "m", TimeSpan.FromDays(7));
                AddWindow(u, d, "seven_day_sonnet", "Week", "Sonnet", "m", TimeSpan.FromDays(7));
            }

            // Dollar credits with resets: iguana_necktie covers cloud sessions;
            // other promotional credits share this shape.
            foreach (var kv in d)
            {
                var g = kv.Value as Dictionary<string, object>;
                double? limit = Json.Num(g, "limit_dollars"), used = Json.Num(g, "used_dollars");
                if (limit == null || limit <= 0 || used == null) continue;
                u.Windows.Add(new UsageWindow
                {
                    Label = kv.Key == "iguana_necktie" ? "Cloud session credits" : "Promotional credit",
                    Short = "$", Used = Json.Num(g, "utilization") ?? used.Value / limit.Value * 100,
                    ResetsAt = Json.FromIso(Json.Str(g, "resets_at")), Spent = new Money { Used = used.Value, Limit = limit.Value, Currency = "USD" },
                });
            }

            // Pay-as-you-go credits beyond plan limits; amounts are in cents.
            var extra = Json.Obj(d, "extra_usage");
            if (extra != null && (Json.Bool(extra, "is_enabled") || Json.Bool(extra, "credits_ever_enabled")))
            {
                double scale = Math.Pow(10, Json.Num(extra, "decimal_places") ?? 2);
                var m = new Money { Used = (Json.Num(extra, "used_credits") ?? 0) / scale, Limit = (Json.Num(extra, "monthly_limit") ?? 0) / scale, Currency = Json.Str(extra, "currency") ?? "USD" };
                string state = Json.Bool(extra, "is_enabled") ? "active"
                    : Json.Str(extra, "disabled_reason") == "out_of_credits" ? "paused, out of credits" : "disabled";
                u.CreditsNote = () => L.F("Extra usage: {0} of {1} per month · {2}", m.Format(m.Used), m.Format(m.Limit), L.T(state));
                u.ExtraActive = Json.Bool(extra, "is_enabled");
            }

            // Weekly usage split across Claude Code, chat and Cowork.
            var bd = Json.Obj(d, "seven_day_breakdown");
            var rows = bd != null && bd.ContainsKey("rows") ? bd["rows"] as object[] : null;
            if (rows != null)
                foreach (var r in rows.OfType<Dictionary<string, object>>())
                {
                    double? pc = Json.Num(r, "percent");
                    if (pc != null && pc > 0) u.Breakdown.Add(new KeyValuePair<string, double>(Json.Str(r, "display_name") ?? Json.Str(r, "key"), pc.Value));
                }
            return u.Windows.Count > 0 ? u : null;
        }

        private static void AddWindow(Usage u, Dictionary<string, object> d, string key, string label, string suffix, string shortLabel, TimeSpan? length)
        {
            var w = Json.Obj(d, key);
            double? used = Json.Num(w, "utilization");
            if (used == null) return;
            DateTime? resets = Json.FromIso(Json.Str(w, "resets_at"));
            u.Windows.Add(new UsageWindow
            {
                Label = label, Suffix = suffix, Short = shortLabel, Used = used.Value, ResetsAt = resets,
                StartedAt = resets.HasValue && length.HasValue ? resets.Value - length.Value : (DateTime?)null,
            });
        }

        private void RefreshLogin(string credsPath)
        {
            if (credsPath.StartsWith(@"\\wsl", StringComparison.OrdinalIgnoreCase))
                Probe.Run("wsl.exe", "-d " + S.WslDistro + " -- bash -lc \"claude auth status >/dev/null 2>&1\"", 40000, null);
            else
            {
                string exe = CliPath();
                if (exe != null) Probe.Run(exe, "auth status", 40000, null);
            }
        }

        /// <summary>The latest Claude Code CLI installed by the desktop app.</summary>
        public static string CliPath()
        {
            string root = Path.Combine(Probe.AppData, @"Claude\claude-code");
            try
            {
                return Directory.Exists(root)
                    ? Directory.GetFiles(root, "claude.exe", SearchOption.AllDirectories).OrderByDescending(File.GetLastWriteTime).FirstOrDefault()
                    : null;
            }
            catch (IOException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
        }

        /// <summary>Latest sample saved and updated by the Claude app.</summary>
        private static Usage AppCachedUsage(Probe p)
        {
            var fi = new FileInfo(Path.Combine(Probe.AppData, @"Claude\plan-usage-history.json"));
            if (!fi.Exists) return null;
            return p.Cached(fi.FullName, fi, () =>
            {
                var d = Json.ParseFile(fi.FullName);
                var samples = d == null || !d.ContainsKey("samples") ? null : d["samples"] as object[];
                if (samples == null || samples.Length == 0) return null;
                var last = samples[samples.Length - 1] as Dictionary<string, object>;
                var u = Json.Obj(last, "u");
                var usage = new Usage { Source = "cache", At = Json.FromUnixMs(Json.Num(last, "t")) ?? fi.LastWriteTime };
                if (Json.Num(u, "fh") != null) usage.Windows.Add(new UsageWindow { Label = "Session 5 h", Short = UsageWindow.ShortFiveHours, Used = Json.Num(u, "fh").Value });
                if (Json.Num(u, "sd") != null) usage.Windows.Add(new UsageWindow { Label = "Week", Short = UsageWindow.ShortWeek, Used = Json.Num(u, "sd").Value });
                return usage;
            }) as Usage;
        }

        /// <summary>Remember the plan whilst WSL is off; limits remain in the saved raw response.</summary>
        private void SavePlan()
        {
            try
            {
                Directory.CreateDirectory(Settings.Folder);
                File.WriteAllText(PlanFile, Json.Serialize(new Dictionary<string, object> { { "plan", plan } }), new UTF8Encoding(false));
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        /// <summary>At startup, restore the last saved response marked as cached.</summary>
        private void LoadPersisted()
        {
            plan = Json.Str(Json.ParseFile(PlanFile), "plan");
            var fi = new FileInfo(RawFile);
            if (!fi.Exists) return;
            var u = Parse(Json.ParseFile(fi.FullName));
            if (u == null) return;
            u.Source = "cache";
            u.At = fi.LastWriteTime;
            live = u;
        }
    }
}
