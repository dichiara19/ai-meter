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
    /// A service defined in providers\*.json: authentication, usage endpoint and response mapping.
    /// The definition format is documented in docs/PROVIDERS.md.
    /// </summary>
    internal sealed class ServiceDefinition
    {
        public string Id, Name, Description, Monogram, Origin;
        public uint ColorLight = 0xFF5B5FC7, ColorDark = 0xFFA9ACF5;
        public Dictionary<string, object> Raw;

        public Dictionary<string, object> Auth { get { return Json.Obj(Raw, "auth"); } }
        public string AuthType { get { return Json.Str(Auth, "type") ?? "none"; } }
        /// <summary>Whether the service authenticates with an API key.</summary>
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
            // Register translations supplied by the definition.
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

        private bool? detected;

        /// <summary>Detect installed files, commands on PATH, or environment variables.</summary>
        public bool Detected()
        {
            if (detected == null) detected = DetectNow();
            // Recheck discovered credentials because another app or WSL may become available later.
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

        /// <summary>Find an installed image or executable icon; null falls back to a monogram.</summary>
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
        /// Resolve the definition's logo image or dark-theme variant from its own directory.
        /// Built-in definitions also support embedded resources and the bundled providers directory.
        /// </summary>
        public Stream LogoImage(bool dark)
        {
            var logo = Json.Obj(Raw, "logo");
            string name = (dark ? Json.Str(logo, "imageDark") : null) ?? Json.Str(logo, "image");
            if (string.IsNullOrEmpty(name)) return null;
            name = Path.GetFileName(name);   // Only accept a filename beside the definition.
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

        // ------------------------------------------------------------ shared utilities
        public static IEnumerable<string> Strings(Dictionary<string, object> d, string key)
        {
            object v;
            if (d == null || !d.TryGetValue(key, out v) || v == null) return new string[0];
            if (v is string) return new[] { (string)v };
            var list = v as IEnumerable;
            return list == null ? new string[0] : list.OfType<string>().ToArray();
        }

        /// <summary>Expand Windows environment variables and the user's home directory.</summary>
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

    /// <summary>Load built-in definitions and user definitions from %APPDATA%\AIMeter\providers.</summary>
    internal static class Definitions
    {
        private static List<ServiceDefinition> all;
        /// <summary>Ignore definitions whose ids belong to providers implemented in code.</summary>
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
                if (d != null && !Reserved.Contains(d.Id)) byId[d.Id] = d;   // User definitions override built-in definitions with the same id.
            };
            // Load embedded definitions first, followed by bundled and user files.
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
    /// JSON paths support dotted fields, array indices, filters and "$" for the root.
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

        /// <summary>Match an array item using field=value or field!=value.</summary>
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

        /// <summary>Parse ISO dates or Unix timestamps, distinguishing seconds from milliseconds by magnitude.</summary>
        public static DateTime? Date(object v)
        {
            var n = Num(v);
            if (n != null && !(v is string && ((string)v).Contains("-"))) return n.Value > 1e11 ? Json.FromUnixMs(n) : Json.FromUnix(n);
            return Json.FromIso(Str(v));
        }
    }

    /// <summary>Store user-supplied API keys encrypted with the current user's DPAPI protection.</summary>
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

    /// <summary>Collect usage according to a JSON service definition.</summary>
    internal sealed class DefinedProvider : Provider
    {
        private readonly ServiceDefinition D;
        private Usage live;
        private string plan;
        private DateTime fetchedAt = DateTime.MinValue;
        private string problem;   // Authentication notice shown when access is missing or expired.
        private string credentialScope, spendScope;

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
            if (live != null) st.Usage = Decorate((DateTime.Now - live.At).TotalMinutes > interval + 2 ? live.AsCache() : live, p, DateTime.Now);
            if (problem != null && live == null)
            {
                st.NeedsLogin = true;
                st.Notice = L.T(problem);
            }
            return st;
        }

        // ================================================================ spending, budgets and models
        /// <summary>Spending fields reported by the service, timestamped for period validation.</summary>
        private sealed class SpendIn { public double? Total, Balance, Day, Week, Month; public string Currency; public DateTime At; }
        private SpendIn spend;

        private SpendIn ReadSpend(object doc)
        {
            var s = Json.Obj(D.Raw, "spend");
            if (s == null) return null;
            double scale = Json.Num(s, "scale") ?? 1;
            Func<string, double?> num = k => { var v = Number(doc, s, k); return v == null ? null : v * scale; };
            var r = new SpendIn { Total = num("total"), Balance = num("balance"), Day = num("day"), Week = num("week"), Month = num("month"), Currency = Currency(doc, s) ?? "USD", At = DateTime.Now };
            return r.Total == null && r.Balance == null && r.Day == null && r.Week == null && r.Month == null ? null : r;
        }

        private double? ServiceSpend(string period)
        {
            return period == "day" ? spend.Day : period == "week" ? spend.Week : spend.Month;
        }

        private PeriodSpend Spending(string period, DateTime now)
        {
            var value = ServiceSpend(period);
            if (!value.HasValue) return SpendTracker.Since(spendScope, SpendTracker.Start(period, now, false));
            var start = SpendTracker.Start(period, now, true);
            // A previous UTC period's response must never be relabelled as current spending.
            if (SpendTracker.Start(period, spend.At, true) != start) return null;
            return new PeriodSpend { Amount = value.Value, From = start };
        }

        private static string SpendText(PeriodSpend value, Money money)
        {
            if (value == null) return L.T("Spending unavailable");
            return value.Estimated ? L.F("{0} estimated since {1}", money.Format(value.Amount), value.From.ToString("g", L.Culture)) : money.Format(value.Amount);
        }

        /// <summary>
        /// Combine provider usage with period spending, user budgets and today's models.
        /// Only provider-reported period totals can establish remaining budget and threshold alerts.
        /// </summary>
        private Usage Decorate(Usage u, Probe p, DateTime now)
        {
            var extra = new List<UsageWindow>();
            Func<string> note = null;
            var models = AgentCosts.For(p, ServiceDefinition.Strings(D.Auth, "ids").DefaultIfEmpty(D.Id));
            if (spend != null)
            {
                var money = new Money { Currency = spend.Currency };
                var values = new Dictionary<string, PeriodSpend> { { "day", Spending("day", now) }, { "week", Spending("week", now) }, { "month", Spending("month", now) } };
                var labels = new Dictionary<string, string> { { "day", "Spent today" }, { "week", "Spent this week" }, { "month", "Spent this month" } };
                foreach (var kv in values)
                    extra.Add(new UsageWindow { Label = labels[kv.Key], Short = "$", NoBar = true, Hidden = true, RightText = SpendText(kv.Value, money),
                        Amount = kv.Value == null ? (double?)null : kv.Value.Amount, AmountUnit = spend.Currency,
                        IsEstimated = kv.Value != null && kv.Value.Estimated });
                note = () => L.F("Spent: today {0} · this week {1} · this month {2}",
                    SpendText(values["day"], money), SpendText(values["week"], money), SpendText(values["month"], money));

                double amount;
                string period;
                if (SpendTracker.Budget(Settings.Current, D.Id, out amount, out period))
                {
                    var value = values[period];
                    bool fromService = ServiceSpend(period).HasValue;
                    var budget = new UsageWindow
                    {
                        Label = period == "day" ? "Daily budget" : period == "week" ? "Weekly budget" : "Monthly budget", Short = "$", Budget = true,
                        StartedAt = SpendTracker.Start(period, now, fromService), ResetsAt = SpendTracker.End(period, now, fromService),
                        Amount = value == null ? (double?)null : value.Amount, AmountUnit = spend.Currency,
                        IsEstimated = value != null && value.Estimated,
                    };
                    if (value == null || value.Estimated)
                    {
                        budget.NoBar = true;
                        budget.RightText = value == null ? "Spending unavailable" : L.F("{0} estimated of {1}", money.Format(value.Amount), money.Format(amount));
                        budget.Detail = () => SpendText(value, money) + "\n" + L.T("Sampled spending can miss usage and top-ups. Remaining budget and threshold alerts require a provider-reported period total.");
                    }
                    else
                    {
                        budget.Used = Math.Max(0, Math.Min(100, value.Amount / amount * 100));
                        budget.Spent = new Money { Used = value.Amount, Limit = amount, Currency = spend.Currency };
                    }
                    extra.Add(budget);
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

        // ================================================================ authentication
        /// <summary>Return the request secret, an empty string if none is required, or null if unavailable.</summary>
        private string Token()
        {
            var a = D.Auth;
            string type = D.AuthType;
            string token = null;
            if (type == "none") return "";
            foreach (var v in ServiceDefinition.Strings(a, "env")) { token = ServiceDefinition.Env(v); if (!string.IsNullOrEmpty(token)) return token.Trim(); }
            if (type == "key")
            {
                // Prefer the saved key, then credentials discovered in another app for this service.
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

            // Extract the token and expiry fields from JSON credentials.
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

        /// <summary>Expand token, JWT and environment placeholders with optional URL and suffix filters.</summary>
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

        // ================================================================ requests
        private void Fetch(Dictionary<string, object> req)
        {
            string token = Token();
            if (token == null)
            {
                problem = Json.Str(D.Auth, "notice") ?? (D.UsesKey ? "Add your API key in Settings to see your usage" : "Sign in to see your usage");
                live = null;
                spend = null;
                return;
            }
            string scope = SpendTracker.Scope(D.Id, token, "");
            if (scope != credentialScope)
            {
                credentialScope = scope;
                live = null;
                spend = null;
                plan = null;
                moreCache.Clear();
                moreCredentials.Clear();
                moreBlocked.Clear();
            }
            try
            {
                string text = Send(req, token);
                // Keep the usage response for diagnostics and definition authors.
                try { File.WriteAllText(Path.Combine(Settings.Folder, D.Id + "-usage-raw.json"), text, new UTF8Encoding(false)); }
                catch (IOException) { }
                var doc = Json.ParseAny(text);
                plan = JPath.Str(JPath.Get(doc, Json.Str(D.Raw, "plan")));
                if (Json.Str(D.Raw, "plan") == null) plan = null;
                live = Map(doc, D.Raw);
                if (live != null) live.Identity = credentialScope;
                problem = live == null ? "The service answered without usage data" : null;
                spend = ReadSpend(doc);
                if (spend != null)
                {
                    spendScope = SpendTracker.Scope(D.Id, token, spend.Currency);
                    SpendTracker.Record(spendScope, spend.Total, spend.Balance);
                }
                if (live != null) FetchMore(token);
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

        private readonly Dictionary<int, DateTime> moreBlocked = new Dictionary<int, DateTime>();
        private readonly Dictionary<int, Usage> moreCache = new Dictionary<int, Usage>();
        private readonly Dictionary<int, string> moreCredentials = new Dictionary<int, string>();

        /// <summary>Extra credential slots to offer in Settings after authentication is rejected.</summary>
        public static readonly HashSet<string> NeedsExtraKey = new HashSet<string>();

        /// <summary>
        /// Fetch additional usage with an optional separate credential; cache each response independently.
        /// After authentication failures, retry the main credential after six hours or an extra key after one hour.
        /// Changing either credential invalidates its cached response and authentication backoff.
        /// </summary>
        private void FetchMore(string token)
        {
            var list = Items(D.Raw, "more").ToList();
            for (int i = 0; i < list.Count; i++)
            {
                var spec = list[i];
                var req = Json.Obj(spec, "request");
                if (req == null) continue;
                string slot = Json.Str(spec, "key");
                string own = slot != null ? ApiKeys.Get(D.Id + "." + slot) : null;
                string credential = SpendTracker.Scope(D.Id, own ?? token, "");
                string previous;
                if (!moreCredentials.TryGetValue(i, out previous) || previous != credential)
                {
                    moreCredentials[i] = credential;
                    moreCache.Remove(i);
                    moreBlocked.Remove(i);
                }
                DateTime until;
                bool fresh = false;
                if (moreBlocked.TryGetValue(i, out until) && DateTime.Now < until)
                {
                    AppendExtra(i, false);
                    continue;
                }
                try
                {
                    string text = Send(req, own ?? token);
                    try { File.WriteAllText(Path.Combine(Settings.Folder, D.Id + "-more" + i + "-raw.json"), text, new UTF8Encoding(false)); }
                    catch (IOException) { }
                    var extra = Map(Json.ParseAny(text), spec);
                    if (extra != null) { moreCache[i] = extra; fresh = true; }
                    moreBlocked.Remove(i);
                    if (slot != null && own == null) lock (NeedsExtraKey) NeedsExtraKey.Remove(D.Id + "." + slot);
                }
                catch (WebException e)
                {
                    var hr = e.Response as HttpWebResponse;
                    if (hr != null && (hr.StatusCode == HttpStatusCode.Unauthorized || hr.StatusCode == HttpStatusCode.Forbidden))
                    {
                        moreBlocked[i] = DateTime.Now.AddHours(own == null ? 6 : 1);
                        if (slot != null) lock (NeedsExtraKey) NeedsExtraKey.Add(D.Id + "." + slot);
                    }
                }
                catch (IOException) { }
                AppendExtra(i, fresh);
            }
        }

        private void AppendExtra(int index, bool fresh)
        {
            Usage cached;
            if (!moreCache.TryGetValue(index, out cached) || (DateTime.Now - cached.At).TotalDays > 10) return;
            foreach (var w in cached.Windows)
                live.Windows.Add(new UsageWindow
                {
                    Label = w.Label, Suffix = w.Suffix, Short = w.Short, Used = w.Used, Spent = w.Spent,
                    RightText = w.RightText, NoBar = w.NoBar, StartedAt = w.StartedAt, ResetsAt = w.ResetsAt,
                    CachedAt = fresh ? (DateTime?)null : cached.At,
                    MeasuredAt = cached.At, Amount = w.Amount, AmountUnit = w.AmountUnit, IsEstimated = w.IsEstimated,
                });
        }

        /// <summary>Send the configured request and return its response body.</summary>
        private string Send(Dictionary<string, object> req, string token)
        {
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
                    return sr.ReadToEnd();
            }
        }

        // ================================================================ response mapping
        /// <summary>Map limits and notes from a main or additional response block.</summary>
        private Usage Map(object doc, Dictionary<string, object> block)
        {
            var u = new Usage { Source = "live", At = DateTime.Now };
            foreach (var spec in Items(block, "limits"))
            {
                if (!Applies(doc, spec)) continue;
                var w = Limit(doc, spec);
                if (w != null) u.Windows.Add(w);
            }
            var notes = new List<Func<string>>();
            foreach (var spec in Items(block, "notes"))
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

        /// <summary>Require all "when" paths and reject any truthy "unless" path.</summary>
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
                Short = UsageWindow.NormaliseShort(Json.Str(spec, "short") ?? ""),
                ResetsAt = JPath.Date(JPath.Get(doc, Json.Str(spec, "resets"))),
                StartedAt = JPath.Date(JPath.Get(doc, Json.Str(spec, "starts"))),
            };
            if (Json.Str(spec, "resets") == null) w.ResetsAt = null;
            if (Json.Str(spec, "starts") == null) w.StartedAt = null;
            string currency = Currency(doc, spec);

            // A standalone balance has an amount but no percentage bar.
            double? balance = Number(doc, spec, "balance");
            if (balance != null)
            {
                var m = new Money { Currency = currency ?? "USD" };
                w.RightText = currency != null ? m.Format(balance.Value * scale) : balance.Value.ToString("0.##", CultureInfo.InvariantCulture);
                w.NoBar = true;
                w.Amount = balance.Value * scale;
                w.AmountUnit = currency ?? "credits";
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

    /// <summary>Prompt for a service API key and save it encrypted on this PC.</summary>
    internal sealed class ApiKeyWindow : Window
    {
        public event Action Saved;

        public ApiKeyWindow(ServiceDefinition d) : this(d, null) { }

        /// <summary>An optional slot saves an extra credential as &lt;id&gt;.&lt;slot&gt;; null selects the main key.</summary>
        public ApiKeyWindow(ServiceDefinition d, string slot)
        {
            var extra = slot == null ? null : Json.Obj(Json.Obj(d.Raw, "keys"), slot);
            string keyPage = extra != null ? Json.Str(extra, "page") : d.KeyPage;
            Title = extra != null ? L.T(Json.Str(extra, "label") ?? slot) + " · " + d.Name : L.F("Connect {0}", d.Name);
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
            var info = Ui.Text((extra != null && Json.Str(extra, "description") != null ? L.T(Json.Str(extra, "description")) + " " : "")
                + L.F("Paste an API key of your {0} account. AI Meter uses it only to read your usage; it is encrypted with Windows data protection and stays on this PC.", d.Name), 13, "Text2");
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
            if (keyPage != null)
            {
                var link = new Button { Content = L.T("Create a key"), Padding = new Thickness(0, 0, 4, 1), FontSize = 13 };
                link.SetResourceReference(StyleProperty, "LinkButton");
                link.Click += (o, e) => { try { System.Diagnostics.Process.Start(keyPage); } catch (Exception) { } };
                DockPanel.SetDock(link, Dock.Left);
                buttons.Children.Add(link);
            }
            var cancel = new Button { Content = L.T("Cancel") };
            cancel.SetResourceReference(StyleProperty, "StdButton");
            cancel.Click += (o, e) => Close();
            DockPanel.SetDock(cancel, Dock.Right);
            var save = new Button { Content = L.T("Save"), Margin = new Thickness(0, 0, 8, 0), IsEnabled = false, IsDefault = true };
            save.SetResourceReference(StyleProperty, "AccentButton");
            save.Click += (o, e) => { ApiKeys.Save(slot == null ? d.Id : d.Id + "." + slot, box.Password); if (Saved != null) Saved(); Close(); };
            DockPanel.SetDock(save, Dock.Right);
            buttons.Children.Add(cancel);
            buttons.Children.Add(save);
            box.PasswordChanged += (o, e) => save.IsEnabled = box.Password.Trim().Length >= 8;
            root.Children.Add(buttons);
            Content = root;
            Loaded += (o, e) => box.Focus();
        }
    }

    /// <summary>Read SQLite values using the Windows-provided winsqlite3.dll library in read-only mode.</summary>
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
        /// Return up to maxRows from a read-only query, preserving integer, float, text and null values.
        /// Return null if the database cannot be opened or the query fails, including unsupported schemas.
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

        /// <summary>Quote a value as a single-quoted SQL string literal, doubling embedded quotes.</summary>
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
                // VS Code and its derivatives store text as UTF-8 or, occasionally, UTF-16.
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
