using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Shell;

namespace AIMeter
{
    /// <summary>
    /// First-launch window that asks for consent before AI Meter docks inside the Windows taskbar,
    /// contacts Anthropic with the Claude Code login or reads API keys saved by other programs.
    /// Nothing is applied until the window closes; closing it keeps the choices as shown.
    /// </summary>
    internal sealed class WelcomeWindow : Window
    {
        /// <summary>Whether the user asked to preview the app with invented data instead of real readings.</summary>
        public bool SampleDataRequested { get; private set; }

        public WelcomeWindow(Settings settings)
        {
            Title = L.T("Welcome to AI Meter");
            Width = 560;
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
            var info = Ui.Text(L.T("AI Meter reads the sessions and limits that your AI apps already save on this PC. Nothing is sent to AI Meter's authors. Choose what it may do; you can change each choice later in Settings."), 13, "Text2");
            info.TextWrapping = TextWrapping.Wrap;
            info.TextTrimming = TextTrimming.None;
            info.Margin = new Thickness(0, 8, 0, 16);
            root.Children.Add(info);

            // The taskbar pill is the main surface, so it is offered switched on; API keys are opt-in.
            var taskbar = SettingsWindow.Toggle(true, v => { });
            var claude = SettingsWindow.Toggle(settings.ClaudeLiveUsage, v => { });
            var keys = SettingsWindow.Toggle(false, v => { });
            var rows = new System.Collections.Generic.List<FrameworkElement>
            {
                SettingsWindow.Row(Ui.Glyph("", 20, "Text"), L.T("Show in the taskbar"),
                    L.T("Places a small indicator inside the Windows taskbar. If you turn this off, AI Meter shows a widget on the desktop instead."), taskbar),
            };
            if (settings.ClaudeEnabled)
                rows.Add(SettingsWindow.Row(Ui.Logo("claude", 20), L.T("Real-time limits"),
                    L.T("Reads your account usage with your Claude Code login (WSL or Windows). The token stays on this PC and is refreshed by the CLI."), claude));
            rows.Add(SettingsWindow.Row(Ui.Glyph("", 20, "Text"), L.T("Use API keys already on this PC"),
                L.T("OpenRouter, DeepSeek, Kimi, Vercel and other keys saved by OpenCode, Pi or Crush, or exported in your shell profile, on Windows and in WSL while it is running. Each key is sent only to its own service."), keys));
            root.Children.Add(SettingsWindow.Card(rows.ToArray()));

            var buttons = new DockPanel { Margin = new Thickness(0, 18, 0, 0), LastChildFill = false };
            var privacy = Link(L.T("Privacy policy"), () => Process.Start("https://github.com/dichiara19/ai-meter#privacy-policy"));
            DockPanel.SetDock(privacy, Dock.Left);
            buttons.Children.Add(privacy);
            var sample = Link(L.T("Preview with sample data"), () => { SampleDataRequested = true; Close(); });
            sample.Margin = new Thickness(16, 0, 0, 0);
            DockPanel.SetDock(sample, Dock.Left);
            buttons.Children.Add(sample);
            var start = new Button { Content = L.T("Start"), IsDefault = true };
            start.SetResourceReference(StyleProperty, "AccentButton");
            start.Click += (o, e) => Close();
            DockPanel.SetDock(start, Dock.Right);
            buttons.Children.Add(start);
            root.Children.Add(buttons);
            Content = root;

            Closed += (o, e) =>
            {
                settings.TaskbarDock = taskbar.IsChecked == true;
                if (!settings.TaskbarDock) settings.DesktopWidget = true;
                settings.ClaudeLiveUsage = claude.IsChecked == true;
                settings.FindKeys = keys.IsChecked == true;
            };
        }

        private static Button Link(string label, Action click)
        {
            var link = new Button { Content = label, Padding = new Thickness(0, 0, 4, 1), FontSize = 13, VerticalAlignment = VerticalAlignment.Center };
            link.SetResourceReference(StyleProperty, "LinkButton");
            link.Click += (o, e) => { try { click(); } catch (Exception) { } };
            return link;
        }
    }
}
