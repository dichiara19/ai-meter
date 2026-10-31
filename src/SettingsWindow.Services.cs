using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shell;
using System.Windows.Threading;

namespace AIMeter
{
    internal partial class SettingsWindow
    {
        // ------------------------------------------------------------ service list
        /// <summary>Service-list metadata: identity, logo, name, enabled state and toggle action.</summary>
        private sealed class Service
        {
            public string Id, Name;
            public FrameworkElement Logo;
            public bool On;
            public Action<bool> Set;
        }

        private IEnumerable<Service> Services()
        {
            yield return new Service { Id = "claude", Name = "Claude", Logo = Ui.Logo("claude", 28), On = S.ClaudeEnabled, Set = v => S.ClaudeEnabled = v };
            yield return new Service { Id = "openai", Name = "ChatGPT · Codex", Logo = Ui.Logo("openai", 28), On = S.OpenAIEnabled, Set = v => S.OpenAIEnabled = v };
            yield return new Service { Id = "copilot", Name = "GitHub Copilot", Logo = Ui.Logo("copilot", 28), On = S.CopilotEnabled, Set = v => S.CopilotEnabled = v };
            yield return new Service { Id = "antigravity", Name = "Google Antigravity", Logo = Ui.Logo("antigravity", 28), On = S.AntigravityEnabled, Set = v => S.AntigravityEnabled = v };
            foreach (var def in Definitions.All)
            {
                var d = def;
                yield return new Service { Id = d.Id, Name = d.Name, Logo = Ui.Logo(d.Id, 28), On = S.ServiceOn(d), Set = v => S.Services[d.Id] = v };
            }
            yield return new Service { Id = "local", Name = L.T("Local models"), Logo = Ui.Glyph(Ui.LocalGlyph, 22, "Text"), On = S.LocalEnabled, Set = v => S.LocalEnabled = v };
        }

        private void BuildServices()
        {
            Header(L.T("Services"));
            Hint(L.T("Turn on only the services you use. On first run AI Meter turns on the ones it finds on this PC."));
            foreach (var s in Services())
            {
                var sv = s;
                Group(Link(sv.Logo, sv.Name, StatusOf(sv), Toggle(sv.On, v => { sv.Set(v); Changed(); Rebuild(); }), () => Open("service:" + sv.Id)));
            }
            Hint(L.T("API keys can come from the programs that already use them, so you don't have to paste them again:"));
            Group(Row(Ui.Glyph("", 20, "Text"), L.T("Use API keys already on this PC"),
                L.T("OpenRouter, DeepSeek, Kimi, Vercel and other keys saved by OpenCode, Pi or Crush, or exported in your shell profile, on Windows and in WSL while it is running. Each key is sent only to its own service."),
                Toggle(S.FindKeys, v => { S.FindKeys = v; KeyFinder.Forget(); Changed(); C.RefreshNow(); Rebuild(); })));
            Hint(L.T("More services can be added with a small definition file, without updating AI Meter."));
            Group(Row(Ui.Glyph("", 20, "Text"), L.T("Add a service"),
                L.T("Put definition files in this folder and restart AI Meter. The format is described in the project documentation."),
                Pair(Button(L.T("Format"), () => Process.Start("https://github.com/dichiara19/ai-meter/blob/main/docs/PROVIDERS.md"), false),
                     Button(L.T("Open folder"), () => { System.IO.Directory.CreateDirectory(Definitions.UserFolder); Process.Start("explorer.exe", "\"" + Definitions.UserFolder + "\""); }, false))));
        }

