using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shell;
using Microsoft.Win32;

namespace AIMeter
{
    internal partial class Ui
    {
        /// <summary>Check whether a service card row is hidden in Settings.</summary>
        private static bool RowHidden(string providerId, string key) { return Settings.Current != null && Settings.Current.RowHidden(providerId, key); }

        /// <summary>Build a limit row with its label and reset, remaining value, and optional bar.</summary>
        public static FrameworkElement UsageRow(UsageWindow w, Usage u, string brand)
        {
            var root = new StackPanel { Margin = new Thickness(0, 6, 0, 0) };
            var top = new DockPanel { LastChildFill = true };
            // Display remaining money for monetary limits.
            // Cap model quota by the enclosing limit's remaining room.
            var parent = u == null ? null : u.ParentOf(w);
            bool capped = parent != null && parent.Remaining < w.Remaining;
            double room = capped ? parent.Remaining : w.Remaining;
            string right = w.RightText != null ? L.T(w.RightText) : (w.Spent != null ? w.Spent.Format(Math.Max(0, w.Spent.Limit - w.Spent.Used)) : Math.Round(room).ToString(It) + "%");
            if (w.CachedAt.HasValue) right = L.F("{0} (cached)", right);
            var pct = Text(right, 13, "Text", FontWeights.SemiBold);
            pct.Margin = new Thickness(8, 0, 0, 0);
            DockPanel.SetDock(pct, Dock.Right);
            top.Children.Add(pct);
            var left = new TextBlock { FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Bottom };
            var lbl = new System.Windows.Documents.Run(LabelOf(w));
            lbl.SetResourceReference(TextElement_Foreground, "Text2");
            left.Inlines.Add(lbl);
            string reset = ResetText(w, u);
            if (reset.Length > 0)
            {
                var r = new System.Windows.Documents.Run("  ·  " + reset);
                r.SetResourceReference(TextElement_Foreground, "Text3");
                left.Inlines.Add(r);
            }

            top.Children.Add(left);
            root.Children.Add(top);
            if (w.NoBar || w.CachedAt.HasValue)
            {
                root.ToolTip = LabelOf(w) + ": " + right + (w.Detail != null ? "\n" + w.Detail() : "")
                    + (w.CachedAt.HasValue ? "\n" + L.F("Last updated {0}", w.CachedAt.Value.ToString("g", L.Culture)) : "");
                return root;
            }
            // Draw effective room solid and the model's own remaining quota behind it.
            var bar = Bar(room, capped ? LevelBrush(room, brand) : LevelBrush(w, brand), 4, capped ? w.Remaining : -1);
            bar.Margin = new Thickness(0, 5, 0, 0);
            root.Children.Add(bar);
            if (capped)
            {
                var c = Text(L.F("{0:0}% of its own limit, but {1} has {2:0}% left", w.Remaining, LabelOf(parent), parent.Remaining), 11.5, "Text3");
                c.Margin = new Thickness(0, 4, 0, 0);
                c.TextWrapping = TextWrapping.Wrap;
                root.Children.Add(c);
            }
            // Show a forecast when exhaustion precedes the reset.
            if (w.RunsOutAt.HasValue)
            {
                var f = Text(L.F("At this pace it runs out {0}.", Alerts.When(w.RunsOutAt.Value)).TrimEnd('.'), 11.5, "Caution");
                f.Margin = new Thickness(0, 4, 0, 0);
                root.Children.Add(f);
            }
            string when = "dddd d MMMM, HH:mm";
            root.ToolTip = (w.Spent != null
                    ? L.F("{0}: {1} used of {2}", LabelOf(w), w.Spent.Format(w.Spent.Used), w.Spent.Format(w.Spent.Limit))
                    : L.F("{0}: {1:0}% used, {2:0}% left", LabelOf(w), w.Used, w.Remaining))
                + (w.Detail != null ? "\n" + w.Detail() : "")
                + (w.RunsOutAt.HasValue ? "\n" + L.F("At this pace it runs out {0}.", Alerts.When(w.RunsOutAt.Value)) : "")
                + (w.StartedAt.HasValue ? "\n" + L.F("Started {0}", w.StartedAt.Value.ToString(when, It)) : "")
                + (w.ResetsAt.HasValue ? "\n" + L.F(w.Spent != null ? "Renews {0}" : "Resets {0}", w.ResetsAt.Value.ToString(when, It)) : "");
            return root;
        }

