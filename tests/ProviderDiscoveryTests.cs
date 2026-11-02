using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;

namespace AIMeter
{
    /// <summary>Discovery of keys that coding agents already keep, and the model lists of services without usage data.</summary>
    internal static class ProviderDiscoveryTests
    {
        internal static void Register(Action<string, Action> test)
        {
            test("Keys are found under each alias coding agents use, with references resolved and commands never run", KeyAliases);
            test("NVIDIA and Gemini lists keep chat models with what they state, and leave their price unknown", UnpricedLists);
            test("Free-model limits come from the definition or the usage answer, with the reset of their zone", DefinedRates);
            test("Daily resets follow the zone's daylight-saving rules", ZoneResets);
            test("list_free_models reports price status, pool, caps, rates and headroom", FreeModelLimits);
            test("Catalogue-only services stay off the surfaces and off by default, but reach MCP", CatalogueOnlyServices);
        }

        private static readonly BindingFlags Private = BindingFlags.Instance | BindingFlags.Static | BindingFlags.NonPublic;

        private static void CatalogueOnlyServices()
        {
            // As providers\nvidia.json: a model list and no usage request. OpenRouter has both.
            var nvidia = ServiceDefinition.FromJson("{ \"id\": \"nvidia\", \"name\": \"NVIDIA NIM\", \"detect\": { \"env\": [\"NVIDIA_API_KEY\"] }, " +
                "\"auth\": { \"type\": \"key\", \"env\": [\"NVIDIA_API_KEY\"] }, \"freeModels\": { \"url\": \"https://example.test/models\", \"format\": \"openai\" } }", "test");
            var router = ServiceDefinition.FromJson("{ \"id\": \"openrouter\", \"name\": \"OpenRouter\", \"auth\": { \"type\": \"key\" }, " +
                "\"request\": { \"url\": \"https://example.test/key\" }, \"freeModels\": { \"url\": \"https://example.test/models\" } }", "test");
            var plain = ServiceDefinition.FromJson("{ \"id\": \"vercel\", \"name\": \"Vercel\", \"request\": { \"url\": \"https://example.test/usage\" } }", "test");
            Assert(nvidia.CatalogueOnly && !router.CatalogueOnly && !plain.CatalogueOnly, "Only a model list without a usage request is catalogue-only.");

            // Found keys turn a monitored service on, never a catalogue-only one; an explicit choice wins either way.
            var settings = new Settings();
            string old = Environment.GetEnvironmentVariable("NVIDIA_API_KEY");
            try
            {
                Environment.SetEnvironmentVariable("NVIDIA_API_KEY", "nvapi-test-0123456789");
                Assert(nvidia.Detected() && !settings.ServiceOn(nvidia), "A catalogue-only service turned itself on because its key was found.");
                settings.Services["nvidia"] = true;
                Assert(settings.ServiceOn(nvidia), "The user's choice to turn a catalogue on was ignored.");
            }
            finally { Environment.SetEnvironmentVariable("NVIDIA_API_KEY", old); }

            var snap = new Snapshot();
            snap.Providers.Add(new ProviderState { Id = "openrouter", Name = "OpenRouter", Usage = new Usage() });
            snap.Providers.Add(new ProviderState { Id = "nvidia", Name = "NVIDIA", CatalogueOnly = true,
                Catalogue = new Catalogue { ReadAt = DateTime.Now, Models = new System.Collections.Generic.List<FreeModel> { new FreeModel { Id = "m", Name = "m" } } } });
            Assert(snap.Shown.Select(p => p.Id).SequenceEqual(new[] { "openrouter" }), "A catalogue-only service reached the surfaces.");
            Assert(!Ui.Summary(snap).Contains("NVIDIA"), "A catalogue-only service reached the tooltip.");
            Assert(Ui.PillRow(snap, null, false).Children.Count == 1, "A catalogue-only service reached the pill.");

            var server = new McpServer(() => snap, () => false);
            var reply = Json.Parse(server.Handle("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/call\",\"params\":{\"name\":\"list_free_models\",\"arguments\":{\"include_unconfirmed\":true}}}"));
            Assert(JsonText.Indented(Json.Obj(reply, "result")).Contains("\"nvidia\""), "A catalogue-only service did not reach MCP.");
        }

