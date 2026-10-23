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
        public string Label;
        public string Short;      // etichetta breve per la pillola: "5h", "7g"
        public double Used;       // percentuale usata 0-100
        public DateTime? ResetsAt; // ora locale
        public DateTime? StartedAt; // inizio della finestra, se noto o ricavabile (fine − durata)
        public Money Spent;       // per i crediti in denaro: speso e limite
        public string RightText;  // testo al posto della percentuale (es. "illimitate")
        public string Detail;     // dettaglio per il suggerimento (es. "212 di 300 rimaste")
        public double Remaining { get { return Math.Max(0, Math.Min(100, 100 - Used)); } }
    }

    /// <summary>Importo speso su un limite, nella valuta del servizio.</summary>
    internal sealed class Money
    {
        public double Used;
        public double Limit;
        public string Currency;   // "USD", "EUR"…

        public string Format(double v)
        {
            string sym = Currency == "EUR" ? "€" : Currency == "USD" ? "$" : Currency == "GBP" ? "£" : Currency;
            return v.ToString("0.00", new CultureInfo("it-IT")) + " " + sym;
        }
    }

    internal sealed class Usage
    {
        public List<UsageWindow> Windows = new List<UsageWindow>();
        public string Source;     // "live", "cache", "log"
        public DateTime At;       // quando il dato è stato rilevato
        /// <summary>Come si ripartisce l'uso settimanale (es. Claude Code 98%, Cowork 2%).</summary>
        public List<KeyValuePair<string, double>> Breakdown = new List<KeyValuePair<string, double>>();
        /// <summary>Riga di stato dei crediti extra / a consumo, se il servizio li prevede.</summary>
        public string CreditsNote;

        /// <summary>Stessi dati, marcati come non più in tempo reale.</summary>
        public Usage AsCache()
        {
            return new Usage { Windows = Windows, Source = "cache", At = At, Breakdown = Breakdown, CreditsNote = CreditsNote };
        }
    }

    internal sealed class Session
    {
        public string Kind;       // "Code · Desktop", "Cowork", "Codex · CLI", "Chat"…
        public string Title;
        public string Status;     // "busy", "idle", "app"
        public string Model;
        public DateTime Updated;
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

        public int Active { get { int n = 0; foreach (var s in Sessions) if (s.Status != "app") n++; return n; } }
        public int Busy { get { int n = 0; foreach (var s in Sessions) if (s.Status == "busy") n++; return n; } }
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
                sb.Append('|').Append(p.Id).Append(p.Running).Append(p.Plan).Append(p.Notice).Append(p.NeedsLogin);
                if (p.Usage != null)
                {
                    sb.Append(p.Usage.Source);
                    foreach (var w in p.Usage.Windows) sb.Append(w.Label).Append((int)w.Used).Append(w.ResetsAt);
                }
                foreach (var s in p.Sessions) sb.Append(s.Kind).Append(s.Title).Append(s.Status).Append(s.Model);
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