        private static readonly DependencyProperty TextElement_Foreground = System.Windows.Documents.TextElement.ForegroundProperty;

        public static FrameworkElement SessionRow(Session s, string brand)
        {
            var g = new Grid { Margin = new Thickness(0, 2, 0, 2), Background = Brushes.Transparent };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(18) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            FrameworkElement dot;
            if (s.Status == "app")
            {
                var e = new Border { Width = 8, Height = 8, CornerRadius = new CornerRadius(4), BorderThickness = new Thickness(1.5) };
                e.SetResourceReference(Border.BorderBrushProperty, "Info");
                dot = e;
            }
            else
            {
                var e = new Border { Width = 8, Height = 8, CornerRadius = new CornerRadius(4) };
                e.SetResourceReference(Border.BackgroundProperty, s.Status == "busy" ? brand : "Track");
                dot = e;
            }
            dot.VerticalAlignment = VerticalAlignment.Top;
            dot.HorizontalAlignment = HorizontalAlignment.Left;
            dot.Margin = new Thickness(1, 6, 0, 0);
            g.Children.Add(dot);

            var texts = new StackPanel();
            texts.Children.Add(Text(L.T(s.Title), 13, s.Status == "idle" && (DateTime.Now - s.Updated).TotalHours > 3 ? "Text2" : "Text"));
            var meta = new List<string> { L.T(s.Kind) };
            string model = PrettyModel(s.Model);
            if (model != null) meta.Add(model);
            if (s.Note != null) meta.Add(s.Note);
            if (s.Status == "busy") meta.Add(L.T("working"));
            texts.Children.Add(Text(string.Join(" · ", meta), 11.5, "Text3"));
            Grid.SetColumn(texts, 1);
            g.Children.Add(texts);

            if (s.Status != "app")
            {
                var ago = Text(Ago(s.Updated), 11.5, "Text3");
                ago.VerticalAlignment = VerticalAlignment.Top;
                ago.Margin = new Thickness(8, 1, 0, 0);
                Grid.SetColumn(ago, 2);
                g.Children.Add(ago);
            }
            g.ToolTip = L.T(s.Title) + "\n" + string.Join(" · ", meta) + (s.Status != "app" ? "\n" + L.F("Last activity: {0}", s.Updated.ToString("HH:mm", It)) : "");
            return g;
        }

        /// <summary>
        /// Build a service card with its header, limits, notice and optional expanded session list. Sections that fold
        /// ("sessions", "free") ask isOpen and toggleOpen by name; without them every section is shown open and cannot fold.
        /// </summary>
        public static Border ProviderCard(ProviderState p, bool expanded, Action toggle, Action<string> login, Func<string, bool> isOpen = null, Action<string> toggleOpen = null)
        {
            if (p.Models != null) return LocalCard(p, expanded, toggle, isOpen, toggleOpen);
            bool sessionsOpen = isOpen == null || isOpen("sessions");
            Action toggleSessions = toggleOpen == null ? null : (Action)(() => toggleOpen("sessions"));
            string brand = BrandBrush(p.Id);
            var stack = new StackPanel();
            stack.Children.Add(ProviderHeader(p, brand, expanded, toggle));

            // Limits.
            var limits = new StackPanel { Margin = new Thickness(0, 4, 0, 0) };
            if (p.Usage != null && p.Usage.Windows.Count > 0)
                foreach (var w in p.Usage.Windows.Where(x => !x.Hidden && !RowHidden(p.Id, x.Key)).Take(6)) limits.Children.Add(UsageRow(w, p.Usage, brand));
            else if (!p.NeedsLogin)
                limits.Children.Add(new TextBlock { Text = L.T("No limit data yet"), FontSize = 12, Margin = new Thickness(0, 6, 0, 0), Foreground = Theme.B("Text3") });
            stack.Children.Add(limits);

            AddUsageNotes(stack, p);
            AddFreeModels(stack, p, isOpen == null || isOpen("free"), toggleOpen == null ? null : (Action)(() => toggleOpen("free")));
            AddTipAndNotice(stack, p, brand, login);
            if (expanded && p.Sessions.Count > 0) AddSessions(stack, p, brand, sessionsOpen, toggleSessions);
            return Card(stack, new Thickness(14, 12, 14, 12));
        }

