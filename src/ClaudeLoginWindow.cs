using System;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Shell;

namespace AIMeter
{
    /// <summary>
    /// Claude sign-in window: runs "claude auth login" without a console, opens its page in the browser and passes on
    /// the code that the page may show. Claude Code saves the sign-in; this window never reads it.
    /// </summary>
    internal sealed class ClaudeLoginWindow : Window
    {
        private const string InstallPage = "https://code.claude.com/docs/en/setup";

        private readonly Func<ProcessStartInfo> command;
        private readonly TextBlock info, status;
        private readonly StackPanel codeRow;
        private readonly TextBox codeBox;
        private readonly Button primary, send, installLink;
        private Action primaryAction;
        private Process cli;
        private string signInPage;
        private bool closed;
        public event Action SignedIn;

        /// <summary>The command gives the sign-in process to start, or null when Claude Code is not found.</summary>
        public ClaudeLoginWindow(Func<ProcessStartInfo> command)
        {
            this.command = command;
            Title = L.F("Connect {0}", "Claude");
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
            info = Ui.Text("", 13, "Text2");
            info.TextWrapping = TextWrapping.Wrap;
            info.TextTrimming = TextTrimming.None;
            info.Margin = new Thickness(0, 8, 0, 12);
            root.Children.Add(info);
            status = Ui.Text("", 12, "Text3");
            status.TextWrapping = TextWrapping.Wrap;
            status.TextTrimming = TextTrimming.None;
            status.Margin = new Thickness(0, 0, 0, 16);
            root.Children.Add(status);
            installLink = new Button { Content = L.T("How to install Claude Code"), Padding = new Thickness(0, 0, 4, 1), FontSize = 13, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 16), Visibility = Visibility.Collapsed };
            installLink.SetResourceReference(StyleProperty, "LinkButton");
            installLink.Click += (o, e) => { try { Process.Start(InstallPage); } catch (Exception) { } };
            root.Children.Add(installLink);

            // The code the page shows when the browser cannot hand the sign-in back to Claude Code by itself.
            codeRow = new StackPanel { Margin = new Thickness(0, 0, 0, 18), Visibility = Visibility.Collapsed };
            var codeLabel = Ui.Text(L.T("If the page shows a code, paste it here:"), 13, "Text2");
            codeLabel.Margin = new Thickness(0, 0, 0, 6);
            codeRow.Children.Add(codeLabel);
            var codeLine = new DockPanel();
            send = new Button { Content = L.T("Send"), Margin = new Thickness(8, 0, 0, 0), IsEnabled = false };
            send.SetResourceReference(StyleProperty, "StdButton");
            send.Click += (o, e) => SendCode();
            DockPanel.SetDock(send, Dock.Right);
            codeLine.Children.Add(send);
            codeBox = new TextBox { BorderThickness = new Thickness(0), Background = System.Windows.Media.Brushes.Transparent, FontSize = 14, Padding = new Thickness(0) };
            codeBox.SetResourceReference(Control.ForegroundProperty, "Text");
            codeBox.SetResourceReference(TextBox.CaretBrushProperty, "Text");
            codeBox.TextChanged += (o, e) => send.IsEnabled = codeBox.Text.Trim().Length > 0;
            var frame = new Border { CornerRadius = new CornerRadius(4), BorderThickness = new Thickness(1, 1, 1, 2), Padding = new Thickness(10, 6, 10, 6), Child = codeBox };
            frame.SetResourceReference(Border.BackgroundProperty, "Control");
            frame.SetResourceReference(Border.BorderBrushProperty, "ControlStroke");
            codeBox.GotKeyboardFocus += (o, e) => frame.SetResourceReference(Border.BorderBrushProperty, "Accent");
            codeBox.LostKeyboardFocus += (o, e) => frame.SetResourceReference(Border.BorderBrushProperty, "ControlStroke");
            codeLine.Children.Add(frame);
            codeRow.Children.Add(codeLine);
            root.Children.Add(codeRow);

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            primary = new Button { Margin = new Thickness(0, 0, 8, 0) };
            primary.SetResourceReference(StyleProperty, "AccentButton");
            primary.Click += (o, e) => { if (primaryAction != null) primaryAction(); };
            var cancel = new Button { Content = L.T("Cancel") };
            cancel.SetResourceReference(StyleProperty, "StdButton");
            cancel.Click += (o, e) => Close();
            buttons.Children.Add(primary);
            buttons.Children.Add(cancel);
            root.Children.Add(buttons);
            Content = root;

