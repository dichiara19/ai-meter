using System;
using System.Collections.Generic;
using System.Linq;

namespace AIMeter
{
    /// <summary>
    /// What an engine says of a model's inside. The NInfer answers are cut from a real server's (2026-11-25.4), with
    /// only the fields that are read.
    /// </summary>
    internal static class LocalEngineTests
    {
        internal static void Register(Action<string, Action> test)
        {
            test("NInfer reports the experts of a mixture of experts, its key-value cache and its speed", NInferSegments);
            test("An engine's running totals become a rate only between two readings of the same run", ExpertRate);
            test("Only the options that say where a model's parts are leave a server's command line", KeptOptions);
            test("What an engine does not report is absent from the card and from MCP, never zero", UnknownSegments);
            test("Every supported language words the experts and the key-value cache", Translations);
        }

        private static void Assert(bool condition, string message) { if (!condition) throw new Exception(message); }

        private const string Stats = "{\"capacity\":{\"device_state_slots\":2,\"host_kv_bytes\":0,\"kv_capacity_tokens\":212992,\"max_context\":212992},"
            + "\"experts\":{\"admitted\":3613,\"cpu_routes\":0,\"hits\":14910,\"routes\":22656,\"slots\":734,\"transferred_bytes\":18998074368},"
            + "\"model\":\"inkling-2-mini-q3-uncensored\",\"object\":\"ninfer.stats\",\"uptime_seconds\":8.572973427}";

        private const string Models = "{\"data\":[{\"id\":\"inkling-2-mini-q3-uncensored\",\"owned_by\":\"ninfer\",\"status\":{\"args\":[\"--model-id\",\"inkling-2-mini-q3-uncensored\","
            + "\"--api-key\",\"sk-not-to-be-kept\",\"--port\",\"18963\",\"--kv-dtype\",\"rk4v4\",\"--host-cache-mib\",\"0\",\"--no-thinking\",\"--expert-residency\",\"host\","
            + "\"--expert-host-mib\",\"12500\"],\"value\":\"loaded\"}}],\"object\":\"list\"}";

        private const string Metrics = "# TYPE llamacpp:predicted_tokens_seconds gauge\nllamacpp:predicted_tokens_seconds 19.699013856602232\nllamacpp:kv_cache_tokens 0\n";

        private static ModelSegments Read(string stats, NInferEngine.Sample previous, DateTime now, out NInferEngine.Sample sample)
        {
            return NInferEngine.Read(Json.ParseAny(stats), NInferEngine.Options(Json.ParseAny(Models)), Metrics, previous, now, out sample);
        }

        private static void NInferSegments()
        {
            Assert(NInferEngine.Is("ninfer 2026-11-25.4+9ba1276 (nvcc 13.4.92, 75 80 86 89 120a)") && !NInferEngine.Is("b6123-0a1b2c3") && !NInferEngine.Is(null), "The build string does not tell NInfer from llama.cpp.");
            NInferEngine.Sample sample;
            var s = Read(Stats, null, DateTime.Now, out sample);
            Assert(s.Engine == "NInfer" && s.KvCapacity == 212992 && s.KvType == "rk4v4", "The key-value cache was not read.");
            Assert(s.ExpertsIn == "host" && s.ExpertSlots == 734 && Math.Abs(s.ExpertHitRatio - 14910.0 / 22656) < 1e-9, "The experts were not read.");
            Assert(Math.Abs(s.TokensPerSecond - 19.699) < 0.001 && s.ExpertBytesPerSecond == 0, "The speed was not read, or a rate was made from one reading.");
            Assert(sample.ExpertBytes == 18998074368 && Math.Abs(sample.Uptime - 8.573) < 0.001, "The reading was not kept for the next one.");

            var m = new LocalModel { Name = "a", Server = "llama-swap", Where = "WSL", Context = 212992, Segments = s };
            Assert(Ui.LocalFacts(m).SequenceEqual(new[] { "llama-swap (WSL)", "NInfer", "208K context", "19.7 tok/s" }), "The facts do not name the engine and its speed.");
            Assert(Ui.SegmentLines(m).SequenceEqual(new[] { "MoE experts: in system memory · 734 kept on the card · 66% found there", "KV cache: up to 208K tokens · rk4v4" }),
                "The segments are not stated: " + string.Join(" / ", Ui.SegmentLines(m)));

            // An answer that is not NInfer's /stats (another engine behind the same interface, an error page) gives nothing.
            Assert(Read("{\"object\":\"list\"}", null, DateTime.Now, out sample) == null && sample == null && Read("null", null, DateTime.Now, out sample) == null, "A foreign answer was read as NInfer's.");
            // A model without routed experts is not a mixture of experts, though the block is there.
            s = Read(Stats.Replace("\"hits\":14910,\"routes\":22656,\"slots\":734", "\"hits\":0,\"routes\":0,\"slots\":0"), null, DateTime.Now, out sample);
            Assert(s.ExpertsIn == null && s.ExpertHitRatio < 0 && Ui.SegmentLines(new LocalModel { Segments = s }).Single().StartsWith("KV cache"), "A dense model was stated as a mixture of experts.");
        }

