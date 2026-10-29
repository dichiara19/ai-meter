using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace AIMeter
{
    /// <summary>
    /// Google Antigravity (Google AI subscriptions): quotas per model group, each with a 5-hour and a weekly window,
    /// from the internal API Antigravity uses (cloudcode-pa, retrieveUserQuotaSummary). The sign-in is that of
    /// Antigravity CLI on the PC (Windows, or WSL when already running); when it expires the CLI itself renews it ("agy models"), never AI Meter.
    /// The Gemini app does not expose its limits, so they are not shown here.
    /// </summary>
    internal sealed class AntigravityProvider : Provider
    {
        private static readonly string[] Hosts = { "https://daily-cloudcode-pa.googleapis.com", "https://cloudcode-pa.googleapis.com" };
        private const string TokenFile = @".gemini\antigravity-cli\antigravity-oauth-token";
        private readonly Settings S;
        private Usage live;
        private string plan, project;
        private DateTime fetchedAt = DateTime.MinValue, refreshedAt = DateTime.MinValue;
        private bool needsLogin;

        public AntigravityProvider(Settings s) { S = s; }

        public override string Id { get { return "antigravity"; } }

        public override bool Enabled(Settings s) { return s.AntigravityEnabled; }

        /// <summary>Antigravity CLI signed in on Windows (the WSL sign-in is visible only while WSL is running).</summary>
        public static bool Detected() { return File.Exists(Path.Combine(Probe.Home, TokenFile)); }

        public override void InvalidateNetwork() { fetchedAt = DateTime.MinValue; }

        public override ProviderState Collect(Probe p)
        {
            var st = new ProviderState { Id = "antigravity", Name = "Antigravity", CanSignIn = true, Plan = plan };
            double interval = p.SavingPower ? 15 : 5;
            if (p.AllowNetwork && (DateTime.Now - fetchedAt).TotalMinutes >= interval)
            {
                fetchedAt = DateTime.Now;
                string file;
                string token = Token(p, out file);
                if (token == null) needsLogin = file == null || !TryRefresh(p, file, out token);
                if (token != null)
                {
                    var fresh = Fetch(token);
                    if (fresh != null) { live = fresh; needsLogin = false; }
                }
            }
            st.Plan = plan;
            if (live != null) st.Usage = (DateTime.Now - live.At).TotalMinutes > interval + 2 ? live.AsCache() : live;
            if (needsLogin)
            {
                st.NeedsLogin = true;
                st.Notice = L.T("Sign in to Antigravity CLI to see your Google AI quotas");
            }
            return st;
        }

        /// <summary>Antigravity CLI access token, if still valid; file = where the sign-in is stored (even if expired).</summary>
        private static string Token(Probe p, out string file)
        {
            file = null;
            foreach (var home in Homes(p))
            {
                string f = Path.Combine(home, TokenFile);
                var d = Json.Obj(Json.ParseFile(f), "token");
                if (d == null) continue;
                file = f;
                string token = Json.Str(d, "access_token");
                DateTime? exp = Expiry(Json.Str(d, "expiry"));
                if (token != null && exp.HasValue && exp.Value > DateTime.Now.AddMinutes(2)) return token;
            }
            return null;
        }

        private static IEnumerable<string> Homes(Probe p)
        {
            yield return Probe.Home;
            if (p.WslUp) yield return p.WslHome;
        }

        /// <summary>
        /// Expired sign-in: Antigravity CLI renews it with a request that uses no quota (the model list), at most
        /// every 30 minutes. In WSL only if it is already running.
        /// </summary>
        private bool TryRefresh(Probe p, string file, out string token)
        {
            token = null;
            if ((DateTime.Now - refreshedAt).TotalMinutes < 30) return false;
            refreshedAt = DateTime.Now;
            if (file.StartsWith(@"\\wsl", StringComparison.OrdinalIgnoreCase))
            {
                if (!p.WslUp) return false;
                Probe.Run("wsl.exe", "-d " + S.WslDistro + " -- bash -lc \"timeout 60 ~/.local/bin/agy models >/dev/null 2>&1\"", 70000, null);
            }
            else
            {
                string exe = Path.Combine(Probe.Home, @".local\bin\agy.exe");
                if (!File.Exists(exe)) exe = Path.Combine(Probe.LocalAppData, @"Programs\Antigravity CLI\agy.exe");
                if (!File.Exists(exe)) return false;
                Probe.Run(exe, "models", 70000, null);
            }
            string again;
            token = Token(p, out again);
            return token != null;
        }

        /// <summary>"2026-10-27T19:10:44.851940758+01:00": up to 9 decimal places, while .NET reads at most 7.</summary>
        private static DateTime? Expiry(string s)
        {
            if (string.IsNullOrEmpty(s)) return null;
            s = Regex.Replace(s, @"(\.\d{7})\d+", "$1");
            DateTimeOffset t;
            if (DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out t)) return t.LocalDateTime;
            return null;
        }

        private Usage Fetch(string token)
        {
            try
            {
                if (project == null)
                {
                    var load = Post(Hosts[1], "loadCodeAssist", token, "{\"metadata\":{\"ideType\":\"ANTIGRAVITY\",\"platform\":\"PLATFORM_UNSPECIFIED\",\"pluginType\":\"GEMINI\"}}");
                    var tier = Json.Obj(load, "paidTier") ?? Json.Obj(load, "currentTier");
                    plan = PlanName(Json.Str(tier, "id"), Json.Str(tier, "name"));
                    project = Json.Str(load, "cloudaicompanionProject") ?? "";
                }
                string body = "{\"project\":" + Json.Serialize(project) + "}";
                foreach (var host in Hosts)
                {
                    var d = Post(host, "retrieveUserQuotaSummary", token, body);
                    var u = Parse(d);
                    if (u != null) return u;
                }
                return null;
            }
            catch (WebException e)
            {
                var hr = e.Response as HttpWebResponse;
                if (hr != null && hr.StatusCode == HttpStatusCode.Unauthorized) needsLogin = true;
                return null;
            }
            catch (IOException) { return null; }
        }

        /// <summary>One API call; null if the service rejects it (e.g. host not enabled for the account).</summary>
        private static Dictionary<string, object> Post(string host, string method, string token, string body)
        {
            var req = (HttpWebRequest)WebRequest.Create(host + "/v1internal:" + method);
            req.Method = "POST";
            req.ContentType = "application/json";
            req.Accept = "application/json";
            req.Headers["Authorization"] = "Bearer " + token;
            req.UserAgent = "antigravity/cli ai-meter/" + Controller.Version;
            req.Timeout = 15000;
            req.ReadWriteTimeout = 15000;
            var bytes = Encoding.UTF8.GetBytes(body);
            using (var s = req.GetRequestStream()) s.Write(bytes, 0, bytes.Length);
            try
            {
                using (var resp = (HttpWebResponse)req.GetResponse())
                using (var r = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                {
                    string text = r.ReadToEnd();
                    // Responses without tokens: for diagnosis and to recognise new fields.
                    try { File.WriteAllText(Path.Combine(Settings.Folder, "antigravity-" + method + "-raw.json"), text, new UTF8Encoding(false)); }
                    catch (IOException) { }
                    return Json.Parse(text);
                }
            }
            catch (WebException e)
            {
                var hr = e.Response as HttpWebResponse;
                if (hr == null || hr.StatusCode == HttpStatusCode.Unauthorized) throw;
                try
                {
                    using (var r = new StreamReader(hr.GetResponseStream(), Encoding.UTF8))
                        File.WriteAllText(Path.Combine(Settings.Folder, "antigravity-" + method + "-error.json"), (int)hr.StatusCode + " " + r.ReadToEnd(), new UTF8Encoding(false));
                }
                catch (IOException) { }
                return null;
            }
        }

        /// <summary>
        /// groups[].buckets[]: for each model group ("Gemini Models", "Claude and GPT models") a 5-hour and a weekly
        /// window. An exhausted quota has no remainingFraction (proto3 omits zero) but has resetTime: it counts as 0%.
        /// Disabled windows do not count.
        /// </summary>
        internal static Usage Parse(Dictionary<string, object> d)
        {
            var groups = d != null && d.ContainsKey("groups") ? d["groups"] as object[] : null;
            if (groups == null) return null;
            var u = new Usage { Source = "live", At = DateTime.Now };
            foreach (var g in groups.OfType<Dictionary<string, object>>())
            {
                string name = GroupName(Json.Str(g, "displayName"));
                var buckets = g.ContainsKey("buckets") ? g["buckets"] as object[] : null;
                if (buckets == null) continue;
                foreach (var b in buckets.OfType<Dictionary<string, object>>())
                {
                    if (Json.Bool(b, "disabled")) continue;
                    string window = (Json.Str(b, "window") ?? "").ToLowerInvariant();
                    string id = Json.Str(b, "bucketId") ?? "";
                    bool five = window == "5h" || id.EndsWith("-5h", StringComparison.Ordinal);
                    bool week = window == "weekly" || id.EndsWith("-weekly", StringComparison.Ordinal);
                    if (!five && !week) continue;
                    DateTime? reset = Json.FromIso(Json.Str(b, "resetTime"));
                    double? frac = Json.Num(b, "remainingFraction");
                    if (frac == null && reset == null) continue;
                    bool first = !u.Windows.Any(w => w.Short == (five ? "5h" : "7g"));
                    u.Windows.Add(new UsageWindow
                    {
                        Label = five ? "Session 5 h" : "Week", Suffix = name,
                        Short = first ? (five ? "5h" : "7g") : "m",
                        Used = Math.Max(0, Math.Min(100, (1 - (frac ?? 0)) * 100)), ResetsAt = reset,
                        StartedAt = reset.HasValue ? reset.Value - (five ? TimeSpan.FromHours(5) : TimeSpan.FromDays(7)) : (DateTime?)null,
                    });
                }
            }
            // 5-hour windows first, then weekly ones, as for the other services.
            u.Windows = u.Windows.OrderBy(w => w.Label == "Session 5 h" ? 0 : 1).ToList();
            return u.Windows.Count > 0 ? u : null;
        }

        /// <summary>Short group name for the limit row: "Claude and GPT models" → "Claude + GPT", "Gemini Models" → "Gemini".</summary>
        internal static string GroupName(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            name = Regex.Replace(name, @"\s+models?$", "", RegexOptions.IgnoreCase);
            return Regex.Replace(name, @"\s+and\s+", " + ", RegexOptions.IgnoreCase).Trim();
        }

        private static string PlanName(string id, string name)
        {
            if (string.IsNullOrEmpty(id)) return null;
            if (id.Contains("ultra")) return "ultra";
            if (id.Contains("pro")) return "pro";
            if (id.Contains("plus")) return "plus";
            if (id.Contains("free")) return "free";
            return name;
        }
    }
}
