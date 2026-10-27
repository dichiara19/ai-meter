using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Shell;

namespace AIMeter
{
    /// <summary>
    /// Un servizio descritto da un file JSON (providers\*.json): dove trovare l'accesso, quale indirizzo interrogare e
    /// come leggere la risposta. Il formato è documentato in docs/PROVIDERS.md.
    /// </summary>
    internal sealed class ServiceDefinition
    {
        public string Id, Name, Description, Monogram, Origin;
        public uint ColorLight = 0xFF5B5FC7, ColorDark = 0xFFA9ACF5;
        public Dictionary<string, object> Raw;

        public Dictionary<string, object> Auth { get { return Json.Obj(Raw, "auth"); } }
        public string AuthType { get { return Json.Str(Auth, "type") ?? "none"; } }
        /// <summary>Il servizio usa una chiave API che l'utente incolla nelle impostazioni.</summary>
        public bool UsesKey { get { return AuthType == "key"; } }
        public string KeyPage { get { return Json.Str(Auth, "page"); } }

        public static ServiceDefinition FromJson(string text, string origin)
        {
            var d = Json.Parse(text);
            string id = Json.Str(d, "id");
            if (d == null || string.IsNullOrEmpty(id) || !Regex.IsMatch(id, "^[a-z0-9][a-z0-9-]{1,31}$")) return null;
            var def = new ServiceDefinition
            {
                Id = id, Raw = d, Origin = origin,
                Name = Json.Str(d, "name") ?? id,
                Description = Json.Str(d, "description"),
            };
            def.Monogram = Json.Str(d, "monogram") ?? def.Name.Substring(0, 1).ToUpperInvariant();
            var color = Json.Obj(d, "color");
            uint c;
            if (ParseColor(Json.Str(color, "light"), out c)) def.ColorLight = c;
            if (ParseColor(Json.Str(color, "dark"), out c)) def.ColorDark = c;
            // traduzioni dei testi della definizione: { "it": { "Credits": "Crediti" } }
            var i18n = Json.Obj(d, "i18n");
            if (i18n != null)
                foreach (var lang in i18n)
                {
                    var t = lang.Value as Dictionary<string, object>;
                    if (t != null) foreach (var kv in t) if (kv.Value is string) L.Register(lang.Key, kv.Key, (string)kv.Value);
                }
            return def;
        }

        private static bool ParseColor(string s, out uint c)
        {
            c = 0;
            if (s == null || !Regex.IsMatch(s, "^#[0-9a-fA-F]{6}$")) return false;
            c = 0xFF000000 | uint.Parse(s.Substring(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            return true;
        }

        /// <summary>Trovato sul PC: un file o una cartella dell'app, un programma nel PATH, una variabile d'ambiente.</summary>
        private bool? detected;

        public bool Detected()
        {
            if (detected == null) detected = DetectNow();
            // una chiave in un programma del PC (OpenCode, Pi…) si cerca ogni volta: può comparire dopo, anche in WSL
            return detected.Value || KeyFinder.Find(this) != null;
        }

        private bool DetectNow()
        {
            var det = Json.Obj(Raw, "detect");
            if (det == null) return false;
            if (Strings(det, "paths").Any(p => { string f = Expand(p); return File.Exists(f) || Directory.Exists(f); })) return true;
            if (Strings(det, "env").Any(v => !string.IsNullOrEmpty(Env(v)))) return true;
            if (Strings(det, "commands").Any(c => OnPath(c) != null)) return true;
            return false;
        }

        /// <summary>Logo dell'app installata: un'immagine o l'icona di un eseguibile; null = monogramma.</summary>
        public string LogoFile()
        {
            var logo = Json.Obj(Raw, "logo");
            foreach (var p in Strings(logo, "file").Concat(Strings(logo, "exe")))
            {
                string f = Expand(p);
                if (File.Exists(f)) return f;
            }
            return null;
        }

        /// <summary>
        /// Logo incluso con la definizione ("logo": { "image": "x.png", "imageDark": "x-dark.png" }), accanto al file:
        /// per quelle incluse una risorsa dell'eseguibile o la cartella providers, per quelle dell'utente la sua cartella.
        /// </summary>
        public Stream LogoImage(bool dark)
        {
            var logo = Json.Obj(Raw, "logo");
            string name = (dark ? Json.Str(logo, "imageDark") : null) ?? Json.Str(logo, "image");
            if (string.IsNullOrEmpty(name)) return null;
            name = Path.GetFileName(name);   // solo un file accanto alla definizione, mai un percorso altrove
            try
            {
                if (Origin == "built-in")
                {
                    var s = typeof(ServiceDefinition).Assembly.GetManifestResourceStream("providers." + name);
                    if (s != null) return s;
                    string f = Path.Combine(Program.Home, "providers", name);
                    return File.Exists(f) ? File.OpenRead(f) : null;
                }
                string own = Path.Combine(Path.GetDirectoryName(Origin), name);
                return File.Exists(own) ? File.OpenRead(own) : null;
            }
            catch (IOException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
            catch (ArgumentException) { return null; }
        }

        // ------------------------------------------------------------ utilità condivise
        public static IEnumerable<string> Strings(Dictionary<string, object> d, string key)
        {
            object v;
            if (d == null || !d.TryGetValue(key, out v) || v == null) return new string[0];
            if (v is string) return new[] { (string)v };
            var list = v as IEnumerable;
            return list == null ? new string[0] : list.OfType<string>().ToArray();
        }

        /// <summary>%VARIABILI% di Windows e ~ per la cartella dell'utente.</summary>
        public static string Expand(string p)
        {
            if (string.IsNullOrEmpty(p)) return p;
            if (p.StartsWith("~")) p = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + p.Substring(1);
            return Environment.ExpandEnvironmentVariables(p).Replace('/', '\\');
        }

        public static string Env(string name)
        {
            return Environment.GetEnvironmentVariable(name)
                   ?? Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.User);
        }

        public static string OnPath(string exe)
        {
            var path = (Environment.GetEnvironmentVariable("PATH") ?? "") + ";" + (Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User) ?? "");
            foreach (var dir in path.Split(';'))
            {
                if (dir.Trim().Length == 0) continue;
                try { string f = Path.Combine(Environment.ExpandEnvironmentVariables(dir.Trim()), exe); if (File.Exists(f)) return f; }
                catch (ArgumentException) { }
            }
            return null;
        }
    }

