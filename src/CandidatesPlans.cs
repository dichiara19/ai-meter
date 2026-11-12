using System;
using System.Collections.Generic;
using System.Linq;

namespace AIMeter
{
    /// <summary>
    /// Candidates on subscription plans (ADR 0003): Claude Code on the Claude plan and Codex on the ChatGPT plan, drawing
    /// on room the plan's windows already pay for. They are offered only when the request asks for them (include_plan),
    /// the user allowed that plan in Settings, the coordinator said which service it runs on and it is another one, and
    /// a fresh reading shows the plan signed in, with no paid usage beyond it and room above the user's reserve in every
    /// window. Anything unknown excludes the plan, with the reason: room is never assumed.
    ///
    /// Free candidates come first and the plan is the fallback, except when a plan's room is about to be lost: a 5-hour
    /// window resetting within the hour, or a weekly one within a day (PlanTier).
    /// </summary>
    internal static partial class Candidates
    {
        /// <summary>The share of a window kept for the user when Settings name none, in percent.</summary>
        public const int DefaultReserveFiveHours = 40, DefaultReserveWeek = 25;
        /// <summary>A plan reading older than this tells nothing about the room now.</summary>
        public static readonly TimeSpan PlanReadingMaxAge = TimeSpan.FromMinutes(15);
        /// <summary>Room left this close to a window's reset is about to be lost: such a plan comes before free models.</summary>
        public static readonly TimeSpan ExpiringFiveHours = TimeSpan.FromHours(1), ExpiringWeek = TimeSpan.FromDays(1);

        /// <summary>The kind of cost of a plan candidate, as list_candidates reports it.</summary>
        public const string IncludedInPlan = "included_in_plan";

        /// <summary>The sign-in methods that show a plan: Claude's usage endpoint ("plan"), Codex's account/read ("chatgpt").</summary>
        private static readonly string[] PlanSignIns = { "plan", "chatgpt" };

        /// <summary>The pool of a plan's delegated tasks: one task at a time per plan.</summary>
        public static string PlanPool(string service) { return service + ":plan"; }

        /// <summary>The key of a reserve in Settings.DelegationReserves: "service|5h" or "service|7d".</summary>
        public static string ReserveKey(string service, string window) { return service + "|" + window; }

        /// <summary>The reserve the user keeps in a plan window, in percent: their setting, or the default.</summary>
        public static int ReserveOf(IDictionary<string, int> reserves, string service, string window)
        {
            int value;
            if (reserves != null && reserves.TryGetValue(ReserveKey(service, window), out value)) return Math.Max(0, Math.Min(100, value));
            return window == UsageWindow.ShortFiveHours ? DefaultReserveFiveHours : DefaultReserveWeek;
        }

        /// <summary>
        /// An agent's access to a plan: the service and the agent's place, with an opaque id like a key's (there is no key
        /// to hash: the agent uses its own sign-in, which AI Meter never reads).
        /// </summary>
        internal static RuntimeAccess PlanAccess(string service, string runtime, string where, string distro)
        {
            var access = Access(service, runtime, where, "plan\n" + runtime + "\n" + where + "\n" + distro);
            access.Plan = true;
            access.Provider = service;
            access.Distro = distro;
            return access;
        }

        /// <summary>
        /// The models a plan agent offers, most suitable first: those its definition lists, or else its own model list
        /// (Codex's cache of the models its sign-in offers). Each has the plan's cost status, never a price.
        /// </summary>
        internal static List<FreeModel> PlanModels(RuntimeKind kind, RuntimeAccess access)
        {
            if (kind.Plan.Models.Count > 0)
                return kind.Plan.Models.Select(m => new FreeModel { Id = m.Id, Name = m.Id, Tools = true, Context = m.Context, MaxOutput = m.MaxOutput, Free = PlanStatus }).ToList();
            return AgentModels.All(access).Select(m => new FreeModel { Id = m.Id, Name = m.Name ?? m.Id, Tools = true, Context = m.Context, Free = PlanStatus }).ToList();
        }

        internal static readonly FreeStatus PlanStatus = new FreeStatus { Status = IncludedInPlan, Kind = "plan", Source = "subscription" };

