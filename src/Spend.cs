using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace AIMeter
{
    internal sealed class PeriodSpend
    {
        public double Amount;
        public DateTime From;
        public bool Estimated;
    }

    /// <summary>Observed spending, isolated by credential and currency. Sampled totals and balances are estimates.</summary>
    internal static class SpendTracker
    {
        private sealed class Reading { public DateTime At; public double? Total, Balance; }
        private static Dictionary<string, List<Reading>> history;
        private static readonly object Gate = new object();
        private static string FilePath { get { return Path.Combine(Settings.Folder, "spend-history.json"); } }

        /// <summary>Hash credentials rather than persisting them or mixing unrelated accounts and currencies.</summary>
        internal static string Scope(string id, string token, string currency)
        {
            using (var hash = SHA256.Create())
                return id + "|" + BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(token + "\0" + currency))).Replace("-", "");
        }

        /// <summary>Keep 40 days of readings; unchanged values need at most one sample every ten minutes.</summary>
        public static void Record(string id, double? total, double? balance) { Record(id, total, balance, DateTime.Now); }

        internal static void Record(string id, double? total, double? balance, DateTime now)
        {
            if (total == null && balance == null) return;
            lock (Gate)
            {
                Load();
                List<Reading> list;
                if (!history.TryGetValue(id, out list)) history[id] = list = new List<Reading>();
                var last = list.Count > 0 ? list[list.Count - 1] : null;
                if (last != null && last.Total == total && last.Balance == balance && (now - last.At).TotalMinutes < 10) return;
                list.Add(new Reading { At = now, Total = total, Balance = balance });
                list.RemoveAll(r => (now - r.At).TotalDays > 40);
                Save();
            }
        }

        /// <summary>Estimate only intervals wholly inside the period. A single sample cannot establish spending.</summary>
        public static PeriodSpend Since(string id, DateTime start)
        {
            lock (Gate)
            {
                Load();
                List<Reading> list;
                if (id == null || !history.TryGetValue(id, out list)) return null;
                var samples = list.Where(r => r.At >= start).OrderBy(r => r.At).ToList();
                if (samples.Count < 2) return null;
                bool totals = samples[samples.Count - 1].Total.HasValue;
                samples = samples.Where(r => totals ? r.Total.HasValue : r.Balance.HasValue).ToList();
                if (samples.Count < 2) return null;
                double spent = 0;
                for (int i = 1; i < samples.Count; i++)
                {
                    var prev = samples[i - 1];
                    var next = samples[i];
                    double delta = totals ? next.Total.Value - prev.Total.Value : prev.Balance.Value - next.Balance.Value;
                    // Counter resets and net top-ups cannot reveal consumption between the two samples.
                    spent += Math.Max(0, delta);
                }
                return new PeriodSpend { Amount = spent, From = samples[0].At, Estimated = true };
            }
        }

        /// <summary>Period boundary: day, Monday-based week, or month; optionally use the service's UTC calendar.</summary>
        public static DateTime Start(string period, DateTime now, bool utc)
        {
            var t = utc ? now.ToUniversalTime() : now;
            DateTime s;
            if (period == "day") s = t.Date;
            else if (period == "week") s = t.Date.AddDays(-(((int)t.DayOfWeek + 6) % 7));
            else s = new DateTime(t.Year, t.Month, 1);
            return utc ? DateTime.SpecifyKind(s, DateTimeKind.Utc).ToLocalTime() : s;
        }

        public static DateTime End(string period, DateTime now, bool utc)
        {
            var s = Start(period, now, utc);
            var t = utc ? s.ToUniversalTime() : s;
            var e = period == "day" ? t.AddDays(1) : period == "week" ? t.AddDays(7) : t.AddMonths(1);
            return utc ? DateTime.SpecifyKind(e, DateTimeKind.Utc).ToLocalTime() : e;
        }

        /// <summary>Read a positive finite budget such as "20|month" from settings.</summary>
        public static bool Budget(Settings s, string id, out double amount, out string period)
        {
            amount = 0; period = "month";
            string v;
            if (s == null || s.Budgets == null || !s.Budgets.TryGetValue(id, out v) || string.IsNullOrEmpty(v)) return false;
            var parts = v.Split('|');
            if (!double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out amount) || double.IsNaN(amount) || double.IsInfinity(amount) || amount <= 0) return false;
            if (parts.Length > 1 && (parts[1] == "day" || parts[1] == "week" || parts[1] == "month")) period = parts[1];
            return true;
        }

        private static void Load()
        {
            if (history != null) return;
            history = new Dictionary<string, List<Reading>>();
            try
            {
                var d = Json.ParseFile(FilePath);
                if (d == null) return;
                foreach (var kv in d)
                {
                    // Legacy histories have no credential identity and cannot safely seed a new baseline.
                    if (!kv.Key.Contains("|")) continue;
                    var rows = kv.Value as object[];
                    if (rows == null) continue;
                    history[kv.Key] = rows.OfType<object[]>().Where(r => r.Length >= 3).Select(r => new Reading
                    {
                        At = Json.FromUnix(JPath.Num(r[0])) ?? DateTime.MinValue, Total = JPath.Num(r[1]), Balance = JPath.Num(r[2]),
                    }).Where(r => r.At > DateTime.MinValue).ToList();
                }
            }
            catch (Exception) { }
        }

        private static void Save()
        {
            try
            {
                var epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
                var o = history.ToDictionary(kv => kv.Key, kv => (object)kv.Value.Select(r => new object[]
                {
                    (long)(r.At.ToUniversalTime() - epoch).TotalSeconds, r.Total, r.Balance,
                }).ToArray());
                Directory.CreateDirectory(Settings.Folder);
                File.WriteAllText(FilePath, Json.Serialize(o), new UTF8Encoding(false));
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>Today's costs reported by OpenCode, Kilo and Pi on Windows and already running WSL distributions.</summary>
    internal static class AgentCosts
    {
        private sealed class PiCost
        {
            public string Provider, Model, Identity;
            public double Cost;
            public long Tokens;
        }

        private static Dictionary<string, Dictionary<string, ModelCost>> today = new Dictionary<string, Dictionary<string, ModelCost>>();
        private static DateTime scannedAt = DateTime.MinValue;
        private static readonly object Gate = new object();

        /// <summary>Merge provider aliases and order today's models by cost, then token count.</summary>
        public static List<ModelCost> For(Probe p, IEnumerable<string> ids)
        {
            lock (Gate)
            {
                if ((DateTime.Now - scannedAt).TotalSeconds >= (p.SavingPower ? 180 : 60) || scannedAt.Date != DateTime.Today)
                {
                    scannedAt = DateTime.Now;
                    var homes = new List<string> { Probe.Home };
                    if (p.WslUp) homes.Add(p.WslHome);
                    try { today = Scan(p, homes); } catch (Exception) { }
                }
                var merged = new Dictionary<string, ModelCost>(StringComparer.OrdinalIgnoreCase);
                foreach (var id in ids.Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    Dictionary<string, ModelCost> byModel;
                    if (!today.TryGetValue(id.ToLowerInvariant(), out byModel)) continue;
                    foreach (var m in byModel.Values) Add(merged, m.Model, m.Cost, m.Tokens, m.Replies);
                }
                return merged.Values.OrderByDescending(m => m.Cost).ThenByDescending(m => m.Tokens).ToList();
            }
        }

        private static void Add(Dictionary<string, ModelCost> d, string model, double cost, long tokens, int replies)
        {
            if (string.IsNullOrEmpty(model)) return;
            ModelCost m;
            if (!d.TryGetValue(model, out m)) d[model] = m = new ModelCost { Model = model };
            m.Cost += cost; m.Tokens += tokens; m.Replies += replies;
        }

        internal static Dictionary<string, Dictionary<string, ModelCost>> Scan(Probe p, List<string> homes)
        {
            var acc = new Dictionary<string, Dictionary<string, ModelCost>>();
            var piResponses = new HashSet<string>(StringComparer.Ordinal);
            Action<string, string, double, long> add = (provider, model, cost, tokens) =>
            {
                if (string.IsNullOrEmpty(provider)) return;
                Dictionary<string, ModelCost> d;
                if (!acc.TryGetValue(provider.ToLowerInvariant(), out d)) acc[provider.ToLowerInvariant()] = d = new Dictionary<string, ModelCost>(StringComparer.OrdinalIgnoreCase);
                Add(d, model, cost, tokens, 1);
            };
            long startMs = (long)(DateTime.Today.ToUniversalTime() - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalMilliseconds;
            foreach (var home in homes)
            {
                foreach (var app in new[] { @".local\share\opencode\opencode.db", @".local\share\kilo\kilo.db" })
                {
                    string db = Path.Combine(home, app);
                    var fi = new FileInfo(db);
                    var wal = new FileInfo(db + "-wal");
                    if (!fi.Exists || (fi.LastWriteTime < DateTime.Today && (!wal.Exists || wal.LastWriteTime < DateTime.Today))) continue;
                    var stamp = wal.Exists && wal.Length > 0 ? wal : fi;
                    var rows = (List<object[]>)p.Cached("costs:" + db + ":" + startMs, stamp, () =>
                        Sqlite.Rows(CodingAgents.Openable(db), "SELECT data FROM message WHERE time_created > " + startMs.ToString(CultureInfo.InvariantCulture), 20000) ?? new List<object[]>());
                    foreach (var r in rows)
                    {
                        var m = Json.ParseAny(r.Length > 0 && r[0] != null ? Convert.ToString(r[0], CultureInfo.InvariantCulture) : "");
                        if (JPath.Str(JPath.Get(m, "role")) != "assistant") continue;
                        long tokens = (long)((JPath.Num(JPath.Get(m, "tokens.input")) ?? 0) + (JPath.Num(JPath.Get(m, "tokens.output")) ?? 0)
                                             + (JPath.Num(JPath.Get(m, "tokens.reasoning")) ?? 0) + (JPath.Num(JPath.Get(m, "tokens.cache.read")) ?? 0)
                                             + (JPath.Num(JPath.Get(m, "tokens.cache.write")) ?? 0));
                        add(JPath.Str(JPath.Get(m, "providerID")), JPath.Str(JPath.Get(m, "modelID")), JPath.Num(JPath.Get(m, "cost")) ?? 0, tokens);
                    }
                }
                PiRootCosts(p, Path.Combine(home, @".pi\agent\sessions"), startMs, piResponses, add);
                foreach (var db in CodingAgents.HermesDatabases(home, home == Probe.Home))
                    foreach (var c in HermesCosts(p, db, startMs))
                    {
                        Dictionary<string, ModelCost> d;
                        if (!acc.TryGetValue(c.Provider, out d)) acc[c.Provider] = d = new Dictionary<string, ModelCost>(StringComparer.OrdinalIgnoreCase);
                        Add(d, c.Model, c.Cost, c.Tokens, c.Replies);
                    }
            }
            // Pi runs that keep their sessions elsewhere (--session-dir, PI_CODING_AGENT_DIR) while they are running.
            try { foreach (var folder in RunningAgents.PiSessionFolders(p)) PiRootCosts(p, folder.Path, startMs, piResponses, add); }
            catch (Exception) { }
            return acc;
        }

        private sealed class HermesCost
        {
            public string Provider, Model;
            public double Cost;
            public long Tokens;
            public int Replies;
        }

        /// <summary>
        /// Today's share of the costs Hermes recorded in a state.db, by provider and model. Hermes adds up cost and tokens
        /// per session and model (session_model_usage; older versions only per session, in sessions), not per reply, so
        /// a session that began before today counts with the share of its messages' tokens written today (or, when
        /// messages carry no token count, of its messages). The cost is the one the provider reported, else Hermes's
        /// estimate. A provider set as "custom" or "auto" is the service whose API is on the same site as the endpoint.
        /// </summary>
        private static List<HermesCost> HermesCosts(Probe p, string db, long startMs)
        {
            var fi = new FileInfo(db);
            var wal = new FileInfo(db + "-wal");
            if (!fi.Exists || (fi.LastWriteTime < DateTime.Today && (!wal.Exists || wal.LastWriteTime < DateTime.Today))) return new List<HermesCost>();
            var stamp = wal.Exists && wal.Length > 0 ? wal : fi;
            return (List<HermesCost>)p.Cached("costs:hermes:" + db + ":" + startMs, stamp, () =>
            {
                string start = (startMs / 1000.0).ToString("R", CultureInfo.InvariantCulture);
                Func<string, string> share = id =>
                    ", (SELECT COALESCE(SUM(token_count), 0) FROM messages m WHERE m.session_id = " + id + ")"
                    + ", (SELECT COALESCE(SUM(token_count), 0) FROM messages m WHERE m.session_id = " + id + " AND m.timestamp >= " + start + ")"
                    + ", (SELECT COUNT(*) FROM messages m WHERE m.session_id = " + id + ")"
                    + ", (SELECT COUNT(*) FROM messages m WHERE m.session_id = " + id + " AND m.timestamp >= " + start + ")";
                const string Tokens = "input_tokens + output_tokens + cache_read_tokens + cache_write_tokens + reasoning_tokens";
                string path = CodingAgents.Openable(db);
                var rows = Sqlite.Rows(path, "SELECT billing_provider, billing_base_url, model, actual_cost_usd, estimated_cost_usd, " + Tokens
                                             + ", api_call_count, first_seen" + share("u.session_id") + " FROM session_model_usage u WHERE last_seen >= " + start, 5000)
                        ?? Sqlite.Rows(path, "SELECT billing_provider, billing_base_url, model, actual_cost_usd, estimated_cost_usd, " + Tokens
                                             + ", api_call_count, started_at" + share("s.id") + " FROM sessions s WHERE COALESCE(last_activity_at, started_at) >= " + start, 5000);
                var list = new List<HermesCost>();
                foreach (var r in rows ?? new List<object[]>())
                {
                    Func<int, double> num = i => r.Length > i && r[i] != null ? Convert.ToDouble(r[i], CultureInfo.InvariantCulture) : 0;
                    Func<int, string> str = i => r.Length > i && r[i] != null ? Convert.ToString(r[i], CultureInfo.InvariantCulture) : null;
                    string provider = str(0);
                    if (string.IsNullOrEmpty(provider) || provider == "custom" || provider == "auto") provider = ServiceAt(str(1));
                    if (provider == null || string.IsNullOrEmpty(str(2))) continue;
                    double part = num(7) * 1000 >= startMs ? 1 : num(8) > 0 ? num(9) / num(8) : num(10) > 0 ? num(11) / num(10) : 0;
                    if (part <= 0) continue;
                    list.Add(new HermesCost
                    {
                        Provider = provider.ToLowerInvariant(), Model = str(2),
                        Cost = (num(3) > 0 ? num(3) : num(4)) * part, Tokens = (long)(num(5) * part),
                        Replies = Math.Max(1, (int)Math.Round(num(6) * part)),
                    });
                }
                return list;
            });
        }

        /// <summary>The first service id of the definition whose usage or model list is on the same site as an endpoint, or null.</summary>
        private static string ServiceAt(string baseUrl)
        {
            Uri uri;
            if (string.IsNullOrEmpty(baseUrl) || !Uri.TryCreate(baseUrl, UriKind.Absolute, out uri)) return null;
            Func<string, string> site = host => string.Join(".", host.Split('.').Reverse().Take(2).Reverse());
            foreach (var d in Definitions.All)
                foreach (var section in new[] { "request", "freeModels" })
                {
                    Uri own;
                    if (Uri.TryCreate(Json.Str(Json.Obj(d.Raw, section), "url") ?? "", UriKind.Absolute, out own) && site(own.Host) == site(uri.Host))
                        return ServiceDefinition.Strings(d.Auth, "ids").DefaultIfEmpty(d.Id).First();
                }
            return null;
        }

        private static void PiRootCosts(Probe p, string root, long startMs, HashSet<string> piResponses, Action<string, string, double, long> add)
        {
            foreach (var dir in CodingAgents.PiSessionDirs(root))
                foreach (var fi in Probe.SafeFiles(dir, "*.jsonl").Where(f => f.LastWriteTime >= DateTime.Today && f.Length < 32L * 1024 * 1024))
                {
                    var entries = (List<PiCost>)p.Cached("costs:pi:" + fi.FullName + ":" + startMs, fi, () => PiToday(fi.FullName, startMs));
                    foreach (var e in entries)
                        if (piResponses.Add(e.Identity)) add(e.Provider, e.Model, e.Cost, e.Tokens);
                }
        }

        /// <summary>Read today's Pi replies, retaining an identity shared by copies in forked sessions.</summary>
        private static List<PiCost> PiToday(string path, long startMs)
        {
            var list = new List<PiCost>();
            string text;
            try { text = File.ReadAllText(path, Encoding.UTF8); }
            catch (IOException) { return list; }
            foreach (var line in text.Split('\n'))
            {
                if (line.IndexOf("\"assistant\"", StringComparison.Ordinal) < 0) continue;
                var e = Json.ParseAny(line);
                if (JPath.Str(JPath.Get(e, "type")) != "message" || JPath.Str(JPath.Get(e, "message.role")) != "assistant") continue;
                double? ms = JPath.Num(JPath.Get(e, "message.timestamp"));
                if (ms == null)
                {
                    var t = Json.FromIso(JPath.Str(JPath.Get(e, "timestamp")));
                    if (t.HasValue) ms = (t.Value.ToUniversalTime() - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalMilliseconds;
                }
                if (ms == null || ms < startMs) continue;
                var u = JPath.Get(e, "message.usage");
                double cost = JPath.Num(JPath.Get(u, "cost.total")) ?? 0;
                long tokens = (long)(JPath.Num(JPath.Get(u, "totalTokens")) ?? ((JPath.Num(JPath.Get(u, "input")) ?? 0) + (JPath.Num(JPath.Get(u, "output")) ?? 0)
                                                                              + (JPath.Num(JPath.Get(u, "cacheRead")) ?? 0) + (JPath.Num(JPath.Get(u, "cacheWrite")) ?? 0)));
                string provider = JPath.Str(JPath.Get(e, "message.provider"));
                string model = JPath.Str(JPath.Get(e, "message.model"));
                if (provider == null || model == null) continue;
                // Pi preserves the message when forking, even when entry ids or parent links change.
                string identity;
                using (var hash = SHA256.Create())
                    identity = ms.Value.ToString("R", CultureInfo.InvariantCulture) + ":" + BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(Json.Serialize(JPath.Get(e, "message")))));
                list.Add(new PiCost { Provider = provider, Model = model, Cost = cost, Tokens = tokens, Identity = identity });
            }
            return list;
        }
    }
}
