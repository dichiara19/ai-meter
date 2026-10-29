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
    /// <summary>
    /// Windows 11-style settings with section navigation and one page per section.
    /// Each service has its own page reached from the service list.
    /// </summary>
    internal sealed class SettingsWindow : Window
    {
        private readonly Controller C;
        private Settings S { get { return C.Settings; } }
        private readonly StackPanel page = new StackPanel { Margin = new Thickness(36, 0, 36, 36), MaxWidth = 1000 };
        private readonly StackPanel nav = new StackPanel { Margin = new Thickness(8, 4, 8, 8) };
        private readonly ScrollViewer scroll;
        private readonly ColumnDefinition navColumn = new ColumnDefinition { Width = new GridLength(280) };
        private readonly DispatcherTimer stats = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        private TextBlock statsText;
        private TextBlock titleText;
        private TimeSpan lastCpu;
        private DateTime lastCpuAt;
        private bool compact;

        /// <summary>Current page: services, service:&lt;id&gt;, display, notifications, general or about.</summary>
        private string current = "services";

        private static readonly string[][] Sections =
        {
            new[] { "services", "", "Services" },
            new[] { "display", "", "Where to see it" },
            new[] { "notifications", "", "Notifications" },
            new[] { "history", "", "Usage history" },
            new[] { "general", "", "General" },
            new[] { "about", "", "About" },
        };

        public SettingsWindow(Controller c)
        {
            C = c;
            Title = L.T("AI Meter Settings");
            Width = 1000; Height = 740; MinWidth = 560; MinHeight = 480;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            FontFamily = Theme.Font;
            UseLayoutRounding = true;
            Icon = Controller.AppIconImage();
            Program.Brand(this);
            Ui.MakeSettingsWindow(this);

            var root = new DockPanel();
            var titleBar = new StackPanel { Orientation = Orientation.Horizontal, Height = 48, Margin = new Thickness(16, 0, 0, 0) };
            var ico = new Image { Source = Icon, Width = 16, Height = 16, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
            titleBar.Children.Add(ico);
            var tt = titleText = Ui.Text(Title, 12, "Text");
            tt.VerticalAlignment = VerticalAlignment.Center;
            titleBar.Children.Add(tt);
            DockPanel.SetDock(titleBar, Dock.Top);
            root.Children.Add(titleBar);

            var body = new Grid();
            body.ColumnDefinitions.Add(navColumn);
            body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var navScroll = new ScrollViewer { Content = nav, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Focusable = false };
            body.Children.Add(navScroll);
            scroll = new ScrollViewer { Content = page, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Focusable = false };
            Grid.SetColumn(scroll, 1);
            body.Children.Add(scroll);
            root.Children.Add(body);
            Content = root;

            // Collapse navigation to icons in narrow windows, matching Windows Settings.
            SizeChanged += (o, e) =>
            {
                bool c2 = e.NewSize.Width < 820;
                if (c2 == compact) return;
                compact = c2;
                navColumn.Width = new GridLength(compact ? 64 : 280);
                BuildNav();
            };

            stats.Tick += (o, e) => UpdateStats();
            Loaded += (o, e) => { lastCpu = Process.GetCurrentProcess().TotalProcessorTime; lastCpuAt = DateTime.Now; stats.Start(); };
            Closed += (o, e) => stats.Stop();
            Build();
        }

        /// <summary>Open a page and scroll to its top.</summary>
        private void Open(string id)
        {
            current = id;
            Build();
            scroll.ScrollToTop();
        }

        public void Build()
        {
            Title = L.T("AI Meter Settings");
            if (titleText != null) titleText.Text = Title;
            BuildNav();
            page.Children.Clear();
            statsText = null;
            if (current.StartsWith("service:", StringComparison.Ordinal)) BuildService(current.Substring(8));
            else if (current == "display") BuildDisplay();
            else if (current == "notifications") BuildNotifications();
            else if (current == "history") BuildHistory();
            else if (current == "general") BuildGeneral();
            else if (current == "about") BuildAbout();
            else BuildServices();
        }

        // ------------------------------------------------------------ section navigation
        private void BuildNav()
        {
            nav.Children.Clear();
            string section = current.StartsWith("service:", StringComparison.Ordinal) ? "services" : current;
            foreach (var s in Sections)
            {
                string id = s[0];
                bool selected = id == section;
                var item = new Border { Height = 36, CornerRadius = new CornerRadius(4), Margin = new Thickness(0, 1, 0, 1), Background = Brushes.Transparent, Cursor = Cursors.Hand };
                if (selected) item.SetResourceReference(Border.BackgroundProperty, "SubtleHover");
                var g = new Grid();
                // Mark the active section with an accent bar on the left.
                var mark = new Border { Width = 3, Height = 16, CornerRadius = new CornerRadius(1.5), HorizontalAlignment = HorizontalAlignment.Left, Visibility = selected ? Visibility.Visible : Visibility.Hidden };
                mark.SetResourceReference(Border.BackgroundProperty, "Accent");
                g.Children.Add(mark);
                var glyph = Ui.Glyph(s[1], 16, "Text");
                glyph.HorizontalAlignment = HorizontalAlignment.Left;
                glyph.VerticalAlignment = VerticalAlignment.Center;
                glyph.Margin = new Thickness(compact ? 16 : 16, 0, 0, 0);
                g.Children.Add(glyph);
                if (!compact)
                {
                    var label = Ui.Text(L.T(s[2]), 14, "Text", selected ? FontWeights.SemiBold : FontWeights.Normal);
                    label.VerticalAlignment = VerticalAlignment.Center;
                    label.Margin = new Thickness(48, 0, 8, 0);
                    g.Children.Add(label);
                }
                else item.ToolTip = L.T(s[2]);
                item.Child = g;
                System.Windows.Automation.AutomationProperties.SetName(item, L.T(s[2]));
                if (!selected)
                {
                    item.MouseEnter += (o, e) => item.SetResourceReference(Border.BackgroundProperty, "SubtleHover");
                    item.MouseLeave += (o, e) => item.Background = Brushes.Transparent;
                }
                item.MouseLeftButtonUp += (o, e) => Open(id);
                item.Focusable = true;
                item.KeyDown += (o, e) =>
                {
                    if (e.Key == Key.Enter || e.Key == Key.Space) { Open(id); e.Handled = true; }
                };
                item.GotKeyboardFocus += (o, e) => { item.SetResourceReference(Border.BorderBrushProperty, "Accent"); item.BorderThickness = new Thickness(1); };
                item.LostKeyboardFocus += (o, e) => item.BorderThickness = new Thickness(0);
                nav.Children.Add(item);
            }
        }

        // ------------------------------------------------------------ page headings
        private void Header(string title)
        {
            var h = Ui.Text(title, 28, "Text", FontWeights.SemiBold);
            h.FontFamily = Theme.DisplayFont;
            h.Margin = new Thickness(0, 8, 0, 20);
            page.Children.Add(h);
        }

        /// <summary>The Services breadcrumb returns to the list, matching Windows Settings navigation.</summary>
        private void Breadcrumb(string parent, string parentId, string title)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 20) };
            var back = Ui.Text(parent, 28, "Text2", FontWeights.SemiBold);
            back.FontFamily = Theme.DisplayFont;
            back.Cursor = Cursors.Hand;
            back.MouseEnter += (o, e) => back.SetResourceReference(TextBlock.ForegroundProperty, "Text");
            back.MouseLeave += (o, e) => back.SetResourceReference(TextBlock.ForegroundProperty, "Text2");
            back.MouseLeftButtonUp += (o, e) => Open(parentId);
            row.Children.Add(back);
            var sep = Ui.Glyph("", 16, "Text2");
            sep.VerticalAlignment = VerticalAlignment.Center;
            sep.Margin = new Thickness(14, 4, 14, 0);
            row.Children.Add(sep);
            var h = Ui.Text(title, 28, "Text", FontWeights.SemiBold);
            h.FontFamily = Theme.DisplayFont;
            h.TextTrimming = TextTrimming.CharacterEllipsis;
            row.Children.Add(h);
            page.Children.Add(row);
        }

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
            var p = C.Last == null ? null : C.Last.Providers.FirstOrDefault(x => x.Id == s.Id);
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
            var prov = C.Last == null ? null : C.Last.Providers.FirstOrDefault(x => x.Id == s.Id);
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
            var claude = C.Last == null ? null : C.Last.Providers.FirstOrDefault(p => p.Id == "claude");
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
                    var d = Definitions.Get(id);
                    if (d != null && d.UsesKey)
                    {
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
                    break;
            }
            var def2 = Definitions.Get(id);
            if (def2 != null && Json.Obj(def2.Raw, "spend") != null) rows.Add(BudgetRow(def2));
            if (id != "local") AddLimitRow(rows, id);
            return rows;
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

        // ------------------------------------------------------------ display surfaces
        private void BuildDisplay()
        {
            Header(L.T("Where to see it"));
            Group(
                Row(Ui.Glyph("", 20, "Text"), L.T("Desktop widget"), L.T("Stays below your windows, like part of the wallpaper. Drag it by its header."),
                    Pair(Button(L.T("Reset position"), () => C.ResetDesktopPosition(), false), Toggle(S.DesktopWidget, v => { S.DesktopWidget = v; Changed(); }))));
            var bar = TaskbarInfo.Read();
            string rec = L.T(bar.RecommendedSide == "left" ? "left corner" : "next to the notification area");
            Group(
                Row(Ui.Glyph("", 20, "Text"), L.T("In the taskbar"),
                    L.T("The pill sits in the free space of the main monitor's taskbar; a click opens the full panel.") + " " + bar.Describe(),
                    Toggle(S.TaskbarDock, v => { C.SetTaskbarDock(v); Rebuild(); })),
                Row(null, L.T("Position in the taskbar"), null, Combo(new[]
                {
                    Tuple.Create("auto", L.F("Automatic ({0})", rec)), Tuple.Create("left", L.T("Left corner")),
                    Tuple.Create("right", L.T("Next to the notification area")),
                }, S.TaskbarSide, v => { S.TaskbarSide = v; Changed(); })));
            Hint(L.T("Taskbar and overlay are alternatives: turning one on turns the other off."));
            Group(
                Row(Ui.Glyph("", 20, "Text"), L.T("Compact overlay"), L.T("A tiny pill, always on top. Hover over it to see the details."),
                    Toggle(S.Overlay, v => { S.Overlay = v; if (v) S.TaskbarDock = false; Changed(); Rebuild(); })),
                Row(null, L.T("Position"), null, Combo(new[]
                {
                    Tuple.Create("tr", L.T("Top right")), Tuple.Create("tl", L.T("Top left")),
                    Tuple.Create("br", L.T("Bottom right")), Tuple.Create("bl", L.T("Bottom left")),
                }, S.OverlayCorner, v => { S.OverlayCorner = v; Changed(); })),
                Row(null, L.T("Values shown"), L.T("Remaining share of the chosen limit, for services whose limit in the taskbar is set to Default."), Combo(new[]
                {
                    Tuple.Create("5h", L.T("5-hour limit")), Tuple.Create("week", L.T("Weekly limit")), Tuple.Create("both", L.T("Both")),
                }, S.OverlayMetric, v => { S.OverlayMetric = v; Changed(); })),
                Row(null, L.T("Hide in full screen"), L.T("During games, videos and presentations the pill disappears by itself."),
                    Toggle(S.OverlayHideFullscreen, v => { S.OverlayHideFullscreen = v; Changed(); })));
            Hint(L.T("The full panel is always one click away from the AI Meter icon in the notification area."));
        }

        // ------------------------------------------------------------ notifications
        private void BuildNotifications()
        {
            Header(L.T("Notifications"));
            Group(
                Row(Ui.Glyph("", 20, "Text"), L.T("When a limit runs low"),
                    L.T("A Windows notification when less than 20% and 5% is left, with when it runs out at this pace and which other service still has room."),
                    Toggle(S.NotifyLow, v => { S.NotifyLow = v; S.Save(); })),
                Row(Ui.Glyph("", 20, "Text"), L.T("When a limit is available again"),
                    L.T("After a limit you had almost used up resets."),
                    Toggle(S.NotifyReset, v => { S.NotifyReset = v; S.Save(); })));

            // Group reset-notification choices by service.
            Section(L.T("When a limit resets"));
            Hint(L.T("Every time these limits reset, at the time the service announces:"));
            var providers = C.Last == null ? new List<ProviderState>() : C.Last.Providers
                .Where(p => p.Models == null && p.Usage != null && p.Usage.Windows.Any(w => w.HasPercent && (w.ResetsAt.HasValue || w.Length.HasValue))).ToList();
            if (providers.Count == 0) Group(Row(null, L.T("No limits yet"), L.T("They appear here after the first update of the services you turned on."), null));
            foreach (var p in providers)
            {
                var pp = p;
                var rows = new List<FrameworkElement> { Row(Ui.Logo(pp.Id, 24), pp.Name, null, null) };
                foreach (var w in pp.Usage.Windows.Where(x => x.HasPercent && (x.ResetsAt.HasValue || x.Length.HasValue)))
                {
                    var ww = w;
                    string key = pp.Id + "|" + ww.Key;
                    rows.Add(Row(null, Ui.LabelOf(ww), null,
                        Toggle(S.NotifiesReset(pp.Id, ww), v => { if (v) S.NotifyResets[key] = true; else S.NotifyResets.Remove(key); S.Save(); })));
                }
                Group(rows.ToArray());
            }

            Section(L.T("Tips"));
            Hint(L.T("Tips, in the panel and as a notification once per limit window:"));
            Group(
                Row(Ui.Glyph("", 20, "Text"), L.T("Unused limit before it resets"),
                    L.T("When a limit resets soon and, at your usual pace, a good part of it would go unused. For the longest limit of each service and for credits that expire: what is left is lost."),
                    Toggle(S.TipsUnused, v => { S.TipsUnused = v; Changed(); C.RefreshTips(); })),
                Row(null, L.T("Also for short limits"),
                    L.T("The same for shorter limits such as the 5-hour session, only when the longer limit has room to absorb them."),
                    Toggle(S.TipsShort, v => { S.TipsShort = v; Changed(); C.RefreshTips(); })),
                Row(Ui.Glyph("", 20, "Text"), L.T("Wait instead of paying"),
                    L.T("When a limit is used up, extra usage is on and the limit resets soon."),
                    Toggle(S.TipsWait, v => { S.TipsWait = v; Changed(); C.RefreshTips(); })));
            Hint(L.T("Do not disturb and Focus in Windows hold these notifications like any other."));
        }

        // ------------------------------------------------------------ general
        private void BuildHistory()
        {
            Header(L.T("Usage history"));
            Group(Row(null, L.T("Keep local usage history"), L.T("Save quota readings and supported session metrics on this PC, including while the panel is closed. No prompts, titles or credentials are archived."),
                Toggle(S.HistoryEnabled, value => { S.HistoryEnabled = value; Changed(); })),
                Row(null, L.T("Usage history"), L.T("Explore consumption, quota cycles, models and value for consumption. Export or delete the archive from the dashboard."),
                    Button(L.T("Open dashboard"), C.OpenHistory, true)));
            Group(Row(null, L.T("Ask for session feedback"), L.T("Optional: after 30 minutes without a logged response, ask whether the result was worth the consumption. At most once per hour; ignoring a request leaves the session unrated."),
                Toggle(S.SessionFeedback, value => { S.SessionFeedback = value; S.FeedbackEnabledSince = DateTime.Now; S.Save(); })),
                Row(null, L.T("Value for consumption"), L.T("Rate the useful work obtained relative to the quota or money consumed."), null));
        }

        private void BuildGeneral()
        {
            Header(L.T("General"));
            var langs = new[] { Tuple.Create("system", L.T("Use the Windows language")) }
                .Concat(L.Languages.Select(l => Tuple.Create(l.Key, l.Value))).ToArray();
            Group(
                Row(Ui.Glyph("", 20, "Text"), L.T("Start with Windows"), L.T("AI Meter starts at sign-in, silently, once the desktop is ready."),
                    Toggle(Settings.StartsWithWindows, v => { Settings.StartsWithWindows = v; })),
                Row(Ui.Glyph("", 20, "Text"), L.T("Language"), null,
                    Combo(langs, S.Language ?? "system", v => { S.Language = v; S.Save(); C.ApplyLanguage(); })),
                Row(Ui.Glyph("", 20, "Text"), L.T("Theme"), null, Combo(new[]
                {
                    Tuple.Create("system", L.T("Use the Windows theme")), Tuple.Create("light", L.T("Light")), Tuple.Create("dark", L.T("Dark")),
                }, S.Theme, v => { S.Theme = v; Changed(); C.ApplyTheme(); })),
                Row(Ui.Glyph("", 20, "Text"), L.T("Refresh rate"), L.T("Automatic: every 10 seconds, every 30 on battery or with Energy Saver. Stops while the PC is locked."),
                    Combo(new[] { Tuple.Create("auto", L.T("Automatic")), Tuple.Create("low", L.T("Maximum saving")) }, S.Refresh, v => { S.Refresh = v; Changed(); })));
            Section(L.T("Windows Subsystem for Linux"));
            Group(Row(Ui.Glyph("", 20, "Text"), L.T("WSL distribution"), L.T("Where to look for Claude Code and Codex installed in Linux. AI Meter never starts WSL: it reads it only if it is already running."),
                Combo(new[] { S.WslDistro }.Concat(WslDistros()).Where(x => !string.IsNullOrEmpty(x)).Distinct().Select(x => Tuple.Create(x, x)).ToArray(), S.WslDistro,
                      v => { S.WslDistro = v; Changed(); })));
        }

        // ------------------------------------------------------------ about
        private void BuildAbout()
        {
            Header(L.T("About"));
            statsText = Ui.Text("", 12, "Text2");
            statsText.TextWrapping = TextWrapping.Wrap;
            Group(
                Row(Ui.Glyph("", 20, "Text"), "AI Meter", null, Button(L.T("Open data folder"), () => Process.Start("explorer.exe", "\"" + Settings.Folder + "\""), false), statsText),
                Row(Ui.Glyph("", 20, "Text"), L.T("Privacy"),
                    L.T("Sessions and limits are read from local files. Only usage requests leave this PC: Claude (Anthropic), Codex (through the codex program), Copilot (GitHub) and each service you turn on, to its own site. Local models are read only on this PC. Each one can be turned off."),
                    Button(L.T("Source code"), () => Process.Start("https://github.com/dichiara19/ai-meter#privacy-policy"), false)));
            UpdateStats();
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

        private void Changed() { S.Save(); C.ApplySettings(); }

        /// <summary>
        /// Offer the service's available limit and value choices for the pill.
        /// Skip the selector when there is only one choice and no saved preference.
        /// </summary>
        private void AddLimitRow(List<FrameworkElement> rows, string id)
        {
            var p = C.Last == null ? null : C.Last.Providers.FirstOrDefault(x => x.Id == id);
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

        /// <summary>Rebuild the page when a setting changes other controls.</summary>
        private void Rebuild() { Dispatcher.BeginInvoke(new Action(Build), System.Windows.Threading.DispatcherPriority.Background); }

        private void UpdateStats()
        {
            if (statsText == null) return;
            var proc = Process.GetCurrentProcess();
            proc.Refresh();
            var now = DateTime.Now;
            var cpu = proc.TotalProcessorTime;
            double pct = (cpu - lastCpu).TotalMilliseconds / Math.Max(1, (now - lastCpuAt).TotalMilliseconds) / Environment.ProcessorCount * 100;
            lastCpu = cpu; lastCpuAt = now;
            statsText.Text = L.F("Version {5} · memory in use {0:0} MB (private {1:0} MB) · CPU {2:0.0}% · refresh every {3} s{4}",
                proc.WorkingSet64 / 1048576.0, proc.PrivateMemorySize64 / 1048576.0, pct, C.CurrentInterval,
                C.SavingPower ? " " + L.T("(energy saving)") : "", Controller.Version);
        }
        // ------------------------------------------------------------ Settings-style controls
        private void Section(string title)
        {
            var t = Ui.Text(title, 14, "Text", FontWeights.SemiBold);
            t.Margin = new Thickness(0, 28, 0, 8);
            page.Children.Add(t);
        }

        /// <summary>
        /// Keep help text 8 px from its heading and associated card.
        /// After a previous card, use 12 px of combined spacing to separate the groups.
        /// </summary>
        private void Hint(string text)
        {
            var t = Ui.Text(text, 12, "Text3");
            t.TextWrapping = TextWrapping.Wrap;
            bool afterTitle = page.Children.Count > 0 && page.Children[page.Children.Count - 1] is TextBlock;
            t.Margin = new Thickness(2, afterTitle ? 0 : 8, 0, 8);
            page.Children.Add(t);
        }

        private void Group(params FrameworkElement[] rows)
        {
            var stack = new StackPanel();
            for (int i = 0; i < rows.Length; i++)
            {
                if (i > 0)
                {
                    var div = new Border { Height = 1 };
                    div.SetResourceReference(Border.BackgroundProperty, "Divider");
                    stack.Children.Add(div);
                }
                stack.Children.Add(rows[i]);
            }
            var card = new Border { CornerRadius = new CornerRadius(6), BorderThickness = new Thickness(1), Child = stack, Margin = new Thickness(0, 0, 0, 4) };
            card.SetResourceReference(Border.BackgroundProperty, "Card");
            card.SetResourceReference(Border.BorderBrushProperty, "CardStroke");
            page.Children.Add(card);
        }

        /// <summary>
        /// Build a Settings-style navigation row with a logo, name, status, toggle and chevron.
        /// The entire row navigates except for the toggle.
        /// </summary>
        private static FrameworkElement Link(FrameworkElement icon, string title, string description, FrameworkElement control, Action open)
        {
            var chev = Ui.Glyph("", 12, "Text2");
            chev.VerticalAlignment = VerticalAlignment.Center;
            chev.Margin = new Thickness(16, 0, 0, 0);
            var right = new StackPanel { Orientation = Orientation.Horizontal };
            if (control != null) right.Children.Add(control);
            right.Children.Add(chev);
            var host = new Border { Background = Brushes.Transparent, Cursor = Cursors.Hand, CornerRadius = new CornerRadius(5), Child = Row(icon, title, description, right) };
            host.MouseEnter += (o, e) => host.SetResourceReference(Border.BackgroundProperty, "SubtleHover");
            host.MouseLeave += (o, e) => host.Background = Brushes.Transparent;
            host.MouseLeftButtonUp += (o, e) =>
            {
                // Do not navigate when the toggle is clicked.
                var d = e.OriginalSource as DependencyObject;
                while (d != null && d != host) { if (d is CheckBox) return; d = VisualTreeHelper.GetParent(d); }
                open();
            };
            System.Windows.Automation.AutomationProperties.SetName(host, title);
            return host;
        }

        private static FrameworkElement Row(FrameworkElement icon, string title, string description, FrameworkElement control, FrameworkElement extra = null)
        {
            // Match Windows Settings card heights: 68 px with an icon, 56 px without, including margins.
            var g = new Grid { MinHeight = icon != null ? 48 : 36, Margin = new Thickness(16, 10, 16, 10) };
            // Reserve a 44 px icon column, widened for service-detail logos.
            double iw = icon != null && !double.IsNaN(icon.Width) ? icon.Width + 16 : 44;
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(44, iw)) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            if (icon != null)
            {
                icon.HorizontalAlignment = HorizontalAlignment.Left;
                icon.VerticalAlignment = VerticalAlignment.Center;
                g.Children.Add(icon);
            }
            var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 16, 0) };
            texts.Children.Add(Ui.Text(title, 14, "Text"));
            if (!string.IsNullOrEmpty(description))
            {
                var d = Ui.Text(description, 12, "Text2");
                d.TextWrapping = TextWrapping.Wrap;
                d.TextTrimming = TextTrimming.None;
                texts.Children.Add(d);
            }
            if (extra != null) { extra.Margin = new Thickness(0, 2, 0, 0); texts.Children.Add(extra); }
            Grid.SetColumn(texts, 1);
            g.Children.Add(texts);
            if (control != null)
            {
                control.VerticalAlignment = VerticalAlignment.Center;
                Grid.SetColumn(control, 2);
                g.Children.Add(control);
            }
            return g;
        }

        private static FrameworkElement Pair(FrameworkElement a, FrameworkElement b)
        {
            var sp = new StackPanel { Orientation = Orientation.Horizontal };
            a.Margin = new Thickness(0, 0, 16, 0);
            a.VerticalAlignment = VerticalAlignment.Center;
            sp.Children.Add(a);
            sp.Children.Add(b);
            return sp;
        }

        private static CheckBox Toggle(bool value, Action<bool> changed)
        {
            var cb = new CheckBox { IsChecked = value };
            cb.SetResourceReference(FrameworkElement.StyleProperty, "ToggleSwitch");
            cb.Checked += (o, e) => changed(true);
            cb.Unchecked += (o, e) => changed(false);
            return cb;
        }

        private static Button Button(string label, Action click, bool accent)
        {
            var b = new Button { Content = label };
            b.SetResourceReference(FrameworkElement.StyleProperty, accent ? "AccentButton" : "StdButton");
            b.Click += (o, e) => click();
            return b;
        }

        private static ComboBox Combo(Tuple<string, string>[] options, string selected, Action<string> changed)
        {
            var cb = new ComboBox();
            cb.SetResourceReference(FrameworkElement.StyleProperty, "Combo");
            foreach (var o in options)
            {
                var item = new ComboBoxItem { Content = o.Item2, Tag = o.Item1 };
                cb.Items.Add(item);
                if (o.Item1 == selected) cb.SelectedItem = item;
            }
            if (cb.SelectedIndex < 0 && cb.Items.Count > 0) cb.SelectedIndex = 0;
            cb.SelectionChanged += (o, e) => { var it = cb.SelectedItem as ComboBoxItem; if (it != null) changed((string)it.Tag); };
            return cb;
        }

        private static string[] WslDistros()
        {
            try
            {
                using (var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Lxss"))
                {
                    if (k == null) return new string[0];
                    return k.GetSubKeyNames().Select(n =>
                    {
                        using (var d = k.OpenSubKey(n)) return d == null ? null : d.GetValue("DistributionName") as string;
                    }).Where(n => n != null && !n.StartsWith("docker-desktop", StringComparison.OrdinalIgnoreCase)).ToArray();
                }
            }
            catch (Exception) { return new string[0]; }
        }
    }
}
