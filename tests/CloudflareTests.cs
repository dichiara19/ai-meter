using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading;

namespace AIMeter
{
    /// <summary>
    /// Cloudflare Workers AI (providers\cloudflare-workers-ai.json) against recorded GraphQL answers: the account value a
    /// request needs besides its key, today's UTC date, errors in a 200 answer and the empty answer for no usage.
    /// </summary>
    internal static class CloudflareTests
    {
        internal static void Register(Action<string, Action> test)
        {
            test("Cloudflare Workers AI reads today's Neurons for the account against the free 10,000", TodayNeurons);
            test("No Neuron rows today is zero, while a missing account sends no request and asks for it", EmptyAndMissing);
            test("A GraphQL error in a 200 answer is a notice, not usage", GraphQlError);
            test("The account ID is read beside the key OpenCode keeps, or from the profile beside Pi's", AccountBesideKey);
            test("An account ID saved with the dashboard address it was copied from is reduced to the ID", AccountInAddress);
            test("Neurons beyond each day's free 10,000 are spent at $0.011 per 1,000, today, this week and this month", PaidNeurons);
            test("Cloudflare's models come from models.dev as a free tier drawing on today's Neurons", FreeTierCatalogue);
        }

        private const BindingFlags Private = BindingFlags.Instance | BindingFlags.Static | BindingFlags.NonPublic;
        private const string Account = "0123456789abcdef0123456789abcdef";
        private const string Token = "cf-synthetic-token-0123456789";

        private static void Assert(bool condition, string message) { if (!condition) throw new Exception(message); }

        /// <summary>An answer with today's total and, by day, this week's and this month's (both empty unless given).</summary>
        private static string Answer(string today, string week = "[]", string month = "[]")
        {
            return "{\"data\":{\"viewer\":{\"accounts\":[{\"today\":" + today + ",\"week\":" + week + ",\"month\":" + month + "}]}},\"errors\":null}";
        }

        private static string Day(string date, int neurons) { return "{\"sum\":{\"totalNeurons\":" + neurons + "},\"dimensions\":{\"date\":\"" + date + "\"}}"; }

