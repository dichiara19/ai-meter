using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Shell;

namespace AIMeter
{
    /// <summary>
    /// GitHub Copilot: richieste premium e quote chat dall'API interna di Copilot.
    /// L'accesso a GitHub lo fa AI Meter con il flusso "codice dispositivo"; il token resta cifrato con DPAPI.
    /// </summary>
    internal sealed class CopilotProvider : Provider
    {
        private readonly Settings S;
        private Usage live;
        private string plan;
        private DateTime fetchedAt = DateTime.MinValue;
        private bool needsLogin;
        private static bool? vsCode;

        public CopilotProvider(Settings s) { S = s; }

        public override string Id { get { return "copilot"; } }

        public override bool Enabled(Settings s) { return s.CopilotEnabled; }

        /// <summary>Copilot è disponibile: account già collegato o VS Code installato (che lo include).</summary>
        public static bool Detected() { return GitHubAuth.HasToken || VsCodeInstalled(); }

        public override void InvalidateNetwork() { fetchedAt = DateTime.MinValue; }

        public static bool VsCodeInstalled()
        {
            if (vsCode == null)
                vsCode = Directory.Exists(Path.Combine(Probe.LocalAppData, @"Programs\Microsoft VS Code"))
                         || Directory.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Microsoft VS Code"));
            return vsCode.Value;
        }

        public override ProviderState Collect(Probe p)
        {
            var st = new ProviderState { Id = "copilot", Name = "Copilot" };
            if (p.IsRunning("code.exe")) st.Sessions.Add(new Session { Kind = "VS Code", Title = "VS Code aperto", Status = "app" });
            st.Running = st.Sessions.Count > 0 || p.IsRunning("copilot.exe");

            string token = GitHubAuth.Token();
            if (token == null)
            {
                st.NeedsLogin = true;
                st.Notice = "Collega GitHub per vedere richieste premium e quote";
                return st;
            }

            double interval = p.SavingPower ? 15 : 5;
            if (p.AllowNetwork && (DateTime.Now - fetchedAt).TotalMinutes >= interval)
            {
                fetchedAt = DateTime.Now;
                string pl;
                var fresh = Fetch(token, out pl);
                if (fresh != null) { live = fresh; plan = pl; needsLogin = false; }
            }
            st.Plan = plan;
            if (live != null) st.Usage = (DateTime.Now - live.At).TotalMinutes > interval + 2 ? live.AsCache() : live;
            if (needsLogin) { st.NeedsLogin = true; st.Notice = "Accesso a GitHub scaduto: ricollegalo"; }
            return st;
        }

