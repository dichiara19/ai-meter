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
        public string Label;      // English translation key, resolved by Ui.LabelOf when displayed.
        public string Suffix;     // Untranslated suffix, such as a model name.
        public string Short;      // Compact pill label, such as "5h" or "7g".
        public double Used;       // Percentage used, from 0 to 100.
        public DateTime? ResetsAt; // Local time.
        public DateTime? StartedAt; // Window start, when known or inferred from its duration.
        public Money Spent;       // Amount spent and monetary limit.
        public string RightText;  // Text or a translation key replacing the percentage, such as "unlimited".
        public Func<string> Detail; // Tooltip details resolved in the active language.
        public bool NoBar;        // A standalone value, without a percentage or bar.
        public DateTime? RunsOutAt; // Forecast exhaustion time, when earlier than the reset.
        public double? PacePerHour; // Estimated percentage points used per hour, when enough samples exist.
        public bool Budget;       // A user spending budget; unused funds do not expire.
        public bool Hidden;       // Pill-only metric; the card presents these values in its spending summary.
        public DateTime? CachedAt; // Last successful extra response, when a later request failed.
        public double Remaining { get { return Math.Max(0, Math.Min(100, 100 - Used)); } }

        /// <summary>Stable identity within a service for preferences, notifications and forecasts.</summary>
        public string Key { get { return Label + "|" + Suffix; } }

        /// <summary>Window duration from its boundaries or known limit type; null if unknown.</summary>
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

        /// <summary>Whether a current percentage is available for bars, alerts and forecasts.</summary>
        public bool HasPercent { get { return !NoBar && RightText == null && !CachedAt.HasValue; } }
    }

    /// <summary>Today's model usage reported by a local agent, including its computed cost and tokens.</summary>
    internal sealed class ModelCost
    {
        public string Model;
        public double Cost;
        public long Tokens;
        public int Replies;
    }

    /// <summary>A service tip displayed in its card and notified once per window.</summary>
    internal sealed class Tip
    {
        public string Key;        // Provider and limit identity for notification deduplication.
        public string Kind;       // "unused" or "wait".
        public string Title, Text;
        public string Line;       // Short text for the service card.
    }

    /// <summary>Spending against a limit, expressed in the service's currency.</summary>
    internal sealed class Money
    {
        public double Used;
        public double Limit;
        public string Currency;   // "USD", "EUR"…

        public string Format(double v)
        {
            string sym = Currency == "EUR" ? "€" : Currency == "USD" ? "$" : Currency == "GBP" ? "£" : Currency == "CNY" ? "¥" : Currency;
            // Use the active locale's currency formatting with the service's currency symbol.
            var nf = (NumberFormatInfo)L.Culture.NumberFormat.Clone();
            nf.CurrencySymbol = sym;
            return v.ToString("C2", nf);
        }
    }

    internal sealed class Usage
    {
        public List<UsageWindow> Windows = new List<UsageWindow>();
        public string Source;     // "live", "cache", "log"
        public DateTime At;       // Time the data was collected.
        public string Identity;   // Opaque credential identity for forecast and alert isolation.
        /// <summary>Weekly usage breakdown, such as Claude Code versus Cowork.</summary>
        public List<KeyValuePair<string, double>> Breakdown = new List<KeyValuePair<string, double>>();
        /// <summary>Extra-credit or pay-as-you-go status resolved in the active language.</summary>
        public Func<string> CreditsNote;
        /// <summary>Whether paid usage beyond the plan's limits is enabled.</summary>
        public bool ExtraActive;
        /// <summary>Today's, this week's and this month's spending in the active language.</summary>
        public Func<string> SpendNote;
        /// <summary>Today's models used through this service, as reported by OpenCode and Pi.</summary>
        public List<ModelCost> TodayModels;

        /// <summary>Copy usage with additional windows, preserving the original provider response.</summary>
        public Usage With(IEnumerable<UsageWindow> extra)
        {
            var u = new Usage { Source = Source, At = At, Identity = Identity, Breakdown = Breakdown, CreditsNote = CreditsNote, ExtraActive = ExtraActive, SpendNote = SpendNote, TodayModels = TodayModels };
            u.Windows.AddRange(Windows);
            u.Windows.AddRange(extra);
            return u;
        }

        /// <summary>
        /// Find the enclosing limit with the same label and no model or group suffix.
        /// Model usage also consumes that limit, so available room is the smaller of the two.
        /// </summary>
        public UsageWindow ParentOf(UsageWindow w)
        {
            if (w.Suffix == null) return null;
            foreach (var x in Windows) if (x != w && x.Suffix == null && x.Label == w.Label && x.HasPercent && x.Spent == null) return x;
            return null;
        }

        /// <summary>Effective room, capped by the enclosing limit when necessary.</summary>
        public double Room(UsageWindow w)
        {
            var p = ParentOf(w);
            return p == null ? w.Remaining : Math.Min(w.Remaining, p.Remaining);
        }

        /// <summary>Return the same data marked as cached.</summary>
        public Usage AsCache()
        {
            return new Usage { Windows = Windows, Source = "cache", At = At, Identity = Identity, Breakdown = Breakdown, CreditsNote = CreditsNote, ExtraActive = ExtraActive, SpendNote = SpendNote, TodayModels = TodayModels };
        }
    }

    internal sealed class Session
    {
        public string Kind;       // "Code · Desktop", "Cowork", "Codex · CLI", "Chat"…
        public string Title;
        public string Status;     // "busy", "idle", "app"
        public string Model;
        public DateTime Updated;
        public string Note;       // Additional row details, such as context usage.
    }

    internal sealed class ProviderState
    {
        public string Id;         // "claude", "openai"
        public string Name;
        public string Plan;
        public bool Running;
        public List<Session> Sessions = new List<Session>();
        public Usage Usage;
        public string Notice;     // Connection status message, when needed.
        public bool NeedsLogin;
        public bool CanSignIn;    // Whether the notice offers a sign-in action managed by AI Meter.
        public List<LocalModel> Models;   // Loaded models, for local providers only.
        public long GpuUsed, GpuTotal;    // GPU memory in bytes, for local providers only.
        public Tip Tip;           // Active tip, evaluated for each snapshot.

        public int Active { get { int n = 0; foreach (var s in Sessions) if (s.Status != "app") n++; return n; } }
        public int Busy { get { int n = 0; foreach (var s in Sessions) if (s.Status == "busy") n++; return n; } }
    }

    /// <summary>A model loaded by a local server on Windows or WSL.</summary>
    internal sealed class LocalModel
    {
        public string Name;
        public string Server;      // "Ollama", "LM Studio", "llama.cpp"…
        public string Where;       // "Windows" or "WSL".
        public string Detail;      // Parameter count and quantization, when known.
        public long Vram, Ram;     // GPU and system memory in bytes; zero means unknown.
        public int Context;        // Loaded context size in tokens; zero means unknown.
        public int ContextUsed;    // Tokens in the largest active request; zero means unknown.
        public int Busy, Slots;    // Active requests and available slots; zero means unknown.
        public List<Session> Sessions = new List<Session>();   // CLI sessions using this model.

        public string Key { get { return Server + "|" + Where + "|" + Name; } }
    }

    internal sealed class Snapshot
    {
        public DateTime Taken = DateTime.Now;
        public List<ProviderState> Providers = new List<ProviderState>();

        /// <summary>Compact render signature, including the current minute for time-sensitive text.</summary>
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
                    foreach (var w in p.Usage.Windows)
                    {
                        sb.Append(w.Key).Append((int)w.Used).Append(w.RightText).Append(w.CachedAt).Append(w.ResetsAt);
                        if (w.Spent != null) sb.Append(w.Spent.Used.ToString("R", CultureInfo.InvariantCulture)).Append('/').Append(w.Spent.Limit.ToString("R", CultureInfo.InvariantCulture));
                        sb.Append(w.RunsOutAt.HasValue ? w.RunsOutAt.Value.Ticks / TimeSpan.TicksPerMinute / 5 : 0);
                    }
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

        /// <summary>Bytes read since the last memory compaction.</summary>
        public static long BytesRead;

        public static Dictionary<string, object> ParseFile(string path)
        {
            try { string text = File.ReadAllText(path, Encoding.UTF8); BytesRead += text.Length; return Parse(text); }
            catch (IOException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
        }

        /// <summary>Parse any JSON value; return null for invalid input.</summary>
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
