using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;

namespace AIMeter
{
    /// <summary>
    /// Execution candidates (docs/plans/orchestration-contracts.md sections 2.1 and 2.4): the free models a task can run
    /// on now, through a runtime that already holds its own key for the service, and why every other model cannot.
    /// Nothing here starts a process or reserves capacity; the job runner re-checks and reserves when it starts a job.
    ///
    /// Selection is deterministic: the same snapshot and request give the same order. Models that share a pool share
    /// its limits, so capacity is reported per pool and never added up across the pool's models.
    /// </summary>
    internal static class Candidates
    {
        public const string FreeOnly = "free_only";
        public const string FreeOrUnconfirmed = "free_or_unconfirmed";

        /// <summary>Choose the candidates for a task and explain each exclusion.</summary>
        public static Selection Select(Snapshot snap, RuntimeScan runtimes, Requirements req, DateTime now)
        {
            var result = new Selection();
            foreach (var p in snap.Providers.Where(x => x.Catalogue != null && (req.Service == null || string.Equals(x.Id, req.Service, StringComparison.OrdinalIgnoreCase))))
            {
                var room = Room(p.Id, p.Catalogue.Rates, now);
                var access = runtimes.Access.Where(a => a.Service == p.Id).ToList();
                foreach (var m in p.Catalogue.Models)
                {
                    string reason = Check(m, req);
                    if (reason == null && access.Count == 0) reason = runtimes.KeyDiscoveryOff ? "key_discovery_off" : runtimes.Places.Count == 0 ? "no_runtime" : "runtime_has_no_key";
                    if (reason == null && room.Blocking != null) reason = room.Blocking;
                    if (reason != null) { result.Exclude(p.Id, m.Id, reason, reason == room.Blocking ? room.Until : null); continue; }
                    foreach (var a in access)
                        result.Chosen.Add(new Candidate { Provider = p, Model = m, Access = a, Room = room, Pool = p.Catalogue.PoolOf(m) ?? p.Id });
                }
            }
            // Known room first, most room first; then unknown room; ties by service, model and runtime place.
            result.Chosen = result.Chosen.OrderBy(c => c.Room.Ratio.HasValue ? 0 : 1).ThenByDescending(c => c.Room.Ratio ?? 0)
                .ThenBy(c => c.Provider.Id, StringComparer.Ordinal).ThenBy(c => c.Model.Id, StringComparer.Ordinal)
                .ThenBy(c => c.Access.Where, StringComparer.Ordinal).ToList();
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
        public static PoolRoom Room(string service, IList<RateLimit> rates, DateTime now)
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
            return room;
        }

        // ------------------------------------------------------------ runtimes

        /// <summary>Random per run, so credential ids cannot be matched across PCs or restarts.</summary>
        private static readonly string Salt = Guid.NewGuid().ToString("N");

        /// <summary>
        /// Where Pi is set up (its ~/.pi/agent folder, on Windows and in WSL while WSL runs) and which services it can
        /// call with its own key: one kept in Pi's files, or in the environment Pi starts with (this process's
        /// variables on Windows, shell-profile exports in WSL). Keys AI Meter keeps never count: it does not hand them
        /// to a runtime. Follows the FindKeys setting, as reading these files is key discovery.
        /// </summary>
        public static RuntimeScan ScanPi()
        {
            var scan = new RuntimeScan();
            var homes = new List<Tuple<string, bool>> { Tuple.Create(Probe.Home, false) };
            string wsl = Probe.WslHomeUp;
            if (wsl != null) homes.Add(Tuple.Create(wsl, true));
            homes = homes.Where(h => Directory.Exists(Path.Combine(h.Item1, @".pi\agent"))).ToList();
            foreach (var h in homes) scan.Places.Add(h.Item2 ? "wsl" : "windows");
            if (Settings.Current == null || !Settings.Current.FindKeys) { scan.KeyDiscoveryOff = true; return scan; }
            foreach (var d in Definitions.All.Where(x => x.UsesKey))
                foreach (var h in homes)
                {
                    string key = PiKey(d, h.Item1, h.Item2);
                    if (key != null) scan.Access.Add(Access(d.Id, "pi", h.Item2 ? "wsl" : "windows", key));
                }
            return scan;
        }

        private static string PiKey(ServiceDefinition d, string home, bool wsl)
        {
            var found = KeyFinder.SearchAll(d, home, wsl).FirstOrDefault(f => f.App == "Pi" || (wsl && f.App == "profile"));
            if (found != null) return found.Key;
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
    }

    /// <summary>Where runtimes are set up and what they can call.</summary>
    internal sealed class RuntimeScan
    {
        public readonly List<string> Places = new List<string>();
        public readonly List<RuntimeAccess> Access = new List<RuntimeAccess>();
        public bool KeyDiscoveryOff;
    }

    /// <summary>A pool's room now (see Candidates.Room).</summary>
    internal sealed class PoolRoom
    {
        public string Blocking, Metric, BindingMetric;
        public DateTime? Until;
        public int MaxNewJobs;
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
