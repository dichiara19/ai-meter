using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace AIMeter
{
    /// <summary>A model's total weights (parameters) and where the number comes from: "huggingface" or "model_id".</summary>
    internal sealed class ModelSize
    {
        public long Parameters;
        public string Source;
    }

    /// <summary>
    /// How large a model is, for the order of delegation candidates (Candidates.Ordered). No service lists states it,
    /// so it comes from two places, the first that knows:
    /// - Hugging Face: when a model list names the model's repository (OpenRouter's hugging_face_id), the total number
    ///   of parameters in its published weights (safetensors.total of huggingface.co's public model API, read with no
    ///   key). A number is kept for good in model-sizes.json next to the settings, since published weights do not
    ///   change; a repository that gives none (gated, private or without safetensors) is asked again after RetryDays.
    /// - The model id, when it states a size as most open models do ("gemma-4-31b-it", "lfm-3-1.7b", "8x7b").
    /// Otherwise the size is unknown. Lookups run in the background, one repository at a time, after a model list is
    /// read: list_candidates never waits for the network and uses what is known when it is called.
    /// </summary>
    internal static class ModelSizes
    {
        private const int SchemaVersion = 1, RetryDays = 7;
        private const string ApiBase = "https://huggingface.co/api/models/";

        /// <summary>Both patterns are simple; the timeout only bounds a pathological id from a model list.</summary>
        private static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(100);

        private static readonly object Sync = new object();
        private static Dictionary<string, Entry> entries;   // Repository id, case-insensitive, to what it gave.
        private static int looking;                           // 1 while a background lookup runs.

        private sealed class Entry
        {
            public long? Parameters;
            public DateTime CheckedUtc;
        }

        private static string FilePath { get { return Path.Combine(Settings.Folder, "model-sizes.json"); } }

        /// <summary>A repository id as "owner/name", the only form sent to Hugging Face.</summary>
        private static readonly Regex Repository = new Regex(@"^[A-Za-z0-9][A-Za-z0-9._-]{0,95}/[A-Za-z0-9][A-Za-z0-9._-]{0,95}$", RegexOptions.None, MatchTimeout);

        /// <summary>
        /// A size written in a model id: a number followed by b (billion) or t (trillion), on its own between separators,
        /// optionally as experts × size. The first one is the total: "-30b-a3b" is 30B with 3B active, and the "a3b" is
        /// skipped because a letter precedes it.
        /// </summary>
        private static readonly Regex IdSize = new Regex(@"(?<![a-z0-9.])(\d+(?:\.\d+)?)(?:x(\d+(?:\.\d+)?))?([bt])(?![a-z0-9])", RegexOptions.CultureInvariant, MatchTimeout);

        /// <summary>The model's total weights, or null when neither Hugging Face nor its id states them.</summary>
        public static ModelSize Of(FreeModel m)
        {
            if (m == null) return null;
            if (m.HuggingFaceId != null)
                lock (Sync)
                {
                    Entry e;
                    if (Entries().TryGetValue(m.HuggingFaceId, out e) && e.Parameters > 0) return new ModelSize { Parameters = e.Parameters.Value, Source = "huggingface" };
                }
            long? fromId = FromId(m.Id);
            return fromId.HasValue ? new ModelSize { Parameters = fromId.Value, Source = "model_id" } : null;
        }

        /// <summary>The size an id states, in parameters; only the part after the last "/" is read.</summary>
        internal static long? FromId(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            string name = id.Substring(id.LastIndexOf('/') + 1).ToLowerInvariant();
            Match match;
            try { match = IdSize.Match(name); }
            catch (RegexMatchTimeoutException) { return null; }
            if (!match.Success) return null;
            double size = double.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
            if (match.Groups[2].Success) size *= double.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
            size *= match.Groups[3].Value == "t" ? 1e12 : 1e9;
            return size > 0 ? (long)size : (long?)null;
        }

        /// <summary>"560.5B", "1.7B" or "1.2T".</summary>
        public static string Format(long parameters)
        {
            return parameters >= 1e12 ? (parameters / 1e12).ToString("0.#", CultureInfo.InvariantCulture) + "T"
                : (parameters / 1e9).ToString(parameters >= 1e10 ? "0" : "0.#", CultureInfo.InvariantCulture) + "B";
        }

        /// <summary>
        /// Look up, in the background, the repositories of these models that are not known yet, or that gave nothing more
        /// than RetryDays ago. Called after a model list is read, which already needs the network to be allowed.
        /// </summary>
        public static void Learn(IEnumerable<FreeModel> models)
        {
            var wanted = Wanted(models, DateTime.UtcNow);
            if (wanted.Count == 0 || Interlocked.CompareExchange(ref looking, 1, 0) != 0) return;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try { LookUp(wanted); }
                catch (Exception ex) { Program.LogError("model sizes", ex); }
                finally { Interlocked.Exchange(ref looking, 0); }
            });
        }

        /// <summary>
        /// The repositories to ask for: plain "owner/name" ids, once each, that are not known yet or gave nothing more
        /// than RetryDays before nowUtc. Anything else a model list puts in hugging_face_id never reaches a URL.
        /// </summary>
        internal static List<string> Wanted(IEnumerable<FreeModel> models, DateTime nowUtc)
        {
            lock (Sync)
            {
                var known = Entries();
                return models.Select(m => m.HuggingFaceId).Where(IsRepository).Distinct(StringComparer.OrdinalIgnoreCase)
                    .Where(id =>
                    {
                        Entry e;
                        return !known.TryGetValue(id, out e) || (e.Parameters == null && nowUtc - e.CheckedUtc > TimeSpan.FromDays(RetryDays));
                    }).ToList();
            }
        }

        private static bool IsRepository(string id)
        {
            try { return id != null && Repository.IsMatch(id); }
            catch (RegexMatchTimeoutException) { return false; }
        }

        /// <summary>
        /// Ask Hugging Face for each repository. A refusal or a missing repository (401, 403, 404) is remembered as no
        /// number; any other failure stops the round, to be tried again after the next model list.
        /// </summary>
        private static void LookUp(List<string> repositories)
        {
            foreach (var id in repositories)
            {
                long? parameters;
                try { parameters = ParseTotal(Get(ApiBase + id + "?expand%5B%5D=safetensors")); }
                catch (WebException e)
                {
                    var response = e.Response as HttpWebResponse;
                    int status = response == null ? 0 : (int)response.StatusCode;
                    if (status != 401 && status != 403 && status != 404) return;
                    parameters = null;
                }
                catch (IOException) { return; }
                Remember(id, parameters, DateTime.UtcNow);
            }
        }

        private static string Get(string url)
        {
            var r = (HttpWebRequest)WebRequest.Create(url);
            r.Accept = "application/json";
            r.UserAgent = "ai-meter/" + Controller.Version;
            r.Timeout = r.ReadWriteTimeout = 15000;
            using (var resp = (HttpWebResponse)r.GetResponse())
            using (var sr = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                return sr.ReadToEnd();
        }

        /// <summary>safetensors.total of a model API answer; null when the answer has none.</summary>
        internal static long? ParseTotal(string json)
        {
            var total = Json.Num(Json.Obj(Json.Parse(json), "safetensors"), "total");
            return total > 0 ? (long?)(long)total.Value : null;
        }

        /// <summary>Keep what a repository gave (null for nothing) and save the file.</summary>
        internal static void Remember(string repository, long? parameters, DateTime checkedUtc)
        {
            lock (Sync)
            {
                Entries()[repository] = new Entry { Parameters = parameters, CheckedUtc = checkedUtc };
                Save();
            }
        }

        /// <summary>Forget everything, in memory only; the file is read again when next needed. For tests.</summary>
        internal static void Reset()
        {
            lock (Sync) entries = null;
        }

        /// <summary>The entries, read from the file the first time. Called with the lock held.</summary>
        private static Dictionary<string, Entry> Entries()
        {
            if (entries != null) return entries;
            entries = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
            try
            {
                if (!File.Exists(FilePath)) return entries;
                var doc = Json.ParseFile(FilePath);
                if (Json.Num(doc, "version") != SchemaVersion) return entries;
                foreach (var e in Json.Items(doc, "models"))
                {
                    string id = Json.Str(e, "repository");
                    DateTime checkedAt;
                    if (id == null || !DateTime.TryParse(Json.Str(e, "checked_at"), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out checkedAt)) continue;
                    double? n = Json.Num(e, "parameters");
                    entries[id] = new Entry { Parameters = n > 0 ? (long?)(long)n.Value : null, CheckedUtc = DateTime.SpecifyKind(checkedAt, DateTimeKind.Utc) };
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            return entries;
        }

        /// <summary>Write the file; a failure is logged and the sizes stay in memory. Called with the lock held.</summary>
        private static void Save()
        {
            var doc = new Dictionary<string, object>
            {
                { "version", SchemaVersion },
                { "models", entries.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase).Select(kv => (object)new Dictionary<string, object>
                    {
                        { "repository", kv.Key }, { "parameters", kv.Value.Parameters },
                        { "checked_at", kv.Value.CheckedUtc.ToString("o", CultureInfo.InvariantCulture) },
                    }).ToList() },
            };
            try
            {
                Directory.CreateDirectory(Settings.Folder);
                string temp = FilePath + ".tmp";
                File.WriteAllText(temp, JsonText.Indented(doc), new UTF8Encoding(false));
                if (File.Exists(FilePath)) File.Replace(temp, FilePath, null);
                else File.Move(temp, FilePath);
            }
            catch (IOException ex) { Program.LogError("model sizes", ex); }
            catch (UnauthorizedAccessException ex) { Program.LogError("model sizes", ex); }
        }
    }
}
