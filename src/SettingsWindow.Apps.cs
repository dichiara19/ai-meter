using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace AIMeter
{
    internal partial class SettingsWindow
    {
        // ------------------------------------------------------------ AI apps connected through MCP

        /// <summary>
        /// The clients the page lists, installed or not. tools\MediaRenderer.cs points it at fixture folders, so the
        /// README images show sample apps instead of the ones on the machine that renders them.
        /// </summary>
        internal static Func<List<McpClients.Client>> KnownApps =
            () => McpClients.Known(Probe.Home, Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), Probe.WslHomeUp);

        private void BuildApps()
        {
            Header(L.T("AI apps"));
            Hint(L.T("Connected apps and coding agents can ask AI Meter, through MCP, how much room your limits have, which sessions are running, which models are free and what you spent today. They only read: AI Meter never shares keys or tokens."));

            var clients = KnownApps().Where(c => c.Installed).ToList();
            var rows = new List<FrameworkElement>();
            foreach (var client in clients)
            {
                var c = client;
                bool connected = false;
                try { connected = McpClients.IsConnected(c); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
                string detail = c.ConfigPath + (c.Note == null ? "" : "\n" + L.T(c.Note));
                rows.Add(Row(Ui.Glyph(c.Wsl ? "" : "", 20, "Text"), c.Wsl ? c.Name + " · WSL" : c.Name, detail,
                    Toggle(connected, v => ConnectApp(c, v))));
            }
            if (rows.Count == 0) rows.Add(Row(null, L.T("No supported apps found"), L.T("Use the configuration below in any app that can start a local MCP server."), null));
            Group(rows.ToArray());
            Hint(L.T("Restart an app after connecting it, so it loads AI Meter's tools. Apps in WSL are listed while WSL is running."));

            Group(
                Row(Ui.Glyph("", 20, "Text"), L.T("Other apps"), L.T("Copy the configuration for apps that are not listed, such as the ChatGPT app when it accepts local servers."),
                    CopyButton(L.T("Copy configuration"), () => McpClients.Snippet(McpRelay.Write()))),
                Row(Ui.Glyph("", 20, "Text"), L.T("Share session titles"), L.T("Titles often repeat your first request. When off, apps see only the agent, status, model and provider of each session."),
                    Toggle(S.McpTitles, v => { S.McpTitles = v; S.Save(); })));

            // Agents can also add AI Meter themselves; the buttons copy each agent's own "mcp add" command.
            Header(L.T("From the command line"));
            Hint(L.T("Prefer a terminal? Copy the command for your CLI, paste it into PowerShell on Windows or into a WSL shell, then restart the CLI."));
            Group(McpClients.CommandLineClients.Select(cli =>
            {
                string id = cli[0];
                return Row(null, cli[1], null,
                    Pair(CopyButton("Windows", () => McpClients.Command(id, McpRelay.Write(), false)),
                         CopyButton("WSL", () => McpClients.Command(id, McpRelay.Write(), true))));
            }).ToArray());
        }

        private void ConnectApp(McpClients.Client c, bool on)
        {
            try
            {
                if (on) McpClients.Connect(c, McpRelay.Write());
                else McpClients.Disconnect(c);
            }
            catch (IOException ex) { AppNotChanged(c, ex.Message); }
            catch (UnauthorizedAccessException ex) { AppNotChanged(c, ex.Message); }
            catch (McpClients.ConfigException ex) { AppNotChanged(c, ex.Message); }
            Rebuild();
        }

        /// <summary>Explain why a client's file was left as it was, with the entry to add by hand.</summary>
        private void AppNotChanged(McpClients.Client c, string reason)
        {
            string snippet = "";
            try { snippet = "\n\n" + McpClients.Snippet(McpRelay.Write()); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            MessageBox.Show(this, L.F("{0} was not changed.", c.Name) + "\n\n" + reason + snippet, "AI Meter", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        private static readonly TimeSpan CopiedDuration = TimeSpan.FromSeconds(2);

        /// <summary>A button that copies text naming the relay and confirms the copy on itself.</summary>
        private Button CopyButton(string label, Func<string> text)
        {
            Button button = null;
            button = Button(label, () => { if (CopyMcpText(text)) ShowCopied(button, CopiedDuration); }, false);
            ReserveCopiedWidth(button);
            return button;
        }

        /// <summary>
        /// Copy text that names the relay; writing the relay first makes sure the pasted command works.
        /// Returns false, after explaining why, when the relay or the clipboard could not be written.
        /// </summary>
        private bool CopyMcpText(Func<string> text)
        {
            string failure = null;
            try { Clipboard.SetText(text()); }
            catch (IOException ex) { failure = ex.Message; }
            catch (UnauthorizedAccessException ex) { failure = ex.Message; }
            catch (System.Runtime.InteropServices.ExternalException ex) { failure = ex.Message; }
            if (failure != null) MessageBox.Show(this, failure, "AI Meter", MessageBoxButton.OK, MessageBoxImage.Warning);
            return failure == null;
        }
    }
}