        /// <summary>Compact list status: off, authentication needed, or the pill's selected limit.</summary>
        private string StatusOf(Service s)
        {
            // The toggle already indicates off; use the subtitle to report local detection.
            if (!s.On)
            {
                var def = Definitions.Get(s.Id);
                var found = def != null && !ApiKeys.Has(def.Id) ? KeyFinder.Find(def) : null;
                if (found != null) return L.F("Key found in {0}", found.Source);
                if (def != null && def.UsesKey && !ApiKeys.Has(def.Id) && !def.Detected()) return L.T("Add an API key to read your usage.");
                return s.Id == "local" ? L.T("Off") : Found(Detected(s.Id)).Trim();
            }
            var p = LastState(s.Id);
            if (s.Id == "claude") return ClaudeStatus(p);
            if (p == null) return L.T("Waiting for the first update");
            if (p.Models != null)
                return p.Models.Count > 0 ? L.N(p.Models.Count, "{0} local model loaded", "{0} local models loaded") : L.T("Local models: none loaded");
            if (p.NeedsLogin && !string.IsNullOrEmpty(p.Notice)) return p.Notice;
            var w = Ui.Primary(p);
            string plan = p.Plan != null ? L.F("{0} plan", p.Plan.ToUpperInvariant()) + " · " : "";
            if (w != null) return plan + L.F("{0}% left · {1}", Math.Round(p.Usage.Room(w)), Ui.LabelOf(w));
            var bal = p.Usage == null ? null : p.Usage.Windows.FirstOrDefault(x => x.NoBar && x.RightText != null);
            if (bal != null) return plan + Ui.LabelOf(bal) + ": " + bal.RightText;
            if (!string.IsNullOrEmpty(p.Notice)) return p.Notice;
            return L.T("On");
        }

        // ------------------------------------------------------------ service detail pages
        private void BuildService(string id)
        {
            var s = Services().FirstOrDefault(x => x.Id == id);
            if (s == null) { current = "services"; BuildServices(); return; }
            Breadcrumb(L.T("Services"), "services", s.Name);
            var logo = s.Id == "local" ? Ui.Glyph(Ui.LocalGlyph, 30, "Text") : Ui.Logo(s.Id, 40);
            Group(Row(logo, s.Name, Description(s.Id), Toggle(s.On, v => { s.Set(v); Changed(); Rebuild(); })));
            if (!s.On) { Hint(L.T("Turn the service on to see its options.")); return; }
            var rows = Options(s.Id);
            if (rows.Count > 0)
            {
                Section(L.T("Options"));
                Group(rows.ToArray());
            }

            // Let users choose which limits, spending summaries and models appear in a service card.
            var prov = LastState(s.Id);
            if (prov == null || prov.Usage == null || prov.Models != null) return;
            var vis = new List<FrameworkElement>();
            foreach (var w in prov.Usage.Windows.Where(x => !x.Hidden))
                vis.Add(Row(null, Ui.LabelOf(w), null, RowToggle(s.Id, w.Key)));
            var def = Definitions.Get(s.Id);
            bool credits = def != null && Json.Obj(def.Raw, "spend") != null;
            if (prov.Usage.SpendNote != null || credits)
                vis.Add(Row(null, L.T("Spend today, this week and this month"), null, RowToggle(s.Id, "*spend")));
            if (prov.Usage.TodayModels != null || credits)
                vis.Add(Row(null, L.T("Models used today"), L.T("From OpenCode and Pi, with the cost they computed."), RowToggle(s.Id, "*models")));
            if (prov.Usage.FreeModels != null || (def != null && Json.Obj(def.Raw, "freeModels") != null))
                vis.Add(Row(null, L.T("Free models"), L.T("From the service's public model list, with the date each one stops being free when announced."), RowToggle(s.Id, "*free")));
            if (vis.Count == 0) return;
            Section(L.T("In the panel"));
            Hint(L.T("What this service's card shows in the panel and in the desktop widget. The pill shows the limit chosen in Limit in the taskbar."));
            Group(vis.ToArray());
        }

        private CheckBox RowToggle(string id, string key)
        {
            return Toggle(!S.RowHidden(id, key), v =>
            {
                if (v) S.HiddenRows.Remove(id + "|" + key); else S.HiddenRows[id + "|" + key] = true;
                Changed();
            });
        }

