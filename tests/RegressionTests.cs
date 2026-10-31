using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.Serialization;
using System.Text;
using System.Threading;

namespace AIMeter
{
    public static class RegressionTests
    {
        private const BindingFlags Private = BindingFlags.Instance | BindingFlags.Static | BindingFlags.NonPublic;
        private static int passed;
        private static string root;

        public static void Run(string directory)
        {
            root = directory;
            L.Init("en");
            Test("A first sample is unknown, not zero spending", FirstSample);
            Test("Period boundaries exclude earlier consumption", PeriodBoundaries);
            Test("Balance top-ups remain explicitly estimated", BalanceTopUp);
            Test("Counter resets never become negative spending", CounterReset);
            Test("Credential and currency scopes persist separately", ScopedHistory);
            Test("Legacy unscoped history cannot seed a baseline", LegacyHistory);
            Test("Unknown and estimated budgets have no remaining percentage", EstimatedBudgets);
            Test("UTC rollover rejects an old daily total", DayRollover);
            Test("UTC week and month rollovers reject old totals", LongRollover);
            Test("A missing period field falls back independently", PartialServicePeriods);
            Test("Current provider totals retain budget bars and alerts", CurrentBudget);
            Test("Automatic selection includes money and excludes stale data", AutomaticMoney);
            Test("Pi forks deduplicate inherited replies across homes", PiForks);
            Test("Independent identical Pi replies are retained", IndependentPiReplies);
            Test("A 503 preserves extra credits and the pill preference", ExtraFailure);
            Test("A successful extra response replaces cached credits", ExtraRecovery);
            Test("Changing credentials clears history and extra cache", ChangedCredential);
            Test("Changing accounts isolates threshold alerts", AlertIdentity);
            Test("Changing accounts discards previous forecast pace", ForecastIdentity);
            Test("Short bursts only forecast the near future", ForecastHorizon);
            Test("Extra-key backoff applies until the key changes", ExtraBackoff);
            Test("Missing choices survive a restart and expire after ten days", MissingChoice);
            Test("Money changes invalidate the render signature", MoneySignature);
            Test("Every supported language describes estimates, cached data and the welcome window", Translations);
            Test("Budgets reject non-finite amounts", InvalidBudget);
            Test("Earlier Italian window codes in definitions keep their length", LegacyWindowCodes);
            Test("Automatic selection prefers less room over a session forecast", AutomaticRoom);
            Test("A secondary limit is not the default pill limit", SecondaryDefault);
            Test("A UTC period gives a counter its reset", UtcPeriod);
            Test("Free models skip routers, paid, expired and media models", FreeModelList);
            StartupRegressionTests.Register(Test);
            HistoryRegressionTests.Register(Test);
            ConsumptionTests.Register(Test);
            Console.WriteLine("PASS: " + passed + " regression tests; isolated files and registry keys, loopback HTTP only.");
        }

        private static void Test(string name, Action action)
        {
            Settings.TestFolder = Path.Combine(root, passed.ToString(CultureInfo.InvariantCulture));
            Directory.CreateDirectory(Settings.TestFolder);
            typeof(SpendTracker).GetField("history", Private).SetValue(null, null);
            var s = (Settings)FormatterServices.GetUninitializedObject(typeof(Settings));
            s.Budgets = new Dictionary<string, string>();
            s.PillLimits = new Dictionary<string, string>();
            s.NotifyResets = new Dictionary<string, bool>();
            s.HiddenRows = new Dictionary<string, bool>();
            s.Services = new Dictionary<string, bool>();
            s.ExpandedServices = new Dictionary<string, bool>();
            s.NotifyLow = true;
            s.NotifyReset = true;
            Settings.Current = s;
            try { action(); }
            catch (Exception e) { throw new Exception("FAIL: " + name + ": " + e.Message, e); }
            passed++;
            Console.WriteLine("PASS: " + name);
        }

        private static void Assert(bool condition, string message)
        {
            if (!condition) throw new Exception(message);
        }

        private static object Call(object instance, string name, params object[] args)
        {
            return instance.GetType().GetMethod(name, Private).Invoke(instance, args);
        }

        private static string Scope(string token) { return SpendTracker.Scope("fixture", token, "USD"); }
        private static void Record(string scope, double? total, double? balance, DateTime at)
        {
            SpendTracker.Record(scope, total, balance, at);
        }

