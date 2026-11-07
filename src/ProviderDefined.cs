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
    /// <summary>Collect usage according to a JSON service definition.</summary>
    internal sealed class DefinedProvider : Provider
    {
        private readonly ServiceDefinition D;
        private Usage live;
        private object usageDoc;   // The last usage answer, for free-model limits the service reports in it.
        private string plan;
        private DateTime fetchedAt = DateTime.MinValue;
        private string problem;   // Authentication notice shown when access is missing or expired.
        private string problemFix;   // What the notice's link opens: see ProviderState.Fix.
        private string credentialScope, spendScope;
        private Catalogue catalogue;
        private DateTime freeNextFetch = DateTime.MinValue;
        private const double FreeModelsRetryMinutes = 15;

        public DefinedProvider(ServiceDefinition d) { D = d; }

        public override string Id { get { return D.Id; } }
        public override bool Enabled(Settings s) { return s.ServiceOn(D); }
        public override void InvalidateNetwork() { fetchedAt = DateTime.MinValue; }
        public override void InvalidateCatalogue() { freeNextFetch = DateTime.MinValue; }

        public override ProviderState Collect(Probe p)
        {
            var st = new ProviderState { Id = D.Id, Name = D.Name, CanSignIn = D.UsesKey, CatalogueOnly = D.CatalogueOnly };
            foreach (var proc in ProcessList())
                if (p.IsRunning(Json.Str(proc, "name") ?? ""))
                    st.Sessions.Add(new Session { Kind = "App", Title = Json.Str(proc, "title") ?? D.Name, Status = "app" });
            st.Running = st.Sessions.Count > 0;

            var req = Json.Obj(D.Raw, "request");
            double interval = Json.Num(req, "interval") ?? 5;
            if (p.SavingPower) interval *= 3;
            if (req != null && p.AllowNetwork && (DateTime.Now - fetchedAt).TotalMinutes >= interval && NetworkBackoff.Ready(D.Id))
            {
                fetchedAt = DateTime.Now;
                Fetch(req);
            }
            FetchFreeModels(p, req != null);
            st.Catalogue = WithRates(catalogue, DateTime.Now);
            st.Plan = plan;
            if (live != null) st.Usage = Decorate((DateTime.Now - live.At).TotalMinutes > interval + 2 ? live.AsCache() : live, p, DateTime.Now);
            if (problem != null && live == null)
            {
                st.NeedsLogin = true;
                st.Notice = L.T(problem);
                st.Fix = problemFix;
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
                if (ExpiresSoon(JPath.Get(doc, Json.Str(a, "expires")))) return null;
                token = JPath.Str(JPath.Get(doc, field));
                if (string.IsNullOrEmpty(token)) return null;
            }
            token = token.Trim('"');
            if (Json.Bool(a, "jwt") && ExpiresSoon(JPath.Get(JwtClaims(token), "exp"))) return null;
            return token;
        }

        /// <summary>
        /// A value a request names as {key:slot}, such as an account id, besides the main secret: the one saved for the
        /// slot in Settings, then the first of the slot's "env" variables that is set, then the slot's "beside" field
        /// next to the key a coding agent keeps (or one of those variables exported in the same home). Each is reduced to
        /// the slot's "pattern", so a value saved with the rest of the address it was copied from still works. Null when absent.
        /// </summary>
        private string SlotValue(string slot)
        {
            var spec = Json.Obj(Json.Obj(D.Raw, "keys"), slot);
            string saved = ServiceDefinition.SlotText(spec, ApiKeys.Get(D.Id + "." + slot));
            if (saved != null || spec == null) return saved;
            var env = ServiceDefinition.Strings(spec, "env");
            foreach (var name in env)
            {
                string v = ServiceDefinition.SlotText(spec, ServiceDefinition.Env(name));
                if (v != null) return v;
            }
            return D.UsesKey ? ServiceDefinition.SlotText(spec, KeyFinder.Beside(KeyFinder.Find(D), Json.Str(spec, "beside"), env)) : null;
        }

        /// <summary>The {key:slot} names in a request's URL, headers and body, in order and without repeats.</summary>
        private static List<string> RequestSlots(Dictionary<string, object> req)
        {
            var parts = new List<string> { Json.Str(req, "url"), Json.Str(req, "body") };
            var headers = Json.Obj(req, "headers");
            if (headers != null) parts.AddRange(headers.Values.Select(h => Convert.ToString(h, CultureInfo.InvariantCulture)));
            return Regex.Matches(string.Join("\n", parts.Where(p => p != null)), @"\{key:([a-zA-Z0-9_-]+)")
                .Cast<Match>().Select(m => m.Groups[1].Value).Distinct().ToList();
        }

        /// <summary>Whether a credential expiry value lies within the next minute; unknown expiries are not treated as expired.</summary>
        private static bool ExpiresSoon(object expiry)
        {
            var at = JPath.Date(expiry);
            return at != null && at < DateTime.Now.AddMinutes(1);
        }

        /// <summary>Keep a raw response beside the settings for diagnostics and definition authors.</summary>
        private void SaveRaw(string suffix, string text)
        {
            try { File.WriteAllText(Path.Combine(Settings.Folder, D.Id + suffix), text, new UTF8Encoding(false)); }
            catch (IOException) { }
        }

        /// <summary>Whether the service refused the credential (401 or 403).</summary>
        private static bool Refused(WebException e)
        {
            var response = e.Response as HttpWebResponse;
            return response != null && (response.StatusCode == HttpStatusCode.Unauthorized || response.StatusCode == HttpStatusCode.Forbidden);
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

        /// <summary>
        /// Expand token, JWT, environment, extra key ({key:slot}) and date placeholders with optional URL and suffix
        /// filters. Dates are UTC, as yyyy-MM-dd: {date:utc} is today, {date:utc-week} this week's Monday and
        /// {date:utc-month} the first of this month, the periods SpendTracker uses for service-reported spending.
        /// </summary>
        private string Fill(string template, string token)
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
                else if (src.StartsWith("key:")) v = SlotValue(src.Substring(4));
                else if (src == "date:utc") v = DateTime.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                else if (src == "date:utc-week" || src == "date:utc-month")
                    v = SpendTracker.Start(src == "date:utc-week" ? "week" : "month", DateTime.Now, true).ToUniversalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
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
                problemFix = null;
                live = null;
                spend = null;
                return;
            }
            // A request that names an extra value it cannot fill is not sent: Settings then offers that slot.
            string missing = null;
            foreach (var slot in RequestSlots(req))
            {
                bool absent = SlotValue(slot) == null;
                lock (NeedsExtraKey)
                {
                    if (absent) NeedsExtraKey.Add(D.Id + "." + slot);
                    else NeedsExtraKey.Remove(D.Id + "." + slot);
                }
                if (absent && missing == null) missing = slot;
            }
            if (missing != null)
            {
                problem = Json.Str(Json.Obj(Json.Obj(D.Raw, "keys"), missing), "notice") ?? "Add your API key in Settings to see your usage";
                problemFix = "slot:" + missing;
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
                SaveRaw("-usage-raw.json", text);
                var doc = Json.ParseAny(text);
                // GraphQL APIs report failures, such as a missing permission, in a 200 answer: "error" is their path.
                if (Json.Str(req, "error") != null && JPath.Get(doc, Json.Str(req, "error")) != null)
                {
                    problem = Json.Str(req, "errorNotice") ?? "The service answered without usage data";
                    problemFix = "settings";
                    live = null;
                    usageDoc = null;
                    spend = null;
                    return;
                }
                plan = JPath.Str(JPath.Get(doc, Json.Str(D.Raw, "plan")));
                if (Json.Str(D.Raw, "plan") == null) plan = null;
                live = Map(doc, D.Raw);
                usageDoc = live != null ? doc : null;
                if (live != null) live.Identity = credentialScope;
                problem = live == null ? "The service answered without usage data" : null;
                problemFix = "settings";
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
                NetworkBackoff.Note(D.Id, e);
                if (Refused(e))
                {
                    problem = Json.Str(D.Auth, "notice") ?? (D.UsesKey ? "The API key was not accepted: check it in Settings" : "Sign-in expired: sign in again");
                    problemFix = null;
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
            var list = Json.Items(D.Raw, "more").ToList();
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
                    SaveRaw("-more" + i + "-raw.json", text);
                    var extra = Map(Json.ParseAny(text), spec);
                    if (extra != null) { moreCache[i] = extra; fresh = true; }
                    moreBlocked.Remove(i);
                    if (slot != null && own == null) lock (NeedsExtraKey) NeedsExtraKey.Remove(D.Id + "." + slot);
                }
                catch (WebException e)
                {
                    NetworkBackoff.Note(D.Id, e);
                    if (Refused(e))
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

        /// <summary>
        /// Read the service's model list ("freeModels": a request, default every 6 hours, and its "format"). A service
        /// with a usage request reads the list only while that request succeeds, so a rejected key reads nothing more.
        /// A service without one, whose limits are not readable, reads the list on its own whenever the key it names is
        /// available; that list is then what tells the user the key works. The request may use {token}, as lists such
        /// as Experiential Labs' need the key. A failed request keeps the previous list and is retried after
        /// FreeModelsRetryMinutes instead of the full interval.
        /// </summary>
        private void FetchFreeModels(Probe p, bool hasUsage)
        {
            var req = Json.Obj(D.Raw, "freeModels");
            if (req == null) return;
            if (hasUsage && live == null) { catalogue = null; return; }
            string token = Json.Serialize(req).Contains("{token}") ? Token() : "";
            if (token == null)
            {
                catalogue = null;
                if (!hasUsage) { problem = Json.Str(D.Auth, "notice") ?? (D.UsesKey ? "Add your API key in Settings to see your usage" : "Sign in to see your usage"); problemFix = null; }
                return;
            }
            double interval = Json.Num(req, "interval") ?? 360;
            if (!p.AllowNetwork || DateTime.Now < freeNextFetch || !NetworkBackoff.Ready(D.Id)) return;
            List<FreeModel> list = null;
            try { list = FreeModels.Parse(Json.ParseAny(Send(req, token)), Json.Str(req, "format"), DefinedFreeStatus(req), Json.Str(req, "provider")); }
            catch (WebException e)
            {
                NetworkBackoff.Note(D.Id, e);
                if (!hasUsage && Refused(e))
                {
                    problem = Json.Str(D.Auth, "notice") ?? "The API key was not accepted: check it in Settings";
                    problemFix = null;
                    catalogue = null;
                }
            }
            catch (IOException) { }
            if (list != null)
            {
                catalogue = new Catalogue { Models = list, ReadAt = DateTime.Now };
                if (!hasUsage) problem = null;
            }
            freeNextFetch = DateTime.Now.AddMinutes(list != null ? interval : Math.Min(interval, FreeModelsRetryMinutes));
        }

        /// <summary>
        /// What the definition's "free" block says about lists without prices: the status, with its kind, source,
        /// reference, check date and note. Null without a block, which leaves such models unknown.
        /// </summary>
        private static FreeStatus DefinedFreeStatus(Dictionary<string, object> req)
        {
            var f = Json.Obj(req, "free");
            if (f == null) return null;
            return new FreeStatus
            {
                Status = Json.Str(f, "status") ?? "unknown", Kind = Json.Str(f, "kind"), Source = Json.Str(f, "source") ?? "documented",
                Reference = Json.Str(f, "reference"), CheckedAt = Json.Day(Json.Str(f, "checked")), Note = Json.Str(f, "note"),
            };
        }

        /// <summary>
        /// The catalogue with its pool, the free models' rate limits and a free tier's allocation ("freeModels.pool",
        /// "freeModels.limits" and "freeModels.allowance"). A limit is a number stated in the definition (documented) or
        /// a path into the last usage answer (provider_reported); a reported limit without a current answer has unknown
        /// values, and "emptyIsZero" names a list whose presence, empty, means nothing used, as in usage limits.
        /// "period": "day" gives the next midnight in "resetZone" (default UTC), whose daylight-saving rules apply.
        /// </summary>
        private Catalogue WithRates(Catalogue read, DateTime now)
        {
            if (read == null) return null;
            var spec = Json.Obj(D.Raw, "freeModels");
            var result = new Catalogue { Models = read.Models, ReadAt = read.ReadAt, Pool = Json.Str(spec, "pool"), Allowance = Allowance.FromJson(Json.Obj(spec, "allowance")) };
            object doc = live != null ? usageDoc : null;
            foreach (var l in Json.Items(spec, "limits"))
            {
                var r = new RateLimit
                {
                    Metric = Json.Str(l, "metric"), Scope = Json.Str(l, "scope") ?? "account", PerModel = Json.Bool(l, "perModel"),
                    Source = Json.Str(l, "source") ?? "documented", Reference = Json.Str(l, "reference"), Note = Json.Str(l, "note"),
                    CheckedAt = Json.Day(Json.Str(l, "checked")), ResetZone = Json.Str(l, "resetZone"),
                };
                r.Limit = LimitValue(l, "limit", doc);
                r.Used = LimitValue(l, "used", doc);
                string emptyPath = Json.Str(l, "emptyIsZero");
                var rows = doc == null || emptyPath == null ? null : JPath.Get(doc, emptyPath) as IList;
                if (r.Used == null && rows != null && rows.Count == 0) r.Used = 0;
                r.Remaining = LimitValue(l, "remaining", doc);
                if (r.Remaining == null && r.Limit.HasValue && r.Used.HasValue) r.Remaining = Math.Max(0, r.Limit.Value - r.Used.Value);
                if (Json.Str(l, "period") == "day")
                {
                    if (r.ResetZone == null) r.ResetZone = "UTC";
                    r.ResetsAt = Zones.NextMidnight(now, r.ResetZone);
                }
                result.Rates.Add(r);
            }
            return result;
        }

        /// <summary>A number stated in the definition, or the number at a path of the usage answer; null when unknown.</summary>
        private static double? LimitValue(Dictionary<string, object> l, string key, object doc)
        {
            object v;
            if (!l.TryGetValue(key, out v) || v == null) return null;
            var path = v as string;
            if (path == null) return JPath.Num(v);
            return doc == null ? null : JPath.Num(JPath.Get(doc, path));
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
            foreach (var spec in Json.Items(block, "limits"))
            {
                if (!Applies(doc, spec)) continue;
                var w = Limit(doc, spec);
                if (w != null) u.Windows.Add(w);
            }
            var notes = new List<Func<string>>();
            foreach (var spec in Json.Items(block, "notes"))
            {
                if (!Applies(doc, spec)) continue;
                string text = Json.Str(spec, "text");
                var values = Json.Items(spec, "values").Select(v => FormatValue(doc, v)).ToArray();
                if (text == null || values.Any(v => v == null)) continue;
                notes.Add(() => L.F(text, values.Cast<object>().ToArray()));
            }
            if (notes.Count > 0) u.CreditsNote = () => string.Join("\n", notes.Select(n => n()));
            return u.Windows.Count > 0 || notes.Count > 0 ? u : null;
        }

        /// <summary>Require all "when" paths and reject any truthy "unless" path.</summary>
        private static bool Applies(object doc, Dictionary<string, object> spec)
        {
            if (ServiceDefinition.Strings(spec, "when").Any(p => !JPath.Truthy(JPath.Get(doc, p)))) return false;
            if (ServiceDefinition.Strings(spec, "unless").Any(p => JPath.Truthy(JPath.Get(doc, p)))) return false;
            return true;
        }

        /// <summary>The number at a path of the answer, or a number written in the definition, such as a documented limit.</summary>
        /// <summary>
        /// A number a definition names by path or as a literal. An object { "items", "value", "above" } adds up, over the
        /// array at "items", what each item's "value" exceeds "above" by (0 when not given): Cloudflare's paid Neurons
        /// are those beyond the free 10,000 of each day. An empty array is 0; a missing one is null.
        /// </summary>
        private static double? Number(object doc, Dictionary<string, object> spec, string key)
        {
            object v;
            if (spec == null || !spec.TryGetValue(key, out v) || v == null) return null;
            var over = v as Dictionary<string, object>;
            if (over != null)
            {
                var items = JPath.Get(doc, Json.Str(over, "items")) as IList;
                if (items == null) return null;
                double above = Json.Num(over, "above") ?? 0, sum = 0;
                foreach (var item in items) sum += Math.Max(0, (JPath.Num(JPath.Get(item, Json.Str(over, "value"))) ?? 0) - above);
                return sum;
            }
            var path = v as string;
            return path == null ? JPath.Num(v) : JPath.Num(JPath.Get(doc, path));
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
            // "period": the service's UTC day, week (from Monday) or month, for counters without reset fields.
            string period = Json.Str(spec, "period");
            if (period == "day" || period == "week" || period == "month")
            {
                w.StartedAt = SpendTracker.Start(period, DateTime.Now, true);
                w.ResetsAt = SpendTracker.End(period, DateTime.Now, true);
            }
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
            // A missing value is unknown, not zero, except where the service answers with no rows for no usage:
            // "emptyIsZero" is the path of that list, and only a list that is present and empty means zero.
            string emptyPath = Json.Str(spec, "emptyIsZero");
            var rows = emptyPath == null ? null : JPath.Get(doc, emptyPath) as IList;
            if (used == null && rows != null && rows.Count == 0) used = 0;
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
}
