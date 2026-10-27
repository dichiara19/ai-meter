using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace AIMeter
{
    /// <summary>
    /// Chiavi API già sul PC, nei programmi che le usano (OpenCode, Pi, Crush) e nei profili della shell, su Windows e in
    /// WSL già acceso. La definizione dice con quali nomi quei programmi conoscono il servizio ("auth": { "ids": [...] });
    /// le variabili d'ambiente sono quelle di "auth.env". Un valore come "$NOME" o "{env:NOME}" si risolve con l'export
    /// della stessa casa; i comandi ("!...") non si eseguono mai. La chiave trovata va solo al servizio stesso.
    /// </summary>
    internal static class KeyFinder
    {
        internal sealed class Found { public string Key, Source; }

        /// <summary>File dei programmi: nome, percorso dalla home, percorso del valore con {id} per il nome del servizio.</summary>
        private static readonly string[][] Sources =
        {
            new[] { "OpenCode", @".local\share\opencode\auth.json", "{id}.key" },
            new[] { "OpenCode", @".config\opencode\opencode.json", "provider.{id}.options.apiKey" },
            new[] { "Pi", @".pi\agent\auth.json", "{id}.key" },
            new[] { "Pi", @".pi\agent\models.json", "providers.{id}.apiKey" },
            new[] { "Crush", @".config\crush\crush.json", "providers.{id}.api_key" },
            new[] { "Crush", @".local\share\crush\crush.json", "providers.{id}.api_key" },
        };

        private static readonly string[] Profiles = { ".bashrc", ".zshrc", ".profile", ".bash_profile", ".zprofile", ".zshenv", @".config\fish\config.fish" };

        private sealed class Entry { public DateTime At; public Found Value; }
        private static readonly Dictionary<string, Entry> cache = new Dictionary<string, Entry>();

        /// <summary>La prima chiave trovata per il servizio, o null. Rilegge i file al massimo una volta al minuto.</summary>
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

        private static Found Search(ServiceDefinition d, string home, bool wsl)
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
                    if (v != null) return new Found { Key = v, Source = s[0] + where };
                }
            }
            // variabili d'ambiente esportate dai profili della shell (in WSL, o in Git Bash su Windows)
            foreach (var name in ServiceDefinition.Strings(d.Auth, "env"))
            {
                string v = FromProfiles(name, home);
                if (Plausible(v)) return new Found { Key = v, Source = L.T("shell profile") + where };
            }
            return null;
        }

        /// <summary>Il valore come chiave: letterale, oppure riferimento a una variabile; mai un comando.</summary>
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

        /// <summary>export NOME=valore (bash, zsh, profile) o set -gx NOME valore (fish); vale l'ultima assegnazione.</summary>
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

        /// <summary>Un file di configurazione: solo se c'è ed è piccolo (al massimo 1 MB).</summary>
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
