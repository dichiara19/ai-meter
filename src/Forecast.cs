using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace AIMeter
{
    /// <summary>
    /// Previsione: a questo ritmo, quando finisce ogni limite. Il ritmo è quello dell'ultima ora e mezza (dalle letture
    /// raccolte), oppure, finché non ce ne sono abbastanza, la media dall'inizio della finestra. Se il limite basta
    /// fino all'azzeramento non si prevede nulla.
    /// </summary>
    internal sealed class Forecaster
    {
        private sealed class Sample { public DateTime At; public double Used; }
        private sealed class Track { public DateTime? Reset; public List<Sample> Samples = new List<Sample>(); }
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
                    if (w.NoBar || w.RightText != null || !w.ResetsAt.HasValue || w.Used >= 100) continue;
                    string key = p.Id + "|" + w.Label + "|" + w.Suffix;
                    Track t;
                    if (!tracks.TryGetValue(key, out t)) tracks[key] = t = new Track();
                    // finestra nuova (azzerata): le letture di prima non contano più
                    if (t.Reset.HasValue && Math.Abs((w.ResetsAt.Value - t.Reset.Value).TotalMinutes) > 10) t.Samples.Clear();
                    t.Reset = w.ResetsAt;
                    if (p.Usage.Source != "cache" && (t.Samples.Count == 0 || t.Samples[t.Samples.Count - 1].At != p.Usage.At))
                        t.Samples.Add(new Sample { At = p.Usage.At, Used = w.Used });
                    t.Samples.RemoveAll(x => (now - x.At).TotalMinutes > 90);
                    w.RunsOutAt = Project(t.Samples, w, now);
                }
            }
        }

        private static DateTime? Project(List<Sample> samples, UsageWindow w, DateTime now)
        {
            double perHour = 0;
            // ritmo recente: retta dei minimi quadrati sulle letture di almeno 10 minuti
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
                if (den > 1e-9) perHour = (n * sxy - sx * sy) / den;
            }
            // altrimenti la media dall'inizio della finestra (dopo almeno 15 minuti e un uso non trascurabile)
            else if (w.StartedAt.HasValue && (now - w.StartedAt.Value).TotalMinutes >= 15 && w.Used >= 3)
                perHour = w.Used / (now - w.StartedAt.Value).TotalHours;
            if (perHour <= 0.05) return null;
            var at = now.AddHours((100 - w.Used) / perHour);
            return at < w.ResetsAt.Value ? at : (DateTime?)null;
        }
    }

    /// <summary>
    /// Notifiche di Windows: quando un limite scende sotto il 20% e il 5% e quando torna disponibile dopo essere stato
    /// quasi esaurito. Una volta sola per soglia e finestra, anche dopo un riavvio (stato in alerts.json).
    /// </summary>
    internal sealed class Alerts
    {
        private sealed class State { public string Reset; public int Level; public string Name, Label; }
        private readonly Dictionary<string, State> states;
        private static string FilePath { get { return Path.Combine(Settings.Folder, "alerts.json"); } }

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
                        if (v != null) states[kv.Key] = new State { Reset = Json.Str(v, "reset"), Level = (int)(Json.Num(v, "level") ?? 0), Name = Json.Str(v, "name"), Label = Json.Str(v, "label") };
                    }
            }
            catch (Exception) { }
        }

        /// <summary>Le notifiche da mostrare per questa raccolta: titolo e testo.</summary>
        public List<Tuple<string, string>> Check(Snapshot s, Settings settings)
        {
            var result = new List<Tuple<string, string>>();
            bool changed = false;
            var now = DateTime.Now;
            foreach (var p in s.Providers)
            {
                if (p.Usage == null || p.Usage.Source == "cache") continue;
                foreach (var w in p.Usage.Windows)
                {
                    if (w.NoBar || w.RightText != null || !w.ResetsAt.HasValue) continue;
                    string key = p.Id + "|" + w.Label + "|" + w.Suffix;
                    string reset = w.ResetsAt.Value.ToString("yyyyMMddHHmm", CultureInfo.InvariantCulture);
                    State st;
                    states.TryGetValue(key, out st);
                    // finestra nuova: se la precedente era quasi esaurita, il limite è di nuovo disponibile
                    if (st != null && st.Reset != reset)
                    {
                        if (st.Level >= 80 && w.Used < 50 && settings.NotifyReset)
                            result.Add(Tuple.Create(L.F("{0} · {1}: available again", p.Name, Ui.LabelOf(w)),
                                L.F("The limit has reset: {0}% left.", Math.Round(w.Remaining))));
                        st = null;
                    }
                    if (st == null) { st = new State { Reset = reset }; states[key] = st; changed = true; }
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
            if (changed) Save();
            return result;
        }

        private static Tuple<string, string> Low(Snapshot s, ProviderState p, UsageWindow w)
        {
            string title = L.F("{0} · {1}: {2}% left", p.Name, Ui.LabelOf(w), Math.Round(w.Remaining));
            var text = new StringBuilder();
            text.Append(L.F("Resets {0}.", When(w.ResetsAt.Value)));
            if (w.RunsOutAt.HasValue) text.Append(' ').Append(L.F("At this pace it runs out {0}.", When(w.RunsOutAt.Value)));
            // un altro servizio con margine: il consiglio per continuare a lavorare
            var other = s.Providers.Where(x => x != p && x.Models == null)
                .Select(x => Tuple.Create(x, Ui.Primary(x.Usage)))
                .Where(x => x.Item2 != null && x.Item2.Remaining >= 40)
                .OrderByDescending(x => x.Item2.Remaining).FirstOrDefault();
            if (other != null) text.Append(' ').Append(L.F("{0} still has {1}% left.", other.Item1.Name, Math.Round(other.Item2.Remaining)));
            return Tuple.Create(title, text.ToString());
        }

        /// <summary>"alle 15:40", "domani alle 09:00" o il giorno della settimana.</summary>
        public static string When(DateTime t)
        {
            if (t.Date == DateTime.Today) return L.F("at {0}", t.ToString("HH:mm", L.Culture));
            if (t.Date == DateTime.Today.AddDays(1)) return L.F("tomorrow at {0}", t.ToString("HH:mm", L.Culture));
            return L.F("on {0} at {1}", t.ToString("dddd", L.Culture), t.ToString("HH:mm", L.Culture));
        }

        private void Save()
        {
            try
            {
                // si tengono solo le finestre degli ultimi 10 giorni
                var keep = states.Where(kv =>
                {
                    DateTime r;
                    return DateTime.TryParseExact(kv.Value.Reset, "yyyyMMddHHmm", CultureInfo.InvariantCulture, DateTimeStyles.None, out r) && r > DateTime.Now.AddDays(-10);
                }).ToDictionary(kv => kv.Key, kv => (object)new Dictionary<string, object> { { "reset", kv.Value.Reset }, { "level", kv.Value.Level }, { "name", kv.Value.Name }, { "label", kv.Value.Label } });
                Directory.CreateDirectory(Settings.Folder);
                File.WriteAllText(FilePath, Json.Serialize(keep), new UTF8Encoding(false));
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
