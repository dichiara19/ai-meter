using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;

namespace AIMeter
{
    internal sealed class UsageWindow
    {
        public string Label;      // chiave inglese, tradotta al momento di mostrarla (Ui.LabelOf)
        public string Suffix;     // aggiunta non tradotta, es. il nome del modello
        public string Short;      // etichetta breve per la pillola: "5h", "7g"
        public double Used;       // percentuale usata 0-100
        public DateTime? ResetsAt; // ora locale
        public DateTime? StartedAt; // inizio della finestra, se noto o ricavabile (fine − durata)
        public Money Spent;       // per i crediti in denaro: speso e limite
        public string RightText;  // chiave inglese al posto della percentuale (es. "unlimited")
        public Func<string> Detail; // dettaglio per il suggerimento, calcolato nella lingua attiva
        public bool NoBar;        // solo un valore (es. un saldo), senza barra né percentuale
        public DateTime? RunsOutAt; // previsione: a questo ritmo il limite finisce prima dell'azzeramento
        public double? PacePerHour; // previsione: punti percentuali usati all'ora, se ci sono abbastanza letture
        public bool Budget;       // budget di spesa scelto dall'utente: non è un credito che scade (niente "usalo o lo perdi")
        public bool Hidden;       // solo da scegliere per la pillola (es. "speso oggi"); nella scheda c'è la riga della spesa
        public double Remaining { get { return Math.Max(0, Math.Min(100, 100 - Used)); } }

        /// <summary>Identità del limite nel suo servizio, stabile tra le letture (impostazioni, notifiche, previsioni).</summary>
        public string Key { get { return Label + "|" + Suffix; } }

        /// <summary>Durata della finestra: dall'inizio all'azzeramento, o dedotta dal tipo di limite. Null se non nota.</summary>
        public TimeSpan? Length
        {
            get
            {
                if (StartedAt.HasValue && ResetsAt.HasValue && ResetsAt.Value > StartedAt.Value) return ResetsAt.Value - StartedAt.Value;
                if (Short == "5h" || Label == "Session 5 h") return TimeSpan.FromHours(5);
                if (Short == "7g" || Label == "Week") return TimeSpan.FromDays(7);
                if (Short == "mese") return TimeSpan.FromDays(30);
                if (Short == "1g") return TimeSpan.FromDays(1);
                return null;
            }
        }

        /// <summary>Un limite in percentuale (con barra), non un saldo né un valore solo testuale.</summary>
        public bool HasPercent { get { return !NoBar && RightText == null; } }
    }

    /// <summary>Un modello usato oggi da un agente del PC con un servizio: costo (nella valuta del servizio) e token.</summary>
    internal sealed class ModelCost
    {
        public string Model;
        public double Cost;
        public long Tokens;
        public int Replies;
    }

    /// <summary>Suggerimento attivo per un servizio: una riga nella scheda e, una volta per finestra, una notifica.</summary>
    internal sealed class Tip
    {
        public string Key;        // servizio|limite a cui si riferisce: una notifica sola per ogni finestra
        public string Kind;       // "unused" o "wait"
        public string Title, Text;
        public string Line;       // testo breve per la scheda del servizio
    }

    /// <summary>Importo speso su un limite, nella valuta del servizio.</summary>
    internal sealed class Money
    {
        public double Used;
        public double Limit;
        public string Currency;   // "USD", "EUR"…

        public string Format(double v)
        {
            string sym = Currency == "EUR" ? "€" : Currency == "USD" ? "$" : Currency == "GBP" ? "£" : Currency == "CNY" ? "¥" : Currency;
            // formato valuta della lingua attiva (es. $226.72 in inglese, 226,72 $ in italiano), con il simbolo del servizio
            var nf = (NumberFormatInfo)L.Culture.NumberFormat.Clone();
            nf.CurrencySymbol = sym;
            return v.ToString("C2", nf);
        }
    }

    internal sealed class Usage
    {
        public List<UsageWindow> Windows = new List<UsageWindow>();
        public string Source;     // "live", "cache", "log"
        public DateTime At;       // quando il dato è stato rilevato
        /// <summary>Come si ripartisce l'uso settimanale (es. Claude Code 98%, Cowork 2%).</summary>
        public List<KeyValuePair<string, double>> Breakdown = new List<KeyValuePair<string, double>>();
        /// <summary>Riga di stato dei crediti extra / a consumo, calcolata nella lingua attiva al momento di mostrarla.</summary>
        public Func<string> CreditsNote;
        /// <summary>L'uso oltre i limiti del piano è attivo e si paga a consumo (es. extra usage di Claude).</summary>
        public bool ExtraActive;
        /// <summary>Spesa di oggi, della settimana e del mese, nella lingua attiva (servizi a credito).</summary>
        public Func<string> SpendNote;
        /// <summary>Modelli usati oggi con questo servizio dagli agenti del PC (OpenCode, Pi): costo e token.</summary>
        public List<ModelCost> TodayModels;

