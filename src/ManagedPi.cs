using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace AIMeter
{
    /// <summary>
    /// The copies of Pi that AI Meter downloads and keeps for delegation, when the user asks for them: Pi's official
    /// standalone builds, one pinned release, for users with no Pi of their own. A copy may live in two places, each
    /// installed and removed on its own (Copy):
    /// - on Windows (ADR 0004), under the folder other programs can read and that stays on this PC
    ///   (Settings.LocalSharedFolder\managed\pi), for tasks on Windows folders started from Windows;
    /// - inside WSL (ADR 0005), in the Linux home of the distribution AI Meter is set to (~/.aimeter/managed/pi), which
    ///   AI Meter reads and writes through the distribution's file share and reaches only while that distribution runs.
    ///
    /// Layout under a copy's root, outside PATH:
    ///   {version}\pi.exe or {version}/pi   the unpacked release, moved into place only when complete;
    ///   config\                            the copy's own configuration (PI_CODING_AGENT_DIR), so it never reads or
    ///                                      writes the user's ~/.pi; auth.json there holds the keys the user chose to
    ///                                      copy, keys-revision the count of its changes. Inside WSL the folder has
    ///                                      mode 700, so other Linux users cannot read the keys;
    ///   models\{version}\                  Pi's own model lists, which the standalone build keeps inside its program.
    ///
    /// Rules this class keeps:
    /// - Nothing is downloaded, updated or removed except by Install and Remove, which only Settings calls.
    /// - The archive must match the SHA-256 written here; anything else is deleted before a byte of it is unpacked.
    /// - Inside WSL the verified bytes are unpacked by the distribution's own tar, the one program AI Meter starts
    ///   there, and only during an installation (and chmod on the configuration folder just before it). A
    ///   distribution that is not running is never started: the copy there is then out of reach, neither installed,
    ///   synced nor removed, and its key file is left as it is.
    /// - A key reaches a copy's auth.json only for a service the user ticked (Settings.ManagedPiServices, one list for
    ///   every copy), and leaves it when the service is unticked or the copy removed. The file is plain text, as in any
    ///   Pi installation.
    /// - No key is returned to a caller outside AI Meter: KeyFor serves the runtime scan, which turns it into an opaque
    ///   credential id.
    /// </summary>
    internal static class ManagedPi
    {
        /// <summary>
        /// The agent definition (agents/pi.json) the copies belong to, and their dispatch places there: Place for the
        /// copy on Windows, PlaceInWsl for the copy inside WSL.
        /// </summary>
        public const string RuntimeId = "pi";
        public const string Place = "managed";
        public const string PlaceInWsl = "managed_wsl";

        /// <summary>
        /// Whether the Microsoft Store build offers the managed copies; GitHub builds always do. False until the Store
        /// listing discloses the optional downloads (ADR 0004 decision 7, ADR 0005 decision 6, docs/STORE.md): a
        /// package is submitted automatically from a release tag, and must not reach certification ahead of its
        /// description. It is also the fallback should certification refuse the download.
        /// </summary>
        private static readonly bool OfferedInStoreBuild = false;

        /// <summary>Values of Release.ArchiveKind.</summary>
        public const string Zip = "zip", TarGz = "tar.gz";

        /// <summary>One archive of Pi's standalone build: where it is published, what it must be and how it unpacks.</summary>
        internal sealed class Release
        {
            public string Version, Url, Sha256;
            /// <summary>The archive's size in bytes, shown before the download and enforced during it.</summary>
            public long ArchiveBytes;
            /// <summary>
            /// Zip: unpacked by AI Meter, entries at the top. TarGz: unpacked inside WSL by the distribution's tar, with
            /// its one top folder stripped, so that the files keep their permissions.
            /// </summary>
            public string ArchiveKind;
            /// <summary>The program inside the unpacked folder.</summary>
            public string ProgramName;
        }

        /// <summary>
        /// The release AI Meter adopts, in both its builds. A newer one is adopted only by changing these lines, in a new
        /// AI Meter version, after the dispatch of both copies has been verified: Pi does not promise a stable event
        /// format.
        /// </summary>
        public const string Version = "1.5.2";

        /// <summary>The Windows build: the digest GitHub and the release's own SHA256SUMS state for pi-windows-x64.zip (checked on 2026-11-19).</summary>
        internal static readonly Release PinnedWindows = new Release
        {
            Version = Version,
            Url = "https://github.com/earendil-works/pi/releases/download/v1.5.2/pi-windows-x64.zip",
            Sha256 = "e6adb8cb8c84eca14391ad24b363de9d2be98a89ca3ba3fdee1fa4de32b7f2e8",
            ArchiveBytes = 61940631,
            ArchiveKind = Zip,
            ProgramName = "pi.exe",
        };

        /// <summary>
        /// The Linux build for the copy inside WSL: pi-linux-x64.tar.gz, one top folder pi/ with the program pi/pi and
        /// no links, 125 MB unpacked (checked and unpacked live on 2026-11-24).
        /// </summary>
        internal static readonly Release PinnedLinux = new Release
        {
            Version = Version,
            Url = "https://github.com/earendil-works/pi/releases/download/v1.5.2/pi-linux-x64.tar.gz",
            Sha256 = "811c6f2c9e7aaee252b5404252e556fb713aa209c1f7f924226aa77d4ef70cb5",
            ArchiveBytes = 58922566,
            ArchiveKind = TarGz,
            ProgramName = "pi",
        };

        /// <summary>Where the release is published, for Settings to name and link.</summary>
        public const string ProjectPage = "https://github.com/earendil-works/pi";

        /// <summary>A zip archive may unpack to at most this much: the 1.5.2 Windows build is 131 MB.</summary>
        private const long UnpackedLimitBytes = 512L * 1024 * 1024;
        private const string PartialSuffix = ".partial";

        /// <summary>A failed installation or removal, with a sentence Settings can show.</summary>
        internal sealed class InstallException : Exception
        {
            public InstallException(string message, Exception inner = null) : base(message, inner) { }
        }

        public static bool Offered { get { return !Program.Demo && (OfferedInStoreBuild || !Native.IsPackaged); } }

        // ------------------------------------------------------------ the copies

        /// <summary>The copy on Windows (ADR 0004): always reachable.</summary>
        public static Copy Windows { get { return new Copy(null); } }

        /// <summary>The copy inside a WSL distribution, its root under the place's home as Windows sees it.</summary>
        internal static Copy InWsl(WslPlace place) { return new Copy(place); }

        /// <summary>
        /// The copies AI Meter can reach among the given running distributions: the one on Windows, then the one inside
        /// the distribution AI Meter is set to (Settings.WslDistro) when that distribution is among them.
        /// </summary>
        internal static List<Copy> Reachable(IEnumerable<WslPlace> running)
        {
            var copies = new List<Copy> { Windows };
            var place = ConfiguredAmong(running);
            if (place != null) copies.Add(InWsl(place));
            return copies;
        }

        /// <summary>The copies AI Meter can reach now, as of the last check of WSL (Probe.WslPlacesUp).</summary>
        public static List<Copy> Reachable() { return Reachable(Probe.WslPlacesUp); }

        /// <summary>The copy in a place ("windows" or "wsl") when it can be reached now; null otherwise.</summary>
        public static Copy ReachableIn(string where) { return Reachable().FirstOrDefault(c => c.Where == where); }

        /// <summary>The distribution AI Meter is set to, when it is among the running places; null otherwise.</summary>
        private static WslPlace ConfiguredAmong(IEnumerable<WslPlace> running)
        {
            string distro = Settings.Current == null ? null : Settings.Current.WslDistro;
            if (string.IsNullOrEmpty(distro) || running == null) return null;
            return running.FirstOrDefault(p => p != null && p.Home != null && string.Equals(p.Distro, distro, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// The state of WSL as AI Meter last saw it, without starting anything: "running" when the distribution it is
        /// set to runs, "stopped" when it does not, "none" when no distribution is registered.
        /// </summary>
        public static string WslState
        {
            get
            {
                string distro = Settings.Current == null ? null : Settings.Current.WslDistro;
                if (string.IsNullOrEmpty(distro)) return "none";
                return ConfiguredAmong(Probe.WslPlacesUp) != null ? "running" : "stopped";
            }
        }

        /// <summary>Whether Git for Windows is found, by the search BindShell uses; nothing is written.</summary>
        public static bool GitForWindowsFound { get { return LocateGitBash() != null; } }

        /// <summary>Bring every copy AI Meter can reach now in step with the user's choices; see Copy.Sync. False when one failed.</summary>
        public static bool Sync(Settings settings)
        {
            bool synced = true;
            foreach (var copy in Reachable()) synced &= copy.Sync(settings);
            return synced;
        }

        /// <summary>
        /// One managed copy: where it lives, its release, its folders as AI Meter reads and writes them (Root and those
        /// under it: Windows paths, through the distribution's file share for the copy inside WSL) and as the agent
        /// sees them (AgentPath: Linux paths inside WSL, the same paths on Windows). A Copy is a view of the folders,
        /// made anew each time it is asked for; it keeps no state of its own.
        /// </summary>
        internal sealed class Copy
        {
            /// <summary>"windows" or "wsl".</summary>
            public readonly string Where;
            /// <summary>The WSL distribution; null on Windows.</summary>
            public readonly string Distro;
            /// <summary>The running distribution's home, as Windows sees it (\\wsl.localhost\Distro\home\user); null on Windows.</summary>
            private readonly WslPlace place;

            internal Copy(WslPlace place)
            {
                this.place = place;
                Where = place == null ? "windows" : "wsl";
                Distro = place == null ? null : place.Distro;
            }

            public bool InsideWsl { get { return place != null; } }

            /// <summary>The release this copy installs: the Windows or the Linux build.</summary>
            public Release Release { get { return InsideWsl ? PinnedLinux : PinnedWindows; } }

            /// <summary>The copy's dispatch place in agents/pi.json.</summary>
            public string PlaceKey { get { return InsideWsl ? PlaceInWsl : Place; } }

            public string Root
            {
                get { return InsideWsl ? Path.Combine(place.Home, ".aimeter", "managed", "pi") : Path.Combine(Settings.LocalSharedFolder, "managed", "pi"); }
            }

            public string ConfigFolder { get { return Path.Combine(Root, "config"); } }
            public string ProgramPath { get { return ProgramIn(Release); } }
            private string KeyFile { get { return Path.Combine(ConfigFolder, "auth.json"); } }
            private string FolderOf(Release release) { return Path.Combine(Root, release.Version); }
            private string ProgramIn(Release release) { return Path.Combine(FolderOf(release), release.ProgramName); }

            /// <summary>
            /// A path under the copy's root as the agent sees it: the Linux path inside WSL (null if the path is not in
            /// the distribution), the same path on Windows.
            /// </summary>
            public string AgentPath(string path) { return InsideWsl ? Candidates.LinuxPath(path, place.Home) : path; }

            /// <summary>The Linux home the copy inside WSL runs in; null on Windows.</summary>
            public string LinuxHome { get { return InsideWsl ? Candidates.LinuxPath(place.Home, place.Home) : null; } }

            /// <summary>
            /// Whether the copy can be reached now: always on Windows; inside WSL while its distribution runs. Only
            /// Install and Remove ask, as a Copy inside WSL is otherwise made only from a running place.
            /// </summary>
            private bool IsReachable
            {
                get { return !InsideWsl || Probe.WslPlacesUp.Any(p => p != null && string.Equals(p.Distro, Distro, StringComparison.OrdinalIgnoreCase)); }
            }

            /// <summary>Whether the adopted release is unpacked and complete. An older release left by a previous AI Meter does not count.</summary>
            public bool Installed
            {
                get
                {
                    try { return Offered && File.Exists(ProgramPath); }
                    catch (ArgumentException) { return false; }
                }
            }

            /// <summary>The version of another release still on disk, which the adopted one would replace; null when there is none.</summary>
            public string OtherVersionInstalled
            {
                get
                {
                    string program = Release.ProgramName;
                    return Probe.SafeDirs(Root).Select(Path.GetFileName)
                        .FirstOrDefault(n => n != Version && n != "config" && !n.EndsWith(PartialSuffix, StringComparison.Ordinal) && File.Exists(Path.Combine(Root, n, program)));
                }
            }

            /// <summary>
            /// What tells one state of the copy from another, for callers that cache what they derived from it: whether
            /// it is installed and when its keys were last written.
            /// </summary>
            public string Stamp
            {
                get
                {
                    if (!Installed) return "";
                    try { return File.Exists(KeyFile) ? File.GetLastWriteTimeUtc(KeyFile).Ticks.ToString() : "0"; }
                    catch (IOException) { return "0"; }
                    catch (UnauthorizedAccessException) { return "0"; }
                }
            }

            // -------------------------------------------------------- installation

            /// <summary>
            /// Download the adopted release, check it and unpack it. progress receives the bytes read so far; cancelled
            /// is asked between blocks. Throws InstallException with the reason; whatever was downloaded is deleted then.
            /// </summary>
            public void Install(Action<long> progress, Func<bool> cancelled) { Install(Release, OpenArchive, progress, cancelled); }

            /// <summary>Install with the release and the source given, so that tests need no network.</summary>
            internal void Install(Release release, Func<string, Stream> open, Action<long> progress, Func<bool> cancelled)
            {
                if (release.ArchiveKind != Release.ArchiveKind) throw new ArgumentException("A " + release.ArchiveKind + " archive cannot be installed " + (InsideWsl ? "inside WSL." : "on Windows."));
                // AI Meter never starts WSL, nor touches the file share of a distribution that is not running, which
                // would start it.
                if (!IsReachable) throw new InstallException(L.T("WSL is not running. Start it to install Pi there; AI Meter does not start it."));

                // Names no earlier attempt used, so nothing left behind or put there by another program is ever unpacked
                // or promoted: the archive file is created new and the staging folder must not exist yet. The archive is
                // always written on Windows, never inside WSL: under the root of the copy on Windows, or beside it for the
                // copy inside WSL, so that the bytes tar receives are read from a handle AI Meter holds alone.
                string attempt = Guid.NewGuid().ToString("N");
                string downloads = InsideWsl ? DownloadFolder : Root;
                string archive = Path.Combine(downloads, (InsideWsl ? "pi-linux-" : "") + release.Version + "-" + attempt + "." + release.ArchiveKind + PartialSuffix);
                string unpacked = FolderOf(release) + "-" + attempt + PartialSuffix;
                try
                {
                    Directory.CreateDirectory(Root);
                    foreach (var stale in Probe.SafeDirs(Root).Where(d => d.EndsWith(PartialSuffix, StringComparison.Ordinal))) TryDelete(stale);
                    foreach (var stale in Probe.SafeFiles(Root, "*" + PartialSuffix)) TryDeleteFile(stale.FullName);
                    if (InsideWsl)
                    {
                        Directory.CreateDirectory(downloads);
                        foreach (var stale in Probe.SafeFiles(downloads, "pi-linux-*" + PartialSuffix)) TryDeleteFile(stale.FullName);
                        // Private before any key can reach it.
                        MakeConfigFolderPrivate();
                    }

                    // One handle, held without sharing from the first byte written to the last one unpacked: what is
                    // unpacked is exactly what was hashed.
                    using (var file = new FileStream(archive, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
                    {
                        Download(release, open, file, progress, cancelled);
                        if (InsideWsl) UnpackInWsl(file, unpacked, cancelled);
                        else Unpack(file, unpacked, cancelled);
                    }
                    if (!File.Exists(Path.Combine(unpacked, release.ProgramName))) throw new InstallException(L.T("The downloaded archive does not contain Pi's program. Nothing was installed."));

                    // The complete folder takes its place in one step, so a copy is never seen half unpacked. Inside WSL
                    // the step is a rename through the file share, within one folder.
                    if (Directory.Exists(FolderOf(release))) Directory.Delete(FolderOf(release), true);
                    Directory.Move(unpacked, FolderOf(release));
                    foreach (var other in Probe.SafeDirs(Root).Where(d => !SamePath(d, FolderOf(release)) && !SamePath(d, ConfigFolder))) TryDelete(other);
                    lock (ConfigLock)
                    {
                        if (!InsideWsl) BindShell();
                        WriteModelLists();
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch (InstallException) { throw; }
                catch (WebException ex) { throw new InstallException(L.T("The download from GitHub failed. Check the connection and try again."), ex); }
                catch (IOException ex) { throw new InstallException(ex.Message, ex); }
                catch (UnauthorizedAccessException ex) { throw new InstallException(ex.Message, ex); }
                catch (InvalidDataException ex) { throw new InstallException(L.T("The downloaded archive could not be read. Nothing was installed."), ex); }
                finally
                {
                    TryDeleteFile(archive);
                    TryDelete(unpacked);
                }
            }

            /// <summary>
            /// Create the configuration folder of the copy inside WSL through the file share, where it belongs to the
            /// Linux user, and give it mode 700, so that the keys copied there later are that user's alone. A folder
            /// that cannot be made private fails the installation before anything is downloaded.
            /// </summary>
            private void MakeConfigFolderPrivate()
            {
                Directory.CreateDirectory(ConfigFolder);
                string linux = AgentPath(ConfigFolder);
                if (linux == null || RunWsl(new[] { "-d", Distro, "-e", "chmod", "700", linux }, null) != 0)
                    throw new InstallException(L.T("Pi's settings folder inside WSL could not be made private. Nothing was installed."));
            }

            /// <summary>
            /// Unpack the verified archive inside WSL: the distribution's tar reads it, through the handle it was
            /// written with, on its standard input and writes into a staging folder that must not exist yet, created
            /// through the file share. tar keeps the files' permissions, so the program stays executable; it strips the
            /// archive's one top folder and gives every file to the user it runs as.
            /// </summary>
            private void UnpackInWsl(FileStream archive, string folder, Func<bool> cancelled)
            {
                if (Directory.Exists(folder) || File.Exists(folder)) throw new IOException("The staging folder already exists: " + folder);
                Directory.CreateDirectory(folder);
                string target = AgentPath(folder);
                if (target == null) throw new IOException("The staging folder is not inside the distribution: " + folder);
                if (cancelled != null && cancelled()) throw new OperationCanceledException();
                archive.Position = 0;
                if (RunWsl(new[] { "-d", Distro, "-e", "tar", "-xz", "-C", target, "--strip-components=1", "--no-same-owner" }, archive) != 0)
                    throw new InstallException(L.T("Pi could not be unpacked inside WSL. Nothing was installed."));
            }

            /// <summary>
            /// Remove the copy with its configuration and the keys copied into it. The count of the key file's changes
            /// is the one file kept, raised like for any other change: a task that is ending while Pi is removed, or that
            /// outlives a removal and a new installation, then sees that the keys changed and never trusts a count it
            /// had seen before. The ticked services are forgotten when no other copy AI Meter can reach stays
            /// installed: with one left, they are its keys still.
            /// Throws InstallException when a file is in use (a task still running) or the copy cannot be reached. The
            /// keys go first and nothing else is touched until they are gone: when the program cannot be deleted, the
            /// keys already are; when the key file itself cannot, the copy and the choices stay as they were, for the
            /// user to try again.
            /// </summary>
            public void Remove(Settings settings)
            {
                if (!IsReachable) throw new InstallException(L.T("WSL is not running. Start it to remove Pi from there; AI Meter does not start it."));
                try
                {
                    // Under the lock Sync writes with: a sync already under way ends first, and one that comes after
                    // finds no choices or no copy, so it cannot bring a key back here.
                    bool choicesSaved = true;
                    lock (ConfigLock)
                    {
                        if (Directory.Exists(ConfigFolder)) AnnounceChange();
                        DeleteKeyFiles();
                        if (settings != null && !Reachable().Any(c => c.Where != Where && c.Installed))
                        {
                            settings.ManagedPiServices = new List<string>();
                            choicesSaved = settings.Save();
                        }
                        foreach (var folder in Probe.SafeDirs(Root).Where(d => !SamePath(d, ConfigFolder))) Directory.Delete(folder, true);
                        foreach (var file in Probe.SafeFiles(Root, "*")) file.Delete();
                        if (Directory.Exists(ConfigFolder))
                        {
                            foreach (var folder in Probe.SafeDirs(ConfigFolder)) Directory.Delete(folder, true);
                            foreach (var file in Probe.SafeFiles(ConfigFolder, "*").Where(f => !SamePath(f.FullName, RevisionFile))) file.Delete();
                            WriteRevision(Revision() + 1);
                        }
                    }
                    // The copy and its keys are gone, but the old choices are still on disk: say so. A later installation
                    // starts with nothing ticked in any case (Controller.InstallManagedPi).
                    if (!choicesSaved) throw new InstallException(L.T("This choice could not be saved, so it will not last past a restart. Check AI Meter's settings file, then change it again."));
                }
                catch (IOException ex) { throw new InstallException(L.T("Pi could not be removed: one of its files is in use. Wait for delegated tasks to end, then try again."), ex); }
                catch (UnauthorizedAccessException ex) { throw new InstallException(L.T("Pi could not be removed: one of its files is in use. Wait for delegated tasks to end, then try again."), ex); }
            }

            // -------------------------------------------------------- keys

            /// <summary>
            /// Bring the copy's configuration in step with the user's choices and this PC: its key file holds one entry
            /// for each ticked service that still has a key, nothing else, and on Windows its settings name the Git Bash
            /// found now (see BindShell). Called when a choice or a key changes, after an installation and at start, so
            /// a key the user replaced or removed in AI Meter does not live on here. Without an installed copy, or with
            /// nothing ticked, no key file is left, nor a half-written one. Returns false when a key file could not be
            /// written or deleted: the caller tells the user, and the next call tries again.
            /// </summary>
            public bool Sync(Settings settings)
            {
                // With sample data the real copy is neither shown nor used, and its keys are left as they are.
                if (settings == null || Program.Demo) return true;
                // One writer at a time: Settings calls this on the interface thread, a key change in the background.
                lock (ConfigLock)
                {
                    bool installed = Installed;
                    if (installed)
                    {
                        if (!InsideWsl) BindShell();
                        WriteModelLists();
                    }
                    return WriteKeys(installed ? settings.ManagedPiServices ?? new List<string>() : new List<string>());
                }
            }

            private string PendingKeyFile { get { return KeyFile + ".new"; } }

            private bool WriteKeys(IEnumerable<string> services)
            {
                try
                {
                    // Inside the error boundary: reading a saved key can fail like any other file.
                    // A ticked service keeps the entry it has while AI Meter finds no key for it at all: a key that was
                    // discovered in another app is out of sight whenever that app's files are (WSL not running, the first
                    // moments after a start), and that is not the user taking it back. A key found and different replaces
                    // it, one found and not plain removes it, and removing the key in AI Meter unticks the service.
                    var held = Json.ParseFile(KeyFile);
                    var entries = new Dictionary<string, object>();
                    foreach (var id in services)
                    {
                        var d = Definitions.Get(id);
                        string provider = ProviderOf(d, PlaceKey);
                        if (provider == null) continue;
                        object entry = SourceKey(d) == null && held != null && held.ContainsKey(provider) ? held[provider] : KeyEntry(d);
                        if (entry != null) entries[provider] = entry;
                    }
                    string text = entries.Count == 0 ? null : Json.Serialize(entries);
                    bool unchanged = text == null ? !File.Exists(KeyFile) : File.Exists(KeyFile) && File.ReadAllText(KeyFile, Encoding.UTF8) == text;
                    if (unchanged)
                    {
                        if (File.Exists(PendingKeyFile)) File.Delete(PendingKeyFile);
                        // A change whose end could not be recorded earlier is closed now.
                        if (Revision() % 2 != 0) WriteRevision(Revision() + 1);
                        return true;
                    }
                    // Inside WSL only the installation makes the configuration folder private (mode 700), as it alone
                    // starts a program there: keys are never written into a folder made by anything else.
                    if (InsideWsl && text != null && !Directory.Exists(ConfigFolder)) return false;
                    AnnounceChange();
                    if (text == null) DeleteKeyFiles();
                    else
                    {
                        try
                        {
                            File.WriteAllText(PendingKeyFile, text, new UTF8Encoding(false));
                            PutInPlace(PendingKeyFile, KeyFile);
                        }
                        finally
                        {
                            // A replacement that failed half-way must not leave a second copy of the keys.
                            if (File.Exists(PendingKeyFile)) File.Delete(PendingKeyFile);
                        }
                    }
                    WriteRevision(Revision() + 1);
                    return true;
                }
                catch (IOException) { return false; }
                catch (UnauthorizedAccessException) { return false; }
            }

            private string RevisionFile { get { return Path.Combine(ConfigFolder, "keys-revision"); } }

            /// <summary>
            /// The count of the key file's changes, kept in a file beside it that holds only that number (0 while there
            /// is no file). Every change raises it twice: to an odd number before the key file is touched, to the next
            /// even one once the change is complete. A plugin reads the keys before and after a task to remove them from
            /// what it returns, and keeps a task's output only when it started on an even count and ended on the same
            /// count or the next even one: anything else means that a key may have come and gone unseen, or that a
            /// change was under way (docs/MCP.md, "Managed Pi"). Announcing first matters: if the count cannot be
            /// written, the key file is not changed at all; if the key file or the closing count fails, the count stays
            /// odd, which no task trusts, until a later call finishes it. Each copy has its own count, beside its own key
            /// file, which describe_dispatch names (managed_config).
            /// </summary>
            private long Revision()
            {
                long revision = 0;
                if (File.Exists(RevisionFile)) long.TryParse(File.ReadAllText(RevisionFile, Encoding.UTF8).Trim(), out revision);
                return Math.Max(0, revision);
            }

            /// <summary>
            /// Raise the count to an odd number no task has seen, before the key file is touched. A count already odd is
            /// a change that did not finish: this one is another change, so it gets the next odd number, or the two
            /// would be counted as one.
            /// </summary>
            private void AnnounceChange()
            {
                long revision = Revision();
                WriteRevision(revision % 2 == 0 ? revision + 1 : revision + 2);
            }

            /// <summary>Write the count through a temporary file, so that a reader never sees it half written.</summary>
            private void WriteRevision(long revision)
            {
                Directory.CreateDirectory(ConfigFolder);
                string pending = RevisionFile + ".new";
                try
                {
                    File.WriteAllText(pending, revision.ToString(System.Globalization.CultureInfo.InvariantCulture), new UTF8Encoding(false));
                    PutInPlace(pending, RevisionFile);
                }
                finally
                {
                    if (File.Exists(pending)) File.Delete(pending);
                }
            }

            /// <summary>Delete the key file and a half-written one; throws when either cannot be deleted.</summary>
            private void DeleteKeyFiles()
            {
                if (File.Exists(PendingKeyFile)) File.Delete(PendingKeyFile);
                if (File.Exists(KeyFile)) File.Delete(KeyFile);
            }

            /// <summary>
            /// The key the copy may use for a service, with the provider id it is kept under; null when it holds none,
            /// the user has not ticked the service now, or AI Meter has a key for the service and it is not this one: a
            /// key that could not be deleted after a choice was withdrawn, or replaced after the user changed it, must
            /// not keep the service on offer. While AI Meter finds no key at all (see WriteKeys) the copy's own stands.
            /// For the runtime scan only, which reports an opaque credential id.
            /// </summary>
            internal string KeyFor(ServiceDefinition d, out string provider)
            {
                provider = ProviderOf(d, PlaceKey);
                var settings = Settings.Current;
                if (provider == null || settings == null || settings.ManagedPiServices == null || !settings.ManagedPiServices.Contains(d.Id)) return null;
                string key = JPath.Str(JPath.Get(Json.ParseFile(KeyFile), provider + ".key"));
                if (string.IsNullOrEmpty(key)) return null;
                return SourceKey(d) != null && key != PlainKey(d) ? null : key;
            }

            // -------------------------------------------------------- model lists

            private string ModelsFolder { get { return Path.Combine(Root, "models", Version); } }

            /// <summary>
            /// Pi's own model list for a provider, as the pinned release has it, at a path AI Meter can read; null when
            /// there is none. The standalone build keeps these lists inside its program, where AI Meter cannot read
            /// them, so AI Meter carries the same files (agents\pi-models, taken from the pi-ai package of the pinned
            /// release, MIT) and writes them beside the copy. Delegation needs them for what a service's own list does
            /// not say: whether a model can call tools (NVIDIA) and what Pi prices a request at, which is how a free
            /// tier's budget is kept (Cloudflare).
            /// </summary>
            public string ModelsFile(string provider)
            {
                if (string.IsNullOrEmpty(provider)) return null;
                try
                {
                    string file = Path.Combine(ModelsFolder, provider + ".json");
                    return File.Exists(file) ? file : null;
                }
                catch (ArgumentException) { return null; }
            }

            /// <summary>Write the lists of the providers the copy has, where they are missing or differ; failures are tried again at the next sync.</summary>
            private void WriteModelLists()
            {
                var kind = Agents.Get(RuntimeId);
                var dispatchPlace = kind == null ? null : kind.PlaceFor(PlaceKey);
                if (dispatchPlace == null) return;
                try
                {
                    foreach (var provider in dispatchPlace.Providers)
                    {
                        string text = ModelListOf(provider);
                        if (text == null) continue;
                        string file = Path.Combine(ModelsFolder, provider + ".json");
                        if (File.Exists(file) && new FileInfo(file).Length == Encoding.UTF8.GetByteCount(text)) continue;
                        Directory.CreateDirectory(ModelsFolder);
                        File.WriteAllText(file, text, new UTF8Encoding(false));
                    }
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
                catch (ArgumentException) { }
            }

            // -------------------------------------------------------- shell (the copy on Windows only)

            private string SettingsFile { get { return Path.Combine(ConfigFolder, "settings.json"); } }

            /// <summary>
            /// Name the Git Bash found on this PC as the Windows copy's shell (Pi's shellPath setting). With its bash
            /// tool, Pi otherwise searches for a bash.exe each time it runs a command, and can find WSL's launcher; with
            /// shellPath set it uses that file only, and fails when the file is gone (Pi 1.5.2, utils/shell.ts; seen
            /// live on 2026-11-20). A binding once written is never taken back, only replaced: should Git Bash
            /// disappear, a task already described with the bash tool then fails at its first command instead of
            /// searching, and new tasks are not offered that tool (BoundShells). The copy's settings file holds nothing
            /// else and is AI Meter's to write. The copy inside WSL has the distribution's own bash and needs none of it.
            /// </summary>
            private void BindShell()
            {
                string bash = LocateGitBash();
                try
                {
                    if (bash == null) return;
                    string text = Json.Serialize(new Dictionary<string, object> { { "shellPath", bash } });
                    if (File.Exists(SettingsFile) && File.ReadAllText(SettingsFile, Encoding.UTF8) == text) return;
                    Directory.CreateDirectory(ConfigFolder);
                    File.WriteAllText(SettingsFile, text, new UTF8Encoding(false));
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }

            /// <summary>
            /// The shells the Windows copy is bound to now, besides the PowerShell tool it always has: GitBashShell while
            /// its settings name a bash.exe that exists. Only then may a plugin give it the bash tool. None inside WSL,
            /// where bash is the copy's own tool.
            /// </summary>
            public string[] BoundShells
            {
                get
                {
                    if (InsideWsl) return new string[0];
                    string bash = Json.Str(Json.ParseFile(SettingsFile), "shellPath");
                    try { return !string.IsNullOrEmpty(bash) && File.Exists(bash) ? new[] { GitBashShell } : new string[0]; }
                    catch (ArgumentException) { return new string[0]; }
                }
            }
        }

        /// <summary>
        /// Where the archive for the copy inside WSL is downloaded and verified: on Windows, beside the Windows copy's
        /// root, never inside WSL.
        /// </summary>
        private static string DownloadFolder { get { return Path.Combine(Settings.LocalSharedFolder, "managed"); } }

        /// <summary>
        /// Write the archive to the open file while hashing it, and go on only if its SHA-256 is the release's. A source
        /// that sends more than the release's size is cut off: the file could not match.
        /// </summary>
        private static void Download(Release release, Func<string, Stream> open, FileStream file, Action<long> progress, Func<bool> cancelled)
        {
            string digest;
            using (var sha = SHA256.Create())
            using (var source = open(release.Url))
            {
                var block = new byte[81920];
                long total = 0;
                int read;
                while ((read = source.Read(block, 0, block.Length)) > 0)
                {
                    if (cancelled != null && cancelled()) throw new OperationCanceledException();
                    total += read;
                    if (total > release.ArchiveBytes) throw new InstallException(L.T("The downloaded file is not the expected Pi release. It was deleted and nothing was installed."));
                    sha.TransformBlock(block, 0, read, null, 0);
                    file.Write(block, 0, read);
                    if (progress != null) progress(total);
                }
                sha.TransformFinalBlock(block, 0, 0);
                digest = BitConverter.ToString(sha.Hash).Replace("-", "");
            }
            file.Flush();
            if (!string.Equals(digest, release.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InstallException(L.T("The downloaded file is not the expected Pi release. It was deleted and nothing was installed."));
        }

        /// <summary>
        /// Unpack the verified zip archive, read back through the handle it was written with, into a folder that must
        /// not exist yet. Every entry must land inside that folder, whatever its name says, and the whole may not exceed
        /// UnpackedLimitBytes: the archive is verified, but a wrong pin should fail here rather than write outside or
        /// fill the disk.
        /// </summary>
        private static void Unpack(FileStream archive, string folder, Func<bool> cancelled)
        {
            if (Directory.Exists(folder) || File.Exists(folder)) throw new IOException("The staging folder already exists: " + folder);
            Directory.CreateDirectory(folder);
            string root = Path.GetFullPath(folder) + Path.DirectorySeparatorChar;
            archive.Position = 0;
            using (var zip = new ZipArchive(archive, ZipArchiveMode.Read, true))
            {
                if (zip.Entries.Sum(e => e.Length) > UnpackedLimitBytes) throw new InvalidDataException("The archive unpacks to more than expected.");
                foreach (var entry in zip.Entries)
                {
                    if (cancelled != null && cancelled()) throw new OperationCanceledException();
                    string target = Path.GetFullPath(Path.Combine(folder, entry.FullName));
                    if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("An entry points outside the archive.");
                    if (entry.FullName.EndsWith("/", StringComparison.Ordinal) || entry.FullName.EndsWith("\\", StringComparison.Ordinal)) { Directory.CreateDirectory(target); continue; }
                    Directory.CreateDirectory(Path.GetDirectoryName(target));
                    entry.ExtractToFile(target, true);
                }
            }
        }

        /// <summary>The plain request: no cookies, credentials or identifiers. GitHub redirects to its release storage.</summary>
        private static Stream OpenArchive(string url)
        {
            var request = (HttpWebRequest)WebRequest.Create(url);
            request.UserAgent = "ai-meter";
            request.Timeout = 30000;
            request.ReadWriteTimeout = 30000;
            return request.GetResponse().GetResponseStream();
        }

        // ------------------------------------------------------------ keys

        /// <summary>
        /// The provider id a copy knows a service by: the first of the service's aliases (auth.ids) among the providers
        /// the standalone build has built in (the "providers" of the copy's place in agents/pi.json; the copy on Windows
        /// by default). Null when Pi has no such provider, and so cannot call the service with a key alone.
        /// </summary>
        public static string ProviderOf(ServiceDefinition d, string placeKey = Place)
        {
            var kind = Agents.Get(RuntimeId);
            var dispatchPlace = kind == null ? null : kind.PlaceFor(placeKey);
            if (dispatchPlace == null || d == null || !d.UsesKey) return null;
            return ServiceDefinition.Strings(d.Auth, "ids").DefaultIfEmpty(d.Id).FirstOrDefault(id => dispatchPlace.Providers.Contains(id, StringComparer.Ordinal));
        }

        /// <summary>
        /// The services whose key can be copied: those Pi has a provider for and AI Meter reads with a key it has now,
        /// together with whatever else the service needs beside its key.
        /// </summary>
        public static List<ServiceDefinition> ServicesWithKey()
        {
            return Definitions.All.Where(d => ProviderOf(d) != null && KeyEntry(d) != null).ToList();
        }

        /// <summary>
        /// A service's entry in a copy's key file: its key and, for a service that needs more than a key, those values
        /// under "env", by the variable name Pi reads them from (Pi 1.5.2, docs/providers.md: Cloudflare's account id
        /// as CLOUDFLARE_ACCOUNT_ID). The values are the ones AI Meter reads the service with; their variable is the
        /// first of the names the service's definition gives the value ("keys", "env"). Null when the key or one of
        /// those values is missing or is not a plain value, as Pi could not call the service then.
        /// </summary>
        private static Dictionary<string, object> KeyEntry(ServiceDefinition d)
        {
            string key = PlainKey(d);
            if (key == null) return null;
            var entry = new Dictionary<string, object> { { "type", "api_key" }, { "key", key } };
            var beside = new Dictionary<string, object>();
            var slots = Json.Obj(d.Raw, "keys");
            if (slots != null)
                foreach (var slot in slots.Keys.OrderBy(k => k, StringComparer.Ordinal))
                {
                    string variable = ServiceDefinition.Strings(Json.Obj(slots, slot), "env").FirstOrDefault();
                    if (variable == null) continue;
                    string value = DefinedProvider.SlotValueOf(d, slot);
                    if (!IsPlain(value, 1)) return null;
                    beside[variable] = value;
                }
            if (beside.Count > 0) entry["env"] = beside;
            return entry;
        }

        /// <summary>
        /// The key AI Meter itself reads a service with: one in this process's environment, the one the user saved, or
        /// one discovered in another app, in ProviderDefined's order.
        /// </summary>
        private static string SourceKey(ServiceDefinition d)
        {
            foreach (var name in ServiceDefinition.Strings(d.Auth, "env"))
            {
                string v = ServiceDefinition.Env(name);
                if (!string.IsNullOrEmpty(v)) return v.Trim();
            }
            string saved = ApiKeys.Get(d.Id);
            if (saved != null) return saved;
            var found = KeyFinder.Find(d);
            return found == null ? null : found.Key;
        }

        /// <summary>
        /// The key to copy for a service, or null when there is none or it is not a plain key. Pi does not take every
        /// value in its key file literally: one that starts with "!" is a command it runs to get the key, and one that
        /// names an environment variable is read from there (Pi 1.5.2, docs/providers.md). Only a value that can be
        /// nothing but a key is copied: no "!" at its start, no "$" anywhere (Pi fills in ${NAME} inside a value too), no
        /// white space or control character, eight characters or more.
        /// </summary>
        private static string PlainKey(ServiceDefinition d)
        {
            string key = SourceKey(d);
            return IsPlain(key, 8) ? key : null;
        }

        /// <summary>Whether Pi would take a value as it is written: see PlainKey.</summary>
        private static bool IsPlain(string value, int minimumLength)
        {
            if (value == null || value.Length < minimumLength || value[0] == '!') return false;
            return !value.Any(c => c == '$' || char.IsWhiteSpace(c) || char.IsControl(c));
        }

        /// <summary>One writer at a time, for every copy: Settings syncs on the interface thread, a key change in the background.</summary>
        private static readonly object ConfigLock = new object();

        // ------------------------------------------------------------ model lists

        /// <summary>The format of the copies' model lists, as AgentModels names it.</summary>
        public const string ModelsFormat = "pi";

        /// <summary>A provider's list as AI Meter carries it: embedded in the program, or in agents\pi-models when running from source.</summary>
        private static string ModelListOf(string provider)
        {
            using (var stream = typeof(ManagedPi).Assembly.GetManifestResourceStream("pimodels." + provider + ".json"))
                if (stream != null)
                    using (var reader = new StreamReader(stream, Encoding.UTF8)) return reader.ReadToEnd();
            try
            {
                string file = Path.Combine(Program.Home, "agents", "pi-models", provider + ".json");
                return File.Exists(file) ? File.ReadAllText(file, Encoding.UTF8) : null;
            }
            catch (IOException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
            catch (ArgumentException) { return null; }
        }

        // ------------------------------------------------------------ shell

        /// <summary>Where Git for Windows, which brings Git Bash, is published: for Settings to open.</summary>
        public const string GitForWindowsPage = "https://git-scm.com/download/win";

        /// <summary>The name, in agents/pi.json's alternatives and in describe_dispatch, of the shell BindShell binds.</summary>
        public const string GitBashShell = "git_bash";

        /// <summary>How BindShell looks for Git Bash; tests replace it, so that they do not depend on what this PC has.</summary>
        internal static Func<string> LocateGitBash = () => FindGitBash(Environment.GetEnvironmentVariable, File.Exists);

        /// <summary>
        /// The bash.exe of Git for Windows, or null: in the usual installation folders, then beside a git.exe on PATH
        /// (Git puts …\Git\cmd there, and bash.exe is in …\Git\bin). A file under the Windows folder is never
        /// returned: WSL's bash.exe launcher is there.
        /// </summary>
        internal static string FindGitBash(Func<string, string> variable, Func<string, bool> exists)
        {
            var places = new List<string>();
            foreach (var name in new[] { "ProgramFiles", "ProgramW6432", "ProgramFiles(x86)" })
                if (!string.IsNullOrEmpty(variable(name))) places.Add(Path.Combine(variable(name), @"Git\bin"));
            if (!string.IsNullOrEmpty(variable("LOCALAPPDATA"))) places.Add(Path.Combine(variable("LOCALAPPDATA"), @"Programs\Git\bin"));
            foreach (var entry in (variable("PATH") ?? "").Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string folder = entry.Trim().Trim('"');
                try { if (folder.Length > 0 && exists(Path.Combine(folder, "git.exe"))) places.Add(Path.GetFullPath(Path.Combine(folder, @"..\bin"))); }
                catch (ArgumentException) { }   // A malformed PATH entry.
                catch (NotSupportedException) { }
            }
            string windows = (variable("SystemRoot") ?? @"C:\Windows").TrimEnd('\\') + "\\";
            foreach (var bashFolder in places)
            {
                string bash = Path.Combine(bashFolder, "bash.exe");
                if (!bash.StartsWith(windows, StringComparison.OrdinalIgnoreCase) && exists(bash)) return bash;
            }
            return null;
        }

        // ------------------------------------------------------------ programs inside WSL

        /// <summary>
        /// How AI Meter starts wsl.exe, the one way it runs anything inside WSL: with the argument list given (never a
        /// shell command line), the stream's remaining bytes written to its standard input when one is given, and the
        /// exit code returned (-1 when wsl.exe could not be started or did not end in time). Only an installation the
        /// user asked for calls it (tar and chmod); tests replace it, so that no test starts WSL.
        /// </summary>
        /// <summary>
        /// Put a file written beside its target in the target's place. On Windows the target is replaced in one step.
        /// Through a WSL distribution's share that operation does not exist (seen live on 2026-11-24: File.Replace
        /// fails there), so the new content is copied over the target, which a reader may then see half written for an
        /// instant: the plugin reads the key file and its count with the revision rule, which withholds on anything
        /// it cannot parse.
        /// </summary>
        private static void PutInPlace(string pending, string target)
        {
            if (!File.Exists(target)) { File.Move(pending, target); return; }
            try { File.Replace(pending, target, null); }
            catch (IOException)
            {
                File.Copy(pending, target, true);
                File.Delete(pending);
            }
            catch (PlatformNotSupportedException)
            {
                File.Copy(pending, target, true);
                File.Delete(pending);
            }
        }

        internal static Func<IList<string>, Stream, int> RunWsl = RunWslProcess;

        /// <summary>How long one program inside WSL may run: unpacking the 125 MB build takes seconds.</summary>
        private const int WslTimeoutMs = 5 * 60 * 1000;

        private static int RunWslProcess(IList<string> arguments, Stream input)
        {
            var psi = new ProcessStartInfo("wsl.exe", CommandLine(arguments))
            {
                UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
                RedirectStandardInput = input != null, RedirectStandardOutput = true, RedirectStandardError = true,
            };
            try
            {
                using (var process = Process.Start(psi))
                {
                    // Both outputs are drained, so that a program that writes much never blocks on a full pipe.
                    process.StandardOutput.ReadToEndAsync();
                    process.StandardError.ReadToEndAsync();
                    if (input != null)
                    {
                        try { input.CopyTo(process.StandardInput.BaseStream); }
                        catch (IOException) { }   // The program ended before reading everything: its exit code says why.
                        finally
                        {
                            try { process.StandardInput.Close(); }
                            catch (IOException) { }
                        }
                    }
                    if (!process.WaitForExit(WslTimeoutMs))
                    {
                        try { process.Kill(); }
                        catch (InvalidOperationException) { }
                        catch (System.ComponentModel.Win32Exception) { }
                        return -1;
                    }
                    return process.ExitCode;
                }
            }
            catch (System.ComponentModel.Win32Exception) { return -1; }
            catch (InvalidOperationException) { return -1; }
        }

        /// <summary>
        /// A command line that gives a program exactly these arguments, by the rules Windows programs split their
        /// command line with (CommandLineToArgvW): an argument with white space or quotes, or an empty one, is quoted,
        /// a quote inside it is escaped, and backslashes are doubled only where they come before a quote.
        /// </summary>
        internal static string CommandLine(IEnumerable<string> arguments)
        {
            return string.Join(" ", arguments.Select(QuoteArgument));
        }

        private static string QuoteArgument(string argument)
        {
            if (argument.Length > 0 && argument.IndexOfAny(new[] { ' ', '\t', '\n', '\v', '"' }) < 0) return argument;
            var quoted = new StringBuilder("\"");
            int backslashes = 0;
            foreach (char c in argument)
            {
                if (c == '\\') { backslashes++; continue; }
                // Backslashes before a quote are doubled, and the quote escaped; elsewhere they stand as they are.
                quoted.Append('\\', c == '"' ? backslashes * 2 + 1 : backslashes);
                backslashes = 0;
                quoted.Append(c);
            }
            // Before the closing quote, backslashes are doubled so that they do not escape it.
            quoted.Append('\\', backslashes * 2);
            return quoted.Append('"').ToString();
        }

        // ------------------------------------------------------------ files

        private static bool SamePath(string a, string b) { return string.Equals(Path.GetFullPath(a).TrimEnd('\\'), Path.GetFullPath(b).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase); }

        private static void TryDelete(string folder)
        {
            try { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        private static void TryDeleteFile(string file)
        {
            try { File.Delete(file); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
