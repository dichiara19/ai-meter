using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace AIMeter
{
    /// <summary>Whitelisted numeric metadata from one local response; absent values remain null.</summary>
    internal sealed class HistoryEvent
    {
        internal string Id, SessionId, Provider, Agent, Model;
        internal DateTime At;
        internal long? Input, Output, CacheRead, CacheWrite;
        internal double? Cost;
    }

    internal sealed class HistorySession
    {
        public string Id { get; set; }
        public string Provider { get; set; }
        public string Agent { get; set; }
        public string Model { get; set; }
        public DateTime First { get; set; }
        public DateTime Last { get; set; }
        public int? Feedback { get; set; }
        public long? Input { get; set; }
        public long? Output { get; set; }
        public long? CacheRead { get; set; }
        public long? CacheWrite { get; set; }
        public double? Cost { get; set; }
        public long Replies { get; set; }
    }

    /// <summary>
    /// Owns the local archive. A connection lives for one operation; the gate serialises writers and UI queries.
    /// Transactions commit observations and file offsets together, so a crash can safely replay a batch.
    /// </summary>
    internal sealed class HistoryStore
    {
        private readonly object gate = new object();
        internal readonly string FilePath;
        internal DateTime Started { get; private set; }
        private bool initialised;
        private static readonly long EpochTicks = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).Ticks;

        internal const int SchemaVersion = 2;

        internal HistoryStore(string path) { FilePath = path; }

        /// <summary>Run work in one immediate transaction under the writer gate; any exception rolls everything back.</summary>
        internal T Transaction<T>(Func<HistoryDatabase, T> work)
        {
            lock (gate) using (var db = Open())
            {
                db.Execute("BEGIN IMMEDIATE");
                try
                {
                    T result = work(db);
                    db.Execute("COMMIT");
                    return result;
                }
                catch { db.Execute("ROLLBACK"); throw; }
            }
        }
        internal static long Stamp(DateTime at) { return (at.ToUniversalTime().Ticks - EpochTicks) / TimeSpan.TicksPerMillisecond; }
        internal static DateTime Time(object value) { return new DateTime(EpochTicks + Convert.ToInt64(value) * TimeSpan.TicksPerMillisecond, DateTimeKind.Utc).ToLocalTime(); }
        internal static string Hash(string text)
        {
            using (var hash = SHA256.Create()) return BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(text ?? ""))).Replace("-", "").ToLowerInvariant();
        }

        private HistoryDatabase Open()
        {
            var db = new HistoryDatabase(FilePath);
            try
            {
                db.Execute("PRAGMA foreign_keys=ON");
                if (initialised) return db;
                long version = Convert.ToInt64(db.Query("PRAGMA user_version")[0][0]);
                if (version > SchemaVersion) throw new IOException("The history archive was created by a newer version of AI Meter.");
                db.Execute("PRAGMA journal_mode=WAL");
                db.Execute("PRAGMA synchronous=NORMAL");
                db.Execute("BEGIN IMMEDIATE");
                try
                {
                    db.Execute("CREATE TABLE IF NOT EXISTS metadata (key TEXT PRIMARY KEY, value TEXT NOT NULL)");
                    db.Execute("CREATE TABLE IF NOT EXISTS observations (provider TEXT NOT NULL, account TEXT NOT NULL, metric TEXT NOT NULL, measured INTEGER NOT NULL, captured INTEGER NOT NULL, resets INTEGER, starts INTEGER, percent REAL, spent REAL, allowance REAL, currency TEXT, quality TEXT NOT NULL, source TEXT NOT NULL, plan TEXT, amount REAL, unit TEXT, PRIMARY KEY(provider,account,metric,measured,quality)) WITHOUT ROWID");
                    db.Execute("CREATE INDEX IF NOT EXISTS observations_time ON observations(measured)");
                    db.Execute("CREATE TABLE IF NOT EXISTS sessions (id TEXT PRIMARY KEY, provider TEXT NOT NULL, agent TEXT NOT NULL, model TEXT, first INTEGER NOT NULL, last INTEGER NOT NULL, feedback INTEGER CHECK(feedback IN (-1,0,1)), notified INTEGER) WITHOUT ROWID");
                    db.Execute("CREATE INDEX IF NOT EXISTS sessions_time ON sessions(last)");
                    db.Execute("CREATE TABLE IF NOT EXISTS events (id TEXT PRIMARY KEY, session TEXT NOT NULL REFERENCES sessions(id), at INTEGER NOT NULL, input INTEGER, output INTEGER, cache_read INTEGER, cache_write INTEGER, cost REAL, model TEXT) WITHOUT ROWID");
                    db.Execute("CREATE INDEX IF NOT EXISTS events_session ON events(session,at)");
                    db.Execute("CREATE INDEX IF NOT EXISTS events_time ON events(at)");
                    db.Execute("CREATE TABLE IF NOT EXISTS cursors (id TEXT PRIMARY KEY, position INTEGER NOT NULL, modified INTEGER NOT NULL) WITHOUT ROWID");
                    db.Execute("CREATE TABLE IF NOT EXISTS coverage (first INTEGER NOT NULL, last INTEGER NOT NULL, reason TEXT NOT NULL, PRIMARY KEY(first,last,reason)) WITHOUT ROWID");
                    // Version 2: consumption intervals, per-scope processing state and alert episodes (see ConsumptionMonitor).
                    db.Execute("CREATE TABLE IF NOT EXISTS consumption_scopes (scope TEXT PRIMARY KEY, provider TEXT NOT NULL, account TEXT NOT NULL, metric TEXT NOT NULL, source TEXT NOT NULL, plan TEXT, allowance REAL, currency TEXT, anchor_at INTEGER, anchor_percent REAL, anchor_resets INTEGER, last_at INTEGER, last_percent REAL, last_resets INTEGER, pending INTEGER NOT NULL DEFAULT 0, reference_since INTEGER, previous_since INTEGER, reference_reason TEXT) WITHOUT ROWID");
                    db.Execute("CREATE TABLE IF NOT EXISTS consumption_intervals (scope TEXT NOT NULL, starts INTEGER NOT NULL, ends INTEGER NOT NULL, points REAL NOT NULL, resets INTEGER, version INTEGER NOT NULL, PRIMARY KEY(scope,starts)) WITHOUT ROWID");
                    db.Execute("CREATE INDEX IF NOT EXISTS consumption_intervals_end ON consumption_intervals(scope,ends)");
                    db.Execute("CREATE TABLE IF NOT EXISTS consumption_episodes (id TEXT PRIMARY KEY, scope TEXT NOT NULL, provider TEXT NOT NULL, account TEXT NOT NULL, metric TEXT NOT NULL, resets INTEGER, state TEXT NOT NULL, opened INTEGER NOT NULL, last INTEGER NOT NULL, evidence TEXT NOT NULL, baseline TEXT NOT NULL, version INTEGER NOT NULL, eligible INTEGER NOT NULL, notified INTEGER, feedback TEXT, normal INTEGER NOT NULL DEFAULT 0, resolved INTEGER, reason TEXT) WITHOUT ROWID");
                    db.Execute("CREATE INDEX IF NOT EXISTS consumption_episodes_time ON consumption_episodes(last)");
                    db.Execute("INSERT OR IGNORE INTO metadata VALUES('started',?)", Stamp(DateTime.Now).ToString(CultureInfo.InvariantCulture));
                    db.Execute("PRAGMA user_version=" + SchemaVersion.ToString(CultureInfo.InvariantCulture));
                    db.Execute("COMMIT");
                }
                catch { db.Execute("ROLLBACK"); throw; }
                Started = Time(Convert.ToInt64(db.Query("SELECT value FROM metadata WHERE key='started'")[0][0], CultureInfo.InvariantCulture));
                initialised = true;
                return db;
            }
            catch { db.Dispose(); throw; }
        }

        internal void Record(Snapshot snapshot, Action<HistoryDatabase> collectEvents, DateTime? pausedSince = null)
        {
            lock (gate)
            using (var db = Open())
            {
                db.Execute("BEGIN IMMEDIATE");
                try
                {
                    var last = db.Query("SELECT value FROM metadata WHERE key='heartbeat'");
                    long now = Stamp(snapshot.Taken);
                    if (pausedSince.HasValue) Gap(db, Stamp(pausedSince.Value), Math.Max(now, Stamp(DateTime.Now)), "collection-paused");
                    else if (last.Count > 0)
                    {
                        long previous = Convert.ToInt64(last[0][0], CultureInfo.InvariantCulture);
                        if (now - previous > TimeSpan.FromMinutes(5).TotalMilliseconds) Gap(db, previous, now, "collection-paused");
                    }
                    db.Execute("INSERT OR REPLACE INTO metadata VALUES('heartbeat',?)", now.ToString(CultureInfo.InvariantCulture));
                    foreach (var provider in snapshot.Providers)
                    {
                        var usage = provider.Usage;
                        if (usage == null || usage.Source == "cache" || usage.At == DateTime.MinValue || usage.At > snapshot.Taken.AddMinutes(5)) continue;
                        var windows = usage.Windows.Concat(usage.Breakdown.Select(part => new UsageWindow {
                            Label = "Weekly usage breakdown", Suffix = part.Key, Amount = part.Value, AmountUnit = "percent", NoBar = true }));
                        foreach (var window in windows)
                        {
                            if (window.CachedAt.HasValue) continue;
                            double? percent = window.HasPercent && Finite(window.Used) ? (double?)window.Used : null;
                            double? amount = window.Amount.HasValue && Finite(window.Amount.Value) ? window.Amount : null;
                            double? spent = window.Spent != null && Finite(window.Spent.Used) ? (double?)window.Spent.Used : null;
                            bool hasValue = percent.HasValue || amount.HasValue || spent.HasValue;
                            string quality = hasValue ? (window.IsEstimated ? "estimated" : "measured") : window.RightText == "unlimited" ? "unlimited" : "unavailable";
                            db.Execute("INSERT OR IGNORE INTO observations VALUES(?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?)",
                                provider.Id, Hash(provider.Id + "|" + (usage.Identity ?? "unidentified")), window.Key, Stamp(window.MeasuredAt ?? usage.At), Math.Max(now, Stamp(window.MeasuredAt ?? usage.At)),
                                window.ResetsAt.HasValue ? (object)Stamp(window.ResetsAt.Value) : null,
                                window.StartedAt.HasValue ? (object)Stamp(window.StartedAt.Value) : null,
                                percent, spent,
                                window.Spent != null && Finite(window.Spent.Limit) ? (object)window.Spent.Limit : null,
                                window.Spent == null ? null : window.Spent.Currency, quality,
                                (usage.Source ?? "unknown") + (usage.Identity == null ? ":unidentified-account" : ""), provider.Plan,
                                amount, window.AmountUnit);
                        }
                    }
                    if (collectEvents != null) collectEvents(db);
                    db.Execute("COMMIT");
                }
                catch
                {
                    db.Execute("ROLLBACK");
                    throw;
                }
            }
        }

        internal static bool Finite(double value) { return !double.IsNaN(value) && !double.IsInfinity(value); }
        internal static void Gap(HistoryDatabase db, long first, long last, string reason)
        {
            db.Execute("INSERT OR IGNORE INTO coverage VALUES(?,?,?)", first, last, reason);
        }

        internal static void AddEvent(HistoryDatabase db, HistoryEvent item)
        {
            if (item.Id == null || item.SessionId == null || item.At == DateTime.MinValue) return;
            long at = Stamp(item.At);
            // A copied response belongs to its first observed session, including Pi fork inheritance.
            var owner = db.Query("SELECT session,input,output,cache_read,cache_write,cost,model,at FROM events WHERE id=?", item.Id);
            string session = owner.Count == 0 ? item.SessionId : Convert.ToString(owner[0][0]);
            object[] previous = owner.Count == 0 ? new object[8] : owner[0];
            if (owner.Count > 0 && (!item.Input.HasValue || Equals(item.Input, previous[1])) && (!item.Output.HasValue || Equals(item.Output, previous[2]))
                && (!item.CacheRead.HasValue || Equals(item.CacheRead, previous[3])) && (!item.CacheWrite.HasValue || Equals(item.CacheWrite, previous[4]))
                && (!item.Cost.HasValue || Equals(item.Cost, previous[5])) && (item.Model == null || item.Model == previous[6] as string)) return;
            db.Execute("INSERT OR IGNORE INTO sessions(id,provider,agent,model,first,last) VALUES(?,?,?,?,?,?)", session, item.Provider ?? "unknown", item.Agent, item.Model, at, at);
            db.Execute("UPDATE sessions SET model=CASE WHEN ?>=last THEN COALESCE(?,model) ELSE model END,first=MIN(first,?),last=MAX(last,?) WHERE id=?", at, item.Model, at, at, session);
            // Some logs repeat an assistant message with updated streaming usage. Replace its counters, never add them twice.
            db.Execute("INSERT OR REPLACE INTO events VALUES(?,?,?,?,?,?,?,?,?)", item.Id, session,
                previous[7] == null ? at : Math.Min(at, Convert.ToInt64(previous[7])),
                (object)item.Input ?? previous[1], (object)item.Output ?? previous[2], (object)item.CacheRead ?? previous[3],
                (object)item.CacheWrite ?? previous[4], (object)item.Cost ?? previous[5], (object)item.Model ?? previous[6]);
        }

        internal List<object[]> Query(string sql, params object[] values)
        {
            lock (gate) using (var db = Open()) return db.Query(sql, values);
        }

        internal T ReadConsistently<T>(Func<T> read)
        {
            // Every writer uses the same gate; a projection cannot straddle two collection batches.
            lock (gate) return read();
        }

        internal List<HistorySession> Sessions(DateTime since, string provider, int maximum)
        {
            return Query("SELECT s.id,s.provider,s.agent,s.model,s.first,s.last,s.feedback,SUM(e.input),SUM(e.output),SUM(e.cache_read),SUM(e.cache_write),SUM(e.cost),COUNT(e.id) FROM sessions s JOIN events e ON e.session=s.id WHERE e.at>=? AND (?='' OR s.provider=?) GROUP BY s.id ORDER BY s.last DESC LIMIT ?", Stamp(since), provider, provider, maximum)
                .Select(row => new HistorySession { Id = (string)row[0], Provider = (string)row[1], Agent = (string)row[2], Model = row[3] as string,
                    First = Time(row[4]), Last = Time(row[5]), Feedback = row[6] == null ? (int?)null : Convert.ToInt32(row[6]),
                    Input = Integer(row[7]), Output = Integer(row[8]), CacheRead = Integer(row[9]), CacheWrite = Integer(row[10]),
                    Cost = row[11] == null ? (double?)null : Convert.ToDouble(row[11]), Replies = Convert.ToInt64(row[12]) }).ToList();
        }

        private static long? Integer(object value) { return value == null ? (long?)null : Convert.ToInt64(value); }

        internal void Rate(string session, int? value)
        {
            if (value.HasValue && (value < -1 || value > 1)) throw new ArgumentOutOfRangeException("value");
            lock (gate) using (var db = Open()) db.Execute("UPDATE sessions SET feedback=? WHERE id=?", value, session);
        }

        /// <summary>Claim at most one recent idle session per hour, durably. Ignored prompts remain unrated.</summary>
        internal string ClaimFeedback(DateTime now, DateTime enabledSince)
        {
            lock (gate) using (var db = Open())
            {
                db.Execute("BEGIN IMMEDIATE");
                try
                {
                    var recent = db.Query("SELECT id FROM sessions WHERE feedback IS NULL AND notified IS NULL AND last BETWEEN ? AND ? AND last>=? AND NOT EXISTS(SELECT 1 FROM sessions WHERE notified>?) ORDER BY last DESC LIMIT 1",
                        Stamp(now.AddHours(-2)), Stamp(now.AddMinutes(-30)), Stamp(enabledSince), Stamp(now.AddHours(-1)));
                    string id = recent.Count == 0 ? null : (string)recent[0][0];
                    if (id != null) db.Execute("UPDATE sessions SET notified=? WHERE id=?", Stamp(now), id);
                    db.Execute("COMMIT");
                    return id;
                }
                catch { db.Execute("ROLLBACK"); throw; }
            }
        }

        internal long SizeBytes
        {
            get { lock (gate) return new[] { FilePath, FilePath + "-wal", FilePath + "-shm" }.Where(File.Exists).Sum(path => new FileInfo(path).Length); }
        }

        /// <summary>Stream a consistent, versioned JSONL export without retaining the archive in memory.</summary>
        internal void Export(string path)
        {
            string destination = Path.GetFullPath(path);
            if (new[] { FilePath, FilePath + "-wal", FilePath + "-shm" }.Any(file => string.Equals(Path.GetFullPath(file), destination, StringComparison.OrdinalIgnoreCase)))
                throw new IOException("Choose an export destination outside the active archive files.");
            lock (gate) using (var db = Open())
            {
                db.Execute("BEGIN");
                string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    using (var writer = new StreamWriter(temporary, false, new UTF8Encoding(false)))
                    {
                        writer.WriteLine(Json.Serialize(new { format = "ai-meter-history", version = 2, timeUnit = "unix-milliseconds", exportedUtc = DateTime.UtcNow.ToString("o") }));
                        // Processing state (file cursors and consumption scopes) is internal and not exported.
                        foreach (string table in new[] { "metadata", "observations", "sessions", "events", "coverage", "consumption_intervals", "consumption_episodes" })
                        {
                            var columns = db.Query("PRAGMA table_info(" + table + ")").Select(row => (string)row[1]).ToArray();
                            db.Read("SELECT * FROM " + table, row =>
                            {
                                var record = new Dictionary<string, object> { { "table", table } };
                                for (int i = 0; i < columns.Length; i++) record[columns[i]] = row[i];
                                writer.WriteLine(Json.Serialize(record));
                            });
                        }
                    }
                    db.Execute("COMMIT");
                    if (File.Exists(path)) File.Replace(temporary, path, null); else File.Move(temporary, path);
                }
                catch
                {
                    try { db.Execute("ROLLBACK"); } catch (IOException) { }
                    if (File.Exists(temporary)) File.Delete(temporary);
                    throw;
                }
            }
        }

        internal volatile int Generation;

        internal void Clear()
        {
            lock (gate) using (var db = Open())
            {
                db.Execute("BEGIN IMMEDIATE");
                try
                {
                    // Deleting history also resets consumption readiness, processing cursors and episodes.
                    foreach (string table in new[] { "events", "sessions", "observations", "coverage", "cursors", "consumption_episodes", "consumption_intervals", "consumption_scopes", "metadata" })
                        db.Execute("DELETE FROM " + table);
                    Started = DateTime.Now;
                    db.Execute("INSERT INTO metadata VALUES('started',?)", Stamp(Started).ToString(CultureInfo.InvariantCulture));
                    db.Execute("COMMIT");
                    Generation++;
                }
                catch { db.Execute("ROLLBACK"); throw; }
                // Reclaim pages and remove deleted text from the active database and WAL.
                db.Execute("PRAGMA wal_checkpoint(TRUNCATE)");
                db.Execute("VACUUM");
                db.Execute("PRAGMA wal_checkpoint(TRUNCATE)");
            }
        }
    }
}
