using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace AIMeter
{
    internal static class HistoryRegressionTests
    {
        internal static void Register(Action<string, Action> test)
        {
            test("History commits source offsets and metrics atomically", AtomicBatch);
            test("History retains fresh readings once and rejects cached extras", FreshReadings);
            test("History separates credential identities and reset cycles", SeparateCycles);
            test("History preserves unknown counters instead of inventing zero", UnknownCounters);
            test("Incremental history resumes an incomplete UTF-8 line after restart", PartialLine);
            test("Oversized source lines do not stall later history", OversizedLine);
            test("Pi history deduplicates inherited replies across session forks", PiForks);
            test("Repeated Claude streaming events replace usage counters", StreamingClaude);
            test("Codex history avoids double counting cached input and repeated totals", CodexCounters);
            test("Feedback requests persist without converting silence into a rating", FeedbackClaims);
            test("Unrated sessions are excluded and coverage survives rating changes", RatingCoverage);
            test("History export is complete and excludes source content", SafeExport);
            test("Deletion clears metrics, ratings, offsets and previous baselines", DeleteHistory);
            test("History identifies collection interruptions without synthetic readings", CollectionGaps);
            test("Quota signals ignore resets, gaps and unidentified accounts", QuotaSignals);
            test("Unsupported newer history schemas are preserved", FutureSchema);
            test("History retains numeric balances and explicit spending quality", StandaloneAmounts);
            test("History preserves each extra response's measurement time", ExtraTimestamp);
            test("Invalid and extreme source data cannot interrupt collection", InvalidSourceData);
            test("Independent Pi replies without numeric timestamps remain distinct", IndependentPiReplies);
            test("Session totals respect the selected period", SelectedPeriod);
            test("Idle intervals cannot manufacture a quota anomaly baseline", IdleBaseline);
            test("History strings are translated in every supported language", Translations);
            test("Short collection pauses remain explicit in history", ShortPause);
            test("Resuming Windows cannot collect through a locked session", LockedResume);
            test("History projections and replay scale without loading source transcripts", ArchiveScale);
            test("Invalid provider numbers remain unavailable while unlimited is explicit", InvalidProviderNumbers);
            test("Daily history uses valid local dates across midnight", LocalDailyHistory);
            test("An empty filter is distinguished from a new archive", FilteredHistory);
            test("Comparison readiness requires active, account-scoped intervals", ComparisonReadiness);
            test("Daily boundaries include skipped and repeated local midnights", MidnightTransitions);
        }

        private static void Assert(bool condition, string message) { if (!condition) throw new Exception(message); }
        private static HistoryStore Store()
        {
            var store = new HistoryStore(Path.Combine(Settings.Folder, "history.sqlite3"));
            store.Query("SELECT 1");
            return store;
        }
        private static long Count(HistoryStore store, string table) { return Convert.ToInt64(store.Query("SELECT COUNT(*) FROM " + table)[0][0]); }
        private static Snapshot SnapshotAt(DateTime time) { return new Snapshot { Taken = time }; }
        private static HistoryEvent Event(string id, string session, DateTime at)
        {
            return new HistoryEvent { Id = id, SessionId = session, Provider = "fixture", Agent = "fixture", Model = "model", At = at, Input = 100, Output = 20 };
        }
        private static void Insert(HistoryStore store, params HistoryEvent[] items)
        {
            store.Record(SnapshotAt(items.Max(item => item.At)), db => { foreach (var item in items) HistoryStore.AddEvent(db, item); });
        }

        private static void AtomicBatch()
        {
            var store = Store(); bool failed = false;
            try
            {
                store.Record(SnapshotAt(store.Started), db =>
                {
                    HistoryStore.AddEvent(db, Event("reply", "session", store.Started));
                    db.Execute("INSERT INTO cursors VALUES(?,?,?)", "cursor", 100, 200);
                    throw new IOException("fixture interruption");
                });
            }
            catch (IOException) { failed = true; }
            Assert(failed && Count(store, "events") == 0 && Count(store, "cursors") == 0 && Count(store, "sessions") == 0, "An interrupted batch left committed metrics or offsets.");
            Insert(store, Event("reply", "session", store.Started));
            Assert(Count(store, "events") == 1, "Replay failed after rollback.");
        }

        private static Snapshot Quota(DateTime at, string account, DateTime reset, double used)
        {
            var snapshot = SnapshotAt(at);
            var usage = new Usage { At = at, Identity = account, Source = "live" };
            usage.Windows.Add(new UsageWindow { Label = "Week", Short = "7g", ResetsAt = reset, Used = used });
            snapshot.Providers.Add(new ProviderState { Id = "fixture", Usage = usage });
            return snapshot;
        }

        private static void FreshReadings()
        {
            var store = Store(); var at = store.Started.AddMinutes(1);
            var snapshot = Quota(at, "a", at.AddDays(1), 35);
            store.Record(snapshot, null); store.Record(snapshot, null);
            snapshot.Providers[0].Usage.Windows.Add(new UsageWindow { Label = "stale", Used = 12, CachedAt = at.AddDays(-1) });
            snapshot.Providers[0].Usage.Source = "cache"; store.Record(snapshot, null);
            Assert(Count(store, "observations") == 1, "A repeated or cached reading became a new observation.");
        }

        private static void SeparateCycles()
        {
            var store = Store(); var at = store.Started.AddMinutes(1); var reset = at.AddDays(1);
            store.Record(Quota(at, "a", reset, 90), null);
            store.Record(Quota(at.AddMinutes(5), "b", reset, 5), null);
            store.Record(Quota(at.AddMinutes(10), "b", reset.AddDays(7), 2), null);
            var report = new HistoryAnalysis(store).Read(DateTime.MinValue, "");
            Assert(report.Cycles.Count == 3, "Accounts or cycles were combined.");
            Assert(!Convert.ToString(store.Query("SELECT account FROM observations LIMIT 1")[0][0]).Equals("a"), "Credential identity was retained verbatim.");
        }

        private static void UnknownCounters()
        {
            var store = Store(); var item = Event("reply", "session", store.Started); item.Input = null; item.Output = 0;
            Insert(store, item);
            var session = store.Sessions(DateTime.MinValue, "", 10)[0];
            Assert(session.Input == null && session.Output == 0 && session.Cost == null && session.CacheRead == null, "Unknown values became zero.");
        }

        private static string Claude(DateTime at, string id, long output)
        {
            return Json.Serialize(new { type = "assistant", timestamp = at.ToUniversalTime().ToString("o"), requestId = "request-" + id,
                message = new { id = id, model = "model", content = new[] { new { type = "text", text = "PRIVATE SOURCE SENTINEL" } }, usage = new { input_tokens = 100, output_tokens = output, cache_read_input_tokens = 50 } } });
        }

        private static void PartialLine()
        {
            var store = Store(); var at = store.Started.AddMinutes(1); string path = Path.Combine(Settings.Folder, "source.jsonl");
            string line = Claude(at, "reply", 42).Replace("model", "mödèl");
            byte[] bytes = Encoding.UTF8.GetBytes(line + "\n"); int split = Array.IndexOf(bytes, (byte)0xC3) + 1;
            File.WriteAllBytes(path, bytes.Take(split).ToArray());
            var reader = new HistoryCollector(store);
            store.Record(SnapshotAt(at), db => reader.ReadFile(db, new FileInfo(path), "Claude Code", at));
            Assert(Count(store, "events") == 0, "An incomplete line was imported.");
            using (var stream = new FileStream(path, FileMode.Append)) stream.Write(bytes, split, bytes.Length - split);
            store = new HistoryStore(store.FilePath); reader = new HistoryCollector(store);
            store.Record(SnapshotAt(at), db => reader.ReadFile(db, new FileInfo(path), "Claude Code", at));
            Assert(Count(store, "events") == 1 && Count(store, "sessions") == 1, "A split UTF-8 line was lost across restart.");
        }

        private static void OversizedLine()
        {
            var store = Store(); var at = store.Started.AddMinutes(1); string path = Path.Combine(Settings.Folder, "source.jsonl");
            File.WriteAllText(path, "partial", new UTF8Encoding(false));
            var reader = new HistoryCollector(store);
            store.Record(SnapshotAt(at), db => reader.ReadFile(db, new FileInfo(path), "Claude Code", at));
            File.WriteAllText(path, new string('x', 3 * 1024 * 1024) + "\n" + Claude(at, "after", 20) + "\n", new UTF8Encoding(false));
            for (int i = 0; i < 3; i++) store.Record(SnapshotAt(at), db => reader.ReadFile(db, new FileInfo(path), "Claude Code", at));
            Assert(Count(store, "events") == 1 && Count(store, "coverage") > 0, "A large line stalled the incremental reader.");
        }

        private static void PiForks()
        {
            var store = Store(); var at = store.Started.AddMinutes(1);
            string line = Json.Serialize(new { type = "message", timestamp = at.ToUniversalTime().ToString("o"),
                message = new { role = "assistant", provider = "fixture", model = "model", timestamp = (at.ToUniversalTime() - new DateTime(1970, 1, 1)).TotalMilliseconds,
                    content = new[] { new { text = "PRIVATE SOURCE SENTINEL" } }, usage = new { input = 12, output = 3, cacheRead = 100, cacheWrite = 0, cost = new { total = 0.25 } } } });
            var original = HistoryCollector.Parse(line, "Pi", "original"); var copy = HistoryCollector.Parse(line, "Pi", "fork");
            Insert(store, original, copy);
            Assert(Count(store, "events") == 1 && Count(store, "sessions") == 1, "Inherited reply was counted twice or created an empty fork.");
            Assert(store.Sessions(DateTime.MinValue, "", 10)[0].Cost == 0.25, "Cost was duplicated.");
        }

        private static void StreamingClaude()
        {
            var store = Store(); var at = store.Started.AddMinutes(1);
            Insert(store, HistoryCollector.Parse(Claude(at, "stream", 1), "Claude Code", "session"));
            Insert(store, HistoryCollector.Parse(Claude(at, "stream", 120), "Claude Code", "session"));
            Assert(Count(store, "events") == 1 && store.Sessions(DateTime.MinValue, "", 10)[0].Output == 120, "Streaming updates accumulated instead of replacing counters.");
        }

        private static void CodexCounters()
        {
            var store = Store(); var at = store.Started.AddMinutes(1);
            string line = Json.Serialize(new { type = "event_msg", timestamp = at.ToUniversalTime().ToString("o"), payload = new { type = "token_count",
                info = new { last_token_usage = new { input_tokens = 500, cached_input_tokens = 300, output_tokens = 100 }, total_token_usage = new { input_tokens = 1500, output_tokens = 200 } } } });
            var item = HistoryCollector.Parse(line, "Codex", "session");
            Assert(item.Input == 200 && item.CacheRead == 300 && item.Output == 100, "Cached input was counted twice.");
            Insert(store, item, HistoryCollector.Parse(line.Replace(at.ToUniversalTime().ToString("o"), at.AddSeconds(2).ToUniversalTime().ToString("o")), "Codex", "session"));
            Assert(Count(store, "events") == 1, "Repeated cumulative totals were counted twice.");
        }

        private static void FeedbackClaims()
        {
            var store = Store(); var at = store.Started.AddMinutes(1);
            Insert(store, Event("r1", "s1", at), Event("r2", "s2", at.AddMinutes(1)));
            Assert(store.ClaimFeedback(at.AddMinutes(20), store.Started) == null, "Prompt interrupted an active session.");
            string session = store.ClaimFeedback(at.AddMinutes(35), store.Started);
            Assert(session != null, "An idle session was not eligible.");
            store = new HistoryStore(store.FilePath);
            Assert(store.ClaimFeedback(at.AddMinutes(40), at) == null, "Restart bypassed notification cooldown.");
            Assert(store.Sessions(DateTime.MinValue, "", 10).All(item => !item.Feedback.HasValue), "Ignoring a prompt created a negative rating.");
            Assert(store.ClaimFeedback(at.AddHours(4), at) == null, "A stale backlog generated a feedback prompt.");
        }

        private static void RatingCoverage()
        {
            var store = Store(); var at = store.Started.AddMinutes(1);
            Insert(store, Event("r1", "s1", at), Event("r2", "s2", at)); store.Rate("s1", 1);
            var analysis = new HistoryAnalysis(store); var report = analysis.Read(DateTime.MinValue, "");
            Assert(report.Rated == 1 && report.SessionCount == 2 && report.Positive == 1 && report.Negative == 0, "Coverage included unrated sessions in the rating denominator.");
            store.Rate("s1", -1); store.Rate("s1", null);
            Assert(analysis.Read(DateTime.MinValue, "").Rated == 0, "Clearing a rating left it in statistics.");
            bool rejected = false; try { store.Rate("s1", 2); } catch (ArgumentOutOfRangeException) { rejected = true; }
            Assert(rejected, "Invalid feedback was accepted.");
        }

        private static void SafeExport()
        {
            var store = Store(); var at = store.Started.AddMinutes(1);
            Insert(store, HistoryCollector.Parse(Claude(at, "reply", 42), "Claude Code", "session"));
            string path = Path.Combine(Settings.Folder, "export.jsonl"); store.Export(path);
            string text = File.ReadAllText(path);
            Assert(!text.Contains("PRIVATE SOURCE SENTINEL") && !text.Contains("request-reply") && !text.Contains("source.jsonl"), "The export leaked source content or raw identifiers.");
            var records = File.ReadAllLines(path).Select(Json.Parse).ToList();
            Assert(records.Count(row => Json.Str(row, "table") == "events") == 1 && records.Count(row => Json.Str(row, "table") == "sessions") == 1, "Export omitted retained data.");
            Assert(text.Contains("ai-meter-history") && text.Contains("output"), "Export is not self describing.");
        }

        private static void DeleteHistory()
        {
            var store = Store(); Insert(store, Event("r1", "s1", store.Started)); store.Rate("s1", 1); store.Clear();
            Assert(Count(store, "events") == 0 && Count(store, "sessions") == 0 && Count(store, "observations") == 0 && Count(store, "cursors") == 0, "Deletion left recoverable history in active tables.");
        }

        private static void CollectionGaps()
        {
            var store = Store(); var at = store.Started;
            store.Record(SnapshotAt(at), null); store.Record(SnapshotAt(at.AddHours(2)), null);
            Assert(Count(store, "coverage") == 1 && Count(store, "observations") == 0, "An interruption was hidden or filled with synthetic readings.");
        }

        private static void QuotaSignals()
        {
            var at = DateTime.Now; long reset = HistoryStore.Stamp(at.AddDays(1)); var rows = new List<object[]>();
            for (int i = 0; i < 12; i++) rows.Add(new object[] { "fixture", "account", "Week|", reset, HistoryStore.Stamp(at.AddMinutes(i * 5)), (double)i, "live" });
            rows.Add(new object[] { "fixture", "account", "Week|", reset, HistoryStore.Stamp(at.AddMinutes(60)), 30.0, "live" });
            Assert(HistoryAnalysis.Signals(rows).Count == 1, "A clear rate spike was missed.");
            rows[rows.Count - 1][3] = reset + 1;
            Assert(HistoryAnalysis.Signals(rows).Count == 0, "A reset became an anomaly.");
            rows[rows.Count - 1][3] = reset; rows[rows.Count - 1][4] = HistoryStore.Stamp(at.AddHours(4));
            Assert(HistoryAnalysis.Signals(rows).Count == 0, "A collection gap became an anomaly.");
            foreach (var row in rows) row[6] = "live:unidentified-account";
            Assert(HistoryAnalysis.Signals(rows).Count == 0, "Unknown accounts were compared.");
        }

        private static void FutureSchema()
        {
            var store = Store();
            using (var database = new HistoryDatabase(store.FilePath)) database.Execute("PRAGMA user_version=999");
            bool rejected = false;
            try { new HistoryStore(store.FilePath).Query("SELECT 1"); } catch (IOException) { rejected = true; }
            Assert(rejected && File.Exists(store.FilePath), "A future archive was overwritten or silently accepted.");
        }

        private static void StandaloneAmounts()
        {
            var store = Store(); var at = store.Started.AddMinutes(1); var snapshot = Quota(at, "a", at.AddDays(1), 1);
            snapshot.Providers[0].Usage.Windows.Add(new UsageWindow { Label = "Balance", NoBar = true, Amount = 150, AmountUnit = "EUR" });
            snapshot.Providers[0].Usage.Windows.Add(new UsageWindow { Label = "Spent today", NoBar = true, Amount = 2.5, AmountUnit = "EUR", IsEstimated = true });
            store.Record(snapshot, null);
            var rows = store.Query("SELECT metric,amount,unit,quality,percent FROM observations WHERE unit IS NOT NULL ORDER BY metric");
            Assert(rows.Count == 2 && Convert.ToDouble(rows[0][1]) == 150 && (string)rows[0][3] == "measured" && (string)rows[1][3] == "estimated" && rows[0][4] == null, "Standalone amounts lost their numeric value, unit or quality.");
            var report = new HistoryAnalysis(store).Read(DateTime.MinValue, "");
            Assert(report.Amounts.Count == 2, "The dashboard omitted spending and balances.");
            bool rejected = false;
            try { store.Export(store.FilePath); } catch (IOException) { rejected = true; }
            Assert(rejected && Count(store, "observations") == 3, "Export overwrote the active database.");
        }

        private static void ExtraTimestamp()
        {
            var store = Store(); var at = store.Started.AddMinutes(10); var snapshot = Quota(at, "a", at.AddDays(1), 1);
            snapshot.Providers[0].Usage.Windows.Add(new UsageWindow { Label = "Extra", MeasuredAt = at.AddMinutes(-5), Amount = 25, AmountUnit = "USD", NoBar = true });
            store.Record(snapshot, null);
            snapshot.Providers[0].Usage.At = at.AddMinutes(1); snapshot.Taken = at.AddMinutes(1); store.Record(snapshot, null);
            var rows = store.Query("SELECT measured FROM observations WHERE metric='Extra|'");
            Assert(rows.Count == 1 && Convert.ToInt64(rows[0][0]) == HistoryStore.Stamp(at.AddMinutes(-5)), "An unchanged extra response acquired the main response's timestamp.");
        }

        private static void InvalidSourceData()
        {
            foreach (string line in new[] { "", "null", "{}", "[1]", "{", "{\"type\":\"message\",\"message\":{\"role\":\"assistant\",\"timestamp\":1e100,\"usage\":{\"input\":1}}}", "{\"type\":\"message\",\"message\":{\"role\":\"assistant\",\"usage\":{\"input\":-1,\"output\":1e100}}}" })
                Assert(HistoryCollector.Parse(line, "Pi", "session") == null, "Invalid source produced a valid usage event.");
        }

        private static void IndependentPiReplies()
        {
            var at = DateTime.Now;
            string line = "{\"type\":\"message\",\"timestamp\":\"" + at.ToUniversalTime().ToString("o") + "\",\"message\":{\"role\":\"assistant\",\"provider\":\"fixture\",\"model\":\"model\",\"usage\":{\"input\":5}}}";
            var first = HistoryCollector.Parse(line, "Pi", "session");
            var second = HistoryCollector.Parse(line.Replace(at.ToUniversalTime().ToString("o"), at.AddSeconds(1).ToUniversalTime().ToString("o")), "Pi", "session");
            Assert(first.Id != second.Id, "Independent replies without message.timestamp were collapsed.");
        }

        private static void SelectedPeriod()
        {
            var store = Store(); var at = store.Started.AddMinutes(1);
            Insert(store, Event("older", "session", at), Event("newer", "session", at.AddHours(1)));
            var session = store.Sessions(at.AddMinutes(30), "", 10)[0];
            Assert(session.Replies == 1 && session.Input == 100, "Session totals included responses outside the selected period.");
        }

        private static void IdleBaseline()
        {
            var at = DateTime.Now; var rows = new List<object[]>(); long reset = HistoryStore.Stamp(at.AddDays(1));
            for (int i = 0; i < 12; i++) rows.Add(new object[] { "fixture", "account", "Week|", reset, HistoryStore.Stamp(at.AddMinutes(i * 5)), 0.0, "live" });
            rows.Add(new object[] { "fixture", "account", "Week|", reset, HistoryStore.Stamp(at.AddMinutes(60)), 8.0, "live" });
            Assert(HistoryAnalysis.Signals(rows).Count == 0, "Ordinary activity after idle time became an anomaly.");
        }

        private static void Translations()
        {
            try
            {
                foreach (string language in new[] { "it", "es", "fr", "de", "pt" })
                {
                    L.Init(language);
                    foreach (string key in new[] { "Usage history", "Value for consumption", "Was the result worth this session's consumption?", "Rate the useful work obtained relative to the quota or money consumed.", "Not rated", "Clear rating" })
                        Assert(L.T(key) != key, "Missing history translation for " + language + ": " + key);
                }
            }
            finally { L.Init("en"); }
        }

        private static void ShortPause()
        {
            var store = Store(); var at = store.Started;
            store.Record(SnapshotAt(at), null); store.Record(SnapshotAt(at.AddMinutes(2)), null, at.AddSeconds(15));
            Assert(Count(store, "coverage") == 1, "A pause shorter than the heartbeat threshold was hidden.");
        }

        private static void LockedResume()
        {
            var controller = new Controller(false, null, null);
            const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
            var locked = typeof(Controller).GetField("sessionLocked", flags);
            var suspended = typeof(Controller).GetField("suspended", flags);
            var paused = typeof(Controller).GetField("paused", flags);
            var update = typeof(Controller).GetMethod("UpdateCollectionPause", flags);
            locked.SetValue(controller, true); suspended.SetValue(controller, true); update.Invoke(controller, null);
            suspended.SetValue(controller, false); update.Invoke(controller, null);
            Assert((bool)paused.GetValue(controller), "Resume enabled collection whilst the session was still locked.");
            locked.SetValue(controller, false); update.Invoke(controller, null);
            Assert(!(bool)paused.GetValue(controller), "Unlock did not resume collection.");
        }

        private static void ArchiveScale()
        {
            var store = Store(); var at = store.Started.AddMinutes(1); var snapshot = SnapshotAt(at.AddMinutes(10));
            var timer = System.Diagnostics.Stopwatch.StartNew();
            Action<HistoryDatabase> capture = db =>
            {
                for (int i = 0; i < 2000; i++) HistoryStore.AddEvent(db, Event("r" + i, "s" + i / 20, at.AddMilliseconds(i)));
            };
            store.Record(snapshot, capture); long captureMs = timer.ElapsedMilliseconds;
            timer.Restart(); store.Record(snapshot, capture); long replayMs = timer.ElapsedMilliseconds;
            timer.Restart(); var report = new HistoryAnalysis(store).Read(DateTime.MinValue, "");
            Assert(report.Events == 2000 && report.SessionCount == 100 && report.Models.Count == 1 && report.Days.Count == 1, "A larger archive produced inconsistent projections or duplicate replay data.");
            Console.WriteLine("History fixture: 2,000 events / 100 sessions; capture " + captureMs + " ms; replay " + replayMs + " ms; projection " + timer.ElapsedMilliseconds + " ms; archive " + store.SizeBytes + " bytes.");
        }

        private static void InvalidProviderNumbers()
        {
            var store = Store(); var at = store.Started.AddMinutes(1); var snapshot = Quota(at, "a", at.AddDays(1), double.NaN);
            snapshot.Providers[0].Usage.Windows.Add(new UsageWindow { Label = "Amount", NoBar = true, Amount = double.PositiveInfinity, IsEstimated = true });
            snapshot.Providers[0].Usage.Windows.Add(new UsageWindow { Label = "Unlimited", RightText = "unlimited" });
            store.Record(snapshot, null);
            var rows = store.Query("SELECT quality,percent,amount FROM observations ORDER BY metric");
            Assert((string)rows[0][0] == "unavailable" && (string)rows[1][0] == "unlimited" && (string)rows[2][0] == "unavailable", "Invalid numbers or unlimited status became a measured zero.");
            Assert(rows.All(row => row[1] == null && row[2] == null), "Non-finite values reached numeric history columns.");
        }

        private static void LocalDailyHistory()
        {
            var store = Store(); var midnight = store.Started.Date.AddDays(1);
            Insert(store, Event("before", "session", midnight.AddMinutes(-1)), Event("after", "session", midnight.AddMinutes(1)));
            var report = new HistoryAnalysis(store).Read(DateTime.MinValue, "");
            Assert(report.Days.Count == 2, "Local midnight did not split daily activity.");
            Assert((string)report.Days[0][0] == midnight.AddDays(-1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
                && (string)report.Days[1][0] == midnight.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), "Daily chart dates were null or used UTC boundaries.");
            Assert(report.Days.Sum(row => Convert.ToInt64(row[1])) == report.Events, "Daily groups lost or duplicated responses.");
        }

        private static void FilteredHistory()
        {
            var store = Store(); var analysis = new HistoryAnalysis(store);
            Assert(!analysis.Read(DateTime.MinValue, "").HasAnyHistory, "A new archive was marked populated.");
            Insert(store, Event("reply", "session", store.Started));
            var filtered = analysis.Read(DateTime.MinValue, "another-provider");
            Assert(filtered.HasAnyHistory && filtered.Events == 0 && filtered.SessionCount == 0, "A filter with no matches looked like an empty archive.");
        }

        private static void ComparisonReadiness()
        {
            var at = DateTime.Now; long reset = HistoryStore.Stamp(at.AddDays(1)); var rows = new List<object[]>();
            for (int i = 0; i < 9; i++) rows.Add(new object[] { "fixture", "account", "Week|", reset, HistoryStore.Stamp(at.AddMinutes(i * 5)), (double)i, "live" });
            Assert(HistoryAnalysis.Compare(rows.Take(2).ToList()).Intervals == 1, "A first interval claimed a complete comparison baseline.");
            Assert(HistoryAnalysis.Compare(rows).Intervals == 8, "Active intervals did not establish comparison readiness.");
            foreach (var row in rows) row[6] = "live:unidentified-account";
            Assert(HistoryAnalysis.Compare(rows).Intervals == 0, "Unidentified accounts established a comparison baseline.");
        }

        private static void MidnightTransitions()
        {
            var spring = TimeZoneInfo.TransitionTime.CreateFixedDateRule(new DateTime(1, 1, 1, 0, 0, 0), 3, 31);
            var autumn = TimeZoneInfo.TransitionTime.CreateFixedDateRule(new DateTime(1, 1, 1, 1, 0, 0), 10, 31);
            var rule = TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(new DateTime(2020, 1, 1), new DateTime(2030, 12, 31), TimeSpan.FromHours(1), spring, autumn);
            var zone = TimeZoneInfo.CreateCustomTimeZone("fixture", TimeSpan.FromHours(-5), "fixture", "standard", "summer", new[] { rule });
            var repeated = new DateTime(2026, 10, 31); var skipped = new DateTime(2026, 3, 31);
            long hour = (long)TimeSpan.FromHours(1).TotalMilliseconds;
            Assert(HistoryAnalysis.DayStart(repeated.AddDays(1), zone) - HistoryAnalysis.DayStart(repeated, zone) == 25 * hour, "A repeated midnight excluded its first hour.");
            Assert(HistoryAnalysis.DayStart(skipped.AddDays(1), zone) - HistoryAnalysis.DayStart(skipped, zone) == 23 * hour, "A skipped midnight invented an extra hour.");
        }
    }
}
