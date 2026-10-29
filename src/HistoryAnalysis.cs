using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace AIMeter
{
    internal sealed class HistoryReport
    {
        internal DateTime Started;
        internal long Bytes, Observations, Events, Gaps, SessionCount, Rated, Positive, Neutral, Negative;
        internal bool HasAnyHistory;
        internal List<HistorySession> Sessions;
        internal List<object[]> Models, Days, Cycles, Trend, Coverage, Amounts;
        // Detector state shared with notifications; selected dates and chart limits never change it.
        internal List<AlertEpisode> Episodes = new List<AlertEpisode>();
        internal List<ReadinessResult> Readiness = new List<ReadinessResult>();
    }

    /// <summary>Read-only projections. Provider quotas, logged tokens and computed API costs remain separate units.</summary>
    internal sealed class HistoryAnalysis
    {
        private readonly HistoryStore store;
        private readonly ConsumptionMonitor monitor;
        internal HistoryAnalysis(HistoryStore store, ConsumptionMonitor monitor = null) { this.store = store; this.monitor = monitor; }

        internal string[] Providers()
        {
            return store.Query("SELECT provider FROM observations UNION SELECT provider FROM sessions ORDER BY provider").Select(row => (string)row[0]).ToArray();
        }

        internal HistoryReport Read(DateTime since, string provider)
        {
            return store.ReadConsistently(() => ReadReport(since, provider));
        }

        private HistoryReport ReadReport(DateTime since, string provider)
        {
            long from = HistoryStore.Stamp(since);
            var report = new HistoryReport();
            report.Observations = Convert.ToInt64(store.Query("SELECT COUNT(*) FROM observations WHERE measured>=? AND (?='' OR provider=?)", from, provider, provider)[0][0]);
            report.Started = store.Started;
            report.HasAnyHistory = store.Query("SELECT 1 FROM observations UNION ALL SELECT 1 FROM events LIMIT 1").Count > 0;
            report.Bytes = store.SizeBytes;
            report.Events = Convert.ToInt64(store.Query("SELECT COUNT(*) FROM events e JOIN sessions s ON s.id=e.session WHERE e.at>=? AND (?='' OR s.provider=?)", from, provider, provider)[0][0]);
            report.Coverage = store.Query("SELECT first,last,reason FROM coverage WHERE last>=? ORDER BY last DESC LIMIT 100", from);
            report.Gaps = Convert.ToInt64(store.Query("SELECT COUNT(*) FROM coverage WHERE last>=?", from)[0][0]);
            report.Sessions = store.Sessions(since, provider, 200);
            var ratings = store.Query("SELECT COUNT(*),COUNT(feedback),SUM(CASE WHEN feedback=1 THEN 1 ELSE 0 END),SUM(CASE WHEN feedback=0 THEN 1 ELSE 0 END),SUM(CASE WHEN feedback=-1 THEN 1 ELSE 0 END) FROM sessions WHERE last>=? AND (?='' OR provider=?)", from, provider, provider)[0];
            report.SessionCount = Convert.ToInt64(ratings[0]); report.Rated = Convert.ToInt64(ratings[1]);
            report.Positive = Count(ratings[2]); report.Neutral = Count(ratings[3]); report.Negative = Count(ratings[4]);
            report.Models = store.Query("SELECT s.provider,s.agent,COALESCE(e.model,'—'),COUNT(*),SUM(e.input),SUM(e.output),SUM(e.cache_read),SUM(e.cache_write),SUM(e.cost),COUNT(e.cost) FROM events e JOIN sessions s ON s.id=e.session WHERE e.at>=? AND (?='' OR s.provider=?) GROUP BY s.provider,s.agent,e.model ORDER BY COUNT(*) DESC LIMIT 100", from, provider, provider);
            report.Days = ReadDays(from, provider);
            report.Cycles = store.Query(@"SELECT provider,account,metric,resets,MIN(measured),MAX(measured),MIN(percent),MAX(percent),COUNT(*),MAX(allowance),currency,
                (SELECT latest.percent FROM observations latest WHERE latest.provider=o.provider AND latest.account=o.account
                    AND latest.metric=o.metric AND latest.resets=o.resets AND latest.currency IS o.currency
                    AND latest.plan IS o.plan AND latest.allowance IS o.allowance AND latest.percent IS NOT NULL ORDER BY latest.measured DESC LIMIT 1),
                MAX(CASE WHEN source LIKE '%unidentified-account%' THEN 1 ELSE 0 END)
                FROM observations o WHERE measured>=? AND percent IS NOT NULL AND resets IS NOT NULL AND (?='' OR provider=?)
                GROUP BY provider,account,metric,resets,currency,plan,allowance ORDER BY MAX(measured) DESC LIMIT 200", from, provider, provider);
            report.Amounts = store.Query(@"SELECT o.provider,o.metric,o.measured,o.amount,o.unit,o.spent,o.allowance,o.currency,o.quality,o.account,o.source
                FROM observations o WHERE o.measured>=? AND (?='' OR o.provider=?) AND (o.unit IS NOT NULL OR o.currency IS NOT NULL)
                AND NOT EXISTS(SELECT 1 FROM observations newer WHERE newer.provider=o.provider AND newer.account=o.account
                    AND newer.metric=o.metric AND newer.measured>o.measured) ORDER BY o.measured DESC LIMIT 100", from, provider, provider);
            report.Trend = store.Query("SELECT provider,account,metric,resets,measured,percent,source,allowance,currency,plan FROM observations WHERE measured>=? AND percent IS NOT NULL AND (?='' OR provider=?) ORDER BY measured DESC LIMIT 5000", from, provider, provider);
            if (monitor != null)
            {
                report.Episodes = monitor.Episodes(since == DateTime.MinValue ? DateTime.MinValue : since.ToUniversalTime(), provider, 20);
                report.Readiness = monitor.Readiness.Where(r => provider == "" || r.Scope.Provider == provider).ToList();
            }
            return report;
        }

        private List<object[]> ReadDays(long from, string provider)
        {
            // The in-box Windows SQLite can return NULL for the localtime modifier. Resolve day boundaries
            // through .NET instead, including DST and fractional UTC offsets, without loading every event.
            var days = new SortedSet<DateTime>();
            var buckets = store.Query(@"SELECT MIN(e.at),MAX(e.at) FROM events e JOIN sessions s ON s.id=e.session
                WHERE e.at>=? AND (?='' OR s.provider=?) GROUP BY e.at/86400000", from, provider, provider);
            foreach (var bucket in buckets)
            {
                DateTime first = HistoryStore.Time(bucket[0]).Date, last = HistoryStore.Time(bucket[1]).Date;
                for (DateTime day = first; day <= last; day = day.AddDays(1)) days.Add(day);
            }
            var result = new List<object[]>();
            foreach (DateTime day in days)
            {
                var row = store.Query(@"SELECT COUNT(*),SUM(input),SUM(output),SUM(cache_read),SUM(cache_write),SUM(cost),COUNT(cost)
                    FROM events e JOIN sessions s ON s.id=e.session WHERE e.at>=? AND e.at<? AND (?='' OR s.provider=?)",
                    Math.Max(from, DayStart(day, TimeZoneInfo.Local)), DayStart(day.AddDays(1), TimeZoneInfo.Local), provider, provider)[0];
                if (Convert.ToInt64(row[0]) == 0) continue;
                result.Add(new object[] { day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), row[0], row[1], row[2], row[3], row[4], row[5], row[6] });
            }
            return result;
        }

        internal static long DayStart(DateTime day, TimeZoneInfo zone)
        {
            DateTime local = DateTime.SpecifyKind(day.Date, DateTimeKind.Unspecified);
            // Some zones advance at midnight or repeat it. Use the first real instant of that local day.
            while (zone.IsInvalidTime(local)) local = local.AddMinutes(1);
            if (zone.IsAmbiguousTime(local))
                return HistoryStore.Stamp(new DateTimeOffset(local, zone.GetAmbiguousTimeOffsets(local).Max()).UtcDateTime);
            return HistoryStore.Stamp(TimeZoneInfo.ConvertTimeToUtc(local, zone));
        }

        private static long Count(object value) { return value == null ? 0 : Convert.ToInt64(value); }
    }
}