        private string Description(string id)
        {
            var claude = LastState("claude");
            switch (id)
            {
                case "claude":
                    return S.ClaudeEnabled ? ClaudeStatus(claude) + ". " + L.T("Claude Code, Cowork and the desktop app.") : Found(ClaudeProvider.Detected()) + L.T("Claude Code, Cowork and the desktop app.");
                case "openai":
                    return Found(CodexProvider.Detected()) + L.T("ChatGPT plan limits in real time, requested from the installed Codex program with its own login: AI Meter never touches the tokens.");
                case "copilot":
                    return Found(CopilotProvider.Detected()) + L.T("Premium requests and monthly quotas of your GitHub account.");
                case "antigravity":
                    return Found(AntigravityProvider.Detected()) + L.T("5-hour and weekly quotas of your Google AI plan in Antigravity, per model group. Read with the Antigravity CLI sign-in (also in WSL). The Gemini app does not expose its limits.");
                case "local":
                    return L.T("Models loaded by Ollama, LM Studio, llama.cpp, llama-swap and vLLM, on Windows and in WSL: video memory, context and requests in progress. Only servers on this PC are contacted.");
            }
            var d = Definitions.Get(id);
            if (d == null) return null;
            return (d.UsesKey ? "" : Found(d.Detected())) + (d.Description != null ? L.T(d.Description) : "")
                   + (d.Origin != "built-in" ? " " + L.T("Added by you.") : "");
        }

        /// <summary>Build an enabled service's authentication, display and pill options.</summary>
        private List<FrameworkElement> Options(string id)
        {
            var rows = new List<FrameworkElement>();
            switch (id)
            {
                case "claude":
                    rows.Add(Row(null, L.T("Real-time limits"),
                        L.T("Reads your account usage with your Claude Code login (WSL or Windows). The token stays on this PC and is refreshed by the CLI."),
                        Pair(Button(L.T("Sign in…"), () => C.LoginClaude(), false), Toggle(S.ClaudeLiveUsage, v => { S.ClaudeLiveUsage = v; Changed(); C.RefreshNow(); }))));
                    rows.Add(Row(null, L.T("Claude Code sessions"), L.T("Desktop, CLI (also in WSL) and VS Code, with title, model and status."), Toggle(S.ClaudeShowCode, v => { S.ClaudeShowCode = v; Changed(); })));
                    rows.Add(Row(null, L.T("Cowork tasks"), L.T("Cowork tasks running in the last 30 minutes."), Toggle(S.ClaudeShowCowork, v => { S.ClaudeShowCowork = v; Changed(); })));
                    rows.Add(Row(null, L.T("Claude app open"), L.T("Individual chats leave no trace on the PC: you only see whether the app is open."), Toggle(S.ClaudeShowChat, v => { S.ClaudeShowChat = v; Changed(); })));
                    break;
                case "openai":
                    rows.Add(Row(null, L.T("ChatGPT app open"), L.T("ChatGPT does not expose chat limits: you only see whether the app is open."), Toggle(S.OpenAIShowChat, v => { S.OpenAIShowChat = v; Changed(); })));
                    break;
                case "copilot":
                    bool gh = GitHubAuth.HasToken;
                    rows.Add(Row(null, L.T("GitHub account"),
                        gh ? L.T("Connected. The token is encrypted with Windows data protection and stays on this PC.")
                           : L.T("Connect your account to read your Copilot quotas."),
                        gh ? Button(L.T("Disconnect"), () => { C.SignOutGitHub(); Build(); }, false) : Button(L.T("Connect GitHub…"), () => C.LoginGitHub(), true)));
                    break;
                case "antigravity":
                    rows.Add(Row(null, L.T("Antigravity CLI sign-in"),
                        L.T("AI Meter uses the sign-in of Antigravity CLI and lets the CLI renew it: the token stays where the CLI keeps it."),
                        Button(L.T("Sign in…"), () => C.LoginAntigravity(), false)));
                    break;
                case "local":
                    rows.Add(Row(null, L.T("Coding agent sessions"), L.T("OpenCode, Pi and other terminal agents: open sessions and the model each one uses, read from their local files."),
                        Toggle(S.LocalAgents, v => { S.LocalAgents = v; Changed(); })));
                    break;
                default:
                    AddKeyRows(rows, Definitions.Get(id));
                    break;
            }
            var def2 = Definitions.Get(id);
            if (def2 != null && Json.Obj(def2.Raw, "spend") != null) rows.Add(BudgetRow(def2));
            if (id != "local") AddLimitRow(rows, id);
            return rows;
        }