    /// <summary>Le definizioni disponibili: quelle incluse in AI Meter e quelle dell'utente in %APPDATA%\AIMeter\providers.</summary>
    internal static class Definitions
    {
        private static List<ServiceDefinition> all;
        /// <summary>Servizi gestiti dal codice: una definizione con lo stesso id viene ignorata.</summary>
        private static readonly string[] Reserved = { "claude", "openai", "copilot", "antigravity", "local" };

        public static string UserFolder { get { return Path.Combine(Settings.Folder, "providers"); } }

        public static IList<ServiceDefinition> All { get { if (all == null) all = Load(); return all; } }

        public static ServiceDefinition Get(string id) { return All.FirstOrDefault(d => d.Id == id); }

        private static List<ServiceDefinition> Load()
        {
            var byId = new Dictionary<string, ServiceDefinition>();
            Action<string, string> add = (text, origin) =>
            {
                ServiceDefinition d;
                try { d = ServiceDefinition.FromJson(text, origin); }
                catch (Exception) { d = null; }
                if (d != null && !Reserved.Contains(d.Id)) byId[d.Id] = d;   // l'ultima vince: quelle dell'utente sostituiscono le incluse
            };
            // incluse: risorse dell'eseguibile, oppure la cartella providers accanto al sorgente
            var asm = typeof(Definitions).Assembly;
            foreach (var n in asm.GetManifestResourceNames().Where(n => n.StartsWith("providers.") && n.EndsWith(".json")).OrderBy(n => n))
                using (var s = asm.GetManifestResourceStream(n))
                using (var r = new StreamReader(s, Encoding.UTF8)) add(r.ReadToEnd(), "built-in");
            foreach (var dir in new[] { Path.Combine(Program.Home, "providers"), UserFolder })
            {
                try
                {
                    if (!Directory.Exists(dir)) continue;
                    foreach (var f in Directory.GetFiles(dir, "*.json").OrderBy(f => f))
                        add(File.ReadAllText(f, Encoding.UTF8), dir == UserFolder ? f : "built-in");
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            return byId.Values.OrderBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        }
    }

    /// <summary>
    /// Percorsi nelle risposte JSON: "data.usage", "items[0].name", "limits[type=session].used", "$" per la radice.
    /// </summary>
    internal static class JPath
    {
        private static readonly Regex Token = new Regex(@"([^.\[\]]+)|\[([^\]]*)\]");

        public static object Get(object root, string path)
        {
            if (string.IsNullOrEmpty(path) || path == "$") return root;
            object cur = root;
            foreach (Match m in Token.Matches(path.StartsWith("$.") ? path.Substring(2) : path))
            {
                if (cur == null) return null;
                if (m.Groups[1].Success)
                {
                    var d = cur as Dictionary<string, object>;
                    object v;
                    cur = d != null && d.TryGetValue(m.Groups[1].Value, out v) ? v : null;
                    continue;
                }
                var list = cur as IList;
                if (list == null) return null;
                string sel = m.Groups[2].Value;
                int i;
                if (int.TryParse(sel, NumberStyles.Integer, CultureInfo.InvariantCulture, out i))
                {
                    if (i < 0) i += list.Count;
                    cur = i >= 0 && i < list.Count ? list[i] : null;
                }
                else cur = list.Cast<object>().FirstOrDefault(x => Matches(x, sel));
            }
            return cur;
        }

        /// <summary>Filtro "campo=valore" o "campo!=valore" su un elemento di un elenco.</summary>
        public static bool Matches(object item, string filter)
        {
            if (string.IsNullOrEmpty(filter)) return true;
            bool not = filter.Contains("!=");
            var parts = filter.Split(new[] { not ? "!=" : "=" }, 2, StringSplitOptions.None);
            if (parts.Length < 2) return Truthy(Get(item, filter));
            bool eq = string.Equals(Str(Get(item, parts[0].Trim())), parts[1].Trim(), StringComparison.OrdinalIgnoreCase);
            return not ? !eq : eq;
        }

        public static string Str(object v)
        {
            if (v == null) return null;
            if (v is bool) return (bool)v ? "true" : "false";
            return Convert.ToString(v, CultureInfo.InvariantCulture);
        }

        public static double? Num(object v)
        {
            if (v == null || v is bool) return null;
            if (v is string)
            {
                double d;
                return double.TryParse((string)v, NumberStyles.Float, CultureInfo.InvariantCulture, out d) ? d : (double?)null;
            }
            try { return Convert.ToDouble(v, CultureInfo.InvariantCulture); }
            catch (FormatException) { return null; }
            catch (InvalidCastException) { return null; }
        }

        public static bool Truthy(object v)
        {
            if (v == null) return false;
            if (v is bool) return (bool)v;
            if (v is string) return ((string)v).Length > 0 && (string)v != "false" && (string)v != "0";
            var n = Num(v);
            if (n != null) return n.Value != 0;
            var c = v as ICollection;
            return c == null || c.Count > 0;
        }

        /// <summary>Data da testo ISO, secondi o millisecondi Unix (riconosciuti dalla grandezza).</summary>
        public static DateTime? Date(object v)
        {
            var n = Num(v);
            if (n != null && !(v is string && ((string)v).Contains("-"))) return n.Value > 1e11 ? Json.FromUnixMs(n) : Json.FromUnix(n);
            return Json.FromIso(Str(v));
        }
    }

    /// <summary>Chiavi API incollate dall'utente, cifrate con DPAPI per l'utente corrente.</summary>
    internal static class ApiKeys
    {
        private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("AIMeter.ApiKey");
        private static string FilePath(string id) { return Path.Combine(Settings.Folder, "keys", id + ".bin"); }

        public static bool Has(string id) { return File.Exists(FilePath(id)); }

        public static string Get(string id)
        {
            try
            {
                if (!Has(id)) return null;
                return Encoding.UTF8.GetString(ProtectedData.Unprotect(File.ReadAllBytes(FilePath(id)), Entropy, DataProtectionScope.CurrentUser));
            }
            catch (CryptographicException) { return null; }
            catch (IOException) { return null; }
        }

        public static void Save(string id, string key)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath(id)));
            File.WriteAllBytes(FilePath(id), ProtectedData.Protect(Encoding.UTF8.GetBytes(key.Trim()), Entropy, DataProtectionScope.CurrentUser));
        }

