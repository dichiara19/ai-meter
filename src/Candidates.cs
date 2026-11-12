using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace AIMeter
{
    /// <summary>
    /// Execution candidates (docs/plans/orchestration-contracts.md sections 2.1 and 2.4): the free models a task can run
    /// on now, through a coding agent on this PC that already holds its own key for the service, and why every other
    /// model cannot. Delegation uses whichever supported agents the user has: those of agents/*.json with a verified
    /// dispatch for the place they are in. Other agents it finds are reported as unsupported, with the reason.
    /// Nothing here starts a process or reserves capacity: coordinator plugins run the agents (ADR 0002), and capacity
    /// leases reserve room.
    ///
    /// Selection is deterministic: the same snapshot, request, leases and known model sizes give the same order (see
    /// Ordered). Models that share a pool share its limits, so capacity is reported per pool and never added up across
    /// the pool's models.
    ///
    /// Capacity already paid for in a subscription plan (Claude, ChatGPT) is offered too, only when asked for and
    /// allowed in Settings: see CandidatesPlans.cs and ADR 0003.
    /// </summary>
    internal static partial class Candidates
    {
        public const string FreeOnly = "free_only";
        public const string FreeOrUnconfirmed = "free_or_unconfirmed";
        /// <summary>The kind of a free status whose models are priced but free within a daily allocation (FreeStatus.Kind).</summary>
        public const string FreeTier = "free_tier";

        internal static bool IsFreeTier(FreeModel m) { return m.Free != null && m.Free.Kind == FreeTier; }

        /// <summary>Exclusions an agent's own model list can make up for: the price or a capability the service does not state.</summary>
        private static readonly string[] Unstated = { "price_not_confirmed", "tools_unknown", "context_unknown", "output_unknown" };

        /// <summary>
        /// Choose the candidates for a task and explain each exclusion. A model the service's list leaves short (its
        /// price, tool support, context or reply size unstated) can still be a best-effort candidate through an agent
        /// whose own list has the model at no price and fills in what the task needs. Those are listed apart, only when
        /// the request asks for them; otherwise they are counted in BestEffortAvailable and keep their exclusion.
        /// A free-tier model (priced, free within a daily allocation) is a candidate only when the request accepts free
        /// tiers, and never a best-effort one: its budget rests on facts the service and the agent both state.
        /// </summary>
        public static Selection Select(Snapshot snap, RuntimeScan runtimes, Requirements req, DateTime now, LeaseBook leases = null)
        {
            var result = new Selection();
            foreach (var p in snap.Providers.Where(x => x.Catalogue != null && (req.Service == null || string.Equals(x.Id, req.Service, StringComparison.OrdinalIgnoreCase))))
            {
                // Sorted so that the agent whose reason is reported does not depend on the scan's order.
                var access = runtimes.Access.Where(a => a.Service == p.Id).OrderBy(a => a.Runtime, StringComparer.Ordinal)
                    .ThenBy(a => a.Where, StringComparer.Ordinal).ThenBy(a => a.Distro, StringComparer.Ordinal).ToList();
                foreach (var m in p.Catalogue.Models)
                {
                    string pool = p.Catalogue.PoolOf(m) ?? p.Id;
                    var room = Room(p.Id, p.Catalogue.Rates, now, leases, pool, BudgetOf(p.Catalogue, leases, pool, now));
                    string reason = Check(m, req);
                    if (reason == null && access.Count == 0) reason = runtimes.KeyDiscoveryOff ? "key_discovery_off" : runtimes.Found.Count == 0 ? "no_runtime" : "runtime_has_no_key";
                    if (reason == null && room.Blocking != null) reason = room.Blocking;
                    if (reason == null)
                    {
                        // An agent prices requests from its own list, and the plugin stops it at the first priced one,
                        // after paying for it: a free model the agent lists at a price is never offered through it. A
                        // free-tier model is the opposite: the plugin stops the agent when its priced requests reach
                        // the budget, so it is offered only through an agent whose list prices it, or spend is unseen.
                        bool freeTier = IsFreeTier(m), offered = false;
                        foreach (var a in access)
                        {
                            var known = AgentModels.Find(a, m.Id);
                            if (freeTier ? known == null || !known.Priced : known != null && known.Priced) continue;
                            result.Chosen.Add(new Candidate { Provider = p, Model = m, Access = a, Room = room, Pool = pool });
                            offered = true;
                        }
                        if (!offered) result.Exclude(p.Id, m.Id, freeTier ? "agent_price_unknown" : "agent_lists_price", null);
                        continue;
                    }
                    if (Unstated.Contains(reason) && room.Blocking == null && !IsFreeTier(m) && EstimateFrom(result, p, m, access, req, room, ref reason)) continue;
                    result.Exclude(p.Id, m.Id, reason, reason == room.Blocking ? room.Until : null);
                }
            }
            SelectPlans(snap, runtimes, req, now, leases, result);
            result.Chosen = Ordered(result.Chosen, req, leases, now);
            result.BestEffort = Ordered(result.BestEffort, req, leases, now);
            return result;
        }

        /// <summary>
        /// Try each agent's own list for a model the service's list leaves short. Returns true when the model went to the
        /// best-effort list. Otherwise, when best effort was asked for, reason becomes the first agent's own reason, such
        /// as agent_lists_price, if an agent lists the model.
        /// </summary>
        private static bool EstimateFrom(Selection result, ProviderState p, FreeModel m, List<RuntimeAccess> access, Requirements req, PoolRoom room, ref string reason)
        {
            bool added = false;
            string agentReason = null;
            foreach (var a in access)
            {
                List<string> unverified;
                var known = AgentModels.Find(a, m.Id);
                string why = Estimate(m, known, req, out unverified);
                if (why != null)
                {
                    if (known != null && agentReason == null) agentReason = why;
                    continue;
                }
                if (!req.BestEffort) { result.BestEffortAvailable++; continue; }
                result.BestEffort.Add(new Candidate { Provider = p, Model = m, Access = a, Room = room, Pool = p.Catalogue.PoolOf(m) ?? p.Id, Estimated = known, Unverified = unverified });
                added = true;
            }
            if (!added && req.BestEffort && agentReason != null) reason = agentReason;
            return added;
        }

        /// <summary>Known room below this share of the pool's tightest limit puts its candidates after the others.</summary>
        public const double LowRoomShare = 0.2;
        /// <summary>A stated reply size below this is too short for most coding tasks: such models come after the others.</summary>
        public const long SmallReplyTokens = 16384;

        /// <summary>
        /// The order of candidates (docs/MCP.md, "Order"), most suitable first. Every candidate's pool can start a task.
        /// Free candidates come before plan ones, except a plan whose room is about to be lost (PlanTier, ADR 0003);
        /// plan candidates keep their plan's own model order. Then, by the first key that differs:
        /// 1. the user's preference (Settings.DelegationModels): preferred, then the others, then avoided;
        /// 2. room: pools with at least LowRoomShare left, or with unknown room, before pools with less;
        /// 3. the model's track record on this PC (LeaseBook.Record), highest score first; an untried model scores 0;
        /// 4. a stated reply size of at least SmallReplyTokens before a smaller one;
        /// 5. the model's total weights (ModelSizes): unknown first, as closed models are often large, then largest first;
        /// 6. the largest context window;
        /// 7. the output speed measured here, fastest first, then models never timed;
        /// 8. service and model id;
        /// then, for the same model, the agent that sends fewer tokens per request, the agent in the place the plugin
        /// runs (Requirements.From), and agent id, place and distribution. Each candidate keeps its Rank, which
        /// list_candidates explains.
        /// </summary>
        private static List<Candidate> Ordered(List<Candidate> candidates, Requirements req, LeaseBook leases, DateTime now)
        {
            foreach (var c in candidates) c.Rank = RankOf(c, req, leases, now);
            return candidates.OrderBy(c => PlanTier(c, now)).ThenBy(c => c.Plan == null ? 0 : c.Plan.ModelOrder)
                .ThenBy(c => c.Rank.Preference == "preferred" ? 0 : c.Rank.Preference == "avoided" ? 2 : 1)
                .ThenBy(c => c.Rank.LowRoom ? 1 : 0)
                .ThenByDescending(c => c.Rank.Record.Score)
                .ThenBy(c => c.Rank.SmallReply ? 1 : 0)
                .ThenBy(c => c.Rank.Size == null ? 0 : 1).ThenByDescending(c => c.Rank.Size == null ? 0 : c.Rank.Size.Parameters)
                .ThenByDescending(c => c.Rank.Context)
                .ThenBy(c => c.Rank.Record.TokensPerSecond.HasValue ? 0 : 1).ThenByDescending(c => c.Rank.Record.TokensPerSecond ?? 0)
                .ThenBy(c => c.Provider.Id, StringComparer.Ordinal).ThenBy(c => c.Model.Id, StringComparer.Ordinal)
                .ThenBy(c => c.Rank.RequestTokens.HasValue ? 0 : 1).ThenBy(c => c.Rank.RequestTokens ?? 0)
                .ThenBy(c => c.Rank.SamePlace == false ? 1 : 0)
                .ThenBy(c => c.Access.Runtime, StringComparer.Ordinal).ThenBy(c => c.Access.Where, StringComparer.Ordinal)
                .ThenBy(c => c.Access.Distro, StringComparer.Ordinal).ToList();
        }

        /// <summary>The values Ordered sorts a candidate by. A best-effort candidate's context and reply size may be the agent's.</summary>
        private static Rank RankOf(Candidate c, Requirements req, LeaseBook leases, DateTime now)
        {
            string preference;
            if (req.Preferences == null || !req.Preferences.TryGetValue(PreferenceKey(c.Provider.Id, c.Model.Id), out preference) || (preference != Preferred && preference != Avoided))
                preference = "neutral";
            long output = c.Model.MaxOutput > 0 ? c.Model.MaxOutput : c.Estimated != null ? c.Estimated.MaxOutput : 0;
            var kind = Agents.Get(c.Access.Runtime);
            return new Rank
            {
                Preference = preference,
                LowRoom = c.Room.Ratio.HasValue && c.Room.Ratio.Value < LowRoomShare,
                RoomRatio = c.Room.Ratio,
                Record = leases == null ? new LeaseBook.ModelRecord { Days = LeaseBook.KeepDays } : leases.Record(c.Provider.Id, c.Model.Id, now),
                SmallReply = output > 0 && output < SmallReplyTokens,
                Size = ModelSizes.Of(c.Model),
                Context = c.Model.Context > 0 ? c.Model.Context : c.Estimated != null ? c.Estimated.Context : 0,
                MaxOutput = output,
                RequestTokens = kind == null ? null : kind.RequestTokens,
                SamePlace = req.From == null ? (bool?)null : c.Access.Where == req.From,
            };
        }

        /// <summary>Values of Settings.DelegationModels.</summary>
        public const string Preferred = "preferred", Avoided = "avoided";

        /// <summary>The key of a model in Settings.DelegationModels: "service|model".</summary>
        public static string PreferenceKey(string service, string model) { return service + "|" + model; }

        /// <summary>
        /// Whether a model can take the task on what an agent's own list says, where the service's list is silent; null
        /// when it can, with what rests on the agent's word in unverified ("price", "tools", "context", "output").
        /// The agent must list the model at no price: a priced model would make the plugin stop at the first request.
        /// </summary>
        private static string Estimate(FreeModel m, AgentModel known, Requirements req, out List<string> unverified)
        {
            unverified = new List<string>();
            if (m.Free != null && !m.Free.Confirmed)
            {
                if (m.Free.Status != "unknown") return "price_not_confirmed";
                unverified.Add("price");
            }
            if (known == null) return "not_in_agent_list";
            if (known.Priced) return "agent_lists_price";
            if (req.Tools)
            {
                bool tools = m.FeaturesUnknown ? known.Tools == true : m.Tools;
                if (!tools) return m.FeaturesUnknown && known.Tools == null ? "tools_unknown" : "no_tools";
                if (m.FeaturesUnknown) unverified.Add("tools");
            }
            long context = m.Context > 0 ? m.Context : known.Context, output = m.MaxOutput > 0 ? m.MaxOutput : known.MaxOutput;
            if (req.MinContext > 0)
            {
                if (context < req.MinContext) return context == 0 ? "context_unknown" : "context_too_small";
                if (m.Context == 0) unverified.Add("context");
            }
            if (req.MinOutput > 0)
            {
                if (output < req.MinOutput) return output == 0 ? "output_unknown" : "output_too_small";
                if (m.MaxOutput == 0) unverified.Add("output");
            }
            return null;
        }

        /// <summary>Why a model cannot take the task, before runtime and capacity; null when it can.</summary>
        private static string Check(FreeModel m, Requirements req)
        {
            if (IsFreeTier(m) && !req.FreeTier) return "free_tier_not_allowed";
            bool confirmed = m.Free == null || m.Free.Confirmed;
            if (!confirmed && !(req.CostPolicy == FreeOrUnconfirmed && m.Free.Status == "unknown")) return "price_not_confirmed";
            return Needs(m, req);
        }

        /// <summary>Why a model cannot meet what the task needs (tools, context, reply size); null when it can. Unknown limits fail a stated need.</summary>
        internal static string Needs(FreeModel m, Requirements req)
        {
            if (req.Tools && !m.Tools) return m.FeaturesUnknown ? "tools_unknown" : "no_tools";
            if (req.MinContext > 0 && m.Context < req.MinContext) return m.Context == 0 ? "context_unknown" : "context_too_small";
            if (req.MinOutput > 0 && m.MaxOutput < req.MinOutput) return m.MaxOutput == 0 ? "output_unknown" : "output_too_small";
            return null;
        }

        /// <summary>
        /// The room a pool has now: blocked while the service asked AI Meter to wait (rate_limited) or a reported counter
        /// is used up (exhausted); at most one new task without a known concurrency limit. Ratio is the remaining share
        /// of the tightest limit whose limit and remaining are both known, or null when none is. A free tier's budget
        /// (see BudgetOf) also closes the pool: when nothing is left beyond the reserve (exhausted), and when it is unknown
        /// (allowance_unknown), since a task could then be billed.
        /// </summary>
        public static PoolRoom Room(string service, IList<RateLimit> rates, DateTime now, LeaseBook leases = null, string pool = null, Budget budget = null)
        {
            var room = new PoolRoom { Budget = budget };
            DateTime wait = NetworkBackoff.Until(service);
            var exhausted = rates.FirstOrDefault(r => r.Remaining.HasValue && r.Remaining.Value <= 0);
            if (wait > now) { room.Blocking = "rate_limited"; room.Until = wait; }
            else if (exhausted != null) { room.Blocking = "exhausted"; room.Metric = exhausted.Metric; room.Until = exhausted.ResetsAt; }
            else if (budget != null && budget.Spendable == null) { room.Blocking = "allowance_unknown"; room.Metric = budget.Allowance.Metric; }
            else if (budget != null && budget.Spendable <= 0) { room.Blocking = "exhausted"; room.Metric = budget.Allowance.Metric; room.Until = budget.ResetsAt; }
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
        /// What a free tier leaves for one task now; null for a catalogue without an allowance. The allocation is the
        /// allowance's rate limit: its limit and the service's count of what was used. The count can lag (Cloudflare's
        /// analytics by a few minutes), so the cost released with today's leases, in units, stands in when it is
        /// larger; both are counted per UTC day, as Cloudflare's allocation is. The reserve is kept back, and MaxCost is
        /// what a task may spend in US dollars, in the agent's own prices, before the plugin stops it.
        /// </summary>
        public static Budget BudgetOf(Catalogue catalogue, LeaseBook leases, string pool, DateTime now)
        {
            var allowance = catalogue == null ? null : catalogue.Allowance;
            if (allowance == null) return null;
            var budget = new Budget { Allowance = allowance };
            var rate = catalogue.Rates.FirstOrDefault(r => r.Metric == allowance.Metric);
            if (rate == null || !rate.Limit.HasValue || !rate.Used.HasValue) return budget;
            budget.Limit = rate.Limit;
            budget.ResetsAt = rate.ResetsAt;
            budget.LeasedCostToday = leases == null || pool == null ? 0 : leases.Today(pool, now).Cost;
            budget.Used = Math.Max(rate.Used.Value, budget.LeasedCostToday / allowance.UsdPerUnit);
            budget.Spendable = Math.Max(0, rate.Limit.Value - budget.Used.Value - allowance.Reserve);
            budget.MaxCost = budget.Spendable * allowance.UsdPerUnit;
            return budget;
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
        /// The coding agents AI Meter knows (agents/*.json and %APPDATA%\AIMeter\agents): the folders that show they are
        /// set up in a home, the program that shows they are still installed (a folder can outlive its agent), where
        /// they keep keys and, for those delegation supports, how a plugin starts them.
        /// </summary>
        internal static IList<RuntimeKind> Kinds { get { return Agents.All; } }

        /// <summary>
        /// Where package managers and Node version managers put agents' programs, relative to a home; "*" stands for
        /// each installed version, newest first.
        /// </summary>
        private static readonly string[] HomeBins =
        {
            @".npm-global\bin", @".local\bin", @".opencode\bin", @".bun\bin", @"go\bin", @".volta\bin", @".local\share\pnpm",
            @".nvm\versions\node\*\bin", @".local\share\fnm\node-versions\*\installation\bin", @".fnm\node-versions\*\installation\bin",
            @".asdf\installs\nodejs\*\bin", @".asdf\shims",
        };

        /// <summary>Where npm keeps global packages in WSL, relative to a home, for the folders of HomeBins.</summary>
        private static readonly string[] HomeModules =
        {
            @".npm-global\lib\node_modules", @".local\lib\node_modules", @".nvm\versions\node\*\lib\node_modules",
            @".local\share\fnm\node-versions\*\installation\lib\node_modules", @".fnm\node-versions\*\installation\lib\node_modules",
            @".asdf\installs\nodejs\*\lib\node_modules",
        };

        /// <summary>Linux system folders for programs and global npm packages, relative to the distribution's root.</summary>
        private static readonly string[] SystemBins = { @"usr\local\bin", @"usr\bin", @"home\linuxbrew\.linuxbrew\bin" };
        private static readonly string[] SystemModules = { @"usr\local\lib\node_modules", @"usr\lib\node_modules" };

        /// <summary>
        /// Whether the agent's program is installed for this home. In WSL the usual Linux folders are checked through
        /// the distribution's file share, so nothing is started inside WSL; on Windows, PATH and npm's global folder.
        /// </summary>
        internal static bool Installed(RuntimeKind kind, string home, bool wsl) { return ProgramPath(kind, home, wsl) != null; }

        /// <summary>
        /// The agent's program as this PC sees it (in WSL, through the distribution's file share), or null when it is not
        /// installed in the usual folders. dirs are the folders to search, from SearchDirs.
        /// </summary>
        internal static string ProgramPath(RuntimeKind kind, string home, bool wsl, IList<string> dirs = null)
        {
            string where = wsl ? "wsl" : "windows";
            var names = kind.ProgramsIn(where);
            // On Windows a native program first, then the wrappers npm writes.
            var files = wsl ? names : names.SelectMany(n => new[] { ".exe", ".cmd", ".bat", ".ps1" }.Select(x => n + x)).ToArray();
            string[] own;
            var ownDirs = kind.ProgramFolders.TryGetValue(where, out own) ? own.SelectMany(f => ExpandNewest(home, f)) : Enumerable.Empty<string>();
            foreach (var d in (dirs ?? SearchDirs(home, wsl)).Concat(ownDirs))
                foreach (var f in files)
                {
                    try { if (File.Exists(Path.Combine(d, f))) return Path.Combine(d, f); }
                    catch (ArgumentException) { }   // A malformed PATH entry.
                }
            return null;
        }

        /// <summary>The existing folders where a home's programs are, in the order they are searched.</summary>
        internal static List<string> SearchDirs(string home, bool wsl)
        {
            var dirs = HomeBins.SelectMany(b => Expand(home, b)).ToList();
            if (wsl)
            {
                var root = DistributionRoot(home);
                if (root != null) dirs.AddRange(SystemBins.Select(b => Path.Combine(root, b)));
            }
            else
            {
                dirs.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm"));
                dirs.AddRange((Environment.GetEnvironmentVariable("PATH") ?? "").Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries).Select(d => d.Trim()));
            }
            return dirs.Where(d =>
            {
                try { return Directory.Exists(d); }
                catch (ArgumentException) { return false; }
            }).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        /// <summary>A home-relative pattern as paths, each "*" standing for every folder there, newest version first.</summary>
        internal static IEnumerable<string> Expand(string home, string pattern)
        {
            IEnumerable<string> paths = new[] { home };
            foreach (var part in pattern.Split('\\'))
            {
                string name = part;
                paths = name == "*" ? paths.SelectMany(p => Probe.SafeDirs(p).OrderByDescending(VersionKey, StringComparer.Ordinal)) : paths.Select(p => Path.Combine(p, name));
            }
            return paths;
        }

        /// <summary>
        /// A home-relative pattern as paths, each "*" standing for every folder there, the most recently written first:
        /// for folders named by a build hash rather than a version (RuntimeKind.ProgramFolders).
        /// </summary>
        internal static IEnumerable<string> ExpandNewest(string home, string pattern)
        {
            IEnumerable<string> paths = new[] { home };
            foreach (var part in pattern.Split('\\'))
            {
                string name = part;
                paths = name == "*" ? paths.SelectMany(p => Probe.SafeDirs(p).OrderByDescending(WrittenAt)) : paths.Select(p => Path.Combine(p, name));
            }
            return paths;
        }

        private static DateTime WrittenAt(string folder)
        {
            try { return Directory.GetLastWriteTimeUtc(folder); }
            catch (IOException) { return DateTime.MinValue; }
            catch (UnauthorizedAccessException) { return DateTime.MinValue; }
        }

        /// <summary>A folder name's numbers padded so that ordinal order is version order: v9.0.0 before v22.1.0.</summary>
        private static string VersionKey(string path)
        {
            return string.Join(".", Regex.Matches(Path.GetFileName(path), @"\d+").Cast<Match>().Select(m => m.Value.PadLeft(10, '0')));
        }

        /// <summary>
        /// The folder of the agent's npm package, or null when npm did not install it: next to its program first, then
        /// npm's global folders (in WSL, also those of Node version managers).
        /// </summary>
        internal static string PackageFolder(RuntimeKind kind, string home, bool wsl, string program)
        {
            if (kind.Package == null) return null;
            var roots = new List<string>();
            if (program != null) roots.Add(Path.Combine(Path.GetDirectoryName(program), wsl ? @"..\lib\node_modules" : "node_modules"));
            if (wsl)
            {
                roots.AddRange(HomeModules.SelectMany(m => Expand(home, m)));
                var root = DistributionRoot(home);
                if (root != null) roots.AddRange(SystemModules.Select(m => Path.Combine(root, m)));
            }
            else roots.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), @"npm\node_modules"));
            foreach (var r in roots)
            {
                try
                {
                    string folder = Path.GetFullPath(Path.Combine(r, kind.Package.Replace('/', '\\')));
                    if (File.Exists(Path.Combine(folder, "package.json"))) return folder;
                }
                catch (ArgumentException) { }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            return null;
        }

        /// <summary>The installed version from the package's package.json, read without running anything; null when unknown.</summary>
        internal static string PackageVersion(string folder)
        {
            return folder == null ? null : Json.Str(Json.ParseFile(Path.Combine(folder, "package.json")), "version");
        }

        /// <summary>
        /// On Windows, the native program to start: the one found if it is an .exe, else one of the definition's
        /// executables inside its npm package (npm puts only .cmd and .ps1 wrappers, which need a shell, on the PATH).
        /// </summary>
        internal static string NativeProgram(RuntimeKind kind, string program, string package)
        {
            if (program == null || program.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return program;
            var place = kind.PlaceFor("windows");
            if (place == null || package == null) return program;
            return place.Executables.Select(e => Path.Combine(package, e)).FirstOrDefault(File.Exists) ?? program;
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
        /// Which agents are set up on Windows and in every running WSL distribution: those delegation can run, with the
        /// services each can call with its own key (one kept in that agent's files, or in the environment it starts with:
        /// this process's variables on Windows, shell-profile exports in WSL), and those it cannot, with the reason. Keys
        /// AI Meter keeps never count: it does not hand them to an agent. Follows the FindKeys setting, as reading these
        /// files is key discovery.
        ///
        /// One delegation asks for list_candidates, then describe_dispatch and claim_capacity for each candidate it
        /// tries, and each would read every agent's files again, over the WSL file share too. A scan is therefore reused
        /// for ScanReuse while the homes and the setting are the same; a key added meanwhile shows up after that.
        /// </summary>
        public static RuntimeScan ScanRuntimes()
        {
            var places = Probe.WslPlacesUp.ToList();
            bool findKeys = Settings.Current != null && Settings.Current.FindKeys;
            string inputs = Probe.Home + "|" + findKeys + "|" + string.Join("|", places.Select(p => p.Distro + "=" + p.Home));
            lock (ScanLock)
            {
                if (lastScan != null && lastScanInputs == inputs && DateTime.UtcNow - lastScanAt < ScanReuse) return lastScan;
            }
            var scan = Scan(Probe.Home, places, Kinds, findKeys);
            lock (ScanLock)
            {
                lastScan = scan;
                lastScanInputs = inputs;
                lastScanAt = DateTime.UtcNow;
            }
            return scan;
        }

        private static readonly TimeSpan ScanReuse = TimeSpan.FromSeconds(15);
        private static readonly object ScanLock = new object();
        private static RuntimeScan lastScan;
        private static string lastScanInputs;
        private static DateTime lastScanAt;

        /// <summary>ScanRuntimes over the given homes: windowsHome (null to skip Windows) and running distributions.</summary>
        internal static RuntimeScan Scan(string windowsHome, IEnumerable<WslPlace> wsl, IList<RuntimeKind> kinds, bool findKeys)
        {
            var scan = new RuntimeScan();
            var homes = new List<WslPlace>();   // Distro null: the Windows home.
            if (windowsHome != null) homes.Add(new WslPlace { Home = windowsHome });
            homes.AddRange(wsl);
            var dirs = homes.ToDictionary(h => h, h => SearchDirs(h.Home, h.Distro != null));
            var found = new List<Tuple<RuntimeKind, WslPlace, string>>();
            foreach (var kind in kinds)
                foreach (var h in homes)
                {
                    bool inWsl = h.Distro != null;
                    string where = inWsl ? "wsl" : "windows";
                    bool setUp = kind.Folders.Any(f => Directory.Exists(Path.Combine(h.Home, f)));
                    string program = ProgramPath(kind, h.Home, inWsl, dirs[h]);
                    if (!setUp && program == null) continue;
                    string reason = Unsupported(kind, where, setUp, program);
                    if (reason != null)
                    {
                        scan.Unsupported.Add(new UnsupportedRuntime { Runtime = kind.Id, Name = kind.Name, Where = where, Distro = h.Distro, Reason = reason });
                        continue;
                    }
                    found.Add(Tuple.Create(kind, h, program));
                    scan.Found.Add(new RuntimePlace { Runtime = kind.Id, Where = where, Distro = h.Distro });
                }
            // A plan agent uses its own sign-in, not a key, so it does not depend on key discovery. Whether it is signed in
            // to the plan is checked against the plan's readings (PlanRoom) and by the plugin before it starts the agent.
            foreach (var f in found.Where(x => x.Item1.Plan != null))
            {
                var access = PlanAccess(f.Item1.Plan.Service, f.Item1.Id, f.Item2.Distro != null ? "wsl" : "windows", f.Item2.Distro);
                Locate(access, f.Item1, f.Item2, f.Item3, dirs[f.Item2]);
                scan.Access.Add(access);
            }
            if (!findKeys) { scan.KeyDiscoveryOff = true; return scan; }
            foreach (var d in Definitions.All.Where(x => x.UsesKey))
                foreach (var f in found)
                {
                    bool inWsl = f.Item2.Distro != null;
                    string alias;
                    bool fromProfile;
                    string key = RuntimeKey(f.Item1, d, f.Item2.Home, inWsl, out alias, out fromProfile);
                    if (key == null) continue;
                    var access = Access(d.Id, f.Item1.Id, inWsl ? "wsl" : "windows", key);
                    access.Provider = alias ?? ServiceDefinition.Strings(d.Auth, "ids").DefaultIfEmpty(d.Id).First();
                    access.KeyFromProfile = fromProfile;
                    access.Distro = f.Item2.Distro;
                    Locate(access, f.Item1, f.Item2, f.Item3, dirs[f.Item2]);
                    scan.Access.Add(access);
                }
            return scan;
        }

        /// <summary>
        /// Fill in where an access's agent runs: its program as the plugin starts it, its version, the node it needs in
        /// WSL, its Linux home there, and its own model list for the access's provider.
        /// </summary>
        private static void Locate(RuntimeAccess access, RuntimeKind kind, WslPlace home, string program, IEnumerable<string> dirs)
        {
            bool inWsl = home.Distro != null;
            string package = PackageFolder(kind, home.Home, inWsl, program);
            access.Version = PackageVersion(package);
            if (inWsl)
            {
                access.Program = LinuxPath(program, home.Home);
                access.Home = LinuxPath(home.Home, home.Home);
                // An agent npm installed is a Node script: its PATH needs a node, perhaps in a version manager's folder.
                access.NeedsNode = package != null;
                string node = NodeFolder(program, dirs);
                access.NodeFolder = node == null ? null : LinuxPath(node, home.Home);
            }
            else access.Program = NativeProgram(kind, program, package);
            access.ModelsFile = AgentModels.FileFor(kind, home.Home, package, access.Provider, access.Version);
            access.ModelsFormat = kind.Models == null ? null : kind.Models.Format;
        }

        /// <summary>The folder of the node an agent's program would run with: its own folder first, then the search folders.</summary>
        internal static string NodeFolder(string program, IEnumerable<string> dirs)
        {
            return new[] { Path.GetDirectoryName(program) }.Concat(dirs).FirstOrDefault(d => File.Exists(Path.Combine(d, "node")));
        }

        /// <summary>Why delegation cannot use an agent found in a place, as a reason code; null when it can.</summary>
        private static string Unsupported(RuntimeKind kind, string where, bool setUp, string program)
        {
            if (kind.Dispatch == null) return "no_dispatch";
            if (kind.PlaceFor(where) == null) return "place_not_verified";
            if (program == null) return "program_not_found";
            if (!setUp) return "not_set_up";
            return null;
        }

        /// <summary>
        /// The key an agent would use for a service: its own, or one from the environment it starts with. Alias is the
        /// provider id the agent keeps it under, when it is in the agent's own files. fromProfile says the key is only
        /// exported in a WSL shell profile, which an agent has only when started from a shell in WSL.
        /// </summary>
        private static string RuntimeKey(RuntimeKind kind, ServiceDefinition d, string home, bool wsl, out string alias, out bool fromProfile)
        {
            alias = null;
            fromProfile = false;
            var found = KeyFinder.SearchAll(d, home, wsl).FirstOrDefault(f => f.App == kind.Name || (wsl && f.App == "profile"));
            if (found != null) { alias = found.Alias; fromProfile = found.App == "profile"; return found.Key; }
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
        /// <summary>Also accept free-tier models: priced, free within a daily allocation the plugin must keep to.</summary>
        public bool FreeTier;
        /// <summary>Also list best-effort candidates, built on the agents' own model lists.</summary>
        public bool BestEffort;
        /// <summary>The user's choices for delegation, "service|model" to "preferred" or "avoided" (Settings.DelegationModels).</summary>
        public IDictionary<string, string> Preferences;
        /// <summary>Where the plugin that asks runs, "windows" or "wsl": for the same model, agents there come first. Null when not said.</summary>
        public string From;
        /// <summary>Also accept capacity included in a subscription plan (ADR 0003). Off by default.</summary>
        public bool Plan;
        /// <summary>The service the coordinator itself runs on ("claude", "openai"…): its plan is never offered. Null when not said.</summary>
        public string Coordinator;
        /// <summary>The plans the user allowed in Settings (Settings.DelegationPlans), by service id; null or empty allows none.</summary>
        public ICollection<string> PlansAllowed;
        /// <summary>The share of each plan window the user keeps, "service|5h" or "service|7d" to a percentage (Settings.DelegationReserves).</summary>
        public IDictionary<string, int> Reserves;
    }

    /// <summary>A runtime that can call a service with its own key; Credential is an opaque id, never the key or its file.</summary>
    internal sealed class RuntimeAccess
    {
        public string Service, Runtime, Where, Credential;
        /// <summary>The provider id the agent knows the service by, such as "google" for the Gemini API.</summary>
        public string Provider;
        /// <summary>The agent's program where it runs: a Linux path in WSL, a Windows path on Windows; null when unknown.</summary>
        public string Program;
        /// <summary>The WSL distribution the agent is in; null on Windows.</summary>
        public string Distro;
        /// <summary>The installed version from its npm package; null when unknown.</summary>
        public string Version;
        /// <summary>In WSL: whether the program is a Node script (npm installed it), and the Linux folder of node, or null.</summary>
        public bool NeedsNode;
        public string NodeFolder;
        /// <summary>The agent's own model list for this service's provider alias, and its format; null when it has none.</summary>
        public string ModelsFile, ModelsFormat;
        /// <summary>
        /// In WSL: the key is exported in a shell profile rather than kept by the agent, so an agent started from
        /// Windows through wsl.exe, which reads no profile, would not have it.
        /// </summary>
        public bool KeyFromProfile;
        /// <summary>In WSL: the Linux home the agent's settings are in, for an agent started from an empty environment.</summary>
        public string Home;
        /// <summary>The agent draws on a subscription plan through its own sign-in, not on a key (ADR 0003).</summary>
        public bool Plan;
    }

    /// <summary>An agent set up in one place: "windows", or "wsl" with its distribution.</summary>
    internal sealed class RuntimePlace
    {
        public string Runtime, Where, Distro;
    }

    /// <summary>
    /// An agent found in a place that delegation cannot use there. Reason: no_dispatch (delegation does not support the
    /// agent), place_not_verified (not in this place), program_not_found (a settings folder, no program) or not_set_up
    /// (a program, no settings folder).
    /// </summary>
    internal sealed class UnsupportedRuntime
    {
        public string Runtime, Name, Where, Distro, Reason;
    }

    /// <summary>Where agents are set up and what they can call.</summary>
    internal sealed class RuntimeScan
    {
        public readonly List<RuntimePlace> Found = new List<RuntimePlace>();
        public readonly List<RuntimeAccess> Access = new List<RuntimeAccess>();
        public readonly List<UnsupportedRuntime> Unsupported = new List<UnsupportedRuntime>();
        public bool KeyDiscoveryOff;
    }

    /// <summary>A pool's room now (see Candidates.Room).</summary>
    internal sealed class PoolRoom
    {
        public string Blocking, Metric, BindingMetric;
        public DateTime? Until;
        public int MaxNewJobs, Leased;
        public double? Ratio;
        /// <summary>A free tier's budget, when the pool has one.</summary>
        public Budget Budget;
        public bool CanStart { get { return Blocking == null && MaxNewJobs > 0; } }
    }

    internal sealed class Candidate
    {
        public ProviderState Provider;
        public FreeModel Model;
        public RuntimeAccess Access;
        public PoolRoom Room;
        public string Pool;
        /// <summary>For a best-effort candidate: what the agent's own list says, and which facts rest on it.</summary>
        public AgentModel Estimated;
        public List<string> Unverified;
        /// <summary>Why the candidate stands where it is (Candidates.Ordered).</summary>
        public Rank Rank;
        /// <summary>For a candidate on a subscription plan: its windows, reserve and model order; null for free models.</summary>
        public PlanRoom Plan;
    }

    /// <summary>The values a candidate is ordered by (Candidates.Ordered), which list_candidates reports as its rank.</summary>
    internal sealed class Rank
    {
        public string Preference;              // "preferred", "neutral" or "avoided".
        public bool LowRoom;                   // Known room below Candidates.LowRoomShare.
        public double? RoomRatio;              // The pool's known room, as a share of its tightest limit.
        public LeaseBook.ModelRecord Record;   // The model's track record on this PC.
        public bool SmallReply;                // A stated reply size below Candidates.SmallReplyTokens.
        public ModelSize Size;                 // Total weights; null when unknown.
        public long Context, MaxOutput;        // Tokens; 0 when unknown.
        public int? RequestTokens;             // The agent's tokens per request on top of the task; null when not stated.
        public bool? SamePlace;                // The agent runs where the plugin does; null when the plugin did not say.
    }

    /// <summary>
    /// The ordered candidates, the best-effort ones when asked for (or how many there would be), and the excluded
    /// models, grouped by service, reason and time.
    /// </summary>
    internal sealed class Selection
    {
        public List<Candidate> Chosen = new List<Candidate>();
        public List<Candidate> BestEffort = new List<Candidate>();
        public int BestEffortAvailable;
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
