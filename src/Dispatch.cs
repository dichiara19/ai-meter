using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace AIMeter
{
    /// <summary>
    /// How a coordinator plugin starts a coding agent on one model (ADR 0002; docs/plans/orchestration-contracts.md
    /// section 3.1). AI Meter starts nothing: it describes the program, the argument list and the environment, and the
    /// plugin starts them without a shell and writes the task to stdin. The task text is never part of a description.
    ///
    /// Every row was verified live in the M4 spike (docs/plans/spike-agents.md) for an agent, a major version and a
    /// place. A combination that was not verified is reported as unsupported, with the reason, rather than guessed.
    /// </summary>
    internal static class Dispatch
    {
        public const string ReadOnly = "read_only";
        public const string Modify = "modify";

        /// <summary>Agents, places and major versions verified in the spike.</summary>
        private static readonly string[][] Verified =
        {
            new[] { "pi", "wsl", "1." },
            new[] { "opencode", "wsl", "1." },
            new[] { "opencode", "windows", "1." },
        };

        /// <summary>npm package of each agent, to read its version without running it (nothing is started in WSL).</summary>
        private static readonly Dictionary<string, string> Packages = new Dictionary<string, string>
        {
            { "pi", "@earendil-works/pi-coding-agent" }, { "opencode", "opencode-ai" },
        };

        /// <summary>
        /// Describe how to run the agent of an access (runtime, place and provider) on a model. from is where the plugin
        /// runs ("windows" or "wsl"): a plugin on Windows reaches an agent in WSL through wsl.exe, which must already be
        /// running. workspace is the folder the task works in; it is a placeholder when not given.
        /// </summary>
        public static Plan Describe(RuntimeAccess access, string model, string mode, string workspace, string from, string distro, string version)
        {
            var plan = new Plan { Runtime = access.Runtime, Where = access.Where, Version = version, Mode = mode ?? ReadOnly };
            if (plan.Mode != ReadOnly && plan.Mode != Modify) return plan.Refuse("mode must be read_only or modify");
            if (from != "windows" && from != "wsl") return plan.Refuse("from must be windows or wsl");
            if (!Verified.Any(v => v[0] == access.Runtime && v[1] == access.Where))
                return plan.Refuse(access.Runtime + " has not been verified " + (access.Where == "wsl" ? "in WSL" : "on Windows") + " yet");
            if (from == "wsl" && access.Where == "windows") return plan.Refuse("starting a Windows agent from WSL has not been verified yet");
            if (access.Where == "wsl" && string.IsNullOrEmpty(distro)) return plan.Refuse("no WSL distribution is known");
            plan.VersionVerified = version != null && Verified.Any(v => v[0] == access.Runtime && v[1] == access.Where && version.StartsWith(v[2], StringComparison.Ordinal));
            string folder = string.IsNullOrEmpty(workspace) ? "{workspace}" : workspace;

            List<string> agent;
            if (access.Runtime == "pi") agent = PiArguments(access.Provider, model, plan);
            // From Windows into WSL, wsl.exe --cd already sets the folder, given as a Windows path the agent would not
            // understand inside WSL; the agent then works in its current folder.
            else agent = OpenCodeArguments(access.Provider, model, access.Where == "wsl" && from == "windows" ? "." : folder, plan);

            if (access.Where == "windows")
            {
                plan.Program = OpenCodeExe() ?? "opencode.exe";
                plan.Arguments = agent;
                plan.WorkingDirectory = folder;
                plan.Stop = "taskkill /PID <pid> /T /F";
            }
            else if (from == "wsl")
            {
                // The full path needs no login shell to put npm's global folder on the PATH.
                plan.Program = access.Program ?? access.Runtime;
                plan.Arguments = agent;
                plan.WorkingDirectory = folder;
                plan.Environment["PATH"] = SearchPath(access.Program);
                plan.Stop = "SIGINT to the process";
            }
            else
            {
                // From Windows: "wsl.exe -e" runs a program without the Linux shell, which would re-read the arguments
                // ("wsl.exe --" hands the command line to that shell). env sets the variables, including a PATH where the
                // agent's interpreter is found, then runs the agent by its full path.
                if (access.Program == null) return plan.Refuse("the agent's program was not found inside WSL");
                var wsl = new List<string> { "-d", distro, "--cd", folder, "-e", "env", "PATH=" + SearchPath(access.Program) };
                wsl.AddRange(plan.Environment.Select(kv => kv.Key + "=" + kv.Value));
                wsl.Add(access.Program);
                wsl.AddRange(agent);
                plan.Program = "wsl.exe";
                plan.Arguments = wsl;
                plan.Environment = new Dictionary<string, string>();   // Already passed inside WSL through env.
                plan.WorkingDirectory = null;
                plan.Stop = "taskkill /PID <pid> /T /F on wsl.exe, which ends the agent inside WSL too";
            }
            if (access.Service == "openrouter")
                plan.Notes.Add("OpenRouter can route a :free model to a provider key you linked (BYOK), billed to that account.");
            if (!plan.VersionVerified)
                plan.Notes.Add("This " + access.Runtime + " version" + (version == null ? " is unknown" : " (" + version + ") is outside the verified range") + "; the description may not fit it.");
            plan.Supported = true;
            return plan;
        }

        /// <summary>Pi 1.x: one task in JSON mode, without its sessions, extensions, MCP servers or context files.</summary>
        private static List<string> PiArguments(string provider, string model, Plan plan)
        {
            var args = new List<string> { "-p", "--mode", "json", "--no-session", "--no-extensions", "--no-mcp", "--no-context-files" };
            if (plan.Mode == ReadOnly) args.AddRange(new[] { "--tools", "read,grep,find,ls" });
            args.AddRange(new[] { "--provider", provider, "--model", model });
            plan.Output = new Dictionary<string, string>
            {
                { "format", "jsonl" },
                { "end", "the agent_settled event" },
                { "outcome", "stopReason of the last assistant message (stop, length, error, aborted); the exit code stays 0 on errors" },
                { "requests", "the number of turn_end events" },
                { "tokens", "the sum of usage.totalTokens of assistant messages; unknown when stopped" },
                { "rate_limited", "an errorMessage that contains 429" },
            };
            plan.Notes.Add("Pi sends about 4,600 tokens of system prompt with every request.");
            return args;
        }

        /// <summary>
        /// OpenCode 1.x: one task in JSON mode with its read-only plan agent or its build agent, isolated from the user's
        /// skills and project configuration, with its small model (session titles) set to the same model.
        /// </summary>
        private static List<string> OpenCodeArguments(string provider, string model, string folder, Plan plan)
        {
            string full = provider + "/" + model;
            plan.Environment["OPENCODE_DISABLE_CLAUDE_CODE"] = "1";
            plan.Environment["OPENCODE_DISABLE_EXTERNAL_SKILLS"] = "1";
            plan.Environment["OPENCODE_DISABLE_PROJECT_CONFIG"] = "1";
            plan.Environment["OPENCODE_DISABLE_AUTOUPDATE"] = "1";
            plan.Environment["OPENCODE_CONFIG_CONTENT"] = Json.Serialize(new Dictionary<string, object> { { "small_model", full } });
            plan.Output = new Dictionary<string, string>
            {
                { "format", "jsonl" },
                { "end", "the process exits" },
                { "outcome", "the exit code (1 with an error event on failure) and the last step_finish.reason" },
                { "requests", "the number of step_finish events" },
                { "tokens", "the sum of step_finish.tokens.total; unknown when stopped" },
                { "rate_limited", "an error event whose message contains 429" },
            };
            plan.Notes.Add("Close stdin after writing the task: with stdin open OpenCode waits for more.");
            return new List<string> { "run", "--pure", "--format", "json", "--agent", plan.Mode == ReadOnly ? "plan" : "build", "--dir", folder, "-m", full };
        }

        /// <summary>A Linux PATH: the agent's own folder first, then the usual system folders.</summary>
        private static string SearchPath(string program)
        {
            string system = "/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin";
            int slash = program == null ? -1 : program.LastIndexOf('/');
            return slash > 0 ? program.Substring(0, slash) + ":" + system : system;
        }

        /// <summary>OpenCode's native Windows program, as npm installs it; never the .cmd or .ps1 wrappers, which need a shell.</summary>
        internal static string OpenCodeExe()
        {
            string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), @"npm\node_modules\opencode-ai\node_modules");
            foreach (var flavour in new[] { "opencode-windows-x64", "opencode-windows-arm64", "opencode-windows-x64-baseline" })
            {
                string exe = Path.Combine(root, flavour, @"bin\opencode.exe");
                if (File.Exists(exe)) return exe;
            }
            return null;
        }

        /// <summary>
        /// The installed version of an agent from its npm package.json, read without running anything; null when not
        /// found. In WSL, home is the distribution's home as seen from Windows.
        /// </summary>
        public static string Version(string runtime, string home, bool wsl)
        {
            string package;
            if (!Packages.TryGetValue(runtime, out package)) return null;
            var roots = new List<string>();
            if (wsl)
            {
                roots.Add(Path.Combine(home, @".npm-global\lib\node_modules"));
                roots.Add(Path.Combine(home, @".local\lib\node_modules"));
                var parent = Directory.GetParent(home);
                var root = parent == null ? null : parent.Parent;
                if (root != null)
                {
                    roots.Add(Path.Combine(root.FullName, @"usr\local\lib\node_modules"));
                    roots.Add(Path.Combine(root.FullName, @"usr\lib\node_modules"));
                }
            }
            else roots.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), @"npm\node_modules"));
            foreach (var r in roots)
            {
                string file = Path.Combine(r, package.Replace('/', '\\'), "package.json");
                try { if (File.Exists(file)) return Json.Str(Json.ParseFile(file), "version"); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            return null;
        }

        /// <summary>A description, or why there is none (Supported false, with Reason).</summary>
        internal sealed class Plan
        {
            public string Runtime, Where, Version, Mode, Program, WorkingDirectory, Stop, Reason;
            public bool Supported, VersionVerified;
            public List<string> Arguments = new List<string>();
            public Dictionary<string, string> Environment = new Dictionary<string, string>();
            public Dictionary<string, string> Output = new Dictionary<string, string>();
            public readonly List<string> Notes = new List<string>();

            public Plan Refuse(string reason) { Supported = false; Reason = reason; return this; }
        }
    }
}