        public static void Remove(string id) { try { File.Delete(FilePath(id)); } catch (IOException) { } }
    }

    /// <summary>Un servizio letto dalla sua definizione JSON.</summary>
    internal sealed class DefinedProvider : Provider
    {
        private readonly ServiceDefinition D;
        private Usage live;
        private string plan;
        private DateTime fetchedAt = DateTime.MinValue;
        private string problem;   // avviso da mostrare (accesso mancante o scaduto)

        public DefinedProvider(ServiceDefinition d) { D = d; }

        public override string Id { get { return D.Id; } }
        public override bool Enabled(Settings s) { return s.ServiceOn(D); }
        public override void InvalidateNetwork() { fetchedAt = DateTime.MinValue; }

        public override ProviderState Collect(Probe p)
        {
            var st = new ProviderState { Id = D.Id, Name = D.Name, CanSignIn = D.UsesKey };
            foreach (var proc in ProcessList())
                if (p.IsRunning(Json.Str(proc, "name") ?? ""))
                    st.Sessions.Add(new Session { Kind = "App", Title = Json.Str(proc, "title") ?? D.Name, Status = "app" });
            st.Running = st.Sessions.Count > 0;

            var req = Json.Obj(D.Raw, "request");
            double interval = Json.Num(req, "interval") ?? 5;
            if (p.SavingPower) interval *= 3;
            if (req != null && p.AllowNetwork && (DateTime.Now - fetchedAt).TotalMinutes >= interval)
            {
                fetchedAt = DateTime.Now;
                Fetch(req);
            }
            st.Plan = plan;
            if (live != null) st.Usage = Decorate((DateTime.Now - live.At).TotalMinutes > interval + 2 ? live.AsCache() : live, p);
            if (problem != null && live == null)
            {
                st.NeedsLogin = true;
                st.Notice = L.T(problem);
            }
            return st;
        }

        // ================================================================ spesa, budget, modelli usati
        /// <summary>Spesa letta dal servizio ("spend" nella definizione): totale, saldo, e se ci sono giorno/settimana/mese.</summary>
        private sealed class SpendIn { public double? Total, Balance, Day, Week, Month; public string Currency; }
        private SpendIn spend;

