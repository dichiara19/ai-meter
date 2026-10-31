using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace AIMeter
{
    /// <summary>A model the service offers at no cost, with the facts needed to pick one.</summary>
    internal sealed class FreeModel
    {
        public string Id, Name;
        public long Context;        // Tokens; 0 when not reported.
        public string Inputs;       // Input modalities, such as "text, image".
        public bool Tools, Reasoning;
        public DateTime? Expires;   // Last day the model is listed, when the service announces it.
    }

    /// <summary>
    /// Free text models from an OpenRouter-format model list (GET /api/v1/models, public): zero prompt and completion
    /// price, with the announced expiration date. Routers such as openrouter/free are not models and are skipped.
    /// </summary>
    internal static class FreeModels
    {
        public static List<FreeModel> Parse(object doc)
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
                var f = new FreeModel { Id = id, Name = Json.Str(m, "name") ?? id, Context = (long)(Json.Num(m, "context_length") ?? 0), Expires = Day(Json.Str(m, "expiration_date")) };
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
                list.Add(f);
            }
            // Expiring models first, soonest first; then by name.
            return list.OrderBy(f => f.Expires ?? DateTime.MaxValue).ThenBy(f => f.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }

        /// <summary>"2026-10-31" as a calendar day; other ISO forms through Json.FromIso.</summary>
        private static DateTime? Day(string s)
        {
            if (string.IsNullOrEmpty(s)) return null;
            DateTime d;
            if (DateTime.TryParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out d)) return d.Date;
            var t = Json.FromIso(s);
            return t.HasValue ? t.Value.Date : (DateTime?)null;
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
