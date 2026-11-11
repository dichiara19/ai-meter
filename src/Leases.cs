using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace AIMeter
{
    /// <summary>
    /// Cooperative capacity leases (ADR 0002; docs/plans/orchestration-contracts.md section 3.2). A coordinator plugin
    /// claims a slot in a pool before it starts an agent, renews it during long tasks and releases it with the outcome.
    /// Active leases count against the pool's room for every caller, so plugins that use leases cannot overbook a pool;
    /// clients that do not use them are invisible, and the answers say so. A lease that is not released expires on its
    /// own with an unknown outcome, so a crashed plugin cannot block a pool.
    ///
    /// The book keeps only pools, models, agents, client labels, times and outcome counters (the cost among them, which
    /// a free tier's budget counts before the service's own count catches up): never task text, code,
    /// logs, paths or keys. It is saved as a small JSON file next to the settings; outcome counters are kept for 30 days.
    /// All members are thread-safe: MCP clients call them from their own threads.
    ///
    /// Times are kept and compared in UTC, so a daylight-saving change cannot stretch or cut a lease. Callers pass "now"
    /// in any kind and get local times back, like the rest of the app; only a lease's own *Utc fields stay in UTC.
    /// </summary>
    internal sealed class LeaseBook
    {
        public const int DefaultTtlSeconds = 1800, MaxTtlSeconds = 7200;
        /// <summary>Pool backoff after an agent's rate limit without Retry-After: a minute, the window of per-minute limits.</summary>
        public static readonly TimeSpan DefaultRateLimitBackoff = TimeSpan.FromSeconds(60);
        private const int SchemaVersion = 1, KeepDays = 30;

        private readonly string path;   // Null keeps the book in memory only, as in tests.
        private readonly object sync = new object();
        private readonly List<Lease> leases = new List<Lease>();
        private readonly List<Observation> observed = new List<Observation>();
        private readonly Dictionary<string, DateTime> backoff = new Dictionary<string, DateTime>();   // Pool to UTC time.

        public LeaseBook(string path)
        {
            this.path = path;
            Load();
        }

        /// <summary>
        /// Take a slot in a pool. capacity is the pool's room before leases (Candidates.Room); the check against active
        /// leases and the claim happen together. Returns null when the pool has no room left.
        /// </summary>
        public Lease Claim(string pool, string service, string model, string runtime, string client, int ttlSeconds, int capacity, DateTime now)
        {
            now = now.ToUniversalTime();
            lock (sync)
            {
                Expire(now);
                if (capacity - CountActive(pool, now) <= 0) return null;
                var lease = new Lease
                {
                    Id = "lease_" + Guid.NewGuid().ToString("N").Substring(0, 16), Pool = pool, Service = service, Model = model,
                    Runtime = runtime, Client = client, ClaimedAtUtc = now, ExpiresAtUtc = now.AddSeconds(Ttl(ttlSeconds)),
                };
                leases.Add(lease);
                Save();
                return lease;
            }
        }

        /// <summary>Move an active lease's expiry; the new expiry in local time, or null when the lease is unknown, released or expired.</summary>
        public DateTime? Renew(string id, int ttlSeconds, DateTime now)
        {
            now = now.ToUniversalTime();
            lock (sync)
            {
                Expire(now);
                var lease = leases.FirstOrDefault(l => l.Id == id);
                if (lease == null) return null;
                lease.ExpiresAtUtc = now.AddSeconds(Ttl(ttlSeconds));
                Save();
                return lease.ExpiresAtUtc.ToLocalTime();
            }
        }

        /// <summary>
        /// End a lease with its outcome. A rate limit puts the pool in backoff until retryAfter, or for a minute. Returns
        /// the lease, or null when it is unknown or already ended: a second release changes nothing.
        /// </summary>
        public Lease Release(string id, Outcome outcome, DateTime now)
        {
            now = now.ToUniversalTime();
            lock (sync)
            {
                Expire(now);
                var lease = leases.FirstOrDefault(l => l.Id == id);
                if (lease == null) return null;
                leases.Remove(lease);
                Record(lease.Pool, outcome, now);
                if (outcome.RateLimited > 0)
                {
                    var until = now + (outcome.RetryAfter ?? DefaultRateLimitBackoff);
                    DateTime current;
                    if (!backoff.TryGetValue(lease.Pool, out current) || until > current) backoff[lease.Pool] = until;
                }
                Save();
                return lease;
            }
        }

        public int Active(string pool, DateTime now)
        {
            lock (sync) return CountActive(pool, now.ToUniversalTime());
        }

        /// <summary>When the first active lease of the pool expires, in local time; null without active leases.</summary>
        public DateTime? EarliestExpiry(string pool, DateTime now)
        {
            now = now.ToUniversalTime();
            lock (sync)
            {
                var active = leases.Where(l => l.Pool == pool && l.ExpiresAtUtc > now).ToList();
                return active.Count == 0 ? (DateTime?)null : active.Min(l => l.ExpiresAtUtc).ToLocalTime();
            }
        }

        /// <summary>Until when (local time) an agent's rate limit keeps the pool closed; MinValue when it does not.</summary>
        public DateTime BackoffUntil(string pool)
        {
            DateTime t;
            lock (sync) return backoff.TryGetValue(pool, out t) ? t.ToLocalTime() : DateTime.MinValue;
        }

        /// <summary>Today's (UTC) outcome counters for a pool, with its leases in flight.</summary>
        public Observation Today(string pool, DateTime now)
        {
            now = now.ToUniversalTime();
            lock (sync)
            {
                Expire(now);
                var day = now.Date;
                var o = observed.FirstOrDefault(x => x.Pool == pool && x.Day == day);
                var copy = o == null ? new Observation { Pool = pool, Day = day } : o.Copy();
                if (copy.LastRateLimited.HasValue) copy.LastRateLimited = copy.LastRateLimited.Value.ToLocalTime();
                copy.InFlight = CountActive(pool, now);
                return copy;
            }
        }

        private static int Ttl(int seconds)
        {
            if (seconds <= 0) seconds = DefaultTtlSeconds;
            return Math.Min(seconds, MaxTtlSeconds);
        }

        /// <summary>Active leases of a pool at a UTC time. Called with the lock held.</summary>
        private int CountActive(string pool, DateTime now)
        {
            return leases.Count(l => l.Pool == pool && l.ExpiresAtUtc > now);
        }

        /// <summary>End leases past their expiry (now in UTC) with an unknown outcome. Called with the lock held.</summary>
        private void Expire(DateTime now)
        {
            var due = leases.Where(l => l.ExpiresAtUtc <= now).ToList();
            foreach (var l in due)
            {
                leases.Remove(l);
                Record(l.Pool, new Outcome { Result = "unknown" }, now);
            }
            if (due.Count > 0) Save();
        }

        /// <summary>Count an ended lease in its pool's row for the UTC day of now (UTC). Called with the lock held.</summary>
        private void Record(string pool, Outcome outcome, DateTime now)
        {
            var day = now.Date;
            var o = observed.FirstOrDefault(x => x.Pool == pool && x.Day == day);
            if (o == null) observed.Add(o = new Observation { Pool = pool, Day = day });
            o.Ended++;
            if (outcome.Result == "unknown") o.Unknown++;
            else if (outcome.Result == "failed" || outcome.Result == "timed_out") o.Failed++;
            if (outcome.Requests.HasValue) o.Requests += outcome.Requests.Value; else o.RequestsUnknown++;
            if (outcome.InputTokens.HasValue) o.InputTokens += outcome.InputTokens.Value;
            if (outcome.OutputTokens.HasValue) o.OutputTokens += outcome.OutputTokens.Value;
            if (outcome.Cost.HasValue) o.Cost += outcome.Cost.Value;
            if (outcome.RateLimited > 0) { o.RateLimited += outcome.RateLimited; o.LastRateLimited = now; }
            observed.RemoveAll(x => x.Day < day.AddDays(-KeepDays));
        }

        // ------------------------------------------------------------ storage

        private void Load()
        {
            if (path == null || !File.Exists(path)) return;
            try
            {
                var doc = Json.ParseFile(path);
                if (Json.Num(doc, "version") != SchemaVersion) return;   // An unknown version is left untouched and replaced.
                foreach (var l in Json.Items(doc, "leases"))
                    leases.Add(new Lease
                    {
                        Id = Json.Str(l, "id"), Pool = Json.Str(l, "pool"), Service = Json.Str(l, "service"), Model = Json.Str(l, "model"),
                        Runtime = Json.Str(l, "runtime"), Client = Json.Str(l, "client"),
                        ClaimedAtUtc = Utc(Json.Str(l, "claimed_at")) ?? DateTime.MinValue, ExpiresAtUtc = Utc(Json.Str(l, "expires_at")) ?? DateTime.MinValue,
                    });
                foreach (var o in Json.Items(doc, "observed"))
                    observed.Add(new Observation
                    {
                        Pool = Json.Str(o, "pool"), Day = Json.Day(Json.Str(o, "day")) ?? DateTime.MinValue,
                        Ended = (int)(Json.Num(o, "ended") ?? 0), Unknown = (int)(Json.Num(o, "unknown") ?? 0), Failed = (int)(Json.Num(o, "failed") ?? 0),
                        Requests = (long)(Json.Num(o, "requests") ?? 0), RequestsUnknown = (int)(Json.Num(o, "requests_unknown") ?? 0),
                        InputTokens = (long)(Json.Num(o, "input_tokens") ?? 0), OutputTokens = (long)(Json.Num(o, "output_tokens") ?? 0),
                        RateLimited = (int)(Json.Num(o, "rate_limited") ?? 0), LastRateLimited = Utc(Json.Str(o, "last_rate_limited")),
                        Cost = Json.Num(o, "cost") ?? 0,
                    });
                foreach (var b in Json.Items(doc, "backoff"))
                {
                    var until = Utc(Json.Str(b, "until"));
                    if (until.HasValue && Json.Str(b, "pool") != null) backoff[Json.Str(b, "pool")] = until.Value;
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        /// <summary>A saved time as UTC. Files written before times were kept in UTC carry a local offset, which this honours.</summary>
        private static DateTime? Utc(string iso)
        {
            DateTime t;
            if (string.IsNullOrEmpty(iso)) return null;
            if (!DateTime.TryParse(iso, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out t)) return null;
            return DateTime.SpecifyKind(t, DateTimeKind.Utc);
        }

        /// <summary>A UTC time in the round-trip format, ending in Z.</summary>
        private static string Iso(DateTime utc) { return utc.ToString("o", CultureInfo.InvariantCulture); }

        /// <summary>Write the book; a failure is logged and the book carries on in memory. Called with the lock held.</summary>
        private void Save()
        {
            if (path == null) return;
            var doc = new Dictionary<string, object>
            {
                { "version", SchemaVersion },
                { "leases", leases.Select(l => (object)new Dictionary<string, object>
                    {
                        { "id", l.Id }, { "pool", l.Pool }, { "service", l.Service }, { "model", l.Model }, { "runtime", l.Runtime },
                        { "client", l.Client }, { "claimed_at", Iso(l.ClaimedAtUtc) }, { "expires_at", Iso(l.ExpiresAtUtc) },
                    }).ToList() },
                { "observed", observed.Select(o => (object)new Dictionary<string, object>
                    {
                        { "pool", o.Pool }, { "day", o.Day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) }, { "ended", o.Ended }, { "unknown", o.Unknown },
                        { "failed", o.Failed }, { "requests", o.Requests }, { "requests_unknown", o.RequestsUnknown }, { "input_tokens", o.InputTokens },
                        { "output_tokens", o.OutputTokens }, { "rate_limited", o.RateLimited }, { "cost", o.Cost },
                        { "last_rate_limited", o.LastRateLimited.HasValue ? Iso(o.LastRateLimited.Value) : null },
                    }).ToList() },
                { "backoff", backoff.Where(kv => kv.Value > DateTime.UtcNow).Select(kv => (object)new Dictionary<string, object> { { "pool", kv.Key }, { "until", Iso(kv.Value) } }).ToList() },
            };
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                string temp = path + ".tmp";
                File.WriteAllText(temp, JsonText.Indented(doc), new UTF8Encoding(false));
                if (File.Exists(path)) File.Replace(temp, path, null);
                else File.Move(temp, path);
            }
            catch (IOException ex) { Program.LogError("leases", ex); }
            catch (UnauthorizedAccessException ex) { Program.LogError("leases", ex); }
        }

        internal sealed class Lease
        {
            public string Id, Pool, Service, Model, Runtime, Client;
            public DateTime ClaimedAtUtc, ExpiresAtUtc;
        }

        /// <summary>What a plugin reports when it releases a lease. Unknown values are null.</summary>
        internal sealed class Outcome
        {
            public string Result;   // completed, failed, cancelled, timed_out, or unknown for an expired lease.
            public long? Requests, InputTokens, OutputTokens;
            /// <summary>What the agent priced the task's requests at, in US dollars; counts against a free tier's budget.</summary>
            public double? Cost;
            public int RateLimited;
            public TimeSpan? RetryAfter;
        }

        /// <summary>One pool's outcome counters for one UTC day, from released and expired leases.</summary>
        internal sealed class Observation
        {
            public string Pool;
            public DateTime Day;
            public int Ended, Unknown, Failed, RequestsUnknown, RateLimited, InFlight;
            public long Requests, InputTokens, OutputTokens;
            public double Cost;                 // US dollars, as the agents priced the requests.
            public DateTime? LastRateLimited;   // UTC in the book; local in the copies Today returns.

            public Observation Copy() { return (Observation)MemberwiseClone(); }
        }
    }
}
