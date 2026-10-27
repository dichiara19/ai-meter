using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace AIMeter
{
    /// <summary>
    /// Spesa dei servizi a credito per giorno, settimana e mese. Se il servizio la fornisce si usa quella; altrimenti la
    /// ricava AI Meter da una cronologia delle letture (spend-history.json): differenza della spesa totale dall'inizio del
    /// periodo, oppure somma dei cali del saldo (le ricariche non contano).
    /// </summary>
    internal static class SpendTracker
    {
        private sealed class Reading { public DateTime At; public double? Total, Balance; }
        private static Dictionary<string, List<Reading>> history;
        private static readonly object Gate = new object();
        private static string FilePath { get { return Path.Combine(Settings.Folder, "spend-history.json"); } }

        /// <summary>Aggiunge una lettura: al massimo una ogni 10 minuti, se non cambia nulla; si tengono 40 giorni.</summary>
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

        /// <summary>Speso dall'inizio del periodo; null se non c'è ancora nessuna lettura.</summary>
        public static double? Since(string id, DateTime start)
        {
            lock (Gate)
            {
                Load();
                List<Reading> list;
                if (!history.TryGetValue(id, out list) || list.Count == 0) return null;
                var now = list[list.Count - 1];
                if (now.Total.HasValue)
                {
                    // l'ultima lettura prima dell'inizio, oppure la prima del periodo
                    var withTotal = list.Where(r => r.Total.HasValue).ToList();
                    var baseline = withTotal.LastOrDefault(r => r.At <= start) ?? withTotal.FirstOrDefault(r => r.At > start);
                    return baseline == null ? (double?)null : Math.Max(0, now.Total.Value - baseline.Total.Value);
                }
                double spent = 0;
                Reading prev = null;
                foreach (var r in list.Where(x => x.Balance.HasValue))
                {
                    if (prev != null && r.At > start)
                    {
                        double drop = prev.Balance.Value - r.Balance.Value;
                        if (drop > 0) spent += drop;
                    }
                    prev = r;
                }
                return spent;
            }
        }

        /// <summary>Inizio del periodo ("day", "week" da lunedì, "month"); utc = i periodi del servizio (es. OpenRouter).</summary>
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

        /// <summary>Budget di un servizio dalle impostazioni ("20|month"); false se non c'è.</summary>
        public static bool Budget(Settings s, string id, out double amount, out string period)
        {
            amount = 0; period = "month";
            string v;
            if (s == null || s.Budgets == null || !s.Budgets.TryGetValue(id, out v) || string.IsNullOrEmpty(v)) return false;
            var parts = v.Split('|');
            if (!double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out amount) || amount <= 0) return false;
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

    /// <summary>
    /// Modelli usati oggi dagli agenti del PC, per servizio: dai messaggi di OpenCode (e Kilo) e dai file di Pi, su Windows
    /// e in WSL già acceso. Costi e token come li calcolano gli agenti. Una scansione al minuto, solo dei file scritti oggi.
    /// </summary>
    internal static class AgentCosts
    {
        private static Dictionary<string, Dictionary<string, ModelCost>> today = new Dictionary<string, Dictionary<string, ModelCost>>();
        private static DateTime scannedAt = DateTime.MinValue;
        private static readonly object Gate = new object();

        /// <summary>I modelli usati oggi con i nomi di servizio indicati, dal più costoso.</summary>
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
                foreach (var id in ids)
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
                foreach (var dir in Probe.SafeDirs(Path.Combine(home, @".pi\agent\sessions")))
                    foreach (var fi in Probe.SafeFiles(dir, "*.jsonl").Where(f => f.LastWriteTime >= DateTime.Today && f.Length < 32L * 1024 * 1024))
                    {
                        var entries = (List<string[]>)p.Cached("costs:pi:" + fi.FullName + ":" + startMs, fi, () => PiToday(fi.FullName, startMs));
                        foreach (var e in entries)
                            add(e[0], e[1], double.Parse(e[2], CultureInfo.InvariantCulture), long.Parse(e[3], CultureInfo.InvariantCulture));
                    }
            }
            return acc;
        }

        /// <summary>Risposte di oggi in un file di Pi: provider, modello, costo, token.</summary>
        private static List<string[]> PiToday(string path, long startMs)
        {
            var list = new List<string[]>();
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
                list.Add(new[] { provider, model, cost.ToString("R", CultureInfo.InvariantCulture), tokens.ToString(CultureInfo.InvariantCulture) });
            }
            return list;
        }
    }
}