        /// <summary>The clickable header: logo, name, plan badge and the running and busy session counts.</summary>
        private static DockPanel ProviderHeader(ProviderState p, string brand, bool expanded, Action toggle)
        {
            var head = CardHeader(Logo(p.Id, 20), expanded, toggle);

            var count = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            if (p.Running)
            {
                count.Children.Add(Text(L.N(p.Active, "{0} session", "{0} sessions"), 12, "Text2"));
                if (p.Busy > 0)
                {
                    var pill = new Border { CornerRadius = new CornerRadius(8), Padding = new Thickness(6, 1, 7, 1), Margin = new Thickness(6, 0, 0, 0) };
                    pill.SetResourceReference(Border.BackgroundProperty, "Track");
                    var sp = new StackPanel { Orientation = Orientation.Horizontal };
                    var d = new Border { Width = 6, Height = 6, CornerRadius = new CornerRadius(3), Margin = new Thickness(0, 0, 5, 0), VerticalAlignment = VerticalAlignment.Center };
                    d.SetResourceReference(Border.BackgroundProperty, brand);
                    sp.Children.Add(d);
                    sp.Children.Add(Text(p.Busy.ToString(It), 11.5, "Text", FontWeights.SemiBold));
                    pill.Child = sp;
                    pill.ToolTip = L.N(p.Busy, "{0} session is working right now", "{0} sessions are working right now");
                    count.Children.Add(pill);
                }
            }
            else count.Children.Add(Text(L.T("not active"), 12, "Text3"));
            DockPanel.SetDock(count, Dock.Right);
            head.Children.Add(count);

            var title = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, ClipToBounds = true };
            var name = Text(p.Name, 14, "Text", FontWeights.SemiBold);
            name.FontFamily = Theme.DisplayFont;
            title.Children.Add(name);
            string plan = PlanLabel(p.Plan);
            if (plan != null)
            {
                var badge = new Border { CornerRadius = new CornerRadius(4), Padding = new Thickness(5, 0, 5, 1), Margin = new Thickness(8, 1, 0, 0), VerticalAlignment = VerticalAlignment.Center };
                badge.SetResourceReference(Border.BackgroundProperty, "Track");
                badge.Child = Text(plan, 10, "Text2", FontWeights.Bold);
                title.Children.Add(badge);
            }
            head.Children.Add(title);
            return head;
        }

