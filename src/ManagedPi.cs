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
    /// Layout, under the folder other programs can read (Settings.SharedFolder), outside PATH:
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
        /// When false, the Microsoft Store build does not offer the managed copy (ADR 0004 decision 7: the fallback
        /// should certification refuse the download); GitHub builds always do.
        /// </summary>
        private static readonly bool OfferedInStoreBuild = true;

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

        public static string Root { get { return Path.Combine(Settings.SharedFolder, "managed", "pi"); } }
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
            string archive = Path.Combine(Root, release.Version + ".zip" + PartialSuffix);
            string unpacked = FolderOf(release) + PartialSuffix;
            try
            {
                Directory.CreateDirectory(Root);
                Download(release, open, archive, progress, cancelled);
                Unpack(archive, unpacked, cancelled);
                if (!File.Exists(Path.Combine(unpacked, ProgramName))) throw new InstallException(L.T("The downloaded archive does not contain Pi's program. Nothing was installed."));

                // The complete folder takes its place in one step, so a copy is never seen half unpacked.
                if (Directory.Exists(FolderOf(release))) Directory.Delete(FolderOf(release), true);
                Directory.Move(unpacked, FolderOf(release));
                foreach (var other in Probe.SafeDirs(Root).Where(d => !SamePath(d, FolderOf(release)) && !SamePath(d, ConfigFolder))) TryDelete(other);
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
        /// Write the archive to a file while hashing it, and keep it only if its SHA-256 is the release's. A source that
        /// sends more than the release's size is cut off: the file could not match.
        /// </summary>
        private static void Download(Release release, Func<string, Stream> open, string archive, Action<long> progress, Func<bool> cancelled)
        {
            string digest;
            using (var sha = SHA256.Create())
            using (var source = open(release.Url))
            using (var file = new FileStream(archive, FileMode.Create, FileAccess.Write, FileShare.None))
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
        /// Unpack a verified archive into a new folder. Every entry must land inside that folder, whatever its name
        /// says, and the whole may not exceed UnpackedLimitBytes: the archive is verified, but a wrong pin should fail
        /// here rather than write outside or fill the disk.
        /// </summary>
        private static void Unpack(string archive, string folder, Func<bool> cancelled)
        {
            TryDelete(folder);
            Directory.CreateDirectory(folder);
            string root = Path.GetFullPath(folder) + Path.DirectorySeparatorChar;
            using (var zip = ZipFile.OpenRead(archive))
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
        /// Throws InstallException when a file is in use (a task still running). The keys go first and nothing else is
        /// touched until they are gone: when the program cannot be deleted, the keys already are; when the key file
        /// itself cannot, the copy and the choices stay as they were, for the user to try again.
        /// </summary>
        public static void Remove(Settings settings)
        {
            try
            {
                if (File.Exists(KeyFile)) File.Delete(KeyFile);
                if (settings != null)
                {
                    settings.ManagedPiServices = new List<string>();
                    settings.Save();
                }
                if (Directory.Exists(Root)) Directory.Delete(Root, true);
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
            return Definitions.All.Where(d => ProviderOf(d) != null && SourceKey(d) != null).ToList();
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
        /// Rewrite the copy's key file from the user's choices: one entry for each ticked service that still has a key,
        /// nothing else. Called when a choice or a key changes, and at start, so a key the user replaced or removed in
        /// AI Meter does not live on here. Returns false when the file could not be written.
        /// </summary>
        public static bool SyncKeys(Settings settings)
        {
            if (settings == null || !Installed) return true;
            // One writer at a time: Settings calls this on the interface thread, a key change in the background.
            lock (KeyLock) return WriteKeys(settings.ManagedPiServices ?? new List<string>());
        }

        private static readonly object KeyLock = new object();

        private static bool WriteKeys(IEnumerable<string> services)
        {
            var entries = new Dictionary<string, object>();
            foreach (var id in services)
            {
                var d = Definitions.Get(id);
                string provider = ProviderOf(d), key = provider == null ? null : SourceKey(d);
                if (key != null) entries[provider] = new Dictionary<string, object> { { "type", "api_key" }, { "key", key } };
            }
            try
            {
                if (entries.Count == 0)
                {
                    if (File.Exists(KeyFile)) File.Delete(KeyFile);
                    return true;
                }
                string text = Json.Serialize(entries);
                if (File.Exists(KeyFile) && File.ReadAllText(KeyFile, Encoding.UTF8) == text) return true;
                Directory.CreateDirectory(ConfigFolder);
                string temporary = KeyFile + ".new";
                File.WriteAllText(temporary, text, new UTF8Encoding(false));
                if (File.Exists(KeyFile)) File.Replace(temporary, KeyFile, null);
                else File.Move(temporary, KeyFile);
                return true;
            }
            catch (IOException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
        }

        /// <summary>
        /// The key the copy holds for a service, with the provider id it is kept under; null when it holds none. For
        /// the runtime scan only, which reports an opaque credential id.
        /// </summary>
        internal static string KeyFor(ServiceDefinition d, out string provider)
        {
            provider = ProviderOf(d);
            if (provider == null) return null;
            string key = JPath.Str(JPath.Get(Json.ParseFile(KeyFile), provider + ".key"));
            return string.IsNullOrEmpty(key) ? null : key;
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
