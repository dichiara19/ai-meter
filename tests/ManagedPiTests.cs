using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace AIMeter
{
    /// <summary>
    /// The copy of Pi that AI Meter keeps for delegation (ADR 0004): a verified installation that leaves nothing behind
    /// when it fails, keys copied only by the user's choice, and how the scan, dispatch and MCP tools offer the copy.
    /// Every test runs on a fake archive under the test folder, with no network and no key from this PC.
    /// </summary>
    internal static partial class ManagedPiTests
    {
        internal static void Register(Action<string, Action> test)
        {
            RegisterWsl(test);
            test("The managed Pi installs from a verified archive, reporting progress and leaving no partial files", () => Isolated(InstallsVerifiedArchive));
            test("A wrong, oversized, escaping, incomplete or cancelled archive installs nothing and keeps an installed copy", () => Isolated(RefusesBadArchives));
            test("The managed Pi holds keys only for ticked services Pi knows, and Remove deletes keys, program and choices", () => Isolated(KeysFollowChoices));
            test("The scan offers the managed Pi only when installed, with its copied keys, after the user's own Pi", () => Isolated(ScanOffersManagedCopy));
            test("Dispatch describes the managed Pi with its own folder, shell and alternatives, on Windows folders only", () => Isolated(DescribesManagedCopy));
            test("The MCP tools show, describe and lease the managed Pi without revealing its keys", () => Isolated(McpShowsManagedCopy));
            test("The managed Pi's strings are translated in every language, with their placeholders", Translations);
            test("Installing the managed Pi deletes stale partial files and folders and never promotes them", () => Isolated(InstallClearsStalePartials));
            test("A service unticked gives no access even while its key is still in the file, and no half-written key file outlives a sync or Remove", () => Isolated(KeysAreRevokedAndLeaveNothing));
            test("Without Git Bash, or once its bash.exe is gone, the managed Pi is offered PowerShell only", () => Isolated(DescribesWithoutGitBash));
            test("Git Bash is found in Git's folders or beside git.exe on PATH, never under the Windows folder", () => Isolated(FindsGitBash));
            test("For the same model a user's own agent comes before the managed Pi, even one that sends more tokens", () => Isolated(OwnAgentBeforeManagedCopy));
            test("The Store build does not offer the managed Pi, even with a copy on disk", () => Isolated(StoreBuildDoesNotOffer));
            test("Only a plain key reaches the managed Pi, and a key changed or removed since the last sync gives no access", () => Isolated(CopiesPlainKeysOnly));
            test("Remove and Sync take turns: once Remove has returned, no sync brings a key file back", () => Isolated(RemoveAndSyncTakeTurns));
            test("Saving the settings reports whether the file was written", () => Isolated(SavingSettingsReportsFailure));
            test("The managed Pi refuses WSL folders in their extended form and inherits no credentials", () => Isolated(ExtendedWslPathsAndEnvironmentPolicy));
            test("Installing over an older release brings back the keys of the services already ticked", () => Isolated(InstallOverOlderReleaseRestoresKeys));
            test("The key file's revision goes up by two at each change of the file and stays when a sync changes nothing", () => Isolated(KeyRevisionCountsChanges));
            test("With sample data the managed Pi is not offered and Sync leaves its key file as it is", () => Isolated(DemoLeavesKeysAlone));
            test("Remove reports choices it could not save, after the keys and the copy are gone", () => Isolated(RemoveReportsUnsavedChoices));
            test("A key change is announced before the key file is touched, and a change left half-way keeps the count odd until finished", () => Isolated(RevisionSurvivesFailures));
            test("Remove keeps only the key file's count, raised past any odd value, and a new copy continues from it", () => Isolated(RemoveKeepsOnlyTheCount));
            test("A saved key that cannot be read counts as none, and the copy keeps the entry it has", () => Isolated(UnreadableKeyCountsAsNone));
            test("The managed Pi starts without skills and prompt templates in every argument list; Pi in WSL keeps the shared ones", () => Isolated(ManagedArgumentsSkipSkills));
            test("An unknown environment policy is refused, never read as inheritance; known ones pass through", () => Isolated(UnknownEnvironmentPolicyIsRefused));
            test("Ticked services saved by another AI Meter are read once, and never this process's own or a failed save", () => Isolated(ServicesSavedElsewhere));
            test("The managed Pi lives in the local shared folder: the test folder, or the profile's .aimeter in the Store build", () => Isolated(LocalSharedFolder));
            test("The managed Pi gets Pi's own model lists beside it, rewritten when missing or changed, and its accesses name them", () => Isolated(ManagedModelLists));
            test("Cloudflare reaches the managed Pi with its account id, and its free tier is offered through the copy with consent", () => Isolated(CloudflareThroughCopy));
            test("An NVIDIA model whose tool support the service does not state is a best-effort candidate through the copy", () => Isolated(NvidiaBestEffortThroughCopy));
        }

        private static void Translations()
        {
            try
            {
                foreach (var row in L.ManagedPiStrings)
                    Assert(row.Length == 6 && row.All(text => !string.IsNullOrEmpty(text)), "A managed Pi string lacks a language: " + row[0]);
                foreach (var language in new[] { "it", "es", "fr", "de", "pt" })
                {
                    L.Init(language);
                    foreach (var row in L.ManagedPiStrings)
                    {
                        string english = row[0], translated = L.T(english);
                        Assert(translated != english, "Missing managed Pi translation for " + language + ": " + english);
                        foreach (var placeholder in new[] { "{0}", "{1}", "{2}" })
                            Assert(english.Contains(placeholder) == translated.Contains(placeholder), "The placeholder " + placeholder + " differs in " + language + ": " + english);
                    }
                }
            }
            finally { L.Init("en"); }
        }

        private static void Assert(bool condition, string message)
        {
            if (!condition) throw new Exception(message);
        }

        private const string Model = "nvidia/nemotron-3.5-lightning:free";
        private const string RouterKey = "sk-or-v1-managedtest0123456789abcdef";
        private const string GeminiKey = "AIzaManagedTest0123456789abcdefghij";
        private const string LabsKey = "explabs-managedtest-0123456789abcdef";

        /// <summary>The service definitions these tests use, read from the repository as the only ones loaded.</summary>
        private static readonly string[] Services = { "openrouter", "gemini", "experientiallabs", "cloudflare-workers-ai", "nvidia" };

        /// <summary>
        /// Run a test with the copy's folder under the test folder (not packaged), the repository's definitions of
        /// Services as the only ones, key discovery off and no key variable of those services in this process, so that
        /// no key of the developer's machine reaches a test. No Git Bash is found unless a test sets one, so results do
        /// not depend on whether Git is installed on this PC. Restores what it changed. A key set in the user's own
        /// environment cannot be hidden, so the test refuses to run then.
        /// </summary>
        private static void Isolated(Action action)
        {
            string repository = Environment.GetEnvironmentVariable("AIMETER_REPOSITORY") ?? Environment.CurrentDirectory;
            Directory.CreateDirectory(Definitions.UserFolder);
            foreach (var id in Services) File.Copy(Path.Combine(repository, "providers", id + ".json"), Path.Combine(Definitions.UserFolder, id + ".json"), true);
            var cached = typeof(Definitions).GetField("all", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
            cached.SetValue(null, null);

            var packaged = Native.PackagedOverride;
            bool findKeys = Settings.Current.FindKeys;
            var services = Settings.Current.ManagedPiServices;
            var locateGitBash = ManagedPi.LocateGitBash;
            var runWsl = ManagedPi.RunWsl;
            var wslPlaces = Probe.WslPlacesUp;
            string wslDistro = Settings.Current.WslDistro, wslUser = Settings.Current.WslUser, prefer = Settings.Current.ManagedPiPrefer;
            bool besideOwn = Settings.Current.ManagedPiBesideOwn;
            var saved = new Dictionary<string, string>();
            foreach (var id in Services)
                foreach (var name in KeyVariables(Definitions.Get(id)))
                {
                    Assert(Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.User) == null,
                        name + " is set in the user's environment; these tests need it unset to stay deterministic.");
                    saved[name] = Environment.GetEnvironmentVariable(name);
                    Environment.SetEnvironmentVariable(name, null);
                }
            try
            {
                Native.PackagedOverride = false;
                Settings.Current.FindKeys = false;
                Settings.Current.ManagedPiServices = new List<string>();
                ManagedPi.LocateGitBash = () => null;
                // No WSL runs, whatever this PC has (an earlier test's collection may have seen it), and nothing is
                // started inside it unless a test sets its own stand-in for wsl.exe.
                Probe.WslPlacesUp = new WslPlace[0];
                Settings.Current.WslDistro = "";
                ManagedPi.RunWsl = (arguments, input) => { throw new Exception("A test started wsl.exe: " + string.Join(" ", arguments)); };
                Settings.Current.ManagedPiPrefer = null;
                Settings.Current.ManagedPiBesideOwn = false;
                action();
            }
            finally
            {
                foreach (var kv in saved) Environment.SetEnvironmentVariable(kv.Key, kv.Value);
                Native.PackagedOverride = packaged;
                ManagedPi.LocateGitBash = locateGitBash;
                ManagedPi.RunWsl = runWsl;
                Probe.WslPlacesUp = wslPlaces;
                Settings.Current.WslDistro = wslDistro;
                Settings.Current.WslUser = wslUser;
                Settings.Current.ManagedPiPrefer = prefer;
                Settings.Current.ManagedPiBesideOwn = besideOwn;
                Settings.Current.FindKeys = findKeys;
                Settings.Current.ManagedPiServices = services;
                foreach (var id in Services)
                {
                    foreach (var slot in Slots(Definitions.Get(id))) ApiKeys.Remove(id + "." + slot);
                    ApiKeys.Remove(id);
                    File.Delete(Path.Combine(Definitions.UserFolder, id + ".json"));
                }
                cached.SetValue(null, null);
            }
        }

        /// <summary>A service's values beside its key (its definition's "keys"), such as Cloudflare's account id.</summary>
        private static IEnumerable<string> Slots(ServiceDefinition d)
        {
            var slots = d == null ? null : Json.Obj(d.Raw, "keys");
            return slots == null ? new string[0] : slots.Keys.ToArray();
        }

        /// <summary>The variables a service's key and the values beside it are read from.</summary>
        private static IEnumerable<string> KeyVariables(ServiceDefinition d)
        {
            var names = ServiceDefinition.Strings(d.Auth, "env").ToList();
            foreach (var slot in Slots(d)) names.AddRange(ServiceDefinition.Strings(Json.Obj(Json.Obj(d.Raw, "keys"), slot), "env"));
            return names.Distinct();
        }

        // ------------------------------------------------------------ archives

        /// <summary>A zip archive in memory: entry names and their text contents, in that order.</summary>
        private static byte[] Archive(params string[] namesAndContents)
        {
            using (var memory = new MemoryStream())
            {
                using (var zip = new ZipArchive(memory, ZipArchiveMode.Create, true))
                    for (int i = 0; i < namesAndContents.Length; i += 2)
                        using (var writer = new StreamWriter(zip.CreateEntry(namesAndContents[i]).Open()))
                            writer.Write(namesAndContents[i + 1]);
                return memory.ToArray();
            }
        }

        private static readonly byte[] Good = Archive("pi.exe", "not really Pi", @"theme/dark.json", "{}");

        /// <summary>The pinned version, with the archive's real digest and exact length: the Windows build, or the Linux one.</summary>
        private static ManagedPi.Release ReleaseOf(byte[] archive, bool linux = false)
        {
            string digest;
            using (var sha = SHA256.Create()) digest = BitConverter.ToString(sha.ComputeHash(archive)).Replace("-", "").ToLowerInvariant();
            return new ManagedPi.Release
            {
                Version = ManagedPi.Version, Url = "https://example.invalid/pi-" + (linux ? "linux-x64.tar.gz" : "windows-x64.zip"), Sha256 = digest, ArchiveBytes = archive.Length,
                ArchiveKind = linux ? ManagedPi.TarGz : ManagedPi.Zip, ProgramName = linux ? "pi" : "pi.exe",
            };
        }

        private static void Install(byte[] archive, ManagedPi.Release release, Func<bool> cancelled = null, List<long> progress = null)
        {
            ManagedPi.Windows.Install(release, url => new MemoryStream(archive), read => { if (progress != null) progress.Add(read); }, cancelled);
        }

        /// <summary>Files and folders under the copy's root named as a partial download or unpacking.</summary>
        private static List<string> Partials()
        {
            if (!Directory.Exists(ManagedPi.Windows.Root)) return new List<string>();
            return Directory.GetFileSystemEntries(ManagedPi.Windows.Root, "*", SearchOption.AllDirectories)
                .Where(p => p.EndsWith(".partial", StringComparison.OrdinalIgnoreCase) || p.EndsWith(".new", StringComparison.OrdinalIgnoreCase)).ToList();
        }

        private static void InstallsVerifiedArchive()
        {
            Assert(!ManagedPi.Windows.Installed && ManagedPi.Windows.Stamp == "", "A copy counted as installed before any installation.");
            var progress = new List<long>();
            Install(Good, ReleaseOf(Good), null, progress);
            Assert(ManagedPi.Windows.Installed && File.ReadAllText(ManagedPi.Windows.ProgramPath) == "not really Pi", "The program was not unpacked where Installed looks.");
            Assert(ManagedPi.Windows.ProgramPath == Path.Combine(ManagedPi.Windows.Root, ManagedPi.Version, "pi.exe"), "The program is not in the pinned version's folder.");
            Assert(File.Exists(Path.Combine(ManagedPi.Windows.Root, ManagedPi.Version, @"theme\dark.json")), "A nested file was not unpacked.");
            Assert(ManagedPi.Windows.Root.StartsWith(Settings.Folder, StringComparison.OrdinalIgnoreCase), "The copy is not under the shared folder.");
            Assert(Partials().Count == 0 && !Directory.GetFiles(ManagedPi.Windows.Root).Any(), "A partial download or unpacking was left: " + string.Join(", ", Partials()));
            Assert(progress.Count > 0 && progress.Last() == Good.Length, "Progress was not reported up to the archive's size.");
            Assert(ManagedPi.Windows.Stamp == "0", "An installed copy without keys has the wrong stamp: " + ManagedPi.Windows.Stamp);
        }

        /// <summary>Install must throw the given exception type; returns its message.</summary>
        private static string Fails<T>(Action install, string what) where T : Exception
        {
            try { install(); }
            catch (T e) { return e.Message; }
            throw new Exception("An install succeeded with " + what + ".");
        }

        private static void RefusesBadArchives()
        {
            string outside = Path.Combine(Settings.Folder, @"managed\evil.txt");
            string beside = Path.Combine(ManagedPi.Windows.Root, "evil.txt");
            var escaping = Archive("pi.exe", "x", @"..\..\evil.txt", "escaped");
            var besideRoot = Archive("pi.exe", "x", @"..\evil.txt", "escaped");
            var forward = Archive("pi.exe", "x", "../../evil.txt", "escaped");
            var wrongDigest = ReleaseOf(Good);
            wrongDigest.Sha256 = new string('0', 64);
            var tooLong = ReleaseOf(Good);
            tooLong.ArchiveBytes = Good.Length - 1;
            var noProgram = Archive("bin/other.exe", "x");

            Action<string> nothingLeft = what =>
            {
                Assert(!ManagedPi.Windows.Installed, "Installed is true after " + what + ".");
                Assert(!Directory.Exists(Path.Combine(ManagedPi.Windows.Root, ManagedPi.Version)), "A version folder was left after " + what + ".");
                Assert(Partials().Count == 0, "A partial file was left after " + what + ": " + string.Join(", ", Partials()));
                Assert(!File.Exists(outside) && !File.Exists(beside), "An entry was written outside the unpacking folder after " + what + ".");
            };

            Fails<ManagedPi.InstallException>(() => Install(Good, wrongDigest), "a wrong digest");
            nothingLeft("a wrong digest");
            Fails<ManagedPi.InstallException>(() => Install(Good, tooLong), "a source longer than the release");
            nothingLeft("a source longer than the release");
            Fails<ManagedPi.InstallException>(() => Install(escaping, ReleaseOf(escaping)), @"an entry ..\..\evil.txt");
            nothingLeft(@"an entry ..\..\evil.txt");
            Fails<ManagedPi.InstallException>(() => Install(besideRoot, ReleaseOf(besideRoot)), @"an entry ..\evil.txt");
            nothingLeft(@"an entry ..\evil.txt");
            Fails<ManagedPi.InstallException>(() => Install(forward, ReleaseOf(forward)), "an entry ../../evil.txt");
            nothingLeft("an entry ../../evil.txt");
            Fails<ManagedPi.InstallException>(() => Install(noProgram, ReleaseOf(noProgram)), "an archive without pi.exe");
            nothingLeft("an archive without pi.exe");
            Fails<OperationCanceledException>(() => Install(Good, ReleaseOf(Good), () => true), "a cancelled download");
            nothingLeft("a cancelled download");
            int asked = 0;
            Fails<OperationCanceledException>(() => Install(Good, ReleaseOf(Good), () => ++asked > 1), "a cancelled unpacking");
            Assert(asked > 1, "Cancellation was not asked while unpacking.");
            nothingLeft("a cancelled unpacking");

            // A failed installation over a complete copy leaves that copy as it was.
            Install(Good, ReleaseOf(Good));
            Fails<ManagedPi.InstallException>(() => Install(Good, wrongDigest), "a wrong digest over a copy");
            Fails<ManagedPi.InstallException>(() => Install(noProgram, ReleaseOf(noProgram)), "an archive without pi.exe over a copy");
            Fails<ManagedPi.InstallException>(() => Install(escaping, ReleaseOf(escaping)), "an escaping entry over a copy");
            Fails<OperationCanceledException>(() => Install(Good, ReleaseOf(Good), () => true), "a cancelled download over a copy");
            Assert(ManagedPi.Windows.Installed && File.ReadAllText(ManagedPi.Windows.ProgramPath) == "not really Pi", "A failed installation destroyed the installed copy.");
            Assert(Partials().Count == 0 && !File.Exists(outside) && !File.Exists(beside), "A failed installation over a copy left files.");
        }

        // ------------------------------------------------------------ keys

        private static string KeyFile { get { return Path.Combine(ManagedPi.Windows.ConfigFolder, "auth.json"); } }

        private static void KeysFollowChoices()
        {
            var settings = Settings.Current;
            var router = Definitions.Get("openrouter");
            var gemini = Definitions.Get("gemini");
            var labs = Definitions.Get("experientiallabs");
            ApiKeys.Save("openrouter", RouterKey);
            ApiKeys.Save("gemini", GeminiKey);
            ApiKeys.Save("experientiallabs", LabsKey);

            Assert(ManagedPi.ProviderOf(router) == "openrouter" && ManagedPi.ProviderOf(gemini) == "google", "A service's provider id for Pi is wrong.");
            Assert(ManagedPi.ProviderOf(labs) == null, "A service Pi has no built-in provider for has one.");
            var copyable = ManagedPi.ServicesWithKey().Select(d => d.Id).ToList();
            Assert(copyable.Contains("openrouter") && copyable.Contains("gemini") && !copyable.Contains("experientiallabs"),
                "The services whose key can be copied are wrong: " + string.Join(", ", copyable));

            // Not installed: nothing is written, whatever is ticked.
            settings.ManagedPiServices = new List<string> { "openrouter" };
            Assert(ManagedPi.Sync(settings) && !File.Exists(KeyFile) && !Directory.Exists(ManagedPi.Windows.Root), "Keys were written for a copy that is not installed.");

            Install(Good, ReleaseOf(Good));
            Assert(ManagedPi.Sync(settings), "The key file could not be written.");
            Assert(File.ReadAllText(KeyFile) == "{\"openrouter\":{\"type\":\"api_key\",\"key\":\"" + RouterKey + "\"}}",
                "The key file is not Pi's auth.json for OpenRouter: " + File.ReadAllText(KeyFile));
            string provider;
            Assert(ManagedPi.Windows.KeyFor(router, out provider) == RouterKey && provider == "openrouter", "KeyFor does not give the copied key.");
            Assert(ManagedPi.Windows.KeyFor(gemini, out provider) == null && provider == "google", "KeyFor gives a key that was not copied.");
            Assert(ManagedPi.Windows.Stamp != "0" && ManagedPi.Windows.Stamp != "", "The stamp does not follow the key file.");

            // Gemini under Pi's provider google; Experiential Labs never, even when ticked.
            settings.ManagedPiServices = new List<string> { "openrouter", "gemini", "experientiallabs" };
            Assert(ManagedPi.Sync(settings), "The key file could not be rewritten.");
            var keys = Json.ParseFile(KeyFile);
            Assert(keys.Keys.OrderBy(k => k, StringComparer.Ordinal).SequenceEqual(new[] { "google", "openrouter" }), "The key file's providers are wrong: " + string.Join(", ", keys.Keys));
            Assert(Json.Str(Json.Obj(keys, "google"), "key") == GeminiKey && Json.Str(Json.Obj(keys, "google"), "type") == "api_key", "Gemini's key is not under google.");
            Assert(!File.ReadAllText(KeyFile).Contains(LabsKey), "A key for a service Pi cannot call was copied.");
            Assert(ManagedPi.Windows.KeyFor(labs, out provider) == null && provider == null, "KeyFor gives a key for a service Pi cannot call.");

            // Unticking removes the entry, and the file once it is empty.
            settings.ManagedPiServices = new List<string> { "openrouter" };
            ManagedPi.Sync(settings);
            Assert(!File.ReadAllText(KeyFile).Contains(GeminiKey) && File.ReadAllText(KeyFile).Contains(RouterKey), "Unticking Gemini did not remove only its key.");
            settings.ManagedPiServices = new List<string>();
            Assert(ManagedPi.Sync(settings) && !File.Exists(KeyFile), "Unticking every service left the key file.");
            Assert(ManagedPi.Windows.Stamp == "0", "The stamp does not show that the keys are gone.");

            // No key found at all (a discovered key out of sight, or the first moments after a start) is not the user
            // taking it back: the copy keeps its entry, the count stays and the service stays on offer. Removing the
            // key in AI Meter unticks the service instead (Controller.RemoveApiKey, not covered here).
            settings.ManagedPiServices = new List<string> { "openrouter" };
            ManagedPi.Sync(settings);
            Assert(File.Exists(KeyFile), "The key was not copied again.");
            string held = File.ReadAllText(KeyFile);
            long count = Revision();
            ApiKeys.Remove("openrouter");
            Assert(ManagedPi.Sync(settings) && File.ReadAllText(KeyFile) == held && Revision() == count, "A key AI Meter cannot find now was withdrawn from the copy.");
            Assert(ManagedPi.Windows.KeyFor(router, out provider) == RouterKey && OffersManaged("openrouter"), "The copy's own key was refused while AI Meter finds none.");
            Assert(!ManagedPi.ServicesWithKey().Any(d => d.Id == "openrouter"), "A service without a key can still be copied.");

            // Ticked with no key found and no entry yet: nothing is written.
            settings.ManagedPiServices = new List<string>();
            Assert(ManagedPi.Sync(settings) && !File.Exists(KeyFile), "Unticking did not remove the kept entry.");
            count = Revision();
            settings.ManagedPiServices = new List<string> { "openrouter" };
            Assert(ManagedPi.Sync(settings) && !File.Exists(KeyFile) && Revision() == count, "A ticked service with no key wrote an entry.");

            // Remove deletes the keys, the program and the choices.
            ApiKeys.Save("openrouter", RouterKey);
            settings.ManagedPiServices = new List<string> { "openrouter", "gemini" };
            ManagedPi.Sync(settings);
            long before = Revision();
            ManagedPi.Windows.Remove(settings);
            AssertRemoved(before, "Remove");
            Assert(settings.ManagedPiServices != null && settings.ManagedPiServices.Count == 0, "Remove did not forget the ticked services.");
            Assert(!ManagedPi.Windows.Installed && ManagedPi.Windows.Stamp == "", "The copy still counts as installed after Remove.");
            long removed = Revision();
            Assert(ManagedPi.Sync(settings), "A sync after Remove failed.");
            AssertRemoved(before, "a sync after Remove");
            Assert(Revision() == removed, "A sync after Remove moved the count.");
        }

        /// <summary>
        /// After Remove only config\keys-revision is left under the copy's root, holding an even count above the one
        /// before: a plugin must never see the count go back.
        /// </summary>
        private static void AssertRemoved(long before, string what)
        {
            Assert(!ManagedPi.Windows.Installed && !File.Exists(KeyFile) && !File.Exists(PendingKeyFile), "The copy or its keys outlived " + what + ".");
            var left = Directory.Exists(ManagedPi.Windows.Root) ? Directory.GetFileSystemEntries(ManagedPi.Windows.Root, "*", SearchOption.AllDirectories).ToList() : new List<string>();
            Assert(left.Count == 2 && left.Contains(ManagedPi.Windows.ConfigFolder) && left.Contains(RevisionFile),
                "After " + what + " more than the count was left: " + string.Join(", ", left));
            long after = Revision();
            Assert(after % 2 == 0 && after > before, "After " + what + " the count is not even and above " + before + ": " + after);
        }

        // ------------------------------------------------------------ scan

        /// <summary>The copy installed, with OpenRouter's and Gemini's keys copied into it.</summary>
        private static void InstallWithKeys()
        {
            Install(Good, ReleaseOf(Good));
            ApiKeys.Save("openrouter", RouterKey);
            ApiKeys.Save("gemini", GeminiKey);
            Settings.Current.ManagedPiServices = new List<string> { "openrouter", "gemini" };
            Assert(ManagedPi.Sync(Settings.Current), "The keys could not be copied.");
        }

        /// <summary>An empty Windows home under the test folder; files and folders (null content) may be added.</summary>
        private static string Home(params string[] pathsAndContents)
        {
            string home = Path.Combine(Settings.Folder, "home-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(home);
            for (int i = 0; i < pathsAndContents.Length; i += 2)
            {
                string path = Path.Combine(home, pathsAndContents[i]);
                if (pathsAndContents[i + 1] == null) { Directory.CreateDirectory(path); continue; }
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllText(path, pathsAndContents[i + 1], new UTF8Encoding(false));
            }
            return home;
        }

        private static RuntimeScan ScanOf(string home, bool findKeys, IList<RuntimeKind> kinds = null, bool besideOwn = false)
        {
            return Candidates.Scan(home, new WslPlace[0], kinds ?? Candidates.Kinds, findKeys, besideOwn);
        }

        private static bool HasManaged(RuntimeScan scan)
        {
            return scan.Found.Any(f => f.Managed) || scan.Access.Any(a => a.Managed);
        }

        /// <summary>
        /// Pi as the built-in definition has it, plus a verified "windows" place for the user's own installation, which
        /// the built-in definition does not have yet.
        /// </summary>
        private const string PiWithWindows = "{ \"id\": \"pi\", \"name\": \"Pi\", \"programs\": { \"windows\": [\"pi\"], \"wsl\": [\"pi\"] }, \"folders\": [\".pi/agent\"], " +
            "\"keys\": [ { \"file\": \".pi/agent/auth.json\", \"path\": \"{id}.key\" } ], " +
            "\"dispatch\": { \"places\": { \"windows\": { \"versions\": [\"1.x\"] }, \"wsl\": { \"versions\": [\"1.x\"] }, " +
            "\"managed\": { \"versions\": [\"1.5.2\"], \"providers\": [\"google\", \"openrouter\"] } }, " +
            "\"arguments\": { \"read_only\": [\"-p\", \"--provider\", \"{provider}\", \"--model\", \"{model}\"], \"modify\": [\"-p\", \"--provider\", \"{provider}\", \"--model\", \"{model}\"] } } }";

        private static Snapshot Fixture()
        {
            var snap = new Snapshot();
            var catalogue = new Catalogue { ReadAt = DateTime.Now, Pool = "openrouter:free", Models = new List<FreeModel>
                { new FreeModel { Id = Model, Name = "Nemotron", Context = 1000000, MaxOutput = 65536, Tools = true, Free = FreeStatus.ZeroPrice } } };
            catalogue.Rates.Add(new RateLimit { Metric = "requests_per_minute", Limit = 20, Scope = "account", Source = "documented" });
            snap.Providers.Add(new ProviderState { Id = "openrouter", Name = "OpenRouter", Catalogue = catalogue });
            return snap;
        }

        private static void ScanOffersManagedCopy()
        {
            string home = Home();
            Assert(!HasManaged(ScanOf(home, true)), "A copy that is not installed was offered.");

            InstallWithKeys();
            var scan = ScanOf(home, true);
            var place = scan.Found.Where(f => f.Managed).ToList();
            Assert(place.Count == 1 && place[0].Runtime == "pi" && place[0].Where == "windows" && place[0].Distro == null, "The copy is not found as Pi on Windows.");
            var access = scan.Access.Where(a => a.Managed).OrderBy(a => a.Service, StringComparer.Ordinal).ToList();
            Assert(access.Select(a => a.Service + ":" + a.Provider).SequenceEqual(new[] { "gemini:google", "openrouter:openrouter" }),
                "The copy's accesses are not its copied keys: " + string.Join(", ", access.Select(a => a.Service + ":" + a.Provider)));
            foreach (var a in access)
            {
                Assert(a.Runtime == "pi" && a.Where == "windows" && a.Program == ManagedPi.Windows.ProgramPath && a.ConfigFolder == ManagedPi.Windows.ConfigFolder
                       && a.Version == ManagedPi.Version, "The managed access for " + a.Service + " lacks its program, folder or version.");
                Assert(a.Credential.StartsWith("cred_") && !a.Credential.Contains(RouterKey) && !a.Credential.Contains(GeminiKey), "A credential id shows the key.");
            }
            Assert(access.Single(a => a.Service == "openrouter").Credential == Candidates.Access("openrouter", "pi", "windows", RouterKey).Credential,
                "The credential id does not follow the key as for any other agent.");

            // The user's choice does not depend on key discovery.
            var off = ScanOf(home, false);
            Assert(off.KeyDiscoveryOff && off.Found.Count(f => f.Managed) == 1 && off.Access.Count(a => a.Managed) == 2, "Key discovery being off hid the copy.");
            Assert(!HasManaged(Candidates.Scan(null, new WslPlace[0], Candidates.Kinds, true)), "The copy was offered without the Windows home.");

            // Only Pi's definition with a managed place offers the copy.
            var noManaged = new List<RuntimeKind> { RuntimeKind.FromJson(PiWithWindows.Replace("\"managed\":", "\"elsewhere\":"), "test") };
            Assert(!HasManaged(ScanOf(home, true, noManaged)), "A definition without a managed place offered the copy.");

            // The user's own Pi on Windows wins, unless the user asked for both.
            var kinds = new List<RuntimeKind> { RuntimeKind.FromJson(PiWithWindows, "test") };
            Assert(kinds[0] != null && kinds[0].PlaceFor("windows") != null && kinds[0].PlaceFor(ManagedPi.Place) != null, "The test definition did not load.");
            string own = Home(@".pi\agent\auth.json", "{ \"openrouter\": { \"type\": \"api_key\", \"key\": \"" + RouterKey + "\" } }", @".local\bin\pi.exe", "own Pi");
            var userFirst = ScanOf(own, true, kinds);
            Assert(userFirst.Found.Any(f => f.Runtime == "pi" && f.Where == "windows" && !f.Managed), "The user's own Pi on Windows was not found.");
            // Service by service: the user's Pi holds a key for OpenRouter only, so the copy still serves Gemini.
            Assert(userFirst.Access.Where(a => a.Managed).Select(a => a.Service).SequenceEqual(new[] { "gemini" }),
                "The copy was not offered exactly for the services the user's own Pi cannot call: " + string.Join(", ", userFirst.Access.Where(a => a.Managed).Select(a => a.Service)));
            // With key discovery off, the user's Pi cannot be offered at all, so the copy serves every key it holds.
            Assert(ScanOf(own, false, kinds).Access.Count(a => a.Managed) == 2, "The copy's keys were hidden by a Pi whose own keys are not read.");

            var both = ScanOf(own, true, kinds, true);
            Assert(both.Found.Count(f => f.Runtime == "pi" && f.Where == "windows") == 2 && both.Found.Count(f => f.Managed) == 1, "Both Pi installations were not found when asked for.");
            var mine = both.Access.Single(a => a.Service == "openrouter" && !a.Managed);
            Assert(mine.Program.EndsWith(@".local\bin\pi.exe", StringComparison.OrdinalIgnoreCase) && mine.ConfigFolder == null, "The user's own access is wrong.");
            foreach (var from in new[] { null, "windows" })
            {
                var chosen = Candidates.Select(Fixture(), both, new Requirements { Service = "openrouter", From = from }, DateTime.Now).Chosen.Where(c => c.Model.Id == Model).ToList();
                Assert(chosen.Count == 2 && !chosen[0].Access.Managed && chosen[1].Access.Managed,
                    "For the same model the user's own Pi does not come first (from " + (from ?? "nowhere") + "): " + string.Join(", ", chosen.Select(c => c.Access.Managed)));
            }
            var fromWsl = Candidates.Select(Fixture(), both, new Requirements { Service = "openrouter", From = "wsl" }, DateTime.Now).Chosen;
            Assert(fromWsl.Count == 1 && !fromWsl[0].Access.Managed, "A caller in WSL was offered the managed copy.");

            // Not installed any more: gone from every scan.
            ManagedPi.Windows.Remove(Settings.Current);
            Assert(!HasManaged(ScanOf(home, true)) && !HasManaged(ScanOf(own, true, kinds, true)), "A removed copy was still offered.");
        }

        // ------------------------------------------------------------ dispatch

        private static RuntimeAccess ManagedAccess(RuntimeScan scan, string service)
        {
            return scan.Access.Single(a => a.Managed && a.Service == service);
        }

        private static string After(List<string> arguments, string option)
        {
            int i = arguments.IndexOf(option);
            return i < 0 || i + 1 >= arguments.Count ? null : arguments[i + 1];
        }

        private static string ShellSettingsFile { get { return Path.Combine(ManagedPi.Windows.ConfigFolder, "settings.json"); } }

        /// <summary>A stand-in for Git for Windows' bash.exe under the test folder; returns its path.</summary>
        private static string FakeGitBash()
        {
            string bash = Path.Combine(Settings.Folder, "git-" + Guid.NewGuid().ToString("N"), "bin", "bash.exe");
            Directory.CreateDirectory(Path.GetDirectoryName(bash));
            File.WriteAllText(bash, "not really bash");
            return bash;
        }

        private static void DescribesManagedCopy()
        {
            string fakeBash = FakeGitBash();
            ManagedPi.LocateGitBash = () => fakeBash;
            InstallWithKeys();
            Assert(Json.Str(Json.ParseFile(ShellSettingsFile), "shellPath") == fakeBash, "The copy's settings do not name the Git Bash found.");
            var router = ManagedAccess(ScanOf(Home(), false), "openrouter");
            Assert(router.Shells.SequenceEqual(new[] { "git_bash" }), "The access is not bound to Git Bash.");
            string workspace = @"C:\src\project";

            var modify = Dispatch.Describe(router, Model, Dispatch.Modify, workspace, "windows", null, router.Version);
            Assert(modify.Supported && modify.Managed && modify.VersionVerified, "The managed copy was not described: " + modify.Reason);
            Assert(modify.Program == ManagedPi.Windows.ProgramPath && modify.WorkingDirectory == workspace && modify.Where == "windows", "The managed copy's program or folder is wrong.");
            Assert(modify.Environment["PI_CODING_AGENT_DIR"] == ManagedPi.Windows.ConfigFolder && modify.Environment["PI_SKIP_VERSION_CHECK"] == "1" && modify.Environment["PI_TELEMETRY"] == "0",
                "The managed copy's environment is wrong.");
            string offline;
            Assert(modify.Environment.TryGetValue("PI_OFFLINE", out offline) && offline == "1", "The managed copy may download tools while it works (PI_OFFLINE).");
            Assert(After(modify.Arguments, "--tools") == "read,powershell,edit,write" && After(modify.Arguments, "--provider") == "openrouter" && After(modify.Arguments, "--model") == Model,
                "The managed copy's modify arguments are wrong: " + string.Join(" ", modify.Arguments));
            Assert(modify.Arguments.Contains("--no-context-files") && !modify.Arguments.Any(a => a.Contains("{")), "The managed copy is not isolated, or a placeholder was left.");
            Assert(modify.Commands == "powershell", "The modify mode does not say it runs PowerShell.");
            Assert(modify.Alternatives.Keys.SequenceEqual(new[] { "git_bash" }), "The git_bash alternative is missing.");
            var bash = modify.Alternatives["git_bash"];
            Assert(After(bash, "--tools") == "read,bash,edit,write" && After(bash, "--provider") == "openrouter" && After(bash, "--model") == Model,
                "The git_bash alternative is wrong: " + string.Join(" ", bash));

            var gemini = Dispatch.Describe(ManagedAccess(ScanOf(Home(), false), "gemini"), "gemini-2.5-flash", Dispatch.Modify, workspace, "windows", null, ManagedPi.Version);
            Assert(After(gemini.Arguments, "--provider") == "google" && After(gemini.Alternatives["git_bash"], "--provider") == "google", "Gemini is not called through Pi's provider google.");

            var readOnly = Dispatch.Describe(router, Model, Dispatch.ReadOnly, workspace, "windows", null, router.Version);
            Assert(readOnly.Supported && readOnly.Commands == "none" && After(readOnly.Arguments, "--tools") == "read,grep,find,ls", "A read-only task of the copy can run commands.");
            Assert(readOnly.Alternatives.Count == 0, "A read-only task has an alternative shell.");

            Assert(!Dispatch.Describe(router, Model, Dispatch.Modify, workspace, "wsl", null, router.Version).Supported, "The copy was described for a caller in WSL.");
            foreach (var inside in new[] { @"\\wsl.localhost\Ubuntu\home\me\repo", @"\\wsl$\Ubuntu\home\me\repo", "//wsl.localhost/Ubuntu/home/me/repo" })
            {
                var refused = Dispatch.Describe(router, Model, Dispatch.Modify, inside, "windows", null, router.Version);
                Assert(!refused.Supported && refused.Reason.Contains("Windows folders only"), "A workspace inside WSL was accepted: " + inside);
            }
            var lost = ManagedAccess(ScanOf(Home(), false), "openrouter");
            lost.ConfigFolder = null;
            Assert(!Dispatch.Describe(lost, Model, Dispatch.Modify, workspace, "windows", null, lost.Version).Supported, "The copy was described without its configuration folder.");
            var other = Dispatch.Describe(router, Model, Dispatch.Modify, workspace, "windows", null, "1.6.0");
            Assert(other.Supported && !other.VersionVerified, "Another version of the copy counted as verified.");

            // The managed place adds to the shared output and notes, after them.
            Assert(modify.Output["outcome"].Contains("is 1 when Pi cannot make a request") && modify.Output["end"].Contains("agent_settled"),
                "The output does not explain the exit code 1, or lost the shared entries.");
            var pi = Agents.Get("pi");
            int lastShared = pi.Dispatch.Notes.Max(n => modify.Notes.IndexOf(n));
            int firstManaged = pi.PlaceFor(ManagedPi.Place).Notes.Min(n => modify.Notes.IndexOf(n));
            Assert(pi.Dispatch.Notes.All(n => modify.Notes.Contains(n)) && firstManaged > lastShared, "The managed notes are missing or come before the shared ones.");
            Assert(modify.Notes.Any(n => n.Contains("BYOK")), "OpenRouter's BYOK note is missing.");

            // The built-in WSL place keeps the shared arguments, without PowerShell.
            Assert(pi.PlaceFor("wsl").Arguments == null && pi.PlaceFor("wsl").Commands.Count == 0 && pi.PlaceFor("wsl").Alternatives.Count == 0, "The WSL place gained the managed copy's entries.");
            var wsl = Candidates.Access("openrouter", "pi", "wsl", RouterKey);
            wsl.Provider = "openrouter";
            wsl.Program = "/home/me/.npm-global/bin/pi";
            var inWsl = Dispatch.Describe(wsl, Model, Dispatch.Modify, "/home/me/project", "wsl", "Ubuntu-24.04", "1.4.0");
            Assert(inWsl.Supported && !inWsl.Managed && !inWsl.Arguments.Any(a => a.Contains("powershell")) && !inWsl.Arguments.Contains("--tools"),
                "Pi in WSL changed: " + string.Join(" ", inWsl.Arguments));
            Assert(inWsl.Commands == null && inWsl.Alternatives.Count == 0 && !inWsl.Environment.ContainsKey("PI_CODING_AGENT_DIR"), "Pi in WSL was given the managed copy's settings.");
            Assert(!inWsl.Output["outcome"].Contains("is 1 when"), "Pi in WSL was given the managed copy's output.");
        }

        // ------------------------------------------------------------ MCP

        /// <summary>A tool's raw answer line and its structured content.</summary>
        private static Dictionary<string, object> Call(McpServer server, string tool, string arguments, out string raw)
        {
            raw = server.Handle("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/call\",\"params\":{\"name\":\"" + tool + "\",\"arguments\":" + arguments + "}}");
            return Json.Obj(Json.Obj(Json.Parse(raw), "result"), "structuredContent");
        }

        private static List<Dictionary<string, object>> Items(Dictionary<string, object> d, string key)
        {
            var list = d.ContainsKey(key) ? d[key] as IList : null;
            return list == null ? new List<Dictionary<string, object>>() : list.OfType<Dictionary<string, object>>().ToList();
        }

        private static bool IsManaged(Dictionary<string, object> runtime) { return runtime != null && runtime.ContainsKey("managed") && Json.Bool(runtime, "managed"); }

        /// <summary>The entry of runtimes.managed for a place, or null; the list must hold one entry per place at most.</summary>
        private static Dictionary<string, object> ManagedEntry(Dictionary<string, object> listed, string where)
        {
            var entries = Items(Json.Obj(listed, "runtimes"), "managed");
            Assert(entries.Select(e => Json.Str(e, "where")).Distinct().Count() == entries.Count, "runtimes.managed lists a place twice.");
            return entries.SingleOrDefault(e => Json.Str(e, "where") == where);
        }

        private static void McpShowsManagedCopy()
        {
            NetworkBackoff.Clear();
            Settings.Current.WslDistro = "Ubuntu-24.04";
            string fakeBash = FakeGitBash();
            ManagedPi.LocateGitBash = () => fakeBash;
            var snap = Fixture();
            string home = Home();
            var server = new McpServer(() => snap, () => false, null, null, () => ScanOf(home, false), new LeaseBook(null));
            string raw;
            var answers = new List<string>();

            // Not installed: the entry says so, with no services, and there is no candidate. WSL is not running here, so
            // the entry for the copy inside WSL is unreachable and not installed.
            var before = Call(server, "list_candidates", "{}", out raw);
            var block = ManagedEntry(before, "windows");
            Assert(block != null && !Json.Bool(block, "installed") && Json.Bool(block, "reachable") && Json.Str(block, "version") == ManagedPi.Version && Items(before, "candidates").Count == 0,
                "runtimes.managed does not say the copy is not installed.");
            Assert(((IList)block["services"]).Count == 0 && Json.Str(block, "set_up_in") != null, "runtimes.managed lists services, or not where to set it up.");
            var stopped = ManagedEntry(before, "wsl");
            Assert(stopped != null && Json.Str(stopped, "distro") == "Ubuntu-24.04" && !Json.Bool(stopped, "reachable") && !Json.Bool(stopped, "installed") && ((IList)stopped["services"]).Count == 0,
                "runtimes.managed does not list the copy inside a stopped WSL as unreachable: " + raw);
            var environment = Json.Obj(Json.Obj(before, "runtimes"), "environment");
            Assert(environment != null && Json.Str(environment, "wsl") == "stopped" && Json.Str(environment, "distro") == "Ubuntu-24.04" && Json.Bool(environment, "git_for_windows"),
                "runtimes.environment does not say that WSL is stopped and Git for Windows found: " + raw);

            InstallWithKeys();
            var listed = Call(server, "list_candidates", "{}", out raw);
            answers.Add(raw);
            var candidate = Items(listed, "candidates").Single();
            Assert(Json.Str(candidate, "service") == "openrouter" && IsManaged(Json.Obj(candidate, "runtime")) && Json.Str(Json.Obj(candidate, "runtime"), "where") == "windows",
                "The candidate does not say it runs on the managed copy.");
            Assert(Items(Json.Obj(listed, "runtimes"), "found").Count(IsManaged) == 1, "runtimes.found does not list the managed copy.");
            block = ManagedEntry(listed, "windows");
            Assert(Json.Bool(block, "installed") && ((IList)block["services"]).Cast<string>().SequenceEqual(new[] { "gemini", "openrouter" }),
                "runtimes.managed does not list the installed copy's services.");
            Assert(Json.Bool(Json.Obj(listed, "runtimes"), "key_discovery") == false, "The fixture should run with key discovery off.");

            var fromWsl = Call(server, "list_candidates", "{\"from\":\"wsl\"}", out raw);
            answers.Add(raw);
            Assert(!Items(fromWsl, "candidates").Any(c => IsManaged(Json.Obj(c, "runtime"))), "A caller in WSL was offered the managed copy.");

            string candidateArgs = "{\"service\":\"openrouter\",\"model\":\"" + Model + "\",\"runtime\":\"pi\"";
            var described = Call(server, "describe_dispatch", candidateArgs + ",\"managed\":true,\"mode\":\"modify\",\"workspace\":\"C:\\\\w\"}", out raw);
            answers.Add(raw);
            Assert(Json.Bool(described, "supported") && Json.Bool(described, "managed") && Json.Str(described, "program") == ManagedPi.Windows.ProgramPath, "describe_dispatch did not describe the copy: " + raw);
            Assert(Json.Str(described, "managed_config") == ManagedPi.Windows.ConfigFolder, "describe_dispatch does not name the copy's configuration folder as managed_config: " + raw);
            Assert(Json.Str(described, "commands") == "powershell" && Json.Str(Json.Obj(described, "environment"), "PI_CODING_AGENT_DIR") == ManagedPi.Windows.ConfigFolder,
                "describe_dispatch lacks the copy's shell or configuration folder.");
            var alternative = Items(described, "alternatives").Single();
            Assert(Json.Str(alternative, "shell") == "git_bash" && ((IList)alternative["arguments"]).Cast<string>().Contains("read,bash,edit,write"), "describe_dispatch lacks the git_bash alternative.");
            // Only with managed: true: a plugin that does not know the copy never gets it.
            var implicitly = Call(server, "describe_dispatch", candidateArgs + "}", out raw);
            answers.Add(raw);
            Assert(Json.Str(implicitly, "error") != null && Json.Str(implicitly, "error").Contains("No usable Pi is set up here") && !implicitly.ContainsKey("program"),
                "Without managed, describe_dispatch described the copy: " + raw);
            var unclaimed = Call(server, "claim_capacity", candidateArgs + ",\"client\":\"claude-code\"}", out raw);
            answers.Add(raw);
            Assert(Json.Str(unclaimed, "error") != null && Json.Str(unclaimed, "error").Contains("No usable Pi is set up here") && Json.Str(unclaimed, "lease_id") == null,
                "Without managed, claim_capacity leased the copy: " + raw);
            var tools = Items(Json.Obj(Json.Parse(server.Handle("{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/list\"}")), "result"), "tools");
            foreach (var name in new[] { "describe_dispatch", "claim_capacity" })
            {
                var properties = Json.Obj(Json.Obj(tools.Single(t => Json.Str(t, "name") == name), "inputSchema"), "properties");
                Assert(properties != null && properties.ContainsKey("managed"), name + "'s schema does not list managed.");
            }
            var refused = Call(server, "describe_dispatch", candidateArgs + ",\"managed\":true,\"workspace\":\"\\\\\\\\wsl.localhost\\\\Ubuntu\\\\home\\\\me\"}", out raw);
            Assert(refused.ContainsKey("supported") && !Json.Bool(refused, "supported") && Json.Str(refused, "reason").Contains("Windows folders only"), "A workspace inside WSL was described: " + raw);
            var notMine = Call(server, "describe_dispatch", candidateArgs + ",\"managed\":false}", out raw);
            Assert(notMine.ContainsKey("error"), "managed: false was described without a Pi of the user's.");

            var claim = Call(server, "claim_capacity", candidateArgs + ",\"managed\":true,\"client\":\"claude-code\"}", out raw);
            answers.Add(raw);
            Assert(Json.Str(claim, "lease_id") != null && Json.Str(claim, "pool") == "openrouter:free", "No lease was granted for the managed candidate: " + raw);

            string keyFile = File.ReadAllText(KeyFile);
            foreach (var answer in answers)
                Assert(!answer.Contains(RouterKey) && !answer.Contains(GeminiKey) && !answer.Contains(keyFile) && !answer.Contains("api_key"), "An answer reveals a key or the key file.");
            Assert(answers[2].Contains(Json.Serialize(ManagedPi.Windows.ConfigFolder).Trim('"')), "The configuration folder should be in the description.");
        }

        // ------------------------------------------------------------ hardening

        private static void InstallClearsStalePartials()
        {
            // Left by an earlier attempt, or put there by another program: neither may be unpacked nor promoted.
            string staleFolder = Path.Combine(ManagedPi.Windows.Root, ManagedPi.Version + ".partial");
            string staleArchive = Path.Combine(ManagedPi.Windows.Root, ManagedPi.Version + ".zip.partial");
            Directory.CreateDirectory(staleFolder);
            File.WriteAllText(Path.Combine(staleFolder, "planted.exe"), "planted");
            File.WriteAllBytes(staleArchive, Archive("pi.exe", "planted Pi"));

            Install(Good, ReleaseOf(Good));
            Assert(ManagedPi.Windows.Installed && File.ReadAllText(ManagedPi.Windows.ProgramPath) == "not really Pi", "The verified archive was not the one installed.");
            Assert(!Directory.Exists(staleFolder) && !File.Exists(staleArchive), "A stale partial folder or archive was left.");
            Assert(!File.Exists(Path.Combine(Path.GetDirectoryName(ManagedPi.Windows.ProgramPath), "planted.exe")), "A stale partial folder's file reached the installed copy.");
            Assert(Partials().Count == 0, "A partial file was left: " + string.Join(", ", Partials()));
        }

        private static string PendingKeyFile { get { return KeyFile + ".new"; } }

        private static void KeysAreRevokedAndLeaveNothing()
        {
            var settings = Settings.Current;
            ApiKeys.Save("openrouter", RouterKey);
            ApiKeys.Save("gemini", GeminiKey);
            string routerOnly = "{\"openrouter\":{\"type\":\"api_key\",\"key\":\"" + RouterKey + "\"}}";

            // Not installed: a key file left behind, and a half-written one, go at the next sync.
            Directory.CreateDirectory(ManagedPi.Windows.ConfigFolder);
            File.WriteAllText(KeyFile, routerOnly);
            File.WriteAllText(PendingKeyFile, routerOnly);
            settings.ManagedPiServices = new List<string> { "openrouter" };
            Assert(ManagedPi.Sync(settings) && !File.Exists(KeyFile) && !File.Exists(PendingKeyFile), "A key file was left beside a copy that is not installed.");

            Install(Good, ReleaseOf(Good));
            Assert(ManagedPi.Sync(settings) && File.ReadAllText(KeyFile) == routerOnly, "OpenRouter's key was not copied.");

            // A half-written file goes when the key file is already up to date, and when it is rewritten.
            File.WriteAllText(PendingKeyFile, "half");
            Assert(ManagedPi.Sync(settings) && !File.Exists(PendingKeyFile) && File.ReadAllText(KeyFile) == routerOnly, "An unchanged sync left auth.json.new.");
            File.WriteAllText(PendingKeyFile, "half");
            settings.ManagedPiServices = new List<string> { "openrouter", "gemini" };
            Assert(ManagedPi.Sync(settings) && !File.Exists(PendingKeyFile) && File.ReadAllText(KeyFile).Contains(GeminiKey), "A rewriting sync left auth.json.new.");

            // Unticked, the key still in the file (as when its deletion failed): no access for that service.
            settings.ManagedPiServices = new List<string> { "gemini" };
            Assert(File.ReadAllText(KeyFile).Contains(RouterKey), "The test needs OpenRouter's key still in the file.");
            var managed = ScanOf(Home(), false).Access.Where(a => a.Managed).Select(a => a.Service).ToList();
            Assert(managed.SequenceEqual(new[] { "gemini" }), "An unticked service kept its access: " + string.Join(", ", managed));
            string provider;
            Assert(ManagedPi.Windows.KeyFor(Definitions.Get("openrouter"), out provider) == null, "KeyFor gives the key of an unticked service.");

            // Nothing ticked: the key file and a half-written one both go.
            File.WriteAllText(PendingKeyFile, "half");
            settings.ManagedPiServices = new List<string>();
            Assert(ManagedPi.Sync(settings) && !File.Exists(KeyFile) && !File.Exists(PendingKeyFile), "Unticking every service left a key file.");

            // Remove deletes the keys first: when the program is in use the keys are gone, the half-written file too.
            settings.ManagedPiServices = new List<string> { "openrouter" };
            ManagedPi.Sync(settings);
            File.WriteAllText(PendingKeyFile, "half");
            using (new FileStream(ManagedPi.Windows.ProgramPath, FileMode.Open, FileAccess.Read, FileShare.None))
                Fails<ManagedPi.InstallException>(() => ManagedPi.Windows.Remove(settings), "the program in use");
            Assert(!File.Exists(KeyFile) && !File.Exists(PendingKeyFile), "Remove left a key file when the program could not be deleted.");
            Assert(File.Exists(ManagedPi.Windows.ProgramPath), "The test needs the program to have survived.");
            Assert(Revision() % 2 == 1, "A Remove that stopped half-way did not leave the count odd: " + Revision());

            // When the key file itself cannot be deleted, the copy and the choices stay as they were.
            settings.ManagedPiServices = new List<string> { "openrouter" };
            Assert(ManagedPi.Sync(settings) && Revision() % 2 == 0, "The key was not copied again after a failed Remove: " + Revision());
            using (new FileStream(KeyFile, FileMode.Open, FileAccess.Read, FileShare.None))
                Fails<ManagedPi.InstallException>(() => ManagedPi.Windows.Remove(settings), "the key file in use");
            Assert(File.Exists(ManagedPi.Windows.ProgramPath) && settings.ManagedPiServices.SequenceEqual(new[] { "openrouter" }), "Remove went on after the key file could not be deleted.");
            long before = Revision();
            ManagedPi.Windows.Remove(settings);
            AssertRemoved(before, "Remove");
        }

        private static void DescribesWithoutGitBash()
        {
            NetworkBackoff.Clear();
            string workspace = @"C:\src\project";
            InstallWithKeys();
            Assert(!File.Exists(ShellSettingsFile) && ManagedPi.Windows.BoundShells.Length == 0, "A shell was bound without Git Bash.");
            var router = ManagedAccess(ScanOf(Home(), false), "openrouter");
            Assert(router.Shells.Length == 0, "The access has a shell without Git Bash.");
            var plain = Dispatch.Describe(router, Model, Dispatch.Modify, workspace, "windows", null, router.Version);
            Assert(plain.Supported && plain.Commands == "powershell" && plain.Alternatives.Count == 0, "The git_bash alternative was offered without Git Bash.");

            var snap = Fixture();
            string home = Home();
            var server = new McpServer(() => snap, () => false, null, null, () => ScanOf(home, false), new LeaseBook(null));
            string raw;
            var described = Call(server, "describe_dispatch", "{\"service\":\"openrouter\",\"model\":\"" + Model + "\",\"runtime\":\"pi\",\"managed\":true,\"mode\":\"modify\",\"workspace\":\"C:\\\\w\"}", out raw);
            Assert(Json.Bool(described, "supported") && Json.Str(described, "commands") == "powershell" && !described.ContainsKey("alternatives"),
                "describe_dispatch offered an alternative without Git Bash: " + raw);

            // Bound, then the bash.exe it names is deleted: no alternative from the next scan on.
            string fakeBash = FakeGitBash();
            ManagedPi.LocateGitBash = () => fakeBash;
            Assert(ManagedPi.Sync(Settings.Current) && ManagedPi.Windows.BoundShells.SequenceEqual(new[] { "git_bash" }), "Git Bash was not bound by a sync.");
            Assert(Dispatch.Describe(ManagedAccess(ScanOf(Home(), false), "openrouter"), Model, Dispatch.Modify, workspace, "windows", null, router.Version).Alternatives.ContainsKey("git_bash"),
                "The git_bash alternative was not offered once bound.");
            File.Delete(fakeBash);
            Assert(ManagedPi.Windows.BoundShells.Length == 0, "A bash.exe that is gone still counts as bound.");
            var gone = ManagedAccess(ScanOf(Home(), false), "openrouter");
            var after = Dispatch.Describe(gone, Model, Dispatch.Modify, workspace, "windows", null, gone.Version);
            Assert(gone.Shells.Length == 0 && after.Alternatives.Count == 0 && after.Commands == "powershell", "The git_bash alternative outlived its bash.exe.");

            // Git Bash no longer found: the binding stays, so that a task already described fails in Pi rather than
            // searching for another bash.exe, and no new task is offered the bash tool.
            ManagedPi.LocateGitBash = () => null;
            Assert(ManagedPi.Sync(Settings.Current) && Json.Str(Json.ParseFile(ShellSettingsFile), "shellPath") == fakeBash, "The binding was taken back when Git Bash was no longer found.");
            Assert(ManagedPi.Windows.BoundShells.Length == 0, "A binding to a missing bash.exe counts as a shell.");

            // Another Git Bash found later replaces the binding.
            string otherBash = FakeGitBash();
            ManagedPi.LocateGitBash = () => otherBash;
            Assert(ManagedPi.Sync(Settings.Current) && Json.Str(Json.ParseFile(ShellSettingsFile), "shellPath") == otherBash, "A Git Bash found later did not replace the binding.");
            Assert(ManagedPi.Windows.BoundShells.SequenceEqual(new[] { "git_bash" }) && ManagedAccess(ScanOf(Home(), false), "openrouter").Shells.SequenceEqual(new[] { "git_bash" }),
                "The new binding is not offered.");
        }

        private static void FindsGitBash()
        {
            Func<Dictionary<string, string>, Func<string, string>> variables = values => name => { string v; return values.TryGetValue(name, out v) ? v : null; };
            Func<string[], Func<string, bool>> files = paths => { var set = new HashSet<string>(paths, StringComparer.OrdinalIgnoreCase); return path => set.Contains(path); };

            string standard = ManagedPi.FindGitBash(variables(new Dictionary<string, string> { { "ProgramFiles", @"C:\Program Files" }, { "SystemRoot", @"C:\Windows" } }),
                files(new[] { @"C:\Program Files\Git\bin\bash.exe" }));
            Assert(standard == @"C:\Program Files\Git\bin\bash.exe", "Git Bash in Program Files was not found: " + standard);

            string onPath = ManagedPi.FindGitBash(variables(new Dictionary<string, string> { { "PATH", @"C:\Tools;C:\Tools\Git\cmd" }, { "SystemRoot", @"C:\Windows" } }),
                files(new[] { @"C:\Tools\Git\cmd\git.exe", @"C:\Tools\Git\bin\bash.exe" }));
            Assert(onPath == @"C:\Tools\Git\bin\bash.exe", "Git Bash beside git.exe on PATH was not found: " + onPath);

            Assert(ManagedPi.FindGitBash(variables(new Dictionary<string, string> { { "ProgramFiles", @"C:\Program Files" }, { "PATH", @"C:\Tools" } }), files(new string[0])) == null,
                "A Git Bash was found where there is none.");

            // WSL's launcher: on PATH without git.exe, and never through a git.exe placed in the Windows folder.
            Assert(ManagedPi.FindGitBash(variables(new Dictionary<string, string> { { "PATH", @"C:\Windows\System32" }, { "SystemRoot", @"C:\Windows" } }),
                files(new[] { @"C:\Windows\System32\bash.exe" })) == null, "WSL's bash.exe was taken for Git Bash.");
            foreach (var systemRoot in new[] { @"C:\Windows", null })
            {
                var values = new Dictionary<string, string> { { "PATH", @"C:\Windows\System32" } };
                if (systemRoot != null) values["SystemRoot"] = systemRoot;
                Assert(ManagedPi.FindGitBash(variables(values), files(new[] { @"C:\Windows\System32\git.exe", @"C:\Windows\bin\bash.exe" })) == null,
                    "A bash.exe under the Windows folder was found through a git.exe there (SystemRoot " + (systemRoot ?? "unset") + ").");
            }

            // Malformed PATH entries are skipped, and the search goes on.
            string malformed = null;
            Func<string, bool> anyGit = path => path.EndsWith("git.exe", StringComparison.OrdinalIgnoreCase) || string.Equals(path, @"C:\Tools\Git\bin\bash.exe", StringComparison.OrdinalIgnoreCase);
            try
            {
                malformed = ManagedPi.FindGitBash(variables(new Dictionary<string, string> { { "PATH", "C:\\bad|path;C:\\a:b:c;\"\";  ;C:\\x<y>;\"C:\\Tools\\Git\\cmd\"" }, { "SystemRoot", @"C:\Windows" } }), anyGit);
            }
            catch (Exception e) { throw new Exception("A malformed PATH entry threw: " + e.GetType().Name + ": " + e.Message); }
            Assert(malformed == @"C:\Tools\Git\bin\bash.exe", "The search stopped at a malformed PATH entry: " + malformed);
        }

        private static void OwnAgentBeforeManagedCopy()
        {
            NetworkBackoff.Clear();
            var opencode = Agents.Get("opencode");
            var pi = Agents.Get("pi");
            Assert(opencode != null && pi != null && opencode.RequestTokens > pi.RequestTokens,
                "The case needs OpenCode to send more tokens per request than Pi, as agents/*.json say.");

            var scan = new RuntimeScan();
            scan.Found.Add(new RuntimePlace { Runtime = "opencode", Where = "windows" });
            scan.Found.Add(new RuntimePlace { Runtime = "pi", Where = "windows", Managed = true });
            scan.Access.Add(Candidates.Access("openrouter", "opencode", "windows", RouterKey));
            var managed = Candidates.Access("openrouter", "pi", "windows", RouterKey);
            managed.Managed = true;
            managed.Provider = "openrouter";
            managed.Program = ManagedPi.Windows.ProgramPath;
            managed.Version = ManagedPi.Version;
            managed.ConfigFolder = ManagedPi.Windows.ConfigFolder;
            scan.Access.Add(managed);

            foreach (var from in new[] { null, "windows" })
            {
                var chosen = Candidates.Select(Fixture(), scan, new Requirements { Service = "openrouter", From = from }, DateTime.Now).Chosen.Where(c => c.Model.Id == Model).ToList();
                Assert(chosen.Count == 2 && chosen[0].Access.Runtime == "opencode" && !chosen[0].Access.Managed && chosen[1].Access.Managed,
                    "The user's own OpenCode does not come before the managed Pi (from " + (from ?? "nowhere") + "): " + string.Join(", ", chosen.Select(c => c.Access.Runtime + (c.Access.Managed ? "/managed" : ""))));
            }
        }

        private static void StoreBuildDoesNotOffer()
        {
            // Installed while not packaged, so that the folder lies under the test folder and looks installed.
            InstallWithKeys();
            string home = Home();
            Assert(ManagedPi.Windows.Installed && HasManaged(ScanOf(home, false)), "The test needs an installed copy first.");
            var snap = Fixture();
            var server = new McpServer(() => snap, () => false, null, null, () => ScanOf(home, false), new LeaseBook(null));
            try
            {
                Native.PackagedOverride = true;
                Assert(!ManagedPi.Offered, "The Store build offers the managed copy.");
                Assert(File.Exists(Path.Combine(Settings.Folder, "managed", "pi", ManagedPi.Version, "pi.exe")), "The installed-looking folder is gone.");
                Assert(!ManagedPi.Windows.Installed, "The Store build reports a copy on disk as installed.");
                Assert(!HasManaged(ScanOf(home, false)) && !HasManaged(ScanOf(home, true)), "The Store build's scan offers the managed copy.");
                string raw;
                var listed = Call(server, "list_candidates", "{}", out raw);
                Assert(!Json.Obj(listed, "runtimes").ContainsKey("managed"), "The Store build's list_candidates has runtimes.managed: " + raw);
                Assert(!Items(listed, "candidates").Any(c => IsManaged(Json.Obj(c, "runtime"))), "The Store build lists a managed candidate.");
            }
            finally { Native.PackagedOverride = false; }
        }

        private static bool OffersManaged(string service)
        {
            return ScanOf(Home(), false).Access.Any(a => a.Managed && a.Service == service);
        }

        private static void CopiesPlainKeysOnly()
        {
            var settings = Settings.Current;
            var router = Definitions.Get("openrouter");
            string provider;
            Install(Good, ReleaseOf(Good));
            settings.ManagedPiServices = new List<string> { "openrouter" };
            ApiKeys.Save("openrouter", RouterKey);
            Assert(ManagedPi.Sync(settings) && File.ReadAllText(KeyFile).Contains(RouterKey) && OffersManaged("openrouter"), "A plain key was not copied.");

            // Pi would run a value starting with "!" as a command, and may read others from the environment.
            var notPlain = new[] { "!echo sk-or-v1-0123456789", "$OPENROUTER_API_KEY", "prefix-${LABEL}-suffix", "abc$$def12", "sk-or-v1-0123456789$", "sk-or v1-0123456789", "sk-or\tv1-0123456789", "sk-or\nv1-0123456789",
                "sk-or\u0001v1-0123456789", "sk-or\u00a0v1-0123456789", "1234567" };
            foreach (var bad in notPlain)
            {
                string shown = bad.Replace("\t", "\\t").Replace("\n", "\\n").Replace("\u0001", "\\u0001").Replace("\u00a0", "\\u00a0");
                ApiKeys.Save("openrouter", RouterKey);
                Assert(ManagedPi.Sync(settings) && File.Exists(KeyFile), "The plain key was not copied back before " + shown + ".");
                ApiKeys.Save("openrouter", bad);
                Assert(!ManagedPi.ServicesWithKey().Any(d => d.Id == "openrouter"), "A service whose key is not plain can be copied: " + shown);
                Assert(ManagedPi.Sync(settings) && !File.Exists(KeyFile), "A key that is not plain was copied, or the earlier entry stayed: " + shown);
                Assert(!OffersManaged("openrouter") && ManagedPi.Windows.KeyFor(router, out provider) == null, "A key that is not plain gives access: " + shown);
            }

            // Saved keys are trimmed, and eight characters are enough.
            ApiKeys.Save("openrouter", "  " + RouterKey + " \t");
            Assert(ManagedPi.Sync(settings) && File.ReadAllText(KeyFile) == "{\"openrouter\":{\"type\":\"api_key\",\"key\":\"" + RouterKey + "\"}}", "A key with white space around it was not copied trimmed.");
            ApiKeys.Save("openrouter", "12345678");
            Assert(ManagedPi.Sync(settings) && File.ReadAllText(KeyFile).Contains("\"12345678\"") && OffersManaged("openrouter"), "A plain key of eight characters was refused.");

            // Changed in AI Meter while the file could not be rewritten: the file's key gives no access. Not found at all:
            // the copy's own key stands.
            ApiKeys.Save("openrouter", RouterKey);
            Assert(ManagedPi.Sync(settings) && OffersManaged("openrouter"), "The key was not copied again.");
            ApiKeys.Save("openrouter", RouterKey + "-rotated");
            Assert(File.ReadAllText(KeyFile).Contains(RouterKey) && !OffersManaged("openrouter") && ManagedPi.Windows.KeyFor(router, out provider) == null,
                "A key replaced in AI Meter still gives access through the file.");
            Assert(ManagedPi.Sync(settings) && File.ReadAllText(KeyFile).Contains(RouterKey + "-rotated") && OffersManaged("openrouter"), "The rotated key was not copied.");
            ApiKeys.Remove("openrouter");
            Assert(OffersManaged("openrouter") && ManagedPi.Windows.KeyFor(router, out provider) == RouterKey + "-rotated", "The copy's own key was refused while AI Meter finds none.");
            Assert(ManagedPi.Sync(settings) && File.ReadAllText(KeyFile).Contains(RouterKey + "-rotated"), "A sync withdrew a key AI Meter cannot find now.");
            ApiKeys.Save("openrouter", "!echo found-again-but-not-plain");
            Assert(ManagedPi.Sync(settings) && !File.Exists(KeyFile) && !OffersManaged("openrouter"), "A key found again but not plain did not remove the entry.");
        }

        private static void RemoveAndSyncTakeTurns()
        {
            var settings = Settings.Current;
            ApiKeys.Save("openrouter", RouterKey);
            long removed = 0;
            for (int round = 0; round < 5; round++)
            {
                // A new copy continues the count Remove left: it never goes back.
                Install(Good, ReleaseOf(Good));
                settings.ManagedPiServices = new List<string> { "openrouter" };
                Assert(ManagedPi.Sync(settings) && File.Exists(KeyFile), "The key was not copied in round " + round + ".");
                Assert(Revision() == removed + 2, "The count did not continue from " + removed + " in round " + round + ": " + Revision());
                long before = Revision();

                Exception failure = null;
                var syncing = new System.Threading.Thread(() =>
                {
                    try { for (int i = 0; i < 50; i++) ManagedPi.Sync(settings); }
                    catch (Exception e) { failure = e; }
                });
                syncing.Start();
                ManagedPi.Windows.Remove(settings);
                syncing.Join();
                Assert(failure == null, "A sync beside Remove threw: " + (failure == null ? "" : failure.Message));
                AssertRemoved(before, "Remove in round " + round);
                Assert(settings.ManagedPiServices.Count == 0 && !ManagedPi.Windows.Installed, "Remove did not clear the choices or the copy in round " + round + ".");
                removed = Revision();
                Assert(ManagedPi.Sync(settings) && Revision() == removed, "A sync after Remove moved the count in round " + round + ".");
                AssertRemoved(before, "a sync after Remove in round " + round);
            }
        }

        private static void SavingSettingsReportsFailure()
        {
            string file = Path.Combine(Settings.Folder, "settings.json");
            Assert(Settings.Current.Save() && File.Exists(file), "A normal save did not report success.");
            using (new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.None))
                Assert(!Settings.Current.Save(), "A save over a settings file held by another handle reported success.");
            Assert(Settings.Current.Save(), "A save once the file was free again did not report success.");
        }

        private static void ExtendedWslPathsAndEnvironmentPolicy()
        {
            NetworkBackoff.Clear();
            foreach (var inside in new[] { @"\\?\UNC\wsl.localhost\Ubuntu\home\me\repo", @"\\?\UNC\wsl$\Ubuntu\home\me\repo", @"\\?\unc\WSL.LOCALHOST\Ubuntu\home\me" })
                Assert(Dispatch.InsideWsl(inside), "An extended path into WSL was not recognised: " + inside);
            foreach (var outside in new[] { @"\\?\C:\work", @"C:\work", @"\\?\UNC\server\share\work" })
                Assert(!Dispatch.InsideWsl(outside), "A Windows path was taken for WSL: " + outside);

            InstallWithKeys();
            var router = ManagedAccess(ScanOf(Home(), false), "openrouter");
            foreach (var inside in new[] { @"\\?\UNC\wsl.localhost\Ubuntu\home\me\repo", @"\\?\UNC\wsl$\Ubuntu\home\me\repo" })
            {
                var refused = Dispatch.Describe(router, Model, Dispatch.Modify, inside, "windows", null, router.Version);
                Assert(!refused.Supported && refused.Reason.Contains("Windows folders only"), "An extended workspace inside WSL was accepted: " + inside);
            }
            var extendedDrive = Dispatch.Describe(router, Model, Dispatch.Modify, @"\\?\C:\work", "windows", null, router.Version);
            Assert(extendedDrive.Supported, "An extended path on a drive was refused: " + extendedDrive.Reason);

            var modify = Dispatch.Describe(router, Model, Dispatch.Modify, @"C:\work", "windows", null, router.Version);
            Assert(modify.EnvironmentPolicy == "no_credentials" && !modify.CleanEnvironment, "The managed copy may inherit the plugin's credentials: " + (modify.EnvironmentPolicy ?? "null"));

            var snap = Fixture();
            string home = Home();
            var server = new McpServer(() => snap, () => false, null, null, () => ScanOf(home, false), new LeaseBook(null));
            string raw;
            var described = Call(server, "describe_dispatch", "{\"service\":\"openrouter\",\"model\":\"" + Model + "\",\"runtime\":\"pi\",\"managed\":true,\"mode\":\"modify\",\"workspace\":\"C:\\\\w\"}", out raw);
            Assert(Json.Str(described, "environment_policy") == "no_credentials", "describe_dispatch lacks the no_credentials policy: " + raw);

            // A plan agent keeps its clean environment; Pi in WSL has no policy.
            var claude = Candidates.PlanAccess("claude", "claude", "wsl", "Test-1");
            claude.Program = "/home/me/.local/bin/claude";
            claude.Home = "/home/me";
            var plan = Dispatch.Describe(claude, "sonnet", null, @"C:\work\repo", "windows", "Test-1", null);
            Assert(plan.Supported && plan.CleanEnvironment && plan.EnvironmentPolicy == "clean", "A plan agent lost its clean environment: " + plan.Reason + " " + (plan.EnvironmentPolicy ?? "null"));
            var wsl = Candidates.Access("openrouter", "pi", "wsl", RouterKey);
            wsl.Provider = "openrouter";
            wsl.Program = "/home/me/.npm-global/bin/pi";
            var inWsl = Dispatch.Describe(wsl, Model, Dispatch.Modify, "/home/me/project", "wsl", "Ubuntu-24.04", "1.4.0");
            Assert(inWsl.Supported && inWsl.EnvironmentPolicy == null && !inWsl.CleanEnvironment, "Pi in WSL was given an environment policy: " + (inWsl.EnvironmentPolicy ?? "null"));
        }

        private static void InstallOverOlderReleaseRestoresKeys()
        {
            var settings = Settings.Current;
            string older = Path.Combine(ManagedPi.Windows.Root, "1.0.0");
            Directory.CreateDirectory(older);
            File.WriteAllText(Path.Combine(older, "pi.exe"), "older Pi");
            Directory.CreateDirectory(ManagedPi.Windows.ConfigFolder);
            File.WriteAllText(KeyFile, "{\"openrouter\":{\"type\":\"api_key\",\"key\":\"" + RouterKey + "\"}}");
            ApiKeys.Save("openrouter", RouterKey);
            settings.ManagedPiServices = new List<string> { "openrouter" };
            Assert(!ManagedPi.Windows.Installed && ManagedPi.Windows.OtherVersionInstalled == "1.0.0", "The older release does not look like one.");

            // An older release is not the adopted one: its key file goes.
            Assert(ManagedPi.Sync(settings) && !File.Exists(KeyFile), "An older release kept its key file.");

            // As the controller does: install, then sync.
            Install(Good, ReleaseOf(Good));
            Assert(!Directory.Exists(older) && ManagedPi.Windows.OtherVersionInstalled == null, "The older release was left.");
            Assert(ManagedPi.Sync(settings) && File.ReadAllText(KeyFile).Contains(RouterKey) && OffersManaged("openrouter"), "The ticked service's key did not come back after the installation.");
        }

        /// <summary>The number in config\keys-revision; 0 when the file is missing.</summary>
        private static long Revision()
        {
            return File.Exists(RevisionFile) ? long.Parse(File.ReadAllText(RevisionFile).Trim(), System.Globalization.CultureInfo.InvariantCulture) : 0;
        }

        private static void KeyRevisionCountsChanges()
        {
            var settings = Settings.Current;
            Install(Good, ReleaseOf(Good));
            ApiKeys.Save("openrouter", RouterKey);
            Assert(Revision() == 0, "A copy without keys has a revision.");
            Assert(ManagedPi.Sync(settings) && Revision() == 0, "A sync with nothing ticked and no key file moved the revision.");

            // Each change raises the count twice: odd while under way, even once complete.
            settings.ManagedPiServices = new List<string> { "openrouter" };
            Assert(ManagedPi.Sync(settings) && File.Exists(KeyFile) && Revision() == 2, "Writing the key file did not count one complete change: " + Revision());
            Assert(ManagedPi.Sync(settings) && Revision() == 2, "A sync that changed nothing moved the revision: " + Revision());

            ApiKeys.Save("openrouter", RouterKey + "-rotated");
            Assert(ManagedPi.Sync(settings) && File.ReadAllText(KeyFile).Contains(RouterKey + "-rotated") && Revision() == 4, "Rewriting the key file did not count one complete change: " + Revision());

            settings.ManagedPiServices = new List<string>();
            Assert(ManagedPi.Sync(settings) && !File.Exists(KeyFile) && Revision() == 6, "Deleting the key file did not count one complete change: " + Revision());
            Assert(ManagedPi.Sync(settings) && Revision() == 6, "A sync with the key file already gone moved the revision: " + Revision());
            Assert(!File.Exists(PendingRevisionFile), "keys-revision.new was left behind.");
        }

        private static string RevisionFile { get { return Path.Combine(ManagedPi.Windows.ConfigFolder, "keys-revision"); } }
        private static string PendingRevisionFile { get { return RevisionFile + ".new"; } }

        private static void RevisionSurvivesFailures()
        {
            var settings = Settings.Current;
            Install(Good, ReleaseOf(Good));
            ApiKeys.Save("openrouter", RouterKey);
            settings.ManagedPiServices = new List<string> { "openrouter" };
            Assert(ManagedPi.Sync(settings) && Revision() == 2, "The key was not copied: " + Revision());
            string copied = File.ReadAllText(KeyFile);

            // (a) The count cannot be announced: the key file is not touched.
            ApiKeys.Save("gemini", GeminiKey);
            settings.ManagedPiServices = new List<string> { "openrouter", "gemini" };
            // FileShare.Read lets the count be read but not replaced, so the write through keys-revision.new is reached.
            var leftBehind = new List<string>();
            foreach (var share in new[] { FileShare.None, FileShare.Read })
            {
                using (new FileStream(RevisionFile, FileMode.Open, FileAccess.Read, share))
                    Assert(!ManagedPi.Sync(settings), "A sync succeeded although the count could not be announced (sharing " + share + ").");
                Assert(File.ReadAllText(KeyFile) == copied, "The key file changed before the change was announced (sharing " + share + ").");
                Assert(Revision() == 2, "The count moved although it could not be written (sharing " + share + "): " + Revision());
                if (File.Exists(PendingRevisionFile)) leftBehind.Add(share.ToString());
            }
            Assert(ManagedPi.Sync(settings) && File.ReadAllText(KeyFile).Contains(GeminiKey) && Revision() == 4, "The change was not made once the count was free: " + Revision());
            Assert(!File.Exists(PendingRevisionFile), "keys-revision.new was left behind.");

            // (b) The key file cannot be replaced: the count stays odd, the old keys stay, no second copy is left.
            copied = File.ReadAllText(KeyFile);
            settings.ManagedPiServices = new List<string> { "openrouter" };
            using (new FileStream(KeyFile, FileMode.Open, FileAccess.Read, FileShare.Read))
                Assert(!ManagedPi.Sync(settings), "A sync succeeded although the key file could not be replaced.");
            Assert(Revision() == 5, "A failed change did not leave the count odd: " + Revision());
            Assert(File.ReadAllText(KeyFile) == copied && !File.Exists(PendingKeyFile), "A failed change altered the key file or left auth.json.new.");

            // A change that follows an unfinished one, with other contents (every service unticked meanwhile), gets a
            // fresh odd number: 5, then 7 announced and 8 closed.
            settings.ManagedPiServices = new List<string>();
            Assert(ManagedPi.Sync(settings) && !File.Exists(KeyFile) && Revision() == 8, "A change after an unfinished one did not get a fresh odd number: " + Revision());

            // An unfinished change whose contents the file already has (the key put back meanwhile) closes at the next even.
            settings.ManagedPiServices = new List<string> { "openrouter" };
            Assert(ManagedPi.Sync(settings) && Revision() == 10, "The key was not copied again: " + Revision());
            copied = File.ReadAllText(KeyFile);
            ApiKeys.Save("openrouter", RouterKey + "-rotated");
            using (new FileStream(KeyFile, FileMode.Open, FileAccess.Read, FileShare.Read))
                Assert(!ManagedPi.Sync(settings), "A sync succeeded although the key file could not be replaced.");
            Assert(Revision() == 11 && File.ReadAllText(KeyFile) == copied, "A failed rotation did not leave the count odd and the file as it was: " + Revision());
            ApiKeys.Save("openrouter", RouterKey);
            Assert(ManagedPi.Sync(settings) && Revision() == 12 && File.ReadAllText(KeyFile) == copied, "An unfinished change the file already matches did not close at the next even: " + Revision());

            // (c) A count left odd by hand with the key file already right is closed by a sync that changes nothing else.
            File.WriteAllText(RevisionFile, "13");
            Assert(ManagedPi.Sync(settings) && Revision() == 14 && File.ReadAllText(KeyFile) == copied, "An odd count was not closed: " + Revision());
            Assert(ManagedPi.Sync(settings) && Revision() == 14, "A closed count moved again: " + Revision());

            // Last, so that the rest of the protocol is checked even while this fails.
            Assert(leftBehind.Count == 0, "keys-revision.new was left behind by a failed announcement (sharing " + string.Join(", ", leftBehind) + ").");
        }

        private static void RemoveKeepsOnlyTheCount()
        {
            var settings = Settings.Current;
            string fakeBash = FakeGitBash();
            ManagedPi.LocateGitBash = () => fakeBash;
            InstallWithKeys();
            Assert(File.Exists(ShellSettingsFile), "The test needs the copy's settings file.");
            File.WriteAllText(PendingKeyFile, "half");
            File.WriteAllText(Path.Combine(ManagedPi.Windows.Root, "stray.txt"), "x");
            Directory.CreateDirectory(Path.Combine(ManagedPi.Windows.Root, "other"));
            File.WriteAllText(Path.Combine(ManagedPi.Windows.Root, @"other\stray.txt"), "x");
            File.WriteAllText(Path.Combine(ManagedPi.Windows.ConfigFolder, "stray.txt"), "x");
            Directory.CreateDirectory(Path.Combine(ManagedPi.Windows.ConfigFolder, "sessions"));
            File.WriteAllText(Path.Combine(ManagedPi.Windows.ConfigFolder, @"sessions\task.jsonl"), "x");

            // A count left odd goes to the next odd number first, then to the even one after it.
            File.WriteAllText(RevisionFile, "5");
            ManagedPi.Windows.Remove(settings);
            AssertRemoved(5, "Remove");
            Assert(Revision() == 8, "Remove from an odd count did not end at the even number after the next odd one: " + Revision());

            Install(Good, ReleaseOf(Good));
            ApiKeys.Save("openrouter", RouterKey);
            settings.ManagedPiServices = new List<string> { "openrouter" };
            Assert(ManagedPi.Sync(settings) && File.Exists(KeyFile) && Revision() == 10, "A new copy did not continue the count: " + Revision());
        }

        private static void UnreadableKeyCountsAsNone()
        {
            var settings = Settings.Current;
            Install(Good, ReleaseOf(Good));
            ApiKeys.Save("openrouter", RouterKey);
            ApiKeys.Save("gemini", GeminiKey);
            settings.ManagedPiServices = new List<string> { "openrouter", "gemini" };
            Assert(ManagedPi.Sync(settings) && File.ReadAllText(KeyFile).Contains(RouterKey), "The keys were not copied.");

            string saved = Path.Combine(Settings.Folder, "keys", "openrouter.bin");
            Assert(File.Exists(saved), "The saved key is not where the test expects it: " + saved);
            var deny = new System.Security.AccessControl.FileSystemAccessRule(System.Security.Principal.WindowsIdentity.GetCurrent().User,
                System.Security.AccessControl.FileSystemRights.ReadData, System.Security.AccessControl.AccessControlType.Deny);
            var security = File.GetAccessControl(saved);
            security.AddAccessRule(deny);
            File.SetAccessControl(saved, security);
            try
            {
                bool denied = false;
                try { File.ReadAllBytes(saved); }
                catch (UnauthorizedAccessException) { denied = true; }
                Assert(denied, "The deny-read rule did not take effect, so the test cannot check an unreadable key.");
                Assert(ApiKeys.Has("openrouter") && ApiKeys.Get("openrouter") == null, "A saved key that cannot be read was not returned as none.");
                bool synced = false;
                try { synced = ManagedPi.Sync(settings); }
                catch (Exception e) { throw new Exception("Sync threw on an unreadable saved key: " + e.GetType().Name + ": " + e.Message); }
                // Like a key out of sight, an unreadable one is not the user taking it back: the copy's entry stands.
                Assert(synced && File.ReadAllText(KeyFile).Contains(RouterKey) && File.ReadAllText(KeyFile).Contains(GeminiKey),
                    "Sync withdrew a key AI Meter could not read, or another service's.");
                Assert(OffersManaged("openrouter") && OffersManaged("gemini"), "The copy's own key was refused while the saved one could not be read.");
            }
            finally
            {
                security = File.GetAccessControl(saved);
                security.RemoveAccessRule(deny);
                File.SetAccessControl(saved, security);
            }
        }

        private static void ManagedArgumentsSkipSkills()
        {
            string fakeBash = FakeGitBash();
            ManagedPi.LocateGitBash = () => fakeBash;
            InstallWithKeys();
            var router = ManagedAccess(ScanOf(Home(), false), "openrouter");
            var readOnly = Dispatch.Describe(router, Model, Dispatch.ReadOnly, @"C:\w", "windows", null, router.Version);
            var modify = Dispatch.Describe(router, Model, Dispatch.Modify, @"C:\w", "windows", null, router.Version);
            Assert(readOnly.Supported && modify.Supported && modify.Alternatives.ContainsKey("git_bash"), "The managed copy was not described with its alternative.");
            var lists = new Dictionary<string, List<string>> { { "read_only", readOnly.Arguments }, { "modify", modify.Arguments }, { "git_bash", modify.Alternatives["git_bash"] } };
            foreach (var list in lists)
                Assert(list.Value.Contains("--no-skills") && list.Value.Contains("--no-prompt-templates"),
                    "The managed copy's " + list.Key + " arguments load skills or prompt templates: " + string.Join(" ", list.Value));

            var wsl = Candidates.Access("openrouter", "pi", "wsl", RouterKey);
            wsl.Provider = "openrouter";
            wsl.Program = "/home/me/.npm-global/bin/pi";
            foreach (var mode in new[] { Dispatch.ReadOnly, Dispatch.Modify })
            {
                var inWsl = Dispatch.Describe(wsl, Model, mode, "/home/me/project", "wsl", "Ubuntu-24.04", "1.4.0");
                Assert(inWsl.Supported && !inWsl.Arguments.Contains("--no-skills") && !inWsl.Arguments.Contains("--no-prompt-templates"),
                    "Pi in WSL was given the managed copy's arguments: " + string.Join(" ", inWsl.Arguments));
            }
        }

        /// <summary>A definition of an agent on Windows, with an environment policy at the dispatch level, the place's, or neither.</summary>
        private static string PolicyAgent(string id, string dispatchPolicy, string placePolicy)
        {
            return "{ \"id\": \"" + id + "\", \"name\": \"Policy " + id + "\", \"programs\": { \"windows\": [\"" + id + "\"] }, \"folders\": [\"." + id + "\"], " +
                "\"dispatch\": { " + (dispatchPolicy == null ? "" : "\"environment_policy\": \"" + dispatchPolicy + "\", ") +
                "\"places\": { \"windows\": { \"versions\": [\"1.x\"]" + (placePolicy == null ? "" : ", \"environment_policy\": \"" + placePolicy + "\"") + " } }, " +
                "\"arguments\": { \"read_only\": [\"--model\", \"{model}\"], \"modify\": [\"--model\", \"{model}\"] } } }";
        }

        private static RuntimeAccess PolicyAccess(string id)
        {
            var access = Candidates.Access("openrouter", id, "windows", RouterKey);
            access.Provider = "openrouter";
            access.Program = @"C:\tools\" + id + ".exe";
            return access;
        }

        private static void UnknownEnvironmentPolicyIsRefused()
        {
            NetworkBackoff.Clear();
            string folder = Agents.UserFolder;
            Directory.CreateDirectory(folder);
            var agents = new Dictionary<string, string>
            {
                { "policyplace", PolicyAgent("policyplace", null, "no_credentails") },
                { "policydispatch", PolicyAgent("policydispatch", "no_credentails", null) },
                { "policyclean", PolicyAgent("policyclean", null, "clean") },
                { "policykept", PolicyAgent("policykept", "no_credentials", null) },
            };
            try
            {
                foreach (var a in agents) File.WriteAllText(Path.Combine(folder, a.Key + ".json"), a.Value);
                Agents.Reload();
                foreach (var a in agents) Assert(Agents.Get(a.Key) != null, "The test definition " + a.Key + " did not load.");

                foreach (var id in new[] { "policyplace", "policydispatch" })
                {
                    var refused = Dispatch.Describe(PolicyAccess(id), Model, Dispatch.ReadOnly, @"C:\w", "windows", null, "1.0.0");
                    Assert(!refused.Supported && refused.Reason != null && refused.Reason.Contains("no_credentails"),
                        "A misspelt environment policy (" + id + ") was not refused by name: " + (refused.Supported ? "supported, policy " + (refused.EnvironmentPolicy ?? "null") : refused.Reason));
                }
                var clean = Dispatch.Describe(PolicyAccess("policyclean"), Model, Dispatch.ReadOnly, @"C:\w", "windows", null, "1.0.0");
                Assert(clean.Supported && clean.CleanEnvironment && clean.EnvironmentPolicy == "clean", "A place's clean policy was not applied: " + (clean.Reason ?? clean.EnvironmentPolicy));
                var kept = Dispatch.Describe(PolicyAccess("policykept"), Model, Dispatch.ReadOnly, @"C:\w", "windows", null, "1.0.0");
                Assert(kept.Supported && !kept.CleanEnvironment && kept.EnvironmentPolicy == "no_credentials", "A dispatch-level no_credentials policy was not passed through: " + (kept.Reason ?? kept.EnvironmentPolicy ?? "null"));

                // Over MCP the refusal is an unsupported description, as for any other.
                var scan = new RuntimeScan();
                scan.Found.Add(new RuntimePlace { Runtime = "policyplace", Where = "windows" });
                scan.Access.Add(PolicyAccess("policyplace"));
                var snap = Fixture();
                var server = new McpServer(() => snap, () => false, null, null, () => scan, new LeaseBook(null));
                string raw;
                var described = Call(server, "describe_dispatch", "{\"service\":\"openrouter\",\"model\":\"" + Model + "\",\"runtime\":\"policyplace\",\"workspace\":\"C:\\\\w\"}", out raw);
                Assert(described.ContainsKey("supported") && !Json.Bool(described, "supported") && Json.Str(described, "reason").Contains("no_credentails"),
                    "describe_dispatch did not refuse a misspelt environment policy: " + raw);
            }
            finally
            {
                foreach (var a in agents) File.Delete(Path.Combine(folder, a.Key + ".json"));
                Agents.Reload();
            }
        }

        private static void ServicesSavedElsewhere()
        {
            string file = Path.Combine(Settings.Folder, "settings.json");
            string previous = File.Exists(file) ? File.ReadAllText(file) : null;
            try
            {
                Assert(Settings.Current.Save() && Settings.ManagedPiServicesSavedElsewhere() == null, "This process's own save was taken for a change made elsewhere.");
                Settings.Load();
                Assert(Settings.ManagedPiServicesSavedElsewhere() == null, "This process's own reading was taken for a change made elsewhere.");

                // Another AI Meter writes the file: read once, then not again.
                Action<string> writeElsewhere = text =>
                {
                    DateTime noted = File.GetLastWriteTimeUtc(file);
                    File.WriteAllText(file, text, new UTF8Encoding(false));
                    File.SetLastWriteTimeUtc(file, noted.AddMinutes(1));
                };
                writeElsewhere("{\"FirstRunDone\":true,\"ManagedPiServices\":[\"gemini\",\"openrouter\"]}");
                var elsewhere = Settings.ManagedPiServicesSavedElsewhere();
                Assert(elsewhere != null && elsewhere.SequenceEqual(new[] { "gemini", "openrouter" }), "The services saved elsewhere were not read: " + (elsewhere == null ? "null" : string.Join(", ", elsewhere)));
                Assert(Settings.ManagedPiServicesSavedElsewhere() == null, "The same change was read twice.");

                // A save of this process that failed leaves its stale file alone, not a change made elsewhere.
                Assert(Settings.Current.Save(), "The settings could not be saved.");
                using (new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.None))
                    Assert(!Settings.Current.Save(), "A save over a held file reported success.");
                Assert(Settings.ManagedPiServicesSavedElsewhere() == null, "A failed save was taken for a change made elsewhere.");

                // An unreadable file gives nothing, and the change is read once the file is free.
                writeElsewhere("{\"FirstRunDone\":true,\"ManagedPiServices\":[\"gemini\"]}");
                using (new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.None))
                    Assert(Settings.ManagedPiServicesSavedElsewhere() == null, "An unreadable settings file gave services.");
                elsewhere = Settings.ManagedPiServicesSavedElsewhere();
                Assert(elsewhere != null && elsewhere.SequenceEqual(new[] { "gemini" }), "A change was lost because the file was held for a moment.");

                // A file that is not valid gives nothing, and does not throw.
                writeElsewhere("{\"FirstRunDone\":true,\"ManagedPiServices\":[");
                List<string> invalid;
                try { invalid = Settings.ManagedPiServicesSavedElsewhere(); }
                catch (Exception e) { throw new Exception("An invalid settings file made ManagedPiServicesSavedElsewhere throw: " + e.GetType().Name + ": " + e.Message); }
                Assert(invalid == null, "An invalid settings file gave services: " + (invalid == null ? "" : string.Join(", ", invalid)));
            }
            finally
            {
                if (previous == null) File.Delete(file); else File.WriteAllText(file, previous, new UTF8Encoding(false));
                File.Delete(file + ".bad");
                File.Delete(file + ".tmp");
                Settings.Load();   // Notes the restored file's time and clears any untouched-file state.
            }
        }

        private static void LocalSharedFolder()
        {
            Assert(Settings.LocalSharedFolder == Settings.Folder, "Under tests the local shared folder is not the test folder: " + Settings.LocalSharedFolder);
            Assert(ManagedPi.Windows.Root.StartsWith(Settings.LocalSharedFolder + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase), "The copy is not under the local shared folder.");
            try
            {
                Native.PackagedOverride = true;
                string store = Path.Combine(Probe.Home, ".aimeter");
                Assert(Settings.LocalSharedFolder == store, "The Store build's local shared folder is not the profile's .aimeter: " + Settings.LocalSharedFolder);
                Assert(ManagedPi.Windows.Root.StartsWith(store + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase), "The Store build's copy is not under the profile's .aimeter: " + ManagedPi.Windows.Root);
            }
            finally { Native.PackagedOverride = false; }
        }

        /// <summary>The providers of the managed place, as agents/pi.json lists them.</summary>
        private static readonly string[] PiProviders = { "cloudflare-workers-ai", "deepseek", "google", "moonshotai", "nvidia", "openrouter", "vercel-ai-gateway" };

        private static string RepositoryModelList(string provider)
        {
            string repository = Environment.GetEnvironmentVariable("AIMETER_REPOSITORY") ?? Environment.CurrentDirectory;
            return File.ReadAllText(Path.Combine(repository, "agents", "pi-models", provider + ".json"));
        }

        private static string ModelListPath(string provider) { return Path.Combine(ManagedPi.Windows.Root, "models", ManagedPi.Version, provider + ".json"); }

        private static void ManagedModelLists()
        {
            Assert(Agents.Get("pi").PlaceFor(ManagedPi.Place).Providers.OrderBy(p => p, StringComparer.Ordinal).SequenceEqual(PiProviders),
                "The managed place's providers are not the ones these tests expect.");
            Assert(ManagedPi.Windows.ModelsFile("nvidia") == null, "A model list was named before the copy was installed.");

            // Written right after the installation, one per provider, as AI Meter carries them.
            Install(Good, ReleaseOf(Good));
            foreach (var provider in PiProviders)
            {
                Assert(ManagedPi.Windows.ModelsFile(provider) == ModelListPath(provider), "The model list for " + provider + " is not where expected: " + (ManagedPi.Windows.ModelsFile(provider) ?? "null"));
                Assert(File.ReadAllText(ModelListPath(provider)) == RepositoryModelList(provider), "The model list for " + provider + " is not Pi's own.");
            }
            Assert(ManagedPi.Windows.ModelsFile("experientiallabs") == null && ManagedPi.Windows.ModelsFile(null) == null && ManagedPi.Windows.ModelsFile("") == null,
                "A model list was named for a provider the copy has none for.");

            // A list deleted, or changed in length, is written again at the next sync.
            File.Delete(ModelListPath("nvidia"));
            File.WriteAllText(ModelListPath("google"), "{}");
            Assert(ManagedPi.Windows.ModelsFile("nvidia") == null, "A deleted list is still named.");
            Assert(ManagedPi.Sync(Settings.Current), "The sync failed.");
            Assert(File.ReadAllText(ModelListPath("nvidia")) == RepositoryModelList("nvidia") && File.ReadAllText(ModelListPath("google")) == RepositoryModelList("google"),
                "A missing or changed model list was not written again.");

            // The managed accesses name their provider's list, in Pi's format.
            ApiKeys.Save("openrouter", RouterKey);
            ApiKeys.Save("gemini", GeminiKey);
            Settings.Current.ManagedPiServices = new List<string> { "openrouter", "gemini" };
            Assert(ManagedPi.Sync(Settings.Current), "The keys could not be copied.");
            var scan = ScanOf(Home(), false);
            var router = ManagedAccess(scan, "openrouter");
            var gemini = ManagedAccess(scan, "gemini");
            Assert(router.ModelsFile == ModelListPath("openrouter") && gemini.ModelsFile == ModelListPath("google") && router.ModelsFormat == "pi" && gemini.ModelsFormat == "pi",
                "The managed accesses do not name Pi's lists: " + router.ModelsFile + ", " + gemini.ModelsFile);

            // Remove takes them away; a sync without a copy writes none.
            long before = Revision();
            ManagedPi.Windows.Remove(Settings.Current);
            AssertRemoved(before, "Remove");
            Assert(ManagedPi.Sync(Settings.Current) && ManagedPi.Windows.ModelsFile("nvidia") == null && !Directory.Exists(Path.Combine(ManagedPi.Windows.Root, "models")),
                "Model lists were written for a copy that is not installed.");
        }

        private const string CloudflareService = "cloudflare-workers-ai";
        private const string CloudflareKey = "cf-token-0123456789abcdef";
        private const string CloudflareAccount = "0123456789abcdef0123456789abcdef";
        /// <summary>A chat model in Pi 1.5.2's list for Cloudflare, at a price.</summary>
        private const string CloudflareModel = "@cf/deepseek-ai/deepseek-v4-flash-0731";

        /// <summary>Cloudflare's free tier with one model, as tests/FreeTierTests.cs builds it.</summary>
        private static Snapshot CloudflareFixture()
        {
            var tier = new FreeStatus { Status = "confirmed_free", Kind = Candidates.FreeTier, Source = "documented" };
            var catalogue = new Catalogue
            {
                ReadAt = DateTime.Now, Pool = "cloudflare-workers-ai:neurons",
                Models = new List<FreeModel> { new FreeModel { Id = CloudflareModel, Name = "DeepSeek V4 Flash", Context = 131072, MaxOutput = 16384, Tools = true, Free = tier } },
                Allowance = new Allowance { Metric = "neurons_per_day", Unit = "neurons", UsdPerUnit = 0.000011, Reserve = 1000 },
            };
            catalogue.Rates.Add(new RateLimit
            {
                Metric = "neurons_per_day", Limit = 10000, Used = 2500, Remaining = 7500, Scope = "account",
                Source = "provider_reported", ResetsAt = DateTime.UtcNow.Date.AddDays(1).ToLocalTime(), ResetZone = "UTC",
            });
            var snap = new Snapshot();
            snap.Providers.Add(new ProviderState { Id = CloudflareService, Name = "Cloudflare Workers AI", Catalogue = catalogue });
            return snap;
        }

        private static void CloudflareThroughCopy()
        {
            NetworkBackoff.Clear();
            var settings = Settings.Current;
            Install(Good, ReleaseOf(Good));
            ApiKeys.Save(CloudflareService, CloudflareKey);
            settings.ManagedPiServices = new List<string> { CloudflareService };

            // Without the account id Pi could not call it: no entry.
            Assert(!ManagedPi.ServicesWithKey().Any(d => d.Id == CloudflareService), "Cloudflare can be copied without its account id.");
            Assert(ManagedPi.Sync(settings) && !File.Exists(KeyFile), "Cloudflare's key was copied without its account id.");

            ApiKeys.Save(CloudflareService + ".account", CloudflareAccount);
            Assert(ManagedPi.ServicesWithKey().Any(d => d.Id == CloudflareService), "Cloudflare cannot be copied with its key and account id.");
            Assert(ManagedPi.Sync(settings), "The key file could not be written.");
            string expected = "{\"cloudflare-workers-ai\":{\"type\":\"api_key\",\"key\":\"" + CloudflareKey + "\",\"env\":{\"CLOUDFLARE_ACCOUNT_ID\":\"" + CloudflareAccount + "\"}}}";
            Assert(File.ReadAllText(KeyFile) == expected, "Cloudflare's entry does not carry its account id: " + File.ReadAllText(KeyFile));

            var scan = ScanOf(Home(), false);
            var access = ManagedAccess(scan, CloudflareService);
            Assert(access.Provider == CloudflareService && access.ModelsFile == ModelListPath(CloudflareService), "Cloudflare's managed access lacks its provider or model list.");
            Assert(AgentModels.Find(access, CloudflareModel) != null && AgentModels.Find(access, CloudflareModel).Priced, "Pi's list does not price the test model.");

            var chosen = Candidates.Select(CloudflareFixture(), scan, new Requirements { Tools = true, FreeTier = true }, DateTime.Now).Chosen;
            Assert(chosen.Count == 1 && chosen[0].Model.Id == CloudflareModel && chosen[0].Access.Managed && chosen[0].Room.CanStart,
                "The free tier was not offered through the copy with consent: " + string.Join(", ", chosen.Select(c => c.Model.Id + "@" + c.Access.Runtime)));
            var refused = Candidates.Select(CloudflareFixture(), scan, new Requirements { Tools = true }, DateTime.Now);
            Assert(refused.Chosen.Count == 0 && refused.Excluded.Single(e => e.Service == CloudflareService).Reason == "free_tier_not_allowed",
                "The free tier was offered through the copy without consent.");
        }

        /// <summary>A model at no cost in Pi 1.5.2's list for NVIDIA.</summary>
        private const string NvidiaModel = "deepseek-ai/deepseek-v4.1-flash";

        private static void NvidiaBestEffortThroughCopy()
        {
            NetworkBackoff.Clear();
            Install(Good, ReleaseOf(Good));
            ApiKeys.Save("nvidia", "nvapi-0123456789abcdef");
            Settings.Current.ManagedPiServices = new List<string> { "nvidia" };
            Assert(ManagedPi.Sync(Settings.Current), "NVIDIA's key could not be copied.");
            var scan = ScanOf(Home(), false);
            var access = ManagedAccess(scan, "nvidia");
            var listed = AgentModels.Find(access, NvidiaModel);
            Assert(listed != null && !listed.Priced && listed.Tools == true, "Pi's list does not have the test model at no cost with tools.");

            var snap = new Snapshot();
            var catalogue = new Catalogue { ReadAt = DateTime.Now, Pool = "nvidia:account", Models = new List<FreeModel>
                { new FreeModel { Id = NvidiaModel, Name = NvidiaModel, FeaturesUnknown = true, Free = new FreeStatus { Status = "unknown", Note = "no prices" } } } };
            snap.Providers.Add(new ProviderState { Id = "nvidia", Name = "NVIDIA", Catalogue = catalogue });

            var asked = Candidates.Select(snap, scan, new Requirements { Tools = true, BestEffort = true }, DateTime.Now);
            var best = asked.BestEffort.SingleOrDefault();
            Assert(best != null && best.Model.Id == NvidiaModel && best.Access.Managed && best.Unverified.Contains("tools"),
                "The NVIDIA model is not a best-effort candidate through the copy: " + string.Join(", ", asked.BestEffort.Select(c => c.Model.Id + "@" + c.Access.Runtime)));
            var plain = Candidates.Select(snap, scan, new Requirements { Tools = true }, DateTime.Now);
            Assert(plain.BestEffort.Count == 0 && plain.BestEffortAvailable == 1, "Without consent the best-effort model was listed, or not counted.");
        }

        private static void DemoLeavesKeysAlone()
        {
            var settings = Settings.Current;
            InstallWithKeys();
            byte[] before = File.ReadAllBytes(KeyFile);
            long revision = Revision();
            bool demo = Program.Demo;
            try
            {
                Program.Demo = true;
                Assert(!ManagedPi.Offered && !ManagedPi.Windows.Installed, "With sample data the managed copy is offered or installed.");
                settings.ManagedPiServices = new List<string> { "openrouter" };
                ApiKeys.Save("openrouter", RouterKey + "-rotated");
                Assert(ManagedPi.Sync(settings), "Sync failed with sample data.");
                Assert(File.ReadAllBytes(KeyFile).SequenceEqual(before) && Revision() == revision, "With sample data Sync changed the copy's key file.");
                settings.ManagedPiServices = new List<string>();
                Assert(ManagedPi.Sync(settings) && File.ReadAllBytes(KeyFile).SequenceEqual(before), "With sample data Sync deleted the copy's key file.");
            }
            finally { Program.Demo = demo; }
        }

        private static void RemoveReportsUnsavedChoices()
        {
            var settings = Settings.Current;
            InstallWithKeys();
            File.WriteAllText(PendingKeyFile, "half");
            string file = Path.Combine(Settings.Folder, "settings.json");
            Assert(settings.Save(), "The settings could not be saved before the test.");
            long before = Revision();
            using (new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.None))
                Fails<ManagedPi.InstallException>(() => ManagedPi.Windows.Remove(settings), "settings that could not be saved");
            AssertRemoved(before, "a Remove whose settings could not be saved");
            Assert(settings.ManagedPiServices.Count == 0 && !ManagedPi.Windows.Installed, "Remove did not clear the choices in memory when the settings could not be saved.");
        }
    }
}