        private static void UnpricedLists()
        {
            var unknown = new FreeStatus { Status = "unknown", Note = "test" };
            var nvidia = Json.ParseAny("{\"object\":\"list\",\"data\":[{\"id\":\"meta/llama-4-maverick\"},{\"id\":\"nvidia/nv-embedqa-e5-v5\"},{\"id\":\"nvidia/llama-3.1-nemoguard-8b-content-safety\"}]}");
            var list = FreeModels.Parse(nvidia, "openai", unknown);
            Assert(list.Count == 1 && list[0].Id == "meta/llama-4-maverick", "Embedding or safety models were listed as chat models.");
            Assert(list[0].FeaturesUnknown && list[0].Context == 0 && list[0].Free.Status == "unknown", "An ids-only list claimed facts it does not state.");

            var gemini = Json.ParseAny("{\"models\":[" +
                "{\"name\":\"models/gemini-2.5-flash\",\"displayName\":\"Gemini 2.5 Flash\",\"inputTokenLimit\":1048576,\"outputTokenLimit\":65536,\"supportedGenerationMethods\":[\"generateContent\",\"countTokens\"],\"thinking\":true}," +
                "{\"name\":\"models/text-embedding-004\",\"supportedGenerationMethods\":[\"embedContent\"]}," +
                "{\"name\":\"models/gemini-2.5-flash-preview-tts\",\"supportedGenerationMethods\":[\"generateContent\"]}]}");
            list = FreeModels.Parse(gemini, "gemini", unknown);
            Assert(list.Count == 1 && list[0].Id == "gemini-2.5-flash" && list[0].Name == "Gemini 2.5 Flash", "The Gemini list kept a non-chat model or lost its name.");
            Assert(list[0].Context == 1048576 && list[0].MaxOutput == 65536 && list[0].Reasoning, "Gemini token limits or thinking were not read.");
            Assert(FreeModels.Parse(gemini, "unknown-format") == null, "An unknown format produced a list.");
        }

        private static DefinedProvider RatedProvider()
        {
            string json = "{ \"id\": \"rated\", \"name\": \"Rated\", \"freeModels\": { \"url\": \"http://127.0.0.1:9/models\", \"pool\": \"rated:free\", \"limits\": [" +
                "{ \"metric\": \"requests_per_minute\", \"limit\": 20, \"source\": \"documented\", \"reference\": \"https://example.test/limits\", \"checked\": \"2026-11-02\" }," +
                "{ \"metric\": \"requests_per_day\", \"limit\": \"data.free.limit\", \"used\": \"data.free.used\", \"source\": \"provider_reported\", \"period\": \"day\" } ] } }";
            return new DefinedProvider(ServiceDefinition.FromJson(json, "test"));
        }

        private static Catalogue Rated(DefinedProvider p, bool withUsage, double used)
        {
            typeof(DefinedProvider).GetField("live", Private).SetValue(p, withUsage ? new Usage { Source = "live", At = DateTime.Now } : null);
            typeof(DefinedProvider).GetField("usageDoc", Private).SetValue(p, Json.ParseAny("{\"data\":{\"free\":{\"limit\":50,\"used\":" + used + "}}}"));
            var read = new Catalogue { ReadAt = DateTime.Now, Models = new System.Collections.Generic.List<FreeModel> { new FreeModel { Id = "a:free", Name = "A", Free = FreeStatus.ZeroPrice } } };
            return (Catalogue)typeof(DefinedProvider).GetMethod("WithRates", Private).Invoke(p, new object[] { read, DateTime.Now });
        }

        private static void DefinedRates()
        {
            var p = RatedProvider();
            var c = Rated(p, true, 12);
            Assert(c.Pool == "rated:free" && c.Rates.Count == 2, "The pool or the limits were not read from the definition.");
            var minute = c.Rates[0];
            Assert(minute.Limit == 20 && minute.Source == "documented" && minute.CheckedAt.HasValue && minute.Coverage == null, "A documented limit is wrong.");
            var day = c.Rates[1];
            Assert(day.Limit == 50 && day.Used == 12 && day.Remaining == 38 && day.Coverage == "all_clients", "A reported limit was not read from the usage answer.");
            Assert(day.ResetZone == "UTC" && day.ResetsAt.HasValue && day.ResetsAt.Value.ToUniversalTime().TimeOfDay == TimeSpan.Zero, "A daily limit has no UTC midnight reset.");

            var stale = Rated(p, false, 12).Rates[1];
            Assert(stale.Limit == null && stale.Used == null && stale.Remaining == null, "A reported limit kept values without a current usage answer.");
        }

