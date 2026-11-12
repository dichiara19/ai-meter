using System;
using System.Collections.Generic;
using System.Globalization;
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

            BuildDelegationModels();
        }

        /// <summary>
        /// The user's choice for each free model with tool support, which comes first in the order of delegation
        /// candidates (Candidates.Ordered). A choice for a model no longer listed stays visible, so it can be undone.
        /// </summary>
        private void BuildDelegationModels()
        {
            Header(L.T("Delegated tasks"));
            Hint(L.T("Coding agents that delegate tasks through AI Meter try preferred models first and avoided ones last. The others follow how they did on this PC, then their size and context."));
            var rows = new List<FrameworkElement>();
            var listed = new HashSet<string>();
            var snap = C.Last;
            if (snap != null)
                foreach (var p in snap.Providers.Where(x => x.Catalogue != null).OrderBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase))
                    foreach (var m in p.Catalogue.Models.Where(x => x.Tools && (x.Free == null || x.Free.Confirmed)).OrderBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase))
                    {
                        string key = Candidates.PreferenceKey(p.Id, m.Id);
                        if (!listed.Add(key)) continue;
                        var facts = new List<string> { p.Name };
                        var size = ModelSizes.Of(m);
                        if (size != null) facts.Add(ModelSizes.Format(size.Parameters));
                        if (m.Context > 0) facts.Add(L.F("{0} context", FreeModels.Context(m.Context)));
                        rows.Add(Row(null, m.Name, string.Join(" · ", facts), PreferenceCombo(key)));
                    }
            foreach (var key in S.DelegationModels.Keys.Where(k => !listed.Contains(k)).OrderBy(k => k, StringComparer.Ordinal).ToList())
                rows.Add(Row(null, key.Substring(key.IndexOf('|') + 1), L.T("Not in the current model lists"), PreferenceCombo(key)));
            if (rows.Count == 0)
                rows.Add(Row(null, L.T("No free models with tool support yet"), L.T("They appear here once a service with free models is on and its model list has been read."), null));
            Group(rows.ToArray());
            BuildDelegationPlans();
        }

        /// <summary>
        /// Whether delegated tasks may draw on each subscription plan a supported agent can run on (ADR 0003), off by
        /// default, and the share of each window the user keeps; the reserves show only while the plan is on.
        /// </summary>
        private void BuildDelegationPlans()
        {
            var kinds = Agents.All.Where(k => k.Plan != null && k.Dispatch != null).ToList();
            if (kinds.Count == 0) return;
            Hint(L.T("Delegated tasks can also run in Claude Code or Codex on your own plan, never on paid usage. A plan is used only if you turn it on here and an AI app asks for it; it is skipped while extra usage or credits are on, and below what you keep of each window."));
            var rows = new List<FrameworkElement>();
            var snap = C.Last;
            foreach (var kind in kinds)
            {
                string service = kind.Plan.Service;
                var provider = snap == null ? null : snap.Providers.FirstOrDefault(p => p.Id == service);
                string plan = provider != null ? provider.Name : service == "openai" ? "ChatGPT" : service == "claude" ? "Claude" : service;
                bool on;
                S.DelegationPlans.TryGetValue(service, out on);
                rows.Add(Row(null, L.F("{0} plan", plan), L.F("Tasks run in {0} with its own sign-in.", kind.Name), Toggle(on, v =>
                {
                    var next = new Dictionary<string, bool>(S.DelegationPlans);
                    next[service] = v;
                    S.DelegationPlans = next;
                    S.Save();
                    Rebuild();
                })));
                if (!on) continue;
                rows.Add(Row(null, L.T("Reserve in the 5-hour window"), L.T("Delegated tasks leave this share of the window for you."), ReserveCombo(service, UsageWindow.ShortFiveHours)));
                rows.Add(Row(null, L.T("Reserve in the weekly window"), L.T("Delegated tasks leave this share of the window for you."), ReserveCombo(service, UsageWindow.ShortWeek)));
            }
            Group(rows.ToArray());
        }

        /// <summary>The share of a plan window kept for the user. The settings get a new dictionary, as MCP threads read the current one.</summary>
        private ComboBox ReserveCombo(string service, string window)
        {
            int current = Candidates.ReserveOf(S.DelegationReserves, service, window);
            var values = new[] { 10, 20, 25, 30, 40, 50, 60, 75, 90 };
            if (!values.Contains(current)) values = values.Concat(new[] { current }).OrderBy(v => v).ToArray();
            return Combo(values.Select(v => Tuple.Create(v.ToString(CultureInfo.InvariantCulture), v.ToString(L.Culture) + "%")).ToArray(),
                current.ToString(CultureInfo.InvariantCulture), v =>
                {
                    var next = new Dictionary<string, int>(S.DelegationReserves);
                    next[Candidates.ReserveKey(service, window)] = int.Parse(v, CultureInfo.InvariantCulture);
                    S.DelegationReserves = next;
                    S.Save();
                });
        }

        /// <summary>Default, preferred or avoided. The settings get a new dictionary, as MCP threads read the current one.</summary>
        private ComboBox PreferenceCombo(string key)
        {
            string current;
            S.DelegationModels.TryGetValue(key, out current);
            return Combo(new[]
            {
                Tuple.Create("", L.T("Default")), Tuple.Create(Candidates.Preferred, L.T("Preferred")), Tuple.Create(Candidates.Avoided, L.T("Avoided")),
            }, current ?? "", v =>
            {
                var next = new Dictionary<string, string>(S.DelegationModels);
                if (v == "") next.Remove(key); else next[key] = v;
                S.DelegationModels = next;
                S.Save();
            });
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
