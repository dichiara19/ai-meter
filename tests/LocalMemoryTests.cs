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
            test("Each memory's bar is split into what holds it, and WSL's share is part of the PC's", MemoryParts);
            test("A model partly on disk, in swap or shared with the card says so, with how each figure is known", Placement);
            test("What WSL uses outside RAM is stated only when it is worth it", OutsideRam);
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
            Assert(HostMemory.Wsl(root) == null, "A distribution without meminfo gave figures.");
            File.WriteAllText(Path.Combine(root, "proc", "meminfo"), "MemTotal:       16000000 kB\nMemFree:         1000000 kB\nMemAvailable:    4000000 kB\nSwapTotal:       8000000 kB\nSwapFree:        5000000 kB\n");
            var wsl = HostMemory.Wsl(root);
            Assert(wsl.Total == 16000000L * 1024 && wsl.Used == 12000000L * 1024, "Memory in use in WSL is not the total less what is available.");
            Assert(wsl.Touched == 15000000L * 1024 && wsl.SwapUsed == 3000000L * 1024 && wsl.SwapTotal == 8000000L * 1024, "What WSL asked Windows for, or its swap, was not read.");

            // A process: resident memory, the part mapped from files and what was swapped out.
            Directory.CreateDirectory(Path.Combine(root, "proc", "7"));
            File.WriteAllText(Path.Combine(root, "proc", "7", "status"), "VmRSS:\t 9000 kB\nRssAnon:\t 6000 kB\nRssFile:\t 3000 kB\nVmSwap:\t 500 kB\n");
            var process = HostMemory.Process(root, 7);
            Assert(process.Resident == 9000L * 1024 && process.Mapped == 3000L * 1024 && process.Swapped == 500L * 1024, "A process's memory was not split.");
            Assert(HostMemory.Process(root, 8).Resident == 0, "A process that ended gave figures.");

            // A model file, as its server names it: under the WSL root for a Linux path, never outside it.
            Directory.CreateDirectory(Path.Combine(root, "models"));
            File.WriteAllBytes(Path.Combine(root, "models", "m.gguf"), new byte[1234]);
            Assert(HostMemory.FileSize("/models/m.gguf", root) == 1234 && HostMemory.FileSize(Path.Combine(root, "models", "m.gguf"), null) == 1234, "A model file's size was not read.");
            Assert(HostMemory.FileSize("/models/../models/m.gguf", root) == 0 && HostMemory.FileSize("models/m.gguf", root) == 0 && HostMemory.FileSize("m.gguf", null) == 0
                && HostMemory.FileSize("/models/none.gguf", root) == 0 && HostMemory.FileSize(null, root) == 0, "A path that is relative, climbs or names nothing gave a size.");

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

        private static void MemoryParts()
        {
            long gb = 1L << 30;
            var p = new ProviderState
            {
                Id = "local", Running = true, GpuUsed = 7 * gb, GpuTotal = 8 * gb, RamUsed = 28 * gb, RamTotal = 32 * gb, WslUsed = 13 * gb, WslTotal = 16 * gb, WslHeld = 12 * gb,
                Models = new List<LocalModel>
                {
                    new LocalModel { Name = "a", Where = "WSL", Ram = 11 * gb, Vram = 6 * gb },
                    new LocalModel { Name = "b", Where = "Windows", Ram = 2 * gb },
                },
            };
            // Windows without its model, the model on Windows, the model in WSL, the rest of WSL, then WSL's room:
            // 4 GB to its ceiling of 16, all of which the PC still has free.
            var system = Ui.SystemMemoryParts(p);
            Assert(system.Select(x => x.Bytes / gb).SequenceEqual(new long[] { 14, 2, 11, 1, 4 }), "The PC's memory is not split into Windows, WSL and the models of each.");
            Assert(system.Where(x => !x.Room).Sum(x => x.Bytes) == p.RamUsed && system.Count(x => x.Room) == 1, "The segments in use do not add up to the memory in use.");
            Assert(system.Where(x => x.Name != null).Select(x => x.Name).SequenceEqual(new[] { "Windows 16 GB", "models 13 GB", "WSL 12/16 GB" }), "The legend does not name Windows, the models and WSL with its ceiling.");
            p.RamUsed = 30 * gb;
            Assert(Ui.SystemMemoryParts(p).Single(x => x.Room).Bytes == 2 * gb, "WSL's room exceeds what the PC has free.");
            p.RamUsed = 28 * gb;
            Assert(system.Single(x => x.Wsl).Name.StartsWith("WSL"), "The WSL logo is not on WSL's entry.");
            Assert(Ui.WslMemoryParts(p).Select(x => x.Bytes / gb).SequenceEqual(new long[] { 11, 2 }), "WSL's own memory is not split into its models and the rest.");
            Assert(Ui.VideoMemoryParts(p).Select(x => x.Bytes / gb).SequenceEqual(new long[] { 6, 1 }), "Video memory is not split into the models and other programs.");
            // The models keep one colour in every bar; little memory left colours the value, like a limit running out.
            Assert(system[1].Brush == system[2].Brush && system[2].Brush == Ui.VideoMemoryParts(p)[0].Brush && system[2].Brush == Ui.WslMemoryParts(p)[0].Brush, "The models change colour between bars.");
            Assert(Ui.MemoryLevel(28 * gb, 32 * gb) == "Critical" && Ui.MemoryLevel(20 * gb, 32 * gb) == "Caution" && Ui.MemoryLevel(8 * gb, 32 * gb) == "Text" && Ui.MemoryLevel(0, 0) == "Text", "A nearly full memory does not warn, or an unknown one does.");

            // A model larger than what Windows counts for WSL (paged out) cannot push the bar past what is in use.
            p.WslHeld = 5 * gb;
            system = Ui.SystemMemoryParts(p);
            Assert(system.All(x => x.Bytes >= 0) && system.Where(x => !x.Room).Sum(x => x.Bytes) == p.RamUsed && system[2].Bytes == 5 * gb, "A model larger than its place's share was not cut to it.");

            // Without WSL and without models there is one segment and one legend entry.
            var alone = new ProviderState { Id = "local", RamUsed = 8 * gb, RamTotal = 32 * gb, Models = new List<LocalModel>() };
            system = Ui.SystemMemoryParts(alone);
            Assert(system.Count(x => x.Bytes > 0) == 1 && system.Count(x => x.Name != null) == 1 && system[0].Brush != system[2].Brush, "A PC without WSL or models shows more than Windows.");

            // The bar: one column per holder that holds something, and one for what is free.
            var segments = (System.Windows.Controls.Grid)Ui.SegmentedBar(system, alone.RamTotal, 5).Children[1];
            Assert(segments.Children.Count == 1 && segments.ColumnDefinitions.Count == 2 && segments.ColumnDefinitions[1].Width.Value == 24 * gb, "The bar does not leave the free part empty.");
            // WSL's room is an outline, not a fill: it is free memory.
            p.WslHeld = 12 * gb;
            segments = (System.Windows.Controls.Grid)Ui.SegmentedBar(Ui.SystemMemoryParts(p), p.RamTotal, 5).Children[1];
            var room = (System.Windows.Controls.Border)segments.Children[4];
            Assert(segments.Children.Count == 5 && room.Background == null && room.BorderThickness.Left == 1, "WSL's room is drawn as memory in use.");
        }

        private static void Placement()
        {
            long gb = 1L << 30;
            // A 20 GB file: 8 GB in video memory, 6 GB in system memory (4 of them mapped from the file), so 6 GB still on disk.
            var m = new LocalModel
            {
                Name = "a", Server = "llama.cpp", Where = "WSL", Vram = 8 * gb, Ram = 6 * gb, RamMapped = 4 * gb, FileSize = 20 * gb, Swapped = gb, GpuShared = 2 * gb,
                VramOrigin = MemoryOrigin.Measured, RamOrigin = MemoryOrigin.Reported,
            };
            Assert(m.OnDisk == 6 * gb, "What is neither in video nor in system memory is not on disk.");
            var facts = Ui.LocalFacts(m);
            Assert(facts.Contains("~6.0 GB on disk") && facts.Contains("1.0 GB in swap"), "The facts do not say what is on disk and in swap.");
            Assert(Ui.LocalPlacement(m).SequenceEqual(new[]
            {
                "Video memory: 8.0 GB, measured from the process", "System memory: 6.0 GB, reported by the server", "Mapped from files: 4.0 GB",
                "Shared with the graphics card: 2.0 GB", "In swap: 1.0 GB", "Model file: 20 GB", "Still on disk: about 6.0 GB (estimate)",
            }), "The placement does not state each figure with how it is known.");
            Assert(Ui.PlacementParts(m).Select(x => x.Bytes / gb).SequenceEqual(new long[] { 8, 6, 7 }) && Ui.PlacementParts(m)[2].Room, "The model's bar is not video memory, system memory, then what is outside memory.");
            Assert(Ui.PlacementParts(m).Select(x => x.Name).SequenceEqual(new[] { "8.0 GB VRAM", "6.0 GB RAM", "~6.0 GB on disk · 1.0 GB in swap" }), "The bar's legend does not state each place.");
            Assert(Ui.LocalFacts(m, false).SequenceEqual(new[] { "llama.cpp (WSL)" }), "The facts repeat the memory the bar's legend states.");
            // In system memory and on disk only: the legend still names each part by its own figure.
            var onHost = new LocalModel { Name = "c", Server = "x", Where = "Windows", Ram = 4 * gb, FileSize = 9 * gb, VramOrigin = MemoryOrigin.Measured };
            Assert(Ui.PlacementParts(onHost).Select(x => x.Name).SequenceEqual(new[] { null, "4.0 GB RAM", "~5.0 GB on disk" }), "A model without video memory names its parts wrongly.");

            // More in memory than the file holds (a second copy of the weights): nothing is on disk, never a negative figure.
            m.Ram = 14 * gb;
            Assert(m.OnDisk == 0 && !Ui.LocalFacts(m).Any(f => f.Contains("on disk")), "A model larger in memory than its file is stated as on disk.");
            // Video memory that could not be read (one of several models in WSL): the file less system memory alone would
            // call "on disk" what is on the card. A 16 GB file with 13 GB in system memory was stated as 2.9 GB on disk.
            var unread = new LocalModel { Name = "d", Server = "x", Where = "WSL", Ram = 13 * gb, FileSize = 16 * gb, RamOrigin = MemoryOrigin.Measured };
            Assert(!unread.PlacementKnown && unread.OnDisk == 0 && !Ui.LocalFacts(unread).Any(f => f.Contains("on disk")), "An estimate was made without the video memory.");
            unread.VramOrigin = MemoryOrigin.Measured;
            Assert(unread.PlacementKnown && unread.OnDisk == 3 * gb, "Video memory read as none did not allow the estimate.");
            // Unknown memory, or an unknown file: no estimate at all.
            Assert(new LocalModel { FileSize = 20 * gb }.OnDisk == 0 && new LocalModel { Vram = 8 * gb }.OnDisk == 0, "An estimate was made from an unknown figure.");
            // A few megabytes are noise, not a place.
            var small = new LocalModel { Name = "b", Server = "x", Where = "Windows", Vram = 8 * gb, FileSize = 8 * gb + (100L << 20), Swapped = 10L << 20, VramOrigin = MemoryOrigin.Unknown };
            Assert(Ui.LocalFacts(small).Count == 2 && Ui.LocalPlacement(small).SequenceEqual(new[] { "Video memory: 8.0 GB", "Model file: 8.1 GB" }), "Noise was stated, or a figure of unknown origin was given one.");
            Assert(Ui.PlacementParts(small).Count(x => x.Bytes > 0) == 1, "A model in one memory gets a bar of several parts.");

            // Memory shared with the card: summed over the model's processes, and bounded by the PC's memory, not by the
            // process's resident memory (10.88 GB shared by a process with 10.85 GB resident was seen, the rest in swap).
            var shared = new Dictionary<uint, long> { { 1, 10 * gb }, { 2, gb }, { 3, 159 * gb } };
            Assert(LocalProvider.SharedWithCard(shared, new uint[] { 1, 2, 9 }, 32 * gb) == 11 * gb, "Shared memory is not summed over the processes.");
            Assert(LocalProvider.SharedWithCard(shared, new uint[] { 3 }, 32 * gb) == 0 && LocalProvider.SharedWithCard(shared, new uint[] { 9 }, 32 * gb) == 0
                && LocalProvider.SharedWithCard(shared, new uint[] { 1 }, 0) == 0, "A figure larger than the PC's memory, or one with nothing to bound it, was kept.");
        }

        private static void OutsideRam()
        {
            long gb = 1L << 30;
            var p = new ProviderState { Id = "local", WslTotal = 16 * gb, WslUsed = 14 * gb, WslTouched = 15 * gb, WslHeld = 5 * gb, WslSwapUsed = 3 * gb, Models = new List<LocalModel>() };
            Assert(p.WslOutside == 10 * gb, "What WSL uses outside RAM is not what it asked for less what the machine holds.");
            Assert(Ui.WslOutsideNotes(p).SequenceEqual(new[] { "~10 GB compressed or paged out by Windows", "3.0 GB in swap" }), "WSL's memory outside RAM is not stated.");
            Assert(Ui.WslMemoryLines(p).Contains("~10 GB compressed or paged out by Windows"), "The tooltip lacks WSL's memory outside RAM.");

            // The usual case: the machine holds what Linux asked for, give or take, and nothing is said.
            p.WslHeld = 15 * gb - (900L << 20);
            p.WslSwapUsed = 0;
            Assert(Ui.WslOutsideNotes(p).Count == 0, "A rounding difference was stated as memory paged out.");
            p.WslHeld = 16 * gb;
            Assert(p.WslOutside == 0, "A machine holding more than Linux asked for gave a negative figure.");
            p.WslHeld = 0;
            Assert(p.WslOutside == 0, "An estimate was made without knowing what the machine holds.");
        }

        private static void Translations()
        {
            var keys = new[] { "System memory", "System memory: {0}", "Inside WSL", "WSL memory: {0}", "Local models: {0}", "Held on Windows: {0}", "models {0}",
                "~{0} on disk", "{0} in swap", "Mapped from files: {0}", "Shared with the graphics card: {0}", "In swap: {0}", "Model file: {0}",
                "Still on disk: about {0} (estimate)", "{0}, reported by the server", "{0}, measured from the process", "~{0} compressed or paged out by Windows" };
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
