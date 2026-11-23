using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace AIMeter
{
    /// <summary>
    /// The copy of Pi that AI Meter downloads and keeps for delegation, when the user asks for it (ADR 0004): Pi's
    /// official standalone build for Windows, one pinned release, for tasks on Windows folders where the user has no
    /// Pi of their own.
    ///
    /// Layout, under the folder other programs can read and that stays on this PC (Settings.LocalSharedFolder), outside PATH:
    ///   managed\pi\{version}\pi.exe   the unpacked release, moved into place only when complete;
    ///   managed\pi\config\            the copy's own configuration (PI_CODING_AGENT_DIR), so it never reads or writes
    ///                                 the user's ~/.pi; auth.json there holds the keys the user chose to copy.
    ///
    /// Rules this class keeps:
    /// - Nothing is downloaded, updated or removed except by Install and Remove, which only Settings calls.
    /// - The archive must match the SHA-256 written here; anything else is deleted before a byte of it is unpacked.
    /// - A key reaches auth.json only for a service the user ticked (Settings.ManagedPiServices), and leaves it when the
    ///   service is unticked or the copy removed. The file is plain text, as in any Pi installation.
    /// - No key is returned to a caller outside AI Meter: KeyFor serves the runtime scan, which turns it into an opaque
    ///   credential id.
    /// </summary>
    internal static class ManagedPi
    {
        /// <summary>The agent definition (agents/pi.json) the copy belongs to, and its dispatch place there.</summary>
        public const string RuntimeId = "pi";
        public const string Place = "managed";

        /// <summary>
        /// Whether the Microsoft Store build offers the managed copy; GitHub builds always do. False until the Store
        /// listing discloses the optional download (ADR 0004 decision 7, docs/STORE.md): a package is submitted
        /// automatically from a release tag, and must not reach certification ahead of its description. It is also
        /// the fallback should certification refuse the download.
        /// </summary>
        private static readonly bool OfferedInStoreBuild = false;

        /// <summary>One release of Pi's standalone build: where it is published and what the archive must be.</summary>
        internal sealed class Release
        {
            public string Version, Url, Sha256;
            /// <summary>The archive's size in bytes, shown before the download and enforced during it.</summary>
            public long ArchiveBytes;
        }

        /// <summary>
        /// The release AI Meter adopts. A newer one is adopted only by changing these lines, in a new AI Meter version,
        /// after its dispatch has been verified: Pi does not promise a stable event format. The digest is the one GitHub
        /// and the release's own SHA256SUMS state for pi-windows-x64.zip (checked on 2026-11-19).
        /// </summary>
        internal static readonly Release Pinned = new Release
        {
            Version = "1.5.2",
            Url = "https://github.com/earendil-works/pi/releases/download/v1.5.2/pi-windows-x64.zip",
            Sha256 = "e6adb8cb8c84eca14391ad24b363de9d2be98a89ca3ba3fdee1fa4de32b7f2e8",
            ArchiveBytes = 61940631,
        };

        /// <summary>Where the release is published, for Settings to name and link.</summary>
        public const string ProjectPage = "https://github.com/earendil-works/pi";

        /// <summary>An archive may unpack to at most this much: the 1.5.2 build is 131 MB.</summary>
        private const long UnpackedLimitBytes = 512L * 1024 * 1024;
        private const string ProgramName = "pi.exe";
        private const string PartialSuffix = ".partial";

        /// <summary>A failed installation or removal, with a sentence Settings can show.</summary>
        internal sealed class InstallException : Exception
        {
            public InstallException(string message, Exception inner = null) : base(message, inner) { }
        }

        public static bool Offered { get { return !Program.Demo && (OfferedInStoreBuild || !Native.IsPackaged); } }

        public static string Root { get { return Path.Combine(Settings.LocalSharedFolder, "managed", "pi"); } }
        public static string ConfigFolder { get { return Path.Combine(Root, "config"); } }
        public static string ProgramPath { get { return ProgramIn(Pinned); } }
        private static string KeyFile { get { return Path.Combine(ConfigFolder, "auth.json"); } }

        private static string FolderOf(Release release) { return Path.Combine(Root, release.Version); }
        private static string ProgramIn(Release release) { return Path.Combine(FolderOf(release), ProgramName); }

        /// <summary>Whether the adopted release is unpacked and complete. An older release left by a previous AI Meter does not count.</summary>
        public static bool Installed
        {
            get
            {
                try { return Offered && File.Exists(ProgramPath); }
                catch (ArgumentException) { return false; }
            }
        }

        /// <summary>The version of another release still on disk, which the adopted one would replace; null when there is none.</summary>
        public static string OtherVersionInstalled
        {
            get
            {
                return Probe.SafeDirs(Root).Select(Path.GetFileName)
                    .FirstOrDefault(n => n != Pinned.Version && n != "config" && !n.EndsWith(PartialSuffix, StringComparison.Ordinal) && File.Exists(Path.Combine(Root, n, ProgramName)));
            }
        }

        /// <summary>
        /// What tells one state of the copy from another, for callers that cache what they derived from it: whether it
        /// is installed and when its keys were last written.
        /// </summary>
        public static string Stamp
        {
            get
            {
                if (!Installed) return "";
                try { return File.Exists(KeyFile) ? File.GetLastWriteTimeUtc(KeyFile).Ticks.ToString() : "0"; }
                catch (IOException) { return "0"; }
                catch (UnauthorizedAccessException) { return "0"; }
            }
        }

        // ------------------------------------------------------------ installation

        /// <summary>
        /// Download the adopted release, check it and unpack it. progress receives the bytes read so far; cancelled is
        /// asked between blocks. Throws InstallException with the reason; whatever was downloaded is deleted then.
        /// </summary>
        public static void Install(Action<long> progress, Func<bool> cancelled) { Install(Pinned, OpenArchive, progress, cancelled); }

        /// <summary>Install with the release and the source given, so that tests need no network.</summary>
        internal static void Install(Release release, Func<string, Stream> open, Action<long> progress, Func<bool> cancelled)
        {
            // Names no earlier attempt used, so nothing left behind or put there by another program is ever unpacked or
            // promoted: the archive file is created new and the staging folder must not exist yet.
            string attempt = Guid.NewGuid().ToString("N");
            string archive = Path.Combine(Root, release.Version + "-" + attempt + ".zip" + PartialSuffix);
            string unpacked = FolderOf(release) + "-" + attempt + PartialSuffix;
            try
            {
                Directory.CreateDirectory(Root);
                foreach (var stale in Probe.SafeDirs(Root).Where(d => d.EndsWith(PartialSuffix, StringComparison.Ordinal))) TryDelete(stale);
                foreach (var stale in Probe.SafeFiles(Root, "*" + PartialSuffix)) TryDeleteFile(stale.FullName);

                // One handle, held without sharing from the first byte written to the last entry unpacked: what is
                // unpacked is exactly what was hashed.
                using (var file = new FileStream(archive, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
                {
                    Download(release, open, file, progress, cancelled);
                    Unpack(file, unpacked, cancelled);
                }
                if (!File.Exists(Path.Combine(unpacked, ProgramName))) throw new InstallException(L.T("The downloaded archive does not contain Pi's program. Nothing was installed."));

                // The complete folder takes its place in one step, so a copy is never seen half unpacked.
                if (Directory.Exists(FolderOf(release))) Directory.Delete(FolderOf(release), true);
                Directory.Move(unpacked, FolderOf(release));
                foreach (var other in Probe.SafeDirs(Root).Where(d => !SamePath(d, FolderOf(release)) && !SamePath(d, ConfigFolder))) TryDelete(other);
                lock (ConfigLock) BindShell();
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
            if (!string.Equals(digest, release.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InstallException(L.T("The downloaded file is not the expected Pi release. It was deleted and nothing was installed."));
        }

        /// <summary>
        /// Unpack the verified archive, read back through the handle it was written with, into a folder that must not
        /// exist yet. Every entry must land inside that folder, whatever its name says, and the whole may not exceed
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

        /// <summary>
        /// Remove the copy with its configuration and the keys copied into it, and forget which services were ticked.
        /// The count of the key file's changes is the one file kept, raised like for any other change: a task that is
        /// ending while Pi is removed, or that outlives a removal and a new installation, then sees that the keys
        /// changed and never trusts a count it had seen before.
        /// Throws InstallException when a file is in use (a task still running). The keys go first and nothing else is
        /// touched until they are gone: when the program cannot be deleted, the keys already are; when the key file
        /// itself cannot, the copy and the choices stay as they were, for the user to try again.
        /// </summary>
        public static void Remove(Settings settings)
        {
            try
            {
                // Under the lock Sync writes with: a sync already under way ends first, and one that comes after
                // finds no choices and no copy, so it cannot bring a key back.
                bool choicesSaved = true;
                lock (ConfigLock)
                {
                    if (Directory.Exists(ConfigFolder)) AnnounceChange();
                    DeleteKeyFiles();
                    if (settings != null)
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

        // ------------------------------------------------------------ keys

        /// <summary>
        /// The provider id the copy knows a service by: the first of the service's aliases (auth.ids) among the
        /// providers the standalone build has built in (the place's "providers" in agents/pi.json). Null when Pi has no
        /// such provider, and so cannot call the service with a key alone.
        /// </summary>
        public static string ProviderOf(ServiceDefinition d)
        {
            var kind = Agents.Get(RuntimeId);
            var place = kind == null ? null : kind.PlaceFor(Place);
            if (place == null || d == null || !d.UsesKey) return null;
            return ServiceDefinition.Strings(d.Auth, "ids").DefaultIfEmpty(d.Id).FirstOrDefault(id => place.Providers.Contains(id, StringComparer.Ordinal));
        }

        /// <summary>The services whose key can be copied: those Pi has a provider for and AI Meter reads with a key it has now.</summary>
        public static List<ServiceDefinition> ServicesWithKey()
        {
            return Definitions.All.Where(d => ProviderOf(d) != null && PlainKey(d) != null).ToList();
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
            if (key == null || key.Length < 8 || key[0] == '!') return null;
            return key.Any(c => c == '$' || char.IsWhiteSpace(c) || char.IsControl(c)) ? null : key;
        }

        /// <summary>
        /// Bring the copy's configuration in step with the user's choices and this PC: its key file holds one entry for
        /// each ticked service that still has a key, nothing else, and its settings name the Git Bash found now (see
        /// BindShell). Called when a choice or a key changes, after an installation and at start, so a key the user
        /// replaced or removed in AI Meter does not live on here. Without an installed copy, or with nothing ticked, no
        /// key file is left, nor a half-written one. Returns false when a key file could not be written or deleted: the
        /// caller tells the user, and the next call tries again.
        /// </summary>
        public static bool Sync(Settings settings)
        {
            // With sample data the real copy is neither shown nor used, and its keys are left as they are.
            if (settings == null || Program.Demo) return true;
            // One writer at a time: Settings calls this on the interface thread, a key change in the background.
            lock (ConfigLock)
            {
                if (Installed) BindShell();
                return WriteKeys(Installed ? settings.ManagedPiServices ?? new List<string>() : new List<string>());
            }
        }

        private static readonly object ConfigLock = new object();
        private static string PendingKeyFile { get { return KeyFile + ".new"; } }

        private static bool WriteKeys(IEnumerable<string> services)
        {
            try
            {
                // Inside the error boundary: reading a saved key can fail like any other file.
                var entries = new Dictionary<string, object>();
                foreach (var id in services)
                {
                    var d = Definitions.Get(id);
                    string provider = ProviderOf(d), key = provider == null ? null : PlainKey(d);
                    if (key != null) entries[provider] = new Dictionary<string, object> { { "type", "api_key" }, { "key", key } };
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
                AnnounceChange();
                if (text == null) DeleteKeyFiles();
                else
                {
                    try
                    {
                        File.WriteAllText(PendingKeyFile, text, new UTF8Encoding(false));
                        if (File.Exists(KeyFile)) File.Replace(PendingKeyFile, KeyFile, null);
                        else File.Move(PendingKeyFile, KeyFile);
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

        private static string RevisionFile { get { return Path.Combine(ConfigFolder, "keys-revision"); } }

        /// <summary>
        /// The count of the key file's changes, kept in a file beside it that holds only that number (0 while there is
        /// no file). Every change raises it twice: to an odd number before the key file is touched, to the next even
        /// one once the change is complete. A plugin reads the keys before and after a task to remove them from what it
        /// returns, and keeps a task's output only when it started on an even count and ended on the same count or the
        /// next even one: anything else means that a key may have come and gone unseen, or that a change was under way
        /// (docs/MCP.md, "Managed Pi"). Announcing first matters: if the count cannot be written, the key file is not
        /// changed at all; if the key file or the closing count fails, the count stays odd, which no task trusts,
        /// until a later call finishes it.
        /// </summary>
        private static long Revision()
        {
            long revision = 0;
            if (File.Exists(RevisionFile)) long.TryParse(File.ReadAllText(RevisionFile, Encoding.UTF8).Trim(), out revision);
            return Math.Max(0, revision);
        }

        /// <summary>
        /// Raise the count to an odd number no task has seen, before the key file is touched. A count already odd is
        /// a change that did not finish: this one is another change, so it gets the next odd number, or the two would
        /// be counted as one.
        /// </summary>
        private static void AnnounceChange()
        {
            long revision = Revision();
            WriteRevision(revision % 2 == 0 ? revision + 1 : revision + 2);
        }

        /// <summary>Write the count through a temporary file, so that a reader never sees it half written.</summary>
        private static void WriteRevision(long revision)
        {
            Directory.CreateDirectory(ConfigFolder);
            string pending = RevisionFile + ".new";
            try
            {
                File.WriteAllText(pending, revision.ToString(System.Globalization.CultureInfo.InvariantCulture), new UTF8Encoding(false));
                if (File.Exists(RevisionFile)) File.Replace(pending, RevisionFile, null);
                else File.Move(pending, RevisionFile);
            }
            finally
            {
                if (File.Exists(pending)) File.Delete(pending);
            }
        }

        /// <summary>Delete the key file and a half-written one; throws when either cannot be deleted.</summary>
        private static void DeleteKeyFiles()
        {
            if (File.Exists(PendingKeyFile)) File.Delete(PendingKeyFile);
            if (File.Exists(KeyFile)) File.Delete(KeyFile);
        }

        /// <summary>
        /// The key the copy may use for a service, with the provider id it is kept under; null when it holds none, the
        /// user has not ticked the service now, or the key in the file is no longer the one AI Meter has: a key that
        /// could not be deleted after a choice was withdrawn, or replaced after the user changed or removed it, must not
        /// keep the service on offer. For the runtime scan only, which reports an opaque credential id.
        /// </summary>
        internal static string KeyFor(ServiceDefinition d, out string provider)
        {
            provider = ProviderOf(d);
            var settings = Settings.Current;
            if (provider == null || settings == null || settings.ManagedPiServices == null || !settings.ManagedPiServices.Contains(d.Id)) return null;
            string key = JPath.Str(JPath.Get(Json.ParseFile(KeyFile), provider + ".key"));
            return string.IsNullOrEmpty(key) || key != PlainKey(d) ? null : key;
        }

        // ------------------------------------------------------------ shell

        /// <summary>The name, in agents/pi.json's alternatives and in describe_dispatch, of the shell BindShell binds.</summary>
        public const string GitBashShell = "git_bash";

        private static string SettingsFile { get { return Path.Combine(ConfigFolder, "settings.json"); } }

        /// <summary>
        /// Name the Git Bash found on this PC as the copy's shell (Pi's shellPath setting). With its bash tool, Pi
        /// otherwise searches for a bash.exe each time it runs a command, and can find WSL's launcher; with shellPath
        /// set it uses that file only, and fails when the file is gone (Pi 1.5.2, utils/shell.ts; seen live on
        /// 2026-11-20). A binding once written is never taken back, only replaced: should Git Bash disappear, a task
        /// already described with the bash tool then fails at its first command instead of searching, and new tasks
        /// are not offered that tool (BoundShells). The copy's settings file holds nothing else and is AI Meter's to write.
        /// </summary>
        private static void BindShell()
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

        /// <summary>How BindShell looks for Git Bash; tests replace it, so that they do not depend on what this PC has.</summary>
        internal static Func<string> LocateGitBash = () => FindGitBash(Environment.GetEnvironmentVariable, File.Exists);

        /// <summary>
        /// The shells the copy is bound to now, besides the PowerShell tool it always has: GitBashShell while its
        /// settings name a bash.exe that exists. Only then may a plugin give it the bash tool.
        /// </summary>
        public static string[] BoundShells
        {
            get
            {
                string bash = Json.Str(Json.ParseFile(SettingsFile), "shellPath");
                try { return !string.IsNullOrEmpty(bash) && File.Exists(bash) ? new[] { GitBashShell } : new string[0]; }
                catch (ArgumentException) { return new string[0]; }
            }
        }

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
            foreach (var place in places)
            {
                string bash = Path.Combine(place, "bash.exe");
                if (!bash.StartsWith(windows, StringComparison.OrdinalIgnoreCase) && exists(bash)) return bash;
            }
            return null;
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