        /// <summary>Copia con altre finestre (budget, spesa del periodo), senza toccare quella letta dal servizio.</summary>
        public Usage With(IEnumerable<UsageWindow> extra)
        {
            var u = new Usage { Source = Source, At = At, Breakdown = Breakdown, CreditsNote = CreditsNote, ExtraActive = ExtraActive, SpendNote = SpendNote, TodayModels = TodayModels };
            u.Windows.AddRange(Windows);
            u.Windows.AddRange(extra);
            return u;
        }

        /// <summary>
        /// Il limite generale che contiene questo: stessa etichetta senza modello o gruppo (es. "Settimana" per
        /// "Settimana · Fable"). L'uso del modello conta anche lì, quindi il margine vero è il più basso dei due.
        /// </summary>
        public UsageWindow ParentOf(UsageWindow w)
        {
            if (w.Suffix == null) return null;
            foreach (var x in Windows) if (x != w && x.Suffix == null && x.Label == w.Label && x.HasPercent && x.Spent == null) return x;
            return null;
        }

        /// <summary>Margine reale di un limite: il suo, o quello del limite generale che lo contiene se è più basso.</summary>
        public double Room(UsageWindow w)
        {
            var p = ParentOf(w);
            return p == null ? w.Remaining : Math.Min(w.Remaining, p.Remaining);
        }

        /// <summary>Stessi dati, marcati come non più in tempo reale.</summary>
        public Usage AsCache()
        {
            return new Usage { Windows = Windows, Source = "cache", At = At, Breakdown = Breakdown, CreditsNote = CreditsNote, ExtraActive = ExtraActive, SpendNote = SpendNote, TodayModels = TodayModels };
        }
    }

    internal sealed class Session
    {
        public string Kind;       // "Code · Desktop", "Cowork", "Codex · CLI", "Chat"…
        public string Title;
        public string Status;     // "busy", "idle", "app"
        public string Model;
        public DateTime Updated;
        public string Note;       // dettaglio aggiuntivo nella riga (es. contesto occupato)
    }

    internal sealed class ProviderState
    {
        public string Id;         // "claude", "openai"
        public string Name;
        public string Plan;
        public bool Running;
        public List<Session> Sessions = new List<Session>();
        public Usage Usage;
        public string Notice;     // messaggio di stato del collegamento, se serve
        public bool NeedsLogin;
        public bool CanSignIn;    // l'avviso offre il collegamento "Accedi" (accesso gestito da AI Meter)
        public List<LocalModel> Models;   // solo per i modelli locali: i modelli caricati in memoria
        public long GpuUsed, GpuTotal;    // solo per i modelli locali: memoria video della scheda (byte), letta nella raccolta
        public Tip Tip;           // suggerimento attivo (calcolato a ogni lettura)

        public int Active { get { int n = 0; foreach (var s in Sessions) if (s.Status != "app") n++; return n; } }
        public int Busy { get { int n = 0; foreach (var s in Sessions) if (s.Status == "busy") n++; return n; } }
    }

    /// <summary>Un modello caricato da un server locale (Ollama, LM Studio, llama.cpp…), su Windows o in WSL.</summary>
    internal sealed class LocalModel
    {
        public string Name;
        public string Server;      // "Ollama", "LM Studio", "llama.cpp"…
        public string Where;       // "Windows" o "WSL"
        public string Detail;      // parametri e quantizzazione, se noti
        public long Vram, Ram;     // byte in memoria video e di sistema, 0 = sconosciuto
        public int Context;        // finestra di contesto caricata (token), 0 = sconosciuta
        public int ContextUsed;    // token nella richiesta più lunga in corso, 0 = sconosciuto
        public int Busy, Slots;    // richieste in corso e posti disponibili (0 = sconosciuti)
        public List<Session> Sessions = new List<Session>();   // sessioni delle CLI che lo usano

        public string Key { get { return Server + "|" + Where + "|" + Name; } }
    }

    internal sealed class Snapshot
    {
        public DateTime Taken = DateTime.Now;
        public List<ProviderState> Providers = new List<ProviderState>();

