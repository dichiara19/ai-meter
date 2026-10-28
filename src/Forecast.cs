using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace AIMeter
{
    /// <summary>
    /// Forecast limit exhaustion from the last 90 minutes of samples, or from the window's average pace.
    /// Require enough readings before using recent pace.
    /// Do not predict exhaustion when the limit lasts until its reset.
    /// </summary>
    internal sealed class Forecaster
    {
        private sealed class Sample { public DateTime At; public double Used; }
        private sealed class Track { public DateTime? Reset; public string Identity; public List<Sample> Samples = new List<Sample>(); }
        private readonly Dictionary<string, Track> tracks = new Dictionary<string, Track>();

        public void Apply(Snapshot s)
        {
            var now = DateTime.Now;
            foreach (var p in s.Providers)
            {
                if (p.Usage == null) continue;
                foreach (var w in p.Usage.Windows)
                {
                    w.RunsOutAt = null;
                    w.PacePerHour = null;
                    if (!w.HasPercent || !w.ResetsAt.HasValue || w.Used >= 100) continue;
                    string key = p.Id + "|" + w.Label + "|" + w.Suffix;
                    Track t;
                    if (!tracks.TryGetValue(key, out t)) tracks[key] = t = new Track();
                    if (t.Identity != p.Usage.Identity) { t.Samples.Clear(); t.Identity = p.Usage.Identity; }
                    // Discard previous samples when the limit enters a new window.
                    if (t.Reset.HasValue && Math.Abs((w.ResetsAt.Value - t.Reset.Value).TotalMinutes) > 10) t.Samples.Clear();
                    t.Reset = w.ResetsAt;
                    if (p.Usage.Source != "cache" && (t.Samples.Count == 0 || t.Samples[t.Samples.Count - 1].At != p.Usage.At))
                        t.Samples.Add(new Sample { At = p.Usage.At, Used = w.Used });
                    t.Samples.RemoveAll(x => (now - x.At).TotalMinutes > 90);
                    w.PacePerHour = Pace(t.Samples, w, now);
                    if (w.PacePerHour > 0.05)
                    {
                        var at = now.AddHours((100 - w.Used) / w.PacePerHour.Value);
                        if (at < w.ResetsAt.Value) w.RunsOutAt = at;
                    }
                }
            }
        }

        /// <summary>Percentage points used per hour, or null without enough data.</summary>
        private static double? Pace(List<Sample> samples, UsageWindow w, DateTime now)
        {
            double? perHour = null;
            // Fit recent pace with least squares over at least ten minutes of readings.
            if (samples.Count >= 2 && (samples[samples.Count - 1].At - samples[0].At).TotalMinutes >= 10)
            {
                var t0 = samples[0].At;
                double n = samples.Count, sx = 0, sy = 0, sxx = 0, sxy = 0;
                foreach (var x in samples)
                {
                    double h = (x.At - t0).TotalHours;
                    sx += h; sy += x.Used; sxx += h * h; sxy += h * x.Used;
                }
                double den = n * sxx - sx * sx;
                if (den > 1e-9) perHour = Math.Max(0, (n * sxy - sx * sy) / den);
            }
            // Otherwise use the window average after 15 minutes and a meaningful amount of usage.
            else if (w.StartedAt.HasValue && (now - w.StartedAt.Value).TotalMinutes >= 15 && w.Used >= 3)
                perHour = w.Used / (now - w.StartedAt.Value).TotalHours;
            return perHour;
        }
    }

    /// <summary>
    /// Notify at 20% and 5% remaining, on recovery, on selected resets, and for usage tips.
    /// Deduplicate each threshold and window across restarts.
    /// Persist notification state in alerts.json.
    /// </summary>
    internal sealed class Alerts
    {
        private sealed class State { public string Reset; public int Level; public string Name, Label, Announced, Tip, Seen, Identity; }
        private readonly Dictionary<string, State> states;
        private static string FilePath { get { return Path.Combine(Settings.Folder, "alerts.json"); } }
        private const string Stamp = "yyyyMMddHHmm";

        public Alerts()
        {
            states = new Dictionary<string, State>();
            try
            {
                var d = Json.ParseFile(FilePath);
                if (d != null)
                    foreach (var kv in d)
                    {
                        var v = kv.Value as Dictionary<string, object>;
                        if (v != null)
                            states[kv.Key] = new State
                            {
                                Reset = Json.Str(v, "reset"), Level = (int)(Json.Num(v, "level") ?? 0), Name = Json.Str(v, "name"), Label = Json.Str(v, "label"),
                                Announced = Json.Str(v, "announced"), Tip = Json.Str(v, "tip"), Seen = Json.Str(v, "seen"), Identity = Json.Str(v, "identity"),
                            };
                    }
            }
            catch (Exception) { }
        }

        /// <summary>Return notification titles and text for this snapshot, and update service tips.</summary>
        public List<Tuple<string, string>> Check(Snapshot s, Settings settings)
        {
            var result = new List<Tuple<string, string>>();
            bool changed = false;
            var now = DateTime.Now;

            // Announce selected resets at their reported time, even without a new reading.
            foreach (var kv in states)
            {
                var st = kv.Value;
                DateTime at;
                bool on;
                if (st.Announced == st.Reset || !Parse(st.Reset, out at) || at > now) continue;
                if (!settings.NotifyResets.TryGetValue(kv.Key, out on) || !on) continue;
                string id = kv.Key.Substring(0, Math.Max(0, kv.Key.IndexOf('|')));
                var provider = s.Providers.FirstOrDefault(p => p.Id == id);
                if (provider == null || provider.Usage == null || st.Identity != provider.Usage.Identity) continue;
                st.Announced = st.Reset;
                changed = true;
                // Avoid delayed reset notifications after a long shutdown or lock.
                if ((now - at).TotalMinutes <= 30)
                    result.Add(Tuple.Create(L.F("{0} · {1} has reset", st.Name, st.Label), L.T("The full limit is available again.")));
            }

            foreach (var p in s.Providers)
            {
                if (p.Usage == null || p.Usage.Source == "cache") continue;
                foreach (var w in p.Usage.Windows)
                {
                    if (w.CachedAt.HasValue) continue;
                    string key = p.Id + "|" + w.Key;
                    State st;
                    states.TryGetValue(key, out st);
                    string seen = now.Date.AddHours(now.Hour).ToString(Stamp, CultureInfo.InvariantCulture);
                    if (st == null) { st = new State(); states[key] = st; changed = true; }
                    if (st.Identity != p.Usage.Identity)
                    {
                        st = new State { Identity = p.Usage.Identity };
                        states[key] = st;
                        changed = true;
                    }
                    if (st.Seen != seen) { st.Seen = seen; changed = true; }
                    if (!w.HasPercent || !w.ResetsAt.HasValue) continue;
                    string reset = w.ResetsAt.Value.ToString(Stamp, CultureInfo.InvariantCulture);
                    // A new window makes a previously exhausted limit available again.
                    // Do not repeat a reset already announced through an explicit notification preference.
                    // Treat small reset-time fluctuations as the same window and retain its first timestamp.
                    if (st.Reset != null && !Same(st.Reset, reset))
                    {
                        if (st.Level >= 80 && w.Used < 50 && settings.NotifyReset && st.Announced != st.Reset)
                            result.Add(Tuple.Create(L.F("{0} · {1}: available again", p.Name, Ui.LabelOf(w)),
                                L.F("The limit has reset: {0}% left.", Math.Round(w.Remaining))));
                        st = new State { Seen = seen, Identity = p.Usage.Identity }; states[key] = st;
                    }
                    if (st.Reset == null) { st.Reset = reset; changed = true; }
                    if (st.Name != p.Name || st.Label != Ui.LabelOf(w)) { st.Name = p.Name; st.Label = Ui.LabelOf(w); changed = true; }
                    int level = w.Used >= 95 ? 95 : w.Used >= 80 ? 80 : 0;
                    if (level > st.Level)
                    {
                        st.Level = level;
                        changed = true;
                        if (settings.NotifyLow) result.Add(Low(s, p, w));
                    }
                }
            }

            // Keep an applicable tip in the card, but notify only once per window.
            foreach (var p in s.Providers)
            {
                p.Tip = TipFor(p, settings, now);
                if (p.Tip == null || p.Usage.Source == "cache") continue;
                State st;
                if (!states.TryGetValue(p.Tip.Key, out st) || st.Tip == p.Tip.Kind) continue;
                st.Tip = p.Tip.Kind;
                changed = true;
                result.Add(Tuple.Create(p.Tip.Title, p.Tip.Text));
            }

            // Retire choices for limits missing from successful readings for more than ten days.
            // Announce a pill fallback to Default; remove stale reset preferences silently.
            bool settingsChanged = false;
            foreach (var p in s.Providers)
            {
                if (p.Usage == null || p.Usage.Source != "live") continue;
                string choice;
                if (settings.PillLimits.TryGetValue(p.Id, out choice) && choice != "auto" && !string.IsNullOrEmpty(choice) && Gone(p, choice, now, ref changed))
                {
                    settings.PillLimits.Remove(p.Id);
                    settingsChanged = true;
                    result.Add(Tuple.Create(L.F("{0}: limit in the taskbar back to Default", p.Name),
                        L.F("{0} is no longer reported by the service.", Ui.LabelOf(FromKey(choice)))));
                }
                foreach (var key in settings.NotifyResets.Keys.Where(k => k.StartsWith(p.Id + "|", StringComparison.Ordinal)).ToList())
                    if (Gone(p, key.Substring(p.Id.Length + 1), now, ref changed)) { settings.NotifyResets.Remove(key); settingsChanged = true; }
            }
            if (settingsChanged) settings.Save();

            if (changed) Save();
            return result;
        }

        /// <summary>Return true only after a missing limit's ten-day grace period expires.</summary>
        private bool Gone(ProviderState p, string windowKey, DateTime now, ref bool changed)
        {
            if (p.Usage.Windows.Any(w => w.Key == windowKey)) return false;
            string key = p.Id + "|" + windowKey;
            State st;
            DateTime seen;
            if (!states.TryGetValue(key, out st)) { st = new State(); states[key] = st; }
            if (!Parse(st.Seen, out seen))
            {
                // Existing preferences also get a grace period when upgrading or starting without extra data.
                st.Seen = now.ToString(Stamp, CultureInfo.InvariantCulture);
                changed = true;
                return false;
            }
            return now - seen > TimeSpan.FromDays(10);
        }

        private static UsageWindow FromKey(string key)
        {
            int bar = key.IndexOf('|');
            string suffix = bar < 0 ? "" : key.Substring(bar + 1);
            return new UsageWindow { Label = bar < 0 ? key : key.Substring(0, bar), Suffix = suffix.Length > 0 ? suffix : null };
        }

        /// <summary>
        /// Apply the same tip policy to every provider and window duration.
        /// Suggest waiting when a spent limit resets soon while extra usage is billed separately.
        /// Suggest unused quota near the last 15% of a window, bounded between half an hour and four days.
        ///   Use the normal spending pace to estimate quota that would otherwise expire.
        ///   Require at least 30% unused quota for the longest windows or expiring monetary credits.
        ///   Short-window tips also require opt-in and enough room in the enclosing long windows.
        /// </summary>
        public static Tip TipFor(ProviderState p, Settings settings, DateTime now)
        {
            if (p.Usage == null || p.Models != null) return null;
            // User budgets do not expire, so they never receive unused-credit tips.
            var ws = p.Usage.Windows.Where(w => w.HasPercent && !w.Budget && w.ResetsAt.HasValue && w.ResetsAt.Value > now && w.Length.HasValue).ToList();
            if (ws.Count == 0) return null;

            if (settings.TipsWait && p.Usage.ExtraActive)
            {
                var blocked = ws.Where(w => w.Spent == null && w.Used >= 100 && w.ResetsAt.Value - now <= Lead(w)).OrderBy(w => w.ResetsAt.Value).FirstOrDefault();
                if (blocked != null)
                {
                    string when = When(blocked.ResetsAt.Value);
                    return new Tip
                    {
                        Key = p.Id + "|" + blocked.Key, Kind = "wait",
                        Title = L.F("{0} · {1} resets {2}", p.Name, Ui.LabelOf(blocked), when),
                        Text = L.T("Extra usage is on and billed separately. If you can, wait for the reset instead of paying for it."),
                        Line = L.F("{0} resets {1}: wait for it instead of paying for extra usage", Ui.LabelOf(blocked), when),
                    };
                }
            }
            if (!settings.TipsUnused) return null;

            double longest = ws.Where(w => w.Spent == null).Select(w => w.Length.Value.TotalHours).DefaultIfEmpty(0).Max();
            UsageWindow best = null;
            double bestUnused = 0;
            foreach (var w in ws)
            {
                if (w.Used >= 100) continue;
                // An enclosing general limit determines a model's effective remaining quota.
                // Only that general limit should receive the corresponding tip.
                if (p.Usage.ParentOf(w) != null) continue;
                var left = w.ResetsAt.Value - now;
                if (left > Lead(w)) continue;
                double unused = Unused(w, left, now);
                bool isLong = w.Spent != null || w.Length.Value.TotalHours >= longest * 0.9;
                if (isLong ? unused < 30 : !settings.TipsShort || unused < 40) continue;
                // Only recommend short-window usage when the long windows have room to absorb it.
                if (!isLong && ws.Any(x => x.Spent == null && x.Length.Value.TotalHours >= longest * 0.9 && x.Remaining < unused)) continue;
                if (unused > bestUnused) { best = w; bestUnused = unused; }
            }
            if (best == null) return null;

            string label = Ui.LabelOf(best), at = When(best.ResetsAt.Value);
            var tip = new Tip { Key = p.Id + "|" + best.Key, Kind = "unused" };
            if (best.Spent != null)
            {
                string amount = best.Spent.Format(Math.Max(0, best.Spent.Limit - best.Spent.Used));
                tip.Title = L.F("{0} · {1}: {2} still unused", p.Name, label, amount);
                tip.Text = L.F("It resets {0} and unused credit does not carry over.", at);
                tip.Line = L.F("{0}: {1} still unused, resets {2}", label, amount, at);
            }
            else
            {
                double left = Math.Round(best.Remaining);
                tip.Title = L.F("{0} · {1}: {2}% still unused", p.Name, label, left);
                tip.Text = L.F("It resets {0}. At your usual pace about {1}% would go unused: a good moment to use it.", at, Math.Round(bestUnused));
                tip.Line = L.F("{0}: {1}% still unused, resets {2}", label, left, at);
            }
            return tip;
        }

        /// <summary>Tip lead time is 15% of the window, bounded between half an hour and four days.</summary>
        private static TimeSpan Lead(UsageWindow w)
        {
            double h = w.Length.Value.TotalHours * 0.15;
            return TimeSpan.FromHours(Math.Max(0.5, Math.Min(96, h)));
        }

        /// <summary>
        /// Estimate unused quota using recent pace for imminent resets.
        /// For longer horizons, use the window average, including overnight and idle time.
        /// </summary>
        private static double Unused(UsageWindow w, TimeSpan left, DateTime now)
        {
            double pace;
            if (left.TotalHours <= 6 && w.PacePerHour.HasValue) pace = w.PacePerHour.Value;
            else
            {
                double elapsed = (now - (w.ResetsAt.Value - w.Length.Value)).TotalHours;
                pace = elapsed >= 0.25 ? w.Used / elapsed : 0;
            }
            return Math.Max(0, w.Remaining - pace * left.TotalHours);
        }

        private static Tuple<string, string> Low(Snapshot s, ProviderState p, UsageWindow w)
        {
            string title = L.F("{0} · {1}: {2}% left", p.Name, Ui.LabelOf(w), Math.Round(w.Remaining));
            var text = new StringBuilder();
            text.Append(L.F("Resets {0}.", When(w.ResetsAt.Value)));
            if (w.RunsOutAt.HasValue) text.Append(' ').Append(L.F("At this pace it runs out {0}.", When(w.RunsOutAt.Value)));
            // Suggest another service with enough room to continue working.
            var other = s.Providers.Where(x => x != p && x.Models == null)
                .Select(x => Tuple.Create(x, Ui.Primary(x)))
                .Where(x => x.Item2 != null && x.Item1.Usage.Room(x.Item2) >= 40)
                .OrderByDescending(x => x.Item1.Usage.Room(x.Item2)).FirstOrDefault();
            if (other != null) text.Append(' ').Append(L.F("{0} still has {1}% left.", other.Item1.Name, Math.Round(other.Item1.Usage.Room(other.Item2))));
            return Tuple.Create(title, text.ToString());
        }

        /// <summary>Format a local time as today, tomorrow or a weekday.</summary>
        public static string When(DateTime t)
        {
            if (t.Date == DateTime.Today) return L.F("at {0}", t.ToString("HH:mm", L.Culture));
            if (t.Date == DateTime.Today.AddDays(1)) return L.F("tomorrow at {0}", t.ToString("HH:mm", L.Culture));
            return L.F("on {0} at {1}", t.ToString("dddd", L.Culture), t.ToString("HH:mm", L.Culture));
        }

        private static bool Parse(string stamp, out DateTime t)
        {
            return DateTime.TryParseExact(stamp, Stamp, CultureInfo.InvariantCulture, DateTimeStyles.None, out t);
        }

        /// <summary>Reset times within ten minutes belong to the same window, matching forecast handling.</summary>
        private static bool Same(string a, string b)
        {
            DateTime x, y;
            if (a == b) return true;
            return Parse(a, out x) && Parse(b, out y) && Math.Abs((x - y).TotalMinutes) <= 10;
        }

        private void Save()
        {
            try
            {
                // Retain recent sightings and reset windows for ten days.
                var keep = states.Where(kv =>
                {
                    DateTime r, seen;
                    return (Parse(kv.Value.Reset, out r) && r > DateTime.Now.AddDays(-10))
                        || (Parse(kv.Value.Seen, out seen) && seen >= DateTime.Now.AddDays(-10));
                }).ToDictionary(kv => kv.Key, kv => (object)new Dictionary<string, object>
                {
                    { "reset", kv.Value.Reset }, { "level", kv.Value.Level }, { "name", kv.Value.Name }, { "label", kv.Value.Label },
                    { "announced", kv.Value.Announced }, { "tip", kv.Value.Tip }, { "seen", kv.Value.Seen }, { "identity", kv.Value.Identity },
                });
                Directory.CreateDirectory(Settings.Folder);
                File.WriteAllText(FilePath, Json.Serialize(keep), new UTF8Encoding(false));
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
