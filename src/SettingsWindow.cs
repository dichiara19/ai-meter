using System;
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
        private TimeSpan lastCpu;
        private DateTime lastCpuAt;

        public SettingsWindow(Controller c)
        {
            C = c;
            Title = "Impostazioni di AI Meter";
            Width = 760; Height = 720; MinWidth = 560; MinHeight = 480;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            FontFamily = Theme.Font;
            UseLayoutRounding = true;
            Icon = Controller.AppIconImage();
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
            var tt = Ui.Text("Impostazioni di AI Meter", 12, "Text");
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
            var h = Ui.Text("Impostazioni", 28, "Text", FontWeights.SemiBold);
            h.FontFamily = Theme.DisplayFont;
            h.Margin = new Thickness(0, 8, 0, 4);
            page.Children.Add(h);
            var sub = Ui.Text("Scegli quali servizi AI monitorare e dove vedere sessioni e limiti.", 14, "Text2");
            sub.TextWrapping = TextWrapping.Wrap;
            page.Children.Add(sub);

            // ------------------------------------------------ servizi collegati
            Section("Servizi collegati");
            var claude = C.Last == null ? null : C.Last.Providers.FirstOrDefault(p => p.Id == "claude");
            Group(
                Row(Ui.Logo("claude", 28), "Claude", ClaudeStatus(claude), Toggle(S.ClaudeEnabled, v => { S.ClaudeEnabled = v; Changed(); })),
                Row(null, "Limiti in tempo reale",
                    "Legge l'utilizzo del tuo account con l'accesso di Claude Code (WSL o Windows). Il token resta sul PC e viene rinnovato dalla CLI.",
                    Pair(Button("Accedi…", () => C.LoginClaude(), false), Toggle(S.ClaudeLiveUsage, v => { S.ClaudeLiveUsage = v; Changed(); C.RefreshNow(); }))),
                Row(null, "Sessioni Claude Code", "Desktop, CLI (anche in WSL) e VS Code, con titolo, modello e stato.", Toggle(S.ClaudeShowCode, v => { S.ClaudeShowCode = v; Changed(); })),
                Row(null, "Attività Cowork", "Le attività Cowork in corso negli ultimi 30 minuti.", Toggle(S.ClaudeShowCowork, v => { S.ClaudeShowCowork = v; Changed(); })),
                Row(null, "App Claude aperta", "Le singole chat non lasciano tracce sul PC: si vede solo se l'app è aperta.", Toggle(S.ClaudeShowChat, v => { S.ClaudeShowChat = v; Changed(); })));

            Group(
                Row(Ui.Logo("openai", 28), "ChatGPT · Codex", "Limiti del piano ChatGPT letti dai log locali di Codex: nessun accesso alla rete.",
                    Toggle(S.OpenAIEnabled, v => { S.OpenAIEnabled = v; Changed(); })),
                Row(null, "App ChatGPT aperta", "ChatGPT non espone i limiti della chat: si vede solo se l'app è aperta.", Toggle(S.OpenAIShowChat, v => { S.OpenAIShowChat = v; Changed(); })));

            var wslRow = Row(null, "Distribuzione WSL", "Dove cercare Claude Code e Codex installati in Linux. Il widget non avvia mai WSL: lo legge solo se è già acceso.",
                Combo(new[] { S.WslDistro }.Concat(WslDistros()).Where(x => !string.IsNullOrEmpty(x)).Distinct().Select(x => Tuple.Create(x, x)).ToArray(), S.WslDistro,
                      v => { S.WslDistro = v; Changed(); }));
            Group(wslRow);

            // ------------------------------------------------ dove vederlo
            Section("Dove vederlo");
            Group(
                Row(Ui.Glyph("", 20, "Text"), "Widget sul desktop", "Resta sotto le finestre, come parte dello sfondo. Trascinalo dall'intestazione.",
                    Pair(Button("Ripristina posizione", () => C.ResetDesktopPosition(), false), Toggle(S.DesktopWidget, v => { S.DesktopWidget = v; Changed(); }))));
            Group(
                Row(Ui.Glyph("", 20, "Text"), "Sovrimpressione compatta", "Una pillola minuscola sempre in primo piano. Passaci sopra per vedere i dettagli.",
                    Toggle(S.Overlay, v => { S.Overlay = v; Changed(); })),
                Row(null, "Posizione", null, Combo(new[]
                {
                    Tuple.Create("tr", "In alto a destra"), Tuple.Create("tl", "In alto a sinistra"),
                    Tuple.Create("br", "In basso a destra"), Tuple.Create("bl", "In basso a sinistra"),
                }, S.OverlayCorner, v => { S.OverlayCorner = v; Changed(); })),
                Row(null, "Valori mostrati", "Percentuale rimasta del limite scelto.", Combo(new[]
                {
                    Tuple.Create("5h", "Limite di 5 ore"), Tuple.Create("week", "Limite settimanale"), Tuple.Create("both", "Entrambi"),
                }, S.OverlayMetric, v => { S.OverlayMetric = v; Changed(); })),
                Row(null, "Nascondi a schermo intero", "Durante giochi, video e presentazioni la pillola sparisce da sola.",
                    Toggle(S.OverlayHideFullscreen, v => { S.OverlayHideFullscreen = v; Changed(); })));
            Hint("Il pannello completo è sempre a un clic dall'icona di AI Meter nell'area di notifica.");

            // ------------------------------------------------ generale
            Section("Generale");
            Group(
                Row(Ui.Glyph("", 20, "Text"), "Avvia con Windows", "AI Meter parte all'accesso, in silenzio, dopo che il desktop è pronto.",
                    Toggle(Settings.StartsWithWindows, v => { Settings.StartsWithWindows = v; })),
                Row(Ui.Glyph("", 20, "Text"), "Tema", null, Combo(new[]
                {
                    Tuple.Create("system", "Usa il tema di Windows"), Tuple.Create("light", "Chiaro"), Tuple.Create("dark", "Scuro"),
                }, S.Theme, v => { S.Theme = v; Changed(); C.ApplyTheme(); })),
                Row(Ui.Glyph("", 20, "Text"), "Frequenza di aggiornamento", "Automatica: ogni 10 secondi, ogni 30 a batteria o con il Risparmio energia. Si ferma con il PC bloccato.",
                    Combo(new[] { Tuple.Create("auto", "Automatica"), Tuple.Create("low", "Risparmio massimo") }, S.Refresh, v => { S.Refresh = v; Changed(); })));

            // ------------------------------------------------ informazioni
            Section("Informazioni");
            statsText = Ui.Text("", 12, "Text2");
            statsText.TextWrapping = TextWrapping.Wrap;
            Group(
                Row(Ui.Glyph("", 20, "Text"), "AI Meter", null, Button("Apri cartella dati", () => Process.Start("explorer.exe", "\"" + Settings.Folder + "\""), false), statsText),
                Row(Ui.Glyph("", 20, "Text"), "Privacy",
                    "Sessioni e limiti vengono letti dai file locali. L'unico dato che esce dal PC è la richiesta dei limiti Claude in tempo reale ad Anthropic, con il tuo accesso di Claude Code: si spegne con l'interruttore \"Limiti in tempo reale\".",
                    Button("Codice sorgente", () => Process.Start("https://github.com/dichiara19/ai-meter#privacy-policy"), false)));
            UpdateStats();
        }

        private string ClaudeStatus(ProviderState p)
        {
            if (!S.ClaudeEnabled) return "Disattivato";
            if (p == null) return "In attesa del primo aggiornamento";
            string plan = p.Plan != null ? " · piano " + p.Plan.ToUpperInvariant() : "";
            if (p.Usage != null && p.Usage.Source == "live") return "Collegato" + plan + " · limiti in tempo reale";
            if (p.NeedsLogin) return "Accesso necessario per i limiti in tempo reale";
            if (!string.IsNullOrEmpty(p.Notice)) return p.Notice;
            return "Limiti dall'ultimo dato disponibile" + plan;
        }

        private void Changed() { S.Save(); C.ApplySettings(); }

        private void UpdateStats()
        {
            if (statsText == null) return;
            var proc = Process.GetCurrentProcess();
            proc.Refresh();
            var now = DateTime.Now;
            var cpu = proc.TotalProcessorTime;
            double pct = (cpu - lastCpu).TotalMilliseconds / Math.Max(1, (now - lastCpuAt).TotalMilliseconds) / Environment.ProcessorCount * 100;
            lastCpu = cpu; lastCpuAt = now;
            var it = new CultureInfo("it-IT");
            statsText.Text = string.Format(it,
                "Versione {5} · memoria in uso {0:0} MB (privata {1:0} MB) · CPU {2:0.0}% · aggiornamento ogni {3} s{4}",
                proc.WorkingSet64 / 1048576.0, proc.PrivateMemorySize64 / 1048576.0, pct, C.CurrentInterval,
                C.SavingPower ? " (risparmio energia)" : "", Controller.Version);
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