        private static void FirstSample()
        {
            Record(Scope("a"), 500, null, DateTime.Today.AddHours(10));
            Assert(SpendTracker.Since(Scope("a"), DateTime.Today) == null, "A single sample reported spending.");
        }

        private static void PeriodBoundaries()
        {
            var start = DateTime.Today;
            Record(Scope("a"), 100, null, start.AddDays(-2));
            Record(Scope("a"), 125, null, start.AddHours(1));
            Assert(SpendTracker.Since(Scope("a"), start) == null, "Pre-period spending leaked across the boundary.");
            Record(Scope("a"), 127, null, start.AddHours(2));
            var value = SpendTracker.Since(Scope("a"), start);
            Assert(value.Amount == 2 && value.Estimated && value.From == start.AddHours(1), "Incorrect amount or coverage.");
        }

        private static void BalanceTopUp()
        {
            var now = DateTime.Today.AddHours(10);
            Record(Scope("a"), null, 100, now);
            Record(Scope("a"), null, 140, now.AddMinutes(15));
            var value = SpendTracker.Since(Scope("a"), now.Date);
            Assert(value.Amount == 0 && value.Estimated, "A net top-up was presented as exact spending.");
        }

        private static void CounterReset()
        {
            var now = DateTime.Today.AddHours(10);
            Record(Scope("a"), 100, null, now);
            Record(Scope("a"), 2, null, now.AddMinutes(15));
            Record(Scope("a"), 5, null, now.AddMinutes(30));
            Assert(SpendTracker.Since(Scope("a"), now.Date).Amount == 3, "A counter reset corrupted subsequent increments.");
        }

        private static void ScopedHistory()
        {
            var now = DateTime.Today.AddHours(10);
            Record(Scope("secret-a"), 10, null, now);
            Record(Scope("secret-b"), 1010, null, now.AddMinutes(15));
            string euro = SpendTracker.Scope("fixture", "secret-a", "EUR");
            Record(euro, 3000, null, now.AddMinutes(15));
            typeof(SpendTracker).GetField("history", Private).SetValue(null, null);
            Assert(SpendTracker.Since(Scope("secret-a"), now.Date) == null, "Account A inherited another baseline.");
            Assert(SpendTracker.Since(Scope("secret-b"), now.Date) == null, "Account B inherited another baseline.");
            Assert(SpendTracker.Since(euro, now.Date) == null, "Currencies shared a baseline.");
            var text = File.ReadAllText(Path.Combine(Settings.Folder, "spend-history.json"));
            Assert(!text.Contains("secret-a") && !text.Contains("secret-b"), "History persisted a raw credential.");
        }

        private static void LegacyHistory()
        {
            File.WriteAllText(Path.Combine(Settings.Folder, "spend-history.json"), "{\"fixture\":[[1700000000,10,null]]}");
            Assert(SpendTracker.Since("fixture", DateTime.MinValue) == null, "Legacy history was used without account identity.");
        }

        private static DefinedProvider SpendingProvider(double? day, double? week, double? month, DateTime at)
        {
            var d = ServiceDefinition.FromJson("{\"id\":\"fixture\",\"spend\":{\"total\":\"total\",\"day\":\"day\",\"week\":\"week\",\"month\":\"month\",\"currency\":\"USD\"}}", "fixture");
            var p = new DefinedProvider(d);
            var doc = new Dictionary<string, object> { { "total", 500 }, { "day", day }, { "week", week }, { "month", month } };
            var spend = Call(p, "ReadSpend", doc);
            spend.GetType().GetField("At").SetValue(spend, at);
            typeof(DefinedProvider).GetField("spend", Private).SetValue(p, spend);
            typeof(DefinedProvider).GetField("spendScope", Private).SetValue(p, Scope("a"));
            return p;
        }

        private static Usage Decorate(DefinedProvider p, DateTime now)
        {
            // Prevent scans of the user's real agent sessions in this isolated test process.
            typeof(AgentCosts).GetField("scannedAt", Private).SetValue(null, DateTime.Now);
            return (Usage)Call(p, "Decorate", new Usage { Source = "live", At = now }, new Probe(Settings.Current), now);
        }