        /// <summary>Cached-data marker, weekly breakdown, extra credits, period spending and today's models.</summary>
        private static void AddUsageNotes(StackPanel stack, ProviderState p)
        {
            // Mark cached provider usage once in the card.
            if (p.Usage != null && p.Usage.Source != "live" && string.IsNullOrEmpty(p.Notice))
            {
                var st = Text(L.F("Limits updated {0}", AgoPhrase(p.Usage.At)), 11.5, "Text3");
                st.Margin = new Thickness(0, 8, 0, 0);
                stack.Children.Add(st);
            }

            // Compact weekly breakdown and extra-credit notes.
            if (p.Usage != null && p.Usage.Breakdown.Count > 0)
            {
                var b = Text(L.F("This week: {0}", string.Join(" · ", p.Usage.Breakdown.Select(kv => kv.Key + " " + Math.Round(kv.Value) + "%"))), 11.5, "Text3");
                b.Margin = new Thickness(0, 8, 0, 0);
                b.TextWrapping = TextWrapping.Wrap;
                stack.Children.Add(b);
            }
            if (p.Usage != null && p.Usage.CreditsNote != null)
            {
                var c = Text(p.Usage.CreditsNote(), 11.5, "Text3");
                c.Margin = new Thickness(0, p.Usage.Breakdown.Count > 0 ? 2 : 8, 0, 0);
                c.TextWrapping = TextWrapping.Wrap;
                stack.Children.Add(c);
            }

            // Show period spending and today's locally reported model usage for credit services.
            if (p.Usage != null && p.Usage.SpendNote != null && !RowHidden(p.Id, "*spend"))
            {
                var sn = Text(p.Usage.SpendNote(), 11.5, "Text3");
                sn.Margin = new Thickness(0, 8, 0, 0);
                sn.TextWrapping = TextWrapping.Wrap;
                stack.Children.Add(sn);
            }
            if (p.Usage != null && p.Usage.TodayModels != null && p.Usage.TodayModels.Count > 0 && !RowHidden(p.Id, "*models"))
            {
                // Show each model beside its cost, or tokens if the agent does not report a cost.
                var ms = p.Usage.TodayModels;
                var money = new Money { Currency = "USD" };
                var box = new StackPanel { Margin = new Thickness(0, p.Usage.SpendNote != null ? 4 : 8, 0, 0) };
                box.Children.Add(Text(L.F("Today: {0}", L.N(ms.Sum(m => m.Replies), "{0} reply", "{0} replies")), 11.5, "Text3"));
                foreach (var m in ms.Take(3))
                {
                    var row = new DockPanel { Margin = new Thickness(0, 1, 0, 0) };
                    var cost = Text(m.Cost > 0 ? money.Format(m.Cost) : L.F("{0} tokens", Tokens((int)Math.Min(int.MaxValue, m.Tokens))), 11.5, "Text2");
                    cost.Margin = new Thickness(8, 0, 0, 0);
                    DockPanel.SetDock(cost, Dock.Right);
                    row.Children.Add(cost);
                    var modelName = Text(ShortModel(m.Model), 11.5, "Text2");
                    modelName.TextTrimming = TextTrimming.CharacterEllipsis;
                    row.Children.Add(modelName);
                    box.Children.Add(row);
                }
                if (ms.Count > 3) box.Children.Add(Text(L.F("+{0} more", ms.Count - 3), 11.5, "Text3"));
                box.ToolTip = L.T("Models used today through this service by OpenCode and Pi, with the cost they computed:") + "\n"
                              + string.Join("\n", ms.Select(m => L.F("{0}: {1}, {2} tokens, {3} replies", m.Model, money.Format(m.Cost), Tokens((int)Math.Min(int.MaxValue, m.Tokens)), m.Replies)));
                stack.Children.Add(box);
            }
        }

        /// <summary>
        /// The free models box. Folded, it counts the models and names those whose free period ends soonest; open, it lists
        /// every model, and a click copies the model id to pass to Pi, OpenCode or a script.
        /// </summary>
        private static void AddFreeModels(StackPanel stack, ProviderState p, bool open, Action toggle)
        {
            if (p.Usage == null || p.Usage.FreeModels == null || RowHidden(p.Id, "*free")) return;
            var fm = p.Usage.FreeModels;
            double indent = toggle != null ? 18 : 0;
            var box = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
            string count = L.N(fm.Count, "{0} free model", "{0} free models");
            if (toggle != null) box.Children.Add(FoldRow(count, open, toggle, new Thickness(0)));
            else box.Children.Add(Text(count, 11.5, "Text3"));
            var shown = open ? fm.Take(FreeModelsShown).ToList() : fm.Where(m => m.Expires.HasValue).Take(3).ToList();
            if (open)
            {
                var hint = Text(L.T("Click a model to copy its ID for Pi, OpenCode or your scripts."), 11, "Text3");
                hint.TextWrapping = TextWrapping.Wrap;
                hint.TextTrimming = TextTrimming.None;
                hint.Margin = new Thickness(indent, 0, 0, 2);
                box.Children.Add(hint);
            }
            foreach (var m in shown) box.Children.Add(FreeModelRow(m, indent));
            if (open && fm.Count > FreeModelsShown)
            {
                var more = Text(L.F("+{0} more", fm.Count - FreeModelsShown), 11.5, "Text3");
                more.Margin = new Thickness(indent, 1, 0, 0);
                box.Children.Add(more);
            }
            stack.Children.Add(box);
        }

        private const int FreeModelsShown = 40;

