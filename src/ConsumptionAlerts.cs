using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace AIMeter
{
    /// <summary>A desktop notice for a newly opened episode, already claimed in the archive.</summary>
    internal sealed class ConsumptionNotice
    {
        internal string EpisodeId, Account, Title, Text;
        internal int Generation;
        internal DateTime EventUtc;
    }

    /// <summary>A session with recorded responses overlapping an episode. Overlap is not attribution.</summary>
    internal sealed class SessionCandidate
    {
        internal string SessionId, Agent, Model;
        internal DateTime First, Last;
        internal long Replies;
        internal long? Input, Output, CacheRead, CacheWrite;
        internal double? Cost;
    }

    internal sealed class AlertEpisode
    {
        internal const string Open = "open", AcknowledgedExpected = "expected", Resolved = "resolved";
        internal string Id, Scope, Provider, Account, Metric, State, Reason;
        internal DateTime OpenedUtc, LastUtc;
        internal DateTime? ResolvedUtc, NotifiedUtc;
        internal bool WasEligible;
        internal AnomalyEvidence Evidence;
        internal Dictionary<string, object> Baseline;
        internal List<SessionCandidate> Candidates = new List<SessionCandidate>();
    }

    /// <summary>
    /// Turns stored quota readings into consumption intervals and alert episodes, after each history batch.
    /// It adds no polling, provider requests or WSL access. Processing is incremental through a durable cursor
    /// over capture time, idempotent on replay, and transactional: an interrupted batch leaves no partial state.
    /// Episodes are recorded whether or not notifications are enabled; notices are claimed in the same
    /// transaction that opens an episode, so a restart cannot deliver the same initial notice twice.
    /// </summary>
    internal sealed class ConsumptionMonitor
    {
        internal const int ResolveAfterIntervals = 6;       // About 30 minutes of valid, ordinary readings.
        internal const double FreshMinutes = 20;            // Older intervals are stored but never notified.
        internal const double ReportingDelayMinutes = 5;    // Quota readings trail the responses that consumed it.
        private const int BatchLimit = 20000;
        private const string Cursor = "consumption-captured";

        private readonly HistoryStore store;
        private readonly Dictionary<string, BaselineSummary> references = new Dictionary<string, BaselineSummary>();
        private volatile int generation = -1;
        internal int Generation { get { return store.Generation; } }
        private List<ReadinessResult> readiness = new List<ReadinessResult>();

        internal ConsumptionMonitor(HistoryStore store) { this.store = store; }

        /// <summary>Readiness from the latest batch, for settings and the dashboard. Never touches the archive.</summary>
        internal List<ReadinessResult> Readiness { get { lock (references) return generation == store.Generation ? readiness : new List<ReadinessResult>(); } }

        /// <summary>Process readings recorded since the last call. Returns notices to display, at most one per account.</summary>
        /// <param name="notifySinceUtc">Notifications are enabled from this instant; null when they are off.</param>
        internal List<ConsumptionNotice> Process(DateTime nowUtc, DateTime? notifySinceUtc, TimeZoneInfo zone)
        {
            var notices = new List<ConsumptionNotice>();
            var ready = store.Transaction(db =>
            {
                if (generation != store.Generation)
                {
                    lock (references) references.Clear();
                    generation = store.Generation;
                }
                long cursor = MetadataLong(db, Cursor);
                var rows = db.Query(@"SELECT provider,account,metric,source,plan,allowance,currency,measured,captured,resets,percent,quality
                    FROM observations WHERE captured>? AND (percent IS NOT NULL OR quality IN ('unavailable','estimated'))
                    ORDER BY captured,measured LIMIT ?", cursor, BatchLimit);
                if (rows.Count == BatchLimit)
                {
                    // Never split one capture batch across calls.
                    long lastCaptured = Convert.ToInt64(rows[rows.Count - 1][8]);
                    if (rows.Any(r => Convert.ToInt64(r[8]) != lastCaptured)) rows = rows.Where(r => Convert.ToInt64(r[8]) != lastCaptured).ToList();
                }

                if (rows.Count > 0)
                {
                    long first = rows.Min(r => Convert.ToInt64(r[7]));
                    // Only collection pauses interrupt quota readings; other coverage rows describe transcript sources.
                    var coverage = db.Query("SELECT first,last FROM coverage WHERE reason='collection-paused' AND last>=?", first - (long)TimeSpan.FromMinutes(IntervalBuilder.MaxGapMinutes).TotalMilliseconds)
                        .Select(r => Tuple.Create(Convert.ToInt64(r[0]), Convert.ToInt64(r[1]))).ToList();
                    Func<DateTime, DateTime, bool> gap = (a, b) => coverage.Any(c => c.Item1 < HistoryStore.Stamp(b) && c.Item2 > HistoryStore.Stamp(a));
                    foreach (var group in rows.GroupBy(r => ScopeOf(r).Key))
                        ProcessScope(db, ScopeOf(group.First()), group.ToList(), gap, nowUtc, notifySinceUtc, zone, notices);
                    db.Execute("INSERT OR REPLACE INTO metadata VALUES(?,?)", Cursor, Convert.ToInt64(rows[rows.Count - 1][8]).ToString(CultureInfo.InvariantCulture));
                }

                // A finished quota cycle closes its episode explicitly, even without a new reading.
                foreach (var row in db.Query("SELECT id FROM consumption_episodes WHERE state<>? AND resets IS NOT NULL AND resets<?", AlertEpisode.Resolved, HistoryStore.Stamp(nowUtc)))
                    Resolve(db, (string)row[0], nowUtc, "reset");
                return AllReadiness(db, nowUtc, zone);
            });
            lock (references) readiness = ready;
            foreach (var notice in notices) notice.Generation = generation;
            return notices;
        }

        private void ProcessScope(HistoryDatabase db, ConsumptionScope scope, List<object[]> rows, Func<DateTime, DateTime, bool> gap,
            DateTime nowUtc, DateTime? notifySinceUtc, TimeZoneInfo zone, List<ConsumptionNotice> notices)
        {
            var state = db.Query("SELECT anchor_at,anchor_percent,anchor_resets,last_at,last_percent,last_resets,pending FROM consumption_scopes WHERE scope=?", scope.Key);
            if (state.Count == 0)
            {
                if (rows.All(r => r[10] == null)) return;
                db.Execute("INSERT INTO consumption_scopes(scope,provider,account,metric,source,plan,allowance,currency) VALUES(?,?,?,?,?,?,?,?)",
                    scope.Key, scope.Provider, scope.Account, scope.Metric, scope.Source, scope.Plan, scope.Allowance, scope.Currency);
                state = db.Query("SELECT anchor_at,anchor_percent,anchor_resets,last_at,last_percent,last_resets,pending FROM consumption_scopes WHERE scope=?", scope.Key);
            }
            var builder = new IntervalBuilder { Anchor = Reading(state[0], 0), Last = Reading(state[0], 3) };
            int pending = Convert.ToInt32(state[0][6]);
            var readings = rows.Select(r => new QuotaReading { MeasuredUtc = Utc(r[7]), ResetsUtc = r[9] == null ? (DateTime?)null : Utc(r[9]), Percent = Num(r[10]), Quality = (string)r[11] })
                .OrderBy(r => r.MeasuredUtc).ToList();

            if (builder.Last != null && readings[0].MeasuredUtc <= builder.Last.MeasuredUtc)
            {
                // A late reading rewrites the intervals around it. Rebuild this scope silently: no retrospective notices.
                builder = Rebuild(db, scope, nowUtc);
                lock (references) references.Clear();
                pending = 0;
            }
            else
            {
                foreach (var reading in readings)
                {
                    var interval = builder.Feed(reading, gap);
                    if (builder.ContinuityBroken)
                    {
                        pending = 0;
                        db.Execute("UPDATE consumption_episodes SET normal=0 WHERE scope=? AND state<>?", scope.Key, AlertEpisode.Resolved);
                    }
                    if (interval == null) continue;
                    Insert(db, scope, interval);
                    if (interval.EndUtc >= nowUtc.AddMinutes(-FreshMinutes))
                        pending = Evaluate(db, scope, interval, pending, nowUtc, notifySinceUtc, zone, notices);
                }
            }
            db.Execute("UPDATE consumption_scopes SET anchor_at=?,anchor_percent=?,anchor_resets=?,last_at=?,last_percent=?,last_resets=?,pending=? WHERE scope=?",
                Stamp(builder.Anchor), builder.Anchor == null ? null : builder.Anchor.Percent, Resets(builder.Anchor),
                Stamp(builder.Last), builder.Last == null ? null : builder.Last.Percent, Resets(builder.Last), pending, scope.Key);
        }

        private static IntervalBuilder Rebuild(HistoryDatabase db, ConsumptionScope scope, DateTime nowUtc)
        {
            db.Execute("DELETE FROM consumption_intervals WHERE scope=?", scope.Key);
            var builder = new IntervalBuilder();
            long from = HistoryStore.Stamp(nowUtc.AddDays(-ConsumptionAnalysis.HistoryDays - 1));
            var coverage = db.Query("SELECT first,last FROM coverage WHERE reason='collection-paused' AND last>=?", from).Select(r => Tuple.Create(Convert.ToInt64(r[0]), Convert.ToInt64(r[1]))).ToList();
            Func<DateTime, DateTime, bool> gap = (a, b) => coverage.Any(c => c.Item1 < HistoryStore.Stamp(b) && c.Item2 > HistoryStore.Stamp(a));
            db.Read(@"SELECT measured,resets,percent,quality FROM observations WHERE provider=? AND account=? AND metric=? AND source=? AND plan IS ? AND allowance IS ? AND currency IS ?
                AND measured>=? ORDER BY measured", row =>
            {
                var interval = builder.Feed(new QuotaReading { MeasuredUtc = Utc(row[0]), ResetsUtc = row[1] == null ? (DateTime?)null : Utc(row[1]), Percent = Num(row[2]), Quality = (string)row[3] }, gap);
                if (interval != null) Insert(db, scope, interval);
            }, scope.Provider, scope.Account, scope.Metric, scope.Source, scope.Plan, scope.Allowance, scope.Currency, from);
            return builder;
        }

        private static void Insert(HistoryDatabase db, ConsumptionScope scope, ConsumptionInterval i)
        {
            db.Execute("INSERT OR REPLACE INTO consumption_intervals VALUES(?,?,?,?,?,?)", scope.Key, HistoryStore.Stamp(i.StartUtc), HistoryStore.Stamp(i.EndUtc),
                i.Points, i.ResetsUtc.HasValue ? (object)HistoryStore.Stamp(i.ResetsUtc.Value) : null, ConsumptionAnalysis.AlgorithmVersion);
        }

        /// <summary>Advance the scope's episode state with one fresh interval. Returns the new count of weak candidates.</summary>
        private int Evaluate(HistoryDatabase db, ConsumptionScope scope, ConsumptionInterval interval, int pending,
            DateTime nowUtc, DateTime? notifySinceUtc, TimeZoneInfo zone, List<ConsumptionNotice> notices)
        {
            string referenceSince;
            var b = ReferenceFor(db, scope, interval.EndUtc, zone, out referenceSince);
            var open = db.Query("SELECT id,resets,normal,baseline FROM consumption_episodes WHERE scope=? AND state<>? ORDER BY opened DESC LIMIT 1", scope.Key, AlertEpisode.Resolved);
            if (open.Count > 0 && IntervalBuilder.SameCycle(open[0][1] == null ? (DateTime?)null : Utc(open[0][1]), interval.ResetsUtc))
                b = FrozenBaseline(Json.Parse((string)open[0][3]));
            var result = ConsumptionAnalysis.Readiness(scope, b, interval.EndUtc, nowUtc, ReferenceChanged(db, scope, interval.EndUtc, zone));
            // Learning, unavailable or changed references pause evaluation; they neither open nor resolve episodes.
            if (result.State != ReadinessState.Ready) return 0;

            var recent = db.Query("SELECT starts,ends,points,resets FROM consumption_intervals WHERE scope=? AND ends>? AND ends<=? ORDER BY ends",
                scope.Key, HistoryStore.Stamp(interval.EndUtc.AddMinutes(-60)), HistoryStore.Stamp(interval.EndUtc)).Select(Interval).ToList();
            var evidence = ConsumptionAnalysis.Sustained(recent, b) ?? ConsumptionAnalysis.Fast(interval, b);

            if (open.Count > 0)
            {
                string id = (string)open[0][0];
                DateTime? resets = open[0][1] == null ? (DateTime?)null : Utc(open[0][1]);
                if (IntervalBuilder.SameCycle(resets, interval.ResetsUtc))
                {
                    if (evidence != null)
                        db.Execute("UPDATE consumption_episodes SET last=?,evidence=?,normal=0 WHERE id=?", HistoryStore.Stamp(interval.EndUtc), Json.Serialize(evidence.ToJson()), id);
                    else if (Convert.ToInt32(open[0][2]) + 1 >= ResolveAfterIntervals) Resolve(db, id, interval.EndUtc, "normal");
                    else db.Execute("UPDATE consumption_episodes SET normal=normal+1 WHERE id=?", id);
                    return 0;
                }
                Resolve(db, id, interval.EndUtc, "reset");
            }

            if (evidence == null) return 0;
            // Strong single departures and sustained shifts open at once; weaker fast departures need persistence.
            if (!evidence.IsStrong && pending < 1) return pending + 1;
            Open(db, scope, interval, evidence, b, nowUtc, notifySinceUtc, notices);
            return 0;
        }

        private static void Open(HistoryDatabase db, ConsumptionScope scope, ConsumptionInterval interval, AnomalyEvidence evidence, BaselineSummary b,
            DateTime nowUtc, DateTime? notifySinceUtc, List<ConsumptionNotice> notices)
        {
            string id = HistoryStore.Hash(scope.Key + "|" + HistoryStore.Stamp(evidence.StartUtc)).Substring(0, 24);
            bool eligible = notifySinceUtc.HasValue && evidence.StartUtc >= notifySinceUtc.Value;
            // Overlapping limits of the same account share one notice; percentages are never summed.
            bool coalesced = db.Query("SELECT 1 FROM consumption_episodes WHERE provider=? AND account=? AND state<>? AND notified IS NOT NULL",
                scope.Provider, scope.Account, AlertEpisode.Resolved).Count > 0;
            bool notify = eligible && !coalesced && !notices.Any(n => n.Account == scope.Account);
            db.Execute("INSERT OR IGNORE INTO consumption_episodes(id,scope,provider,account,metric,resets,state,opened,last,evidence,baseline,version,eligible,notified,reason) VALUES(?,?,?,?,?,?,?,?,?,?,?,?,?,?,?)",
                id, scope.Key, scope.Provider, scope.Account, scope.Metric, interval.ResetsUtc.HasValue ? (object)HistoryStore.Stamp(interval.ResetsUtc.Value) : null,
                AlertEpisode.Open, HistoryStore.Stamp(evidence.StartUtc), HistoryStore.Stamp(interval.EndUtc), Json.Serialize(evidence.ToJson()), Json.Serialize(b.ToJson()),
                ConsumptionAnalysis.AlgorithmVersion, eligible, notify ? (object)HistoryStore.Stamp(nowUtc) : null, coalesced ? "coalesced" : null);
            if (!notify) return;
            notices.Add(new ConsumptionNotice
            {
                EpisodeId = id, Account = scope.Account, EventUtc = evidence.EndUtc,
                Title = L.F("{0} · {1}: unusual consumption", HistoryVisuals.ProviderName(scope.Provider), HistoryVisuals.MetricLabel(scope.Metric)),
                Text = Explain(evidence) + " " + L.T("Open the history to review possibly associated sessions."),
            });
        }

        /// <summary>The observed change and its comparison, in neutral terms that never claim a cause.</summary>
        internal static string Explain(AnomalyEvidence e)
        {
            string local = HistoryStore.Time(HistoryStore.Stamp(e.EndUtc)).ToString("t", L.Culture);
            return L.F("{0} points in {1} min up to {2} ({3}/h), unusual compared with your observed history: usually up to {4}/h.",
                Points(e.Points), Math.Round(e.Minutes).ToString(CultureInfo.InvariantCulture), local, Points(e.RatePerHour), Points(e.ThresholdPerHour));
        }

        private static string Points(double value) { return value.ToString(value >= 10 ? "0" : "0.#", L.Culture); }

        private static void Resolve(HistoryDatabase db, string id, DateTime atUtc, string reason)
        {
            db.Execute("UPDATE consumption_episodes SET state=?,resolved=?,reason=COALESCE(reason||','||?,?) WHERE id=? AND state<>?",
                AlertEpisode.Resolved, HistoryStore.Stamp(atUtc), reason, reason, id, AlertEpisode.Resolved);
        }

        /// <summary>Use the exact thresholds saved when the episode opened, including across restarts.</summary>
        private static BaselineSummary FrozenBaseline(Dictionary<string, object> json)
        {
            return new BaselineSummary
            {
                Context = Json.Str(json, "context"), TimeZone = Json.Str(json, "timeZone"),
                FromUtc = DateTime.Parse(Json.Str(json, "fromUtc"), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                ToUtc = DateTime.Parse(Json.Str(json, "toUtc"), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                ObservedDays = (int)(Json.Num(json, "observedDays") ?? 0), ActiveDays = (int)(Json.Num(json, "activeDays") ?? 0),
                ActiveIntervals = (int)(Json.Num(json, "activeIntervals") ?? 0), SustainedWindows = (int)(Json.Num(json, "sustainedWindows") ?? 0),
                MedianRate = Json.Num(json, "medianPointsPerHour") ?? 0, FastThreshold = Json.Num(json, "fastThresholdPointsPerHour") ?? 0,
                SustainedMedian = Json.Num(json, "sustainedMedianPointsPerHour") ?? 0, SustainedThreshold = Json.Num(json, "sustainedThresholdPointsPerHour") ?? 0,
            };
        }

        /// <summary>The reference for this scope and local day, cached until the day, zone or reference changes.</summary>
        private BaselineSummary ReferenceFor(HistoryDatabase db, ConsumptionScope scope, DateTime atUtc, TimeZoneInfo zone, out string since)
        {
            lock (references)
            {
                var row = db.Query("SELECT reference_since FROM consumption_scopes WHERE scope=?", scope.Key);
                since = row.Count == 0 || row[0][0] == null ? "" : Convert.ToString(row[0][0], CultureInfo.InvariantCulture);
                DateTime day = ConsumptionAnalysis.LocalDay(atUtc, zone);
                string key = scope.Key + "|" + day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + "|" + zone.Id + "|" + since + "|" + ConsumptionAnalysis.AlgorithmVersion;
                BaselineSummary b;
                if (references.TryGetValue(key, out b)) return b;
                if (references.Count > 200) references.Clear();

                // Learn only from completed prior local days, within the rolling window and the current reference.
                DateTime to = HistoryStore.Time(HistoryAnalysis.DayStart(day, zone)).ToUniversalTime();
                DateTime from = HistoryStore.Time(HistoryAnalysis.DayStart(day.AddDays(-ConsumptionAnalysis.HistoryDays), zone)).ToUniversalTime();
                if (since.Length > 0) from = new[] { from, Utc(since) }.Max();
                var completed = db.Query(@"SELECT starts,ends,points,resets FROM consumption_intervals i WHERE scope=? AND version=? AND starts>=? AND ends<?
                    AND NOT EXISTS(SELECT 1 FROM consumption_episodes e WHERE e.scope=i.scope AND e.opened<i.ends AND COALESCE(e.resolved,e.last)>i.starts)",
                    scope.Key, ConsumptionAnalysis.AlgorithmVersion, HistoryStore.Stamp(from), HistoryStore.Stamp(to)).Select(Interval).ToList();
                b = ConsumptionAnalysis.Reference(completed, zone, atUtc, from, to);
                references[key] = b;
                return b;
            }
        }

        /// <summary>Episodes opened on four of the previous seven days suggest the reference itself has changed.</summary>
        private static bool ReferenceChanged(HistoryDatabase db, ConsumptionScope scope, DateTime atUtc, TimeZoneInfo zone)
        {
            DateTime day = ConsumptionAnalysis.LocalDay(atUtc, zone);
            long from = HistoryAnalysis.DayStart(day.AddDays(-7), zone), to = HistoryAnalysis.DayStart(day, zone);
            var since = db.Query("SELECT reference_since FROM consumption_scopes WHERE scope=?", scope.Key);
            if (since.Count > 0 && since[0][0] != null) from = Math.Max(from, Convert.ToInt64(since[0][0]));
            int days = db.Query("SELECT opened FROM consumption_episodes WHERE scope=? AND opened>=? AND opened<?", scope.Key, from, to)
                .Select(r => ConsumptionAnalysis.LocalDay(Utc(r[0]), zone)).Distinct().Count();
            return days >= 4;
        }

        private List<ReadinessResult> AllReadiness(HistoryDatabase db, DateTime nowUtc, TimeZoneInfo zone)
        {
            var result = new List<ReadinessResult>();
            foreach (var row in db.Query("SELECT provider,account,metric,source,plan,allowance,currency,last_at FROM consumption_scopes ORDER BY provider,metric"))
            {
                var scope = new ConsumptionScope { Provider = (string)row[0], Account = (string)row[1], Metric = (string)row[2], Source = (string)row[3], Plan = row[4] as string, Allowance = Num(row[5]), Currency = row[6] as string };
                DateTime? last = row[7] == null ? (DateTime?)null : Utc(row[7]);
                string since;
                var b = ReferenceFor(db, scope, nowUtc, zone, out since);
                result.Add(ConsumptionAnalysis.Readiness(scope, b, last, nowUtc, ReferenceChanged(db, scope, nowUtc, zone)));
            }
            return result;
        }

        /// <summary>Silence an episode as expected. This neither whitelists sessions nor trains the reference.</summary>
        internal void Acknowledge(string id)
        {
            store.Transaction(db => { db.Execute("UPDATE consumption_episodes SET state=?,feedback='expected' WHERE id=? AND state=?", AlertEpisode.AcknowledgedExpected, id, AlertEpisode.Open); return 0; });
        }

        /// <summary>Start a new reference from now, keeping the previous start and the reason.</summary>
        internal void RebuildReference(string scopeKey, DateTime nowUtc)
        {
            store.Transaction(db =>
            {
                db.Execute("UPDATE consumption_scopes SET previous_since=reference_since,reference_since=?,reference_reason='rebuilt-after-change' WHERE scope=?", HistoryStore.Stamp(nowUtc), scopeKey);
                return 0;
            });
            lock (references) references.Clear();
        }

        /// <summary>Episodes overlapping a period, with sessions whose responses overlap each episode.</summary>
        internal List<AlertEpisode> Episodes(DateTime sinceUtc, string provider, int maximum)
        {
            return store.ReadConsistently(() =>
            {
                var episodes = store.Query("SELECT id,scope,provider,account,metric,state,opened,last,resolved,notified,eligible,evidence,baseline,reason FROM consumption_episodes WHERE last>=? AND (?='' OR provider=?) ORDER BY opened DESC LIMIT ?",
                    HistoryStore.Stamp(sinceUtc), provider, provider, maximum)
                    .Select(r => new AlertEpisode
                    {
                        Id = (string)r[0], Scope = (string)r[1], Provider = (string)r[2], Account = (string)r[3], Metric = (string)r[4], State = (string)r[5],
                        OpenedUtc = Utc(r[6]), LastUtc = Utc(r[7]), ResolvedUtc = r[8] == null ? (DateTime?)null : Utc(r[8]),
                        NotifiedUtc = r[9] == null ? (DateTime?)null : Utc(r[9]), WasEligible = Convert.ToInt64(r[10]) != 0,
                        Evidence = AnomalyEvidence.FromJson(Json.Parse((string)r[11])), Baseline = Json.Parse((string)r[12]), Reason = r[13] as string,
                    }).ToList();
                foreach (var e in episodes) e.Candidates = Candidates(e.Provider, e.Evidence.StartUtc, e.Evidence.EndUtc);
                return episodes;
            });
        }

        /// <summary>
        /// Sessions of the same provider with responses in the episode, allowing for reporting delay.
        /// Sessions carry no account scope, so a provider match cannot establish an account match.
        /// </summary>
        internal List<SessionCandidate> Candidates(string provider, DateTime startUtc, DateTime endUtc)
        {
            return store.Query(@"SELECT s.id,s.agent,MAX(e.model),MIN(e.at),MAX(e.at),COUNT(*),SUM(e.input),SUM(e.output),SUM(e.cache_read),SUM(e.cache_write),SUM(e.cost)
                FROM events e JOIN sessions s ON s.id=e.session WHERE s.provider=? AND e.at BETWEEN ? AND ? GROUP BY s.id ORDER BY MIN(e.at) LIMIT 6",
                provider, HistoryStore.Stamp(startUtc.AddMinutes(-ReportingDelayMinutes)), HistoryStore.Stamp(endUtc.AddMinutes(2)))
                .Select(r => new SessionCandidate
                {
                    SessionId = (string)r[0], Agent = (string)r[1], Model = r[2] as string, First = HistoryStore.Time(r[3]), Last = HistoryStore.Time(r[4]),
                    Replies = Convert.ToInt64(r[5]), Input = Long(r[6]), Output = Long(r[7]), CacheRead = Long(r[8]), CacheWrite = Long(r[9]), Cost = Num(r[10]),
                }).ToList();
        }

        private static ConsumptionScope ScopeOf(object[] r)
        {
            return new ConsumptionScope { Provider = (string)r[0], Account = (string)r[1], Metric = (string)r[2], Source = (string)r[3], Plan = r[4] as string, Allowance = Num(r[5]), Currency = r[6] as string };
        }

        private static ConsumptionInterval Interval(object[] r)
        {
            return new ConsumptionInterval { StartUtc = Utc(r[0]), EndUtc = Utc(r[1]), Points = Convert.ToDouble(r[2]), ResetsUtc = r[3] == null ? (DateTime?)null : Utc(r[3]) };
        }

        private static QuotaReading Reading(object[] row, int offset)
        {
            if (row[offset] == null) return null;
            return new QuotaReading { MeasuredUtc = Utc(row[offset]), Percent = Num(row[offset + 1]), ResetsUtc = row[offset + 2] == null ? (DateTime?)null : Utc(row[offset + 2]), Quality = "measured" };
        }

        private static long MetadataLong(HistoryDatabase db, string key)
        {
            var row = db.Query("SELECT value FROM metadata WHERE key=?", key);
            return row.Count == 0 ? 0 : Convert.ToInt64(row[0][0], CultureInfo.InvariantCulture);
        }

        private static DateTime Utc(object stamp) { return HistoryStore.Time(stamp).ToUniversalTime(); }
        private static object Stamp(QuotaReading r) { return r == null ? null : (object)HistoryStore.Stamp(r.MeasuredUtc); }
        private static object Resets(QuotaReading r) { return r == null || !r.ResetsUtc.HasValue ? null : (object)HistoryStore.Stamp(r.ResetsUtc.Value); }
        private static double? Num(object value) { return value == null ? (double?)null : Convert.ToDouble(value, CultureInfo.InvariantCulture); }
        private static long? Long(object value) { return value == null ? (long?)null : Convert.ToInt64(value); }
    }
}