        private static void EstimatedBudgets()
        {
            var now = DateTime.Today.AddHours(12);
            Settings.Current.Budgets["fixture"] = "10|month";
            var p = SpendingProvider(null, null, null, now);
            Record(Scope("a"), 500, null, now.AddMinutes(-15));
            var unknown = Decorate(p, now).Windows.Single(w => w.Budget);
            Assert(unknown.NoBar && !unknown.HasPercent && unknown.Spent == null, "Unknown spending created available budget.");
            Record(Scope("a"), 509.5, null, now);
            var estimated = Decorate(p, now).Windows.Single(w => w.Budget);
            Assert(estimated.NoBar && !estimated.HasPercent && estimated.RightText.Contains("estimated"), "An estimate created a percentage.");
            Assert(estimated.Detail().Contains("since"), "The estimate lost its observation start.");
            var snapshot = SnapshotFor(new ProviderState { Id = "fixture", Name = "Fixture", Usage = Decorate(p, now) });
            new Forecaster().Apply(snapshot);
            Assert(new Alerts().Check(snapshot, Settings.Current).Count == 0, "An estimate triggered a threshold or reset alert.");
            Assert(estimated.RunsOutAt == null, "An estimate received a forecast.");
            var row = Ui.UsageRow(estimated, snapshot.Providers[0].Usage, "Info") as System.Windows.Controls.StackPanel;
            Assert(row != null && row.Children.Count == 1 && row.ToolTip.ToString().Contains("since"), "The estimate lost its UI explanation.");
        }

        private static void DayRollover()
        {
            var utc = new DateTime(2026, 10, 29, 0, 1, 0, DateTimeKind.Utc);
            Settings.Current.Budgets["fixture"] = "10|day";
            var p = SpendingProvider(9.5, 20, 50, utc.AddMinutes(-2).ToLocalTime());
            var u = Decorate(p, utc.ToLocalTime());
            Assert(!u.Windows.Single(w => w.Budget).HasPercent, "Yesterday's total became today's budget.");
            Assert(u.Windows.Single(w => w.Label == "Spent today").RightText == "Spending unavailable", "Yesterday was relabelled as today.");
            Assert(new Alerts().Check(SnapshotFor(new ProviderState { Id = "fixture", Usage = u }), Settings.Current).Count == 0, "Rollover produced a false alert.");
        }

        private static void LongRollover()
        {
            var utc = new DateTime(2026, 6, 1, 0, 1, 0, DateTimeKind.Utc);
            var p = SpendingProvider(9.5, 9.5, 9.5, utc.AddMinutes(-2).ToLocalTime());
            foreach (var period in new[] { "week", "month" })
            {
                Settings.Current.Budgets["fixture"] = "10|" + period;
                Assert(!Decorate(p, utc.ToLocalTime()).Windows.Single(w => w.Budget).HasPercent, "An old " + period + " total remained current.");
            }
        }

        private static void PartialServicePeriods()
        {
            var now = DateTime.Now;
            Record(Scope("a"), 500, null, now.AddMinutes(-15));
            Record(Scope("a"), 502, null, now);
            var p = SpendingProvider(1, null, null, now);
            var u = Decorate(p, now);
            Assert(!u.Windows.Single(w => w.Label == "Spent today").RightText.Contains("estimated"), "A provider total lost its exact status.");
            Assert(u.Windows.Single(w => w.Label == "Spent this month").RightText.Contains("estimated"), "A missing month disabled local fallback.");
        }

        private static void CurrentBudget()
        {
            var now = DateTime.Now;
            Settings.Current.Budgets["fixture"] = "10|day";
            var u = Decorate(SpendingProvider(9.5, 20, 50, now), now);
            var budget = u.Windows.Single(w => w.Budget);
            Assert(budget.HasPercent && budget.Used == 95 && budget.Spent.Used == 9.5, "A valid provider total lost its bar.");
            var snapshot = SnapshotFor(new ProviderState { Id = "fixture", Name = "Fixture", Usage = u });
            var alerts = new Alerts();
            Assert(alerts.Check(snapshot, Settings.Current).Count == 1, "A 95% budget failed to alert.");
            Assert(alerts.Check(snapshot, Settings.Current).Count == 0, "The same threshold alerted twice.");
        }