        /// <summary>A free model with its expiry, or its context window, on the right; a click copies its id.</summary>
        private static FrameworkElement FreeModelRow(FreeModel m, double indent)
        {
            var row = new DockPanel { Margin = new Thickness(indent, 1, 0, 0), Background = Brushes.Transparent, Cursor = Cursors.Hand };
            string context = FreeModels.Context(m.Context);
            string right = m.Expires.HasValue ? L.F("free until {0}", FreeModels.When(m.Expires.Value)) : context == null ? null : L.F("{0} context", context);
            var side = Text(right ?? "", 11.5, m.Expires.HasValue && m.Expires.Value <= DateTime.Today.AddDays(1) ? "Caution" : "Text3");
            side.Margin = new Thickness(8, 0, 0, 0);
            DockPanel.SetDock(side, Dock.Right);
            row.Children.Add(side);
            var modelName = Text(m.Name, 11.5, "Text2");
            modelName.TextTrimming = TextTrimming.CharacterEllipsis;
            row.Children.Add(modelName);
            row.ToolTip = FreeModels.Line(m) + "\n" + L.F("Click to copy {0}", m.Id);
            row.MouseLeftButtonUp += (o, e) =>
            {
                try { Clipboard.SetText(m.Id); side.Text = L.T("Copied"); }
                catch (System.Runtime.InteropServices.COMException) { }
                e.Handled = true;
            };
            return row;
        }

        /// <summary>The active unused-quota or wait-instead-of-paying tip, then the service notice with an optional sign-in link.</summary>
        private static void AddTipAndNotice(StackPanel stack, ProviderState p, string brand, Action<string> login)
        {
            if (p.Tip != null)
            {
                var tip = new DockPanel { Margin = new Thickness(0, 8, 0, 0) };
                var ic = Glyph("", 12, brand);
                ic.Margin = new Thickness(0, 1, 6, 0);
                ic.VerticalAlignment = VerticalAlignment.Top;
                DockPanel.SetDock(ic, Dock.Left);
                tip.Children.Add(ic);
                var t = Text(p.Tip.Line, 11.5, "Text2");
                t.TextWrapping = TextWrapping.Wrap;
                tip.Children.Add(t);
                tip.ToolTip = p.Tip.Text;
                stack.Children.Add(tip);
            }

            if (!string.IsNullOrEmpty(p.Notice))
            {
                var note = new DockPanel { Margin = new Thickness(0, 8, 0, 0) };
                var ic = Glyph("", 12, "Text3");
                ic.Margin = new Thickness(0, 0, 6, 0);
                DockPanel.SetDock(ic, Dock.Left);
                note.Children.Add(ic);
                if (p.NeedsLogin && p.CanSignIn && login != null)
                {
                    var link = new Button { Content = L.T("Sign in"), Padding = new Thickness(4, 0, 4, 1), FontSize = 12, Margin = new Thickness(4, 0, 0, 0) };
                    link.SetResourceReference(FrameworkElement.StyleProperty, "LinkButton");
                    link.Click += (o, e) => login(p.Id);
                    DockPanel.SetDock(link, Dock.Right);
                    note.Children.Add(link);
                }
                var nt = Text(p.Notice, 11.5, "Text3");
                nt.VerticalAlignment = VerticalAlignment.Center;
                nt.TextWrapping = TextWrapping.Wrap;
                note.Children.Add(nt);
                stack.Children.Add(note);
            }
        }

        /// <summary>Open desktop apps, then agent sessions, folded behind a summary row when the caller supports it.</summary>
        private static void AddSessions(StackPanel stack, ProviderState p, string brand, bool sessionsOpen, Action toggleSessions)
        {
            var div = new Border { Height = 1, Margin = new Thickness(-14, 12, -14, 6) };
            div.SetResourceReference(Border.BackgroundProperty, "Divider");
            stack.Children.Add(div);
            // Open desktop apps stay visible; agent sessions fold behind a summary row when the caller supports it.
            foreach (var s in p.Sessions.Where(s => s.Status == "app")) stack.Children.Add(SessionRow(s, brand));
            var ordered = p.Sessions.Where(s => s.Status != "app")
                .OrderByDescending(s => s.Status == "busy")
                .ThenByDescending(s => s.Updated).ToList();
            if (ordered.Count > 0 && toggleSessions != null) stack.Children.Add(SessionSummary(ordered, sessionsOpen, toggleSessions));
            if (sessionsOpen || toggleSessions == null)
            {
                foreach (var s in ordered.Take(6)) stack.Children.Add(SessionRow(s, brand));
                if (ordered.Count > 6)
                {
                    var more = Text(L.F("+{0} more sessions", ordered.Count - 6), 11.5, "Text3");
                    more.Margin = new Thickness(18, 2, 0, 0);
                    stack.Children.Add(more);
                }
            }
        }

