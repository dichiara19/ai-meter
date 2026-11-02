using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;

namespace AIMeter
{
    /// <summary>
    /// Execution candidates (docs/plans/orchestration-contracts.md sections 2.1 and 2.4): the free models a task can run
    /// on now, through a coding agent on this PC that already holds its own key for the service, and why every other
    /// model cannot. No particular agent is required: delegation uses whichever supported agents the user has.
    /// Nothing here starts a process or reserves capacity: coordinator plugins run the agents (ADR 0002), and capacity
    /// leases reserve room.
    ///
    /// Selection is deterministic: the same snapshot and request give the same order. Models that share a pool share
    /// its limits, so capacity is reported per pool and never added up across the pool's models.
    /// </summary>
    internal static class Candidates
    {
        public const string FreeOnly = "free_only";
        public const string FreeOrUnconfirmed = "free_or_unconfirmed";

        /// <summary>Choose the candidates for a task and explain each exclusion.</summary>
        public static Selection Select(Snapshot snap, RuntimeScan runtimes, Requirements req, DateTime now, LeaseBook leases = null)
        {
            var result = new Selection();
            foreach (var p in snap.Providers.Where(x => x.Catalogue != null && (req.Service == null || string.Equals(x.Id, req.Service, StringComparison.OrdinalIgnoreCase))))
            {
                var access = runtimes.Access.Where(a => a.Service == p.Id).ToList();
                foreach (var m in p.Catalogue.Models)
                {
                    var room = Room(p.Id, p.Catalogue.Rates, now, leases, p.Catalogue.PoolOf(m) ?? p.Id);
                    string reason = Check(m, req);
                    if (reason == null && access.Count == 0) reason = runtimes.KeyDiscoveryOff ? "key_discovery_off" : runtimes.Found.Count == 0 ? "no_runtime" : "runtime_has_no_key";
                    if (reason == null && room.Blocking != null) reason = room.Blocking;
                    if (reason != null) { result.Exclude(p.Id, m.Id, reason, reason == room.Blocking ? room.Until : null); continue; }
                    foreach (var a in access)
                        result.Chosen.Add(new Candidate { Provider = p, Model = m, Access = a, Room = room, Pool = p.Catalogue.PoolOf(m) ?? p.Id });
                }
            }
            // Known room first, most room first; then unknown room; ties by service, model and runtime place.
            result.Chosen = result.Chosen.OrderBy(c => c.Room.Ratio.HasValue ? 0 : 1).ThenByDescending(c => c.Room.Ratio ?? 0)
                .ThenBy(c => c.Provider.Id, StringComparer.Ordinal).ThenBy(c => c.Model.Id, StringComparer.Ordinal)
                .ThenBy(c => c.Access.Runtime, StringComparer.Ordinal).ThenBy(c => c.Access.Where, StringComparer.Ordinal).ToList();
            return result;
        }

        /// <summary>Why a model cannot take the task, before runtime and capacity; null when it can.</summary>
        private static string Check(FreeModel m, Requirements req)
        {
            bool confirmed = m.Free == null || m.Free.Confirmed;
            if (!confirmed && !(req.CostPolicy == FreeOrUnconfirmed && m.Free.Status == "unknown")) return "price_not_confirmed";
            if (req.Tools && !m.Tools) return m.FeaturesUnknown ? "tools_unknown" : "no_tools";
            if (req.MinContext > 0 && m.Context < req.MinContext) return m.Context == 0 ? "context_unknown" : "context_too_small";
            if (req.MinOutput > 0 && m.MaxOutput < req.MinOutput) return m.MaxOutput == 0 ? "output_unknown" : "output_too_small";
            return null;
        }