        private static void AutomaticMoney()
        {
            var u = new Usage();
            var credit = new UsageWindow { Label = "Account credits", Used = 50, Spent = new Money { Used = 50, Limit = 100 } };
            var budget = new UsageWindow { Label = "Daily budget", Budget = true, Used = 95, Spent = new Money { Used = 9.5, Limit = 10 } };
            u.Windows.Add(credit); u.Windows.Add(budget);
            Assert(Ui.Tightest(u) == budget, "Automatic ignored a monetary budget.");
            budget.CachedAt = DateTime.Now;
            Assert(Ui.Tightest(u) == credit, "Automatic selected stale usage.");
            credit.NoBar = true;
            Assert(Ui.Tightest(u) == null, "Automatic treated a standalone balance as a percentage.");
        }

        private static string PiFile(string home, string name, long timestamp)
        {
            var path = Path.Combine(home, @".pi\agent\sessions\project", name + ".jsonl");
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            var entry = Json.Parse("{\"type\":\"message\",\"id\":\"original-entry\",\"message\":{\"role\":\"assistant\",\"provider\":\"fixture\",\"model\":\"test-model\",\"timestamp\":0,\"usage\":{\"totalTokens\":1000,\"cost\":{\"total\":2.5}}}}");
            Json.Obj(entry, "message")["timestamp"] = timestamp;
            File.WriteAllText(path, Json.Serialize(entry) + "\n");
            return path;
        }

        private static long NowMs() { return (long)(DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalMilliseconds; }

        private static void PiForks()
        {
            string a = Path.Combine(Settings.Folder, "home-a"), b = Path.Combine(Settings.Folder, "home-b");
            var file = PiFile(a, "original", NowMs());
            string fork = PiFile(b, "fork", NowMs());
            var entry = Json.Parse(File.ReadAllText(file));
            entry["id"] = "regenerated-entry";
            entry["parentId"] = "different-parent";
            File.WriteAllText(fork, Json.Serialize(entry) + "\n");
            var cost = AgentCosts.Scan(new Probe(Settings.Current), new List<string> { a, b })["fixture"]["test-model"];
            Assert(cost.Cost == 2.5 && cost.Tokens == 1000 && cost.Replies == 1, "Forked history doubled the totals.");
        }

        private static void IndependentPiReplies()
        {
            string home = Path.Combine(Settings.Folder, "home");
            long now = NowMs();
            PiFile(home, "first", now - 10);
            PiFile(home, "second", now);
            var cost = AgentCosts.Scan(new Probe(Settings.Current), new List<string> { home })["fixture"]["test-model"];
            Assert(cost.Replies == 2 && cost.Cost == 5, "Distinct paid responses were incorrectly deduplicated.");
        }

        private sealed class Reply
        {
            public int Status;
            public string Body;
            public Reply(int status, string body) { Status = status; Body = body; }
        }

        private sealed class Server : IDisposable
        {
            private readonly TcpListener listener = new TcpListener(IPAddress.Loopback, 0);
            private readonly Thread thread;
            public readonly string Url;
            public int Requests;
            public Server(params Reply[] replies)
            {
                listener.Start();
                Url = "http://127.0.0.1:" + ((IPEndPoint)listener.LocalEndpoint).Port.ToString(CultureInfo.InvariantCulture);
                thread = new Thread(() =>
                {
                    try
                    {
                        foreach (var reply in replies)
                            using (var client = listener.AcceptTcpClient())
                            using (var stream = client.GetStream())
                            {
                                client.ReceiveTimeout = 3000;
                                var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, true);
                                string header;
                                while (!string.IsNullOrEmpty(header = reader.ReadLine())) { }
                                Interlocked.Increment(ref Requests);
                                var body = Encoding.UTF8.GetBytes(reply.Body);
                                var head = Encoding.ASCII.GetBytes("HTTP/1.1 " + reply.Status + " Fixture\r\nContent-Type: application/json\r\nContent-Length: " + body.Length + "\r\nConnection: close\r\n\r\n");
                                stream.Write(head, 0, head.Length);
                                stream.Write(body, 0, body.Length);
                            }
                    }
                    catch (SocketException) { }
                    catch (IOException) { }
                    catch (ObjectDisposedException) { }
                });
                thread.IsBackground = true;
                thread.Start();
            }
            public void Dispose() { listener.Stop(); thread.Join(4000); }
        }