        /// <summary>A card header with the icon on the left and, when the card can fold, a chevron that toggles it.</summary>
        private static DockPanel CardHeader(FrameworkElement icon, bool expanded, Action toggle)
        {
            var head = new DockPanel { Background = Brushes.Transparent, Cursor = toggle != null ? Cursors.Hand : null };
            icon.Margin = new Thickness(0, 0, 10, 0);
            DockPanel.SetDock(icon, Dock.Left);
            head.Children.Add(icon);
            if (toggle == null) return head;
            var chev = Glyph(expanded ? "" : "", 10, "Text3");
            chev.Margin = new Thickness(10, 0, 2, 0);
            DockPanel.SetDock(chev, Dock.Right);
            head.Children.Add(chev);
            head.MouseLeftButtonUp += (o, e) => { toggle(); e.Handled = true; };
            return head;
        }

        /// <summary>A clickable row that counts agent sessions and shows or hides their list.</summary>
        private static FrameworkElement SessionSummary(List<Session> sessions, bool open, Action toggle)
        {
            int busy = sessions.Count(s => s.Status == "busy");
            string text = L.N(sessions.Count, "{0} session", "{0} sessions");
            if (busy > 0) text += " · " + busy.ToString(It) + " " + L.T("working");
            return FoldRow(text, open, toggle, new Thickness(0, 2, 0, 2));
        }

        /// <summary>A clickable summary line with a chevron that shows or hides the rows below it.</summary>
        private static FrameworkElement FoldRow(string text, bool open, Action toggle, Thickness margin)
        {
            var row = new DockPanel { Background = Brushes.Transparent, Cursor = Cursors.Hand, Margin = margin };
            var chev = Glyph(open ? "" : "", 9, "Text3");
            chev.Width = 18;
            chev.HorizontalAlignment = HorizontalAlignment.Left;
            DockPanel.SetDock(chev, Dock.Left);
            row.Children.Add(chev);
            row.Children.Add(Text(text, 11.5, "Text2"));
            row.MouseLeftButtonUp += (o, e) => { toggle(); e.Handled = true; };
            return row;
        }

        /// <summary>
        /// Build the local-model card with GPU capacity and each model's server, memory and context.
        /// List attached CLI sessions under each model and unmatched sessions at the bottom.
        /// </summary>
        public static Border LocalCard(ProviderState p, bool expanded, Action toggle, Func<string, bool> isOpen = null, Action<string> toggleOpen = null)
        {
            var stack = new StackPanel();
            var icon = Glyph(LocalGlyph, 18, "Text");
            icon.Width = 20;
            var head = CardHeader(icon, expanded, toggle);
            var count = Text(!p.Running ? L.T("no server running") : p.Models.Count == 0 ? L.T("none loaded") : L.N(p.Models.Count, "{0} loaded", "{0} loaded"), 12, p.Models.Count > 0 ? "Text2" : "Text3");
            count.VerticalAlignment = VerticalAlignment.Center;
            DockPanel.SetDock(count, Dock.Right);
            head.Children.Add(count);
            var name = Text(p.Name, 14, "Text", FontWeights.SemiBold);
            name.FontFamily = Theme.DisplayFont;
            name.VerticalAlignment = VerticalAlignment.Center;
            head.Children.Add(name);
            stack.Children.Add(head);

            // GPU memory is the capacity constraint for local models.
            if (p.Running && p.GpuTotal > 0)
            {
                double free = 100 - Math.Min(100, p.GpuUsed * 100.0 / p.GpuTotal);
                var w = new UsageWindow { Label = "Video memory", Used = 100 - free, RightText = L.F("{0} free", GpuMemory.Format(Math.Max(0, p.GpuTotal - p.GpuUsed))) };
                var row = UsageRow(w, null, "Info");
                row.ToolTip = L.F("Video memory: {0} of {1} in use", GpuMemory.Format(p.GpuUsed), GpuMemory.Format(p.GpuTotal));
                stack.Children.Add(row);
            }

            foreach (var m in p.Models) stack.Children.Add(LocalModelBlock(m));

            // CLI sessions without a matching loaded model, including cloud and unreachable servers.
            if (expanded && p.Sessions.Count > 0)
            {
                var div = new Border { Height = 1, Margin = new Thickness(-14, 12, -14, 6) };
                div.SetResourceReference(Border.BackgroundProperty, "Divider");
                stack.Children.Add(div);
                var sub = Text(L.T("Coding agents"), 11.5, "Text3", FontWeights.SemiBold);
                sub.Margin = new Thickness(0, 0, 0, 2);
                stack.Children.Add(sub);
                var ordered = p.Sessions.OrderByDescending(x => x.Status == "busy").ThenByDescending(x => x.Updated).ToList();
                bool open = isOpen == null || isOpen("sessions");
                if (toggleOpen != null) stack.Children.Add(SessionSummary(ordered, open, () => toggleOpen("sessions")));
                if (open)
                {
                    foreach (var s in ordered.Take(6)) stack.Children.Add(SessionRow(s, "Info"));
                    if (ordered.Count > 6)
                    {
                        var more = Text(L.F("+{0} more sessions", ordered.Count - 6), 11.5, "Text3");
                        more.Margin = new Thickness(18, 2, 0, 0);
                        stack.Children.Add(more);
                    }
                }
            }
            if (!p.Running && p.Sessions.Count == 0)
            {
                var none = Text(L.T("Ollama, LM Studio, llama.cpp and other local servers appear here while they run, on Windows or in WSL."), 12, "Text3");
                none.TextWrapping = TextWrapping.Wrap;
                none.Margin = new Thickness(0, 6, 0, 0);
                stack.Children.Add(none);
            }
            return Card(stack, new Thickness(14, 12, 14, 12));
        }