        private SpendIn ReadSpend(object doc)
        {
            var s = Json.Obj(D.Raw, "spend");
            if (s == null) return null;
            double scale = Json.Num(s, "scale") ?? 1;
            Func<string, double?> num = k => { var v = Number(doc, s, k); return v == null ? null : v * scale; };
            var r = new SpendIn { Total = num("total"), Balance = num("balance"), Day = num("day"), Week = num("week"), Month = num("month"), Currency = Currency(doc, s) ?? "USD" };
            return r.Total == null && r.Balance == null && r.Day == null ? null : r;
        }

        /// <summary>
        /// La lettura del servizio più quello che calcola AI Meter: spesa di oggi, della settimana e del mese (per la pillola
        /// e una riga nella scheda), il budget scelto nelle impostazioni come limite con barra, e i modelli usati oggi.
        /// </summary>
        private Usage Decorate(Usage u, Probe p)
        {
            var extra = new List<UsageWindow>();
            Func<string> note = null;
            var models = AgentCosts.For(p, ServiceDefinition.Strings(D.Auth, "ids").DefaultIfEmpty(D.Id));
            if (spend != null)
            {
                var money = new Money { Currency = spend.Currency };
                bool fromService = spend.Day != null || spend.Week != null || spend.Month != null;
                var now = DateTime.Now;
                Func<string, double?> spent = per => fromService
                    ? (per == "day" ? spend.Day : per == "week" ? spend.Week : spend.Month)
                    : SpendTracker.Since(D.Id, SpendTracker.Start(per, now, false));
                var values = new Dictionary<string, double?> { { "day", spent("day") }, { "week", spent("week") }, { "month", spent("month") } };
                var labels = new Dictionary<string, string> { { "day", "Spent today" }, { "week", "Spent this week" }, { "month", "Spent this month" } };
                foreach (var kv in values)
                    if (kv.Value.HasValue)
                        extra.Add(new UsageWindow { Label = labels[kv.Key], Short = "$", NoBar = true, Hidden = true, RightText = money.Format(kv.Value.Value) });
                if (values.Values.Any(v => v.HasValue))
                    note = () => L.F("Spent: today {0} · this week {1} · this month {2}",
                        values["day"].HasValue ? money.Format(values["day"].Value) : "–",
                        values["week"].HasValue ? money.Format(values["week"].Value) : "–",
                        values["month"].HasValue ? money.Format(values["month"].Value) : "–");

                double amount;
                string period;
                if (SpendTracker.Budget(Settings.Current, D.Id, out amount, out period))
                {
                    double used = spent(period) ?? 0;
                    extra.Add(new UsageWindow
                    {
                        Label = period == "day" ? "Daily budget" : period == "week" ? "Weekly budget" : "Monthly budget", Short = "$", Budget = true,
                        Used = Math.Min(100, used / amount * 100), Spent = new Money { Used = used, Limit = amount, Currency = spend.Currency },
                        StartedAt = SpendTracker.Start(period, now, fromService), ResetsAt = SpendTracker.End(period, now, fromService),
                    });
                }
            }
            var result = u.With(extra);
            result.SpendNote = note;
            result.TodayModels = models.Count > 0 ? models : null;
            return result;
        }

        private List<Dictionary<string, object>> ProcessList()
        {
            object v;
            var list = D.Raw.TryGetValue("processes", out v) ? v as IList : null;
            return list == null ? new List<Dictionary<string, object>>() : list.OfType<Dictionary<string, object>>().ToList();
        }

        // ================================================================ accesso
        /// <summary>Il segreto da usare nella richiesta ("" se non serve), null se manca o è scaduto.</summary>
        private string Token()
        {
            var a = D.Auth;
            string type = D.AuthType;
            string token = null;
            if (type == "none") return "";
            foreach (var v in ServiceDefinition.Strings(a, "env")) { token = ServiceDefinition.Env(v); if (!string.IsNullOrEmpty(token)) return token.Trim(); }
            if (type == "key")
            {
                // la chiave salvata in AI Meter, altrimenti quella di un programma del PC che usa lo stesso servizio
                token = ApiKeys.Get(D.Id);
                if (token != null) return token;
                var found = KeyFinder.Find(D);
                return found == null ? null : found.Key;
            }
            if (type == "env") return null;
            if (type == "file")
            {
                string f = ServiceDefinition.Expand(Json.Str(a, "path"));
                try { token = File.Exists(f) ? File.ReadAllText(f, Encoding.UTF8) : null; }
                catch (IOException) { token = null; }
            }
            else if (type == "sqlite")
                token = Sqlite.ReadValue(ServiceDefinition.Expand(Json.Str(a, "path")), Json.Str(a, "query"));
            if (string.IsNullOrEmpty(token)) return null;
            token = token.Trim();

            // valore JSON: il token è un campo, e la scadenza un altro
            string field = Json.Str(a, "field");
            if (field != null)
            {
                var doc = Json.ParseAny(token);
                var exp = JPath.Date(JPath.Get(doc, Json.Str(a, "expires")));
                if (exp != null && exp < DateTime.Now.AddMinutes(1)) return null;
                token = JPath.Str(JPath.Get(doc, field));
                if (string.IsNullOrEmpty(token)) return null;
            }
            token = token.Trim('"');
            if (Json.Bool(a, "jwt"))
            {
                var exp = JPath.Date(JPath.Get(JwtClaims(token), "exp"));
                if (exp != null && exp < DateTime.Now.AddMinutes(1)) return null;
            }
            return token;
        }