        private static Reply MainReply(double total)
        {
            return new Reply(200, "{\"data\":{\"usage\":" + total.ToString(CultureInfo.InvariantCulture) + "}}");
        }
        private static Reply Credits(double used) { return new Reply(200, "{\"data\":{\"total_credits\":100,\"total_usage\":" + used.ToString(CultureInfo.InvariantCulture) + "}}"); }

        private static DefinedProvider NetworkProvider(Server server, bool keySlot, string variable)
        {
            var raw = Json.Parse("{\"id\":\"fixture\",\"spend\":{\"total\":\"data.usage\",\"currency\":\"USD\"},\"limits\":[{\"label\":\"Spent with this key\",\"balance\":\"data.usage\",\"currency\":\"USD\"}]}");
            raw["request"] = new Dictionary<string, object> { { "url", server.Url + "/main" }, { "timeout", 2 } };
            var more = new Dictionary<string, object>
            {
                { "request", new Dictionary<string, object> { { "url", server.Url + "/extra" }, { "timeout", 2 } } },
                { "limits", new object[] { new Dictionary<string, object> { { "label", "Account credits" }, { "used", "data.total_usage" }, { "limit", "data.total_credits" }, { "currency", "USD" } } } },
            };
            if (keySlot) more["key"] = "management";
            raw["more"] = new object[] { more };
            if (variable != null) raw["auth"] = new Dictionary<string, object> { { "type", "env" }, { "env", new[] { variable } } };
            return new DefinedProvider(ServiceDefinition.FromJson(Json.Serialize(raw), "fixture"));
        }

        private static Usage Fetch(DefinedProvider provider)
        {
            var d = (ServiceDefinition)typeof(DefinedProvider).GetField("D", Private).GetValue(provider);
            Call(provider, "Fetch", Json.Obj(d.Raw, "request"));
            return (Usage)typeof(DefinedProvider).GetField("live", Private).GetValue(provider);
        }

        private static Snapshot SnapshotFor(ProviderState provider)
        {
            var s = new Snapshot(); s.Providers.Add(provider); return s;
        }

        private static void ExtraFailure()
        {
            using (var server = new Server(MainReply(7), Credits(25), MainReply(8), new Reply(503, "{}")))
            {
                var p = NetworkProvider(server, false, null);
                Settings.Current.PillLimits["fixture"] = "Account credits|";
                var alerts = new Alerts();
                var first = Fetch(p);
                alerts.Check(SnapshotFor(new ProviderState { Id = "fixture", Name = "Fixture", Usage = first }), Settings.Current);
                var second = Fetch(p);
                var credit = second.Windows.Single(w => w.Label == "Account credits");
                Assert(credit.CachedAt.HasValue && !credit.HasPercent && credit.Spent.Used == 25, "A transient failure lost or refreshed cached credits.");
                alerts.Check(SnapshotFor(new ProviderState { Id = "fixture", Name = "Fixture", Usage = second }), Settings.Current);
                Assert(Settings.Current.PillLimits["fixture"] == "Account credits|", "A transient failure erased the pill choice.");
            }
        }

        private static void ExtraRecovery()
        {
            using (var server = new Server(MainReply(7), Credits(25), MainReply(8), new Reply(503, "{}"), MainReply(9), Credits(30)))
            {
                var p = NetworkProvider(server, false, null);
                Fetch(p); Fetch(p);
                var credit = Fetch(p).Windows.Single(w => w.Label == "Account credits");
                Assert(credit.HasPercent && !credit.CachedAt.HasValue && credit.Spent.Used == 30, "Successful data failed to replace cached usage.");
            }
        }

        private static void ChangedCredential()
        {
            const string variable = "AIMETER_REGRESSION_KEY";
            try
            {
                using (var server = new Server(MainReply(10), Credits(25), MainReply(1010), new Reply(503, "{}")))
                {
                    Environment.SetEnvironmentVariable(variable, "synthetic-key-a");
                    var p = NetworkProvider(server, false, variable);
                    Fetch(p);
                    Environment.SetEnvironmentVariable(variable, "synthetic-key-b");
                    var second = Fetch(p);
                    Assert(!second.Windows.Any(w => w.Label == "Account credits"), "A different key inherited cached account credits.");
                    Assert(SpendTracker.Since(Scope("synthetic-key-b"), DateTime.Today) == null, "A different key inherited cumulative spending.");
                    var history = File.ReadAllText(Path.Combine(Settings.Folder, "spend-history.json"));
                    Assert(!history.Contains("synthetic-key"), "A network request persisted raw credentials in history.");
                }
            }
            finally { Environment.SetEnvironmentVariable(variable, null); }
        }

