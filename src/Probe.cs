using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;

namespace AIMeter
{
    /// <summary>
    /// What every service needs during a collection: running processes, WSL state,
    /// file cache and efficient reads. Rules:
    ///  - each file is parsed only when it changes (cached by modification time and size);
    ///  - only the tail of large logs is read;
    ///  - WSL is never started: it is read only if its VM is already running.
    /// </summary>
    internal sealed class Probe
    {
        public readonly Settings S;
        public Dictionary<uint, string> Procs = new Dictionary<uint, string>();
        public bool AllowNetwork;
        public bool SavingPower;
        private readonly Dictionary<uint, string> pathByPid = new Dictionary<uint, string>();
        private readonly Dictionary<string, CacheEntry> cache = new Dictionary<string, CacheEntry>(StringComparer.OrdinalIgnoreCase);
        private bool wslUp, vmSeen;
        private DateTime wslCheckedAt = DateTime.MinValue;

        public static readonly string Home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        public static readonly string AppData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        public static readonly string LocalAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        /// <summary>Used is the last time a read asked for the entry, so that Trim can age out entries no scan reads any more.</summary>
        private sealed class CacheEntry { public DateTime Mtime; public long Length; public object Value; public DateTime Used = DateTime.UtcNow; }

        /// <summary>Entries unused for this long go, wherever their file is; past the most entries, the least recently used go first.</summary>
        private static readonly TimeSpan UnusedFor = TimeSpan.FromHours(1);
        private const int MostEntries = 4000;

        public Probe(Settings s) { S = s; }

        /// <summary>Refreshes processes and WSL state at the start of each collection.</summary>
        public void Begin(bool allowNetwork, bool savingPower)
        {
            AllowNetwork = allowNetwork;
            SavingPower = savingPower;
            Procs = Native.Processes();
            foreach (var pid in pathByPid.Keys.ToList()) if (!Procs.ContainsKey(pid)) pathByPid.Remove(pid);
            RefreshWslState();
        }

        public bool IsRunning(string exe) { return Procs.ContainsValue(exe); }

        public string PathOf(uint pid)
        {
            string p;
            if (!pathByPid.TryGetValue(pid, out p)) { p = Native.ProcessPath(pid); pathByPid[pid] = p; }
            return p;
        }