        /// <summary>
        /// Add the plan candidates of every agent with a plan, or the reason each plan's models are left out. An agent not
        /// set up anywhere adds nothing: runtimes.unsupported says why, when it was found.
        /// </summary>
        private static void SelectPlans(Snapshot snap, RuntimeScan runtimes, Requirements req, DateTime now, LeaseBook leases, Selection result)
        {
            foreach (var kind in Kinds.Where(k => k.Plan != null))
            {
                string service = kind.Plan.Service;
                if (req.Service != null && !string.Equals(service, req.Service, StringComparison.OrdinalIgnoreCase)) continue;
                var access = runtimes.Access.Where(a => a.Plan && a.Runtime == kind.Id).OrderBy(a => a.Where, StringComparer.Ordinal)
                    .ThenBy(a => a.Distro, StringComparer.Ordinal).ToList();
                if (access.Count == 0) continue;
                var models = access.Select(a => PlanModels(kind, a)).FirstOrDefault(l => l.Count > 0) ?? new List<FreeModel>();
                if (models.Count == 0)
                {
                    result.Exclude(service, kind.Id, "plan_models_unknown", null);
                    continue;
                }
                string reason = PlanGate(service, req);
                if (reason != null)
                {
                    foreach (var m in models) result.Exclude(service, m.Id, reason, null);
                    continue;
                }
                var provider = snap.Providers.FirstOrDefault(p => p.Id == service);
                for (int i = 0; i < models.Count; i++)
                {
                    // The same needs as free models, with the same reasons: a limit nobody states fails a stated need.
                    string need = Needs(models[i], req);
                    if (need != null)
                    {
                        result.Exclude(service, models[i].Id, need, null);
                        continue;
                    }
                    PlanRoom plan;
                    var room = PlanRoomOf(provider, service, models[i].Id, req.Reserves, now, leases, out plan);
                    if (room.Blocking != null)
                    {
                        result.Exclude(service, models[i].Id, room.Blocking, room.Until);
                        continue;
                    }
                    plan.ModelOrder = i;
                    foreach (var a in access)
                        result.Chosen.Add(new Candidate { Provider = provider, Model = models[i], Access = a, Room = room, Pool = PlanPool(service), Plan = plan });
                }
            }
        }

        /// <summary>Why a request may not use a plan at all, before its windows are looked at; null when it may.</summary>
        internal static string PlanGate(string service, Requirements req)
        {
            if (!req.Plan) return "plan_not_requested";
            if (req.PlansAllowed == null || !req.PlansAllowed.Contains(service)) return "plan_off_in_settings";
            if (string.IsNullOrEmpty(req.Coordinator)) return "coordinator_unknown";
            if (string.Equals(req.Coordinator, service, StringComparison.OrdinalIgnoreCase)) return "same_plan_as_coordinator";
            return null;
        }