        private static void ExtraBackoff()
        {
            ApiKeys.Save("fixture.management", "synthetic-management-a");
            using (var server = new Server(MainReply(7), new Reply(403, "{}"), MainReply(8), MainReply(9), Credits(30)))
            {
                var p = NetworkProvider(server, true, null);
                Fetch(p);
                Fetch(p);
                Assert(server.Requests == 3, "An unchanged rejected extra key bypassed backoff.");
                ApiKeys.Save("fixture.management", "synthetic-management-b");
                var credit = Fetch(p).Windows.Single(w => w.Label == "Account credits");
                Assert(credit.HasPercent && server.Requests == 5, "A changed extra key did not bypass the old backoff.");
            }
        }

        private static void AlertIdentity()
        {
            var now = DateTime.Now;
            Settings.Current.Budgets["fixture"] = "10|day";
            var u = Decorate(SpendingProvider(9.5, 20, 50, now), now);
            u.Identity = Scope("account-a");
            var snapshot = SnapshotFor(new ProviderState { Id = "fixture", Name = "Fixture", Usage = u });
            var alerts = new Alerts();
            Assert(alerts.Check(snapshot, Settings.Current).Count == 1, "The first account did not alert.");
            u.Identity = Scope("account-b");
            Assert(alerts.Check(snapshot, Settings.Current).Count == 1, "The new account inherited threshold suppression.");
            Assert(new Alerts().Check(snapshot, Settings.Current).Count == 0, "The new account's alert identity was not persisted.");
        }

        private static void ForecastIdentity()
        {
            var now = DateTime.Now;
            var w = new UsageWindow { Label = "Daily budget", Used = 10, StartedAt = now.AddHours(-2), ResetsAt = now.AddHours(4) };
            var u = new Usage { Source = "live", Identity = Scope("account-a"), At = now.AddMinutes(-30) }; u.Windows.Add(w);
            var snapshot = SnapshotFor(new ProviderState { Id = "fixture", Usage = u });
            var forecast = new Forecaster();
            forecast.Apply(snapshot);
            u.At = now; w.Used = 60;
            forecast.Apply(snapshot);
            Assert(w.PacePerHour > 90, "The recent pace fixture was not established.");
            u.Identity = Scope("account-b"); w.Used = 20;
            forecast.Apply(snapshot);
            Assert(w.PacePerHour > 9 && w.PacePerHour < 11, "The new account inherited the old account's pace.");
        }

        /// <summary>Apply two readings of one window, fifteen minutes apart, and return the window.</summary>
        private static UsageWindow ForecastAfter(double startedHoursAgo, double resetsInHours, double before, double after)
        {
            var now = DateTime.Now;
            var w = new UsageWindow { Label = "Window", Used = before, StartedAt = now.AddHours(-startedHoursAgo), ResetsAt = now.AddHours(resetsInHours) };
            var u = new Usage { Source = "live", Identity = Scope("account"), At = now.AddMinutes(-15) }; u.Windows.Add(w);
            var snapshot = SnapshotFor(new ProviderState { Id = "fixture", Usage = u });
            var forecast = new Forecaster();
            forecast.Apply(snapshot);
            u.At = now; w.Used = after;
            forecast.Apply(snapshot);
            return w;
        }

        private static void ForecastHorizon()
        {
            // The reported case: one point used in a burst, eleven hours into a weekly window.
            var weekly = ForecastAfter(11, 157, 0, 1);
            Assert(weekly.PacePerHour > 3, "The recent burst pace was not measured.");
            Assert(weekly.RunsOutAt == null, "A short burst forecast the end of a weekly limit.");

            var session = ForecastAfter(2, 3, 60, 75);
            Assert(session.RunsOutAt.HasValue && session.RunsOutAt.Value < DateTime.Now.AddHours(1), "An imminent session exhaustion was not forecast.");

            var sustained = ForecastAfter(96, 72, 89, 90);
            Assert(sustained.RunsOutAt.HasValue, "A sustained weekly average did not forecast exhaustion.");

            var early = ForecastAfter(2, 166, 4, 5);
            Assert(early.RunsOutAt == null, "The average of a window's first hours forecast the whole week.");
        }