        // ================================================================ WSL
        public bool WslUp { get { return wslUp; } }
        public string WslRoot { get { return @"\\wsl.localhost\" + S.WslDistro; } }
        public string WslHome { get { return WslRoot + @"\home\" + S.WslUser; } }

        /// <summary>The WSL home if the distribution is running, otherwise null: for readers outside a collection.</summary>
        public static volatile string WslHomeUp;

        /// <summary>
        /// Every running distribution with a home where a coding agent is set up (the configured one first, with its
        /// configured home), as of the last check: for delegation, which can use agents in any of them. Empty when none.
        /// </summary>
        public static volatile IList<WslPlace> WslPlacesUp = new WslPlace[0];

        private List<WslPlace> otherPlaces = new List<WslPlace>();

        private void RefreshWslState()
        {
            RefreshWslStateCore();
            WslHomeUp = wslUp ? WslHome : null;
            var places = new List<WslPlace>();
            if (wslUp) places.Add(new WslPlace { Distro = S.WslDistro, Home = WslHome });
            places.AddRange(otherPlaces);
            WslPlacesUp = places;
        }

        private void RefreshWslStateCore()
        {
            bool vm = Procs.ContainsValue("vmmemwsl") || Procs.ContainsValue("vmmem");
            if (!vm) { wslUp = false; vmSeen = false; otherPlaces = new List<WslPlace>(); return; }
            // A VM that has just appeared is checked immediately, then every minute if a distribution is active and every 5 if not.
            // No longer than that: opening \\wsl.localhost of a distribution that has just stopped would restart it.
            // wsl.exe -l queries only the Windows service; it starts no processes in Linux.
            if (vmSeen && (DateTime.Now - wslCheckedAt).TotalSeconds < (wslUp || otherPlaces.Count > 0 ? 60 : 300)) return;
            vmSeen = true;
            wslCheckedAt = DateTime.Now;
            // The VM may be running for another distribution: ask which ones are active, without starting any.
            string output = Run("wsl.exe", "-l --running -q", 8000, Encoding.Unicode);
            var running = (output ?? "").Split('\n').Select(l => l.Trim().Trim('\0')).Where(l => l.Length > 0).ToList();
            wslUp = !string.IsNullOrEmpty(S.WslDistro) && running.Any(d => d.Equals(S.WslDistro, StringComparison.OrdinalIgnoreCase));
            if (wslUp && !Directory.Exists(WslHome)) DetectWslUser();
            otherPlaces = running.Where(d => !d.Equals(S.WslDistro ?? "", StringComparison.OrdinalIgnoreCase))
                .Select(d => new WslPlace { Distro = d, Home = AgentHome(@"\\wsl.localhost\" + d) }).Where(p => p.Home != null).ToList();
        }

        /// <summary>The Linux user may be named differently from the Windows one: picks the home holding an agent's data.</summary>
        private void DetectWslUser()
        {
            string home = AgentHome(WslRoot);
            if (home == null) return;
            S.WslUser = Path.GetFileName(home);
            S.Save();
        }

        /// <summary>The first home under a distribution's root where a coding agent of agents/*.json is set up, or null.</summary>
        internal static string AgentHome(string root)
        {
            var markers = Agents.All.SelectMany(a => a.Folders).Distinct().ToList();
            return SafeDirs(root + @"\home").FirstOrDefault(home => markers.Any(m => Directory.Exists(Path.Combine(home, m))));
        }

        // ================================================================ cache
        public object Cached(string key, FileInfo fi, Func<object> load)
        {
            CacheEntry e;
            if (cache.TryGetValue(key, out e) && e.Mtime == fi.LastWriteTimeUtc && e.Length == fi.Length) { e.Used = DateTime.UtcNow; return e.Value; }
            object v = load();
            cache[key] = new CacheEntry { Mtime = fi.LastWriteTimeUtc, Length = fi.Length, Value = v };
            return v;
        }

        /// <summary>Value bound to a fixed key, computed only once (e.g. the header of a log that does not change).</summary>
        public T Once<T>(string key, Func<T> load) where T : class
        {
            CacheEntry e;
            if (cache.TryGetValue(key, out e)) { e.Used = DateTime.UtcNow; return e.Value as T; }
            T v = load();
            cache[key] = new CacheEntry { Value = v };
            return v;
        }

        /// <summary>Reads a JSON file when it changes and keeps only the given fields (never the whole document).</summary>
        public Dictionary<string, object> CachedFields(FileInfo fi, params string[] keys)
        {
            return Cached("json:" + fi.FullName, fi, () => Keep(Json.ParseFile(fi.FullName), keys)) as Dictionary<string, object>;
        }

        public static Dictionary<string, object> Keep(Dictionary<string, object> d, params string[] keys)
        {
            if (d == null) return null;
            var small = new Dictionary<string, object>(keys.Length);
            object v;
            foreach (var k in keys) if (d.TryGetValue(k, out v) && !(v is Dictionary<string, object>) && !(v is object[])) small[k] = v;
            return small;
        }

        /// <summary>
        /// Occasionally clears cache entries: those unused for an hour, those for local files that no longer exist, and
        /// the least recently used past MostEntries. WSL files (\\ paths) are aged out rather than checked, so that
        /// trimming never wakes a stopped distribution.
        /// </summary>
        public void Trim()
        {
            var now = DateTime.UtcNow;
            foreach (var kv in cache.ToList())
            {
                int colon = kv.Key.IndexOf(':');
                string path = colon >= 0 ? kv.Key.Substring(colon + 1) : kv.Key;
                bool gone = !path.StartsWith(@"\\") && path.Contains(@"\") && !File.Exists(path);
                if (gone || now - kv.Value.Used > UnusedFor) cache.Remove(kv.Key);
            }
            if (cache.Count > MostEntries)
                foreach (var kv in cache.OrderBy(kv => kv.Value.Used).Take(cache.Count - MostEntries).ToList()) cache.Remove(kv.Key);
        }

#if AIMETER_TEST
        internal int CacheCount { get { return cache.Count; } }
        internal void AgeCacheEntry(string key, TimeSpan by) { cache[key].Used -= by; }
#endif

        // ================================================================ files and processes
        public static IEnumerable<FileInfo> SafeFiles(string dir, string pattern)
        {
            try { return Directory.Exists(dir) ? new DirectoryInfo(dir).GetFiles(pattern) : new FileInfo[0]; }
            catch (IOException) { return new FileInfo[0]; }
            catch (UnauthorizedAccessException) { return new FileInfo[0]; }
        }

        public static IEnumerable<string> SafeDirs(string dir)
        {
            try { return Directory.Exists(dir) ? Directory.GetDirectories(dir) : new string[0]; }
            catch (IOException) { return new string[0]; }
            catch (UnauthorizedAccessException) { return new string[0]; }
        }

        public static string LastSegment(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            var parts = path.TrimEnd('/', '\\').Split('/', '\\');
            return parts.Length == 0 ? null : parts[parts.Length - 1];
        }

        private static FileStream OpenShared(string path)
        {
            return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.SequentialScan);
        }

        public static string TailText(string path, int maxBytes)
        {
            try
            {
                using (var fs = OpenShared(path))
                {
                    long start = Math.Max(0, fs.Length - maxBytes);
                    fs.Seek(start, SeekOrigin.Begin);
                    var buf = new byte[fs.Length - start];
                    int read = 0, n;
                    while (read < buf.Length && (n = fs.Read(buf, read, buf.Length - read)) > 0) read += n;
                    Json.BytesRead += read;
                    string text = Encoding.UTF8.GetString(buf, 0, read);
                    if (start > 0) { int nl = text.IndexOf('\n'); text = nl >= 0 ? text.Substring(nl + 1) : ""; }
                    return text;
                }
            }
            catch (IOException) { return ""; }
            catch (UnauthorizedAccessException) { return ""; }
        }

        public static string LastLineContaining(string path, string marker, int maxBytes)
        {
            string text = TailText(path, maxBytes);
            int i = text.LastIndexOf(marker, StringComparison.Ordinal);
            if (i < 0) return null;
            int s = text.LastIndexOf('\n', i) + 1;
            int e = text.IndexOf('\n', i);
            return e < 0 ? text.Substring(s) : text.Substring(s, e - s);
        }

        public static string ReadFirstLine(string path, int maxBytes)
        {
            try
            {
                using (var fs = OpenShared(path))
                {
                    var ms = new MemoryStream();
                    int b;
                    while (ms.Length < maxBytes && (b = fs.ReadByte()) >= 0 && b != '\n') ms.WriteByte((byte)b);
                    return Encoding.UTF8.GetString(ms.ToArray());
                }
            }
            catch (IOException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
        }

        /// <summary>Runs a program without a window and returns its output (null if it fails or times out).</summary>
        public static string Run(string exe, string args, int timeoutMs, Encoding outputEncoding)
        {
            try
            {
                var psi = new ProcessStartInfo(exe, args)
                {
                    UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
                    RedirectStandardOutput = true, RedirectStandardError = true,
                };
                if (outputEncoding != null) psi.StandardOutputEncoding = outputEncoding;
                using (var p = Process.Start(psi))
                {
                    var outTask = p.StandardOutput.ReadToEndAsync();
                    p.StandardError.ReadToEndAsync();
                    if (!p.WaitForExit(timeoutMs)) { try { p.Kill(); } catch (InvalidOperationException) { } return null; }
                    return outTask.Result;
                }
            }
            catch (System.ComponentModel.Win32Exception) { return null; }
            catch (InvalidOperationException) { return null; }
        }
    }

    /// <summary>A monitored AI service: knows whether it is enabled in Settings and collects sessions and limits.</summary>
    internal abstract class Provider
    {
        public abstract string Id { get; }
        public abstract bool Enabled(Settings s);
        public abstract ProviderState Collect(Probe p);
        /// <summary>Forces the next read over the network (Refresh button, fresh sign-in, resume).</summary>
        public virtual void InvalidateNetwork() { }
        /// <summary>Forces the next read of the free-model list, which has its own, longer interval.</summary>
        public virtual void InvalidateCatalogue() { }
    }

    /// <summary>Orchestrator: collects the state of each enabled service, sharing processes, WSL and cache.</summary>
    internal sealed class Collector
    {
        private readonly Probe probe;
        private readonly List<Provider> providers;
        private readonly HashSet<string> failing = new HashSet<string>();   // Providers whose last read threw, already logged.
        private readonly Dictionary<string, string> names = new Dictionary<string, string>();   // Card names from the last good reads.

        public Collector(Settings settings)
        {
            probe = new Probe(settings);
            providers = new List<Provider> { new ClaudeProvider(settings), new CodexProvider(settings), new CopilotProvider(settings), new AntigravityProvider(settings) };
            foreach (var d in Definitions.All) providers.Add(new DefinedProvider(d));
            providers.Add(new LocalProvider(settings));
        }

        /// <summary>A collector of the given providers only, for tests.</summary>
        internal Collector(Settings settings, List<Provider> providers)
        {
            probe = new Probe(settings);
            this.providers = providers;
        }

        public bool WslUp { get { return probe.WslUp; } }

        public void InvalidateNetwork() { foreach (var p in providers) p.InvalidateNetwork(); }

        /// <summary>Force one provider's next network read: its usage, or its free-model list.</summary>
        public void Invalidate(string id, bool catalogue)
        {
            foreach (var p in providers.Where(x => string.Equals(x.Id, id, StringComparison.OrdinalIgnoreCase)))
            {
                if (catalogue) p.InvalidateCatalogue();
                else p.InvalidateNetwork();
            }
        }

        public void Trim() { probe.Trim(); }

        public Snapshot Collect(bool allowNetwork, bool savingPower)
        {
            probe.Begin(allowNetwork, savingPower);
            var snap = new Snapshot();
            foreach (var p in providers)
            {
                if (!p.Enabled(probe.S)) continue;
                try
                {
                    var state = p.Collect(probe);
                    snap.Providers.Add(state);
                    failing.Remove(p.Id);
                    if (state.Name != null) names[p.Id] = state.Name;
                }
                catch (Exception ex) { snap.Providers.Add(Failed(p.Id, ex)); }   // A faulty service does not stop the others.
            }
            PlaceAgentSessions(snap);
            return snap;
        }

        /// <summary>
        /// The card of a provider whose read threw: it stays in place with a notice instead of vanishing. The error is
        /// logged once until a read succeeds again, so a provider that fails every cycle does not fill the log.
        /// </summary>
        private ProviderState Failed(string id, Exception ex)
        {
            if (failing.Add(id)) Program.LogError("collect-" + id, ex);
            string name;
            if (!names.TryGetValue(id, out name))
            {
                var definition = Definitions.Get(id);
                name = definition != null ? definition.Name : id;
            }
            return new ProviderState { Id = id, Name = name, Notice = L.T("Could not read this service: see errors.log") };
        }

        // Agent provider ids for the services implemented in code; defined services use their key ids ("auth.ids").
        private static readonly Dictionary<string, string[]> BuiltInProviderIds = new Dictionary<string, string[]>
        {
            { "claude", new[] { "anthropic" } },
            { "openai", new[] { "openai-codex" } },
            { "copilot", new[] { "github-copilot" } },
            { "antigravity", new[] { "google-antigravity" } },
        };

        /// <summary>
        /// Move coding-agent sessions from the local card to the card of the service they use, when that card is shown:
        /// a Pi session on OpenRouter belongs with OpenRouter's limits. Sessions of other providers stay with the agents.
        /// </summary>
        internal static void PlaceAgentSessions(Snapshot snap)
        {
            var local = snap.Providers.FirstOrDefault(x => x.Id == "local");
            if (local == null) return;
            foreach (var s in local.Sessions.Where(x => !string.IsNullOrEmpty(x.Provider)).ToList())
            {
                var target = snap.Providers.FirstOrDefault(x => x.Id != "local" && ProviderIds(x.Id).Contains(s.Provider, StringComparer.OrdinalIgnoreCase));
                if (target == null) continue;
                local.Sessions.Remove(s);
                target.Sessions.Add(s);
                target.Running = true;
            }
        }

        private static IEnumerable<string> ProviderIds(string serviceId)
        {
            string[] ids;
            if (BuiltInProviderIds.TryGetValue(serviceId, out ids)) return ids;
            var d = Definitions.Get(serviceId);
            return d == null ? new string[0] : ServiceDefinition.Strings(d.Auth, "ids").DefaultIfEmpty(d.Id);
        }
    }

    /// <summary>A running WSL distribution and the home, as Windows sees it (\\wsl.localhost\Distro\home\user), to read.</summary>
    internal sealed class WslPlace
    {
        public string Distro, Home;
    }
}
