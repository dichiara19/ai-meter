using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;

namespace AIMeter
{
    /// <summary>
    /// Ciò che serve a tutti i servizi durante una raccolta: processi in esecuzione, stato di WSL,
    /// cache dei file e letture efficienti. Regole:
    ///  - ogni file viene interpretato solo quando cambia (cache per data di modifica e dimensione);
    ///  - dei log grandi si legge solo la coda;
    ///  - WSL non viene mai avviato: lo si legge solo se la sua VM è già accesa.
    /// </summary>
    internal sealed class Probe
    {
        public readonly Settings S;
        public Dictionary<uint, string> Procs = new Dictionary<uint, string>();
        public bool AllowNetwork;
        public bool SavingPower;
        private readonly Dictionary<uint, string> pathByPid = new Dictionary<uint, string>();
        private readonly Dictionary<string, CacheEntry> cache = new Dictionary<string, CacheEntry>(StringComparer.OrdinalIgnoreCase);
        private bool wslUp;
        private DateTime wslCheckedAt = DateTime.MinValue;

        public static readonly string Home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        public static readonly string AppData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        public static readonly string LocalAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        private sealed class CacheEntry { public DateTime Mtime; public long Length; public object Value; }

        public Probe(Settings s) { S = s; }

        /// <summary>Aggiorna processi e stato di WSL all'inizio di ogni raccolta.</summary>
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

        private void RefreshWslState()
        {
            bool vm = Procs.ContainsValue("vmmemwsl") || Procs.ContainsValue("vmmem");
            if (!vm || string.IsNullOrEmpty(S.WslDistro)) { wslUp = false; return; }
            if ((DateTime.Now - wslCheckedAt).TotalSeconds < 120) return;
            wslCheckedAt = DateTime.Now;
            // la VM può essere accesa per un'altra distribuzione: chiediamo quali sono attive, senza avviarne nessuna
            string output = Run("wsl.exe", "-l --running -q", 8000, Encoding.Unicode);
            wslUp = output != null && output.Split('\n').Any(l => l.Trim().Trim('\0').Equals(S.WslDistro, StringComparison.OrdinalIgnoreCase));
            if (wslUp && !Directory.Exists(WslHome)) DetectWslUser();
        }

        /// <summary>L'utente Linux può chiamarsi diversamente da quello di Windows: prende la home che contiene i dati di un servizio.</summary>
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

        /// <summary>Valore legato a una chiave fissa, calcolato una volta sola (es. l'intestazione di un log che non cambia).</summary>
        public T Once<T>(string key, Func<T> load) where T : class
        {
            CacheEntry e;
            if (cache.TryGetValue(key, out e)) return e.Value as T;
            T v = load();
            cache[key] = new CacheEntry { Value = v };
            return v;
        }

        /// <summary>Legge un JSON quando cambia e ne conserva solo i campi indicati (mai il documento intero).</summary>
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

        /// <summary>Svuota la cache delle voci per file non più esistenti, una volta ogni tanto.</summary>
        public void Trim()
        {
            foreach (var k in cache.Keys.ToList())
            {
                int colon = k.IndexOf(':');
                string path = colon >= 0 ? k.Substring(colon + 1) : k;
                if (!path.StartsWith(@"\\") && path.Contains(@"\") && !File.Exists(path)) cache.Remove(k);
            }
        }

        // ================================================================ file e processi
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

        /// <summary>Esegue un programma senza finestra e ne restituisce l'output (null se fallisce o va oltre il tempo).</summary>
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

    /// <summary>Un servizio AI monitorato: sa se è attivo nelle impostazioni e raccoglie sessioni e limiti.</summary>
    internal abstract class Provider
    {
        public abstract string Id { get; }
        public abstract bool Enabled(Settings s);
        public abstract ProviderState Collect(Probe p);
        /// <summary>Forza la prossima lettura via rete (pulsante Aggiorna, login appena fatto, risveglio).</summary>
        public virtual void InvalidateNetwork() { }
    }

    /// <summary>Orchestratore: per ogni servizio attivo raccoglie lo stato, condividendo processi, WSL e cache.</summary>
    internal sealed class Collector
    {
        private readonly Probe probe;
        private readonly List<Provider> providers;

        public Collector(Settings settings)
        {
            probe = new Probe(settings);
            providers = new List<Provider> { new ClaudeProvider(settings), new CodexProvider(settings), new CopilotProvider(settings) };
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
                catch (Exception ex) { Trace.WriteLine(p.Id + ": " + ex); }   // un servizio difettoso non ferma gli altri
            }
            return snap;
        }
    }
}
