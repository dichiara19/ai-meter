using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace AIMeter
{
    /// <summary>A model the service offers, possibly at no cost, with the facts needed to pick one.</summary>
    internal sealed class FreeModel
    {
        public string Id, Name;
        public long Context;        // Tokens; 0 when not reported.
        public long MaxOutput;      // Tokens in one reply; 0 when not reported.
        public string Inputs;       // Input modalities, such as "text, image".
        public bool Tools, Reasoning;
        public bool FeaturesUnknown;   // The list does not say whether the model supports tools or reasoning.
        public DateTime? Expires;   // Last day the model is listed, when the service announces it.
        public string HuggingFaceId;   // The model's Hugging Face repository ("owner/name"), when the list names it.
        public FreeStatus Free;
    }

    /// <summary>Whether a model costs nothing and how that is known. A key that works proves nothing about price.</summary>
    internal sealed class FreeStatus
    {
        public string Status;       // "confirmed_free", "paid" or "unknown".
        public string Kind;         // "zero_price", "free_tier", "promotional_credit" or "local"; null when unknown.
        public string Source;       // "catalogue" (prices in the list), "documented" or "configured".
        public string Reference;    // Address of the documentation, for documented facts.
        public DateTime? CheckedAt; // When the documentation was last checked.
        public string Note;         // Why the status is unknown, in English, for agents.

        public bool Confirmed { get { return Status == "confirmed_free"; } }

        public static readonly FreeStatus ZeroPrice = new FreeStatus { Status = "confirmed_free", Kind = "zero_price", Source = "catalogue" };
    }

    /// <summary>
    /// One rate limit of a service's free models (docs/plans/orchestration-contracts.md §2.3). Unknown values are null:
    /// a limit the service has but AI Meter cannot read keeps its metric with Source "unknown", never a made-up number.
    /// </summary>
    internal sealed class RateLimit
    {
        public string Metric;       // requests_per_minute, requests_per_day, tokens_per_minute, tokens_per_day, concurrent_requests…
        public double? Limit, Used, Remaining;
        public DateTime? ResetsAt;
        public string ResetZone;    // IANA zone of a calendar reset, such as "UTC" or "America/Los_Angeles".
        public string Scope;        // "account", "project" or "model".
        public bool PerModel;       // Counted for each model separately, within the scope.
        public string Source;       // "provider_reported", "documented", "configured" or "unknown".
        public string Reference, Note;
        public DateTime? CheckedAt;

        /// <summary>What the value covers: a provider's own count includes every client; nothing else does.</summary>
        public string Coverage { get { return Source == "provider_reported" ? "all_clients" : null; } }
    }

    /// <summary>Calendar resets in a named time zone, following its daylight-saving rules.</summary>
    internal static class Zones
    {
        /// <summary>IANA names used by service definitions, and the Windows zones .NET Framework knows them by.</summary>
        private static readonly Dictionary<string, string> WindowsIds = new Dictionary<string, string>
        {
            { "America/Los_Angeles", "Pacific Standard Time" },
        };

        /// <summary>The next midnight in the zone after nowUtc, in UTC; null for a zone not listed here.</summary>
        public static DateTime? NextMidnightUtc(DateTime nowUtc, string zone)
        {
            TimeZoneInfo tz;
            string id;
            if (zone == "UTC") tz = TimeZoneInfo.Utc;
            else if (zone != null && WindowsIds.TryGetValue(zone, out id))
            {
                try { tz = TimeZoneInfo.FindSystemTimeZoneById(id); }
                catch (TimeZoneNotFoundException) { return null; }
                catch (InvalidTimeZoneException) { return null; }
            }
            else return null;
            var midnight = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(nowUtc, DateTimeKind.Utc), tz).Date.AddDays(1);
            while (tz.IsInvalidTime(midnight)) midnight = midnight.AddHours(1);   // A midnight skipped by a clock change.
            return TimeZoneInfo.ConvertTimeToUtc(midnight, tz);
        }

        /// <summary>The next midnight in the zone, in local time.</summary>
        public static DateTime? NextMidnight(DateTime nowLocal, string zone)
        {
            var utc = NextMidnightUtc(nowLocal.ToUniversalTime(), zone);
            return utc.HasValue ? utc.Value.ToLocalTime() : (DateTime?)null;
        }
    }

    /// <summary>
    /// A service's models and when the list was read, with the rate limits of its free models. Pool names the counter
    /// the limits draw from; "{model}" in it stands for each model's id when every model has its own counter.
    /// </summary>
    internal sealed class Catalogue
    {
        public List<FreeModel> Models;
        public DateTime ReadAt;
        public string Pool;
        public List<RateLimit> Rates = new List<RateLimit>();

        public string PoolOf(FreeModel m) { return Pool == null ? null : Pool.Replace("{model}", m.Id); }

        /// <summary>The free allocation the models draw from, for a free tier; null for models that cost nothing.</summary>
        public Allowance Allowance;
    }

    /// <summary>
    /// A free tier's daily allocation ("freeModels.allowance"): the rate limit that counts it, its unit, what one unit
    /// costs beyond the allocation, and a reserve kept back from delegated tasks. Cloudflare's models are priced, but the
    /// first 10,000 Neurons of each UTC day are free: a task is free only while it stays within what is left.
    /// </summary>
    internal sealed class Allowance
    {
        public string Metric;       // The rate limit that counts the allocation, such as neurons_per_day.
        public string Unit;         // Such as "neurons".
        public double UsdPerUnit;   // Price of one unit beyond the allocation, in US dollars.
        public double Reserve;      // Units never offered to a task: they absorb the request that crosses the limit and reporting lag.

        public static Allowance FromJson(Dictionary<string, object> d)
        {
            if (d == null) return null;
            string metric = Json.Str(d, "metric");
            double? price = Json.Num(d, "usdPerUnit");
            if (metric == null || price == null || !(price.Value > 0)) return null;
            return new Allowance { Metric = metric, Unit = Json.Str(d, "unit") ?? metric, UsdPerUnit = price.Value, Reserve = Math.Max(0, Json.Num(d, "reserve") ?? 0) };
        }
    }

    /// <summary>
    /// What a free tier leaves for a task now (Candidates.BudgetOf). Used is the larger of the service's count and the
    /// cost released with today's leases, since the service's count can lag behind tasks that just ended. Spendable and
    /// MaxCost are null when the allocation or its use is unknown, which closes the pool: an unknown budget is not room.
    /// </summary>
    internal sealed class Budget
    {
        public Allowance Allowance;
        public double? Limit, Used, Spendable, MaxCost;
        public double LeasedCostToday;
        public DateTime? ResetsAt;
    }

    /// <summary>
    /// Model lists in the formats services use. From an OpenRouter-format list (GET /api/v1/models, public) only text
    /// models with a zero prompt and completion price are kept, with the announced expiration date; routers such as
    /// openrouter/free are not models and are skipped.
    /// </summary>
    internal static class FreeModels
    {
        /// <summary>
        /// Models from a model list in the given format; null when the answer is not such a list. "openrouter" lists
        /// prices, so it keeps only models that cost nothing. "openai" (ids only, as NVIDIA's) and "gemini" (token
        /// limits, no prices) list every chat model, and the given status says what is known about their price.
        /// "models.dev" is the public models.dev catalogue: provider names the entry to read, and the given status
        /// applies to its models, whose prices are those beyond a free tier (Cloudflare's).
        /// </summary>
        public static List<FreeModel> Parse(object doc, string format, FreeStatus unpriced = null, string provider = null)
        {
            var status = unpriced ?? new FreeStatus { Status = "unknown" };
            switch (format ?? "openrouter")
            {
                case "openrouter": return ParseOpenRouter(doc);
                case "openai": return ParseOpenAi(doc, status);
                case "gemini": return ParseGemini(doc, status);
                case "models.dev": return ParseModelsDev(doc, provider, status);
                default: return null;
            }
        }

        /// <summary>
        /// One provider of the models.dev catalogue ({ provider: { models: { id: { name, tool_call, reasoning, limit,
        /// modalities, status } } } }): models that answer in text and are not deprecated, with what the catalogue
        /// states about tools, reasoning, context and reply size. Null when the provider is not in the answer.
        /// </summary>
        private static List<FreeModel> ParseModelsDev(object doc, string provider, FreeStatus status)
        {
            var root = doc as Dictionary<string, object>;
            var models = provider == null || root == null ? null : Json.Obj(Json.Obj(root, provider), "models");
            if (models == null) return null;
            var list = new List<FreeModel>();
            foreach (var entry in models)
            {
                var m = entry.Value as Dictionary<string, object>;
                if (m == null || Json.Str(m, "status") == "deprecated" || !LooksLikeChat(entry.Key)) continue;
                var modalities = Json.Obj(m, "modalities");
                var outputs = modalities != null && modalities.ContainsKey("output") ? modalities["output"] as IList : null;
                if (outputs != null && (outputs.Count == 0 || outputs.OfType<string>().Any(o => o != "text"))) continue;
                var inputs = modalities != null && modalities.ContainsKey("input") ? modalities["input"] as IList : null;
                var limit = Json.Obj(m, "limit");
                string id = Json.Str(m, "id") ?? entry.Key;
                list.Add(new FreeModel
                {
                    Id = id, Name = Json.Str(m, "name") ?? id,
                    Context = (long)(Json.Num(limit, "context") ?? 0), MaxOutput = (long)(Json.Num(limit, "output") ?? 0),
                    Inputs = inputs == null ? null : string.Join(", ", inputs.OfType<string>()),
                    Tools = Json.Bool(m, "tool_call"), Reasoning = Json.Bool(m, "reasoning"),
                    FeaturesUnknown = !m.ContainsKey("tool_call"), Free = status,
                });
            }
            return list.OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }

        /// <summary>
        /// Words that mark embedding, ranking, safety, speech and image models in lists that do not say what a model
        /// produces. A heuristic: such lists give only names.
        /// </summary>
        private static readonly string[] NotChat = { "embed", "rerank", "reward", "guard", "safety", "tts", "imagen", "image-generation", "veo", "aqa", "clip" };

        private static bool LooksLikeChat(string id)
        {
            string lower = id.ToLowerInvariant();
            return !NotChat.Any(lower.Contains);
        }

        /// <summary>An OpenAI-style list (data[].id): names only, so context, output and features are unknown.</summary>
        private static List<FreeModel> ParseOpenAi(object doc, FreeStatus status)
        {
            var root = doc as Dictionary<string, object>;
            var data = root != null && root.ContainsKey("data") ? root["data"] as IList : null;
            if (data == null) return null;
            return data.OfType<Dictionary<string, object>>().Select(m => Json.Str(m, "id")).Where(id => id != null && LooksLikeChat(id))
                .Select(id => new FreeModel { Id = id, Name = id, FeaturesUnknown = true, Free = status })
                .OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }

        /// <summary>
        /// The Gemini API's list (models[]: name, displayName, inputTokenLimit, outputTokenLimit,
        /// supportedGenerationMethods, thinking): models that can generate content, with their token limits.
        /// </summary>
        private static List<FreeModel> ParseGemini(object doc, FreeStatus status)
        {
            var root = doc as Dictionary<string, object>;
            var models = root != null && root.ContainsKey("models") ? root["models"] as IList : null;
            if (models == null) return null;
            var list = new List<FreeModel>();
            foreach (var m in models.OfType<Dictionary<string, object>>())
            {
                string name = Json.Str(m, "name");
                var methods = m.ContainsKey("supportedGenerationMethods") ? m["supportedGenerationMethods"] as IList : null;
                if (name == null || methods == null || !methods.Contains("generateContent")) continue;
                string id = name.StartsWith("models/", StringComparison.Ordinal) ? name.Substring(7) : name;
                if (!LooksLikeChat(id)) continue;
                list.Add(new FreeModel
                {
                    Id = id, Name = Json.Str(m, "displayName") ?? id,
                    Context = (long)(Json.Num(m, "inputTokenLimit") ?? 0), MaxOutput = (long)(Json.Num(m, "outputTokenLimit") ?? 0),
                    Reasoning = Json.Bool(m, "thinking"), FeaturesUnknown = true, Free = status,
                });
            }
            return list.OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }

        private static List<FreeModel> ParseOpenRouter(object doc)
        {
            var root = doc as Dictionary<string, object>;
            var data = root != null && root.ContainsKey("data") ? root["data"] as IList : null;
            if (data == null) return null;
            var today = DateTime.Today;
            var list = new List<FreeModel>();
            foreach (var m in data.OfType<Dictionary<string, object>>())
            {
                string id = Json.Str(m, "id");
                var price = Json.Obj(m, "pricing");
                if (id == null || price == null || id.StartsWith("openrouter/", StringComparison.Ordinal)) continue;
                if (JPath.Num(Json.Str(price, "prompt")) != 0 || JPath.Num(Json.Str(price, "completion")) != 0) continue;
                var f = new FreeModel { Id = id, Name = Json.Str(m, "name") ?? id, Context = (long)(Json.Num(m, "context_length") ?? 0), Expires = Json.Day(Json.Str(m, "expiration_date")), Free = FreeStatus.ZeroPrice };
                if (f.Expires.HasValue && f.Expires.Value < today) continue;
                var arch = Json.Obj(m, "architecture");
                // Media generators such as Lyria list zero token prices but are not chat models.
                var outputs = arch != null && arch.ContainsKey("output_modalities") ? arch["output_modalities"] as IList : null;
                if (outputs != null && outputs.OfType<string>().Any(o => o != "text")) continue;
                var inputs = arch != null && arch.ContainsKey("input_modalities") ? arch["input_modalities"] as IList : null;
                f.Inputs = inputs == null ? null : string.Join(", ", inputs.OfType<string>());
                var parameters = m.ContainsKey("supported_parameters") ? m["supported_parameters"] as IList : null;
                f.Tools = parameters != null && parameters.Contains("tools");
                f.Reasoning = parameters != null && parameters.Contains("reasoning");
                f.MaxOutput = (long)(Json.Num(Json.Obj(m, "top_provider"), "max_completion_tokens") ?? 0);
                string repository = Json.Str(m, "hugging_face_id");
                f.HuggingFaceId = string.IsNullOrWhiteSpace(repository) ? null : repository.Trim();
                list.Add(f);
            }
            // Expiring models first, soonest first; then by name.
            return list.OrderBy(f => f.Expires ?? DateTime.MaxValue).ThenBy(f => f.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }

        /// <summary>"today", "tomorrow" or "31 Dec".</summary>
        public static string When(DateTime day)
        {
            if (day <= DateTime.Today) return L.T("today");
            if (day == DateTime.Today.AddDays(1)) return L.T("tomorrow");
            return day.ToString("d MMM", L.Culture);
        }

        public static string Context(long tokens)
        {
            if (tokens <= 0) return null;
            if (tokens >= 1000000) return (tokens / 1000000.0).ToString("0.#", CultureInfo.InvariantCulture) + "M";
            return Ui.Tokens((int)tokens);
        }

        /// <summary>One tooltip line: name, context, inputs, tools, reasoning and expiry.</summary>
        public static string Line(FreeModel f)
        {
            var parts = new List<string> { f.Name };
            string ctx = Context(f.Context);
            if (ctx != null) parts.Add(L.F("{0} context", ctx));
            if (!string.IsNullOrEmpty(f.Inputs)) parts.Add(f.Inputs);
            if (f.Tools) parts.Add(L.T("tools"));
            if (f.Reasoning) parts.Add(L.T("reasoning"));
            if (f.Expires.HasValue) parts.Add(L.F("free until {0}", When(f.Expires.Value)));
            return string.Join(" · ", parts);
        }
    }
}
