using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace AIMeter
{
    /// <summary>
    /// The copy of Pi that AI Meter keeps inside WSL (ADR 0005), beside the one on Windows: its pinned Linux build,
    /// its installation through wsl.exe, keys kept in step in both copies, and how the scan, the order of candidates,
    /// dispatch and the MCP tools offer it. No test starts WSL: the distribution is a folder under the test folder,
    /// standing for its file share, and wsl.exe a stand-in that records its arguments and unpacks the archive itself.
    /// </summary>
    internal static partial class ManagedPiTests
    {
        private static void RegisterWsl(Action<string, Action> test)
        {
            test("The managed Pi pins one release in two builds: a zip with pi.exe for Windows, a tar.gz with pi for WSL", PinsBothBuilds);
            test("Arguments for wsl.exe survive Windows' command-line splitting: spaces, quotes, backslashes and empty ones", QuotesArguments);
            test("The copy inside WSL is unpacked by tar through wsl.exe from the verified bytes, after chmod 700 on its settings", () => Isolated(InstallsInsideWsl));
            test("Inside WSL a stopped distribution, a failed chmod or tar, a wrong digest or a missing program install nothing", () => Isolated(RefusesBadInstallsInsideWsl));
            test("Installing inside WSL deletes stale partial files on both sides and never promotes them", () => Isolated(InstallInsideWslClearsStalePartials));
            test("One list of services fills the key file of both copies, each with its own count; a stopped WSL keeps its file", () => Isolated(SyncsBothCopies));
            test("Removing one copy keeps the choices for the other, and the copy inside a stopped WSL cannot be removed", () => Isolated(RemovesOneCopyOfTwo));
            test("The scan offers the copy inside WSL with Linux paths, after the user's own Pi there, service by service", () => Isolated(ScanOffersCopyInsideWsl));
            test("The copy inside WSL is offered from Windows and from WSL, and the preferred copy comes first", () => Isolated(SelectsAndOrdersBothCopies));
            test("Dispatch starts the copy inside WSL through wsl.exe from Windows and directly from WSL, with bash and no credentials", () => Isolated(DescribesCopyInsideWsl));
            test("The MCP tools list both copies and WSL's state, and describe and lease the copy inside WSL without its keys", () => Isolated(McpShowsCopyInsideWsl));
        }

        // ------------------------------------------------------------ a distribution under the test folder

        private const string Distro = "Test-Distro";

        /// <summary>
        /// A running distribution: its root as Windows would see it (\\wsl.localhost\Test-Distro), the user's home under
        /// it, and a stand-in for wsl.exe that records every argument list, answers chmod and unpacks what tar receives.
        /// </summary>
        private sealed class WslFixture
        {
            public string Root, Home;
            public WslPlace Place;
            public readonly List<string[]> Calls = new List<string[]>();
            /// <summary>The bytes tar received on its standard input, last time.</summary>
            public byte[] TarInput;
            public int ChmodExit, TarExit;

            public ManagedPi.Copy Copy { get { return ManagedPi.InWsl(Place); } }

            public string WindowsPath(string linux) { return Root + linux.Replace('/', '\\'); }

            public int Run(IList<string> arguments, Stream input)
            {
                var args = arguments.ToArray();
                Calls.Add(args);
                if (args.Length < 4 || args[0] != "-d" || args[1] != Distro || args[2] != "-e") return 127;
                if (args[3] == "chmod") return ChmodExit;
                if (args[3] != "tar" || input == null) return 127;
                var received = new MemoryStream();
                input.CopyTo(received);
                TarInput = received.ToArray();
                if (TarExit != 0) return TarExit;
                try { Untar(TarInput, WindowsPath(args[6])); }
                catch (InvalidDataException) { return 2; }
                return 0;
            }
        }

        /// <summary>The distribution AI Meter is set to, running, with wsl.exe replaced by the fixture's stand-in.</summary>
        private static WslFixture Wsl()
        {
            var w = new WslFixture { Root = Path.Combine(Settings.Folder, "wsl-" + Guid.NewGuid().ToString("N"), Distro) };
            w.Home = Path.Combine(w.Root, "home", "me");
            Directory.CreateDirectory(w.Home);
            w.Place = new WslPlace { Distro = Distro, Home = w.Home };
            Settings.Current.WslDistro = Distro;
            Probe.WslPlacesUp = new[] { w.Place };
            ManagedPi.RunWsl = w.Run;
            return w;
        }

        /// <summary>A tar.gz archive in memory: entry names and their text contents, null for a folder.</summary>
        private static byte[] TarGz(params string[] namesAndContents)
        {
            using (var memory = new MemoryStream())
            {
                using (var gzip = new GZipStream(memory, CompressionMode.Compress, true))
                {
                    for (int i = 0; i < namesAndContents.Length; i += 2)
                    {
                        bool folder = namesAndContents[i + 1] == null;
                        byte[] content = folder ? new byte[0] : Encoding.UTF8.GetBytes(namesAndContents[i + 1]);
                        var header = new byte[512];
                        Field(header, 0, namesAndContents[i], 100);
                        Field(header, 100, "0000755", 8);
                        Field(header, 108, "0001750", 8);
                        Field(header, 116, "0001750", 8);
                        Field(header, 124, Convert.ToString(content.Length, 8).PadLeft(11, '0'), 12);
                        Field(header, 136, "00000000000", 12);
                        for (int c = 148; c < 156; c++) header[c] = (byte)' ';
                        header[156] = (byte)(folder ? '5' : '0');
                        Field(header, 257, "ustar", 6);
                        Field(header, 263, "00", 2);
                        Field(header, 148, Convert.ToString(header.Sum(b => (int)b), 8).PadLeft(6, '0'), 7);
                        header[155] = (byte)' ';
                        gzip.Write(header, 0, header.Length);
                        gzip.Write(content, 0, content.Length);
                        int padding = (512 - content.Length % 512) % 512;
                        gzip.Write(new byte[padding], 0, padding);
                    }
                    gzip.Write(new byte[1024], 0, 1024);
                }
                return memory.ToArray();
            }
        }

        private static void Field(byte[] header, int offset, string text, int length)
        {
            var bytes = Encoding.ASCII.GetBytes(text);
            Array.Copy(bytes, 0, header, offset, Math.Min(bytes.Length, length));
        }

        /// <summary>What tar -xz --strip-components=1 does with the archives these tests make: their files, less the top folder.</summary>
        private static void Untar(byte[] archive, string folder)
        {
            if (!Directory.Exists(folder)) throw new InvalidDataException("tar: " + folder + ": no such folder");
            using (var gzip = new GZipStream(new MemoryStream(archive), CompressionMode.Decompress))
            {
                var header = new byte[512];
                while (ReadBlock(gzip, header, 512) && header.Any(b => b != 0))
                {
                    string name = Encoding.ASCII.GetString(header, 0, 100).TrimEnd('\0');
                    long size = Convert.ToInt64(Encoding.ASCII.GetString(header, 124, 12).Trim('\0', ' '), 8);
                    var content = new byte[size];
                    if (!ReadBlock(gzip, content, (int)size)) throw new InvalidDataException("tar: unexpected end");
                    int padding = (int)((512 - size % 512) % 512);
                    ReadBlock(gzip, new byte[padding], padding);
                    var parts = name.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length <= 1) continue;
                    string target = Path.Combine(folder, string.Join("\\", parts.Skip(1)));
                    if (header[156] == '5') { Directory.CreateDirectory(target); continue; }
                    Directory.CreateDirectory(Path.GetDirectoryName(target));
                    File.WriteAllBytes(target, content);
                }
            }
        }

        private static bool ReadBlock(Stream stream, byte[] buffer, int count)
        {
            int read = 0, n;
            while (read < count && (n = stream.Read(buffer, read, count - read)) > 0) read += n;
            return read == count;
        }

        private static readonly byte[] LinuxGood = TarGz("pi/", null, "pi/pi", "not really Linux Pi", "pi/theme/", null, "pi/theme/dark.json", "{}");

        private static void InstallInWsl(WslFixture w, byte[] archive = null, ManagedPi.Release release = null, Func<bool> cancelled = null)
        {
            archive = archive ?? LinuxGood;
            w.Copy.Install(release ?? ReleaseOf(archive, true), url => new MemoryStream(archive), null, cancelled);
        }

        /// <summary>The files left under a folder named as a partial download or unpacking.</summary>
        private static List<string> PartialsUnder(string folder)
        {
            if (!Directory.Exists(folder)) return new List<string>();
            return Directory.GetFileSystemEntries(folder, "*", SearchOption.AllDirectories).Where(p => p.EndsWith(".partial", StringComparison.OrdinalIgnoreCase)).ToList();
        }

        private static string DownloadFolder { get { return Path.Combine(Settings.Folder, "managed"); } }

        // ------------------------------------------------------------ pins and quoting

        private static void PinsBothBuilds()
        {
            var windows = ManagedPi.PinnedWindows;
            var linux = ManagedPi.PinnedLinux;
            Assert(windows.Version == ManagedPi.Version && linux.Version == ManagedPi.Version && ManagedPi.Version == "1.5.2", "The two builds are not of the one pinned release.");
            Assert(windows.ArchiveKind == ManagedPi.Zip && windows.ProgramName == "pi.exe" && windows.Url.EndsWith("/v1.5.2/pi-windows-x64.zip", StringComparison.Ordinal),
                "The Windows build's pin is wrong.");
            Assert(linux.ArchiveKind == ManagedPi.TarGz && linux.ProgramName == "pi" && linux.Url == "https://github.com/earendil-works/pi/releases/download/v1.5.2/pi-linux-x64.tar.gz",
                "The Linux build's kind, program or address is wrong.");
            Assert(linux.Sha256 == "811c6f2c9e7aaee252b5404252e556fb713aa209c1f7f924226aa77d4ef70cb5" && linux.ArchiveBytes == 58922566, "The Linux build's digest or size is not the one checked.");
            Assert(ManagedPi.Windows.Release == windows && !ManagedPi.Windows.InsideWsl && ManagedPi.Windows.PlaceKey == ManagedPi.Place, "The copy on Windows does not use the Windows build.");
            var inside = ManagedPi.InWsl(new WslPlace { Distro = Distro, Home = @"\\wsl.localhost\Test-Distro\home\me" });
            Assert(inside.Release == linux && inside.InsideWsl && inside.PlaceKey == ManagedPi.PlaceInWsl && inside.Where == "wsl" && inside.Distro == Distro,
                "The copy inside WSL does not use the Linux build.");
            Assert(inside.Root == @"\\wsl.localhost\Test-Distro\home\me\.aimeter\managed\pi" && inside.AgentPath(inside.ConfigFolder) == "/home/me/.aimeter/managed/pi/config"
                && inside.AgentPath(inside.ProgramPath) == "/home/me/.aimeter/managed/pi/1.5.2/pi" && inside.LinuxHome == "/home/me",
                "The copy inside WSL is not in the Linux home: " + inside.Root + ", " + inside.AgentPath(inside.ProgramPath));

            var definition = Agents.Get("pi").PlaceFor(ManagedPi.PlaceInWsl);
            var windowsPlace = Agents.Get("pi").PlaceFor(ManagedPi.Place);
            Assert(definition != null && definition.Verifies("1.5.2") && definition.Providers.SequenceEqual(windowsPlace.Providers), "agents/pi.json lacks the place managed_wsl, or its providers differ.");
            Assert(definition.Commands["read_only"] == "none" && definition.Commands["modify"] == "bash" && definition.Alternatives.Count == 0 && definition.EnvironmentPolicy == "no_credentials",
                "The place managed_wsl does not give bash without alternatives and without credentials.");
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr CommandLineToArgvW(string commandLine, out int count);

        [DllImport("kernel32.dll")]
        private static extern IntPtr LocalFree(IntPtr memory);

        /// <summary>How Windows splits a command line into arguments, after the program's name.</summary>
        private static List<string> Split(string commandLine)
        {
            int count;
            IntPtr argv = CommandLineToArgvW("wsl.exe " + commandLine, out count);
            try { return Enumerable.Range(1, count - 1).Select(i => Marshal.PtrToStringUni(Marshal.ReadIntPtr(argv, i * IntPtr.Size))).ToList(); }
            finally { LocalFree(argv); }
        }

        private static void QuotesArguments()
        {
            var cases = new[]
            {
                new[] { "-d", Distro, "-e", "tar", "-xz", "-C", "/home/me/My Projects/.aimeter/managed/pi/1.5.2-abc.partial", "--strip-components=1" },
                new[] { "", "plain", "two words", "tab\there", "say \"hi\"", @"trailing\", @"trailing space\ ", @"spaced\ folder\", @"a\\b", @"a\\""b", "\"", @"\\server\share\x y\" },
            };
            foreach (var arguments in cases)
            {
                string line = ManagedPi.CommandLine(arguments);
                var back = Split(line);
                Assert(back.SequenceEqual(arguments), "The command line " + line + " splits into [" + string.Join("|", back) + "]");
            }
            Assert(ManagedPi.CommandLine(new[] { "-d", "Ubuntu", "-e", "chmod", "700", "/home/me/.aimeter/managed/pi/config" }) == "-d Ubuntu -e chmod 700 /home/me/.aimeter/managed/pi/config",
                "Arguments that need no quotes were quoted.");
        }

        // ------------------------------------------------------------ installation inside WSL

        private static void InstallsInsideWsl()
        {
            var w = Wsl();
            Assert(!w.Copy.Installed && w.Copy.Stamp == "" && !Directory.Exists(w.Copy.Root), "A copy inside WSL counted as installed before any installation.");
            var progress = new List<long>();
            w.Copy.Install(ReleaseOf(LinuxGood, true), url => new MemoryStream(LinuxGood), progress.Add, null);

            var copy = w.Copy;
            Assert(copy.Installed && File.ReadAllText(copy.ProgramPath) == "not really Linux Pi", "The program was not unpacked where Installed looks.");
            Assert(copy.ProgramPath == Path.Combine(w.Home, @".aimeter\managed\pi\1.5.2\pi") && File.Exists(Path.Combine(w.Home, @".aimeter\managed\pi\1.5.2\theme\dark.json")),
                "The archive's top folder was not stripped, or a nested file is missing.");
            Assert(progress.Count > 0 && progress.Last() == LinuxGood.Length, "Progress was not reported up to the archive's size.");
            Assert(!ManagedPi.Windows.Installed && !Directory.Exists(ManagedPi.Windows.Root), "Installing inside WSL touched the copy on Windows.");

            // chmod 700 on the settings folder first, then tar with exactly these arguments.
            Assert(w.Calls.Count == 2, "wsl.exe was not started twice: " + string.Join(" / ", w.Calls.Select(c => string.Join(" ", c))));
            Assert(w.Calls[0].SequenceEqual(new[] { "-d", Distro, "-e", "chmod", "700", "/home/me/.aimeter/managed/pi/config" }),
                "chmod was not asked for the settings folder: " + string.Join(" ", w.Calls[0]));
            var tar = w.Calls[1];
            Assert(tar.Length == 9 && tar.Take(6).SequenceEqual(new[] { "-d", Distro, "-e", "tar", "-xz", "-C" }) && tar.Skip(7).SequenceEqual(new[] { "--strip-components=1", "--no-same-owner" }),
                "tar's arguments are wrong: " + string.Join(" ", tar));
            Assert(tar[6].StartsWith("/home/me/.aimeter/managed/pi/1.5.2-", StringComparison.Ordinal) && tar[6].EndsWith(".partial", StringComparison.Ordinal),
                "tar did not unpack into a new staging folder under the copy's root: " + tar[6]);
            Assert(w.TarInput != null && w.TarInput.SequenceEqual(LinuxGood), "tar did not receive exactly the verified bytes.");

            // Nothing is left behind, on either side, and nothing lands in the home outside the copy's folder.
            Assert(PartialsUnder(w.Home).Count == 0 && PartialsUnder(DownloadFolder).Count == 0, "A partial file was left: " + string.Join(", ", PartialsUnder(w.Home).Concat(PartialsUnder(DownloadFolder))));
            Assert(Directory.GetFileSystemEntries(w.Home).SequenceEqual(new[] { Path.Combine(w.Home, ".aimeter") }), "The installation wrote into the home outside .aimeter.");
            Assert(Directory.GetDirectories(copy.Root).Select(Path.GetFileName).OrderBy(n => n, StringComparer.Ordinal).SequenceEqual(new[] { "1.5.2", "config", "models" }),
                "The copy's root holds more than the release, its settings and the model lists: " + string.Join(", ", Directory.GetDirectories(copy.Root)));
            Assert(copy.ModelsFile("openrouter") == Path.Combine(copy.Root, @"models\1.5.2\openrouter.json") && File.ReadAllText(copy.ModelsFile("openrouter")) == RepositoryModelList("openrouter"),
                "Pi's model lists were not written beside the copy inside WSL.");
            Assert(copy.BoundShells.Length == 0 && !File.Exists(Path.Combine(copy.ConfigFolder, "settings.json")), "The copy inside WSL was bound to a Windows shell.");
            Assert(copy.Stamp == "0", "An installed copy without keys has the wrong stamp: " + copy.Stamp);

            // A Windows archive is not for this copy.
            Fails<ArgumentException>(() => copy.Install(ReleaseOf(Good), url => new MemoryStream(Good), null, null), "a zip archive inside WSL");
        }

        private static void RefusesBadInstallsInsideWsl()
        {
            var w = Wsl();
            var copy = w.Copy;
            string programFolder = Path.Combine(copy.Root, ManagedPi.Version);
            Action<string> nothingLeft = what =>
            {
                Assert(!copy.Installed && !Directory.Exists(programFolder), "A copy was installed after " + what + ".");
                Assert(PartialsUnder(w.Home).Count == 0 && PartialsUnder(DownloadFolder).Count == 0, "A partial file was left after " + what + ".");
            };

            // Not running: refused before anything is written or started, inside WSL or on Windows.
            Probe.WslPlacesUp = new WslPlace[0];
            bool opened = false;
            string message = Fails<ManagedPi.InstallException>(() => copy.Install(ReleaseOf(LinuxGood, true), url => { opened = true; return new MemoryStream(LinuxGood); }, null, null), "a stopped distribution");
            Assert(message.Contains("Start it") && !opened && w.Calls.Count == 0 && !Directory.Exists(copy.Root) && !Directory.Exists(DownloadFolder),
                "A stopped distribution was written to, started or downloaded for.");
            Probe.WslPlacesUp = new[] { w.Place };

            // chmod fails: nothing is downloaded.
            w.ChmodExit = 1;
            Fails<ManagedPi.InstallException>(() => copy.Install(ReleaseOf(LinuxGood, true), url => { opened = true; return new MemoryStream(LinuxGood); }, null, null), "a failed chmod");
            Assert(!opened && w.Calls.All(c => c[3] == "chmod"), "The archive was downloaded or unpacked although the settings folder could not be made private.");
            nothingLeft("a failed chmod");
            w.ChmodExit = 0;

            // A wrong digest: tar never runs.
            var wrongDigest = ReleaseOf(LinuxGood, true);
            wrongDigest.Sha256 = new string('0', 64);
            w.Calls.Clear();
            Fails<ManagedPi.InstallException>(() => InstallInWsl(w, LinuxGood, wrongDigest), "a wrong digest");
            Assert(!w.Calls.Any(c => c[3] == "tar"), "tar ran on an archive with the wrong digest.");
            nothingLeft("a wrong digest");

            // tar fails, or the archive has no program.
            w.TarExit = 2;
            Fails<ManagedPi.InstallException>(() => InstallInWsl(w), "a tar that exits with 2");
            nothingLeft("a tar that exits with 2");
            w.TarExit = 0;
            var noProgram = TarGz("pi/", null, "pi/other", "x");
            Fails<ManagedPi.InstallException>(() => InstallInWsl(w, noProgram), "an archive without pi");
            nothingLeft("an archive without pi");
            Fails<OperationCanceledException>(() => InstallInWsl(w, null, null, () => true), "a cancelled download");
            nothingLeft("a cancelled download");

            // A failed installation over a complete copy leaves it as it was.
            InstallInWsl(w);
            Fails<ManagedPi.InstallException>(() => InstallInWsl(w, LinuxGood, wrongDigest), "a wrong digest over a copy");
            w.TarExit = 2;
            Fails<ManagedPi.InstallException>(() => InstallInWsl(w), "a failed tar over a copy");
            w.TarExit = 0;
            Fails<ManagedPi.InstallException>(() => InstallInWsl(w, noProgram), "an archive without pi over a copy");
            Assert(copy.Installed && File.ReadAllText(copy.ProgramPath) == "not really Linux Pi", "A failed installation destroyed the copy inside WSL.");
            Assert(PartialsUnder(w.Home).Count == 0 && PartialsUnder(DownloadFolder).Count == 0, "A failed installation over a copy left files.");
        }

        private static void InstallInsideWslClearsStalePartials()
        {
            var w = Wsl();
            var copy = w.Copy;
            string staleFolder = Path.Combine(copy.Root, ManagedPi.Version + "-old.partial");
            string staleArchive = Path.Combine(copy.Root, ManagedPi.Version + "-old.tar.gz.partial");
            string staleDownload = Path.Combine(DownloadFolder, "pi-linux-" + ManagedPi.Version + "-old.tar.gz.partial");
            Directory.CreateDirectory(staleFolder);
            File.WriteAllText(Path.Combine(staleFolder, "pi"), "planted");
            File.WriteAllBytes(staleArchive, TarGz("pi/", null, "pi/pi", "planted Pi"));
            Directory.CreateDirectory(DownloadFolder);
            File.WriteAllBytes(staleDownload, TarGz("pi/", null, "pi/pi", "planted Pi"));

            InstallInWsl(w);
            Assert(copy.Installed && File.ReadAllText(copy.ProgramPath) == "not really Linux Pi", "The verified archive was not the one installed.");
            Assert(!Directory.Exists(staleFolder) && !File.Exists(staleArchive) && !File.Exists(staleDownload), "A stale partial folder or archive was left.");
            Assert(PartialsUnder(w.Home).Count == 0 && PartialsUnder(DownloadFolder).Count == 0, "A partial file was left.");
        }

        // ------------------------------------------------------------ keys in two copies

        private static string KeyFileOf(ManagedPi.Copy copy) { return Path.Combine(copy.ConfigFolder, "auth.json"); }

        private static long RevisionOf(ManagedPi.Copy copy)
        {
            string file = Path.Combine(copy.ConfigFolder, "keys-revision");
            return File.Exists(file) ? long.Parse(File.ReadAllText(file).Trim(), System.Globalization.CultureInfo.InvariantCulture) : 0;
        }

        private static void SyncsBothCopies()
        {
            var w = Wsl();
            var settings = Settings.Current;
            InstallInWsl(w);
            Install(Good, ReleaseOf(Good));
            ApiKeys.Save("openrouter", RouterKey);
            ApiKeys.Save("gemini", GeminiKey);
            settings.ManagedPiServices = new List<string> { "openrouter", "gemini" };

            var windows = ManagedPi.Windows;
            var inside = w.Copy;
            Assert(ManagedPi.Sync(settings), "The keys could not be copied.");
            Assert(File.Exists(KeyFileOf(windows)) && File.ReadAllText(KeyFileOf(inside)) == File.ReadAllText(KeyFileOf(windows)) && File.ReadAllText(KeyFileOf(inside)).Contains(GeminiKey),
                "The two copies do not hold the same keys from the one list.");
            Assert(RevisionOf(windows) == 2 && RevisionOf(inside) == 2, "Each copy does not count its own change: " + RevisionOf(windows) + ", " + RevisionOf(inside));
            string provider;
            Assert(inside.KeyFor(Definitions.Get("openrouter"), out provider) == RouterKey && provider == "openrouter", "The copy inside WSL does not give its key to the scan.");
            Assert(inside.Stamp != "0" && inside.Stamp != "", "The stamp of the copy inside WSL does not follow its key file.");

            settings.ManagedPiServices = new List<string> { "openrouter" };
            Assert(ManagedPi.Sync(settings) && !File.ReadAllText(KeyFileOf(inside)).Contains(GeminiKey) && !File.ReadAllText(KeyFileOf(windows)).Contains(GeminiKey),
                "Unticking a service did not take it out of both copies.");
            Assert(RevisionOf(windows) == 4 && RevisionOf(inside) == 4, "A change was not counted in both copies.");

            // WSL stops: its copy is out of reach, and its key file stays as it is until it runs again.
            string held = File.ReadAllText(KeyFileOf(inside));
            Probe.WslPlacesUp = new WslPlace[0];
            settings.ManagedPiServices = new List<string>();
            Assert(ManagedPi.Sync(settings) && !File.Exists(KeyFileOf(windows)), "The copy on Windows did not lose its keys.");
            Assert(File.ReadAllText(KeyFileOf(inside)) == held && RevisionOf(inside) == 4 && w.Calls.Count(c => c[3] != "chmod" && c[3] != "tar") == 0,
                "The key file inside a stopped WSL was touched.");
            Probe.WslPlacesUp = new[] { w.Place };
            Assert(ManagedPi.Sync(settings) && !File.Exists(KeyFileOf(inside)) && RevisionOf(inside) == 6, "The copy inside WSL did not lose its keys once WSL ran again.");

            // Keys are never written into a settings folder that the installation did not make private.
            Directory.Delete(inside.ConfigFolder, true);
            settings.ManagedPiServices = new List<string> { "openrouter" };
            Assert(!ManagedPi.Sync(settings) && !Directory.Exists(inside.ConfigFolder) && File.Exists(KeyFileOf(windows)),
                "Keys were written inside WSL into a settings folder made by a sync, or the copy on Windows lost them.");
        }

        private static void RemovesOneCopyOfTwo()
        {
            var w = Wsl();
            var settings = Settings.Current;
            InstallInWsl(w);
            InstallWithKeys();
            var inside = w.Copy;
            Assert(File.Exists(KeyFileOf(inside)), "The test needs keys in the copy inside WSL.");

            // WSL stopped: the copy there cannot be removed, and nothing changes.
            Probe.WslPlacesUp = new WslPlace[0];
            Fails<ManagedPi.InstallException>(() => inside.Remove(settings), "a removal inside a stopped WSL");
            Assert(File.Exists(KeyFileOf(inside)) && File.Exists(inside.ProgramPath) && settings.ManagedPiServices.Count == 2, "A removal inside a stopped WSL changed something.");
            Probe.WslPlacesUp = new[] { w.Place };

            // One copy removed: its keys and program go, the choices stay for the other.
            long before = RevisionOf(inside);
            inside.Remove(settings);
            Assert(!inside.Installed && !File.Exists(KeyFileOf(inside)) && RevisionOf(inside) > before && RevisionOf(inside) % 2 == 0, "The copy inside WSL was not removed with its keys.");
            Assert(Directory.GetFileSystemEntries(inside.Root, "*", SearchOption.AllDirectories).SequenceEqual(new[] { inside.ConfigFolder, Path.Combine(inside.ConfigFolder, "keys-revision") }),
                "Removing the copy inside WSL left more than its count.");
            Assert(settings.ManagedPiServices.SequenceEqual(new[] { "openrouter", "gemini" }) && ManagedPi.Windows.Installed && File.Exists(KeyFileOf(ManagedPi.Windows)),
                "Removing one copy forgot the choices or touched the other copy.");

            // The last copy removed: the choices go.
            ManagedPi.Windows.Remove(settings);
            Assert(settings.ManagedPiServices.Count == 0, "Removing the last copy kept the choices.");
        }

        // ------------------------------------------------------------ scan, order and dispatch

        private static void ScanOffersCopyInsideWsl()
        {
            var w = Wsl();
            string windowsHome = Home();
            Assert(!HasManaged(Candidates.Scan(windowsHome, new[] { w.Place }, Candidates.Kinds, false)), "A copy that is not installed was offered.");
            InstallInWsl(w);
            InstallWithKeys();

            var scan = Candidates.Scan(windowsHome, new[] { w.Place }, Candidates.Kinds, false);
            var places = scan.Found.Where(f => f.Managed).OrderBy(f => f.Where, StringComparer.Ordinal).ToList();
            Assert(places.Count == 2 && places[1].Runtime == "pi" && places[1].Where == "wsl" && places[1].Distro == Distro, "The copy inside WSL is not found as Pi in its distribution.");
            var inside = scan.Access.Where(a => a.Managed && a.Where == "wsl").OrderBy(a => a.Service, StringComparer.Ordinal).ToList();
            Assert(inside.Select(a => a.Service + ":" + a.Provider).SequenceEqual(new[] { "gemini:google", "openrouter:openrouter" }), "The copy's accesses inside WSL are not its copied keys.");
            var copy = w.Copy;
            foreach (var a in inside)
            {
                Assert(a.Runtime == "pi" && a.Distro == Distro && a.Program == "/home/me/.aimeter/managed/pi/1.5.2/pi" && a.Home == "/home/me" && a.Version == ManagedPi.Version,
                    "The access inside WSL lacks its Linux program, home or version: " + a.Program);
                Assert(a.ConfigFolder == "/home/me/.aimeter/managed/pi/config" && a.ConfigFolderFromWindows == copy.ConfigFolder && !a.NeedsNode && a.Shells.Length == 0,
                    "The access inside WSL lacks its settings folder on either side, or has a shell or a node.");
                Assert(a.ModelsFile == copy.ModelsFile(a.Provider) && File.Exists(a.ModelsFile) && a.ModelsFormat == "pi", "The access inside WSL does not name a model list AI Meter can read.");
            }
            Assert(scan.Access.Count(a => a.Managed && a.Where == "windows") == 2, "The copy on Windows was not offered beside it.");
            Assert(!Candidates.Scan(windowsHome, new WslPlace[0], Candidates.Kinds, false).Access.Any(a => a.Where == "wsl"), "The copy inside a stopped WSL was offered.");
            Settings.Current.WslDistro = "Other-Distro";
            Assert(!Candidates.Scan(windowsHome, new[] { w.Place }, Candidates.Kinds, false).Access.Any(a => a.Managed && a.Where == "wsl"),
                "A copy in a distribution AI Meter is not set to was offered.");
            Settings.Current.WslDistro = Distro;

            // The user's own Pi in the distribution wins, service by service, unless the user asked for both.
            Directory.CreateDirectory(Path.Combine(w.Home, @".pi\agent"));
            File.WriteAllText(Path.Combine(w.Home, @".pi\agent\auth.json"), "{ \"openrouter\": { \"type\": \"api_key\", \"key\": \"" + RouterKey + "\" } }");
            Directory.CreateDirectory(Path.Combine(w.Home, @".local\bin"));
            File.WriteAllText(Path.Combine(w.Home, @".local\bin\pi"), "own Pi");
            var own = Candidates.Scan(windowsHome, new[] { w.Place }, Candidates.Kinds, true);
            Assert(own.Access.Any(a => a.Where == "wsl" && !a.Managed && a.Service == "openrouter"), "The user's own Pi in WSL was not found with its key.");
            Assert(own.Access.Where(a => a.Managed && a.Where == "wsl").Select(a => a.Service).SequenceEqual(new[] { "gemini" }),
                "The copy inside WSL was not offered exactly for the services the user's own Pi there cannot call.");
            // It still holds both keys, and is reported so: a copy with keys must not read as one without.
            Assert(own.Found.Single(p => p.Managed && p.Where == "wsl").Services.OrderBy(s => s, StringComparer.Ordinal).SequenceEqual(new[] { "gemini", "openrouter" }),
                "The copy inside WSL does not report every service it holds a key for.");
            Assert(own.Access.Count(a => a.Managed && a.Where == "windows") == 2, "The user's Pi in WSL hid the copy on Windows.");
            var both = Candidates.Scan(windowsHome, new[] { w.Place }, Candidates.Kinds, true, true);
            Assert(both.Access.Count(a => a.Managed && a.Where == "wsl") == 2, "Both were not offered inside WSL when asked for.");
        }

        private static void SelectsAndOrdersBothCopies()
        {
            NetworkBackoff.Clear();
            var w = Wsl();
            InstallInWsl(w);
            InstallWithKeys();
            var scan = Candidates.Scan(Home(), new[] { w.Place }, Candidates.Kinds, false);
            Func<Requirements, List<string>> order = req => Candidates.Select(Fixture(), scan, req, DateTime.Now).Chosen.Where(c => c.Model.Id == Model)
                .Select(c => c.Access.Where + (c.Access.Managed ? "/managed" : "")).ToList();

            Assert(order(new Requirements { Service = "openrouter", From = "wsl" }).SequenceEqual(new[] { "wsl/managed" }), "From WSL, the copy on Windows was offered, or the one inside WSL was not.");
            foreach (var from in new[] { null, "windows" })
            {
                Assert(order(new Requirements { Service = "openrouter", From = from }).SequenceEqual(new[] { "wsl/managed", "windows/managed" }),
                    "By default the copy inside WSL does not come first (from " + (from ?? "nowhere") + ").");
                Assert(order(new Requirements { Service = "openrouter", From = from, ManagedPiPrefer = "windows" }).SequenceEqual(new[] { "windows/managed", "wsl/managed" }),
                    "The preference for the copy on Windows was not followed (from " + (from ?? "nowhere") + ").");
            }
            Assert(order(new Requirements { Service = "openrouter", From = "wsl", ManagedPiPrefer = "windows" }).SequenceEqual(new[] { "wsl/managed" }),
                "Preferring the copy on Windows offered it to a caller in WSL.");

            // A user's own agent still comes before either copy.
            var withOwn = Candidates.Scan(Home(), new[] { w.Place }, Candidates.Kinds, false);
            withOwn.Found.Add(new RuntimePlace { Runtime = "opencode", Where = "windows" });
            withOwn.Access.Add(Candidates.Access("openrouter", "opencode", "windows", RouterKey));
            var chosen = Candidates.Select(Fixture(), withOwn, new Requirements { Service = "openrouter", From = "windows" }, DateTime.Now).Chosen.Where(c => c.Model.Id == Model).ToList();
            Assert(chosen.Count == 3 && chosen[0].Access.Runtime == "opencode" && chosen[1].Access.Where == "wsl" && chosen[2].Access.Where == "windows",
                "The user's own agent does not come before both copies.");
        }

        private static void DescribesCopyInsideWsl()
        {
            var w = Wsl();
            InstallInWsl(w);
            InstallWithKeys();
            var router = Candidates.Scan(Home(), new[] { w.Place }, Candidates.Kinds, false).Access.Single(a => a.Managed && a.Where == "wsl" && a.Service == "openrouter");
            string program = "/home/me/.aimeter/managed/pi/1.5.2/pi", config = "/home/me/.aimeter/managed/pi/config";

            // From Windows: wsl.exe, env with the copy's variables, the program by its Linux path.
            string workspace = @"C:\src\my project";
            var modify = Dispatch.Describe(router, Model, Dispatch.Modify, workspace, "windows", router.Distro, router.Version);
            Assert(modify.Supported && modify.Managed && modify.VersionVerified && modify.Program == "wsl.exe" && modify.Where == "wsl", "The copy inside WSL was not described from Windows: " + modify.Reason);
            var expected = new List<string> { "-d", Distro, "--cd", workspace, "-e", "env", "PATH=/home/me/.aimeter/managed/pi/1.5.2:/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin",
                "PI_CODING_AGENT_DIR=" + config, "PI_OFFLINE=1", "PI_SKIP_VERSION_CHECK=1", "PI_TELEMETRY=0", program,
                "-p", "--mode", "json", "--no-session", "--no-extensions", "--no-mcp", "--no-context-files", "--no-skills", "--no-prompt-templates", "--tools", "read,bash,edit,write",
                "--provider", "openrouter", "--model", Model };
            Assert(modify.Arguments.SequenceEqual(expected), "wsl.exe's arguments are wrong: " + string.Join(" ", modify.Arguments));
            Assert(modify.Environment.Count == 0 && modify.WorkingDirectory == null && modify.Commands == "bash" && modify.Alternatives.Count == 0,
                "From Windows the copy inside WSL has an environment, a folder, a shell other than bash or an alternative.");
            Assert(modify.EnvironmentPolicy == "no_credentials" && !modify.CleanEnvironment, "The copy inside WSL may inherit the plugin's credentials.");
            Assert(modify.ManagedConfig == w.Copy.ConfigFolder && modify.ManagedConfig.StartsWith(w.Home, StringComparison.OrdinalIgnoreCase),
                "From Windows managed_config is not the settings folder through the distribution's share: " + modify.ManagedConfig);
            var readOnly = Dispatch.Describe(router, Model, Dispatch.ReadOnly, workspace, "windows", router.Distro, router.Version);
            Assert(readOnly.Supported && readOnly.Commands == "none" && After(readOnly.Arguments, "--tools") == "read,grep,find,ls", "A read-only task of the copy inside WSL can run commands.");
            Assert(modify.Notes.Any(n => n.Contains("/mnt/")) && !modify.Notes.Any(n => n.Contains("PowerShell tool")), "The notes are not those of the copy inside WSL.");

            // A folder inside WSL is fine for this copy, from Windows too.
            var insideFolder = Dispatch.Describe(router, Model, Dispatch.Modify, @"\\wsl.localhost\Test-Distro\home\me\repo", "windows", router.Distro, router.Version);
            Assert(insideFolder.Supported, "A folder inside WSL was refused for the copy inside WSL: " + insideFolder.Reason);

            // From WSL: the program itself, with the variables in its environment and the Linux settings folder.
            var fromWsl = Dispatch.Describe(router, Model, Dispatch.Modify, "/home/me/repo", "wsl", router.Distro, router.Version);
            Assert(fromWsl.Supported && fromWsl.Program == program && fromWsl.WorkingDirectory == "/home/me/repo" && After(fromWsl.Arguments, "--tools") == "read,bash,edit,write",
                "The copy inside WSL was not described from WSL: " + fromWsl.Reason);
            Assert(fromWsl.Environment["PI_CODING_AGENT_DIR"] == config && fromWsl.Environment["PI_OFFLINE"] == "1" && fromWsl.Environment["PATH"].StartsWith("/home/me/.aimeter/managed/pi/1.5.2:", StringComparison.Ordinal),
                "From WSL the copy's environment is wrong.");
            Assert(fromWsl.ManagedConfig == config, "From WSL managed_config is not the Linux path: " + fromWsl.ManagedConfig);

            // The copy on Windows keeps its own: its Windows folder, from Windows only.
            var onWindows = Candidates.Scan(Home(), new[] { w.Place }, Candidates.Kinds, false).Access.Single(a => a.Managed && a.Where == "windows" && a.Service == "openrouter");
            var windowsPlan = Dispatch.Describe(onWindows, Model, Dispatch.Modify, @"C:\w", "windows", null, onWindows.Version);
            Assert(windowsPlan.Supported && windowsPlan.ManagedConfig == ManagedPi.Windows.ConfigFolder && windowsPlan.Commands == "powershell", "The copy on Windows changed.");
        }

        // ------------------------------------------------------------ MCP

        private static void McpShowsCopyInsideWsl()
        {
            NetworkBackoff.Clear();
            var w = Wsl();
            var snap = Fixture();
            string home = Home();
            var server = new McpServer(() => snap, () => false, null, null, () => Candidates.Scan(home, Probe.WslPlacesUp, Candidates.Kinds, false), new LeaseBook(null));
            string raw;
            var answers = new List<string>();

            // Running, nothing installed, no Git for Windows.
            var before = Call(server, "list_candidates", "{}", out raw);
            var entry = ManagedEntry(before, "wsl");
            Assert(entry != null && Json.Str(entry, "distro") == Distro && Json.Bool(entry, "reachable") && !Json.Bool(entry, "installed") && Json.Str(entry, "version") == ManagedPi.Version
                && Json.Str(entry, "set_up_in") != null && Json.Str(entry, "note") != null, "runtimes.managed does not list the copy inside a running WSL as not installed: " + raw);
            var environment = Json.Obj(Json.Obj(before, "runtimes"), "environment");
            Assert(Json.Str(environment, "wsl") == "running" && Json.Str(environment, "distro") == Distro && !Json.Bool(environment, "git_for_windows"),
                "runtimes.environment does not say that WSL runs and Git for Windows is missing: " + raw);

            InstallInWsl(w);
            InstallWithKeys();
            var listed = Call(server, "list_candidates", "{}", out raw);
            answers.Add(raw);
            Assert(Items(Json.Obj(listed, "runtimes"), "managed").Count == 2, "runtimes.managed does not have one entry per place.");
            entry = ManagedEntry(listed, "wsl");
            Assert(Json.Bool(entry, "installed") && ((IList)entry["services"]).Cast<string>().SequenceEqual(new[] { "gemini", "openrouter" }), "The entry inside WSL does not list its services.");
            var candidates = Items(listed, "candidates");
            Assert(candidates.Count == 2 && Json.Str(Json.Obj(candidates[0], "runtime"), "where") == "wsl" && Json.Str(Json.Obj(candidates[0], "runtime"), "distro") == Distro
                && IsManaged(Json.Obj(candidates[0], "runtime")) && IsManaged(Json.Obj(candidates[1], "runtime")), "The copy inside WSL is not the first candidate: " + raw);
            Assert(Items(Json.Obj(listed, "runtimes"), "found").Count(IsManaged) == 2, "runtimes.found does not list both copies.");
            var fromWsl = Call(server, "list_candidates", "{\"from\":\"wsl\"}", out raw);
            answers.Add(raw);
            var offered = Items(fromWsl, "candidates");
            Assert(offered.Count == 1 && Json.Str(Json.Obj(offered[0], "runtime"), "where") == "wsl" && IsManaged(Json.Obj(offered[0], "runtime")), "A caller in WSL was not offered exactly the copy inside WSL.");

            string candidateArgs = "{\"service\":\"openrouter\",\"model\":\"" + Model + "\",\"runtime\":\"pi\",\"managed\":true";
            var described = Call(server, "describe_dispatch", candidateArgs + ",\"where\":\"wsl\",\"distro\":\"" + Distro + "\",\"mode\":\"modify\",\"workspace\":\"C:\\\\w\"}", out raw);
            answers.Add(raw);
            Assert(Json.Bool(described, "supported") && Json.Bool(described, "managed") && Json.Str(described, "program") == "wsl.exe" && Json.Str(described, "where") == "wsl"
                && Json.Str(described, "distro") == Distro && Json.Str(described, "commands") == "bash" && !described.ContainsKey("alternatives"), "describe_dispatch did not describe the copy inside WSL: " + raw);
            Assert(Json.Str(described, "managed_config") == w.Copy.ConfigFolder && Json.Str(described, "environment_policy") == "no_credentials",
                "From Windows managed_config is not the share path, or the policy is missing: " + raw);
            var inWsl = Call(server, "describe_dispatch", candidateArgs + ",\"where\":\"wsl\",\"from\":\"wsl\",\"workspace\":\"/home/me/repo\"}", out raw);
            answers.Add(raw);
            Assert(Json.Str(inWsl, "managed_config") == "/home/me/.aimeter/managed/pi/config" && Json.Str(inWsl, "program") == "/home/me/.aimeter/managed/pi/1.5.2/pi",
                "From WSL managed_config is not the Linux path: " + raw);

            // Without a place, the preferred copy; with one, that copy.
            var preferred = Call(server, "describe_dispatch", candidateArgs + ",\"workspace\":\"C:\\\\w\"}", out raw);
            Assert(Json.Str(preferred, "where") == "wsl", "Without a place the copy inside WSL, preferred by default, was not described: " + raw);
            Settings.Current.ManagedPiPrefer = "windows";
            preferred = Call(server, "describe_dispatch", candidateArgs + ",\"workspace\":\"C:\\\\w\"}", out raw);
            Assert(Json.Str(preferred, "where") == "windows", "Without a place the preferred copy on Windows was not described: " + raw);
            Settings.Current.ManagedPiPrefer = null;
            var onWindows = Call(server, "describe_dispatch", candidateArgs + ",\"where\":\"windows\",\"workspace\":\"C:\\\\w\"}", out raw);
            Assert(Json.Str(onWindows, "where") == "windows" && Json.Str(onWindows, "managed_config") == ManagedPi.Windows.ConfigFolder, "where: windows did not pick the copy on Windows: " + raw);
            var claim = Call(server, "claim_capacity", candidateArgs + ",\"where\":\"wsl\",\"distro\":\"" + Distro + "\",\"client\":\"claude-code\"}", out raw);
            answers.Add(raw);
            Assert(Json.Str(claim, "lease_id") != null, "No lease was granted for the copy inside WSL: " + raw);
            var tools = Items(Json.Obj(Json.Parse(server.Handle("{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/list\"}")), "result"), "tools");
            var claimSchema = Json.Obj(Json.Obj(tools.Single(t => Json.Str(t, "name") == "claim_capacity"), "inputSchema"), "properties");
            Assert(claimSchema.ContainsKey("where") && claimSchema.ContainsKey("distro"), "claim_capacity's schema does not let a caller pick the copy.");

            // WSL stops: the entry stays, unreachable, and the copy is not offered.
            Probe.WslPlacesUp = new WslPlace[0];
            var stopped = Call(server, "list_candidates", "{}", out raw);
            entry = ManagedEntry(stopped, "wsl");
            Assert(!Json.Bool(entry, "reachable") && !Json.Bool(entry, "installed") && ((IList)entry["services"]).Count == 0 && Json.Str(Json.Obj(Json.Obj(stopped, "runtimes"), "environment"), "wsl") == "stopped",
                "A stopped WSL's copy is not reported unreachable: " + raw);
            Assert(!Items(stopped, "candidates").Any(c => Json.Str(Json.Obj(c, "runtime"), "where") == "wsl"), "The copy inside a stopped WSL was offered.");

            // No distribution at all: no entry inside WSL, and WSL is "none".
            Settings.Current.WslDistro = "";
            var none = Call(server, "list_candidates", "{}", out raw);
            Assert(ManagedEntry(none, "wsl") == null && ManagedEntry(none, "windows") != null && Json.Str(Json.Obj(Json.Obj(none, "runtimes"), "environment"), "wsl") == "none"
                && !Json.Obj(Json.Obj(none, "runtimes"), "environment").ContainsKey("distro"), "Without a distribution runtimes.managed or runtimes.environment is wrong: " + raw);

            string keys = File.ReadAllText(KeyFileOf(w.Copy));
            foreach (var answer in answers)
                Assert(!answer.Contains(RouterKey) && !answer.Contains(GeminiKey) && !answer.Contains(keys) && !answer.Contains("api_key"), "An answer reveals a key or the key file.");
        }
    }
}