        /// <summary>
        /// The room a pool has now: blocked while the service asked AI Meter to wait (rate_limited) or a reported counter
        /// is used up (exhausted); at most one new task without a known concurrency limit. Ratio is the remaining share
        /// of the tightest limit whose limit and remaining are both known, or null when none is.
        /// </summary>
        public static PoolRoom Room(string service, IList<RateLimit> rates, DateTime now, LeaseBook leases = null, string pool = null)
        {
            var room = new PoolRoom();
            DateTime wait = NetworkBackoff.Until(service);
            var exhausted = rates.FirstOrDefault(r => r.Remaining.HasValue && r.Remaining.Value <= 0);
            if (wait > now) { room.Blocking = "rate_limited"; room.Until = wait; }
            else if (exhausted != null) { room.Blocking = "exhausted"; room.Metric = exhausted.Metric; room.Until = exhausted.ResetsAt; }
            var concurrency = rates.FirstOrDefault(r => r.Metric == "concurrent_requests" && r.Limit.HasValue);
            room.MaxNewJobs = room.Blocking != null ? 0 : concurrency == null ? 1 : (int)Math.Max(0, concurrency.Limit.Value - (concurrency.Used ?? 0));
            var known = rates.Where(r => r.Limit.HasValue && r.Limit.Value > 0 && r.Remaining.HasValue).ToList();
            if (known.Count > 0)
            {
                var tightest = known.OrderBy(r => r.Remaining.Value / r.Limit.Value).First();
                room.Ratio = Math.Max(0, tightest.Remaining.Value / tightest.Limit.Value);
                room.BindingMetric = tightest.Metric;
            }
            if (leases != null && pool != null) ApplyLeases(room, leases, pool, now);
            return room;
        }

        /// <summary>
        /// Count the pool's active leases against its room, and close it while an agent's rate limit lasts. A pool whose
        /// room is all leased is blocked ("leased") until its first lease expires or is released.
        /// </summary>
        private static void ApplyLeases(PoolRoom room, LeaseBook leases, string pool, DateTime now)
        {
            room.Leased = leases.Active(pool, now);
            DateTime agentWait = leases.BackoffUntil(pool);
            if (room.Blocking == null && agentWait > now)
            {
                room.Blocking = "rate_limited";
                room.Until = agentWait;
                room.MaxNewJobs = 0;
            }
            if (room.Blocking != null) return;
            room.MaxNewJobs = Math.Max(0, room.MaxNewJobs - room.Leased);
            if (room.MaxNewJobs == 0)
            {
                room.Blocking = "leased";
                room.Until = leases.EarliestExpiry(pool, now);
            }
        }

        // ------------------------------------------------------------ runtimes

        /// <summary>Random per run, so credential ids cannot be matched across PCs or restarts.</summary>
        private static readonly string Salt = Guid.NewGuid().ToString("N");

        /// <summary>
        /// Coding agents that can run a delegated task on a free model with their own keys: the folders that show they
        /// are set up in a home, and the program that shows they are still installed (a folder can outlive its agent).
        /// App names the agent in KeyFinder's sources. Adding an agent here makes delegation consider it; running tasks
        /// through it also needs its dispatch description.
        /// </summary>
        internal static readonly RuntimeKind[] Kinds =
        {
            new RuntimeKind { Id = "pi", Name = "Pi", App = "Pi", Program = "pi", Folders = new[] { @".pi\agent" } },
            new RuntimeKind { Id = "opencode", Name = "OpenCode", App = "OpenCode", Program = "opencode", Folders = new[] { @".local\share\opencode", @".config\opencode" } },
            new RuntimeKind { Id = "crush", Name = "Crush", App = "Crush", Program = "crush", Folders = new[] { @".config\crush", @".local\share\crush" } },
        };

        /// <summary>Where package managers put agents' programs, relative to a home.</summary>
        private static readonly string[] HomeBins = { @".npm-global\bin", @".local\bin", @".opencode\bin", @".bun\bin", @"go\bin", @".volta\bin", @".local\share\pnpm" };

        /// <summary>Linux system folders for programs, relative to the distribution's root.</summary>
        private static readonly string[] SystemBins = { @"usr\local\bin", @"usr\bin", @"home\linuxbrew\.linuxbrew\bin" };