        private static void ZoneResets()
        {
            Func<int, int, int, int, DateTime> utc = (month, day, hour, minute) => new DateTime(2026, month, day, hour, minute, 0, DateTimeKind.Utc);
            // 7 March 23:30 in Los Angeles (PST): the next midnight is still standard time, before the 2:00 change.
            Assert(Zones.NextMidnightUtc(utc(3, 8, 7, 30), "America/Los_Angeles") == utc(3, 8, 8, 0), "Wrong reset before the spring change.");
            // 1 July: daylight time, UTC-7.
            Assert(Zones.NextMidnightUtc(utc(7, 1, 12, 0), "America/Los_Angeles") == utc(7, 2, 7, 0), "Wrong reset in summer.");
            // 1 November 05:00 in Los Angeles: clocks go back at 2:00, so the next midnight is UTC-8.
            Assert(Zones.NextMidnightUtc(utc(11, 1, 12, 0), "America/Los_Angeles") == utc(11, 2, 8, 0), "Wrong reset across the autumn change.");
            Assert(Zones.NextMidnightUtc(utc(11, 2, 23, 59), "UTC") == utc(11, 3, 0, 0), "Wrong UTC reset.");
            Assert(Zones.NextMidnightUtc(utc(11, 2, 12, 0), "Mars/Olympus") == null, "An unknown zone produced a reset.");
        }

        private static void FreeModelLimits()
        {
            NetworkBackoff.Clear();
            var rated = Rated(RatedProvider(), true, 50);   // The daily counter is used up.
            var nvidia = new Catalogue
            {
                ReadAt = DateTime.Now, Pool = "nvidia:account",
                Models = new System.Collections.Generic.List<FreeModel> { new FreeModel { Id = "meta/llama-4", Name = "meta/llama-4", FeaturesUnknown = true, Free = new FreeStatus { Status = "unknown", Note = "n" } } },
            };
            nvidia.Rates.Add(new RateLimit { Metric = "requests_per_minute", Scope = "account", Source = "unknown" });
            var snap = new Snapshot();
            snap.Providers.Add(new ProviderState { Id = "rated", Name = "Rated", Catalogue = rated });
            snap.Providers.Add(new ProviderState { Id = "nvidia", Name = "NVIDIA", Catalogue = nvidia });
            var server = new McpServer(() => snap, () => false);

            var models = Models(server, "{}");
            Assert(models.Count == 1 && Json.Str(models[0], "id") == "a:free", "A model of unknown price was listed by default.");
            var limits = Json.Obj(models[0], "limits");
            Assert(Json.Str(limits, "pool") == "rated:free", "The pool is missing.");
            var caps = Json.Obj(limits, "per_request");
            Assert(Json.Obj(caps, "context_tokens")["value"] == null && Json.Str(Json.Obj(caps, "context_tokens"), "source") == "unknown", "An unknown cap was not null.");
            var headroom = Json.Obj(limits, "headroom");
            Assert(!Json.Bool(headroom, "can_start_now") && Json.Num(headroom, "max_new_jobs_now") == 0
                   && Json.Str(Json.Obj(headroom, "blocking"), "metric") == "requests_per_day", "A used-up daily counter did not block.");

            var all = Models(server, "{\"include_unconfirmed\":true}");
            var unknown = all.Single(m => Json.Str(m, "service") == "nvidia");
            Assert(Json.Str(Json.Obj(unknown, "free"), "status") == "unknown" && unknown["tools"] == null, "An unconfirmed model claimed a price or features.");
            var nvidiaRoom = Json.Obj(Json.Obj(unknown, "limits"), "headroom");
            Assert(Json.Bool(nvidiaRoom, "can_start_now") && Json.Num(nvidiaRoom, "max_new_jobs_now") == 1, "Unknown concurrency did not allow exactly one task.");
            Assert(Models(server, "{\"include_unconfirmed\":true,\"min_output_tokens\":1}").Count == 0, "A model with an unknown output limit passed a minimum.");

            NetworkBackoff.Set("nvidia", DateTime.Now.AddMinutes(5));
            unknown = Models(server, "{\"include_unconfirmed\":true,\"service\":\"nvidia\"}").Single();
            Assert(Json.Str(Json.Obj(Json.Obj(Json.Obj(unknown, "limits"), "headroom"), "blocking"), "reason") == "rate_limited", "A rate-limited service did not block.");
            NetworkBackoff.Clear();
        }