        private Usage Fetch(string token, out string planName)
        {
            planName = null;
            try
            {
                var req = (HttpWebRequest)WebRequest.Create("https://api.github.com/copilot_internal/user");
                req.Headers["Authorization"] = "token " + token;
                req.Accept = "application/json";
                req.Headers["Editor-Version"] = "vscode/1.96.2";
                req.Headers["Editor-Plugin-Version"] = "copilot-chat/0.26.7";
                req.UserAgent = "GitHubCopilotChat/0.26.7";
                req.Headers["X-Github-Api-Version"] = "2025-04-01";
                req.Timeout = 15000;
                req.ReadWriteTimeout = 15000;
                using (var resp = (HttpWebResponse)req.GetResponse())
                using (var r = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                {
                    string body = r.ReadToEnd();
                    // risposta senza token: per diagnosi e per riconoscere nuovi campi
                    try { File.WriteAllText(Path.Combine(Settings.Folder, "copilot-usage-raw.json"), body, new UTF8Encoding(false)); }
                    catch (IOException) { }
                    var d = Json.Parse(body);
                    planName = PlanName(Json.Str(d, "copilot_plan"), Json.Str(d, "access_type_sku"));
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
            DateTime? reset = ParseDate(Json.Str(d, "quota_reset_date"));
            var snaps = Json.Obj(d, "quota_snapshots");
            if (snaps != null)
            {
                AddQuota(u, Json.Obj(snaps, "premium_interactions"), "Richieste premium", reset, true);
                AddQuota(u, Json.Obj(snaps, "chat"), "Chat", reset, false);
                AddQuota(u, Json.Obj(snaps, "completions"), "Completamenti", reset, false);
            }
            // piano gratuito: totali mensili e quanto ne resta
            var monthly = Json.Obj(d, "monthly_quotas");
            var left = Json.Obj(d, "limited_user_quotas");
            if (u.Windows.Count == 0 && monthly != null && left != null)
                foreach (var k in new[] { "chat", "completions" })
                {
                    double? total = Json.Num(monthly, k), rem = Json.Num(left, k);
                    if (total == null || rem == null || total <= 0) continue;
                    u.Windows.Add(new UsageWindow
                    {
                        Label = k == "chat" ? "Chat" : "Completamenti", Short = u.Windows.Count == 0 ? "mese" : "m",
                        Used = Math.Max(0, Math.Min(100, (total.Value - rem.Value) / total.Value * 100)), ResetsAt = reset,
                        Detail = string.Format(CultureInfo.InvariantCulture, "{0:0} di {1:0} rimasti", rem.Value, total.Value),
                    });
                }
            return u.Windows.Count > 0 ? u : null;
        }

        private static void AddQuota(Usage u, Dictionary<string, object> q, string label, DateTime? reset, bool primary)
        {
            if (q == null) return;
            bool unlimited = Json.Bool(q, "unlimited");
            double? pctLeft = Json.Num(q, "percent_remaining");
            double? ent = Json.Num(q, "entitlement"), rem = Json.Num(q, "remaining");
            if (!unlimited && pctLeft == null) return;
            if (unlimited && !primary) return;   // quote illimitate secondarie: niente da mostrare
            u.Windows.Add(new UsageWindow
            {
                Label = label, Short = primary && !u.Windows.Any(w => w.Short == "mese") ? "mese" : "m",
                Used = unlimited ? 0 : 100 - pctLeft.Value, ResetsAt = reset,
                RightText = unlimited ? "illimitate" : null,
                Detail = !unlimited && ent != null && rem != null ? string.Format(CultureInfo.InvariantCulture, "{0:0} di {1:0} rimaste", rem.Value, ent.Value) : null,
            });
        }

        /// <summary>Nome breve del piano per l'etichetta (es. licenza gratuita per studenti → EDU).</summary>
        private static string PlanName(string plan, string sku)
        {
            sku = (sku ?? "").ToLowerInvariant();
            if (sku.Contains("educational")) return "edu";
            if (sku.Contains("free_limited") || plan == "free") return "free";
            switch (plan)
            {
                case "individual": return sku.Contains("pro_plus") ? "pro+" : "pro";
                case "business": return "business";
                case "enterprise": return "enterprise";
                default: return plan;
            }
        }

        private static DateTime? ParseDate(string s)
        {
            if (string.IsNullOrEmpty(s)) return null;
            DateTime t;
            if (DateTime.TryParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out t)) return t;
            return Json.FromIso(s);
        }
    }

    /// <summary>Accesso a GitHub con il flusso "codice dispositivo"; token cifrato con DPAPI per l'utente corrente.</summary>
    internal static class GitHubAuth
    {
        // client OAuth di Copilot per VS Code: è quello che può leggere le quote, e GitHub lo mostra come "Visual Studio Code"
        private const string ClientId = "Iv1.b507a08c87ecfe98";
        private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("AIMeter.GitHub");
        private static string FilePath { get { return Path.Combine(Settings.Folder, "github-token.bin"); } }

        public static bool HasToken { get { return File.Exists(FilePath); } }

        public static string Token()
        {
            try
            {
                if (!File.Exists(FilePath)) return null;
                return Encoding.UTF8.GetString(ProtectedData.Unprotect(File.ReadAllBytes(FilePath), Entropy, DataProtectionScope.CurrentUser));
            }
            catch (CryptographicException) { return null; }
            catch (IOException) { return null; }
        }

        public static void Save(string token)
        {
            Directory.CreateDirectory(Settings.Folder);
            File.WriteAllBytes(FilePath, ProtectedData.Protect(Encoding.UTF8.GetBytes(token), Entropy, DataProtectionScope.CurrentUser));
        }

        public static void SignOut() { try { File.Delete(FilePath); } catch (IOException) { } }

        public sealed class DeviceCode { public string Device, User, Uri; public int Interval, ExpiresIn; }

        public static DeviceCode Start()
        {
            var d = Post("https://github.com/login/device/code", "client_id=" + ClientId + "&scope=read:user");
            if (d == null || Json.Str(d, "device_code") == null) return null;
            return new DeviceCode
            {
                Device = Json.Str(d, "device_code"), User = Json.Str(d, "user_code"),
                Uri = Json.Str(d, "verification_uri") ?? "https://github.com/login/device",
                Interval = (int)(Json.Num(d, "interval") ?? 5), ExpiresIn = (int)(Json.Num(d, "expires_in") ?? 900),
            };
        }

        /// <summary>Un tentativo: token se autorizzato, "pending"/"slow_down" se in attesa, altro testo se è fallito.</summary>
        public static string Poll(DeviceCode c, out string token)
        {
            token = null;
            var d = Post("https://github.com/login/oauth/access_token",
                "client_id=" + ClientId + "&device_code=" + Uri.EscapeDataString(c.Device) + "&grant_type=urn:ietf:params:oauth:grant-type:device_code");
            if (d == null) return "pending";
            token = Json.Str(d, "access_token");
            if (token != null) return "ok";
            string err = Json.Str(d, "error") ?? "pending";
            return err == "authorization_pending" ? "pending" : err;
        }

        private static Dictionary<string, object> Post(string url, string form)
        {
            try
            {
                var req = (HttpWebRequest)WebRequest.Create(url);
                req.Method = "POST";
                req.Accept = "application/json";
                req.ContentType = "application/x-www-form-urlencoded";
                req.UserAgent = "ai-meter/" + Controller.Version;
                req.Timeout = 15000;
                var bytes = Encoding.ASCII.GetBytes(form);
                using (var s = req.GetRequestStream()) s.Write(bytes, 0, bytes.Length);
                using (var resp = (HttpWebResponse)req.GetResponse())
                using (var r = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                    return Json.Parse(r.ReadToEnd());
            }
            catch (WebException) { return null; }
            catch (IOException) { return null; }
        }
    }

    /// <summary>Finestra di accesso a GitHub: mostra il codice, apre la pagina, attende la conferma.</summary>
    internal sealed class GitHubLoginWindow : Window
    {
        private readonly TextBlock code, status;
        private readonly Button open;
        private GitHubAuth.DeviceCode dc;
        private bool closed;
        public event Action SignedIn;

        public GitHubLoginWindow()
        {
            Title = "Collega GitHub Copilot";
            Width = 460;
            SizeToContent = SizeToContent.Height;
            ResizeMode = ResizeMode.NoResize;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            FontFamily = Theme.Font;
            UseLayoutRounding = true;
            Icon = Controller.AppIconImage();
            WindowChrome.SetWindowChrome(this, new WindowChrome { CaptionHeight = 32, GlassFrameThickness = new Thickness(-1), UseAeroCaptionButtons = true, ResizeBorderThickness = new Thickness(0) });
            SourceInitialized += (o, e) => Ui.ApplyBackdrop(this, 2, false);

            var root = new StackPanel { Margin = new Thickness(24, 40, 24, 24) };
            var h = Ui.Text("Collega GitHub Copilot", 20, "Text", FontWeights.SemiBold);
            h.FontFamily = Theme.DisplayFont;
            root.Children.Add(h);
            var info = Ui.Text("Inserisci questo codice nella pagina di GitHub che si apre. AI Meter userà l'accesso solo per leggere le quote di Copilot. "
                             + "GitHub mostrerà la richiesta come \"Visual Studio Code\": è l'app ufficiale di Copilot, attraverso cui si leggono le quote.", 13, "Text2");
            info.TextWrapping = TextWrapping.Wrap;
            info.TextTrimming = TextTrimming.None;
            info.Margin = new Thickness(0, 8, 0, 16);
            root.Children.Add(info);
            code = Ui.Text("…", 30, "Text", FontWeights.SemiBold);
            code.FontFamily = new System.Windows.Media.FontFamily("Cascadia Mono, Consolas");
            code.HorizontalAlignment = HorizontalAlignment.Center;
            root.Children.Add(code);
            status = Ui.Text("Richiedo il codice a GitHub…", 12, "Text3");
            status.HorizontalAlignment = HorizontalAlignment.Center;
            status.Margin = new Thickness(0, 6, 0, 18);
            root.Children.Add(status);

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            open = new Button { Content = "Copia il codice e apri GitHub", IsEnabled = false, Margin = new Thickness(0, 0, 8, 0) };
            open.SetResourceReference(StyleProperty, "AccentButton");
            open.Click += (o, e) => OpenGitHub();
            var cancel = new Button { Content = "Annulla" };
            cancel.SetResourceReference(StyleProperty, "StdButton");
            cancel.Click += (o, e) => Close();
            buttons.Children.Add(open);
            buttons.Children.Add(cancel);
            root.Children.Add(buttons);
            Content = root;

            Closed += (o, e) => closed = true;
            Loaded += async (o, e) => await Run();
        }

        private void OpenGitHub()
        {
            if (dc == null) return;
            try { Clipboard.SetText(dc.User); } catch (System.Runtime.InteropServices.COMException) { }
            Process.Start(dc.Uri);
            status.Text = "Codice copiato. Conferma su GitHub, poi torna qui.";
        }

        private async Task Run()
        {
            dc = await Task.Run(() => GitHubAuth.Start());
            if (closed) return;
            if (dc == null) { status.Text = "GitHub non risponde. Riprova più tardi."; return; }
            code.Text = dc.User;
            open.IsEnabled = true;
            status.Text = "In attesa della conferma su GitHub…";
            var until = DateTime.Now.AddSeconds(dc.ExpiresIn);
            int wait = Math.Max(5, dc.Interval);
            while (!closed && DateTime.Now < until)
            {
                await Task.Delay(wait * 1000);
                if (closed) return;
                string token = null;
                string r = await Task.Run(() => { string t; var res = GitHubAuth.Poll(dc, out t); token = t; return res; });
                if (r == "ok")
                {
                    GitHubAuth.Save(token);
                    status.Text = "Collegato.";
                    if (SignedIn != null) SignedIn();
                    await Task.Delay(800);
                    Close();
                    return;
                }
                if (r == "slow_down") wait += 5;
                else if (r != "pending") { status.Text = r == "access_denied" ? "Accesso negato su GitHub." : "Il codice è scaduto. Riapri questa finestra."; return; }
            }
            if (!closed) status.Text = "Il codice è scaduto. Riapri questa finestra.";
        }
    }
}
