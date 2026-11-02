using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace AIMeter
{
    /// <summary>
    /// Discover existing API keys in the coding agents of agents/*.json (OpenCode, Pi, Crush and any agent a definition
    /// gives key sources) and shell profiles on Windows and running WSL.
    /// Provider definitions declare the service aliases used by those apps in auth.ids.
    /// Resolve environment references using auth.env and exports from the same home directory.
    /// Never execute command values; discovered credentials are used only for their configured service.
    /// </summary>
    internal static class KeyFinder
    {
        /// <summary>
        /// A discovered key: Source is the display text, which names WSL when the key is there; App (the agent's name,
        /// such as "OpenCode", or "profile") says where; Alias is the provider id the app keeps it under (null for a
        /// profile export).
        /// </summary>
        internal sealed class Found { public string Key, Source, App, Alias; }

        /// <summary>App name, home-relative path and JSON value path, with {id} standing for a service alias, in agent order.</summary>
        private static IEnumerable<string[]> Sources
        {
            get { return Agents.All.SelectMany(a => a.Keys.Select(k => new[] { a.Name, k.File, k.Path })); }
        }

        private static readonly string[] Profiles = { ".bashrc", ".zshrc", ".profile", ".bash_profile", ".zprofile", ".zshenv", @".config\fish\config.fish" };

        private sealed class Entry { public DateTime At; public Found Value; }
        private static readonly Dictionary<string, Entry> cache = new Dictionary<string, Entry>();

        /// <summary>Return the first discovered key or null, rereading files at most once per minute.</summary>
        public static Found Find(ServiceDefinition d)
        {
            if (d == null || !d.UsesKey || Settings.Current == null || !Settings.Current.FindKeys) return null;
            string wsl = Probe.WslHomeUp;
            string cacheKey = d.Id + "|" + wsl;
            lock (cache)
            {
                Entry e;
                if (cache.TryGetValue(cacheKey, out e) && (DateTime.Now - e.At).TotalSeconds < 60) return e.Value;
            }
            Found f = Search(d, Probe.Home, false) ?? (wsl != null ? Search(d, wsl, true) : null);
            lock (cache) cache[cacheKey] = new Entry { At = DateTime.Now, Value = f };
            return f;
        }

        public static void Forget() { lock (cache) cache.Clear(); }

        private static Found Search(ServiceDefinition d, string home, bool wsl) { return SearchAll(d, home, wsl).FirstOrDefault(); }

        /// <summary>Keys in one home, lazily: taking the first one reads no further files.</summary>
        internal static IEnumerable<Found> SearchAll(ServiceDefinition d, string home, bool wsl)
        {
            var ids = ServiceDefinition.Strings(d.Auth, "ids").DefaultIfEmpty(d.Id).ToList();
            string where = wsl ? " (WSL)" : "";
            foreach (var s in Sources)
            {
                var doc = ReadJson(Path.Combine(home, s[1]));
                if (doc == null) continue;
                foreach (var id in ids)
                {
                    string v = Resolve(JPath.Str(JPath.Get(doc, s[2].Replace("{id}", id))), home, wsl);
                    if (v != null) { yield return new Found { Key = v, Source = s[0] + where, App = s[0], Alias = id }; break; }
                }
            }
            // Read exports from WSL or Windows Git Bash shell profiles.
            foreach (var name in ServiceDefinition.Strings(d.Auth, "env"))
            {
                string v = FromProfiles(name, home);
                if (Plausible(v)) { yield return new Found { Key = v, Source = L.T("shell profile") + where, App = "profile" }; break; }
            }
        }

        /// <summary>Resolve a literal key or environment reference, without executing commands.</summary>
        private static string Resolve(string v, string home, bool wsl)
        {
            if (string.IsNullOrEmpty(v)) return null;
            v = v.Trim();
            if (v.StartsWith("!")) return null;
            var m = Regex.Match(v, @"^(?:\$\{?([A-Za-z_][A-Za-z0-9_]*)\}?|\{env:([A-Za-z_][A-Za-z0-9_]*)\})$");
            if (m.Success)
            {
                string name = m.Groups[1].Success && m.Groups[1].Value.Length > 0 ? m.Groups[1].Value : m.Groups[2].Value;
                string env = wsl ? null : ServiceDefinition.Env(name);
                v = !string.IsNullOrEmpty(env) ? env : FromProfiles(name, home);
            }
            return Plausible(v) ? v : null;
        }

        private static bool Plausible(string v) { return !string.IsNullOrEmpty(v) && v.Length >= 16 && !v.Any(char.IsWhiteSpace) && !v.Contains("$"); }

        /// <summary>Read bash/zsh exports or fish set -gx assignments; the last assignment wins.</summary>
        private static string FromProfiles(string name, string home)
        {
            string found = null;
            var sh = new Regex(@"^\s*(?:export\s+)?" + Regex.Escape(name) + @"=(['""]?)([^'""\s#]+)\1\s*(?:#.*)?$");
            var fish = new Regex(@"^\s*set\s+(?:-[a-zA-Z]+\s+)*" + Regex.Escape(name) + @"\s+(['""]?)([^'""\s]+)\1\s*$");
            foreach (var p in Profiles)
            {
                string text = ReadSmall(Path.Combine(home, p));
                if (text == null) continue;
                foreach (var line in text.Split('\n'))
                {
                    var m = sh.Match(line.TrimEnd('\r'));
                    if (!m.Success) m = fish.Match(line.TrimEnd('\r'));
                    if (m.Success) found = m.Groups[2].Value;
                }
            }
            return found;
        }

        private static object ReadJson(string path)
        {
            string text = ReadSmall(path);
            return text == null ? null : Json.ParseAny(text);
        }

        /// <summary>Read an existing configuration file only if it is no larger than 1 MB.</summary>
        private static string ReadSmall(string path)
        {
            try
            {
                var fi = new FileInfo(path);
                if (!fi.Exists || fi.Length > 1 << 20) return null;
                return File.ReadAllText(path, Encoding.UTF8);
            }
            catch (IOException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
            catch (ArgumentException) { return null; }
        }
    }
}
