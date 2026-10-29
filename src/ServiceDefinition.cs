using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Shell;

namespace AIMeter
{
    /// <summary>
    /// A service defined in providers\*.json: authentication, usage endpoint and response mapping.
    /// The definition format is documented in docs/PROVIDERS.md.
    /// </summary>
    internal sealed class ServiceDefinition
    {
        public string Id, Name, Description, Monogram, Origin;
        public uint ColourLight = 0xFF5B5FC7, ColourDark = 0xFFA9ACF5;
        public Dictionary<string, object> Raw;

        public Dictionary<string, object> Auth { get { return Json.Obj(Raw, "auth"); } }
        public string AuthType { get { return Json.Str(Auth, "type") ?? "none"; } }
        /// <summary>Whether the service authenticates with an API key.</summary>
        public bool UsesKey { get { return AuthType == "key"; } }
        public string KeyPage { get { return Json.Str(Auth, "page"); } }

        public static ServiceDefinition FromJson(string text, string origin)
        {
            var d = Json.Parse(text);
            string id = Json.Str(d, "id");
            if (d == null || string.IsNullOrEmpty(id) || !Regex.IsMatch(id, "^[a-z0-9][a-z0-9-]{1,31}$")) return null;
            var def = new ServiceDefinition
            {
                Id = id, Raw = d, Origin = origin,
                Name = Json.Str(d, "name") ?? id,
                Description = Json.Str(d, "description"),
            };
            def.Monogram = Json.Str(d, "monogram") ?? def.Name.Substring(0, 1).ToUpperInvariant();
            var colour = Json.Obj(d, "color");
            uint c;
            if (ParseColour(Json.Str(colour, "light"), out c)) def.ColourLight = c;
            if (ParseColour(Json.Str(colour, "dark"), out c)) def.ColourDark = c;
            // Register translations supplied by the definition.
            var i18n = Json.Obj(d, "i18n");
            if (i18n != null)
                foreach (var lang in i18n)
                {
                    var t = lang.Value as Dictionary<string, object>;
                    if (t != null) foreach (var kv in t) if (kv.Value is string) L.Register(lang.Key, kv.Key, (string)kv.Value);
                }
            return def;
        }

        private static bool ParseColour(string s, out uint c)
        {
            c = 0;
            if (s == null || !Regex.IsMatch(s, "^#[0-9a-fA-F]{6}$")) return false;
            c = 0xFF000000 | uint.Parse(s.Substring(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            return true;
        }

        private bool? detected;

        /// <summary>Detect installed files, commands on PATH, or environment variables.</summary>
        public bool Detected()
        {
            if (detected == null) detected = DetectNow();
            // Recheck discovered credentials because another app or WSL may become available later.
            return detected.Value || KeyFinder.Find(this) != null;
        }

        private bool DetectNow()
        {
            var det = Json.Obj(Raw, "detect");
            if (det == null) return false;
            if (Strings(det, "paths").Any(p => { string f = Expand(p); return File.Exists(f) || Directory.Exists(f); })) return true;
            if (Strings(det, "env").Any(v => !string.IsNullOrEmpty(Env(v)))) return true;
            if (Strings(det, "commands").Any(c => OnPath(c) != null)) return true;
            return false;
        }

        /// <summary>Find an installed image or executable icon; null falls back to a monogram.</summary>
        public string LogoFile()
        {
            var logo = Json.Obj(Raw, "logo");
            foreach (var p in Strings(logo, "file").Concat(Strings(logo, "exe")))
            {
                string f = Expand(p);
                if (File.Exists(f)) return f;
            }
            return null;
        }

        /// <summary>
        /// Resolve the definition's logo image or dark-theme variant from its own directory.
        /// Built-in definitions also support embedded resources and the bundled providers directory.
        /// </summary>
        public Stream LogoImage(bool dark)
        {
            var logo = Json.Obj(Raw, "logo");
            string name = (dark ? Json.Str(logo, "imageDark") : null) ?? Json.Str(logo, "image");
            if (string.IsNullOrEmpty(name)) return null;
            name = Path.GetFileName(name);   // Only accept a filename beside the definition.
            try
            {
                if (Origin == "built-in")
                {
                    var s = typeof(ServiceDefinition).Assembly.GetManifestResourceStream("providers." + name);
                    if (s != null) return s;
                    string f = Path.Combine(Program.Home, "providers", name);
                    return File.Exists(f) ? File.OpenRead(f) : null;
                }
                string own = Path.Combine(Path.GetDirectoryName(Origin), name);
                return File.Exists(own) ? File.OpenRead(own) : null;
            }
            catch (IOException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
            catch (ArgumentException) { return null; }
        }

        // ------------------------------------------------------------ shared utilities
        public static IEnumerable<string> Strings(Dictionary<string, object> d, string key)
        {
            object v;
            if (d == null || !d.TryGetValue(key, out v) || v == null) return new string[0];
            if (v is string) return new[] { (string)v };
            var list = v as IEnumerable;
            return list == null ? new string[0] : list.OfType<string>().ToArray();
        }

        /// <summary>Expand Windows environment variables and the user's home directory.</summary>
        public static string Expand(string p)
        {
            if (string.IsNullOrEmpty(p)) return p;
            if (p.StartsWith("~")) p = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + p.Substring(1);
            return Environment.ExpandEnvironmentVariables(p).Replace('/', '\\');
        }

        public static string Env(string name)
        {
            return Environment.GetEnvironmentVariable(name)
                   ?? Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.User);
        }

        public static string OnPath(string exe)
        {
            var path = (Environment.GetEnvironmentVariable("PATH") ?? "") + ";" + (Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User) ?? "");
            foreach (var dir in path.Split(';'))
            {
                if (dir.Trim().Length == 0) continue;
                try { string f = Path.Combine(Environment.ExpandEnvironmentVariables(dir.Trim()), exe); if (File.Exists(f)) return f; }
                catch (ArgumentException) { }
            }
            return null;
        }
    }

    /// <summary>Load built-in definitions and user definitions from %APPDATA%\AIMeter\providers.</summary>
    internal static class Definitions
    {
        private static List<ServiceDefinition> all;
        /// <summary>Ignore definitions whose ids belong to providers implemented in code.</summary>
        private static readonly string[] Reserved = { "claude", "openai", "copilot", "antigravity", "local" };

        public static string UserFolder { get { return Path.Combine(Settings.Folder, "providers"); } }

        public static IList<ServiceDefinition> All { get { if (all == null) all = Load(); return all; } }

        public static ServiceDefinition Get(string id) { return All.FirstOrDefault(d => d.Id == id); }

        private static List<ServiceDefinition> Load()
        {
            var byId = new Dictionary<string, ServiceDefinition>();
            Action<string, string> add = (text, origin) =>
            {
                ServiceDefinition d;
                try { d = ServiceDefinition.FromJson(text, origin); }
                catch (Exception) { d = null; }
                if (d != null && !Reserved.Contains(d.Id)) byId[d.Id] = d;   // User definitions override built-in definitions with the same id.
            };
            // Load embedded definitions first, followed by bundled and user files.
            var asm = typeof(Definitions).Assembly;
            foreach (var n in asm.GetManifestResourceNames().Where(n => n.StartsWith("providers.") && n.EndsWith(".json")).OrderBy(n => n))
                using (var s = asm.GetManifestResourceStream(n))
                using (var r = new StreamReader(s, Encoding.UTF8)) add(r.ReadToEnd(), "built-in");
            foreach (var dir in new[] { Path.Combine(Program.Home, "providers"), UserFolder })
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
            return byId.Values.OrderBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        }
    }
}
