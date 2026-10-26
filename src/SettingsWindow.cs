using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shell;
using System.Windows.Threading;

namespace AIMeter
{
    /// <summary>Impostazioni nello stile dell'app Impostazioni di Windows 11.</summary>
    internal sealed class SettingsWindow : Window
    {
        private readonly Controller C;
        private Settings S { get { return C.Settings; } }
        private readonly StackPanel page = new StackPanel { Margin = new Thickness(36, 0, 36, 36), MaxWidth = 860 };
        private readonly DispatcherTimer stats = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        private TextBlock statsText;
        private TextBlock titleText;
        private TimeSpan lastCpu;
        private DateTime lastCpuAt;

        public SettingsWindow(Controller c)
        {
            C = c;
            Title = L.T("AI Meter Settings");
            Width = 760; Height = 720; MinWidth = 560; MinHeight = 480;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            FontFamily = Theme.Font;
            UseLayoutRounding = true;
            Icon = Controller.AppIconImage();
            Program.Brand(this);
            WindowChrome.SetWindowChrome(this, new WindowChrome
            {
                CaptionHeight = 48, GlassFrameThickness = new Thickness(-1), ResizeBorderThickness = new Thickness(6),
                UseAeroCaptionButtons = true, CornerRadius = new CornerRadius(0),
            });
            SourceInitialized += (o, e) => Ui.ApplyBackdrop(this, 2, false);

            var root = new DockPanel();
            var titleBar = new StackPanel { Orientation = Orientation.Horizontal, Height = 48, Margin = new Thickness(16, 0, 0, 0) };
            var ico = new Image { Source = Icon, Width = 16, Height = 16, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
            titleBar.Children.Add(ico);
            var tt = titleText = Ui.Text(Title, 12, "Text");
            tt.VerticalAlignment = VerticalAlignment.Center;
            titleBar.Children.Add(tt);
            DockPanel.SetDock(titleBar, Dock.Top);
            root.Children.Add(titleBar);
            root.Children.Add(new ScrollViewer { Content = page, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Focusable = false });
            Content = root;

            stats.Tick += (o, e) => UpdateStats();
            Loaded += (o, e) => { lastCpu = Process.GetCurrentProcess().TotalProcessorTime; lastCpuAt = DateTime.Now; stats.Start(); };
            Closed += (o, e) => stats.Stop();
            Build();
        }

        public void Build()
        {
            page.Children.Clear();
            Title = L.T("AI Meter Settings");
            if (titleText != null) titleText.Text = Title;
            var h = Ui.Text(L.T("Settings"), 28, "Text", FontWeights.SemiBold);
            h.FontFamily = Theme.DisplayFont;
            h.Margin = new Thickness(0, 8, 0, 4);
            page.Children.Add(h);
            var sub = Ui.Text(L.T("Choose which AI services to monitor and where to see sessions and limits."), 14, "Text2");
            sub.TextWrapping = TextWrapping.Wrap;
            page.Children.Add(sub);

            // ------------------------------------------------ servizi
            // ogni servizio si attiva a parte; le sue opzioni compaiono solo quando è attivo
            Section(L.T("Services"));
            Hint(L.T("Turn on only the services you use. On first run AI Meter turns on the ones it finds on this PC."));
            var claude = C.Last == null ? null : C.Last.Providers.FirstOrDefault(p => p.Id == "claude");
            var claudeRows = new List<FrameworkElement>
            {
                Row(Ui.Logo("claude", 28), "Claude",
                    S.ClaudeEnabled ? ClaudeStatus(claude) : Found(ClaudeProvider.Detected()) + L.T("Claude Code, Cowork and the desktop app."),
                    Toggle(S.ClaudeEnabled, v => { S.ClaudeEnabled = v; Changed(); Rebuild(); })),
            };
            if (S.ClaudeEnabled)
                claudeRows.AddRange(new[]
                {
                    Row(null, L.T("Real-time limits"),
                        L.T("Reads your account usage with your Claude Code login (WSL or Windows). The token stays on this PC and is refreshed by the CLI."),
                        Pair(Button(L.T("Sign in…"), () => C.LoginClaude(), false), Toggle(S.ClaudeLiveUsage, v => { S.ClaudeLiveUsage = v; Changed(); C.RefreshNow(); }))),
                    Row(null, L.T("Claude Code sessions"), L.T("Desktop, CLI (also in WSL) and VS Code, with title, model and status."), Toggle(S.ClaudeShowCode, v => { S.ClaudeShowCode = v; Changed(); })),
                    Row(null, L.T("Cowork tasks"), L.T("Cowork tasks running in the last 30 minutes."), Toggle(S.ClaudeShowCowork, v => { S.ClaudeShowCowork = v; Changed(); })),
                    Row(null, L.T("Claude app open"), L.T("Individual chats leave no trace on the PC: you only see whether the app is open."), Toggle(S.ClaudeShowChat, v => { S.ClaudeShowChat = v; Changed(); })),
                });
            Group(claudeRows.ToArray());

            var openaiRows = new List<FrameworkElement>
            {
                Row(Ui.Logo("openai", 28), "ChatGPT · Codex",
                    Found(CodexProvider.Detected()) + L.T("ChatGPT plan limits in real time, requested from the installed Codex program with its own login: AI Meter never touches the tokens."),
                    Toggle(S.OpenAIEnabled, v => { S.OpenAIEnabled = v; Changed(); Rebuild(); })),
            };
            if (S.OpenAIEnabled)
                openaiRows.Add(Row(null, L.T("ChatGPT app open"), L.T("ChatGPT does not expose chat limits: you only see whether the app is open."), Toggle(S.OpenAIShowChat, v => { S.OpenAIShowChat = v; Changed(); })));
            Group(openaiRows.ToArray());

            bool gh = GitHubAuth.HasToken;
            var copilotRows = new List<FrameworkElement>
            {
                Row(Ui.Logo("copilot", 28), "GitHub Copilot",
                    Found(CopilotProvider.Detected()) + L.T("Premium requests and monthly quotas of your GitHub account."),
                    Toggle(S.CopilotEnabled, v => { S.CopilotEnabled = v; Changed(); Rebuild(); })),
            };
            if (S.CopilotEnabled)
                copilotRows.Add(Row(null, L.T("GitHub account"),
                    gh ? L.T("Connected. The token is encrypted with Windows data protection and stays on this PC.")
                       : L.T("Connect your account to read your Copilot quotas."),
                    gh ? Button(L.T("Disconnect"), () => { C.SignOutGitHub(); Build(); }, false) : Button(L.T("Connect GitHub…"), () => C.LoginGitHub(), true)));
            Group(copilotRows.ToArray());

            // servizi descritti dai file di definizione (inclusi e aggiunti dall'utente)
            foreach (var def in Definitions.All)
            {
                var d = def;
                bool on = S.ServiceOn(d);
                var rows = new List<FrameworkElement>
                {
                    Row(Ui.Logo(d.Id, 28), d.Name,
                        (d.UsesKey ? "" : Found(d.Detected())) + (d.Description != null ? L.T(d.Description) : "")
                        + (d.Origin != "built-in" ? " " + L.T("Added by you.") : ""),
                        Toggle(on, v => { S.Services[d.Id] = v; Changed(); Rebuild(); })),
                };
                if (on && d.UsesKey)
                    rows.Add(Row(null, L.T("API key"),
                        ApiKeys.Has(d.Id) ? L.T("Saved. It is encrypted with Windows data protection and stays on this PC.") : L.T("Add an API key to read your usage."),
                        ApiKeys.Has(d.Id) ? Button(L.T("Remove"), () => { C.RemoveApiKey(d); Build(); }, false) : Button(L.T("Add key…"), () => C.EnterApiKey(d), true)));
                Group(rows.ToArray());
            }
            var localRows = new List<FrameworkElement>
            {
                Row(Ui.Glyph(Ui.LocalGlyph, 22, "Text"), L.T("Local models"),
                    L.T("Models loaded by Ollama, LM Studio, llama.cpp, llama-swap and vLLM, on Windows and in WSL: video memory, context and requests in progress. Only servers on this PC are contacted."),
                    Toggle(S.LocalEnabled, v => { S.LocalEnabled = v; Changed(); Rebuild(); })),
            };
            if (S.LocalEnabled)
                localRows.Add(Row(null, L.T("Coding agent sessions"), L.T("OpenCode, Pi and other terminal agents: open sessions and the model each one uses, read from their local files."),
                    Toggle(S.LocalAgents, v => { S.LocalAgents = v; Changed(); })));
            Group(localRows.ToArray());

            Hint(L.T("More services can be added with a small definition file, without updating AI Meter."));
            Group(Row(Ui.Glyph("", 20, "Text"), L.T("Add a service"),
                L.T("Put definition files in this folder and restart AI Meter. The format is described in the project documentation."),
                Pair(Button(L.T("Format"), () => Process.Start("https://github.com/dichiara19/ai-meter/blob/main/docs/PROVIDERS.md"), false),
                     Button(L.T("Open folder"), () => { System.IO.Directory.CreateDirectory(Definitions.UserFolder); Process.Start("explorer.exe", "\"" + Definitions.UserFolder + "\""); }, false))));

            var wslRow = Row(null, L.T("WSL distribution"), L.T("Where to look for Claude Code and Codex installed in Linux. AI Meter never starts WSL: it reads it only if it is already running."),
                Combo(new[] { S.WslDistro }.Concat(WslDistros()).Where(x => !string.IsNullOrEmpty(x)).Distinct().Select(x => Tuple.Create(x, x)).ToArray(), S.WslDistro,
                      v => { S.WslDistro = v; Changed(); }));
            Group(wslRow);

            // ------------------------------------------------ dove vederlo
            Section(L.T("Where to see it"));
            Group(
                Row(Ui.Glyph("\uE7F4", 20, "Text"), L.T("Desktop widget"), L.T("Stays below your windows, like part of the wallpaper. Drag it by its header."),
                    Pair(Button(L.T("Reset position"), () => C.ResetDesktopPosition(), false), Toggle(S.DesktopWidget, v => { S.DesktopWidget = v; Changed(); }))));
            var bar = TaskbarInfo.Read();
            string rec = L.T(bar.RecommendedSide == "left" ? "left corner" : "next to the notification area");
            Group(
                Row(Ui.Glyph("\uE7C4", 20, "Text"), L.T("In the taskbar"),
                    L.T("The pill sits in the free space of the main monitor's taskbar; a click opens the full panel.") + " " + bar.Describe(),
                    Toggle(S.TaskbarDock, v => { C.SetTaskbarDock(v); Rebuild(); })),
                Row(null, L.T("Position in the taskbar"), null, Combo(new[]
                {
                    Tuple.Create("auto", L.F("Automatic ({0})", rec)), Tuple.Create("left", L.T("Left corner")),
                    Tuple.Create("right", L.T("Next to the notification area")),
                }, S.TaskbarSide, v => { S.TaskbarSide = v; Changed(); })));
            Hint(L.T("Taskbar and overlay are alternatives: turning one on turns the other off."));
            Group(
                Row(Ui.Glyph("\uE718", 20, "Text"), L.T("Compact overlay"), L.T("A tiny pill, always on top. Hover over it to see the details."),
                    Toggle(S.Overlay, v => { S.Overlay = v; if (v) S.TaskbarDock = false; Changed(); Rebuild(); })),
                Row(null, L.T("Position"), null, Combo(new[]
                {
                    Tuple.Create("tr", L.T("Top right")), Tuple.Create("tl", L.T("Top left")),
                    Tuple.Create("br", L.T("Bottom right")), Tuple.Create("bl", L.T("Bottom left")),
                }, S.OverlayCorner, v => { S.OverlayCorner = v; Changed(); })),
                Row(null, L.T("Values shown"), L.T("Remaining share of the chosen limit."), Combo(new[]
                {
                    Tuple.Create("5h", L.T("5-hour limit")), Tuple.Create("week", L.T("Weekly limit")), Tuple.Create("both", L.T("Both")),
                }, S.OverlayMetric, v => { S.OverlayMetric = v; Changed(); })),
                Row(null, L.T("Hide in full screen"), L.T("During games, videos and presentations the pill disappears by itself."),
                    Toggle(S.OverlayHideFullscreen, v => { S.OverlayHideFullscreen = v; Changed(); })));
            Hint(L.T("The full panel is always one click away from the AI Meter icon in the notification area."));

            // ------------------------------------------------ generale
            // ------------------------------------------------ notifiche
            Section(L.T("Notifications"));
            Group(
                Row(Ui.Glyph("", 20, "Text"), L.T("When a limit runs low"),
                    L.T("A Windows notification when less than 20% and 5% is left, with when it runs out at this pace and which other service still has room."),
                    Toggle(S.NotifyLow, v => { S.NotifyLow = v; S.Save(); })),
                Row(Ui.Glyph("", 20, "Text"), L.T("When a limit is available again"),
                    L.T("After a limit you had almost used up resets."),
                    Toggle(S.NotifyReset, v => { S.NotifyReset = v; S.Save(); })));
            Hint(L.T("Do not disturb and Focus in Windows hold these notifications like any other."));

            Section(L.T("General"));
            var langs = new[] { Tuple.Create("system", L.T("Use the Windows language")) }
                .Concat(L.Languages.Select(l => Tuple.Create(l.Key, l.Value))).ToArray();
            Group(
                Row(Ui.Glyph("\uE7E8", 20, "Text"), L.T("Start with Windows"), L.T("AI Meter starts at sign-in, silently, once the desktop is ready."),
                    Toggle(Settings.StartsWithWindows, v => { Settings.StartsWithWindows = v; })),
                Row(Ui.Glyph("\uF2B7", 20, "Text"), L.T("Language"), null,
                    Combo(langs, S.Language ?? "system", v => { S.Language = v; S.Save(); C.ApplyLanguage(); })),
                Row(Ui.Glyph("\uE790", 20, "Text"), L.T("Theme"), null, Combo(new[]
                {
                    Tuple.Create("system", L.T("Use the Windows theme")), Tuple.Create("light", L.T("Light")), Tuple.Create("dark", L.T("Dark")),
                }, S.Theme, v => { S.Theme = v; Changed(); C.ApplyTheme(); })),
                Row(Ui.Glyph("\uE8BE", 20, "Text"), L.T("Refresh rate"), L.T("Automatic: every 10 seconds, every 30 on battery or with Energy Saver. Stops while the PC is locked."),
                    Combo(new[] { Tuple.Create("auto", L.T("Automatic")), Tuple.Create("low", L.T("Maximum saving")) }, S.Refresh, v => { S.Refresh = v; Changed(); })));

            // ------------------------------------------------ informazioni
            Section(L.T("About"));
            statsText = Ui.Text("", 12, "Text2");
            statsText.TextWrapping = TextWrapping.Wrap;
            Group(
                Row(Ui.Glyph("\uE946", 20, "Text"), "AI Meter", null, Button(L.T("Open data folder"), () => Process.Start("explorer.exe", "\"" + Settings.Folder + "\""), false), statsText),
                Row(Ui.Glyph("\uE72E", 20, "Text"), L.T("Privacy"),
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

        private static string Found(bool detected) { return L.T(detected ? "Found on this PC." : "Not found on this PC.") + " "; }

        /// <summary>Ridisegna la pagina dopo un cambio che ne modifica altri interruttori.</summary>
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
        // ------------------------------------------------------------ costruttori stile Impostazioni
        private void Section(string title)
        {
            var t = Ui.Text(title, 14, "Text", FontWeights.SemiBold);
            t.Margin = new Thickness(0, 28, 0, 8);
            page.Children.Add(t);
        }

        private void Hint(string text)
        {
            var t = Ui.Text(text, 12, "Text3");
            t.TextWrapping = TextWrapping.Wrap;
            t.Margin = new Thickness(2, 6, 0, 0);
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

        private static FrameworkElement Row(FrameworkElement icon, string title, string description, FrameworkElement control, FrameworkElement extra = null)
        {
            var g = new Grid { MinHeight = icon != null ? 68 : 56, Margin = new Thickness(16, 10, 16, 10) };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(icon != null ? 44 : 44) });
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