        private static void TodayNeurons()
        {
            using (var server = new Recorder(Answer("[{\"count\":12,\"sum\":{\"totalNeurons\":2500}}]")))
            using (new Variables(Token, Account))
            {
                var p = Provider(server.Url);
                var u = Fetch(p);
                var w = u == null ? null : u.Windows.Single();
                Assert(w != null && w.Label == "Free Neurons today" && w.Short == UsageWindow.ShortDay && Math.Abs(w.Used - 25) < 0.001, "Today's Neurons were not read against 10,000.");
                Assert(w.ResetsAt == DateTime.UtcNow.Date.AddDays(1).ToLocalTime(), "The free allocation does not reset at midnight UTC.");
                string request = server.Requests.Single();
                Assert(request.Contains("Authorization: Bearer " + Token), "The token was not sent as a bearer token.");
                Assert(request.Contains("\"account\":\"" + Account + "\"") && request.Contains("\"date\":\"" + DateTime.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + "\""),
                    "The query does not name the account and today's UTC date.");
                var utc = DateTime.UtcNow.Date;
                Assert(request.Contains("\"week\":\"" + utc.AddDays(-(((int)utc.DayOfWeek + 6) % 7)).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + "\"")
                       && request.Contains("\"month\":\"" + new DateTime(utc.Year, utc.Month, 1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + "\""),
                    "The query does not start the week on Monday and the month on the first, in UTC.");
            }
        }

        private static void EmptyAndMissing()
        {
            using (var server = new Recorder(Answer("[]")))
            using (new Variables(Token, Account))
            {
                var w = Fetch(Provider(server.Url)).Windows.Single();
                Assert(w.Used == 0, "A day without Neuron rows was not zero.");
            }
            bool findKeys = Settings.Current.FindKeys;
            try
            {
                // Without the account in Settings, the environment or beside a found key, nothing is sent.
                Settings.Current.FindKeys = false;
                using (var server = new Recorder(Answer("[]")))
                using (new Variables(Token, null))
                {
                    var p = Provider(server.Url);
                    Assert(Fetch(p) == null && server.Requests.Count == 0, "A request without the account was sent.");
                    Assert((string)typeof(DefinedProvider).GetField("problem", Private).GetValue(p) == "Add your Cloudflare account ID in Settings to see your Neurons", "The missing account was not explained.");
                    lock (DefinedProvider.NeedsExtraKey) Assert(DefinedProvider.NeedsExtraKey.Contains("cloudflare-workers-ai.account"), "Settings does not offer the account ID.");
                    Assert((string)typeof(DefinedProvider).GetField("problemFix", Private).GetValue(p) == "slot:account", "The card's link does not ask for the account ID.");
                }
                using (var server = new Recorder(Answer("[]")))
                using (new Variables(Token, Account))
                {
                    Fetch(Provider(server.Url));
                    lock (DefinedProvider.NeedsExtraKey) Assert(!DefinedProvider.NeedsExtraKey.Contains("cloudflare-workers-ai.account"), "A found account is still asked for.");
                }
            }
            finally { Settings.Current.FindKeys = findKeys; }
        }

        private static void GraphQlError()
        {
            using (var server = new Recorder("{\"data\":null,\"errors\":[{\"message\":\"not authorized for that account\"}]}"))
            using (new Variables(Token, Account))
            {
                var p = Provider(server.Url);
                Assert(Fetch(p) == null, "An error answer produced usage.");
                string problem = (string)typeof(DefinedProvider).GetField("problem", Private).GetValue(p);
                Assert(problem != null && problem.StartsWith("Cloudflare refused to show this account's Neurons"), "The GraphQL error was not shown as the definition's notice.");
                // The key and the account are both there: the card's link opens Settings rather than asking for a key again.
                Assert((string)typeof(DefinedProvider).GetField("problemFix", Private).GetValue(p) == "settings", "The card's link asks for the key the service already has.");
            }
        }

        private static void AccountBesideKey()
        {
            var d = Definition("https://example.test/graphql");
            string home = Home(@".local\share\opencode\auth.json",
                "{ \"cloudflare-workers-ai\": { \"type\": \"api\", \"key\": \"" + Token + "\", \"metadata\": { \"accountId\": \"" + Account + "\" } } }");
            var found = KeyFinder.SearchAll(d, home, false).First();
            Assert(found.App == "OpenCode" && KeyFinder.Beside(found, "metadata.accountId", new[] { "CLOUDFLARE_ACCOUNT_ID" }) == Account, "The account beside OpenCode's key was not found.");

            // Pi keeps the key, the account ID is the variable Pi needs, exported in the profile.
            home = Home(@".pi\agent\auth.json", "{ \"cloudflare-workers-ai\": { \"type\": \"api_key\", \"key\": \"" + Token + "\" } }",
                ".bashrc", "export CLOUDFLARE_ACCOUNT_ID=" + Account + "\n");
            found = KeyFinder.SearchAll(d, home, false).First();
            Assert(found.App == "Pi" && KeyFinder.Beside(found, "metadata.accountId", new[] { "CLOUDFLARE_ACCOUNT_ID" }) == Account, "The account exported beside Pi's key was not found.");
            Assert(KeyFinder.Beside(found, null, new string[0]) == null, "A value was invented where there is none.");
        }

        private static void AccountInAddress()
        {
            var spec = Json.Obj(Json.Obj(Definition("https://example.test/graphql").Raw, "keys"), "account");
            Assert(ServiceDefinition.SlotText(spec, "https://dash.cloudflare.com/" + Account + "/home") == Account, "The ID was not taken from the dashboard address.");
            Assert(ServiceDefinition.SlotText(spec, " " + Account.ToUpperInvariant() + " ") == Account.ToUpperInvariant(), "An upper-case ID was not recognised.");
            Assert(ServiceDefinition.SlotText(spec, "dash.cloudflare.com/home") == null && ServiceDefinition.SlotText(spec, "  ") == null, "A value without an ID was accepted.");

            // The value saved before the pattern existed, as one user pasted it, still names the account.
            bool findKeys = Settings.Current.FindKeys;
            Settings.Current.FindKeys = false;
            ApiKeys.Save("cloudflare-workers-ai.account", Account + "/home");
            try
            {
                using (var server = new Recorder(Answer("[]")))
                using (new Variables(Token, null))
                {
                    Assert(Fetch(Provider(server.Url)) != null, "The saved address did not reach the service as an ID.");
                    Assert(server.Requests.Single().Contains("\"account\":\"" + Account + "\""), "The request does not name the bare ID.");
                }
            }
            finally
            {
                ApiKeys.Remove("cloudflare-workers-ai.account");
                Settings.Current.FindKeys = findKeys;
            }
        }

        private static void PaidNeurons()
        {
            // Today 12,000: 2,000 paid. This month also a day of 25,000 (15,000 paid) and one under the free allocation.
            string month = "[" + Day("2026-11-01", 25000) + "," + Day("2026-11-02", 4000) + "," + Day("2026-11-03", 12000) + "]";
            using (var server = new Recorder(Answer("[{\"count\":40,\"sum\":{\"totalNeurons\":12000}}]", "[" + Day("2026-11-02", 4000) + "," + Day("2026-11-03", 12000) + "]", month)))
            using (new Variables(Token, Account))
            {
                var p = Provider(server.Url);
                var u = Fetch(p);
                Assert(u != null && u.Windows.Single().Used >= 100, "Neurons beyond the free allocation do not fill the limit.");
                var spend = typeof(DefinedProvider).GetField("spend", Private).GetValue(p);
                Func<string, double?> value = f => (double?)spend.GetType().GetField(f).GetValue(spend);
                Assert(Math.Abs(value("Day").Value - 0.022) < 1e-9, "Today's 2,000 paid Neurons are not $0.022.");
                Assert(Math.Abs(value("Week").Value - 0.022) < 1e-9, "This week's spending does not count only the Neurons beyond each day's allocation.");
                Assert(Math.Abs(value("Month").Value - 0.187) < 1e-9, "This month's 17,000 paid Neurons are not $0.187.");
            }
            using (var server = new Recorder(Answer("[]")))
            using (new Variables(Token, Account))
            {
                var p = Provider(server.Url);
                Fetch(p);
                var spend = typeof(DefinedProvider).GetField("spend", Private).GetValue(p);
                Assert(spend != null && (double?)spend.GetType().GetField("Month").GetValue(spend) == 0, "A month without paid Neurons is not $0.");
            }
        }

        /// <summary>models.dev's catalogue: another provider, and Cloudflare's chat model beside a safety model, a deprecated one and an image model.</summary>
        private const string ModelsDev = "{ \"other\": { \"models\": { \"x/chat\": { \"id\": \"x/chat\", \"tool_call\": true } } }, " +
            "\"cloudflare-workers-ai\": { \"id\": \"cloudflare-workers-ai\", \"models\": { " +
            "\"@cf/openai/gpt-oss-120b\": { \"id\": \"@cf/openai/gpt-oss-120b\", \"name\": \"GPT OSS 120B\", \"tool_call\": true, \"reasoning\": true, " +
            "\"modalities\": { \"input\": [\"text\"], \"output\": [\"text\"] }, \"limit\": { \"context\": 128000, \"output\": 16384 }, \"cost\": { \"input\": 0.35, \"output\": 0.75 } }, " +
            "\"@cf/meta/llama-guard-3-8b\": { \"id\": \"@cf/meta/llama-guard-3-8b\", \"tool_call\": false, \"modalities\": { \"input\": [\"text\"], \"output\": [\"text\"] } }, " +
            "\"@cf/old/chat\": { \"id\": \"@cf/old/chat\", \"tool_call\": true, \"status\": \"deprecated\" }, " +
            "\"@cf/black-forest-labs/flux\": { \"id\": \"@cf/black-forest-labs/flux\", \"modalities\": { \"input\": [\"text\"], \"output\": [\"image\"] } } } } }";

        /// <summary>
        /// The model list comes from models.dev, Cloudflare's entry only, as a free tier: priced models free within the
        /// 10,000 Neurons of each UTC day. The Neuron limit reads the usage answer, an empty day as zero, and the budget
        /// keeps the reserve back.
        /// </summary>
        private static void FreeTierCatalogue()
        {
            var spec = Json.Obj(Definition("http://127.0.0.1:9/graphql").Raw, "freeModels");
            Assert(Json.Str(spec, "url") == "https://models.dev/api.json" && Json.Str(spec, "url").IndexOf("{token}", StringComparison.Ordinal) < 0,
                "The model list must be models.dev's public catalogue, read without the Cloudflare token.");
            var status = (FreeStatus)typeof(DefinedProvider).GetMethod("DefinedFreeStatus", Private).Invoke(null, new object[] { spec });
            var models = FreeModels.Parse(Json.ParseAny(ModelsDev), Json.Str(spec, "format"), status, Json.Str(spec, "provider"));
            Assert(models != null && models.Select(m => m.Id).SequenceEqual(new[] { "@cf/openai/gpt-oss-120b" }),
                "Only Cloudflare's current text chat models belong in the list: " + (models == null ? "none" : string.Join(", ", models.Select(m => m.Id))));
            var gpt = models.Single();
            Assert(gpt.Tools && gpt.Reasoning && !gpt.FeaturesUnknown && gpt.Context == 128000 && gpt.MaxOutput == 16384 && gpt.Name == "GPT OSS 120B",
                "The catalogue's facts were not read.");
            Assert(gpt.Free.Confirmed && gpt.Free.Kind == Candidates.FreeTier && Candidates.IsFreeTier(gpt), "Cloudflare's models are not marked as a free tier.");
            Assert(FreeModels.Parse(Json.ParseAny(ModelsDev), "models.dev", status, "missing") == null, "A provider missing from the catalogue gave a list.");

            foreach (var day in new[] { "[]", "[{\"count\":12,\"sum\":{\"totalNeurons\":2500}}]" })
                using (var server = new Recorder(Answer(day)))
                using (new Variables(Token, Account))
                {
                    var p = Provider(server.Url);
                    Fetch(p);
                    var read = new Catalogue { Models = models, ReadAt = DateTime.Now };
                    var catalogue = (Catalogue)typeof(DefinedProvider).GetMethod("WithRates", Private).Invoke(p, new object[] { read, DateTime.Now });
                    var neurons = catalogue.Rates.Single(r => r.Metric == "neurons_per_day");
                    double used = day == "[]" ? 0 : 2500;
                    Assert(neurons.Source == "provider_reported" && neurons.Limit == 10000 && neurons.Used == used && neurons.Remaining == 10000 - used,
                        "Today's Neurons did not reach the free models' limit (" + day + ").");
                    Assert(neurons.ResetsAt == DateTime.UtcNow.Date.AddDays(1).ToLocalTime(), "The allocation does not reset at midnight UTC.");
                    Assert(catalogue.PoolOf(gpt) == "cloudflare-workers-ai:neurons" && catalogue.Allowance.Metric == "neurons_per_day" &&
                           catalogue.Allowance.UsdPerUnit == 0.000011 && catalogue.Allowance.Reserve == 1000, "The pool or the allowance is wrong.");
                    var budget = Candidates.BudgetOf(catalogue, null, catalogue.PoolOf(gpt), DateTime.Now);
                    Assert(Math.Abs(budget.Spendable.Value - (9000 - used)) < 1e-9 && Math.Abs(budget.MaxCost.Value - (9000 - used) * 0.000011) < 1e-12,
                        "The budget does not keep the reserve back.");
                }
        }

        // ------------------------------------------------------------ helpers

        private static ServiceDefinition Definition(string url)
        {
            string repository = Environment.GetEnvironmentVariable("AIMETER_REPOSITORY") ?? Environment.CurrentDirectory;
            var raw = Json.Parse(File.ReadAllText(Path.Combine(repository, @"providers\cloudflare-workers-ai.json"), Encoding.UTF8));
            Json.Obj(raw, "request")["url"] = url;
            Json.Obj(raw, "request")["timeout"] = 2;
            return ServiceDefinition.FromJson(Json.Serialize(raw), "test");
        }

        private static DefinedProvider Provider(string url) { return new DefinedProvider(Definition(url)); }

        private static Usage Fetch(DefinedProvider p)
        {
            var d = (ServiceDefinition)typeof(DefinedProvider).GetField("D", Private).GetValue(p);
            typeof(DefinedProvider).GetMethod("Fetch", Private).Invoke(p, new object[] { Json.Obj(d.Raw, "request") });
            return (Usage)typeof(DefinedProvider).GetField("live", Private).GetValue(p);
        }

        /// <summary>A temporary home folder with the given relative files and contents.</summary>
        private static string Home(params string[] files)
        {
            string home = Path.Combine(Settings.Folder, "cloudflare-home-" + Guid.NewGuid().ToString("N"));
            for (int i = 0; i + 1 < files.Length; i += 2)
            {
                string path = Path.Combine(home, files[i]);
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllText(path, files[i + 1]);
            }
            return home;
        }

        /// <summary>Set the token and account variables the definition reads, restoring them afterwards.</summary>
        private sealed class Variables : IDisposable
        {
            private readonly string key = Environment.GetEnvironmentVariable("CLOUDFLARE_API_KEY"), account = Environment.GetEnvironmentVariable("CLOUDFLARE_ACCOUNT_ID");
            public Variables(string token, string accountId)
            {
                Environment.SetEnvironmentVariable("CLOUDFLARE_API_KEY", token);
                Environment.SetEnvironmentVariable("CLOUDFLARE_ACCOUNT_ID", accountId);
            }
            public void Dispose()
            {
                Environment.SetEnvironmentVariable("CLOUDFLARE_API_KEY", key);
                Environment.SetEnvironmentVariable("CLOUDFLARE_ACCOUNT_ID", account);
            }
        }

        /// <summary>A loopback server that answers every request with one body and records each request, body included.</summary>
        private sealed class Recorder : IDisposable
        {
            private readonly TcpListener listener = new TcpListener(IPAddress.Loopback, 0);
            private readonly Thread thread;
            public readonly string Url;
            public readonly List<string> Requests = new List<string>();

            public Recorder(string body)
            {
                listener.Start();
                Url = "http://127.0.0.1:" + ((IPEndPoint)listener.LocalEndpoint).Port.ToString(CultureInfo.InvariantCulture) + "/graphql";
                thread = new Thread(() =>
                {
                    try
                    {
                        while (true)
                            using (var client = listener.AcceptTcpClient())
                            using (var stream = client.GetStream())
                            {
                                client.ReceiveTimeout = 3000;
                                var reader = new StreamReader(stream, Encoding.UTF8, false, 1024, true);
                                var request = new StringBuilder();
                                int length = 0;
                                string header;
                                while (!string.IsNullOrEmpty(header = reader.ReadLine()))
                                {
                                    request.AppendLine(header);
                                    if (header.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)) length = int.Parse(header.Substring(15).Trim(), CultureInfo.InvariantCulture);
                                }
                                var content = new char[length];
                                int read = 0;
                                while (read < length) { int n = reader.Read(content, read, length - read); if (n <= 0) break; read += n; }
                                request.Append(content, 0, read);
                                lock (Requests) Requests.Add(request.ToString());
                                var bytes = Encoding.UTF8.GetBytes(body);
                                var head = Encoding.ASCII.GetBytes("HTTP/1.1 200 Fixture\r\nContent-Type: application/json\r\nContent-Length: " + bytes.Length + "\r\nConnection: close\r\n\r\n");
                                stream.Write(head, 0, head.Length);
                                stream.Write(bytes, 0, bytes.Length);
                            }
                    }
                    catch (SocketException) { }
                    catch (IOException) { }
                    catch (ObjectDisposedException) { }
                    catch (InvalidOperationException) { }
                });
                thread.IsBackground = true;
                thread.Start();
            }

            public void Dispose() { listener.Stop(); thread.Join(4000); }
        }
    }
}