        /// <summary>API-key rows for a key-based service: the main key, then extra keys that are saved or were requested.</summary>
        private void AddKeyRows(List<FrameworkElement> rows, ServiceDefinition d)
        {
            if (d == null || !d.UsesKey) return;
            var found = ApiKeys.Has(d.Id) ? null : KeyFinder.Find(d);
            string text = ApiKeys.Has(d.Id) ? L.T("Saved. It is encrypted with Windows data protection and stays on this PC.")
                        : found != null ? L.F("Using the key found in {0}. AI Meter reads it where it is and sends it only to {1}.", found.Source, d.Name)
                        : L.T("Add an API key to read your usage.");
            rows.Add(Row(null, L.T("API key"), text,
                ApiKeys.Has(d.Id) ? Button(L.T("Remove"), () => { C.RemoveApiKey(d); Build(); }, false)
                                  : Button(found != null ? L.T("Use another key…") : L.T("Add key…"), () => C.EnterApiKey(d), found == null)));
            // Offer extra credentials only when needed or already saved.
            var keys = Json.Obj(d.Raw, "keys");
            if (keys != null)
                foreach (var kv in keys)
                {
                    string slot = kv.Key;
                    var spec = kv.Value as Dictionary<string, object>;
                    bool saved = ApiKeys.Has(d.Id + "." + slot);
                    bool needed;
                    lock (DefinedProvider.NeedsExtraKey) needed = DefinedProvider.NeedsExtraKey.Contains(d.Id + "." + slot);
                    if (!saved && !needed) continue;
                    rows.Add(Row(null, L.T(Json.Str(spec, "label") ?? slot),
                        saved ? L.T("Saved. It is encrypted with Windows data protection and stays on this PC.") : L.T(Json.Str(spec, "description") ?? ""),
                        saved ? Button(L.T("Remove"), () => { ApiKeys.Remove(d.Id + "." + slot); C.RefreshNow(); Build(); }, false)
                              : Button(L.T("Add key…"), () => C.EnterApiKey(d, slot), true)));
                }
        }

        /// <summary>Edit a credit service's budget amount and period; an empty amount disables it.</summary>
        private FrameworkElement BudgetRow(ServiceDefinition d)
        {
            double amount;
            string period;
            bool has = SpendTracker.Budget(S, d.Id, out amount, out period);
            var box = new TextBox { Width = 80, FontSize = 14, BorderThickness = new Thickness(0), Background = Brushes.Transparent, Padding = new Thickness(0), Text = has ? amount.ToString("0.##", L.Culture) : "" };
            box.SetResourceReference(Control.ForegroundProperty, "Text");
            box.SetResourceReference(TextBox.CaretBrushProperty, "Text");
            System.Windows.Automation.AutomationProperties.SetName(box, L.T("Spending budget"));
            var frame = new Border { CornerRadius = new CornerRadius(4), BorderThickness = new Thickness(1, 1, 1, 2), Padding = new Thickness(10, 5, 10, 5), Child = box };
            frame.SetResourceReference(Border.BackgroundProperty, "Control");
            frame.SetResourceReference(Border.BorderBrushProperty, "ControlStroke");
            box.GotKeyboardFocus += (o, e) => frame.SetResourceReference(Border.BorderBrushProperty, "Accent");
            box.LostKeyboardFocus += (o, e) => frame.SetResourceReference(Border.BorderBrushProperty, "ControlStroke");
            string chosen = period;
            Action save = () =>
            {
                double a;
                string t = box.Text.Trim().Replace("$", "").Replace("€", "");
                bool ok = double.TryParse(t, NumberStyles.Float, L.Culture, out a) || double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out a);
                string before;
                S.Budgets.TryGetValue(d.Id, out before);
                string after = ok && a > 0 ? a.ToString("R", CultureInfo.InvariantCulture) + "|" + chosen : null;
                if (after == before) return;
                if (after == null) S.Budgets.Remove(d.Id); else S.Budgets[d.Id] = after;
                Changed();
                C.RefreshNow();
            };
            box.LostFocus += (o, e) => save();
            box.KeyDown += (o, e) => { if (e.Key == Key.Enter) save(); };
            var per = Combo(new[]
            {
                Tuple.Create("day", L.T("per day")), Tuple.Create("week", L.T("per week")), Tuple.Create("month", L.T("per month")),
            }, period, v => { chosen = v; save(); });
            return Row(null, L.T("Spending budget"),
                L.T("An amount per day, week or month: the pill and the panel show how much of it is used, with a notification at 80% and 95%. Leave it empty for no budget.") + " "
                + L.T("Estimated spending is shown without remaining credit, forecasts or threshold alerts."),
                Pair(frame, per));
        }

