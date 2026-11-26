using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;

namespace AIMeter
{
    /// <summary>
    /// NInfer (github.com/iamwavecut/ninfer-all): an engine that answers llama-server's /props and /slots, and says
    /// more of itself on its own read-only endpoints. From /stats come the experts of a mixture-of-experts model and
    /// the capacity of the key-value cache; from /v1/models, the options the server was started with, of which only
    /// the ones named here are read. It reports no size in bytes for either cache, so none is shown (ADR 0006,
    /// decision 7).
    /// </summary>
    internal static class NInferEngine
    {
        public const string Name = "NInfer";

        /// <summary>What is kept of one reading of a server, to turn its running totals into a rate at the next.</summary>
        internal sealed class Sample
        {
            public DateTime At;
            public double Uptime;        // Seconds since the server was ready: lower than before means it restarted.
            public long ExpertBytes;     // Bytes of experts moved to the card since then.
        }

        private const string KvTypeOption = "--kv-dtype", ExpertResidencyOption = "--expert-residency";

        /// <summary>The build string of /props names the engine: "ninfer &lt;version&gt; (&lt;toolchain&gt;)".</summary>
        public static bool Is(string build)
        {
            return build != null && build.StartsWith("ninfer ", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// The options of the running server that say where its parts are, from the arguments /v1/models lists.
        /// A command line can hold a key: only these two options are looked for, and nothing else of it is kept.
        /// </summary>
        public static Dictionary<string, string> Options(object models)
        {
            var kept = new Dictionary<string, string>();
            var list = JPath.Get(models, "data") as IList;
            var arguments = list == null || list.Count == 0 ? null : JPath.Get(list[0], "status.args") as IList;
            if (arguments == null) return kept;
            for (int i = 0; i + 1 < arguments.Count; i++)
            {
                string option = arguments[i] as string;
                if (option == KvTypeOption || option == ExpertResidencyOption) kept[option] = arguments[i + 1] as string;
            }
            return kept;
        }

        /// <summary>
        /// The state of a model from its server's /stats and /metrics; null when /stats is not NInfer's. The sample
        /// goes back in at the next reading: without one, or after a restart, there is no rate yet.
        /// </summary>
        public static ModelSegments Read(object stats, Dictionary<string, string> options, string metrics, Sample previous, DateTime now, out Sample sample)
        {
            sample = null;
            if (JPath.Str(JPath.Get(stats, "object")) != "ninfer.stats") return null;
            string kvType, residency;
            options.TryGetValue(KvTypeOption, out kvType);
            options.TryGetValue(ExpertResidencyOption, out residency);
            var segments = new ModelSegments
            {
                Engine = Name,
                KvCapacity = (int)(JPath.Num(JPath.Get(stats, "capacity.kv_capacity_tokens")) ?? 0),
                KvType = kvType,
                TokensPerSecond = LocalProvider.Metric(metrics, "llamacpp:predicted_tokens_seconds") ?? 0,
            };
            double routes = JPath.Num(JPath.Get(stats, "experts.routes")) ?? 0, hits = JPath.Num(JPath.Get(stats, "experts.hits")) ?? 0;
            int slots = (int)(JPath.Num(JPath.Get(stats, "experts.slots")) ?? 0);
            long moved = (long)(JPath.Num(JPath.Get(stats, "experts.transferred_bytes")) ?? 0);
            sample = new Sample { At = now, Uptime = JPath.Num(JPath.Get(stats, "uptime_seconds")) ?? 0, ExpertBytes = moved };
            // A model without routed experts reports the block with zeros: it is not a mixture of experts.
            if (routes <= 0 && slots <= 0) return segments;

            segments.ExpertsIn = new[] { "device", "host", "disk" }.Contains(residency) ? residency : null;
            segments.ExpertSlots = slots;
            if (routes > 0) segments.ExpertHitRatio = Math.Min(1, hits / routes);
            double seconds = previous == null ? 0 : (now - previous.At).TotalSeconds;
            if (seconds > 0 && sample.Uptime > previous.Uptime && moved >= previous.ExpertBytes)
                segments.ExpertBytesPerSecond = (long)((moved - previous.ExpertBytes) / seconds);
            return segments;
        }
    }
}
