using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace AIMeter
{
    /// <summary>
    /// Session folders of coding agents that are running now, read from their arguments and environment. Pi started with
    /// --session-dir or PI_CODING_AGENT_DIR (directly, from a script or inside a bwrap sandbox) keeps its sessions outside
    /// ~/.pi, where the file scan cannot find them. Windows processes are read through their command line; WSL processes
    /// through \\wsl.localhost\&lt;distro&gt;\proc, only while the distribution is running, so nothing is started in Linux.
    /// </summary>
    internal static class RunningAgents
    {
        /// <summary>Windows executables that can host Pi: its own binary or the JavaScript runtime it was installed with.</summary>
        private static readonly string[] WindowsHosts = { "pi.exe", "node.exe", "bun.exe" };

        // Arguments of WSL processes by pid: they do not change, so each process is read once.
        private static readonly Dictionary<string, string[]> wslArguments = new Dictionary<string, string[]>();
        private static readonly object Gate = new object();

        /// <summary>A folder that holds Pi session files, as a Windows path, with "Windows" or "WSL".</summary>
        internal sealed class Folder { public string Path, Where; }

        public static List<Folder> PiSessionFolders(Probe p)
        {
            var found = new List<Folder>();
            foreach (var pid in p.Procs.Where(kv => WindowsHosts.Contains(kv.Value)).Select(kv => kv.Key))
            {
                var args = Native.ProcessArguments(pid);
                if (args != null) found.AddRange(PiFolders(args, null).Select(f => new Folder { Path = f, Where = "Windows" }));
            }
            if (p.WslUp)
            {
                string root = p.WslRoot;
                foreach (var proc in WslPiProcesses(root))
                    found.AddRange(PiFolders(proc.Key, proc.Value).Select(f => new Folder { Path = FromWsl(root, f), Where = "WSL" }));
            }
            return found.GroupBy(f => f.Path, StringComparer.OrdinalIgnoreCase).Select(g => g.First()).ToList();
        }

        /// <summary>WSL processes that run Pi, with their arguments and, for Pi itself, its environment.</summary>
        private static List<KeyValuePair<string[], Dictionary<string, string>>> WslPiProcesses(string root)
        {
            var list = new List<KeyValuePair<string[], Dictionary<string, string>>>();
            string proc = Path.Combine(root, "proc");
            var pids = Probe.SafeDirs(proc).Select(Path.GetFileName).Where(n => n.All(char.IsDigit)).ToList();
            lock (Gate)
            {
                foreach (var gone in wslArguments.Keys.Except(pids).ToList()) wslArguments.Remove(gone);
                foreach (var pid in pids)
                {
                    string[] args;
                    if (!wslArguments.TryGetValue(pid, out args))
                        wslArguments[pid] = args = ReadNulSeparated(Path.Combine(proc, pid, "cmdline")) ?? new string[0];
                    if (!IsPi(args)) continue;
                    list.Add(new KeyValuePair<string[], Dictionary<string, string>>(args, Environment(Path.Combine(proc, pid, "environ"))));
                }
            }
            return list;
        }

        /// <summary>
        /// Session folders named by one Pi process: --session-dir, PI_CODING_AGENT_SESSION_DIR, or the sessions folder of
        /// PI_CODING_AGENT_DIR, from the environment or from a sandbox's --setenv. Paths are as the process sees them.
        /// </summary>
        internal static IEnumerable<string> PiFolders(IList<string> args, IDictionary<string, string> env)
        {
            if (!IsPi(args)) yield break;
            for (int i = 0; i < args.Count; i++)
            {
                string a = args[i];
                if (a == "--session-dir" && i + 1 < args.Count) yield return args[i + 1];
                else if (a.StartsWith("--session-dir=", StringComparison.Ordinal)) yield return a.Substring(14);
                else if (a == "--setenv" && i + 2 < args.Count)
                {
                    string folder = FromVariable(args[i + 1], args[i + 2]);
                    if (folder != null) yield return folder;
                }
            }
            if (env == null) yield break;
            foreach (var kv in env)
            {
                string folder = FromVariable(kv.Key, kv.Value);
                if (folder != null) yield return folder;
            }
        }

        private static string FromVariable(string name, string value)
        {
            if (string.IsNullOrEmpty(value)) return null;
            if (name == "PI_CODING_AGENT_SESSION_DIR") return value;
            if (name == "PI_CODING_AGENT_DIR") return value.TrimEnd('/', '\\') + (value.Contains('\\') ? @"\sessions" : "/sessions");
            return null;
        }

        /// <summary>Whether the arguments start Pi: a "pi" executable or the pi-coding-agent package, also as a sandboxed command.</summary>
        internal static bool IsPi(IList<string> args)
        {
            foreach (var a in args)
            {
                if (a.IndexOf("pi-coding-agent", StringComparison.OrdinalIgnoreCase) >= 0) return true;
                string name = a.Substring(Math.Max(a.LastIndexOf('/'), a.LastIndexOf('\\')) + 1);
                if (name.Equals("pi", StringComparison.OrdinalIgnoreCase) || name.Equals("pi.exe", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("pi.cmd", StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        /// <summary>A Linux path as seen from Windows: /mnt/c/... becomes C:\..., anything else goes through \\wsl.localhost.</summary>
        internal static string FromWsl(string root, string path)
        {
            if (path.Length >= 6 && path.StartsWith("/mnt/", StringComparison.Ordinal) && char.IsLetter(path[5]) && (path.Length == 6 || path[6] == '/'))
                return char.ToUpperInvariant(path[5]) + @":\" + path.Substring(Math.Min(path.Length, 7)).Replace('/', '\\');
            return root + path.Replace('/', '\\');
        }

        private static string[] ReadNulSeparated(string file)
        {
            try
            {
                // Files in /proc report a length of zero, so they are read to the end of the stream instead.
                var text = File.ReadAllText(file, Encoding.UTF8);
                return text.Split(new[] { '\0' }, StringSplitOptions.RemoveEmptyEntries);
            }
            catch (IOException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
        }

        private static Dictionary<string, string> Environment(string file)
        {
            var env = new Dictionary<string, string>();
            foreach (var entry in ReadNulSeparated(file) ?? new string[0])
            {
                int eq = entry.IndexOf('=');
                if (eq > 0 && entry.StartsWith("PI_CODING_AGENT_", StringComparison.Ordinal)) env[entry.Substring(0, eq)] = entry.Substring(eq + 1);
            }
            return env;
        }
    }
}
