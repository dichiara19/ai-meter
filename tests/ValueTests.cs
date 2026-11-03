using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace AIMeter
{
    /// <summary>Every surface states a limit the same way, and the two display choices change all of them together.</summary>
    internal static class ValueTests
    {
        internal static void Register(Action<string, Action> test)
        {
            test("A limit reads as what is left, in its own unit, on every surface", WhatIsLeft);
            test("Showing what is used, or credit limits as a percentage, changes every surface; notifications still say what is left", Choices);
            test("Video memory reads as free out of total, or in use, like a limit", VideoMemory);
            test("The pill shows the money left on a credit limit, and the card lists the limit the pill uses", PillAndCard);
            test("Every supported language words the limit values and their settings", Translations);
        }

        private static void Assert(bool condition, string message) { if (!condition) throw new Exception(message); }

        private static UsageWindow Session() { return new UsageWindow { Label = "Session 5 h", Short = UsageWindow.ShortFiveHours, Used = 82 }; }
        private static UsageWindow Credits() { return new UsageWindow { Label = "Account credits", Short = "$", Used = 95.1, Spent = new Money { Used = 14.27, Limit = 15, Currency = "USD" } }; }

        /// <summary>Run with the display choices set, restoring them afterwards.</summary>
        private static void With(string limits, string money, Action body)
        {
            var s = Settings.Current;
            string oldLimits = s.LimitValues, oldMoney = s.MoneyValues;
            try { s.LimitValues = limits; s.MoneyValues = money; body(); }
            finally { s.LimitValues = oldLimits; s.MoneyValues = oldMoney; }
        }

        private static void WhatIsLeft()
        {
            With("left", "amount", () =>
            {
                Assert(Ui.Value(Session(), null, Ui.ValueStyle.Number) == "18%" && Ui.Value(Session(), null, Ui.ValueStyle.Short) == "18% left", "A quota does not read as the share left.");
                Assert(Ui.Value(Credits(), null, Ui.ValueStyle.Number) == "$0.73" && Ui.Value(Credits(), null, Ui.ValueStyle.Short) == "$0.73 left"
                       && Ui.Value(Credits(), null, Ui.ValueStyle.Long) == "$0.73 left of $15.00", "A credit limit does not read as the money left.");
                var balance = new UsageWindow { Label = "Spent with this key", NoBar = true, RightText = "$12.64" };
                Assert(Ui.Value(balance, null, Ui.ValueStyle.Long) == "$12.64", "A balance was reworded.");

                // A model limit capped by its general limit shows the effective room.
                var u = new Usage();
                u.Windows.Add(new UsageWindow { Label = "Week", Short = UsageWindow.ShortWeek, Used = 90 });
                var model = new UsageWindow { Label = "Week", Suffix = "Opus", Short = "m", Used = 20 };
                u.Windows.Add(model);
                Assert(Ui.Value(model, u, Ui.ValueStyle.Short) == "10% left", "A capped limit does not show its effective room.");

                // A service with no limit shows its balance, never a hidden spending row.
                var p = new ProviderState { Id = "fixture", Name = "Fixture", Usage = new Usage() };
                p.Usage.Windows.Add(new UsageWindow { Label = "Spent today", NoBar = true, Hidden = true, RightText = "$1.00" });
                p.Usage.Windows.Add(balance);
                Assert(Ui.Balance(p) == balance, "A hidden spending row was taken for the balance.");
            });
        }

        private static void Choices()
        {
            With("used", "amount", () =>
            {
                Assert(Ui.Value(Session(), null, Ui.ValueStyle.Short) == "82% used" && Ui.Value(Credits(), null, Ui.ValueStyle.Long) == "$14.27 used of $15.00", "Showing what is used did not apply.");
                Assert(Ui.Value(Session(), null, Ui.ValueStyle.Long, true) == "18% left", "A notification did not say what is left.");
            });
            With("left", "percent", () =>
            {
                Assert(Ui.Value(Credits(), null, Ui.ValueStyle.Number) == "5%" && Ui.Value(Credits(), null, Ui.ValueStyle.Short) == "5% left", "Credit limits as a percentage did not apply.");
            });
            var p = new ProviderState { Id = "claude", Name = "Claude", Usage = new Usage() };
            p.Usage.Windows.Add(Session());
            With("used", "amount", () => Assert(Ui.Summary(Snapshot(p)) == "Claude: 82% used (Session 5 h)", "The tooltip does not follow the choice."));
        }

        private static void VideoMemory()
        {
            long gb = 1L << 30;
            With("left", "amount", () =>
            {
                Assert(GpuMemory.Value((long)(6.2 * gb), 8 * gb, Ui.ValueStyle.Number) == "1.8/8.0 GB", "The pill does not show free out of total.");
                Assert(GpuMemory.Value((long)(6.2 * gb), 8 * gb, Ui.ValueStyle.Long) == "1.8 GB free of 8.0 GB", "The card does not show free out of total.");
                Assert(GpuMemory.Pair(gb / 2, 16 * gb) == "0.5/16 GB", "A small part of a large card lost its decimals.");
            });
            With("used", "amount", () => Assert(GpuMemory.Value((long)(6.2 * gb), 8 * gb, Ui.ValueStyle.Number) == "6.2/8.0 GB", "In-use memory did not follow the choice."));
        }

        private static void PillAndCard()
        {
            var p = new ProviderState { Id = "openrouter", Name = "OpenRouter", Usage = new Usage { Source = "live", At = DateTime.Now } };
            p.Usage.Windows.Add(new UsageWindow { Label = "Spent with this key", NoBar = true, RightText = "$12.64", Amount = 12.64, AmountUnit = "USD" });
            p.Usage.Windows.Add(new UsageWindow { Label = "Free model requests today", Short = "m", Used = 4 });
            p.Usage.Windows.Add(Credits());
            var s = Settings.Current;
            string old;
            bool had = s.PillLimits.TryGetValue("openrouter", out old);
            try
            {
                s.PillLimits["openrouter"] = "Account credits|";
                With("left", "amount", () =>
                {
                    var texts = Texts(Ui.PillRow(Snapshot(p), "5h", true));
                    Assert(texts.Contains("$0.73") && !texts.Any(t => t.EndsWith("%")), "The pill does not show the money left on the chosen credit limit.");
                    var card = Texts(Ui.ProviderCard(p, false, null, x => { }));
                    Assert(card.Any(t => t.StartsWith("Account credits")) && card.Contains("$0.73 left"), "The card does not list the limit the pill shows.");
                });
            }
            finally { if (had) s.PillLimits["openrouter"] = old; else s.PillLimits.Remove("openrouter"); }
        }

        private static void Translations()
        {
            var keys = new[]
            {
                "{0}% left", "{0}% used", "{0} left", "{0} used", "{0} left of {1}", "{0} used of {1}", "{0} free of {1}", "{0} in use of {1}",
                "{0} still has {1} left.", "Limits show", "In the taskbar, the panel, the widget and the overlay. Video memory follows the same choice.",
                "What is left", "What is used", "Credit limits show", "For limits in money, such as credits, key limits and budgets.",
                "The amount", "A percentage", "Video memory: {0}", "Notifications always say what is left, since they warn that a limit is running out.",
            };
            try
            {
                foreach (var lang in new[] { "it", "es", "fr", "de", "pt" })
                {
                    L.Init(lang);
                    foreach (var key in keys) Assert(L.T(key) != key, "Missing translation for " + lang + ": " + key);
                }
            }
            finally { L.Init("en"); }
        }

        private static Snapshot Snapshot(ProviderState p)
        {
            var s = new Snapshot { Taken = DateTime.Now };
            s.Providers.Add(p);
            return s;
        }

        /// <summary>The text of every TextBlock in a control tree, runs included.</summary>
        private static List<string> Texts(DependencyObject root)
        {
            var list = new List<string>();
            var text = root as TextBlock;
            // A label built from runs reports no Text: join its runs instead.
            if (text != null) list.Add(text.Inlines.Count > 0 ? string.Concat(text.Inlines.OfType<System.Windows.Documents.Run>().Select(r => r.Text)) : text.Text);
            foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>()) list.AddRange(Texts(child));
            return list;
        }
    }
}