        private static void ExpertRate()
        {
            var start = new DateTime(2026, 11, 26, 12, 0, 0);
            NInferEngine.Sample first, second, third;
            Read(Stats, null, start, out first);
            // Ten seconds later, 2 GiB more moved to the card in the same run: 205 MiB a second.
            string later = Stats.Replace("\"transferred_bytes\":18998074368", "\"transferred_bytes\":21145558016").Replace("\"uptime_seconds\":8.572973427", "\"uptime_seconds\":18.57");
            var s = Read(later, first, start.AddSeconds(10), out second);
            Assert(s.ExpertBytesPerSecond == (2L << 30) / 10, "The rate is not what moved over the time between the readings.");
            Assert(Ui.SegmentLines(new LocalModel { Segments = s })[0].EndsWith("205 MB/s to the card"), "The rate is not stated.");
            // The server restarted: its totals start again, and the difference with the old run means nothing.
            s = Read(Stats.Replace("\"transferred_bytes\":18998074368", "\"transferred_bytes\":5000").Replace("\"uptime_seconds\":8.572973427", "\"uptime_seconds\":2.0"), second, start.AddSeconds(20), out third);
            Assert(s.ExpertBytesPerSecond == 0 && third.ExpertBytes == 5000, "A rate was made across a restart.");
            // The same instant twice cannot divide.
            Assert(Read(later, first, start, out second).ExpertBytesPerSecond == 0, "A rate was made over no time.");
        }

        private static void KeptOptions()
        {
            var options = NInferEngine.Options(Json.ParseAny(Models));
            Assert(options.Count == 2 && options["--kv-dtype"] == "rk4v4" && options["--expert-residency"] == "host", "The two options were not read.");
            Assert(!options.Values.Contains("sk-not-to-be-kept") && !options.ContainsKey("--api-key"), "A key on the command line was kept.");
            Assert(NInferEngine.Options(Json.ParseAny("{\"data\":[]}")).Count == 0 && NInferEngine.Options(null).Count == 0 && NInferEngine.Options(Json.ParseAny("{\"data\":[{\"id\":\"x\"}]}")).Count == 0,
                "A server that lists no arguments gave options.");
            // An option given as the last word has no value to read.
            Assert(NInferEngine.Options(Json.ParseAny("{\"data\":[{\"status\":{\"args\":[\"--port\",\"1\",\"--kv-dtype\"]}}]}")).Count == 0, "An option without a value was read.");
        }

        private static void UnknownSegments()
        {
            // llama-server with --metrics: the speed alone.
            var speedOnly = new LocalModel { Name = "a", Server = "llama.cpp", Where = "Windows", Segments = new ModelSegments { TokensPerSecond = 42.34 } };
            Assert(Ui.LocalFacts(speedOnly).SequenceEqual(new[] { "llama.cpp", "42.3 tok/s" }) && Ui.SegmentLines(speedOnly).Count == 0, "A speed alone is stated with segments it does not have.");
            Assert(Ui.SegmentLines(new LocalModel { Name = "b" }).Count == 0, "A model whose engine says nothing has segments.");
            Assert(LocalProvider.Metric("llamacpp:predicted_tokens_seconds 31.5\n", "llamacpp:predicted_tokens_seconds") == 31.5 && LocalProvider.Metric("<html>404</html>", "llamacpp:predicted_tokens_seconds") == null
                && LocalProvider.Metric(null, "x") == null, "A metric was not read, or one was read from a page that has none.");
            Assert(new ModelSegments().Signature != new ModelSegments { TokensPerSecond = 20 }.Signature, "A change of speed does not redraw the card.");
        }

        private static void Translations()
        {
            var keys = new[] { "MoE experts: {0}", "in system memory", "in video memory", "on disk", "{0} kept on the card", "{0}% found there", "{0}/s to the card", "KV cache: {0}", "up to {0} tokens" };
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
    }
}