        private string ClaudeStatus(ProviderState p)
        {
            if (!S.ClaudeEnabled) return L.T("Off");
            if (p == null) return L.T("Waiting for the first update");
            string plan = p.Plan != null ? " · " + L.F("{0} plan", p.Plan.ToUpperInvariant()) : "";
            if (p.Usage != null && p.Usage.Source == "live") return L.T("Connected") + plan + " · " + L.T("real-time limits");
            if (p.NeedsLogin) return L.T("Sign-in needed for real-time limits");
            if (!string.IsNullOrEmpty(p.Notice)) return p.Notice;
            return L.T("Limits from the last available data") + plan;
        }

        /// <summary>
        /// Offer the service's available limit and value choices for the pill.
        /// Skip the selector when there is only one choice and no saved preference.
        /// </summary>
        private void AddLimitRow(List<FrameworkElement> rows, string id)
        {
            var p = LastState(id);
            // Include monetary values, estimated budgets and temporarily cached rows.
            var windows = p == null || p.Usage == null ? new List<UsageWindow>() : p.Usage.Windows.Where(w => w.HasPercent || w.Hidden || w.Budget || w.NoBar || w.CachedAt.HasValue).ToList();
            string cur;
            S.PillLimits.TryGetValue(id, out cur);
            if (windows.Count < 2 && string.IsNullOrEmpty(cur)) return;
            // Name the limit selected by Default so its behaviour is visible.
            var dflt = p == null ? null : Ui.Primary(p.Usage);
            var options = new List<Tuple<string, string>>
            {
                Tuple.Create("", dflt != null ? L.F("Default ({0})", Ui.LabelOf(dflt)) : L.T("Default")),
                Tuple.Create("auto", L.T("Automatic: the one closest to running out")),
            };
            options.AddRange(windows.Select(w => Tuple.Create(w.Key, Ui.LabelOf(w))));
            if (!string.IsNullOrEmpty(cur) && !options.Any(o => o.Item1 == cur)) options.Add(Tuple.Create(cur, L.T("Not available right now")));
            rows.Add(Row(null, L.T("Limit in the taskbar"), L.T("Which limit the pill, its tooltip and the overlay show for this service."),
                Combo(options.ToArray(), cur ?? "", v => { if (v == "") S.PillLimits.Remove(id); else S.PillLimits[id] = v; Changed(); })));
        }

        private static string Found(bool detected) { return L.T(detected ? "Found on this PC." : "Not found on this PC.") + " "; }

        private static bool Detected(string id)
        {
            switch (id)
            {
                case "claude": return ClaudeProvider.Detected();
                case "openai": return CodexProvider.Detected();
                case "copilot": return CopilotProvider.Detected();
                case "antigravity": return AntigravityProvider.Detected();
            }
            var d = Definitions.Get(id);
            return d != null && d.Detected();
        }
    }
}
