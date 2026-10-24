using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;

namespace AIMeter
{
    /// <summary>
    /// Cursor: utilizzo incluso nel piano e spesa a consumo del ciclo di fatturazione, letti da cursor.com con
    /// l'accesso dell'app Cursor installata (il token che l'app tiene nel suo database locale e rinnova da sé).
    /// AI Meter non rinnova né salva il token. Nessun cookie del browser.
    /// </summary>
    internal sealed class CursorProvider : Provider
    {
        private readonly Settings S;
        private Usage live;
        private string plan;
        private DateTime fetchedAt = DateTime.MinValue;
        private bool needsLogin;

        public CursorProvider(Settings s) { S = s; }

        public override string Id { get { return "cursor"; } }
        public override bool Enabled(Settings s) { return s.CursorEnabled; }
        public override void InvalidateNetwork() { fetchedAt = DateTime.MinValue; }

        private static string StateDb { get { return Path.Combine(Probe.AppData, @"Cursor\User\globalStorage\state.vscdb"); } }

        public static string ExePath()
        {
            string f = Path.Combine(Probe.LocalAppData, @"Programs\cursor\Cursor.exe");
            return File.Exists(f) ? f : null;
        }

        public static bool Detected() { return File.Exists(StateDb) || ExePath() != null; }

        public override ProviderState Collect(Probe p)
        {
            var st = new ProviderState { Id = "cursor", Name = "Cursor" };
            if (p.IsRunning("cursor.exe")) st.Sessions.Add(new Session { Kind = "App", Title = "Cursor app open", Status = "app" });
            st.Running = st.Sessions.Count > 0;

            double interval = p.SavingPower ? 15 : 5;
            if (p.AllowNetwork && (DateTime.Now - fetchedAt).TotalMinutes >= interval)
            {
                fetchedAt = DateTime.Now;
                string token = ReadToken();
                if (token == null) needsLogin = true;
                else
                {
                    string pl;
                    var fresh = Fetch(token, out pl);
                    if (fresh != null) { live = fresh; plan = pl; needsLogin = false; }
                }
            }
            st.Plan = plan;
            if (live != null) st.Usage = (DateTime.Now - live.At).TotalMinutes > interval + 2 ? live.AsCache() : live;
            if (needsLogin && live == null)
            {
                st.NeedsLogin = true;
                st.Notice = L.T("Sign in to the Cursor app to see your usage");
            }
            return st;
        }

        // ================================================================ token dall'app Cursor
        /// <summary>Il token di accesso dell'app, se valido per almeno un altro minuto.</summary>
        private static string ReadToken()
        {
            string token = Sqlite.ReadValue(StateDb, "SELECT value FROM ItemTable WHERE key = 'cursorAuth/accessToken'");
            if (string.IsNullOrEmpty(token)) return null;
            token = token.Trim().Trim('"');
            var claims = JwtClaims(token);
            double? exp = Json.Num(claims, "exp");
            if (exp != null && Json.FromUnix(exp) < DateTime.Now.AddMinutes(1)) return null;
            return token;
        }

        private static Dictionary<string, object> JwtClaims(string jwt)
        {
            var parts = jwt.Split('.');
            if (parts.Length < 2) return null;
            string b = parts[1].Replace('-', '+').Replace('_', '/');
            b = b.PadRight(b.Length + (4 - b.Length % 4) % 4, '=');
            try { return Json.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(b))); }
            catch (FormatException) { return null; }
        }

        // ================================================================ utilizzo
        private Usage Fetch(string token, out string planName)
        {
            planName = null;
            string sub = Json.Str(JwtClaims(token), "sub") ?? "";
            string user = sub.Contains("|") ? sub.Substring(sub.LastIndexOf('|') + 1) : sub;
            try
            {
                var req = (HttpWebRequest)WebRequest.Create("https://cursor.com/api/usage-summary");
                req.Accept = "application/json";
                req.Headers["Cookie"] = "WorkosCursorSessionToken=" + user + "%3A%3A" + token;
                req.UserAgent = "ai-meter/" + Controller.Version;
                req.Timeout = 15000;
                req.ReadWriteTimeout = 15000;
                using (var resp = (HttpWebResponse)req.GetResponse())
                using (var r = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                {
                    string body = r.ReadToEnd();
                    // risposta senza token: per diagnosi e per riconoscere nuovi campi
                    try { File.WriteAllText(Path.Combine(Settings.Folder, "cursor-usage-raw.json"), body, new UTF8Encoding(false)); }
                    catch (IOException) { }
                    var d = Json.Parse(body);
                    planName = Json.Str(d, "membershipType");
                    return Parse(d);
                }
            }
            catch (WebException e)
            {
                var hr = e.Response as HttpWebResponse;
                if (hr != null && (hr.StatusCode == HttpStatusCode.Unauthorized || hr.StatusCode == HttpStatusCode.Forbidden)) needsLogin = true;
                return null;
            }
            catch (IOException) { return null; }
        }

        private static Usage Parse(Dictionary<string, object> d)
        {
            if (d == null) return null;
            var u = new Usage { Source = "live", At = DateTime.Now };
            DateTime? start = Json.FromIso(Json.Str(d, "billingCycleStart"));
            DateTime? end = Json.FromIso(Json.Str(d, "billingCycleEnd"));
            var individual = Json.Obj(d, "individualUsage");
            var planUse = Json.Obj(individual, "plan");

            if (Json.Bool(d, "isUnlimited"))
                u.Windows.Add(new UsageWindow { Label = "Included usage", Short = "mese", Used = 0, RightText = "unlimited", ResetsAt = end, StartedAt = start });
            else if (planUse != null)
            {
                double? used = Json.Num(planUse, "used"), limit = Json.Num(planUse, "limit");
                double? pct = Json.Num(planUse, "totalPercentUsed");
                if (pct == null && used != null && limit != null && limit > 0) pct = used.Value / limit.Value * 100;
                if (pct != null)
                    u.Windows.Add(new UsageWindow
                    {
                        Label = "Included usage", Short = "mese", Used = Math.Max(0, Math.Min(100, pct.Value)), ResetsAt = end, StartedAt = start,
                        Spent = used != null && limit != null && limit > 0 ? new Money { Used = used.Value / 100, Limit = limit.Value / 100, Currency = "USD" } : null,
                    });
            }

            // spesa a consumo oltre il piano (importi in centesimi)
            var od = Json.Obj(individual, "onDemand");
            if (od != null && Json.Bool(od, "enabled"))
            {
                var m = new Money { Used = (Json.Num(od, "used") ?? 0) / 100, Limit = (Json.Num(od, "limit") ?? 0) / 100, Currency = "USD" };
                u.CreditsNote = m.Limit > 0
                    ? (Func<string>)(() => L.F("On-demand usage: {0} of {1}", m.Format(m.Used), m.Format(m.Limit)))
                    : () => L.F("On-demand usage: {0}", m.Format(m.Used));
            }
            return u.Windows.Count > 0 ? u : null;
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

        private const int SQLITE_OPEN_READONLY = 1, SQLITE_ROW = 100;

        public static string ReadValue(string path, string sql)
        {
            if (!File.Exists(path)) return null;
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
                // VS Code / Cursor salvano i valori come testo UTF-8, a volte come UTF-16
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
