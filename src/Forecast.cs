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
                    w.PacePerHour = null;
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
                    w.PacePerHour = Pace(t.Samples, w, now);
                    if (w.PacePerHour > 0.05)
                    {
                        var at = now.AddHours((100 - w.Used) / w.PacePerHour.Value);
                        if (at < w.ResetsAt.Value) w.RunsOutAt = at;
                    }
                }
            }
        }

        /// <summary>Punti percentuali all'ora; null se non ci sono ancora abbastanza letture.</summary>
        private static double? Pace(List<Sample> samples, UsageWindow w, DateTime now)
        {
            double? perHour = null;
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
                if (den > 1e-9) perHour = Math.Max(0, (n * sxy - sx * sy) / den);
            }
            // altrimenti la media dall'inizio della finestra (dopo almeno 15 minuti e un uso non trascurabile)
            else if (w.StartedAt.HasValue && (now - w.StartedAt.Value).TotalMinutes >= 15 && w.Used >= 3)
                perHour = w.Used / (now - w.StartedAt.Value).TotalHours;
            return perHour;
        }
    }

    /// <summary>
    /// Notifiche di Windows: quando un limite scende sotto il 20% e il 5%, quando torna disponibile dopo essere stato
    /// quasi esaurito, a ogni azzeramento dei limiti scelti, e i suggerimenti. Una volta sola per soglia e finestra,
    /// anche dopo un riavvio (stato in alerts.json).
    /// </summary>
    internal sealed class Alerts
    {
        private sealed class State { public string Reset; public int Level; public string Name, Label, Announced, Tip; }
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
                                Announced = Json.Str(v, "announced"), Tip = Json.Str(v, "tip"),
                            };
                    }
            }
            catch (Exception) { }
        }

        /// <summary>Le notifiche da mostrare per questa raccolta: titolo e testo. Imposta anche i suggerimenti dei servizi.</summary>
        public List<Tuple<string, string>> Check(Snapshot s, Settings settings)
        {
            var result = new List<Tuple<string, string>>();
            bool changed = false;
            var now = DateTime.Now;

            // azzeramenti dei limiti scelti: all'ora comunicata dal servizio, anche senza una lettura nuova
            foreach (var kv in states)
            {
                var st = kv.Value;
                DateTime at;
                bool on;
                if (st.Announced == st.Reset || !Parse(st.Reset, out at) || at > now) continue;
                if (!settings.NotifyResets.TryGetValue(kv.Key, out on) || !on) continue;
                string id = kv.Key.Substring(0, Math.Max(0, kv.Key.IndexOf('|')));
                if (!s.Providers.Any(p => p.Id == id)) continue;   // servizio spento
                st.Announced = st.Reset;
                changed = true;
                // PC spento o bloccato all'ora dell'azzeramento: niente avviso in ritardo
                if ((now - at).TotalMinutes <= 30)
                    result.Add(Tuple.Create(L.F("{0} · {1} has reset", st.Name, st.Label), L.T("The full limit is available again.")));
            }

            foreach (var p in s.Providers)
            {
                if (p.Usage == null || p.Usage.Source == "cache") continue;
                foreach (var w in p.Usage.Windows)
                {
                    if (!w.HasPercent || !w.ResetsAt.HasValue) continue;
                    string key = p.Id + "|" + w.Key;
                    string reset = w.ResetsAt.Value.ToString(Stamp, CultureInfo.InvariantCulture);
                    State st;
                    states.TryGetValue(key, out st);
                    // finestra nuova: se la precedente era quasi esaurita, il limite è di nuovo disponibile
                    // (se l'azzeramento è già stato annunciato perché scelto nelle impostazioni, non si ripete).
                    // Un orario che oscilla di qualche minuto tra le letture è la stessa finestra: si tiene il primo.
                    if (st != null && !Same(st.Reset, reset))
                    {
                        if (st.Level >= 80 && w.Used < 50 && settings.NotifyReset && st.Announced != st.Reset)
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

            // suggerimenti: la riga nella scheda resta finché vale, la notifica una volta per finestra
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

            // scelte su limiti che il servizio non comunica più da oltre 10 giorni (es. è cambiato il modello del limite
            // settimanale): la pillola torna al predefinito, e lo si dice; l'avviso di azzeramento si toglie in silenzio
            bool settingsChanged = false;
            foreach (var p in s.Providers)
            {
                if (p.Usage == null || p.Usage.Source != "live") continue;
                string choice;
                if (settings.PillLimits.TryGetValue(p.Id, out choice) && choice != "auto" && !string.IsNullOrEmpty(choice) && Gone(p, choice))
                {
                    settings.PillLimits.Remove(p.Id);
                    settingsChanged = true;
                    result.Add(Tuple.Create(L.F("{0}: limit in the taskbar back to Default", p.Name),
                        L.F("{0} is no longer reported by the service.", Ui.LabelOf(FromKey(choice)))));
                }
                foreach (var key in settings.NotifyResets.Keys.Where(k => k.StartsWith(p.Id + "|", StringComparison.Ordinal)).ToList())
                    if (Gone(p, key.Substring(p.Id.Length + 1))) { settings.NotifyResets.Remove(key); settingsChanged = true; }
            }
            if (settingsChanged) settings.Save();

            if (changed) Save();
            return result;
        }

        /// <summary>Né nella lettura attuale né tra le finestre viste negli ultimi 10 giorni.</summary>
        private bool Gone(ProviderState p, string windowKey)
        {
            return !p.Usage.Windows.Any(w => w.Key == windowKey) && !states.ContainsKey(p.Id + "|" + windowKey);
        }

        private static UsageWindow FromKey(string key)
        {
            int bar = key.IndexOf('|');
            string suffix = bar < 0 ? "" : key.Substring(bar + 1);
            return new UsageWindow { Label = bar < 0 ? key : key.Substring(0, bar), Suffix = suffix.Length > 0 ? suffix : null };
        }

        /// <summary>
        /// Il suggerimento per un servizio, uguale per ogni servizio e ogni durata di limite.
        /// - Aspettare: un limite esaurito si azzera a breve mentre si paga l'uso extra.
        /// - Margine inutilizzato: all'azzeramento manca circa il 15% della finestra (tra mezz'ora e 4 giorni: 45 minuti
        ///   per 5 ore, un giorno per la settimana, 4 giorni per il mese) e, al ritmo abituale, ne resterebbe inutilizzato
        ///   almeno il 30%. Vale per i limiti più lunghi del servizio e per i crediti in denaro: quello che resta si perde.
        ///   I limiti più brevi pesano su quelli lunghi, quindi solo se attivati e se quelli lunghi hanno spazio.
        /// </summary>
        public static Tip TipFor(ProviderState p, Settings settings, DateTime now)
        {
            if (p.Usage == null || p.Models != null) return null;
            var ws = p.Usage.Windows.Where(w => w.HasPercent && w.ResetsAt.HasValue && w.ResetsAt.Value > now && w.Length.HasValue).ToList();
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
                // limite di un modello dentro uno generale (es. Settimana · Fable dentro Settimana): quello che resta
                // davvero è del limite generale, ed è lui ad avere il suo suggerimento
                if (p.Usage.ParentOf(w) != null) continue;
                var left = w.ResetsAt.Value - now;
                if (left > Lead(w)) continue;
                double unused = Unused(w, left, now);
                bool isLong = w.Spent != null || w.Length.Value.TotalHours >= longest * 0.9;
                if (isLong ? unused < 30 : !settings.TipsShort || unused < 40) continue;
                // un limite breve pesa su quelli lunghi: si suggerisce solo se quelli hanno spazio per assorbirlo
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

        /// <summary>Quanto prima dell'azzeramento si suggerisce: il 15% della finestra, tra mezz'ora e 4 giorni.</summary>
        private static TimeSpan Lead(UsageWindow w)
        {
            double h = w.Length.Value.TotalHours * 0.15;
            return TimeSpan.FromHours(Math.Max(0.5, Math.Min(96, h)));
        }

        /// <summary>
        /// Percentuale che resterebbe all'azzeramento. Ritmo: quello recente se mancano poche ore (è il lavoro in corso),
        /// altrimenti la media dall'inizio della finestra, che comprende notti e pause.
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
            // un altro servizio con margine: il consiglio per continuare a lavorare
            var other = s.Providers.Where(x => x != p && x.Models == null)
                .Select(x => Tuple.Create(x, Ui.Primary(x)))
                .Where(x => x.Item2 != null && x.Item1.Usage.Room(x.Item2) >= 40)
                .OrderByDescending(x => x.Item1.Usage.Room(x.Item2)).FirstOrDefault();
            if (other != null) text.Append(' ').Append(L.F("{0} still has {1}% left.", other.Item1.Name, Math.Round(other.Item1.Usage.Room(other.Item2))));
            return Tuple.Create(title, text.ToString());
        }

        /// <summary>"alle 15:40", "domani alle 09:00" o il giorno della settimana.</summary>
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

        /// <summary>Stessa finestra: azzeramenti a meno di 10 minuti l'uno dall'altro (come nella previsione).</summary>
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
                // si tengono solo le finestre degli ultimi 10 giorni
                var keep = states.Where(kv =>
                {
                    DateTime r;
                    return Parse(kv.Value.Reset, out r) && r > DateTime.Now.AddDays(-10);
                }).ToDictionary(kv => kv.Key, kv => (object)new Dictionary<string, object>
                {
                    { "reset", kv.Value.Reset }, { "level", kv.Value.Level }, { "name", kv.Value.Name }, { "label", kv.Value.Label },
                    { "announced", kv.Value.Announced }, { "tip", kv.Value.Tip },
                });
                Directory.CreateDirectory(Settings.Folder);
                File.WriteAllText(FilePath, Json.Serialize(keep), new UTF8Encoding(false));
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