        private static void MissingChoice()
        {
            Settings.Current.PillLimits["fixture"] = "Account credits|";
            var p = new ProviderState { Id = "fixture", Name = "Fixture", Usage = new Usage { Source = "live" } };
            new Alerts().Check(SnapshotFor(p), Settings.Current);
            Assert(Settings.Current.PillLimits.ContainsKey("fixture"), "A saved choice had no initial grace period.");
            new Alerts().Check(SnapshotFor(p), Settings.Current);
            Assert(Settings.Current.PillLimits.ContainsKey("fixture"), "Restarting lost the grace period.");
            var path = Path.Combine(Settings.Folder, "alerts.json");
            var data = Json.ParseFile(path);
            Json.Obj(data, "fixture|Account credits|")["seen"] = DateTime.Now.AddDays(-11).ToString("yyyyMMddHHmm", CultureInfo.InvariantCulture);
            File.WriteAllText(path, Json.Serialize(data));
            new Alerts().Check(SnapshotFor(p), Settings.Current);
            Assert(!Settings.Current.PillLimits.ContainsKey("fixture"), "A permanently missing limit never expired.");
        }

        private static void MoneySignature()
        {
            var u = new Usage();
            var w = new UsageWindow { Label = "Credits", Used = 95, Spent = new Money { Used = 9.50, Limit = 10 } }; u.Windows.Add(w);
            var snapshot = SnapshotFor(new ProviderState { Id = "fixture", Usage = u });
            var signature = snapshot.Signature(); w.Spent.Used = 9.59;
            Assert(signature != snapshot.Signature(), "An amount change did not invalidate rendering.");
        }

        private static void Translations()
        {
            foreach (var lang in new[] { "it", "es", "fr", "de", "pt" })
            {
                L.Init(lang);
                Assert(L.T("Spending unavailable") != "Spending unavailable", "Missing unavailable translation for " + lang);
                Assert(L.T("{0} estimated since {1}") != "{0} estimated since {1}", "Missing estimate translation for " + lang);
                Assert(L.T("Last updated {0}") != "Last updated {0}", "Missing cached-data translation for " + lang);
                foreach (var key in new[] { "Welcome to AI Meter", "Show in the taskbar", "Preview with sample data", "Sample data", "Leave sample data", "Showing sample data", "Free models", "free until {0}", "{0} free models" })
                    Assert(L.T(key) != key, "Missing welcome translation for " + lang + ": " + key);
            }
            L.Init("en");
        }

        private static void InvalidBudget()
        {
            foreach (var amount in new[] { "NaN", "Infinity", "-1", "0" })
            {
                Settings.Current.Budgets["fixture"] = amount + "|day";
                double value; string period;
                Assert(!SpendTracker.Budget(Settings.Current, "fixture", out value, out period), "Accepted invalid amount " + amount);
            }
        }

        private static void LegacyWindowCodes()
        {
            var limit = typeof(DefinedProvider).GetMethod("Limit", Private);
            var document = new Dictionary<string, object> { { "pct", 10 } };
            var expected = new Dictionary<string, TimeSpan> { { "7g", TimeSpan.FromDays(7) }, { "mese", TimeSpan.FromDays(30) }, { "1g", TimeSpan.FromDays(1) }, { "7d", TimeSpan.FromDays(7) } };
            foreach (var pair in expected)
            {
                var spec = new Dictionary<string, object> { { "label", "Usage" }, { "short", pair.Key }, { "percent", "pct" } };
                var window = (UsageWindow)limit.Invoke(null, new object[] { document, spec });
                Assert(window != null && window.Length == pair.Value, "Wrong window length for short code " + pair.Key);
            }
        }

