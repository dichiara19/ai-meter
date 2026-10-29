using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace AIMeter
{
    // Deterministic consumption analysis: interval validation, personal reference ranges, readiness and evidence.
    // Nothing here reads the system clock, the registry, the network or the UI; callers pass times explicitly.
    // Units: quota percentage points ("points") and points per hour. All instants are UTC.

    /// <summary>One comparable quota series. Accounts, metrics, plans, allowances and currencies never mix.</summary>
    internal sealed class ConsumptionScope
    {
        internal string Provider, Account, Metric, Source, Plan, Currency;
        internal double? Allowance;

        /// <summary>Unknown account identities are ineligible for personal comparisons.</summary>
        internal bool IsIdentified { get { return Source != null && !Source.Contains("unidentified-account"); } }

        internal string Key
        {
            get
            {
                string allowance = Allowance.HasValue ? Allowance.Value.ToString("R", CultureInfo.InvariantCulture) : "";
                return string.Join("\u001f", Provider, Account, Metric, Source, Plan ?? "", Currency ?? "", allowance);
            }
        }
    }

    /// <summary>A stored quota reading. Percent is null when the provider reported no usable number.</summary>
    internal sealed class QuotaReading
    {
        internal DateTime MeasuredUtc;
        internal DateTime? ResetsUtc;
        internal double? Percent;
        internal string Quality;
    }

    /// <summary>Consumption between two valid readings of the same cycle, without interpolation.</summary>
    internal sealed class ConsumptionInterval
    {
        internal DateTime StartUtc, EndUtc;
        internal DateTime? ResetsUtc;
        internal double Points;
        internal double Minutes { get { return (EndUtc - StartUtc).TotalMinutes; } }
        internal double PointsPerHour { get { return Points * 60 / Minutes; } }
        internal bool IsActive { get { return Points > 0; } }
    }

    /// <summary>
    /// Accumulates consecutive valid readings into non-overlapping intervals of at least five minutes.
    /// Any reading that breaks continuity starts a new interval from that reading; nothing spans the break.
    /// The state is plain data so it can be persisted between collection batches.
    /// </summary>
    internal sealed class IntervalBuilder
    {
        /// <summary>Nominal interval length.</summary>
        internal const double NominalMinutes = 5;

        /// <summary>
        /// Longest accepted gap between consecutive readings. Calibration parameter, not a guarantee: providers observed
        /// so far report every 5 minutes at the median and every 7–9 minutes at the 90th percentile, so a strict
        /// five-minute bound would reject most real data. Intervals therefore last between 5 and 15 minutes.
        /// </summary>
        internal const double MaxGapMinutes = 10;

        /// <summary>Resets reported within this tolerance belong to the same cycle (providers round reset times).</summary>
        internal const double ResetToleranceMinutes = 10;

        internal QuotaReading Anchor, Last;
        internal readonly Dictionary<string, int> Exclusions = new Dictionary<string, int>();

        /// <summary>Feed one reading in measurement order. Returns a completed interval, or null.</summary>
        /// <param name="coverageGap">Whether a recorded collection interruption overlaps the span since the previous reading.</param>
        internal ConsumptionInterval Feed(QuotaReading reading, Func<DateTime, DateTime, bool> coverageGap)
        {
            if (reading.Quality != "measured" || !reading.Percent.HasValue || !HistoryStore.Finite(reading.Percent.Value)
                || reading.Percent.Value < 0 || reading.Percent.Value > 100)
                return Break(null, "unusable-reading");
            if (Last != null && reading.MeasuredUtc <= Last.MeasuredUtc) { Exclude("out-of-order-or-duplicate"); return null; }
            if (Anchor == null) return Break(reading, null);
            if (!SameCycle(Last.ResetsUtc, reading.ResetsUtc)) return Break(reading, "reset");
            if (reading.Percent.Value < Last.Percent.Value) return Break(reading, "decrease");
            if ((reading.MeasuredUtc - Last.MeasuredUtc).TotalMinutes > MaxGapMinutes) return Break(reading, "gap");
            if (coverageGap != null && coverageGap(Last.MeasuredUtc, reading.MeasuredUtc)) return Break(reading, "collection-interrupted");

            Last = reading;
            if ((reading.MeasuredUtc - Anchor.MeasuredUtc).TotalMinutes < NominalMinutes) return null;
            var interval = new ConsumptionInterval
            {
                StartUtc = Anchor.MeasuredUtc, EndUtc = reading.MeasuredUtc, ResetsUtc = reading.ResetsUtc,
                Points = reading.Percent.Value - Anchor.Percent.Value,
            };
            Anchor = reading;
            return interval;
        }

        private ConsumptionInterval Break(QuotaReading restart, string reason)
        {
            if (reason != null) Exclude(reason);
            Anchor = Last = restart;
            return null;
        }

        private void Exclude(string reason)
        {
            int count;
            Exclusions.TryGetValue(reason, out count);
            Exclusions[reason] = count + 1;
        }

        internal static bool SameCycle(DateTime? a, DateTime? b)
        {
            if (!a.HasValue || !b.HasValue) return a.HasValue == b.HasValue;
            return Math.Abs((a.Value - b.Value).TotalMinutes) <= ResetToleranceMinutes;
        }
    }

    /// <summary>Robust summary of a scope's completed history, frozen into every episode it supports.</summary>
    internal sealed class BaselineSummary
    {
        internal const int Version = 1;
        internal string Context;                 // "all", "weekday" or "weekend"
        internal string TimeZone;
        internal DateTime FromUtc, ToUtc;
        internal int ObservedDays, ActiveDays, Intervals, ActiveIntervals, SustainedWindows, Weeks;
        internal double MedianRate, DeviationRate, UpperRate, FastThreshold;            // points per hour
        internal double SustainedMedian, SustainedUpper, SustainedThreshold;            // points per hour
        internal double StabilitySpread;                                                // relative, leave-one-day-out

        internal Dictionary<string, object> ToJson()
        {
            return new Dictionary<string, object>
            {
                { "version", Version }, { "context", Context }, { "timeZone", TimeZone },
                { "fromUtc", FromUtc.ToString("o", CultureInfo.InvariantCulture) }, { "toUtc", ToUtc.ToString("o", CultureInfo.InvariantCulture) },
                { "observedDays", ObservedDays }, { "activeDays", ActiveDays }, { "intervals", Intervals }, { "activeIntervals", ActiveIntervals },
                { "sustainedWindows", SustainedWindows }, { "medianPointsPerHour", MedianRate }, { "deviationPointsPerHour", DeviationRate },
                { "upperPointsPerHour", UpperRate }, { "fastThresholdPointsPerHour", FastThreshold },
                { "sustainedMedianPointsPerHour", SustainedMedian }, { "sustainedUpperPointsPerHour", SustainedUpper },
                { "sustainedThresholdPointsPerHour", SustainedThreshold },
            };
        }
    }

    internal enum ReadinessState { Learning, Ready, TemporarilyUnavailable, ReferenceChanged }

    /// <summary>Why a scope can or cannot be evaluated. Reason is an untranslated format string with Arguments.</summary>
    internal sealed class ReadinessResult
    {
        internal ConsumptionScope Scope;
        internal ReadinessState State;
        internal string Reason;
        internal object[] Arguments = new object[0];
        internal BaselineSummary Baseline;
    }

    internal enum EvidenceKind { Fast, Sustained }

    /// <summary>An observed departure from the personal reference range, with what it was compared against.</summary>
    internal sealed class AnomalyEvidence
    {
        internal EvidenceKind Kind;
        internal bool IsStrong;
        internal DateTime StartUtc, EndUtc;
        internal double Points, Minutes, RatePerHour, ThresholdPerHour, ReferenceMedianPerHour;

        internal Dictionary<string, object> ToJson()
        {
            return new Dictionary<string, object>
            {
                { "kind", Kind == EvidenceKind.Fast ? "fast" : "sustained" }, { "strong", IsStrong },
                { "startUtc", StartUtc.ToString("o", CultureInfo.InvariantCulture) }, { "endUtc", EndUtc.ToString("o", CultureInfo.InvariantCulture) },
                { "points", Points }, { "minutes", Minutes }, { "pointsPerHour", RatePerHour },
                { "thresholdPointsPerHour", ThresholdPerHour }, { "referenceMedianPointsPerHour", ReferenceMedianPerHour },
            };
        }

        internal static AnomalyEvidence FromJson(Dictionary<string, object> json)
        {
            if (json == null) return null;
            Func<string, DateTime> time = name => DateTime.Parse(Json.Str(json, name), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
            return new AnomalyEvidence
            {
                Kind = Json.Str(json, "kind") == "sustained" ? EvidenceKind.Sustained : EvidenceKind.Fast,
                IsStrong = json.ContainsKey("strong") && Equals(json["strong"], true),
                StartUtc = time("startUtc"), EndUtc = time("endUtc"),
                Points = Json.Num(json, "points") ?? 0, Minutes = Json.Num(json, "minutes") ?? 0, RatePerHour = Json.Num(json, "pointsPerHour") ?? 0,
                ThresholdPerHour = Json.Num(json, "thresholdPointsPerHour") ?? 0, ReferenceMedianPerHour = Json.Num(json, "referenceMedianPointsPerHour") ?? 0,
            };
        }
    }

    /// <summary>Reference ranges, readiness and detection. Constants are calibration candidates, versioned with every result.</summary>
    internal static class ConsumptionAnalysis
    {
        internal const int AlgorithmVersion = 1;
        internal const int HistoryDays = 56;              // Rolling eight-week reference.
        internal const int MinimumObservedDays = 14;
        internal const int MinimumActiveDays = 8;
        internal const int MinimumActiveIntervals = 60;
        internal const int MinimumSustainedWindows = 20;
        internal const double SustainedMinutes = 30;
        internal const double FloorPerHour = 1;           // Positive floor when the median or deviation is zero.
        internal const double MaximumStabilitySpread = 0.5;
        internal const double StaleMinutes = 20;
        internal const double FastMinimumPoints = 2;      // A single quantum step is never enough evidence.
        internal const double StrongMinimumPoints = 3;
        internal const double SustainedMinimumPoints = 3;

        /// <summary>
        /// Summarise completed intervals. The caller passes only intervals from completed prior periods,
        /// so the period under evaluation never trains its own reference.
        /// </summary>
        internal static BaselineSummary Summarise(List<ConsumptionInterval> intervals, TimeZoneInfo zone, string context, DateTime fromUtc, DateTime toUtc)
        {
            var b = new BaselineSummary { Context = context, TimeZone = zone.Id, FromUtc = fromUtc, ToUtc = toUtc, Intervals = intervals.Count };
            var active = intervals.Where(i => i.IsActive).ToList();
            b.ActiveIntervals = active.Count;
            b.ObservedDays = intervals.Select(i => LocalDay(i.EndUtc, zone)).Distinct().Count();
            b.ActiveDays = active.Select(i => LocalDay(i.EndUtc, zone)).Distinct().Count();
            b.Weeks = active.Select(i => Week(LocalDay(i.EndUtc, zone))).Distinct().Count();
            if (active.Count > 0)
            {
                var rates = active.Select(i => i.PointsPerHour).ToList();
                b.MedianRate = Median(rates);
                b.DeviationRate = 1.4826 * Median(rates.Select(r => Math.Abs(r - b.MedianRate)).ToList());
                b.UpperRate = Quantile(rates, 0.95);
                b.FastThreshold = Threshold(b.UpperRate, b.MedianRate, b.DeviationRate, 4);
            }
            var sustained = SustainedRates(intervals);
            b.SustainedWindows = sustained.Count;
            if (sustained.Count > 0)
            {
                b.SustainedMedian = Median(sustained);
                double deviation = 1.4826 * Median(sustained.Select(r => Math.Abs(r - b.SustainedMedian)).ToList());
                b.SustainedUpper = Quantile(sustained, 0.95);
                b.SustainedThreshold = Threshold(b.SustainedUpper, b.SustainedMedian, deviation, 3);
            }
            b.StabilitySpread = Stability(active, zone, b.FastThreshold);
            return b;
        }

        private static double Threshold(double upper, double median, double deviation, double deviations)
        {
            return Math.Max(upper, Math.Max(median + deviations * deviation, median + FloorPerHour));
        }

        /// <summary>Relative spread of the fast threshold when each active day is left out in turn.</summary>
        private static double Stability(List<ConsumptionInterval> active, TimeZoneInfo zone, double threshold)
        {
            if (threshold <= 0) return double.PositiveInfinity;
            var days = active.GroupBy(i => LocalDay(i.EndUtc, zone)).ToList();
            if (days.Count < 3) return double.PositiveInfinity;
            double low = double.MaxValue, high = double.MinValue;
            foreach (var day in days)
            {
                var rates = active.Where(i => LocalDay(i.EndUtc, zone) != day.Key).Select(i => i.PointsPerHour).ToList();
                double median = Median(rates);
                double t = Threshold(Quantile(rates, 0.95), median, 1.4826 * Median(rates.Select(r => Math.Abs(r - median)).ToList()), 4);
                low = Math.Min(low, t); high = Math.Max(high, t);
            }
            return (high - low) / threshold;
        }

        /// <summary>Rates over non-overlapping 30-minute stretches of contiguous intervals with some consumption.</summary>
        internal static List<double> SustainedRates(List<ConsumptionInterval> intervals)
        {
            var result = new List<double>();
            double points = 0, minutes = 0;
            DateTime? end = null;
            foreach (var i in intervals.OrderBy(i => i.StartUtc))
            {
                if (end != i.StartUtc) { points = 0; minutes = 0; }
                points += i.Points; minutes += i.Minutes; end = i.EndUtc;
                if (minutes < SustainedMinutes) continue;
                if (points > 0) result.Add(points * 60 / minutes);
                points = 0; minutes = 0;
            }
            return result;
        }

        /// <summary>
        /// Choose the comparison for an evaluation time: weekday or weekend when that context has repeated coverage
        /// across weeks, otherwise the general reference. The chosen context is part of every explanation.
        /// </summary>
        internal static BaselineSummary Reference(List<ConsumptionInterval> completed, TimeZoneInfo zone, DateTime atUtc, DateTime fromUtc, DateTime toUtc)
        {
            var general = Summarise(completed, zone, "all", fromUtc, toUtc);
            bool weekend = IsWeekend(LocalDay(atUtc, zone));
            var subset = completed.Where(i => IsWeekend(LocalDay(i.EndUtc, zone)) == weekend).ToList();
            var contextual = Summarise(subset, zone, weekend ? "weekend" : "weekday", fromUtc, toUtc);
            bool sufficient = contextual.ActiveDays >= 4 && contextual.ActiveIntervals >= 30 && contextual.Weeks >= 2
                && contextual.SustainedWindows >= MinimumSustainedWindows / 2 && contextual.StabilitySpread <= MaximumStabilitySpread;
            if (!sufficient) return general;
            // Contextual readiness inherits the general data minima.
            contextual.ObservedDays = general.ObservedDays;
            return contextual;
        }

        /// <summary>Whether the scope may be evaluated now, and why.</summary>
        internal static ReadinessResult Readiness(ConsumptionScope scope, BaselineSummary b, DateTime? lastReadingUtc, DateTime nowUtc, bool referenceChanged)
        {
            var r = new ReadinessResult { Scope = scope, Baseline = b };
            if (!scope.IsIdentified) return Set(r, ReadinessState.TemporarilyUnavailable, "The account cannot be identified, so personal comparisons are disabled.");
            if (b.ObservedDays < MinimumObservedDays) return Set(r, ReadinessState.Learning, "{0} of {1} observed days.", b.ObservedDays, MinimumObservedDays);
            if (b.ActiveDays < MinimumActiveDays) return Set(r, ReadinessState.Learning, "{0} of {1} days with consumption.", b.ActiveDays, MinimumActiveDays);
            if (b.ActiveIntervals < MinimumActiveIntervals) return Set(r, ReadinessState.Learning, "{0} of {1} intervals with consumption.", b.ActiveIntervals, MinimumActiveIntervals);
            if (b.StabilitySpread > MaximumStabilitySpread) return Set(r, ReadinessState.Learning, "The reference range still changes when a single day is left out.");
            if (referenceChanged) return Set(r, ReadinessState.ReferenceChanged, "Consumption has stayed above the reference on several recent days.");
            if (!lastReadingUtc.HasValue || (nowUtc - lastReadingUtc.Value).TotalMinutes > StaleMinutes)
                return Set(r, ReadinessState.TemporarilyUnavailable, "No recent valid reading.");
            return Set(r, ReadinessState.Ready, "Compared with {0} days with consumption.", b.ActiveDays);
        }

        private static ReadinessResult Set(ReadinessResult r, ReadinessState state, string reason, params object[] arguments)
        {
            r.State = state; r.Reason = reason; r.Arguments = arguments;
            return r;
        }

        /// <summary>Compare a newly completed interval against the reference range. Returns null when it is within range.</summary>
        internal static AnomalyEvidence Fast(ConsumptionInterval interval, BaselineSummary b)
        {
            if (interval.Points < FastMinimumPoints || b.ActiveIntervals == 0) return null;
            // One reported point is the measurement resolution: allow it before calling a rate unusual.
            double threshold = b.FastThreshold + 60 / interval.Minutes;
            double rate = interval.PointsPerHour;
            if (rate <= threshold) return null;
            return new AnomalyEvidence
            {
                Kind = EvidenceKind.Fast, IsStrong = rate >= 2 * threshold && interval.Points >= StrongMinimumPoints,
                StartUtc = interval.StartUtc, EndUtc = interval.EndUtc, Points = interval.Points, Minutes = interval.Minutes,
                RatePerHour = rate, ThresholdPerHour = threshold, ReferenceMedianPerHour = b.MedianRate,
            };
        }

        /// <summary>Compare the latest contiguous 30 minutes with equivalent historical stretches. Gaps restart accumulation.</summary>
        internal static AnomalyEvidence Sustained(List<ConsumptionInterval> recent, BaselineSummary b)
        {
            if (b.SustainedWindows < MinimumSustainedWindows || recent.Count == 0) return null;
            double points = 0, minutes = 0;
            var ordered = recent.OrderByDescending(i => i.EndUtc).ToList();
            DateTime start = ordered[0].EndUtc;
            foreach (var i in ordered)
            {
                if (i.EndUtc != start) break;
                points += i.Points; minutes += i.Minutes; start = i.StartUtc;
                if (minutes >= SustainedMinutes) break;
            }
            if (minutes < SustainedMinutes || points < SustainedMinimumPoints) return null;
            double threshold = b.SustainedThreshold + 60 / minutes;
            double rate = points * 60 / minutes;
            if (rate <= threshold) return null;
            return new AnomalyEvidence
            {
                Kind = EvidenceKind.Sustained, IsStrong = true, StartUtc = start, EndUtc = ordered[0].EndUtc,
                Points = points, Minutes = minutes, RatePerHour = rate, ThresholdPerHour = threshold, ReferenceMedianPerHour = b.SustainedMedian,
            };
        }

        internal static DateTime LocalDay(DateTime utc, TimeZoneInfo zone) { return TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), zone).Date; }
        private static bool IsWeekend(DateTime day) { return day.DayOfWeek == DayOfWeek.Saturday || day.DayOfWeek == DayOfWeek.Sunday; }
        private static int Week(DateTime day) { return (int)((day - new DateTime(2000, 1, 3)).TotalDays / 7); }

        internal static double Median(List<double> values)
        {
            if (values.Count == 0) return 0;
            var ordered = values.OrderBy(v => v).ToArray();
            int middle = ordered.Length / 2;
            return ordered.Length % 2 == 0 ? (ordered[middle - 1] + ordered[middle]) / 2 : ordered[middle];
        }

        internal static double Quantile(List<double> values, double q)
        {
            if (values.Count == 0) return 0;
            var ordered = values.OrderBy(v => v).ToArray();
            double position = q * (ordered.Length - 1);
            int low = (int)Math.Floor(position), high = (int)Math.Ceiling(position);
            return ordered[low] + (ordered[high] - ordered[low]) * (position - low);
        }
    }
}
