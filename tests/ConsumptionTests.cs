using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace AIMeter
{
    /// <summary>
    /// Consumption detector: interval validation, readiness, episodes, notification claims and a chronological replay.
    /// Fixtures are synthetic and deterministic; they test invariants and report replay figures, not real-world accuracy.
    /// </summary>
    internal static class ConsumptionTests
    {
        internal static void Register(Action<string, Action> test)
        {
            test("Consumption intervals reject gaps, resets, decreases and unusable readings", IntervalValidation);
            test("Consumption readiness explains learning until enough history exists", LearningReadiness);
            test("Ordinary history stays quiet while spikes and sustained shifts open one episode each", ReplayDetection);
            test("A restart cannot repeat a claimed consumption notice", RestartClaims);
            test("Consumption notices need opt-in and never replay a backlog", OptInOnly);
            test("Accounts never share a consumption reference", SeparateAccounts);
            test("Collection gaps and late readings never produce retrospective anomalies", GapsAndLateReadings);
            test("Expected episodes stay silent and resets close episodes", EpisodeTransitions);
            test("Overlapping sessions are candidates, never causes", SessionCandidates);
            test("Deleting history resets consumption readiness and episodes", DeleteConsumption);
            test("A version 1 archive upgrades in place", SchemaUpgrade);
            test("Consumption days follow daylight-saving rules", DaylightSaving);
            test("Consumption strings are translated in every supported language", Translations);
        }

        private static void Assert(bool condition, string message) { if (!condition) throw new Exception(message); }
        private static readonly DateTime Start = new DateTime(2026, 1, 5);   // A Monday, far from daylight-saving changes.

        private static HistoryStore Store()
        {
            var store = new HistoryStore(Path.Combine(Settings.Folder, "history.sqlite3"));
            store.Query("SELECT 1");
            return store;
        }

        // ------------------------------------------------------------ fixture generator

        private sealed class Segment { internal DateTime From, To; internal double PerHour; }

        /// <summary>
        /// A daily-reset limit read every 5–6.5 minutes from 08:00 to 23:00, reported as whole percentage points.
        /// Weekdays have two work sessions at about 6 points per hour; weekends have occasional lighter use.
        /// </summary>
        private sealed class Fixture
        {
            internal readonly List<Segment> Segments = new List<Segment>();
            internal readonly List<Tuple<DateTime, DateTime>> Labelled = new List<Tuple<DateTime, DateTime>>();
            internal string Account = "account-a", Source = "live";

            internal Fixture(int seed, int days)
            {
                var random = new Random(seed);
                for (int d = 0; d < days; d++)
                {
                    DateTime day = Start.AddDays(d);
                    bool weekend = day.DayOfWeek == DayOfWeek.Saturday || day.DayOfWeek == DayOfWeek.Sunday;
                    int sessions = weekend ? (random.NextDouble() < 0.5 ? 1 : 0) : 2;
                    for (int s = 0; s < sessions; s++)
                    {
                        DateTime from = day.AddHours(s == 0 ? 9 + random.NextDouble() * 2 : 14 + random.NextDouble() * 3);
                        Segments.Add(new Segment { From = from, To = from.AddHours(1.5 + random.NextDouble() * 1.5), PerHour = (weekend ? 3 : 5) + random.NextDouble() * 2 });
                    }
                }
            }

            internal void Inject(DateTime from, double hours, double perHour)
            {
                Segments.Add(new Segment { From = from, To = from.AddHours(hours), PerHour = perHour });
                Labelled.Add(Tuple.Create(from, from.AddHours(hours)));
            }

            /// <summary>Points consumed since local midnight at a local time, capped at 100.</summary>
            internal double Used(DateTime local)
            {
                DateTime midnight = local.Date;
                double total = 0;
                foreach (var s in Segments)
                {
                    DateTime a = s.From < midnight ? midnight : s.From, b = s.To > local ? local : s.To;
                    if (b > a) total += (b - a).TotalHours * s.PerHour;
                }
                return Math.Min(100, total);
            }

            internal IEnumerable<DateTime> Times(DateTime fromLocal, DateTime toLocal, int seed)
            {
                var random = new Random(seed);
                for (DateTime day = fromLocal.Date; day < toLocal; day = day.AddDays(1))
                    for (DateTime t = day.AddHours(8).AddSeconds(random.Next(0, 90)); t < day.AddHours(23); t = t.AddSeconds(300 + random.Next(0, 90)))
                        if (t >= fromLocal && t < toLocal) yield return t;
            }
        }

        private static void Insert(HistoryStore store, Fixture f, IEnumerable<DateTime> times)
        {
            store.Transaction(db =>
            {
                foreach (var t in times)
                    db.Execute("INSERT OR IGNORE INTO observations VALUES(?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?)", "fixture", f.Account, "Daily|", HistoryStore.Stamp(t), HistoryStore.Stamp(t),
                        HistoryStore.Stamp(t.Date.AddDays(1)), HistoryStore.Stamp(t.Date), Math.Floor(f.Used(t)), null, null, null, "measured", f.Source, "pro", null, null);
                return 0;
            });
        }

        /// <summary>Ingest whole days, processing once per day: stored but too old to notify.</summary>
        private static void Backfill(HistoryStore store, ConsumptionMonitor monitor, Fixture f, int fromDay, int toDay)
        {
            for (int d = fromDay; d < toDay; d++)
            {
                DateTime from = Start.AddDays(d), to = from.AddDays(1);
                Insert(store, f, f.Times(from, to, d));
                monitor.Process(to.ToUniversalTime(), null, TimeZoneInfo.Local);
            }
        }

        private sealed class Replay
        {
            internal readonly List<Tuple<DateTime, ConsumptionNotice>> Notices = new List<Tuple<DateTime, ConsumptionNotice>>();
            internal DateTime LastLocal;
        }

        /// <summary>Replay chronologically in 15-minute steps, as the collection worker would.</summary>
        private static Replay Run(HistoryStore store, ConsumptionMonitor monitor, Fixture f, int fromDay, int toDay, DateTime? notifySince)
        {
            var replay = new Replay();
            var times = f.Times(Start.AddDays(fromDay), Start.AddDays(toDay), 1000 + fromDay).ToList();
            int next = 0;
            for (DateTime step = Start.AddDays(fromDay).AddMinutes(15); step <= Start.AddDays(toDay); step = step.AddMinutes(15))
            {
                var batch = new List<DateTime>();
                while (next < times.Count && times[next] <= step) batch.Add(times[next++]);
                if (batch.Count == 0) continue;
                Insert(store, f, batch);
                foreach (var n in monitor.Process(step.ToUniversalTime(), notifySince, TimeZoneInfo.Local)) replay.Notices.Add(Tuple.Create(step, n));
                replay.LastLocal = step;
            }
            return replay;
        }

        // ------------------------------------------------------------ tests

        private static void IntervalValidation()
        {
            var b = new IntervalBuilder();
            DateTime t = new DateTime(2026, 1, 5, 9, 0, 0, DateTimeKind.Utc), reset = t.AddHours(10);
            Func<double, double, QuotaReading> at = (minutes, percent) => new QuotaReading { MeasuredUtc = t.AddMinutes(minutes), Percent = percent, ResetsUtc = reset, Quality = "measured" };
            Assert(b.Feed(at(0, 10), null) == null, "A single reading produced an interval.");
            Assert(b.Feed(at(3, 11), null) == null, "Three minutes produced an interval.");
            var i = b.Feed(at(6, 12), null);
            Assert(i != null && i.Points == 2 && i.Minutes == 6, "A valid six-minute interval was not produced.");
            Assert(b.Feed(at(25, 30), null) == null && b.Exclusions["gap"] == 1, "A 19-minute gap produced an interval.");
            Assert(b.Feed(at(31, 20), null) == null && b.Exclusions["decrease"] == 1, "A decrease produced an interval.");
            reset = reset.AddDays(1);
            Assert(b.Feed(at(37, 25), null) == null && b.Exclusions["reset"] == 1, "A reset crossing produced an interval.");
            Assert(b.Feed(new QuotaReading { MeasuredUtc = t.AddMinutes(43), Percent = 27, ResetsUtc = reset, Quality = "estimated" }, null) == null, "An estimate was accepted.");
            Assert(b.Feed(new QuotaReading { MeasuredUtc = t.AddMinutes(44), Percent = double.NaN, ResetsUtc = reset, Quality = "measured" }, null) == null, "A non-finite reading was accepted.");
            Assert(b.Exclusions["unusable-reading"] == 2, "Unusable readings were not recorded.");
            b.Feed(at(50, 30), null);
            Assert(b.Feed(at(56, 33), (x, y) => true) == null && b.Exclusions["collection-interrupted"] == 1, "An interval overlapping a collection gap was accepted.");
            Assert(b.Feed(at(56, 33), null) == null && b.Exclusions["out-of-order-or-duplicate"] == 1, "A duplicate reading was accepted.");
        }

        private static void LearningReadiness()
        {
            var store = Store(); var monitor = new ConsumptionMonitor(store); var f = new Fixture(1, 5);
            Backfill(store, monitor, f, 0, 5);
            var r = monitor.Readiness.Single();
            Assert(r.State == ReadinessState.Learning && r.Reason.Contains("observed days") && Convert.ToInt32(r.Arguments[0]) == 5, "Learning did not explain the missing observed days.");
            Assert(Convert.ToInt64(store.Query("SELECT COUNT(*) FROM consumption_episodes")[0][0]) == 0, "A learning scope opened an episode.");
        }

        private static int Count(HistoryStore store, string sql) { return Convert.ToInt32(store.Query(sql)[0][0]); }

        /// <summary>Four weeks of reference, then two held-out weeks with one spike and one sustained shift.</summary>
        private static void ReplayDetection()
        {
            var store = Store(); var monitor = new ConsumptionMonitor(store); var f = new Fixture(7, 42);
            DateTime spike = Start.AddDays(30).AddHours(16), sustained = Start.AddDays(37).AddHours(19);   // A Wednesday and a Wednesday evening.
            f.Inject(spike, 0.25, 48);
            f.Inject(sustained, 2, 16);
            Backfill(store, monitor, f, 0, 28);
            var ready = monitor.Readiness.Single();
            Assert(ready.State == ReadinessState.Ready || ready.State == ReadinessState.TemporarilyUnavailable, "Four weeks did not establish a reference: " + L.F(ready.Reason, ready.Arguments));

            var replay = Run(store, monitor, f, 28, 42, Start);
            var episodes = monitor.Episodes(DateTime.MinValue, "", 100);
            var delays = new List<double>();
            foreach (var label in f.Labelled)
            {
                var hit = episodes.FirstOrDefault(e => e.OpenedUtc <= label.Item2.ToUniversalTime() && e.LastUtc >= label.Item1.ToUniversalTime());
                Assert(hit != null, "A labelled departure at " + label.Item1.ToString("g", CultureInfo.InvariantCulture) + " was missed.");
                var notice = replay.Notices.FirstOrDefault(n => n.Item2.EpisodeId == hit.Id);
                Assert(notice != null, "A labelled episode was not notified.");
                delays.Add((notice.Item1 - label.Item1).TotalMinutes);
            }
            int unlabelled = episodes.Count(e => !f.Labelled.Any(l => e.OpenedUtc <= l.Item2.ToUniversalTime() && e.LastUtc >= l.Item1.ToUniversalTime()));
            int activeDays = f.Segments.Where(s => s.From >= Start.AddDays(28)).Select(s => s.From.Date).Distinct().Count();
            Assert(replay.Notices.Count == episodes.Count(e => e.NotifiedUtc.HasValue), "A notice was repeated within an episode.");
            Assert(unlabelled <= 2, "Ordinary held-out activity produced " + unlabelled + " episodes.");
            Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                "Consumption replay (synthetic, held-out 14 days): {0} labelled detected, delays {1} min; {2} unlabelled episodes over {3} active days ({4:0.00}/active day).",
                f.Labelled.Count, string.Join("/", delays.Select(d => d.ToString("0", CultureInfo.InvariantCulture))), unlabelled, activeDays, unlabelled / (double)activeDays));
        }

        private static ConsumptionMonitor Detected(HistoryStore store, out Fixture f, DateTime? notifySince)
        {
            var monitor = new ConsumptionMonitor(store); f = new Fixture(7, 31);
            f.Inject(Start.AddDays(30).AddHours(16), 0.25, 48);
            Backfill(store, monitor, f, 0, 28);
            Run(store, monitor, f, 28, 31, notifySince);
            return monitor;
        }

        private static void RestartClaims()
        {
            var store = Store(); Fixture f;
            Detected(store, out f, Start);
            int claimed = Count(store, "SELECT COUNT(*) FROM consumption_episodes WHERE notified IS NOT NULL");
            Assert(claimed >= 1, "The fixture did not claim a notice.");
            var restarted = new ConsumptionMonitor(new HistoryStore(store.FilePath));
            Assert(restarted.Process(Start.AddDays(31).ToUniversalTime(), Start, TimeZoneInfo.Local).Count == 0, "A restart produced a notice.");
            store.Transaction(db => { db.Execute("DELETE FROM metadata WHERE key='consumption-captured'"); return 0; });
            Assert(restarted.Process(Start.AddDays(31).ToUniversalTime(), Start, TimeZoneInfo.Local).Count == 0, "Reprocessing after a lost cursor repeated a notice.");
            Assert(Count(store, "SELECT COUNT(*) FROM consumption_episodes WHERE notified IS NOT NULL") == claimed, "Reprocessing duplicated an episode.");
        }

        private static void OptInOnly()
        {
            var store = Store(); Fixture f;
            var monitor = Detected(store, out f, null);
            Assert(Count(store, "SELECT COUNT(*) FROM consumption_episodes") >= 1, "Episodes were not recorded without notifications.");
            Assert(Count(store, "SELECT COUNT(*) FROM consumption_episodes WHERE notified IS NOT NULL OR eligible<>0") == 0, "An episode was claimed before opt-in.");
            var later = Start.AddDays(31);
            Assert(monitor.Process(later.ToUniversalTime(), later, TimeZoneInfo.Local).Count == 0, "Opting in replayed earlier episodes.");
        }

        private static void SeparateAccounts()
        {
            var store = Store(); var monitor = new ConsumptionMonitor(store);
            var a = new Fixture(3, 28);
            var b = new Fixture(4, 28) { Account = "account-b" };
            var u = new Fixture(5, 28) { Account = "unknown", Source = "live:unidentified-account" };
            // Capture order is chronological across accounts, as in the collection worker.
            for (int d = 0; d < 28; d++)
            {
                DateTime from = Start.AddDays(d), to = from.AddDays(1);
                Insert(store, a, a.Times(from, to, d)); Insert(store, u, u.Times(from, to, d));
                if (d >= 26) Insert(store, b, b.Times(from, to, d));
                monitor.Process(to.ToUniversalTime(), null, TimeZoneInfo.Local);
            }
            var states = monitor.Readiness.ToDictionary(r => r.Scope.Account);
            Assert(states["account-a"].State != ReadinessState.Learning, "The long-observed account did not become ready.");
            Assert(states["account-b"].State == ReadinessState.Learning, "A new account borrowed another account's reference.");
            Assert(states["unknown"].State == ReadinessState.TemporarilyUnavailable && states["unknown"].Reason.Contains("identified"), "An unidentified account was compared.");
        }

        private static void GapsAndLateReadings()
        {
            var store = Store(); var monitor = new ConsumptionMonitor(store); var f = new Fixture(7, 31);
            Backfill(store, monitor, f, 0, 28);
            DateTime day = Start.AddDays(29), before = day.AddHours(16), after = before.AddMinutes(40);
            Insert(store, f, f.Times(day, before, 1));
            monitor.Process(before.ToUniversalTime(), Start, TimeZoneInfo.Local);
            // Forty unobserved minutes during heavy use must not look like one fast interval.
            f.Segments.Add(new Segment { From = before, To = after, PerHour = 40 });
            store.Transaction(db => { HistoryStore.Gap(db, HistoryStore.Stamp(before), HistoryStore.Stamp(after), "collection-paused"); return 0; });
            Insert(store, f, f.Times(after, after.AddMinutes(30), 2));
            Assert(monitor.Process(after.AddMinutes(30).ToUniversalTime(), Start, TimeZoneInfo.Local).Count == 0, "A collection gap produced a notice.");
            Assert(Count(store, "SELECT COUNT(*) FROM consumption_intervals WHERE starts<" + HistoryStore.Stamp(after) + " AND ends>" + HistoryStore.Stamp(before)) == 0, "An interval spans the gap.");

            // Transcript coverage markers describe log sources, not quota collection.
            store.Transaction(db => { HistoryStore.Gap(db, HistoryStore.Stamp(Start), HistoryStore.Stamp(after.AddHours(2)), "initial-tail-only"); return 0; });
            int beforeMarker = Count(store, "SELECT COUNT(*) FROM consumption_intervals");
            Insert(store, f, f.Times(after.AddMinutes(30), after.AddMinutes(60), 3));
            monitor.Process(after.AddMinutes(60).ToUniversalTime(), Start, TimeZoneInfo.Local);
            Assert(Count(store, "SELECT COUNT(*) FROM consumption_intervals") > beforeMarker, "A transcript coverage marker interrupted quota intervals.");

            // A late reading inside an already processed period rebuilds silently.
            int intervals = Count(store, "SELECT COUNT(*) FROM consumption_intervals");
            store.Transaction(db =>
            {
                db.Execute("INSERT INTO observations VALUES(?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?)", "fixture", f.Account, "Daily|", HistoryStore.Stamp(day.AddHours(12).AddSeconds(7)),
                    HistoryStore.Stamp(after.AddMinutes(61)), HistoryStore.Stamp(day.AddDays(1)), HistoryStore.Stamp(day), 90.0, null, null, null, "measured", f.Source, "pro", null, null);
                return 0;
            });
            Assert(monitor.Process(after.AddMinutes(62).ToUniversalTime(), Start, TimeZoneInfo.Local).Count == 0, "A late reading produced a retrospective notice.");
            Assert(Count(store, "SELECT COUNT(*) FROM consumption_intervals") > 0 && Count(store, "SELECT COUNT(*) FROM consumption_intervals") <= intervals + 2, "The late reading did not rebuild intervals coherently.");
        }

        private static void EpisodeTransitions()
        {
            var store = Store(); Fixture f;
            var monitor = Detected(store, out f, Start);
            var episode = monitor.Episodes(DateTime.MinValue, "", 10).First();
            store.Transaction(db => { db.Execute("UPDATE consumption_episodes SET state='open',resolved=NULL WHERE id=?", episode.Id); return 0; });
            monitor.Acknowledge(episode.Id);
            Assert(monitor.Episodes(DateTime.MinValue, "", 10).First(e => e.Id == episode.Id).State == AlertEpisode.AcknowledgedExpected, "Acknowledging did not silence the episode.");
            var reference = monitor.Readiness.Single().Baseline.FastThreshold;
            monitor.Process(Start.AddDays(31).AddMinutes(1).ToUniversalTime(), Start, TimeZoneInfo.Local);
            Assert(monitor.Readiness.Single().Baseline.FastThreshold == reference, "Marking as expected changed the reference.");
            // The daily cycle ended at midnight: the episode closes explicitly.
            monitor.Process(Start.AddDays(31).AddHours(1).ToUniversalTime(), Start, TimeZoneInfo.Local);
            Assert(monitor.Episodes(DateTime.MinValue, "", 10).First(e => e.Id == episode.Id).State == AlertEpisode.Resolved, "A reset did not close the episode.");
        }

        private static void SessionCandidates()
        {
            var store = Store(); Fixture f;
            var monitor = Detected(store, out f, Start);
            var episode = monitor.Episodes(DateTime.MinValue, "", 10).First();
            Assert(episode.Candidates.Count == 0, "A candidate was invented without local sessions.");
            DateTime during = episode.Evidence.StartUtc.ToLocalTime().AddMinutes(2);
            store.Record(new Snapshot { Taken = during }, db =>
            {
                HistoryStore.AddEvent(db, new HistoryEvent { Id = "r1", SessionId = "s1", Provider = "fixture", Agent = "Agent", Model = "model", At = during, Input = 10, Output = 5, CacheRead = 100 });
                HistoryStore.AddEvent(db, new HistoryEvent { Id = "r2", SessionId = "s2", Provider = "other", Agent = "Agent", Model = "model", At = during, Input = 10 });
            });
            var c = monitor.Episodes(DateTime.MinValue, "", 10).First().Candidates;
            Assert(c.Count == 1 && c[0].Input == 10 && c[0].Output == 5 && c[0].CacheRead == 100 && c[0].CacheWrite == null, "Candidates mixed services or merged token measures.");
        }

        private static void DeleteConsumption()
        {
            var store = Store(); Fixture f;
            Detected(store, out f, Start);
            store.Clear();
            foreach (string table in new[] { "consumption_scopes", "consumption_intervals", "consumption_episodes" })
                Assert(Count(store, "SELECT COUNT(*) FROM " + table) == 0, "Deletion left " + table + ".");
            Assert(store.Query("SELECT 1 FROM metadata WHERE key='consumption-captured'").Count == 0, "Deletion kept the processing cursor.");
        }

        private static void SchemaUpgrade()
        {
            var store = Store();
            store.Record(new Snapshot { Taken = store.Started }, db => HistoryStore.AddEvent(db, new HistoryEvent { Id = "r", SessionId = "s", Provider = "p", Agent = "a", At = store.Started, Input = 1 }));
            using (var db = new HistoryDatabase(store.FilePath))
            {
                foreach (string table in new[] { "consumption_scopes", "consumption_intervals", "consumption_episodes" }) db.Execute("DROP TABLE " + table);
                db.Execute("PRAGMA user_version=1");
            }
            var upgraded = new HistoryStore(store.FilePath);
            Assert(Count(upgraded, "SELECT COUNT(*) FROM events") == 1 && Count(upgraded, "SELECT COUNT(*) FROM consumption_episodes") == 0, "The upgrade lost data or tables.");
            Assert(Convert.ToInt64(upgraded.Query("PRAGMA user_version")[0][0]) == HistoryStore.SchemaVersion, "The schema version was not advanced.");
        }

        private static void DaylightSaving()
        {
            var rule = TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(new DateTime(2020, 1, 1), new DateTime(2030, 12, 31), TimeSpan.FromHours(1),
                TimeZoneInfo.TransitionTime.CreateFixedDateRule(new DateTime(1, 1, 1, 2, 0, 0), 3, 29), TimeZoneInfo.TransitionTime.CreateFixedDateRule(new DateTime(1, 1, 1, 3, 0, 0), 10, 25));
            var zone = TimeZoneInfo.CreateCustomTimeZone("fixture", TimeSpan.FromHours(1), "fixture", "standard", "summer", new[] { rule });
            Assert(ConsumptionAnalysis.LocalDay(new DateTime(2026, 3, 28, 23, 30, 0, DateTimeKind.Utc), zone) == new DateTime(2026, 3, 29), "Summer time shifted a local day.");
            Assert(ConsumptionAnalysis.LocalDay(new DateTime(2026, 1, 10, 22, 30, 0, DateTimeKind.Utc), zone) == new DateTime(2026, 1, 10), "Standard time shifted a local day.");
            var intervals = new List<ConsumptionInterval>();
            for (int d = 0; d < 3; d++)
                intervals.Add(new ConsumptionInterval { StartUtc = new DateTime(2026, 3, 28 + d, 21, 55, 0, DateTimeKind.Utc), EndUtc = new DateTime(2026, 3, 28 + d, 22, 30, 0, DateTimeKind.Utc), Points = 1 });
            Assert(ConsumptionAnalysis.Summarise(intervals, zone, "all", DateTime.MinValue, DateTime.MaxValue).ObservedDays == 3, "Daylight saving merged or split observed days.");
        }

        private static void Translations()
        {
            try
            {
                foreach (string language in new[] { "it", "es", "fr", "de", "pt" })
                {
                    L.Init(language);
                    foreach (string key in new[] { "Notify unusual consumption", "Unusual consumption", "{0} · {1}: unusual consumption", "{0} of {1} observed days.", "Ready", "Learning", "This was expected", "Rebuild the reference" })
                        Assert(L.T(key) != key, "Missing consumption translation for " + language + ": " + key);
                }
            }
            finally { L.Init("en"); }
        }
    }
}