        private static Dictionary<string, object> JwtClaims(string jwt)
        {
            var parts = (jwt ?? "").Split('.');
            if (parts.Length < 2) return null;
            string b = parts[1].Replace('-', '+').Replace('_', '/');
            b = b.PadRight(b.Length + (4 - b.Length % 4) % 4, '=');
            try { return Json.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(b))); }
            catch (FormatException) { return null; }
        }

        /// <summary>{token}, {jwt.campo}, {env:NOME}, con filtri |urlencode e |afterLast:X (X = un carattere).</summary>
        private static string Fill(string template, string token)
        {
            if (template == null) return null;
            return Regex.Replace(template, @"\{([a-zA-Z][^{}]*)\}", m =>
            {
                string expr = m.Groups[1].Value;
                int bar = expr.IndexOf('|');
                string src = bar < 0 ? expr : expr.Substring(0, bar);
                string filters = bar < 0 ? "" : expr.Substring(bar + 1);
                string v;
                if (src == "token") v = token;
                else if (src.StartsWith("jwt.")) v = JPath.Str(JPath.Get(JwtClaims(token), src.Substring(4)));
                else if (src.StartsWith("env:")) v = ServiceDefinition.Env(src.Substring(4));
                else return m.Value;
                v = v ?? "";
                while (filters.Length > 0)
                {
                    if (filters.StartsWith("afterLast:") && filters.Length >= 11)
                    {
                        char c = filters[10];
                        int i = v.LastIndexOf(c);
                        if (i >= 0) v = v.Substring(i + 1);
                        filters = filters.Substring(11);
                    }
                    else if (filters.StartsWith("urlencode")) { v = Uri.EscapeDataString(v); filters = filters.Substring(9); }
                    else break;
                    if (filters.StartsWith("|")) filters = filters.Substring(1);
                }
                return v;
            });
        }

        // ================================================================ richiesta
        private void Fetch(Dictionary<string, object> req)
        {
            string token = Token();
            if (token == null)
            {
                problem = Json.Str(D.Auth, "notice") ?? (D.UsesKey ? "Add your API key in Settings to see your usage" : "Sign in to see your usage");
                live = null;
                return;
            }
            try
            {
                var r = (HttpWebRequest)WebRequest.Create(Fill(Json.Str(req, "url"), token));
                r.Method = Json.Str(req, "method") ?? "GET";
                r.Accept = "application/json";
                r.UserAgent = "ai-meter/" + Controller.Version;
                r.Timeout = r.ReadWriteTimeout = (int)((Json.Num(req, "timeout") ?? 15) * 1000);
                var headers = Json.Obj(req, "headers");
                if (headers != null)
                    foreach (var h in headers)
                    {
                        string value = Fill(Convert.ToString(h.Value, CultureInfo.InvariantCulture), token);
                        if (h.Key.Equals("Accept", StringComparison.OrdinalIgnoreCase)) r.Accept = value;
                        else if (h.Key.Equals("Content-Type", StringComparison.OrdinalIgnoreCase)) r.ContentType = value;
                        else if (h.Key.Equals("User-Agent", StringComparison.OrdinalIgnoreCase)) r.UserAgent = value;
                        else r.Headers[h.Key] = value;
                    }
                string body = Fill(Json.Str(req, "body"), token);
                if (body != null)
                {
                    if (r.ContentType == null) r.ContentType = "application/json";
                    var bytes = Encoding.UTF8.GetBytes(body);
                    using (var s = r.GetRequestStream()) s.Write(bytes, 0, bytes.Length);
                }
                using (var resp = (HttpWebResponse)r.GetResponse())
                using (var sr = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                {
                    string text = sr.ReadToEnd();
                    // risposta senza segreti, per diagnosi e per chi scrive una definizione
                    try { File.WriteAllText(Path.Combine(Settings.Folder, D.Id + "-usage-raw.json"), text, new UTF8Encoding(false)); }
                    catch (IOException) { }
                    var doc = Json.ParseAny(text);
                    plan = JPath.Str(JPath.Get(doc, Json.Str(D.Raw, "plan")));
                    if (Json.Str(D.Raw, "plan") == null) plan = null;
                    live = Map(doc);
                    problem = live == null ? "The service answered without usage data" : null;
                    spend = ReadSpend(doc);
                    if (spend != null) SpendTracker.Record(D.Id, spend.Total, spend.Balance);
                }
            }
            catch (WebException e)
            {
                var hr = e.Response as HttpWebResponse;
                if (hr != null && (hr.StatusCode == HttpStatusCode.Unauthorized || hr.StatusCode == HttpStatusCode.Forbidden))
                {
                    problem = Json.Str(D.Auth, "notice") ?? (D.UsesKey ? "The API key was not accepted: check it in Settings" : "Sign-in expired: sign in again");
                    live = null;
                }
            }
            catch (IOException) { }
        }

        // ================================================================ lettura della risposta
        private Usage Map(object doc)
        {
            var u = new Usage { Source = "live", At = DateTime.Now };
            foreach (var spec in Items(D.Raw, "limits"))
            {
                if (!Applies(doc, spec)) continue;
                var w = Limit(doc, spec);
                if (w != null) u.Windows.Add(w);
            }
            var notes = new List<Func<string>>();
            foreach (var spec in Items(D.Raw, "notes"))
            {
                if (!Applies(doc, spec)) continue;
                string text = Json.Str(spec, "text");
                var values = Items(spec, "values").Select(v => FormatValue(doc, v)).ToArray();
                if (text == null || values.Any(v => v == null)) continue;
                notes.Add(() => L.F(text, values.Cast<object>().ToArray()));
            }
            if (notes.Count > 0) u.CreditsNote = () => string.Join("\n", notes.Select(n => n()));
            return u.Windows.Count > 0 || notes.Count > 0 ? u : null;
        }

        private static IEnumerable<Dictionary<string, object>> Items(Dictionary<string, object> d, string key)
        {
            object v;
            var list = d != null && d.TryGetValue(key, out v) ? v as IList : null;
            return list == null ? new Dictionary<string, object>[0] : list.OfType<Dictionary<string, object>>().ToArray();
        }

        /// <summary>"when": tutti i percorsi veri; "unless": nessuno.</summary>
        private static bool Applies(object doc, Dictionary<string, object> spec)
        {
            if (ServiceDefinition.Strings(spec, "when").Any(p => !JPath.Truthy(JPath.Get(doc, p)))) return false;
            if (ServiceDefinition.Strings(spec, "unless").Any(p => JPath.Truthy(JPath.Get(doc, p)))) return false;
            return true;
        }

        private static double? Number(object doc, Dictionary<string, object> spec, string key)
        {
            string path = Json.Str(spec, key);
            return path == null ? null : JPath.Num(JPath.Get(doc, path));
        }

        private static string Currency(object doc, Dictionary<string, object> spec)
        {
            string path = Json.Str(spec, "currencyPath");
            return (path != null ? JPath.Str(JPath.Get(doc, path)) : null) ?? Json.Str(spec, "currency");
        }

        private static UsageWindow Limit(object doc, Dictionary<string, object> spec)
        {
            double scale = Json.Num(spec, "scale") ?? 1;
            var w = new UsageWindow
            {
                Label = Json.Str(spec, "label") ?? "Usage",
                Short = Json.Str(spec, "short") ?? "",
                ResetsAt = JPath.Date(JPath.Get(doc, Json.Str(spec, "resets"))),
                StartedAt = JPath.Date(JPath.Get(doc, Json.Str(spec, "starts"))),
            };
            if (Json.Str(spec, "resets") == null) w.ResetsAt = null;
            if (Json.Str(spec, "starts") == null) w.StartedAt = null;
            string currency = Currency(doc, spec);

            // saldo senza un limite: solo l'importo, senza barra
            double? balance = Number(doc, spec, "balance");
            if (balance != null)
            {
                var m = new Money { Currency = currency ?? "USD" };
                w.RightText = currency != null ? m.Format(balance.Value * scale) : balance.Value.ToString("0.##", CultureInfo.InvariantCulture);
                w.NoBar = true;
                return w;
            }
            if (JPath.Truthy(JPath.Get(doc, Json.Str(spec, "unlimited"))) && Json.Str(spec, "unlimited") != null)
            {
                w.RightText = "unlimited";
                return w;
            }

            double? used = Number(doc, spec, "used"), limit = Number(doc, spec, "limit"), remaining = Number(doc, spec, "remaining");
            if (used == null && remaining != null && limit != null) used = limit - remaining;
            double? pct = Number(doc, spec, "percent");
            if (pct != null) pct *= Json.Num(spec, "percentScale") ?? 1;
            else if (used != null && limit != null && limit > 0) pct = used.Value / limit.Value * 100;
            if (pct == null) return null;
            w.Used = Math.Max(0, Math.Min(100, pct.Value));
            if (currency != null && used != null && limit != null && limit > 0)
                w.Spent = new Money { Used = used.Value * scale, Limit = limit.Value * scale, Currency = currency };
            return w;
        }

        private static string FormatValue(object doc, Dictionary<string, object> v)
        {
            var n = JPath.Num(JPath.Get(doc, Json.Str(v, "path")));
            if (n == null) return null;
            double x = n.Value * (Json.Num(v, "scale") ?? 1);
            string currency = Currency(doc, v);
            if (currency != null) return new Money { Currency = currency }.Format(x);
            return x.ToString("N" + (int)(Json.Num(v, "decimals") ?? 0), L.Culture);
        }
    }

    /// <summary>Finestra per incollare la chiave API di un servizio: resta cifrata su questo PC.</summary>
    internal sealed class ApiKeyWindow : Window
    {
        public event Action Saved;

        public ApiKeyWindow(ServiceDefinition d)
        {
            Title = L.F("Connect {0}", d.Name);
            Width = 460;
            SizeToContent = SizeToContent.Height;
            ResizeMode = ResizeMode.NoResize;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            FontFamily = Theme.Font;
            UseLayoutRounding = true;
            Icon = Controller.AppIconImage();
            Program.Brand(this);
            WindowChrome.SetWindowChrome(this, new WindowChrome { CaptionHeight = 32, GlassFrameThickness = new Thickness(-1), UseAeroCaptionButtons = true, ResizeBorderThickness = new Thickness(0) });
            SourceInitialized += (o, e) => Ui.ApplyBackdrop(this, 2, false);

            var root = new StackPanel { Margin = new Thickness(24, 40, 24, 24) };
            var h = Ui.Text(Title, 20, "Text", FontWeights.SemiBold);
            h.FontFamily = Theme.DisplayFont;
            root.Children.Add(h);
            var info = Ui.Text(L.F("Paste an API key of your {0} account. AI Meter uses it only to read your usage; it is encrypted with Windows data protection and stays on this PC.", d.Name), 13, "Text2");
            info.TextWrapping = TextWrapping.Wrap;
            info.TextTrimming = TextTrimming.None;
            info.Margin = new Thickness(0, 8, 0, 16);
            root.Children.Add(info);

            var box = new PasswordBox { BorderThickness = new Thickness(0), Background = System.Windows.Media.Brushes.Transparent, FontSize = 14, Padding = new Thickness(0) };
            box.SetResourceReference(Control.ForegroundProperty, "Text");
            box.SetResourceReference(PasswordBox.CaretBrushProperty, "Text");
            var frame = new Border { CornerRadius = new CornerRadius(4), BorderThickness = new Thickness(1, 1, 1, 2), Padding = new Thickness(10, 6, 10, 6), Child = box };
            frame.SetResourceReference(Border.BackgroundProperty, "Control");
            frame.SetResourceReference(Border.BorderBrushProperty, "ControlStroke");
            box.GotKeyboardFocus += (o, e) => frame.SetResourceReference(Border.BorderBrushProperty, "Accent");
            box.LostKeyboardFocus += (o, e) => frame.SetResourceReference(Border.BorderBrushProperty, "ControlStroke");
            root.Children.Add(frame);

            var buttons = new DockPanel { Margin = new Thickness(0, 18, 0, 0), LastChildFill = false };
            if (d.KeyPage != null)
            {
                var link = new Button { Content = L.T("Create a key"), Padding = new Thickness(0, 0, 4, 1), FontSize = 13 };
                link.SetResourceReference(StyleProperty, "LinkButton");
                link.Click += (o, e) => { try { System.Diagnostics.Process.Start(d.KeyPage); } catch (Exception) { } };
                DockPanel.SetDock(link, Dock.Left);
                buttons.Children.Add(link);
            }
            var cancel = new Button { Content = L.T("Cancel") };
            cancel.SetResourceReference(StyleProperty, "StdButton");
            cancel.Click += (o, e) => Close();
            DockPanel.SetDock(cancel, Dock.Right);
            var save = new Button { Content = L.T("Save"), Margin = new Thickness(0, 0, 8, 0), IsEnabled = false, IsDefault = true };
            save.SetResourceReference(StyleProperty, "AccentButton");
            save.Click += (o, e) => { ApiKeys.Save(d.Id, box.Password); if (Saved != null) Saved(); Close(); };
            DockPanel.SetDock(save, Dock.Right);
            buttons.Children.Add(cancel);
            buttons.Children.Add(save);
            box.PasswordChanged += (o, e) => save.IsEnabled = box.Password.Trim().Length >= 8;
            root.Children.Add(buttons);
            Content = root;
            Loaded += (o, e) => box.Focus();
        }
    }

    /// <summary>Lettura di un valore da un database SQLite con la libreria inclusa in Windows (winsqlite3.dll), in sola lettura.</summary>
    internal static class Sqlite
    {
        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.StdCall)] private static extern int sqlite3_open_v2(byte[] file, out IntPtr db, int flags, IntPtr vfs);
        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.StdCall)] private static extern int sqlite3_prepare_v2(IntPtr db, byte[] sql, int n, out IntPtr stmt, IntPtr tail);
        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.StdCall)] private static extern int sqlite3_step(IntPtr stmt);
        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.StdCall)] private static extern IntPtr sqlite3_column_blob(IntPtr stmt, int col);
        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.StdCall)] private static extern int sqlite3_column_bytes(IntPtr stmt, int col);
        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.StdCall)] private static extern int sqlite3_finalize(IntPtr stmt);
        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.StdCall)] private static extern int sqlite3_close(IntPtr db);
        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.StdCall)] private static extern int sqlite3_busy_timeout(IntPtr db, int ms);

        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.StdCall)] private static extern int sqlite3_column_count(IntPtr stmt);
        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.StdCall)] private static extern int sqlite3_column_type(IntPtr stmt, int col);
        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.StdCall)] private static extern long sqlite3_column_int64(IntPtr stmt, int col);
        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.StdCall)] private static extern double sqlite3_column_double(IntPtr stmt, int col);

        private const int SQLITE_OPEN_READONLY = 1, SQLITE_ROW = 100;

        /// <summary>
        /// Righe di una query in sola lettura (al massimo maxRows): interi come long, decimali come double, testo come string,
        /// NULL come null. null se il database non si apre o la query fallisce (es. tabella assente in una versione vecchia).
        /// </summary>
        public static List<object[]> Rows(string path, string sql, int maxRows)
        {
            if (string.IsNullOrEmpty(sql) || !File.Exists(path)) return null;
            IntPtr db = IntPtr.Zero, stmt = IntPtr.Zero;
            try
            {
                if (sqlite3_open_v2(Z(path), out db, SQLITE_OPEN_READONLY, IntPtr.Zero) != 0) return null;
                sqlite3_busy_timeout(db, 1000);
                if (sqlite3_prepare_v2(db, Z(sql), -1, out stmt, IntPtr.Zero) != 0) return null;
                var rows = new List<object[]>();
                int cols = sqlite3_column_count(stmt);
                while (rows.Count < maxRows && sqlite3_step(stmt) == SQLITE_ROW)
                {
                    var row = new object[cols];
                    for (int c = 0; c < cols; c++)
                    {
                        switch (sqlite3_column_type(stmt, c))
                        {
                            case 1: row[c] = sqlite3_column_int64(stmt, c); break;
                            case 2: row[c] = sqlite3_column_double(stmt, c); break;
                            case 5: row[c] = null; break;
                            default:
                                int n = sqlite3_column_bytes(stmt, c);
                                IntPtr p = sqlite3_column_blob(stmt, c);
                                if (n <= 0 || p == IntPtr.Zero) { row[c] = ""; break; }
                                var bytes = new byte[n];
                                Marshal.Copy(p, bytes, 0, n);
                                row[c] = Encoding.UTF8.GetString(bytes);
                                break;
                        }
                    }
                    rows.Add(row);
                }
                return rows;
            }
            catch (DllNotFoundException) { return null; }
            catch (EntryPointNotFoundException) { return null; }
            finally
            {
                if (stmt != IntPtr.Zero) sqlite3_finalize(stmt);
                if (db != IntPtr.Zero) sqlite3_close(db);
            }
        }

        /// <summary>Testo SQL tra apici, per inserire un identificativo letto dal database stesso.</summary>
        public static string Quote(string s) { return "'" + (s ?? "").Replace("'", "''") + "'"; }

        public static string ReadValue(string path, string sql)
        {
            if (string.IsNullOrEmpty(sql) || !File.Exists(path)) return null;
            IntPtr db = IntPtr.Zero, stmt = IntPtr.Zero;
            try
            {
                if (sqlite3_open_v2(Z(path), out db, SQLITE_OPEN_READONLY, IntPtr.Zero) != 0) return null;
                sqlite3_busy_timeout(db, 2000);
                if (sqlite3_prepare_v2(db, Z(sql), -1, out stmt, IntPtr.Zero) != 0) return null;
                if (sqlite3_step(stmt) != SQLITE_ROW) return null;
                int n = sqlite3_column_bytes(stmt, 0);
                IntPtr p = sqlite3_column_blob(stmt, 0);
                if (n <= 0 || p == IntPtr.Zero) return null;
                var bytes = new byte[n];
                Marshal.Copy(p, bytes, 0, n);
                // VS Code e i suoi derivati salvano i valori come testo UTF-8, a volte come UTF-16
                bool utf16 = n >= 2 && bytes[1] == 0;
                return (utf16 ? Encoding.Unicode : Encoding.UTF8).GetString(bytes);
            }
            catch (DllNotFoundException) { return null; }
            catch (EntryPointNotFoundException) { return null; }
            finally
            {
                if (stmt != IntPtr.Zero) sqlite3_finalize(stmt);
                if (db != IntPtr.Zero) sqlite3_close(db);
            }
        }

        private static byte[] Z(string s) { return Encoding.UTF8.GetBytes(s + "\0"); }
    }
}
