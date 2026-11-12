using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace AIMeter
{
    /// <summary>
    /// How a coordinator plugin starts a coding agent on one model (ADR 0002; docs/plans/orchestration-contracts.md
    /// section 3.1). AI Meter starts nothing: it describes the program, the argument list and the environment, and the
    /// plugin starts them without a shell and writes the task to stdin. The task text is never part of a description.
    ///
    /// The rows are data (agents/*.json): the built-in ones were verified live in the M4 spike
    /// (docs/plans/spike-agents.md) for an agent, a major version and a place. A combination without a dispatch entry
    /// is reported as unsupported, with the reason, rather than guessed.
    /// </summary>
    internal static class Dispatch
    {
        public const string ReadOnly = "read_only";
        public const string Modify = "modify";

        /// <summary>The structured warning that OpenRouter may bill a :free request to a provider key linked to it (BYOK).</summary>
        public const string ByokFallback = "openrouter_byok_fallback";

        /// <summary>
        /// Describe how to run the agent of an access (runtime, place and provider) on a model, from the agent's definition
        /// (agents/*.json). from is where the plugin runs ("windows" or "wsl"): a plugin on Windows reaches an agent in
        /// WSL through wsl.exe, which must already be running. workspace is the folder the task works in; it is a
        /// placeholder when not given.
        /// </summary>
        public static Plan Describe(RuntimeAccess access, string model, string mode, string workspace, string from, string distro, string version)
        {
            var plan = new Plan { Runtime = access.Runtime, Where = access.Where, Version = version, Mode = mode ?? ReadOnly };
            if (plan.Mode != ReadOnly && plan.Mode != Modify) return plan.Refuse("mode must be read_only or modify");
            if (from != "windows" && from != "wsl") return plan.Refuse("from must be windows or wsl");
            var kind = Agents.Get(access.Runtime);
            if (kind == null) return plan.Refuse("unknown runtime " + access.Runtime);
            var place = kind.PlaceFor(access.Where);
            if (place == null)
                return plan.Refuse(access.Runtime + " has not been verified " + (access.Where == "wsl" ? "in WSL" : "on Windows") + " yet");
            if (from == "wsl" && access.Where == "windows") return plan.Refuse("starting a Windows agent from WSL has not been verified yet");
            if (access.Where == "wsl" && string.IsNullOrEmpty(distro)) return plan.Refuse("no WSL distribution is known");
            plan.VersionVerified = place.Verifies(version);
            string folder = string.IsNullOrEmpty(workspace) ? "{workspace}" : workspace;

            // From Windows into WSL, wsl.exe --cd already sets the folder, given as a Windows path the agent would not
            // understand inside WSL; the agent then works in its current folder.
            var values = new Dictionary<string, string>
            {
                { "provider", access.Provider }, { "model", model }, { "workspace", access.Where == "wsl" && from == "windows" ? "." : folder },
            };
            List<string> template;
            if (!(place.Arguments ?? kind.Dispatch.Arguments).TryGetValue(plan.Mode, out template))
                return plan.Refuse(access.Runtime + " has no arguments for " + plan.Mode);
            var agent = template.Select(a => Fill(a, values)).ToList();
            foreach (var kv in place.Environment ?? kind.Dispatch.Environment)
                plan.Environment[kv.Key] = kv.Value is string ? Fill((string)kv.Value, values) : Json.Serialize(FillAll(kv.Value, values));
            plan.Output = new Dictionary<string, string>(kind.Dispatch.Output);
            plan.Notes.AddRange(kind.Dispatch.Notes);
            // A plan agent starts from an empty environment, so no API key or base URL can make it bill the API (ADR 0003).
            plan.CleanEnvironment = access.Plan || kind.Dispatch.CleanEnvironment;
            if (access.Plan && kind.Plan == null) return plan.Refuse(kind.Name + " has no plan in its definition");
            // The program's arguments for one run of the agent with these arguments, in a folder (inside WSL only).
            Func<string, List<string>, List<string>> wrap;

            if (access.Where == "windows")
            {
                // Never a .cmd or .ps1 wrapper: started without a shell, only a native program runs.
                if (access.Program == null || !access.Program.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                    return plan.Refuse(kind.Name + "'s native Windows program was not found; its .cmd and .ps1 wrappers need a shell");
                plan.Program = access.Program;
                wrap = (cd, args) => args;
                plan.WorkingDirectory = folder;
                plan.Stop = "taskkill /PID <pid> /T /F";
            }
            else if (access.NeedsNode && access.NodeFolder == null)
                return plan.Refuse(kind.Name + " is a Node.js program, but node was not found inside WSL next to it or in the usual folders");
            else if (from == "wsl")
            {
                // The full path needs no login shell to put npm's global folder on the PATH.
                plan.Program = access.Program ?? access.Runtime;
                wrap = (cd, args) => args;
                plan.WorkingDirectory = folder;
                plan.Environment["PATH"] = SearchPath(access.Program, access.NodeFolder);
                plan.Stop = "SIGINT to the process";
            }
            else
            {
                // From Windows: "wsl.exe -e" runs a program without the Linux shell, which would re-read the arguments
                // ("wsl.exe --" hands the command line to that shell). env sets the variables, including a PATH where the
                // agent's interpreter is found, then runs the agent by its full path.
                if (access.Program == null) return plan.Refuse("the agent's program was not found inside WSL");
                // The key is never passed on: it would be on wsl.exe's command line and in this description.
                if (access.KeyFromProfile)
                    return plan.Refuse("the key for " + access.Service + " is only exported in a WSL shell profile, which wsl.exe -e does not read; " +
                        "start the coordinator from a shell in WSL, or give " + kind.Name + " its own key");
                if (plan.CleanEnvironment && access.Home == null) return plan.Refuse("the agent's home inside WSL is not known");
                var prefix = new List<string> { "-e", "env" };
                // env -i starts from nothing: only the home, a PATH and a locale reach a plan agent.
                if (plan.CleanEnvironment) prefix.AddRange(new[] { "-i", "HOME=" + access.Home, "LANG=C.UTF-8" });
                prefix.Add("PATH=" + SearchPath(access.Program, access.NodeFolder));
                prefix.AddRange(plan.Environment.Select(kv => kv.Key + "=" + kv.Value));
                prefix.Add(access.Program);
                wrap = (cd, args) => new List<string> { "-d", distro, "--cd", cd }.Concat(prefix).Concat(args).ToList();
                plan.Program = "wsl.exe";
                plan.Environment = new Dictionary<string, string>();   // Already passed inside WSL through env.
                plan.WorkingDirectory = null;
                plan.Stop = "taskkill /PID <pid> /T /F on wsl.exe, which ends the agent inside WSL too";
            }
            plan.Arguments = wrap(folder, agent);
            if (access.Plan)
            {
                // The plugin runs this first, with the same environment, and starts the agent only on a plan sign-in.
                plan.SignIn = new SignInCheck
                {
                    Program = plan.Program, Arguments = wrap("~", kind.Plan.SignInArguments), JsonField = kind.Plan.SignInField,
                    Expect = kind.Plan.SignInValue, Text = kind.Plan.SignInText,
                };
                plan.Billing = Candidates.IncludedInPlan;
            }
            plan.Warnings.AddRange(Warnings(access.Service));
            if (plan.Warnings.Contains(ByokFallback))
                plan.Notes.Add("OpenRouter can route a :free model to a provider key you linked (BYOK), billed to that account.");
            if (!plan.VersionVerified)
                plan.Notes.Add("This " + access.Runtime + " version" + (version == null ? " is unknown" : " (" + version + ") is outside the verified range") + "; the description may not fit it.");
            plan.Supported = true;
            return plan;
        }

        /// <summary>
        /// Structured warnings about a service's free models that a plugin must act on. OpenRouter: a :free request
        /// that hits the free route's limit can be retried on a provider key the user linked (BYOK) and billed there,
        /// which neither the catalogue price nor AI Meter can see.
        /// </summary>
        internal static List<string> Warnings(string service)
        {
            return service == "openrouter" ? new List<string> { ByokFallback } : new List<string>();
        }

        /// <summary>A template with {provider}, {model} and {workspace} filled in one pass, so a value is never read as a placeholder.</summary>
        private static string Fill(string template, Dictionary<string, string> values)
        {
            return Regex.Replace(template, @"\{(provider|model|workspace)\}", m => values[m.Groups[1].Value] ?? m.Value);
        }

        /// <summary>Fill the strings of a JSON value (an environment value written as an object or array).</summary>
        private static object FillAll(object value, Dictionary<string, string> values)
        {
            var s = value as string;
            if (s != null) return Fill(s, values);
            var d = value as Dictionary<string, object>;
            if (d != null) return d.ToDictionary(kv => kv.Key, kv => FillAll(kv.Value, values));
            var list = value as object[];
            return list != null ? list.Select(v => FillAll(v, values)).ToArray() : value;
        }

        /// <summary>A Linux PATH: the agent's own folder first, then node's when it is elsewhere, then the usual system folders.</summary>
        private static string SearchPath(string program, string node)
        {
            string system = "/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin";
            var folders = new List<string>();
            int slash = program == null ? -1 : program.LastIndexOf('/');
            if (slash > 0) folders.Add(program.Substring(0, slash));
            if (node != null && !folders.Contains(node) && !system.Split(':').Contains(node)) folders.Add(node);
            folders.Add(system);
            return string.Join(":", folders);
        }

        /// <summary>A description, or why there is none (Supported false, with Reason).</summary>
        internal sealed class Plan
        {
            public string Runtime, Where, Version, Mode, Program, WorkingDirectory, Stop, Reason;
            public bool Supported, VersionVerified;
            /// <summary>Start from an empty environment plus Environment, never the plugin's own (plan agents).</summary>
            public bool CleanEnvironment;
            /// <summary>For a plan agent: the sign-in check to pass before starting it, and "included_in_plan".</summary>
            public SignInCheck SignIn;
            public string Billing;
            public List<string> Arguments = new List<string>();
            public Dictionary<string, string> Environment = new Dictionary<string, string>();
            public Dictionary<string, string> Output = new Dictionary<string, string>();
            public readonly List<string> Notes = new List<string>();
            /// <summary>Structured warnings (see Warnings), alongside the notes that explain them.</summary>
            public readonly List<string> Warnings = new List<string>();

            public Plan Refuse(string reason) { Supported = false; Reason = reason; return this; }
        }

        /// <summary>
        /// How a plugin checks that a plan agent is signed in to the plan, not to an API key: run Program with Arguments
        /// in the agent's environment, then expect JsonField to equal Expect in its JSON output, or Text in its output.
        /// Only that is read; the rest of the output (an account's email, say) is dropped.
        /// </summary>
        internal sealed class SignInCheck
        {
            public string Program, JsonField, Expect, Text;
            public List<string> Arguments = new List<string>();
        }
    }
}