        private static System.Collections.Generic.List<System.Collections.Generic.Dictionary<string, object>> Models(McpServer server, string arguments)
        {
            var reply = Json.Parse(server.Handle("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/call\",\"params\":{\"name\":\"list_free_models\",\"arguments\":" + arguments + "}}"));
            var content = Json.Obj(Json.Obj(reply, "result"), "structuredContent");
            return ((System.Collections.IList)content["models"]).OfType<System.Collections.Generic.Dictionary<string, object>>().ToList();
        }

        private static void Assert(bool condition, string message)
        {
            if (!condition) throw new Exception(message);
        }

        /// <summary>A fake home with the given files, relative path to content.</summary>
        private static string Home(params string[] pathsAndContents)
        {
            string home = Path.Combine(Settings.Folder, "home-" + Guid.NewGuid().ToString("N"));
            for (int i = 0; i < pathsAndContents.Length; i += 2)
            {
                string file = Path.Combine(home, pathsAndContents[i]);
                Directory.CreateDirectory(Path.GetDirectoryName(file));
                File.WriteAllText(file, pathsAndContents[i + 1], new UTF8Encoding(false));
            }
            return home;
        }

        /// <summary>KeyFinder's search of one home; "WSL" mode resolves variables from shell profiles only, never this process.</summary>
        private static KeyFinder.Found Search(ServiceDefinition d, string home)
        {
            var search = typeof(KeyFinder).GetMethod("Search", BindingFlags.Static | BindingFlags.NonPublic);
            return (KeyFinder.Found)search.Invoke(null, new object[] { d, home, true });
        }

        private static ServiceDefinition Definition(string id, string ids, string env)
        {
            return ServiceDefinition.FromJson("{ \"id\": \"" + id + "\", \"name\": \"" + id + "\", \"auth\": { \"type\": \"key\", \"ids\": [" + ids + "], \"env\": [" + env + "] } }", "test");
        }

        private static void KeyAliases()
        {
            var nvidia = Definition("nvidia", "\"nvidia\", \"nvidia-nim\"", "\"NVIDIA_API_KEY\"");
            var gemini = Definition("gemini", "\"google\", \"gemini\"", "\"GEMINI_API_KEY\"");

            // Pi keeps the NVIDIA NIM extension's key under its own provider id.
            var pi = Home(@".pi\agent\auth.json", "{ \"nvidia-nim\": { \"type\": \"api_key\", \"key\": \"nvapi-0123456789abcdef\" } }");
            var found = Search(nvidia, pi);
            Assert(found != null && found.Key == "nvapi-0123456789abcdef" && found.Source.StartsWith("Pi"), "The nvidia-nim alias was not found in Pi.");
            Assert(Search(gemini, pi) == null, "A key was given to the wrong service.");

            // OpenCode refers to a variable exported in the shell profile.
            var openCode = Home(@".local\share\opencode\auth.json", "{ \"google\": { \"type\": \"api\", \"key\": \"{env:GEMINI_API_KEY}\" } }",
                ".bashrc", "export PATH=$PATH:/x\nexport GEMINI_API_KEY='AIzaSy0123456789abcdef'\n");
            found = Search(gemini, openCode);
            Assert(found != null && found.Key == "AIzaSy0123456789abcdef" && found.Source.StartsWith("OpenCode"), "An {env:} reference was not resolved from the profile.");

            // Only the profile: the definition's own variable names.
            found = Search(nvidia, Home(".zshrc", "export NVIDIA_API_KEY=nvapi-fedcba9876543210\n"));
            Assert(found != null && found.Key == "nvapi-fedcba9876543210", "The variable in the profile was not found.");

            // A command is never run, a malformed file is skipped, and a short value is not a key.
            Assert(Search(nvidia, Home(@".pi\agent\auth.json", "{ \"nvidia\": { \"key\": \"!op read op://vault/nvidia\" } }")) == null, "A command value was used.");
            Assert(Search(nvidia, Home(@".pi\agent\auth.json", "{ \"nvidia\": { \"key\": ")) == null, "A malformed file produced a key.");
            Assert(Search(nvidia, Home(@".pi\agent\auth.json", "{ \"nvidia\": { \"key\": \"short\" } }")) == null, "A placeholder was taken for a key.");
        }
    }
}