        /// <summary>Firma compatta: l'interfaccia si ridisegna solo se cambia (o cambia il minuto).</summary>
        public string Signature()
        {
            var sb = new StringBuilder();
            sb.Append(DateTime.Now.ToString("HHmm"));
            foreach (var p in Providers)
            {
                sb.Append('|').Append(p.Id).Append(p.Running).Append(p.Plan).Append(p.Notice).Append(p.NeedsLogin).Append(p.Tip != null ? p.Tip.Line : "");
                if (p.Usage != null)
                {
                    sb.Append(p.Usage.Source);
                    foreach (var w in p.Usage.Windows) sb.Append(w.Label).Append((int)w.Used).Append(w.RightText).Append(w.ResetsAt).Append(w.RunsOutAt.HasValue ? w.RunsOutAt.Value.Ticks / TimeSpan.TicksPerMinute / 5 : 0);
                    if (p.Usage.TodayModels != null) foreach (var m in p.Usage.TodayModels) sb.Append(m.Model).Append((int)(m.Cost * 100)).Append(m.Tokens >> 10);
                }
                foreach (var s in p.Sessions) sb.Append(s.Kind).Append(s.Title).Append(s.Status).Append(s.Model).Append(s.Note);
                if (p.Models != null) sb.Append(p.GpuUsed >> 28);
                if (p.Models != null)
                    foreach (var m in p.Models)
                    {
                        sb.Append(m.Key).Append(m.Vram >> 26).Append(m.Ram >> 26).Append(m.Context).Append(m.ContextUsed >> 10).Append(m.Busy);
                        foreach (var s in m.Sessions) sb.Append(s.Title).Append(s.Status);
                    }
            }
            return sb.ToString();
        }
    }

    internal static class Json
    {
        private static readonly JavaScriptSerializer Ser = new JavaScriptSerializer { MaxJsonLength = int.MaxValue, RecursionLimit = 64 };

        public static Dictionary<string, object> Parse(string text)
        {
            try { return Ser.DeserializeObject(text) as Dictionary<string, object>; }
            catch (ArgumentException) { return null; }
            catch (InvalidOperationException) { return null; }
        }

        /// <summary>Byte letti da disco dall'ultima compattazione della memoria.</summary>
        public static long BytesRead;

        public static Dictionary<string, object> ParseFile(string path)
        {
            try { string text = File.ReadAllText(path, Encoding.UTF8); BytesRead += text.Length; return Parse(text); }
            catch (IOException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
        }

        /// <summary>Qualsiasi valore JSON (oggetto, elenco, numero…), null se non valido.</summary>
        public static object ParseAny(string text)
        {
            try { return Ser.DeserializeObject(text); }
            catch (ArgumentException) { return null; }
            catch (InvalidOperationException) { return null; }
        }

        public static string Serialize(object o) { return Ser.Serialize(o); }
        public static T Deserialize<T>(string s) { return Ser.Deserialize<T>(s); }

        public static Dictionary<string, object> Obj(Dictionary<string, object> d, string key)
        {
            object v;
            return d != null && d.TryGetValue(key, out v) ? v as Dictionary<string, object> : null;
        }

        public static string Str(Dictionary<string, object> d, string key)
        {
            object v;
            return d != null && d.TryGetValue(key, out v) && v != null ? Convert.ToString(v, CultureInfo.InvariantCulture) : null;
        }

        public static double? Num(Dictionary<string, object> d, string key)
        {
            object v;
            if (d == null || !d.TryGetValue(key, out v) || v == null) return null;
            try { return Convert.ToDouble(v, CultureInfo.InvariantCulture); }
            catch (FormatException) { return null; }
            catch (InvalidCastException) { return null; }
        }

        public static bool Bool(Dictionary<string, object> d, string key)
        {
            object v;
            return d != null && d.TryGetValue(key, out v) && v is bool && (bool)v;
        }

        public static DateTime? FromUnixMs(double? ms)
        {
            if (ms == null || ms <= 0) return null;
            return new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddMilliseconds(ms.Value).ToLocalTime();
        }

        public static DateTime? FromUnix(double? s) { return s == null ? null : FromUnixMs(s * 1000); }

        public static DateTime? FromIso(string s)
        {
            DateTime t;
            if (string.IsNullOrEmpty(s)) return null;
            if (DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out t))
                return DateTime.SpecifyKind(t, DateTimeKind.Utc).ToLocalTime();
            return null;
        }
    }
}
