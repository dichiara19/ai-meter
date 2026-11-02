using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;

namespace AIMeter
{
    internal partial class SettingsWindow
    {
        // ------------------------------------------------------------ AI apps connected through MCP
        private void BuildApps()
        {
            Header(L.T("AI apps"));
            Hint(L.T("Connected apps and coding agents can ask AI Meter, through MCP, how much room your limits have, which sessions are running, which models are free and what you spent today. They only read: AI Meter never shares keys or tokens."));

            var clients = McpClients.Known(Probe.Home, Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), Probe.WslHomeUp)
                .Where(c => c.Installed).ToList();
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
                    Button(L.T("Copy configuration"), CopyMcpSnippet, false)),
                Row(Ui.Glyph("", 20, "Text"), L.T("Share session titles"), L.T("Titles often repeat your first request. When off, apps see only the agent, status, model and provider of each session."),
                    Toggle(S.McpTitles, v => { S.McpTitles = v; S.Save(); })));
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

        private void CopyMcpSnippet()
        {
            string failure = null;
            try { Clipboard.SetText(McpClients.Snippet(McpRelay.Write())); }
            catch (IOException ex) { failure = ex.Message; }
            catch (UnauthorizedAccessException ex) { failure = ex.Message; }
            catch (System.Runtime.InteropServices.ExternalException ex) { failure = ex.Message; }
            if (failure != null) MessageBox.Show(this, failure, "AI Meter", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
