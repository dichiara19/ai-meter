using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace AIMeter
{
    /// <summary>
    /// Local AI models: finds the servers listening on this PC (Windows, and WSL when already running), recognises them by
    /// their APIs and reads the models loaded in memory, with video memory, context and requests in progress. Only
    /// 127.0.0.1 is contacted, and only with read requests that never load a model.
    /// </summary>
    internal sealed class LocalProvider : Provider
    {
        private readonly Settings S;
        private readonly Dictionary<string, Server> servers = new Dictionary<string, Server>();
        private readonly Dictionary<string, DateTime> notModels = new Dictionary<string, DateTime>();   // Ports tried that are not model servers.
        private DateTime winScanAt = DateTime.MinValue, wslScanAt = DateTime.MinValue;
        private List<Tuple<int, string>> wslPorts = new List<Tuple<int, string>>();
        private Dictionary<int, uint> winPorts = new Dictionary<int, uint>();
        private Dictionary<uint, uint> parents = new Dictionary<uint, uint>();
        private readonly CodingAgents agents;
        private readonly HashSet<int> swapPorts = new HashSet<int>();   // Ports of the llama-server instances started by llama-swap.
        private readonly Dictionary<int, uint> wslServers = new Dictionary<int, uint>();        // Port to the process serving it in WSL.
        private readonly Dictionary<int, DateTime> wslMisses = new Dictionary<int, DateTime>();   // Ports whose process was not found, and when.
        private readonly Dictionary<string, long> fileSizes = new Dictionary<string, long>();    // Model file to its size, while its model is loaded.
        private long physicalMemory;   // This PC's memory, read once: the bound of what a process can share with the card.
        private readonly Dictionary<int, NInferEngine.Sample> engineSamples = new Dictionary<int, NInferEngine.Sample>();                    // Port to its engine's last reading.
        private readonly Dictionary<int, Dictionary<string, string>> engineOptions = new Dictionary<int, Dictionary<string, string>>();   // Port to its engine's options.
        private readonly HashSet<int> withoutMetrics = new HashSet<int>();   // Ports of llama-server instances started without --metrics.
        private readonly Dictionary<string, GgufHeader> headers = new Dictionary<string, GgufHeader>();   // Model file to its header (null when it has none), while its model is loaded.

        /// <summary>Programs that host a model server, and the default ports of the most common servers.</summary>
        private static readonly string[] KnownProcesses = { "ollama", "llama-server", "llama-swap", "lm studio", "llmster", "koboldcpp", "local-ai", "localai", "vllm" };
        private static readonly int[] KnownPorts = { 11434, 1234, 9931, 8080, 8000, 5001, 5000 };
        /// <summary>Processes that run llama-server as their engine: their children are not separate servers.</summary>
        private static readonly string[] Hosts = { "ollama.exe", "llama-swap.exe", "jan-llama-worker.exe", "jan.exe", "lm studio.exe" };

        private sealed class Server
        {
            public string Kind;       // "ollama", "lmstudio", "llamacpp", "llamarouter", "llamaswap", "vllm", "kobold", "localai", "textgen"
            public int Port;
            public string Where;      // "Windows" or "WSL".
            public uint Pid;          // Windows only.
            public string Proc;
            public int Failures;
        }

        public LocalProvider(Settings s) { S = s; agents = new CodingAgents(s); }

        public override string Id { get { return "local"; } }
        public override bool Enabled(Settings s) { return s.LocalEnabled; }

        public override ProviderState Collect(Probe p)
        {
            Discover(p);
            long inUse;
            if (physicalMemory == 0) HostMemory.Windows(out inUse, out physicalMemory);
            var models = new List<LocalModel>();
            bool running = false;
            swapPorts.Clear();
            // llama-swap first: the llama-server instances it starts must not be counted a second time.
            foreach (var srv in servers.Values.OrderBy(x => x.Kind == "llamaswap" ? 0 : 1).ToList())
            {
                if (srv.Kind == "llamacpp" && swapPorts.Contains(srv.Port)) continue;
                List<LocalModel> found = null;
                try { found = Query(srv); }
                catch (Exception ex) { Trace.WriteLine("local " + srv.Kind + ": " + ex.Message); }
                if (found == null)
                {
                    if (++srv.Failures >= 3) servers.Remove(Key(srv.Where, srv.Port));
                    continue;
                }
                srv.Failures = 0;
                running = true;
                foreach (var m in found)
                {
                    m.Where = srv.Where;
                    if (m.Server == null) m.Server = ServerName(srv.Kind);
                    // A llama-swap model is held by its own server; while that one's port is unknown, so is its process.
                    if (m.Port == 0 && srv.Kind != "llamaswap") m.Port = srv.Port;
                }
                if (srv.Kind == "ollama" && srv.Where == "Windows") OllamaActivity(p, found);
                AttachMemory(p, srv, found);
                models.AddRange(found);
            }
            AttachWslMemory(p, models);
            ShareWslMemory(p, models);
            AttachFileSizes(p, models);
            AttachSegments(models);
            AttachHeaders(p, models);

            var st = new ProviderState { Id = "local", Name = L.T("Local models"), Models = models, Running = running };
            // GPU counters are read here, on the collection thread, and only when a server is active.
            if (running)
            {
                st.GpuTotal = GpuMemory.Total;
                st.GpuUsed = st.GpuTotal > 0 ? GpuMemory.Used : 0;
                HostMemory.Windows(out st.RamUsed, out st.RamTotal);
                // What the WSL virtual machine holds is part of the PC's memory in use, whether or not a server runs in it.
                st.WslHeld = WorkingSet(p.Procs.Where(kv => kv.Value == "vmmemwsl" || kv.Value == "vmmem").Select(kv => kv.Key));
                var wsl = p.WslUp ? HostMemory.Wsl(p.WslRoot) : null;
                if (wsl != null)
                {
                    st.WslUsed = wsl.Used; st.WslTotal = wsl.Total; st.WslTouched = wsl.Touched;
                    st.WslSwapUsed = wsl.SwapUsed; st.WslSwapTotal = wsl.SwapTotal;
                }
            }
            if (S.LocalAgents)
            {
                // CLI sessions belong under the model they use, if it is loaded here.
                foreach (var s in agents.Collect(p))
                {
                    var m = models.FirstOrDefault(x => SameModel(x.Name, s.Model));
                    if (m != null) m.Sessions.Add(s);
                    else st.Sessions.Add(s);
                }
            }
            return st;
        }

        private static string Key(string where, int port) { return where + ":" + port; }

        public static string ServerName(string kind)
        {
            switch (kind)
            {
                case "ollama": return "Ollama";
                case "lmstudio": return "LM Studio";
                case "llamaswap": return "llama-swap";
                case "vllm": return "vLLM";
                case "kobold": return "KoboldCpp";
                case "localai": return "LocalAI";
                case "textgen": return "text-generation-webui";
                default: return "llama.cpp";
            }
        }

        /// <summary>The same model spelt differently: "qwen3:8b" and "ollama/qwen3:8b", letter case, provider prefixes.</summary>
        public static bool SameModel(string loaded, string used)
        {
            if (string.IsNullOrEmpty(loaded) || string.IsNullOrEmpty(used)) return false;
            Func<string, string> norm = x =>
            {
                x = x.ToLowerInvariant();
                int slash = x.LastIndexOf('/');
                if (slash >= 0) x = x.Substring(slash + 1);
                if (x.EndsWith(":latest")) x = x.Substring(0, x.Length - 7);
                if (x.EndsWith(".gguf")) x = x.Substring(0, x.Length - 5);
                return x;
            };
            string a = norm(loaded), b = norm(used);
            return a == b || (b.Length >= 4 && a.StartsWith(b)) || (a.Length >= 4 && b.StartsWith(a));
        }

        // ================================================================ server discovery
        private void Discover(Probe p)
        {
            var candidates = new List<Tuple<int, string, uint, string>>();   // Port, location, PID, process name.
            if ((DateTime.Now - winScanAt).TotalSeconds >= 20)
            {
                winScanAt = DateTime.Now;
                winPorts = Native.ListeningPorts();
                parents = Native.ProcessParents();
                foreach (var kv in winPorts)
                {
                    string name;
                    p.Procs.TryGetValue(kv.Value, out name);
                    name = name ?? "";
                    // WSL NAT networking (the default): for every Linux server on 0.0.0.0 or 127.0.0.1, wslrelay.exe opens the same
                    // port on Windows' 127.0.0.1. It is a WSL server, whatever the port.
                    if (name == "wslrelay.exe") { if (kv.Key >= 1024) candidates.Add(Tuple.Create(kv.Key, "WSL", 0u, "")); continue; }
                    if (IsEngineChild(p, kv.Value, name)) continue;
                    string bare = name.EndsWith(".exe") ? name.Substring(0, name.Length - 4) : name;
                    if (Known(bare, kv.Key)) candidates.Add(Tuple.Create(kv.Key, "Windows", kv.Value, bare));
                }
            }
            // WSL running: its ports (absent from the Windows table in "mirrored" networking) are read from the Linux kernel's TCP
            // table as a file, without starting processes in WSL, so it is not kept awake. Once a minute, every 5 on battery.
            double every = p.SavingPower ? 300 : 60;
            if (p.WslUp && (DateTime.Now - wslScanAt).TotalSeconds >= every)
            {
                wslScanAt = DateTime.Now;
                wslPorts = WslListeners(p.WslRoot);
            }
            if (!p.WslUp) wslPorts.Clear();
            // In "mirrored" networking WSL ports have no row in the Windows table; in NAT they do (wslrelay) and are
            // already candidates. Only ports used on Windows by another program are skipped.
            foreach (var w in wslPorts)
            {
                uint owner;
                string ownerName;
                bool takenByWindows = winPorts.TryGetValue(w.Item1, out owner) && !(p.Procs.TryGetValue(owner, out ownerName) && ownerName == "wslrelay.exe");
                if (!takenByWindows) candidates.Add(Tuple.Create(w.Item1, "WSL", 0u, w.Item2));
            }

            foreach (var c in candidates)
            {
                string key = Key(c.Item2, c.Item1);
                DateTime triedAt;
                // A port that was not a server is retried after 30 minutes: the same number may pass to another program.
                if (servers.ContainsKey(key) || (notModels.TryGetValue(key + "|" + c.Item3, out triedAt) && (DateTime.Now - triedAt).TotalMinutes < 30)) continue;
                string kind = Identify(c.Item1);
                if (kind == null) { notModels[key + "|" + c.Item3] = DateTime.Now; continue; }
                servers[key] = new Server { Kind = kind, Port = c.Item1, Where = c.Item2, Pid = c.Item3, Proc = c.Item4 };
            }
        }

        /// <summary>A llama-server started by Ollama, llama-swap, Jan or LM Studio: part of that server, not a separate one.</summary>
        private bool IsEngineChild(Probe p, uint pid, string name)
        {
            if (name != "llama-server.exe") return false;
            uint parent;
            string parentName;
            if (parents.TryGetValue(pid, out parent) && p.Procs.TryGetValue(parent, out parentName) && Hosts.Contains(parentName)) return true;
            string path = p.PathOf(pid) ?? "";
            return path.IndexOf(@"\ollama\", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool Known(string proc, int port)
        {
            proc = proc.ToLowerInvariant();
            if (KnownProcesses.Any(k => proc == k || proc.StartsWith(k + "-") || proc.StartsWith(k + "_") || proc.StartsWith(k + " "))) return true;
            // Default ports: only for programs that can host a model server (python for vLLM and similar).
            return KnownPorts.Contains(port) && (proc.StartsWith("python") || proc == "node" || proc == "docker" || proc == "com.docker.backend");
        }

        /// <summary>
        /// Ports listening in WSL, from /proc/1/net/tcp and tcp6 (the same network namespace as Linux apps). Only those
        /// reachable from Windows (all interfaces or localhost) and not system ports; the program name is unavailable,
        /// so the recognition probe identifies it.
        /// </summary>
        private static List<Tuple<int, string>> WslListeners(string root)
        {
            var list = new List<Tuple<int, string>>();
            foreach (var file in new[] { @"\proc\1\net\tcp", @"\proc\1\net\tcp6" })
            {
                string text;
                try
                {
                    using (var fs = new FileStream(root + file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                    using (var sr = new StreamReader(fs, Encoding.ASCII)) text = sr.ReadToEnd();
                }
                catch (IOException) { continue; }
                catch (UnauthorizedAccessException) { continue; }
                foreach (var line in text.Split('\n'))
                {
                    var f = line.Trim().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                    if (f.Length < 4 || f[3] != "0A") continue;   // 0A = LISTEN
                    int colon = f[1].LastIndexOf(':');
                    if (colon < 0) continue;
                    string addr = f[1].Substring(0, colon);
                    int port = int.Parse(f[1].Substring(colon + 1), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                    bool reachable = addr == "00000000" || addr == "0100007F" || addr.Trim('0').Length == 0
                                     || addr == "00000000000000000000000001000000" || addr == "0000000000000000FFFF00000100007F";
                    if (!reachable || port < 1024 || list.Any(x => x.Item1 == port)) continue;
                    list.Add(Tuple.Create(port, ""));
                }
            }
            return list;
        }
        /// <summary>
        /// Which server is listening on the port, from a request only that server can answer. The order matters:
        /// KoboldCpp imitates llama-server and Ollama, and llama-swap has an /api/version like Ollama.
        /// </summary>
        private static string Identify(int port)
        {
            // One short request first: if the port does not speak HTTP (adb, databases…) stop immediately.
            if (!SpeaksHttp(port)) return null;
            if (JPath.Str(JPath.Get(Get(port, "/api/extra/version"), "result")) == "KoboldCpp") return "kobold";
            if (JPath.Truthy(JPath.Get(Get(port, "/lmstudio-greeting"), "lmstudio"))) return "lmstudio";
            string root = GetText(port, "/");
            if (root != null && root.Trim() == "Ollama is running") return "ollama";
            if (JPath.Get(Get(port, "/running"), "running") is IList) return "llamaswap";
            var props = Get(port, "/props");
            if (JPath.Get(props, "total_slots") != null && JPath.Get(props, "build_info") != null) return "llamacpp";
            var models = JPath.Get(Get(port, "/models"), "data") as IList;
            if (models != null && models.Cast<object>().Any(x => JPath.Get(x, "status.value") != null)) return "llamarouter";
            var v1 = JPath.Get(Get(port, "/v1/models"), "data") as IList;
            if (v1 != null && v1.Cast<object>().Any(x => JPath.Str(JPath.Get(x, "owned_by")) == "vllm")) return "vllm";
            if (JPath.Get(Get(port, "/system"), "loaded_models") is IList) return "localai";
            if (JPath.Get(Get(port, "/v1/internal/model/info"), "model_name") != null) return "textgen";
            return null;
        }

        // ================================================================ loaded models
        private List<LocalModel> Query(Server s)
        {
            switch (s.Kind)
            {
                case "ollama": return Ollama(s.Port);
                case "lmstudio": return LmStudio(s.Port);
                case "llamaswap": return LlamaSwap(s.Port);
                case "llamarouter": return LlamaRouter(s.Port);
                case "vllm": return Vllm(s.Port);
                case "kobold": return Kobold(s.Port);
                case "localai": return LocalAi(s.Port);
                case "textgen": return TextGen(s.Port);
                default:
                    var m = LlamaCpp(s.Port, "/props", "/slots");
                    return m == null ? null : new List<LocalModel> { m };
            }
        }

        /// <summary>Ollama: /api/ps lists only the models in memory, with size, share in VRAM and configured context.</summary>
        private static List<LocalModel> Ollama(int port)
        {
            var list = JPath.Get(Get(port, "/api/ps"), "models") as IList;
            if (list == null) return null;
            var result = new List<LocalModel>();
            foreach (var x in list)
            {
                long size = (long)(JPath.Num(JPath.Get(x, "size")) ?? 0), vram = (long)(JPath.Num(JPath.Get(x, "size_vram")) ?? 0);
                result.Add(new LocalModel
                {
                    Name = JPath.Str(JPath.Get(x, "name")) ?? JPath.Str(JPath.Get(x, "model")),
                    Detail = Join(JPath.Str(JPath.Get(x, "details.parameter_size")), JPath.Str(JPath.Get(x, "details.quantization_level"))),
                    Vram = vram, Ram = Math.Max(0, size - vram),
                    VramOrigin = vram > 0 ? MemoryOrigin.Reported : MemoryOrigin.Unknown,
                    RamOrigin = size > vram ? MemoryOrigin.Reported : MemoryOrigin.Unknown,
                    Context = (int)(JPath.Num(JPath.Get(x, "context_length")) ?? 0),
                });
            }
            return result;
        }

        /// <summary>
        /// Ollama does not say whether a model is working: its llama-server child does (/slots). With a single model
        /// loaded the match is certain; with several models they are matched by configured context.
        /// </summary>
        private void OllamaActivity(Probe p, List<LocalModel> models)
        {
            if (models.Count == 0) return;
            foreach (var kv in winPorts)
            {
                string name;
                uint parent;
                string parentName;
                if (!p.Procs.TryGetValue(kv.Value, out name) || name != "llama-server.exe") continue;
                if (!parents.TryGetValue(kv.Value, out parent) || !p.Procs.TryGetValue(parent, out parentName) || parentName != "ollama.exe") continue;
                var child = LlamaCpp(kv.Key, "/props", "/slots");
                if (child == null) continue;
                var m = models.Count == 1 ? models[0] : models.FirstOrDefault(x => x.Busy == 0 && x.Context > 0 && child.Slots > 0 && x.Context == child.Context * child.Slots);
                if (m == null) continue;
                m.Busy = child.Busy;
                m.Slots = child.Slots;
                m.ContextUsed = child.ContextUsed;
                if (m.Context == 0) m.Context = child.Context;
            }
        }

        /// <summary>LM Studio: API v1 (≥ 0.4) with the loaded instances, otherwise v0 with the state of each model.</summary>
        private static List<LocalModel> LmStudio(int port)
        {
            var v1 = JPath.Get(Get(port, "/api/v1/models"), "models") as IList;
            if (v1 != null)
            {
                var result = new List<LocalModel>();
                foreach (var x in v1)
                {
                    var inst = JPath.Get(x, "loaded_instances") as IList;
                    if (inst == null || inst.Count == 0 || JPath.Str(JPath.Get(x, "type")) == "embedding") continue;
                    foreach (var i in inst)
                        result.Add(new LocalModel
                        {
                            Name = JPath.Str(JPath.Get(i, "id")) ?? JPath.Str(JPath.Get(x, "key")),
                            Detail = Join(JPath.Str(JPath.Get(x, "params_string")), JPath.Str(JPath.Get(x, "quantization.name"))),
                            Context = (int)(JPath.Num(JPath.Get(i, "config.context_length")) ?? 0),
                            Slots = (int)(JPath.Num(JPath.Get(i, "config.parallel")) ?? 0),
                        });
                }
                return result;
            }
            var v0 = JPath.Get(Get(port, "/api/v0/models"), "data") as IList;
            if (v0 == null) return null;
            return v0.Cast<object>().Where(x => JPath.Str(JPath.Get(x, "state")) == "loaded" && JPath.Str(JPath.Get(x, "type")) != "embeddings")
                .Select(x => new LocalModel
                {
                    Name = JPath.Str(JPath.Get(x, "id")),
                    Detail = Join(JPath.Str(JPath.Get(x, "arch")), JPath.Str(JPath.Get(x, "quantization"))),
                    Context = (int)(JPath.Num(JPath.Get(x, "loaded_context_length")) ?? JPath.Num(JPath.Get(x, "max_context_length")) ?? 0),
                }).ToList();
        }

        /// <summary>
        /// llama-server: /props per model, context per slot and number of slots; /slots (normally enabled) for the requests
        /// in progress and the tokens occupying the fullest slot.
        /// </summary>
        private static LocalModel LlamaCpp(int port, string propsPath, string slotsPath)
        {
            var props = Get(port, propsPath) as Dictionary<string, object>;
            if (props == null) return null;
            string path = JPath.Str(JPath.Get(props, "model_alias")) ?? JPath.Str(JPath.Get(props, "model_path")) ?? "model";
            var m = new LocalModel
            {
                Name = Path.GetFileNameWithoutExtension(path.Replace('/', '\\')),
                Detail = JPath.Truthy(JPath.Get(props, "is_sleeping")) ? L.T("sleeping") : JPath.Str(JPath.Get(props, "model_ftype")),
                Context = (int)(JPath.Num(JPath.Get(props, "default_generation_settings.n_ctx")) ?? 0),
                Slots = (int)(JPath.Num(JPath.Get(props, "total_slots")) ?? 0),
                Port = port,
                File = JPath.Str(JPath.Get(props, "model_path")),
                // A sleeping model is left alone: only /props and /slots are known not to wake it.
                Build = JPath.Truthy(JPath.Get(props, "is_sleeping")) ? null : JPath.Str(JPath.Get(props, "build_info")),
            };
            if (JPath.Get(props, "endpoint_slots") is bool && !(bool)JPath.Get(props, "endpoint_slots")) return m;
            var slots = Get(port, slotsPath) as IList;
            if (slots == null) return m;
            m.Slots = slots.Count;
            foreach (var sl in slots)
            {
                if (!JPath.Truthy(JPath.Get(sl, "is_processing"))) continue;
                m.Busy++;
                // n_prompt_tokens is the slot cache and already includes the generated tokens (verified with llama-server b10787);
                // older versions had n_past, without the tokens being generated.
                double? cached = JPath.Num(JPath.Get(sl, "n_prompt_tokens"));
                int used = cached != null ? (int)cached.Value
                         : (int)(JPath.Num(JPath.Get(sl, "n_past")) ?? 0) + (int)(JPath.Num(JPath.Get(sl, "next_token[0].n_decoded")) ?? JPath.Num(JPath.Get(sl, "next_token.n_decoded")) ?? 0);
                int nctx = (int)(JPath.Num(JPath.Get(sl, "n_ctx")) ?? 0);
                if (nctx > 0) { m.Context = nctx; used = Math.Min(used, nctx); }
                m.ContextUsed = Math.Max(m.ContextUsed, used);
            }
            return m;
        }

        /// <summary>llama-server in router mode (several models): always with autoload=false, so that none is loaded.</summary>
        private static List<LocalModel> LlamaRouter(int port)
        {
            var list = JPath.Get(Get(port, "/models"), "data") as IList;
            if (list == null) return null;
            var result = new List<LocalModel>();
            foreach (var x in list)
            {
                string state = JPath.Str(JPath.Get(x, "status.value")) ?? "";
                if (state != "loaded" && state != "loading" && state != "sleeping") continue;
                string id = JPath.Str(JPath.Get(x, "id"));
                string q = "?model=" + Uri.EscapeDataString(id ?? "") + "&autoload=false";
                var m = state == "loaded" ? LlamaCpp(port, "/props" + q, "/slots" + q) : null;
                if (m == null) m = new LocalModel { Detail = L.T(state == "loading" ? "loading" : "sleeping") };
                m.Name = id;
                result.Add(m);
            }
            return result;
        }

        /// <summary>
        /// llama-swap: /running lists the started models; for ready ones their own llama-server is queried directly
        /// ("proxy" address), because going through /upstream would load the model.
        /// </summary>
        private List<LocalModel> LlamaSwap(int port)
        {
            var list = JPath.Get(Get(port, "/running"), "running") as IList;
            if (list == null) return null;
            var result = new List<LocalModel>();
            foreach (var x in list)
            {
                string id = JPath.Str(JPath.Get(x, "model"));
                if (id == null) continue;
                string state = JPath.Str(JPath.Get(x, "state")) ?? "";
                LocalModel m = null;
                var proxy = Regex.Match(JPath.Str(JPath.Get(x, "proxy")) ?? "", @"^https?://(localhost|127\.0\.0\.1|\[::1\]):(\d+)/?$");
                int target = proxy.Success ? int.Parse(proxy.Groups[2].Value, CultureInfo.InvariantCulture) : 0;
                if (target > 0) swapPorts.Add(target);
                if (state == "ready" && target > 0) m = LlamaCpp(target, "/props", "/slots");
                if (m == null) m = new LocalModel { Port = target };
                m.Name = JPath.Str(JPath.Get(x, "name")) ?? id;
                m.Server = "llama-swap";
                if (state == "starting") m.Detail = L.T("loading");
                else if (state == "stopping") m.Detail = L.T("unloading");
                result.Add(m);
            }
            return result;
        }

        /// <summary>vLLM: one model per process; /metrics for the requests in progress.</summary>
        private static List<LocalModel> Vllm(int port)
        {
            var list = JPath.Get(Get(port, "/v1/models"), "data") as IList;
            if (list == null) return null;
            int running = (int)(Metric(GetText(port, "/metrics"), "vllm:num_requests_running") ?? 0);
            return list.Cast<object>().Where(x => JPath.Str(JPath.Get(x, "parent")) == null).Select(x => new LocalModel
            {
                Name = JPath.Str(JPath.Get(x, "id")),
                Context = (int)(JPath.Num(JPath.Get(x, "max_model_len")) ?? 0),
                Busy = running,
            }).ToList();
        }

        private static List<LocalModel> Kobold(int port)
        {
            string name = JPath.Str(JPath.Get(Get(port, "/api/v1/model"), "result"));
            if (name == null) return null;
            if (name.Contains("/")) name = name.Substring(name.LastIndexOf('/') + 1);
            var perf = Get(port, "/api/extra/perf");
            return new List<LocalModel> { new LocalModel
            {
                Name = name,
                Context = (int)(JPath.Num(JPath.Get(Get(port, "/api/extra/true_max_context_length"), "value")) ?? 0),
                Busy = JPath.Num(JPath.Get(perf, "idle")) == 0 ? 1 + (int)(JPath.Num(JPath.Get(perf, "queue")) ?? 0) : 0,
            } };
        }

        private static List<LocalModel> LocalAi(int port)
        {
            var list = JPath.Get(Get(port, "/system"), "loaded_models") as IList;
            if (list == null) return null;
            var result = list.Cast<object>().Select(x => new LocalModel
            {
                Name = JPath.Str(JPath.Get(x, "id")),
                Detail = JPath.Str(JPath.Get(x, "backend")),
                Vram = (long)(JPath.Num(JPath.Get(x, "size_vram")) ?? 0),
                Ram = (long)(JPath.Num(JPath.Get(x, "process.rss_bytes")) ?? 0),
            }).ToList();
            foreach (var m in result)
            {
                if (m.Vram > 0) m.VramOrigin = MemoryOrigin.Reported;
                if (m.Ram > 0) m.RamOrigin = MemoryOrigin.Reported;
            }
            return result;
        }

        private static List<LocalModel> TextGen(int port)
        {
            string name = JPath.Str(JPath.Get(Get(port, "/v1/internal/model/info"), "model_name"));
            if (name == null) return null;
            if (name == "None" || name.Length == 0) return new List<LocalModel>();
            return new List<LocalModel> { new LocalModel { Name = name, Detail = JPath.Str(JPath.Get(Get(port, "/v1/internal/model/info"), "loader")) } };
        }

        /// <summary>
        /// What the engine says of each model's inside: NInfer's own figures, and for any llama-server interface the
        /// generation speed of /metrics. Only for a model alone on its port, whose server's figures are its own.
        /// </summary>
        private void AttachSegments(List<LocalModel> models)
        {
            var alone = AloneOnPort(models).Where(m => m.Build != null).ToList();
            var ports = new HashSet<int>(alone.Select(m => m.Port));
            foreach (var port in engineSamples.Keys.Where(k => !ports.Contains(k)).ToList()) engineSamples.Remove(port);
            foreach (var port in engineOptions.Keys.Where(k => !ports.Contains(k)).ToList()) engineOptions.Remove(port);
            withoutMetrics.RemoveWhere(port => !ports.Contains(port));
            foreach (var m in alone)
            {
                if (NInferEngine.Is(m.Build)) m.Segments = NInferSegments(m.Port);
                else if (!withoutMetrics.Contains(m.Port))
                {
                    // llama-server answers /metrics only when started with --metrics: one without is asked once.
                    double? speed = Metric(GetText(m.Port, "/metrics"), "llamacpp:predicted_tokens_seconds");
                    if (speed == null) withoutMetrics.Add(m.Port);
                    else if (speed > 0) m.Segments = new ModelSegments { TokensPerSecond = speed.Value };
                }
            }
        }

        /// <summary>
        /// What the header of a GGUF model file adds, whatever engine loaded it: whether the model is a mixture of
        /// experts, and an estimate of the key-value cache's size for the context it was loaded with. The header is
        /// read once and kept while the model is loaded.
        /// </summary>
        private void AttachHeaders(Probe p, List<LocalModel> models)
        {
            var seen = new HashSet<string>();
            foreach (var m in models.Where(x => x.File != null && x.File.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase)))
            {
                string key = m.Where + "|" + m.File;
                seen.Add(key);
                GgufHeader header;
                if (!headers.TryGetValue(key, out header))
                {
                    if (m.Where == "WSL" && !p.WslUp) continue;
                    string file = HostMemory.ResolveFile(m.File, m.Where == "WSL" ? p.WslRoot : null);
                    headers[key] = header = file == null ? null : GgufHeader.Read(file);
                }
                if (header != null) Describe(m, header);
            }
            foreach (var key in headers.Keys.Where(k => !seen.Contains(k)).ToList()) headers.Remove(key);
        }

        /// <summary>Adds to a model what its file's header says, leaving alone what its engine already reported.</summary>
        internal static void Describe(LocalModel m, GgufHeader header)
        {
            // llama-server gives each slot its share of the context: the cache holds them all.
            long tokens = (long)m.Context * Math.Max(1, m.Slots);
            long cache = header.KvBytes(tokens);
            if (header.Experts <= 0 && cache <= 0) return;
            var s = m.Segments ?? (m.Segments = new ModelSegments());
            s.Experts = header.Experts;
            s.ExpertsUsed = header.ExpertsUsed;
            s.KvBytes = cache;
            if (s.KvCapacity == 0 && cache > 0 && tokens <= int.MaxValue) s.KvCapacity = (int)tokens;
        }

        private ModelSegments NInferSegments(int port)
        {
            NInferEngine.Sample previous, sample;
            engineSamples.TryGetValue(port, out previous);
            var stats = Get(port, "/stats");
            double uptime = JPath.Num(JPath.Get(stats, "uptime_seconds")) ?? 0;
            Dictionary<string, string> options;
            // The options belong to one run of the server: they are read again when it has restarted.
            if (!engineOptions.TryGetValue(port, out options) || previous == null || uptime < previous.Uptime)
                engineOptions[port] = options = NInferEngine.Options(Get(port, "/v1/models"));
            var segments = NInferEngine.Read(stats, options, GetText(port, "/metrics"), previous, DateTime.Now, out sample);
            if (sample == null) engineSamples.Remove(port);
            else engineSamples[port] = sample;
            return segments;
        }

        internal static double? Metric(string text, string name)
        {
            if (text == null) return null;
            double sum = 0;
            bool any = false;
            foreach (Match m in Regex.Matches(text, "^" + Regex.Escape(name) + @"(?:\{[^}]*\})?\s+([0-9.eE+-]+)", RegexOptions.Multiline))
            {
                double v;
                if (double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out v)) { sum += v; any = true; }
            }
            return any ? sum : (double?)null;
        }

        private static string Join(params string[] parts)
        {
            var p = parts.Where(x => !string.IsNullOrEmpty(x)).ToArray();
            return p.Length == 0 ? null : string.Join(" · ", p);
        }

        // ================================================================ memory
        /// <summary>
        /// Video and system memory of the models of a server on Windows, from the counters of the process that holds
        /// each one, when the server reports neither. A process holding several models cannot be split between them.
        /// </summary>
        private void AttachMemory(Probe p, Server s, List<LocalModel> models)
        {
            if (s.Where != "Windows") return;
            var gpu = GpuMemory.ByProcess;
            var shared = GpuMemory.SharedByProcess;
            foreach (var m in AloneOnPort(models))
            {
                if (m.Vram > 0 || m.Ram > 0) continue;   // The server's own figures stand.
                uint holder = s.Pid;
                if (m.Port != s.Port && !winPorts.TryGetValue(m.Port, out holder)) continue;
                // The process and those it started (llama-swap's llama-server, LM Studio engines).
                var pids = new HashSet<uint> { holder };
                bool grew = true;
                while (grew)
                {
                    grew = false;
                    foreach (var kv in parents) if (pids.Contains(kv.Value) && pids.Add(kv.Key)) grew = true;
                }
                if (s.Kind == "lmstudio") foreach (var kv in p.Procs) if (kv.Value == "lm studio.exe") pids.Add(kv.Key);
                long vram = 0, v;
                foreach (var pid in pids) if (gpu.TryGetValue(pid, out v)) vram += v;
                m.Vram = vram;
                if (s.Kind != "lmstudio") m.Ram = WorkingSet(pids);   // The Electron app's RAM does not belong to the model.
                // The counters were read: a process absent from them holds no video memory, which is not the same as unknown.
                if (m.Vram > 0 || gpu.Count > 0) m.VramOrigin = MemoryOrigin.Measured;
                if (m.Ram > 0) m.RamOrigin = MemoryOrigin.Measured;
                m.GpuShared = SharedWithCard(shared, pids, physicalMemory);
            }
        }

        /// <summary>
        /// System memory the processes share with the graphics card. A driver can report nonsense for a process (159 GB
        /// seen on a 32 GB PC): a figure larger than the PC's memory counts as unknown. The process's own resident
        /// memory is no bound: pinned memory it has partly swapped out is still shared.
        /// </summary>
        internal static long SharedWithCard(Dictionary<uint, long> shared, IEnumerable<uint> pids, long physicalMemory)
        {
            long total = 0, v;
            foreach (var pid in pids) if (shared.TryGetValue(pid, out v)) total += v;
            return total > 0 && total <= physicalMemory ? total : 0;
        }

        /// <summary>
        /// The size of each model's file, read once and kept while the model is loaded. With it, what is neither in
        /// video nor in system memory is known to be still on disk.
        /// </summary>
        private void AttachFileSizes(Probe p, List<LocalModel> models)
        {
            var seen = new HashSet<string>();
            foreach (var m in models.Where(x => !string.IsNullOrEmpty(x.File)))
            {
                string key = m.Where + "|" + m.File;
                seen.Add(key);
                long size;
                if (!fileSizes.TryGetValue(key, out size))
                {
                    if (m.Where == "WSL" && !p.WslUp) continue;
                    size = HostMemory.FileSize(m.File, m.Where == "WSL" ? p.WslRoot : null);
                    fileSizes[key] = size;
                }
                m.FileSize = size;
            }
            foreach (var key in fileSizes.Keys.Where(k => !seen.Contains(k)).ToList()) fileSizes.Remove(key);
        }

        /// <summary>The models that are the only one on their port: the memory of that port's process is theirs.</summary>
        private static List<LocalModel> AloneOnPort(IEnumerable<LocalModel> models)
        {
            return models.Where(m => m.Port > 0).GroupBy(m => m.Port).Where(g => g.Count() == 1).Select(g => g.First()).ToList();
        }

        /// <summary>Memory the processes hold in RAM (working set); those that have ended count nothing.</summary>
        private static long WorkingSet(IEnumerable<uint> pids)
        {
            long total = 0;
            foreach (var pid in pids)
            {
                try { using (var process = Process.GetProcessById((int)pid)) total += process.WorkingSet64; }
                catch (ArgumentException) { }
                catch (InvalidOperationException) { }
            }
            return total;
        }

        /// <summary>
        /// System memory of the models in WSL whose server reports none: the resident memory of the process serving
        /// each one, read from /proc as a file. /proc is listed only to find a process, and not again for a port that
        /// had none until a minute has passed (five on battery).
        /// </summary>
        private void AttachWslMemory(Probe p, List<LocalModel> models)
        {
            var unknown = AloneOnPort(models.Where(m => m.Where == "WSL")).Where(m => m.Vram == 0 && m.Ram == 0).ToList();
            foreach (var port in wslServers.Keys.Where(k => unknown.All(m => m.Port != k)).ToList()) wslServers.Remove(port);
            foreach (var port in wslMisses.Keys.Where(k => unknown.All(m => m.Port != k)).ToList()) wslMisses.Remove(port);
            if (!p.WslUp) return;
            foreach (var m in unknown)
            {
                uint pid;
                if (wslServers.TryGetValue(m.Port, out pid) && !HostMemory.Serves(HostMemory.CommandLine(p.WslRoot, pid), m.Port, m.File)) wslServers.Remove(m.Port);
                if (!wslServers.TryGetValue(m.Port, out pid))
                {
                    DateTime missedAt;
                    if (wslMisses.TryGetValue(m.Port, out missedAt) && (DateTime.Now - missedAt).TotalSeconds < (p.SavingPower ? 300 : 60)) continue;
                    pid = HostMemory.FindServer(p.WslRoot, m.Port, m.File);
                    if (pid == 0) { wslMisses[m.Port] = DateTime.Now; continue; }
                    wslMisses.Remove(m.Port);
                    wslServers[m.Port] = pid;
                }
                var memory = HostMemory.Process(p.WslRoot, pid);
                m.Ram = memory.Resident;
                m.RamMapped = memory.Mapped;
                m.Swapped = memory.Swapped;
                if (m.Ram > 0) m.RamOrigin = MemoryOrigin.Measured;
            }
        }

        /// <summary>
        /// Video memory used in WSL is all charged to vmmemWSL (and vmwp): it cannot be split between Linux processes.
        /// It is assigned to the model if that is the only one in WSL without its own figure.
        /// </summary>
        private void ShareWslMemory(Probe p, List<LocalModel> models)
        {
            var unknown = models.Where(m => m.Where == "WSL" && m.Vram == 0).ToList();
            if (unknown.Count != 1 || models.Any(m => m.Where == "WSL" && m.Vram > 0)) return;
            long v, total = 0;
            var gpu = GpuMemory.ByProcess;
            var machine = p.Procs.Where(kv => kv.Value == "vmmemwsl" || kv.Value == "vmmem" || kv.Value == "vmwp.exe").Select(kv => kv.Key).ToList();
            foreach (var pid in machine) if (gpu.TryGetValue(pid, out v)) total += v;
            unknown[0].Vram = total;
            if (total > 0 || gpu.Count > 0) unknown[0].VramOrigin = MemoryOrigin.Measured;
            unknown[0].GpuShared = SharedWithCard(GpuMemory.SharedByProcess, machine, physicalMemory);
        }

        // ================================================================ local HTTP
        private static object Get(int port, string path)
        {
            string text = GetText(port, path);
            return text == null ? null : Json.ParseAny(text);
        }

        /// <summary>The port answers with HTTP (any status code) within 800 ms.</summary>
        private static bool SpeaksHttp(int port)
        {
            try
            {
                var r = (HttpWebRequest)WebRequest.Create("http://127.0.0.1:" + port.ToString(CultureInfo.InvariantCulture) + "/");
                r.Timeout = r.ReadWriteTimeout = 800;
                r.Proxy = null;
                r.AllowAutoRedirect = false;
                r.Method = "GET";
                using (r.GetResponse()) return true;
            }
            catch (WebException e) { return e.Response != null; }
            catch (IOException) { return false; }
        }

        private static string GetText(int port, string path)
        {
            try
            {
                var r = (HttpWebRequest)WebRequest.Create("http://127.0.0.1:" + port.ToString(CultureInfo.InvariantCulture) + path);
                r.Timeout = r.ReadWriteTimeout = 1500;
                r.Proxy = null;
                r.AllowAutoRedirect = false;
                r.UserAgent = "ai-meter/" + Controller.Version;
                using (var resp = (HttpWebResponse)r.GetResponse())
                using (var sr = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                {
                    if ((int)resp.StatusCode >= 300) return null;
                    var buf = new char[262144];   // Enough for any useful response; avoids reading huge files.
                    int n = sr.ReadBlock(buf, 0, buf.Length);
                    return new string(buf, 0, n);
                }
            }
            catch (WebException) { return null; }
            catch (IOException) { return null; }
        }
    }
}