        private static void AutomaticRoom()
        {
            var now = DateTime.Now;
            var u = new Usage();
            var session = new UsageWindow { Label = "Session 5 h", Short = UsageWindow.ShortFiveHours, Used = 40, ResetsAt = now.AddHours(3), RunsOutAt = now.AddHours(1) };
            var week = new UsageWindow { Label = "Week", Short = UsageWindow.ShortWeek, Used = 90, ResetsAt = now.AddDays(5) };
            u.Windows.Add(session); u.Windows.Add(week);
            Assert(Ui.Tightest(u) == week, "A session forecast hid a weekly limit with less room.");
            week.Used = 20;
            Assert(Ui.Tightest(u) == session, "Automatic ignored the session with less room.");
            week.Used = 40;
            Assert(Ui.Tightest(u) == session, "A forecast did not break an equal-room tie.");
            session.RunsOutAt = null;
            Assert(Ui.Tightest(u) == week, "An equal-room tie did not prefer the longer window.");
        }

        private static void SecondaryDefault()
        {
            var u = new Usage();
            var free = new UsageWindow { Label = "Free model requests today", Short = "m", Used = 10 };
            var key = new UsageWindow { Label = "Key limit", Short = "$", Used = 30, Spent = new Money { Used = 3, Limit = 10, Currency = "USD" } };
            u.Windows.Add(key); u.Windows.Add(free);
            Assert(Ui.Primary(u) == key, "A secondary counter replaced the service's own limit as default.");
            u.Windows.Remove(key);
            Assert(Ui.Primary(u) == free, "A lone secondary limit was not used as default.");
        }

        private static void UtcPeriod()
        {
            var limit = typeof(DefinedProvider).GetMethod("Limit", Private);
            var document = new Dictionary<string, object> { { "used", 5 }, { "limit", 50 } };
            var spec = new Dictionary<string, object> { { "label", "Free model requests today" }, { "short", "m" }, { "used", "used" }, { "limit", "limit" }, { "period", "day" } };
            var w = (UsageWindow)limit.Invoke(null, new object[] { document, spec });
            var midnight = DateTime.SpecifyKind(DateTime.UtcNow.Date.AddDays(1), DateTimeKind.Utc).ToLocalTime();
            Assert(w != null && w.Used == 10, "Wrong percentage for a request counter.");
            Assert(w.ResetsAt == midnight && w.StartedAt < DateTime.Now && w.Length.HasValue, "A UTC day counter did not reset at UTC midnight.");
        }

        private static void FreeModelList()
        {
            Func<string, string, string, string, object> model = (id, prompt, completion, expires) => new Dictionary<string, object>
            {
                { "id", id }, { "name", id }, { "context_length", 262144 }, { "expiration_date", expires },
                { "pricing", new Dictionary<string, object> { { "prompt", prompt }, { "completion", completion } } },
                { "architecture", new Dictionary<string, object> { { "input_modalities", new ArrayList { "text", "image" } } } },
                { "supported_parameters", new ArrayList { "tools", "temperature" } },
            };
            string later = DateTime.Today.AddDays(30).ToString("yyyy-MM-dd"), soon = DateTime.Today.ToString("yyyy-MM-dd");
            // A media generator with zero token prices, like Google Lyria, is not a free chat model.
            var audio = (Dictionary<string, object>)model("g/lyria", "0", "0", null);
            audio["architecture"] = new Dictionary<string, object> { { "output_modalities", new ArrayList { "text", "audio" } } };
            var doc = new Dictionary<string, object> { { "data", new ArrayList {
                model("b/open:free", "0", "0", null), model("a/ending:free", "0", "0", later), model("c/today:free", "0", "0", soon),
                model("d/expired:free", "0", "0", DateTime.Today.AddDays(-1).ToString("yyyy-MM-dd")),
                model("e/paid", "0.000001", "0.000002", null), model("openrouter/free", "0", "0", null), audio } } };
            var list = FreeModels.Parse(doc);
            Assert(list != null && list.Count == 3, "Wrong number of free models.");
            Assert(list[0].Id == "c/today:free" && list[1].Id == "a/ending:free" && list[2].Id == "b/open:free", "Free models are not ordered by expiry.");
            Assert(list[0].Tools && !list[0].Reasoning && list[0].Inputs == "text, image", "Free model features were not read.");
            Assert(FreeModels.Line(list[0]).Contains(Ui.Tokens(262144)) && FreeModels.Line(list[0]).Contains("free until today"), "Free model line misses context or expiry.");
            Assert(FreeModels.Context(1048576) == "1M" && FreeModels.Context(1000000) == "1M", "Wrong context format.");
        }
    }
}