        /// <summary>One loaded model: memory, server facts, context in use and the CLI sessions attached to it.</summary>
        private static FrameworkElement LocalModelBlock(LocalModel m)
        {
            var block = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };
            var top = new DockPanel();
            string mem = m.Vram > 0 ? GpuMemory.Format(m.Vram) : m.Ram > 0 ? GpuMemory.Format(m.Ram) : null;
            if (mem != null)
            {
                var r = Text(mem, 13, "Text", FontWeights.SemiBold);
                r.Margin = new Thickness(8, 0, 0, 0);
                DockPanel.SetDock(r, Dock.Right);
                top.Children.Add(r);
            }
            var dot = new Border { Width = 8, Height = 8, CornerRadius = new CornerRadius(4), Margin = new Thickness(1, 0, 9, 0), VerticalAlignment = VerticalAlignment.Center };
            dot.SetResourceReference(Border.BackgroundProperty, m.Busy > 0 ? "Success" : "Track");
            DockPanel.SetDock(dot, Dock.Left);
            top.Children.Add(dot);
            var title = Text(m.Name, 13, "Text", FontWeights.SemiBold);
            title.TextTrimming = TextTrimming.CharacterEllipsis;
            top.Children.Add(title);
            block.Children.Add(top);

            var facts = LocalFacts(m).Where(f => !f.Contains("VRAM") || mem == null).ToList();
            if (m.Detail != null) facts.Insert(1, m.Detail);
            var meta = Text(string.Join(" · ", facts), 11.5, "Text3");
            meta.Margin = new Thickness(18, 1, 0, 0);
            meta.TextTrimming = TextTrimming.CharacterEllipsis;
            block.Children.Add(meta);

            // Show context usage for the active request when the server reports it.
            if (m.Context > 0 && m.ContextUsed > 0)
            {
                double left = 100 - Math.Min(100, m.ContextUsed * 100.0 / m.Context);
                var bar = Bar(left, LevelBrush(left, "Info"), 3);
                bar.Margin = new Thickness(18, 5, 0, 0);
                bar.ToolTip = L.F("Context in use: {0} of {1} tokens", Tokens(m.ContextUsed), Tokens(m.Context));
                block.Children.Add(bar);
            }
            block.ToolTip = m.Name + "\n" + string.Join(" · ", LocalFacts(m)) + (m.Slots > 0 ? "\n" + L.F("Requests in progress: {0} of {1}", m.Busy, m.Slots) : "");
            foreach (var s in m.Sessions.Take(4))
            {
                var sr = SessionRow(s, "Info");
                sr.Margin = new Thickness(18, 2, 0, 0);
                block.Children.Add(sr);
            }
            return block;
        }
    }
}