        /// <summary>
        /// Whether the agent's program is installed for this home. In WSL the usual Linux folders are checked through
        /// the distribution's file share, so nothing is started inside WSL; on Windows, PATH and npm's global folder.
        /// Node version managers that keep programs elsewhere are not covered.
        /// </summary>
        internal static bool Installed(RuntimeKind kind, string home, bool wsl) { return ProgramPath(kind, home, wsl) != null; }

        /// <summary>
        /// The agent's program as this PC sees it (in WSL, through the distribution's file share), or null when it is not
        /// installed in the usual folders.
        /// </summary>
        internal static string ProgramPath(RuntimeKind kind, string home, bool wsl)
        {
            var dirs = HomeBins.Select(b => Path.Combine(home, b)).ToList();
            if (wsl)
            {
                var root = DistributionRoot(home);
                if (root != null) dirs.AddRange(SystemBins.Select(b => Path.Combine(root, b)));
                return dirs.Select(d => Path.Combine(d, kind.Program)).FirstOrDefault(File.Exists);
            }
            dirs.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm"));
            dirs.AddRange((Environment.GetEnvironmentVariable("PATH") ?? "").Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries));
            var names = new[] { ".exe", ".cmd", ".bat", ".ps1" }.Select(x => kind.Program + x).ToList();
            foreach (var d in dirs)
            {
                try
                {
                    var found = names.Select(n => Path.Combine(d.Trim(), n)).FirstOrDefault(File.Exists);
                    if (found != null) return found;
                }
                catch (ArgumentException) { }   // A malformed PATH entry.
            }
            return null;
        }

        /// <summary>The distribution's root (\\wsl.localhost\Distro) for a home under it (…\home\user).</summary>
        private static string DistributionRoot(string home)
        {
            var parent = Directory.GetParent(home);
            return parent == null || parent.Parent == null ? null : parent.Parent.FullName;
        }

