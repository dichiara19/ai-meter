using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace AIMeter
{
    /// <summary>
    /// What a coding agent's own model list says about a model: whether it can call tools, its context window, its
    /// reply size and the price the agent charges it at. Values the list does not give are null or 0.
    /// </summary>
    internal sealed class AgentModel
    {
        public string Id;
        public bool? Tools, Reasoning;
        public long Context, MaxOutput;
        /// <summary>Dollars per million tokens, from the agent's list; null when it gives none.</summary>
        public double? InputPrice, OutputPrice;

        public bool Priced { get { return (InputPrice ?? 0) > 0 || (OutputPrice ?? 0) > 0; } }
    }

    /// <summary>
    /// Where an agent keeps its model list (agents/*.json, "models"): a file under its home or its npm package, its
    /// format, and the agent versions whose file layout was checked. {provider} is the alias the agent keeps the
    /// service's key under.
    /// </summary>
    internal sealed class AgentModelSource
    {
        public string Format, File;
        public string[] Versions = new string[0];

        public static AgentModelSource FromJson(Dictionary<string, object> d)
        {
            string format = Json.Str(d, "format"), file = Json.Str(d, "file");
            if (format == null || file == null || !AgentModels.Formats.Contains(format)) return null;
            // Only a file under the agent's home or package, without stepping out of it.
            if (!(file.StartsWith("{home}/", StringComparison.Ordinal) || file.StartsWith("{package}/", StringComparison.Ordinal)) || file.Split('/').Contains("..")) return null;
            return new AgentModelSource { Format = format, File = file, Versions = ServiceDefinition.Strings(d, "versions").ToArray() };
        }

        /// <summary>Versions in the same notation as dispatch places; no versions means any.</summary>
        public bool Covers(string version)
        {
            return Versions.Length == 0 || new DispatchPlace { Versions = Versions }.Verifies(version);
        }
    }

    /// <summary>
    /// Coding agents' own model lists, read from their files without running them, to fill in what a service's list
    /// leaves out (docs/MCP.md "Best-effort candidates"). This is the agent's knowledge, not the service's: candidates
    /// built on it are reported apart, as best effort. Two formats:
    /// - "pi": Pi's bundled catalogue, one file per provider, entries with id, provider, type ("chat" models are the
    ///   ones Pi runs as a coding agent, with tool calling), contextWindow, maxTokens, reasoning and cost;
    /// - "models.dev": the models.dev catalogue OpenCode caches, providers with models carrying tool_call, limit and cost.
    /// </summary>
    internal static class AgentModels
    {
        public static readonly string[] Formats = { "pi", "models.dev" };

        private static readonly Dictionary<string, Tuple<DateTime, Dictionary<string, Dictionary<string, AgentModel>>>> Cache =
            new Dictionary<string, Tuple<DateTime, Dictionary<string, Dictionary<string, AgentModel>>>>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// The agent's model list file for a provider alias, as this PC sees it; null when the agent declares none, its
        /// version is outside the checked range, or the file is not there.
        /// </summary>
        public static string FileFor(RuntimeKind kind, string home, string package, string provider, string version)
        {
            var source = kind.Models;
            if (source == null || provider == null || !Regex.IsMatch(provider, @"^[A-Za-z0-9][A-Za-z0-9._-]*$")) return null;
            if (!source.Covers(version)) return null;
            string root = source.File.StartsWith("{home}/", StringComparison.Ordinal) ? home : package;
            if (root == null) return null;
            string relative = source.File.Substring(source.File.IndexOf('/') + 1).Replace("{provider}", provider);
            string path = Path.Combine(root, RuntimeKind.Local(relative));
            try { return File.Exists(path) ? path : null; }
            catch (ArgumentException) { return null; }
        }

        /// <summary>What the agent's list says about a model of a provider; null when it does not list the model.</summary>
        public static AgentModel Find(RuntimeAccess access, string model)
        {
            if (access == null || access.ModelsFile == null || model == null) return null;
            var byProvider = Load(access.ModelsFile, access.ModelsFormat);
            Dictionary<string, AgentModel> models;
            AgentModel m;
            return byProvider != null && byProvider.TryGetValue(access.Provider ?? "", out models) && models.TryGetValue(model, out m) ? m : null;
        }

        /// <summary>A file's models by provider alias, cached until the file changes.</summary>
        private static Dictionary<string, Dictionary<string, AgentModel>> Load(string file, string format)
        {
            DateTime written;
            try { written = File.GetLastWriteTimeUtc(file); }
            catch (IOException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
            lock (Cache)
            {
                Tuple<DateTime, Dictionary<string, Dictionary<string, AgentModel>>> hit;
                if (Cache.TryGetValue(file, out hit) && hit.Item1 == written) return hit.Item2;
            }
            var parsed = Parse(Json.ParseFile(file), format);
            lock (Cache) Cache[file] = Tuple.Create(written, parsed);
            return parsed;
        }

        /// <summary>Models by provider alias, then by model id; null when the document is not in the format.</summary>
        internal static Dictionary<string, Dictionary<string, AgentModel>> Parse(Dictionary<string, object> doc, string format)
        {
            if (doc == null) return null;
            var result = new Dictionary<string, Dictionary<string, AgentModel>>(StringComparer.OrdinalIgnoreCase);
            Action<string, AgentModel> add = (provider, m) =>
            {
                Dictionary<string, AgentModel> models;
                if (!result.TryGetValue(provider, out models)) result[provider] = models = new Dictionary<string, AgentModel>(StringComparer.Ordinal);
                if (!models.ContainsKey(m.Id)) models[m.Id] = m;
            };
            if (format == "pi") foreach (var e in PiEntries(doc, 0)) add(Json.Str(e, "provider"), FromPi(e));
            else if (format == "models.dev")
            {
                foreach (var p in doc)
                {
                    var models = Json.Obj(p.Value as Dictionary<string, object>, "models");
                    if (models == null) continue;
                    foreach (var m in models)
                    {
                        var e = m.Value as Dictionary<string, object>;
                        if (e != null) add(p.Key, FromModelsDev(Json.Str(e, "id") ?? m.Key, e));
                    }
                }
            }
            else return null;
            return result;
        }

        /// <summary>Pi's entries: objects with an id and a provider, at any depth (Pi groups them by API).</summary>
        private static IEnumerable<Dictionary<string, object>> PiEntries(Dictionary<string, object> node, int depth)
        {
            if (depth > 4) yield break;
            foreach (var v in node.Values.OfType<Dictionary<string, object>>())
            {
                if (Json.Str(v, "id") != null && Json.Str(v, "provider") != null) yield return v;
                else foreach (var e in PiEntries(v, depth + 1)) yield return e;
            }
        }

        private static AgentModel FromPi(Dictionary<string, object> e)
        {
            string type = Json.Str(e, "type");
            var cost = Json.Obj(e, "cost");
            return new AgentModel
            {
                Id = Json.Str(e, "id"),
                Tools = type == null ? (bool?)null : type == "chat",
                Reasoning = e.ContainsKey("reasoning") ? Json.Bool(e, "reasoning") : (bool?)null,
                Context = (long)(Json.Num(e, "contextWindow") ?? 0), MaxOutput = (long)(Json.Num(e, "maxTokens") ?? 0),
                InputPrice = Json.Num(cost, "input"), OutputPrice = Json.Num(cost, "output"),
            };
        }

        private static AgentModel FromModelsDev(string id, Dictionary<string, object> e)
        {
            var limit = Json.Obj(e, "limit");
            var cost = Json.Obj(e, "cost");
            return new AgentModel
            {
                Id = id,
                Tools = e.ContainsKey("tool_call") ? Json.Bool(e, "tool_call") : (bool?)null,
                Reasoning = e.ContainsKey("reasoning") ? Json.Bool(e, "reasoning") : (bool?)null,
                Context = (long)(Json.Num(limit, "context") ?? 0), MaxOutput = (long)(Json.Num(limit, "output") ?? 0),
                InputPrice = Json.Num(cost, "input"), OutputPrice = Json.Num(cost, "output"),
            };
        }
    }
}
