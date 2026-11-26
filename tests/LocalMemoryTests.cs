using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace AIMeter
{
    /// <summary>System memory of local models: read from /proc files in WSL, and stated beside the totals it is part of.</summary>
    internal static class LocalMemoryTests
    {
        internal static void Register(Action<string, Action> test)
        {
            test("A process in WSL is found by the port or the model file its command line names", FindsTheServer);
            test("Memory figures of /proc are read in bytes, and a missing one is unknown", ReadsProcFields);
            test("A model's system memory is stated inside the PC's and WSL's totals", MemoryLines);
            test("Every supported language words system and WSL memory", Translations);
        }

        private static void Assert(bool condition, string message) { if (!condition) throw new Exception(message); }

        /// <summary>A /proc entry as the WSL share shows it: arguments separated by NUL, resident memory in kB.</summary>
        private static void Process(string root, string pid, string[] arguments, long residentKilobytes)
        {
            string directory = Path.Combine(root, "proc", pid);
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "cmdline"), string.Concat(arguments.Select(a => a + "\0")));
            File.WriteAllText(Path.Combine(directory, "status"), "Name:\tx\nVmSize:\t 99999999 kB\nVmRSS:\t " + residentKilobytes + " kB\nRssAnon:\t 1 kB\n");
        }

        private static void FindsTheServer()
        {
            string root = Path.Combine(Settings.TestFolder, "wsl-memory");
            const string model = "/models/big.gguf";
            Process(root, "10", new[] { "bash", "/opt/start.sh", "--port", "8080" }, 3000);
            Process(root, "20", new[] { "/opt/llama-server", "-m", model, "--host", "127.0.0.1", "--port", "8080" }, 6000000);
            Process(root, "30", new[] { "curl", "http://127.0.0.1:8080" }, 9000000);
            Process(root, "40", new[] { "/usr/bin/docker-proxy", "-host-port", "9000", "-container-port", "9000" }, 5000);
            Process(root, "50", new[] { "/opt/swap", "-listen", "127.0.0.1:8800" }, 70000);
            Process(root, "60", new[] { "python3", "-m", "server", "--port=7000" }, 40000);
            Process(root, "70", new[] { "/opt/engine", model }, 2000000);
            Process(root, "80", new string[0], 0);   // A kernel thread has no command line.
            Directory.CreateDirectory(Path.Combine(root, "proc", "self"));

            Assert(HostMemory.FindServer(root, 8080, null) == 20, "The server was not preferred to its wrapper and to a client naming its URL.");
            Assert(HostMemory.FindServer(root, 8800, null) == 50, "An address ending in the port was not read.");
            Assert(HostMemory.FindServer(root, 7000, null) == 60, "An option with '=' was not read.");
            Assert(HostMemory.FindServer(root, 9000, null) == 0, "A proxy forwarding the port to a container was taken for the server.");
            Assert(HostMemory.FindServer(root, 808, null) == 0, "A port was matched by its first digits.");
            Assert(HostMemory.FindServer(root, 5555, model) == 20, "The model file did not find the process when no command line names the port.");
            Assert(HostMemory.FindServer(root, 5555, null) == 0, "A process was found with nothing to find it by.");
            Assert(HostMemory.Resident(root, 20) == 6000000L * 1024, "Resident memory was not read in bytes.");
            Assert(HostMemory.Resident(root, 999) == 0, "A process that ended did not read as unknown.");
            Assert(HostMemory.Serves(HostMemory.CommandLine(root, 20), 8080, null) && !HostMemory.Serves(HostMemory.CommandLine(root, 30), 8080, null), "A process number passed to another program still read as the server.");
        }

        private static void ReadsProcFields()
        {
            Assert(HostMemory.Field("MemTotal:       16376960 kB\nMemAvailable:    2665536 kB\n", "MemAvailable") == 2665536L * 1024, "A meminfo field was not read.");
            Assert(HostMemory.Field("VmRSS:\t   76088 kB\n", "VmRSS") == 76088L * 1024, "A tab-separated status field was not read.");
            Assert(HostMemory.Field("MemTotal: 1 kB\n", "Mem") == -1 && HostMemory.Field(null, "MemTotal") == -1, "An absent field did not read as absent.");

            string root = Path.Combine(Settings.TestFolder, "wsl-meminfo");
            Directory.CreateDirectory(Path.Combine(root, "proc"));
            long used, total;
            Assert(!HostMemory.Wsl(root, out used, out total) && used == 0 && total == 0, "A distribution without meminfo gave figures.");
            File.WriteAllText(Path.Combine(root, "proc", "meminfo"), "MemTotal:       16000000 kB\nMemFree:         1000000 kB\nMemAvailable:    4000000 kB\n");
            Assert(HostMemory.Wsl(root, out used, out total) && total == 16000000L * 1024 && used == 12000000L * 1024, "Memory in use in WSL is not the total less what is available.");

            // This PC's own memory, from Windows: more than a gigabyte, and some of it in use.
            Assert(HostMemory.Windows(out used, out total) && total > (1L << 30) && used > 0 && used < total, "Windows did not report this PC's memory.");
        }

        private static void MemoryLines()
        {
            long gb = 1L << 30;
            var p = new ProviderState
            {
                Id = "local", Running = true, RamUsed = 24 * gb, RamTotal = 32 * gb, WslUsed = 14 * gb, WslTotal = 16 * gb, WslHeld = 15 * gb,
                Models = new List<LocalModel>
                {
                    new LocalModel { Name = "a", Server = "llama.cpp", Where = "WSL", Ram = 11 * gb, Vram = 7 * gb },
                    new LocalModel { Name = "b", Server = "Ollama", Where = "Windows", Ram = 2 * gb },
                },
            };
            Assert(Ui.SystemMemoryLines(p).SequenceEqual(new[] { "System memory: 8.0 GB free of 32 GB", "Local models: 13 GB" }), "The PC's memory does not count the models of both places.");
            Assert(Ui.WslMemoryLines(p).SequenceEqual(new[] { "WSL memory: 2.0 GB free of 16 GB", "Local models: 11 GB", "Held on Windows: 15 GB" }), "WSL's memory does not say what its models and the virtual machine hold.");
            Assert(Ui.LocalMemoryLines(p).Count == 2 && Ui.LocalSummary(p).Contains("WSL memory: "), "The summary lacks the memories that are known.");
            Assert(Ui.LocalFacts(p.Models[0]).Contains("11 GB RAM") && Ui.LocalFacts(p.Models[0]).Contains("7.0 GB VRAM"), "A model in both memories does not state both.");

            p.Models.ForEach(m => m.Ram = 0);
            p.WslHeld = 0;
            Assert(Ui.SystemMemoryLines(p).Count == 1 && Ui.WslMemoryLines(p).Count == 1, "Unknown memory was stated as a figure.");
        }

        private static void Translations()
        {
            var keys = new[] { "System memory", "System memory: {0}", "WSL memory", "WSL memory: {0}", "Local models: {0}", "Held on Windows: {0}" };
            try
            {
                foreach (var lang in new[] { "it", "es", "fr", "de", "pt" })
                {
                    L.Init(lang);
                    foreach (var key in keys) Assert(L.T(key) != key, "Missing translation for " + lang + ": " + key);
                }
            }
            finally { L.Init("en"); }
        }
    }
}
