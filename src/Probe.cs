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

        private sealed class CacheEntry { public DateTime Mtime; public long Length; public object Value; }

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

        private void RefreshWslState()
        {
            RefreshWslStateCore();
            WslHomeUp = wslUp ? WslHome : null;
        }

        private void RefreshWslStateCore()
        {
            bool vm = Procs.ContainsValue("vmmemwsl") || Procs.ContainsValue("vmmem");
            if (!vm || string.IsNullOrEmpty(S.WslDistro)) { wslUp = false; vmSeen = false; return; }
            // A VM that has just appeared is checked immediately, then every minute if the distribution is active and every 5 if not.
            // No longer than that: opening \\wsl.localhost of a distribution that has just stopped would restart it.
            // wsl.exe -l queries only the Windows service; it starts no processes in Linux.
            if (vmSeen && (DateTime.Now - wslCheckedAt).TotalSeconds < (wslUp ? 60 : 300)) return;
            vmSeen = true;
            wslCheckedAt = DateTime.Now;
            // The VM may be running for another distribution: ask which ones are active, without starting any.
            string output = Run("wsl.exe", "-l --running -q", 8000, Encoding.Unicode);
            wslUp = output != null && output.Split('\n').Any(l => l.Trim().Trim('\0').Equals(S.WslDistro, StringComparison.OrdinalIgnoreCase));
            if (wslUp && !Directory.Exists(WslHome)) DetectWslUser();
        }

        /// <summary>The Linux user may be named differently from the Windows one: picks the home holding a service's data.</summary>
        private void DetectWslUser()
        {
            foreach (var home in SafeDirs(WslRoot + @"\home"))
            {
                if (!Directory.Exists(home + @"\.claude") && !Directory.Exists(home + @"\.codex")) continue;
                S.WslUser = Path.GetFileName(home);
                S.Save();
                return;
            }
        }

        // ================================================================ cache
        public object Cached(string key, FileInfo fi, Func<object> load)
        {
            CacheEntry e;
            if (cache.TryGetValue(key, out e) && e.Mtime == fi.LastWriteTimeUtc && e.Length == fi.Length) return e.Value;
            object v = load();
            cache[key] = new CacheEntry { Mtime = fi.LastWriteTimeUtc, Length = fi.Length, Value = v };
            return v;
        }

        /// <summary>Value bound to a fixed key, computed only once (e.g. the header of a log that does not change).</summary>
        public T Once<T>(string key, Func<T> load) where T : class
        {
            CacheEntry e;
            if (cache.TryGetValue(key, out e)) return e.Value as T;
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

        /// <summary>Occasionally clears cache entries for files that no longer exist.</summary>
        public void Trim()
        {
            foreach (var k in cache.Keys.ToList())
            {
                int colon = k.IndexOf(':');
                string path = colon >= 0 ? k.Substring(colon + 1) : k;
                if (!path.StartsWith(@"\\") && path.Contains(@"\") && !File.Exists(path)) cache.Remove(k);
            }
        }

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
    }

    /// <summary>Orchestrator: collects the state of each enabled service, sharing processes, WSL and cache.</summary>
    internal sealed class Collector
    {
        private readonly Probe probe;
        private readonly List<Provider> providers;

        public Collector(Settings settings)
        {
            probe = new Probe(settings);
            providers = new List<Provider> { new ClaudeProvider(settings), new CodexProvider(settings), new CopilotProvider(settings), new AntigravityProvider(settings) };
            foreach (var d in Definitions.All) providers.Add(new DefinedProvider(d));
            providers.Add(new LocalProvider(settings));
        }

        public bool WslUp { get { return probe.WslUp; } }

        public void InvalidateNetwork() { foreach (var p in providers) p.InvalidateNetwork(); }

        public void Trim() { probe.Trim(); }

        public Snapshot Collect(bool allowNetwork, bool savingPower)
        {
            probe.Begin(allowNetwork, savingPower);
            var snap = new Snapshot();
            foreach (var p in providers)
            {
                if (!p.Enabled(probe.S)) continue;
                try { snap.Providers.Add(p.Collect(probe)); }
                catch (Exception ex) { Trace.WriteLine(p.Id + ": " + ex); }   // A faulty service does not stop the others.
            }
            return snap;
        }
    }
}