        /// <summary>
        /// The room a plan has now for a task on a model, and its windows (plan). Blocked when the reading is missing or
        /// older than PlanReadingMaxAge (plan_usage_unknown), does not show a plan sign-in (plan_sign_in_not_confirmed),
        /// paid usage beyond the plan is on (plan_extra_usage_on for Claude, plan_credits_on for Codex), or a window is at
        /// or below the user's reserve (plan_reserve, until that window resets). The windows are the 5-hour one, the
        /// weekly one, and those of the model when the plan has them; the tightest above its reserve binds. One task at a
        /// time per plan, and the pool's leases and rate limits apply as for free pools.
        /// </summary>
        internal static PoolRoom PlanRoomOf(ProviderState provider, string service, string model, IDictionary<string, int> reserves, DateTime now,
            LeaseBook leases, out PlanRoom plan)
        {
            plan = new PlanRoom { Service = service };
            var room = new PoolRoom { MaxNewJobs = 1 };
            var u = provider == null ? null : provider.Usage;
            if (u == null || now - u.At > PlanReadingMaxAge) return Block(room, "plan_usage_unknown");
            if (u.SignIn == null || !PlanSignIns.Contains(u.SignIn)) return Block(room, "plan_sign_in_not_confirmed");
            if (u.ExtraActive) return Block(room, "plan_extra_usage_on");
            if (u.CreditsAvailable) return Block(room, "plan_credits_on");
            plan.ReadAt = u.At;

            foreach (var w in u.Windows.Where(x => x.HasPercent && x.Spent == null && !x.Hidden))
            {
                bool own = w.Short == UsageWindow.ShortFiveHours || w.Short == UsageWindow.ShortWeek;
                if (!own && !(w.Short == "m" && ForModel(w.Suffix, model))) continue;
                var length = w.Length;
                string kind = w.Short == UsageWindow.ShortFiveHours || (length.HasValue && length.Value <= TimeSpan.FromHours(5)) ? UsageWindow.ShortFiveHours : UsageWindow.ShortWeek;
                plan.Windows.Add(new PlanWindow
                {
                    Window = kind, Model = own ? null : w.Suffix, RemainingPercent = w.Remaining, ReservePercent = ReserveOf(reserves, service, kind), ResetsAt = w.ResetsAt,
                });
            }
            if (!plan.Windows.Any(w => w.Model == null)) return Block(room, "plan_usage_unknown");

            var below = plan.Windows.Where(w => w.RemainingPercent <= w.ReservePercent).ToList();
            if (below.Count > 0)
            {
                // The plan opens again only once every window below its reserve has reset: the latest of those resets.
                var resets = below.Where(w => w.ResetsAt.HasValue).Select(w => w.ResetsAt.Value).ToList();
                room.Metric = below.OrderBy(w => w.RemainingPercent - w.ReservePercent).First().Name;
                return Block(room, "plan_reserve", resets.Count == below.Count ? resets.Max() : (DateTime?)null);
            }
            plan.Binding = plan.Windows.OrderBy(w => w.Above).ThenBy(w => w.Name, StringComparer.Ordinal).First();
            room.Ratio = plan.Binding.Above;
            room.BindingMetric = plan.Binding.Name;
            plan.Expiring = plan.Windows.Any(w => w.ResetsAt.HasValue && w.ResetsAt.Value > now &&
                w.ResetsAt.Value - now <= (w.Window == UsageWindow.ShortFiveHours ? ExpiringFiveHours : ExpiringWeek));
            if (leases != null) ApplyLeases(room, leases, PlanPool(service), now);
            return room;
        }

        private static PoolRoom Block(PoolRoom room, string reason, DateTime? until = null)
        {
            room.Blocking = reason;
            room.Until = until;
            room.MaxNewJobs = 0;
            return room;
        }

        /// <summary>
        /// Whether a window kept for a group of models covers this model: Claude names them ("Opus", "Sonnet"), Codex by
        /// limit name ("GPT-5.3-Codex-Spark"); either may name the model by its family or in full.
        /// </summary>
        internal static bool ForModel(string suffix, string model)
        {
            if (string.IsNullOrEmpty(suffix) || string.IsNullOrEmpty(model)) return false;
            string s = suffix.Replace(' ', '-').ToLowerInvariant(), m = model.ToLowerInvariant();
            return s.Contains(m) || m.Contains(s);
        }

        /// <summary>
        /// Where a candidate stands against the others before any other key: 0 for a plan whose room is about to be lost,
        /// 1 for a free model, 2 for any other plan (free models first, the plan as the fallback).
        /// </summary>
        private static int PlanTier(Candidate c, DateTime now)
        {
            return c.Plan == null ? 1 : c.Plan.Expiring ? 0 : 2;
        }
    }

    /// <summary>A plan's windows as a candidate draws on them (Candidates.PlanRoomOf).</summary>
    internal sealed class PlanRoom
    {
        public string Service;
        public DateTime ReadAt;
        public readonly List<PlanWindow> Windows = new List<PlanWindow>();
        /// <summary>The window with the least room above its reserve: the one a task draws the plan down to.</summary>
        public PlanWindow Binding;
        /// <summary>A window resets soon with room above its reserve: the room is about to be lost.</summary>
        public bool Expiring;
        /// <summary>The model's place in the plan's own list, most suitable first.</summary>
        public int ModelOrder;
    }

    /// <summary>One window of a plan: "5h" or "7d", for every model (Model null) or for the models it names.</summary>
    internal sealed class PlanWindow
    {
        public string Window, Model;
        public double RemainingPercent;
        public int ReservePercent;
        public DateTime? ResetsAt;

        /// <summary>"5h", "7d", or with the models it covers: "7d Opus".</summary>
        public string Name { get { return Model == null ? Window : Window + " " + Model; } }

        /// <summary>The share of the room above the reserve that is left, from 0 to 1.</summary>
        public double Above { get { return ReservePercent >= 100 ? 0 : Math.Max(0, (RemainingPercent - ReservePercent) / (100.0 - ReservePercent)); } }
    }
}