            Closed += (o, e) => { closed = true; Stop(); };
            Loaded += (o, e) => Start();
        }

        /// <summary>Start the sign-in, or explain what to do when Claude Code is not found.</summary>
        private void Start()
        {
            Stop();
            signInPage = null;
            codeBox.Text = "";
            codeRow.Visibility = Visibility.Collapsed;
            var psi = command();
            installLink.Visibility = psi == null ? Visibility.Visible : Visibility.Collapsed;
            if (psi == null)
            {
                info.Text = L.T("AI Meter reads real-time limits through the Claude Code sign-in. If you use Claude Code only in the Claude app, open its Code tab once so that the app downloads it, or install Claude Code; then try again.");
                status.Text = L.T("Claude Code was not found on this PC.");
                Primary(L.T("Try again"), Start);
                return;
            }

            info.Text = L.T("Sign in to your Claude account on the page that opens in the browser. AI Meter uses the Claude Code sign-in only to read your limits; it stays on this PC.");
            status.Text = L.T("Opening the sign-in page…");
            Primary(L.T("Open the sign-in page"), OpenPage);
            primary.IsEnabled = false;

            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.WindowStyle = ProcessWindowStyle.Hidden;
            psi.RedirectStandardInput = psi.RedirectStandardOutput = psi.RedirectStandardError = true;
            psi.StandardOutputEncoding = psi.StandardErrorEncoding = new UTF8Encoding(false);
            var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
            DataReceivedEventHandler read = (o, e) => { if (e.Data != null) Dispatcher.BeginInvoke(new Action(() => Output(p, e.Data))); };
            p.OutputDataReceived += read;
            p.ErrorDataReceived += read;
            p.Exited += (o, e) => Dispatcher.BeginInvoke(new Action(() => Finished(p)));
            try { p.Start(); }
            catch (System.ComponentModel.Win32Exception)
            {
                status.Text = L.T("Sign-in did not complete. Try again.");
                Primary(L.T("Try again"), Start);
                return;
            }
            cli = p;
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
        }

        /// <summary>Claude Code prints the address of the page that shows a code; it opens its own page by itself.</summary>
        private void Output(Process p, string line)
        {
            if (p != cli || signInPage != null) return;
            var m = Regex.Match(line, @"https://\S+");
            if (!m.Success) return;
            signInPage = m.Value;
            primary.IsEnabled = true;
            codeRow.Visibility = Visibility.Visible;
            status.Text = L.T("Waiting for you to sign in in the browser…");
        }

        private void OpenPage()
        {
            if (signInPage == null) return;
            try { Process.Start(signInPage); }
            catch (Exception) { }
        }

        private void SendCode()
        {
            if (cli == null || cli.HasExited) return;
            try
            {
                cli.StandardInput.WriteLine(codeBox.Text.Trim());
                cli.StandardInput.Flush();
            }
            catch (System.IO.IOException) { return; }
            send.IsEnabled = false;
            status.Text = L.T("Checking the code…");
        }

        private async void Finished(Process p)
        {
            if (p != cli || closed) return;
            int code = p.ExitCode;
            cli = null;
            p.Dispose();
            if (code == 0)
            {
                status.Text = L.T("Connected.");
                codeRow.Visibility = Visibility.Collapsed;
                primary.IsEnabled = false;
                if (SignedIn != null) SignedIn();
                await System.Threading.Tasks.Task.Delay(800);
                Close();
                return;
            }
            status.Text = L.T("Sign-in did not complete. Try again.");
            codeRow.Visibility = Visibility.Collapsed;
            Primary(L.T("Try again"), Start);
        }

        /// <summary>The main button changes with the step: open the page again, or try again.</summary>
        private void Primary(string text, Action action)
        {
            primaryAction = action;
            primary.Content = text;
            primary.IsEnabled = true;
        }

        /// <summary>End a sign-in still in progress, for instance when the window is closed.</summary>
        private void Stop()
        {
            var p = cli;
            cli = null;
            if (p == null) return;
            try { if (!p.HasExited) p.Kill(); }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception) { }
            p.Dispose();
        }
    }
}
