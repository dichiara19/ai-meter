using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace AIMeter
{
    /// <summary>
    /// A coding agent AI Meter knows (agents/*.json, see docs/MCP.md "Agent definitions"): where it keeps its settings
    /// and keys, which program shows it is installed in each place, and, for agents delegation can run, how a plugin
    /// starts it. An agent without a dispatch entry for a place is reported as found but unsupported there, never
    /// guessed.
    /// </summary>
    internal sealed class RuntimeKind
    {
        public string Id, Name;
        /// <summary>Position in key discovery and in lists: lower first, then by id.</summary>
        public int Order = 1000;
        /// <summary>Settings folders relative to a home, with Windows separators; any one shows the agent is set up.</summary>
        public string[] Folders = new string[0];
        /// <summary>Program names per place ("windows" or "wsl"), without an extension.</summary>
        public readonly Dictionary<string, string[]> Programs = new Dictionary<string, string[]>();
        /// <summary>Where the agent keeps API keys: a home-relative file and a value path with {id} for a service alias.</summary>
        public readonly List<KeySource> Keys = new List<KeySource>();
        /// <summary>npm package, to read the version without running the agent; null when not installed through npm.</summary>
        public string Package;
        /// <summary>How a plugin starts it; null when delegation does not support the agent at all.</summary>
        public AgentDispatch Dispatch;
        /// <summary>"built-in" or the user file the definition came from.</summary>
        public string Origin;

        public string[] ProgramsIn(string where)
        {
            string[] names;
            return Programs.TryGetValue(where, out names) ? names : new string[0];
        }

        /// <summary>The dispatch entry for a place, or null when that place has not been verified.</summary>
        public DispatchPlace PlaceFor(string where)
        {
            DispatchPlace place;
            return Dispatch != null && Dispatch.Places.TryGetValue(where, out place) ? place : null;
        }

        /// <summary>Parse a definition; null when it lacks a valid id or a program name is not a plain file name.</summary>
        public static RuntimeKind FromJson(string text, string origin)
        {
            var d = Json.Parse(text);
            string id = Json.Str(d, "id");
            if (d == null || string.IsNullOrEmpty(id) || !Regex.IsMatch(id, "^[a-z0-9][a-z0-9-]{1,31}$")) return null;
            var kind = new RuntimeKind { Id = id, Name = Json.Str(d, "name") ?? id, Package = Json.Str(d, "package"), Origin = origin };
            kind.Order = (int)(Json.Num(d, "order") ?? kind.Order);
            kind.Folders = ServiceDefinition.Strings(d, "folders").Select(Local).ToArray();
            var programs = Json.Obj(d, "programs");
            foreach (var where in new[] { "windows", "wsl" })
            {
                var names = ServiceDefinition.Strings(programs, where).ToArray();
                if (names.Any(n => !Regex.IsMatch(n, @"^[A-Za-z0-9][A-Za-z0-9._-]*$"))) return null;
                if (names.Length > 0) kind.Programs[where] = names;
            }
            foreach (var k in Objects(d, "keys"))
                if (Json.Str(k, "file") != null && Json.Str(k, "path") != null)
                    kind.Keys.Add(new KeySource { File = Local(Json.Str(k, "file")), Path = Json.Str(k, "path") });
            var dispatch = Json.Obj(d, "dispatch");
            if (dispatch != null) kind.Dispatch = AgentDispatch.FromJson(dispatch);
            return kind;
        }

        /// <summary>A home-relative path written with forward slashes, as Windows sees it.</summary>
        internal static string Local(string path) { return path.Replace('/', '\\').TrimStart('\\'); }

        internal static IEnumerable<Dictionary<string, object>> Objects(Dictionary<string, object> d, string key)
        {
            object v;
            var list = d != null && d.TryGetValue(key, out v) ? v as IEnumerable : null;
            return list == null || list is string ? Enumerable.Empty<Dictionary<string, object>>() : list.OfType<Dictionary<string, object>>();
        }
    }

    internal sealed class KeySource { public string File, Path; }

    /// <summary>
    /// How a plugin starts an agent: per verified place, the version range and (optionally) its own arguments and
    /// environment; otherwise the shared ones. Arguments are kept per mode (read_only, modify). Values may hold the
    /// placeholders {provider}, {model} and {workspace}; an environment value written as a JSON object is serialised
    /// after the placeholders are filled, so a model id cannot change its structure.
    /// </summary>
    internal sealed class AgentDispatch
    {
        public readonly Dictionary<string, DispatchPlace> Places = new Dictionary<string, DispatchPlace>();
        public Dictionary<string, List<string>> Arguments = new Dictionary<string, List<string>>();
        public Dictionary<string, object> Environment = new Dictionary<string, object>();
        public readonly Dictionary<string, string> Output = new Dictionary<string, string>();
        public readonly List<string> Notes = new List<string>();

        public static AgentDispatch FromJson(Dictionary<string, object> d)
        {
            var dispatch = new AgentDispatch
            {
                Arguments = ArgumentsFrom(Json.Obj(d, "arguments")) ?? new Dictionary<string, List<string>>(),
                Environment = Json.Obj(d, "environment") ?? new Dictionary<string, object>(),
            };
            var places = Json.Obj(d, "places");
            foreach (var where in new[] { "windows", "wsl" })
            {
                var p = Json.Obj(places, where);
                if (p == null) continue;
                dispatch.Places[where] = new DispatchPlace
                {
                    Versions = ServiceDefinition.Strings(p, "versions").ToArray(),
                    Executables = ServiceDefinition.Strings(p, "executables").Select(RuntimeKind.Local).ToArray(),
                    Arguments = ArgumentsFrom(Json.Obj(p, "arguments")),
                    Environment = Json.Obj(p, "environment"),
                };
            }
            var output = Json.Obj(d, "output");
            if (output != null) foreach (var kv in output) if (kv.Value is string) dispatch.Output[kv.Key] = (string)kv.Value;
            dispatch.Notes.AddRange(ServiceDefinition.Strings(d, "notes"));
            return dispatch;
        }

        private static Dictionary<string, List<string>> ArgumentsFrom(Dictionary<string, object> d)
        {
            return d == null ? null : d.ToDictionary(kv => kv.Key, kv => ServiceDefinition.Strings(d, kv.Key).ToList());
        }
    }

    internal sealed class DispatchPlace
    {
        /// <summary>Verified versions: "1.x" matches every 1.* version, "1.4.0" only that one.</summary>
        public string[] Versions = new string[0];
        /// <summary>Native Windows programs relative to the npm package folder, for agents npm installs behind a .cmd wrapper.</summary>
        public string[] Executables = new string[0];
        /// <summary>This place's own arguments or environment; null to use the shared ones.</summary>
        public Dictionary<string, List<string>> Arguments;
        public Dictionary<string, object> Environment;

        public bool Verifies(string version)
        {
            return version != null && Versions.Any(v => v.EndsWith("x", StringComparison.Ordinal)
                ? version.StartsWith(v.Substring(0, v.Length - 1), StringComparison.Ordinal)
                : version == v);
        }
    }

    /// <summary>Load the built-in agent definitions and the user's from %APPDATA%\AIMeter\agents, like Definitions.</summary>
    internal static class Agents
    {
        private static List<RuntimeKind> all;

        public static string UserFolder { get { return Path.Combine(Settings.Folder, "agents"); } }

        public static IList<RuntimeKind> All { get { if (all == null) all = Load(); return all; } }

        public static RuntimeKind Get(string id) { return All.FirstOrDefault(k => k.Id == id); }

        /// <summary>Read the definitions again, after the user folder changed.</summary>
        internal static void Reload() { all = null; }

        private static List<RuntimeKind> Load()
        {
            var byId = new Dictionary<string, RuntimeKind>();
            Action<string, string> add = (text, origin) =>
            {
                RuntimeKind k;
                try { k = RuntimeKind.FromJson(text, origin); }
                catch (Exception) { k = null; }
                if (k != null) byId[k.Id] = k;   // A user definition replaces the built-in one with the same id.
            };
            var asm = typeof(Agents).Assembly;
            foreach (var n in asm.GetManifestResourceNames().Where(n => n.StartsWith("agents.") && n.EndsWith(".json")).OrderBy(n => n))
                using (var s = asm.GetManifestResourceStream(n))
                using (var r = new StreamReader(s, Encoding.UTF8)) add(r.ReadToEnd(), "built-in");
            foreach (var dir in new[] { Path.Combine(Program.Home, "agents"), UserFolder })
            {
                try
                {
                    if (!Directory.Exists(dir)) continue;
                    foreach (var f in Directory.GetFiles(dir, "*.json").OrderBy(f => f))
                        add(File.ReadAllText(f, Encoding.UTF8), dir == UserFolder ? f : "built-in");
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            return byId.Values.OrderBy(k => k.Order).ThenBy(k => k.Id, StringComparer.Ordinal).ToList();
        }
    }
}