        /// <summary>A path under a distribution's file share as Linux sees it: \\wsl.localhost\Distro\home\me\x → /home/me/x.</summary>
        internal static string LinuxPath(string path, string home)
        {
            var root = DistributionRoot(home);
            if (root == null || !path.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return null;
            return "/" + path.Substring(root.Length).TrimStart('\\').Replace('\\', '/');
        }

        /// <summary>
        /// Which supported agents are set up (on Windows, and in WSL while WSL runs) and which services each can call
        /// with its own key: one kept in that agent's files, or in the environment it starts with (this process's
        /// variables on Windows, shell-profile exports in WSL). Keys AI Meter keeps never count: it does not hand them
        /// to an agent. Follows the FindKeys setting, as reading these files is key discovery.
        /// </summary>
        public static RuntimeScan ScanRuntimes()
        {
            var scan = new RuntimeScan();
            var homes = new List<Tuple<string, bool>> { Tuple.Create(Probe.Home, false) };
            string wsl = Probe.WslHomeUp;
            if (wsl != null) homes.Add(Tuple.Create(wsl, true));
            var found = new List<Tuple<RuntimeKind, string, bool>>();
            foreach (var kind in Kinds)
                foreach (var h in homes.Where(h => kind.Folders.Any(f => Directory.Exists(Path.Combine(h.Item1, f))) && Installed(kind, h.Item1, h.Item2)))
                {
                    found.Add(Tuple.Create(kind, h.Item1, h.Item2));
                    scan.Found.Add(new RuntimePlace { Runtime = kind.Id, Where = h.Item2 ? "wsl" : "windows" });
                }
            if (Settings.Current == null || !Settings.Current.FindKeys) { scan.KeyDiscoveryOff = true; return scan; }
            foreach (var d in Definitions.All.Where(x => x.UsesKey))
                foreach (var f in found)
                {
                    string alias;
                    string key = RuntimeKey(f.Item1, d, f.Item2, f.Item3, out alias);
                    if (key == null) continue;
                    var access = Access(d.Id, f.Item1.Id, f.Item3 ? "wsl" : "windows", key);
                    access.Provider = alias ?? ServiceDefinition.Strings(d.Auth, "ids").DefaultIfEmpty(d.Id).First();
                    string program = ProgramPath(f.Item1, f.Item2, f.Item3);
                    access.Program = program != null && f.Item3 ? LinuxPath(program, f.Item2) : program;
                    scan.Access.Add(access);
                }
            return scan;
        }

        /// <summary>
        /// The key an agent would use for a service: its own, or one from the environment it starts with. Alias is the
        /// provider id the agent keeps it under, when it is in the agent's own files.
        /// </summary>
        private static string RuntimeKey(RuntimeKind kind, ServiceDefinition d, string home, bool wsl, out string alias)
        {
            alias = null;
            var found = KeyFinder.SearchAll(d, home, wsl).FirstOrDefault(f => f.App == kind.App || (wsl && f.App == "profile"));
            if (found != null) { alias = found.Alias; return found.Key; }
            if (wsl) return null;
            foreach (var name in ServiceDefinition.Strings(d.Auth, "env"))
            {
                string v = ServiceDefinition.Env(name);
                if (!string.IsNullOrEmpty(v)) return v.Trim();
            }
            return null;
        }

        internal static RuntimeAccess Access(string service, string runtime, string where, string key)
        {
            string id;
            using (var sha = SHA256.Create())
                id = "cred_" + BitConverter.ToString(sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(Salt + "\n" + service + "\n" + key))).Replace("-", "").Substring(0, 16).ToLowerInvariant();
            return new RuntimeAccess { Service = service, Runtime = runtime, Where = where, Credential = id };
        }
    }

    /// <summary>What a task needs, and which prices it accepts.</summary>
    internal sealed class Requirements
    {
        public bool Tools;
        public long MinContext, MinOutput;
        public string CostPolicy = Candidates.FreeOnly;
        public string Service;
    }

    /// <summary>A runtime that can call a service with its own key; Credential is an opaque id, never the key or its file.</summary>
    internal sealed class RuntimeAccess
    {
        public string Service, Runtime, Where, Credential;
        /// <summary>The provider id the agent knows the service by, such as "google" for the Gemini API.</summary>
        public string Provider;
        /// <summary>The agent's program where it runs: a Linux path in WSL, a Windows path on Windows; null when unknown.</summary>
        public string Program;
    }

    /// <summary>A coding agent delegation can use (see Candidates.Kinds).</summary>
    internal sealed class RuntimeKind
    {
        public string Id, Name, App, Program;
        public string[] Folders;
    }

    /// <summary>An agent set up in one place: "windows", or "wsl".</summary>
    internal sealed class RuntimePlace
    {
        public string Runtime, Where;
    }

    /// <summary>Where agents are set up and what they can call.</summary>
    internal sealed class RuntimeScan
    {
        public readonly List<RuntimePlace> Found = new List<RuntimePlace>();
        public readonly List<RuntimeAccess> Access = new List<RuntimeAccess>();
        public bool KeyDiscoveryOff;
    }

    /// <summary>A pool's room now (see Candidates.Room).</summary>
    internal sealed class PoolRoom
    {
        public string Blocking, Metric, BindingMetric;
        public DateTime? Until;
        public int MaxNewJobs, Leased;
        public double? Ratio;
        public bool CanStart { get { return Blocking == null && MaxNewJobs > 0; } }
    }

    internal sealed class Candidate
    {
        public ProviderState Provider;
        public FreeModel Model;
        public RuntimeAccess Access;
        public PoolRoom Room;
        public string Pool;
    }

    /// <summary>The ordered candidates and the excluded models, grouped by service, reason and time.</summary>
    internal sealed class Selection
    {
        public List<Candidate> Chosen = new List<Candidate>();
        public readonly List<Exclusion> Excluded = new List<Exclusion>();

        public void Exclude(string service, string model, string reason, DateTime? until)
        {
            var group = Excluded.FirstOrDefault(e => e.Service == service && e.Reason == reason && e.Until == until);
            if (group == null) Excluded.Add(group = new Exclusion { Service = service, Reason = reason, Until = until });
            group.Models.Add(model);
        }
    }

    internal sealed class Exclusion
    {
        public string Service, Reason;
        public DateTime? Until;
        public readonly List<string> Models = new List<string>();
    }
}
