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
        // ------------------------------------------------------------ MCP: connected apps and delegation
        //
        // One overview ("apps") that links to a page per purpose, as Services links to each service:
        //   apps:clients  the apps that may read AI Meter's data, and how to connect one by hand;
        //   apps:agent    the coding agent that runs delegated tasks (SettingsWindow.ManagedPi.cs);
        //   apps:models   which free models delegated tasks try first;
        //   apps:plans    whether delegated tasks may draw on a subscription plan.

        /// <summary>
        /// The clients the page lists, installed or not. tools\MediaRenderer.cs points it at fixture folders, so the
        /// README images show sample apps instead of the ones on the machine that renders them.
        /// </summary>
        internal static Func<List<McpClients.Client>> KnownApps =
            () => McpClients.Known(Probe.Home, Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), Probe.WslHomeUp);

        /// <summary>Build the overview, or the page named after "apps:"; a page this build does not offer falls back to the overview.</summary>
        private void BuildMcp(string subpage)
        {
            if (subpage == "clients") BuildMcpClients();
            else if (subpage == "agent" && DelegationAgentOffered) BuildDelegationAgent();
            else if (subpage == "models") BuildDelegationModels();
            else if (subpage == "plans" && PlanAgents.Count > 0) BuildDelegationPlans();
            else BuildMcpOverview();
        }

        /// <summary>The agents that can run a delegated task on the user's subscription plan (ADR 0003).</summary>
        private static List<RuntimeKind> PlanAgents { get { return Agents.All.Where(k => k.Plan != null && k.Dispatch != null).ToList(); } }

        private void BuildMcpOverview()
        {
            Header("MCP");
            Hint(L.T("Through MCP, the AI apps and coding agents you connect can read your limits, running sessions, free models and today's spending from AI Meter. Access is read-only: keys and tokens are never shared."));

            Section(L.T("Connection"));
            Group(
                Link(Ui.Glyph("", 20, "Text"), L.T("Connected apps"), ConnectedAppsStatus(), null, () => Open("apps:clients")),
                Row(Ui.Glyph("", 20, "Text"), L.T("Share session titles"), L.T("Titles often repeat your first request. When off, apps see only each session's agent, status, model and provider."),
                    Toggle(S.McpTitles, v => { S.McpTitles = v; S.Save(); })));

            Section(L.T("Delegation"));
            Hint(L.T("A connected coding agent can hand tasks over to free models. AI Meter chooses the model and keeps each task within your limits."));
            var rows = new List<FrameworkElement>();
            if (DelegationAgentOffered)
                rows.Add(Link(Ui.Glyph("", 20, "Text"), L.T("Delegation agent"), DelegationAgentStatus(), null, () => Open("apps:agent")));
            rows.Add(Link(Ui.Glyph("", 20, "Text"), L.T("Model order"), ModelOrderStatus(), null, () => Open("apps:models")));
            if (PlanAgents.Count > 0)
                rows.Add(Link(Ui.Glyph("", 20, "Text"), L.T("Subscription plans"), PlansStatus(), null, () => Open("apps:plans")));
            Group(rows.ToArray());
        }

        /// <summary>The installed apps with whether each is connected; an app whose file cannot be read counts as not connected.</summary>
        private static List<Tuple<McpClients.Client, bool>> InstalledApps()
        {
            return KnownApps().Where(c => c.Installed).Select(c =>
            {
                bool connected = false;
                try { connected = McpClients.IsConnected(c); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
                return Tuple.Create(c, connected);
            }).ToList();
        }

        private static string AppName(McpClients.Client c) { return c.Wsl ? c.Name + " · WSL" : c.Name; }

        private static string ConnectedAppsStatus()
        {
            var connected = InstalledApps().Where(a => a.Item2).Select(a => AppName(a.Item1)).ToList();
            return connected.Count == 0 ? L.T("No app connected") : string.Join(", ", connected);
        }

        private string ModelOrderStatus()
        {
            int preferred = S.DelegationModels.Values.Count(v => v == Candidates.Preferred), avoided = S.DelegationModels.Values.Count(v => v == Candidates.Avoided);
            return preferred + avoided == 0 ? L.T("Default order") : L.F("{0} preferred, {1} avoided", preferred, avoided);
        }

        private string PlansStatus()
        {
            var on = PlanAgents.Select(k => k.Plan.Service).Distinct().Where(s => { bool v; return S.DelegationPlans.TryGetValue(s, out v) && v; }).Select(PlanName).ToList();
            return on.Count == 0 ? L.T("Off") : string.Join(", ", on);
        }

        private string PlanName(string service)
        {
            var provider = LastState(service);
            return provider != null ? provider.Name : service == "openai" ? "ChatGPT" : service == "claude" ? "Claude" : service;
        }

        // ------------------------------------------------------------ apps:clients
        private void BuildMcpClients()
        {
            Breadcrumb("MCP", "apps", L.T("Connected apps"));
            Hint(L.T("Choose the apps that may read AI Meter's data. Restart an app after connecting it. Apps in WSL are listed while WSL is running."));
            var rows = new List<FrameworkElement>();
            foreach (var app in InstalledApps())
            {
                var c = app.Item1;
                string detail = c.ConfigPath + (c.Note == null ? "" : "\n" + L.T(c.Note));
                rows.Add(Row(Ui.Glyph(c.Wsl ? "" : "", 20, "Text"), AppName(c), detail, Toggle(app.Item2, v => ConnectApp(c, v))));
            }
            if (rows.Count == 0) rows.Add(Row(null, L.T("No supported app found"), L.T("Connect an app manually with the configuration below."), null));
            Group(rows.ToArray());

            Section(L.T("Manual setup"));
            Group(Row(Ui.Glyph("", 20, "Text"), L.T("MCP configuration"), L.T("For an app that is not listed and can start a local MCP server."),
                CopyButton(L.T("Copy configuration"), () => McpClients.Snippet(McpRelay.Write()))));

            // Agents can also add AI Meter themselves; the buttons copy each agent's own "mcp add" command.
            Section(L.T("Command line"));
            Hint(L.T("Copy the command for your CLI, run it in PowerShell or in a WSL shell, then restart the CLI."));
            Group(McpClients.CommandLineClients.Select(cli =>
            {
                string id = cli[0];
                return Row(null, cli[1], null,
                    Pair(CopyButton("Windows", () => McpClients.Command(id, McpRelay.Write(), false)),
                         CopyButton("WSL", () => McpClients.Command(id, McpRelay.Write(), true))));
            }).ToArray());
        }

        // ------------------------------------------------------------ apps:models

        /// <summary>
        /// The user's choice for each free model with tool support, which comes first in the order of delegation
        /// candidates (Candidates.Ordered). A choice for a model no longer listed stays visible, so it can be undone.
        /// </summary>
        private void BuildDelegationModels()
        {
            Breadcrumb("MCP", "apps", L.T("Model order"));
            Hint(L.T("Delegated tasks try preferred models first and avoided models last. The others are ordered by their results on this PC, then by size and context."));
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
                rows.Add(Row(null, key.Substring(key.IndexOf('|') + 1), L.T("No longer in the model lists"), PreferenceCombo(key)));
            if (rows.Count == 0)
                rows.Add(Row(null, L.T("No free model with tool support"), L.T("Models appear once a service with free models is on and its model list has been read."), null));
            Group(rows.ToArray());
        }

        // ------------------------------------------------------------ apps:plans

        /// <summary>
        /// Whether delegated tasks may draw on each subscription plan a supported agent can run on (ADR 0003), off by
        /// default, and the share of each window the user keeps; the reserves show only while the plan is on.
        /// </summary>
        private void BuildDelegationPlans()
        {
            Breadcrumb("MCP", "apps", L.T("Subscription plans"));
            Hint(L.T("Delegated tasks can also run in Claude Code or Codex on your subscription, never on pay-as-you-go usage. A plan is used only when it is on here and the app requests it. It is skipped while extra usage or credits are active, and once a window reaches the reserve set below."));
            var rows = new List<FrameworkElement>();
            foreach (var kind in PlanAgents)
            {
                string service = kind.Plan.Service;
                bool on;
                S.DelegationPlans.TryGetValue(service, out on);
                rows.Add(Row(null, L.F("{0} plan", PlanName(service)), L.F("Tasks run in {0}, signed in with your account.", kind.Name), Toggle(on, v =>
                {
                    var next = new Dictionary<string, bool>(S.DelegationPlans);
                    next[service] = v;
                    S.DelegationPlans = next;
                    S.Save();
                    Rebuild();
                })));
                if (!on) continue;
                rows.Add(Row(null, L.T("5-hour reserve"), L.T("Share of the window kept for your own use."), ReserveCombo(service, UsageWindow.ShortFiveHours)));
                rows.Add(Row(null, L.T("Weekly reserve"), L.T("Share of the window kept for your own use."), ReserveCombo(service, UsageWindow.ShortWeek)));
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
