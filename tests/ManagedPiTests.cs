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
    internal static class ManagedPiTests
    {
        internal static void Register(Action<string, Action> test)
        {
            test("The managed Pi installs from a verified archive, reporting progress and leaving no partial files", () => Isolated(InstallsVerifiedArchive));
            test("A wrong, oversized, escaping, incomplete or cancelled archive installs nothing and keeps an installed copy", () => Isolated(RefusesBadArchives));
            test("The managed Pi holds keys only for ticked services Pi knows, and Remove deletes keys, program and choices", () => Isolated(KeysFollowChoices));
            test("The scan offers the managed Pi only when installed, with its copied keys, after the user's own Pi", () => Isolated(ScanOffersManagedCopy));
            test("Dispatch describes the managed Pi with its own folder, shell and alternatives, on Windows folders only", () => Isolated(DescribesManagedCopy));
            test("The MCP tools show, describe and lease the managed Pi without revealing its keys", () => Isolated(McpShowsManagedCopy));
            test("The managed Pi's strings are translated in every language, with their placeholders", Translations);
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
        private static readonly string[] Services = { "openrouter", "gemini", "experientiallabs" };

        /// <summary>
        /// Run a test with the copy's folder under the test folder (not packaged), the repository's definitions of
        /// Services as the only ones, key discovery off and no key variable of those services in this process, so that
        /// no key of the developer's machine reaches a test. Restores what it changed. A key set in the user's own
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
            var saved = new Dictionary<string, string>();
            foreach (var id in Services)
                foreach (var name in ServiceDefinition.Strings(Definitions.Get(id).Auth, "env"))
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
                action();
            }
            finally
            {
                foreach (var kv in saved) Environment.SetEnvironmentVariable(kv.Key, kv.Value);
                Native.PackagedOverride = packaged;
                Settings.Current.FindKeys = findKeys;
                Settings.Current.ManagedPiServices = services;
                foreach (var id in Services)
                {
                    ApiKeys.Remove(id);
                    File.Delete(Path.Combine(Definitions.UserFolder, id + ".json"));
                }
                cached.SetValue(null, null);
            }
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

        /// <summary>The pinned version, with the archive's real digest and exact length.</summary>
        private static ManagedPi.Release ReleaseOf(byte[] archive)
        {
            string digest;
            using (var sha = SHA256.Create()) digest = BitConverter.ToString(sha.ComputeHash(archive)).Replace("-", "").ToLowerInvariant();
            return new ManagedPi.Release { Version = ManagedPi.Pinned.Version, Url = "https://example.invalid/pi-windows-x64.zip", Sha256 = digest, ArchiveBytes = archive.Length };
        }

        private static void Install(byte[] archive, ManagedPi.Release release, Func<bool> cancelled = null, List<long> progress = null)
        {
            ManagedPi.Install(release, url => new MemoryStream(archive), read => { if (progress != null) progress.Add(read); }, cancelled);
        }

        /// <summary>Files and folders under the copy's root named as a partial download or unpacking.</summary>
        private static List<string> Partials()
        {
            if (!Directory.Exists(ManagedPi.Root)) return new List<string>();
            return Directory.GetFileSystemEntries(ManagedPi.Root, "*", SearchOption.AllDirectories)
                .Where(p => p.EndsWith(".partial", StringComparison.OrdinalIgnoreCase) || p.EndsWith(".new", StringComparison.OrdinalIgnoreCase)).ToList();
        }

        private static void InstallsVerifiedArchive()
        {
            Assert(!ManagedPi.Installed && ManagedPi.Stamp == "", "A copy counted as installed before any installation.");
            var progress = new List<long>();
            Install(Good, ReleaseOf(Good), null, progress);
            Assert(ManagedPi.Installed && File.ReadAllText(ManagedPi.ProgramPath) == "not really Pi", "The program was not unpacked where Installed looks.");
            Assert(ManagedPi.ProgramPath == Path.Combine(ManagedPi.Root, ManagedPi.Pinned.Version, "pi.exe"), "The program is not in the pinned version's folder.");
            Assert(File.Exists(Path.Combine(ManagedPi.Root, ManagedPi.Pinned.Version, @"theme\dark.json")), "A nested file was not unpacked.");
            Assert(ManagedPi.Root.StartsWith(Settings.Folder, StringComparison.OrdinalIgnoreCase), "The copy is not under the shared folder.");
            Assert(Partials().Count == 0 && !Directory.GetFiles(ManagedPi.Root).Any(), "A partial download or unpacking was left: " + string.Join(", ", Partials()));
            Assert(progress.Count > 0 && progress.Last() == Good.Length, "Progress was not reported up to the archive's size.");
            Assert(ManagedPi.Stamp == "0", "An installed copy without keys has the wrong stamp: " + ManagedPi.Stamp);
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
            string beside = Path.Combine(ManagedPi.Root, "evil.txt");
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
                Assert(!ManagedPi.Installed, "Installed is true after " + what + ".");
                Assert(!Directory.Exists(Path.Combine(ManagedPi.Root, ManagedPi.Pinned.Version)), "A version folder was left after " + what + ".");
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
            Assert(ManagedPi.Installed && File.ReadAllText(ManagedPi.ProgramPath) == "not really Pi", "A failed installation destroyed the installed copy.");
            Assert(Partials().Count == 0 && !File.Exists(outside) && !File.Exists(beside), "A failed installation over a copy left files.");
        }

        // ------------------------------------------------------------ keys

        private static string KeyFile { get { return Path.Combine(ManagedPi.ConfigFolder, "auth.json"); } }

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
            Assert(ManagedPi.SyncKeys(settings) && !File.Exists(KeyFile) && !Directory.Exists(ManagedPi.Root), "Keys were written for a copy that is not installed.");

            Install(Good, ReleaseOf(Good));
            Assert(ManagedPi.SyncKeys(settings), "The key file could not be written.");
            Assert(File.ReadAllText(KeyFile) == "{\"openrouter\":{\"type\":\"api_key\",\"key\":\"" + RouterKey + "\"}}",
                "The key file is not Pi's auth.json for OpenRouter: " + File.ReadAllText(KeyFile));
            string provider;
            Assert(ManagedPi.KeyFor(router, out provider) == RouterKey && provider == "openrouter", "KeyFor does not give the copied key.");
            Assert(ManagedPi.KeyFor(gemini, out provider) == null && provider == "google", "KeyFor gives a key that was not copied.");
            Assert(ManagedPi.Stamp != "0" && ManagedPi.Stamp != "", "The stamp does not follow the key file.");

            // Gemini under Pi's provider google; Experiential Labs never, even when ticked.
            settings.ManagedPiServices = new List<string> { "openrouter", "gemini", "experientiallabs" };
            Assert(ManagedPi.SyncKeys(settings), "The key file could not be rewritten.");
            var keys = Json.ParseFile(KeyFile);
            Assert(keys.Keys.OrderBy(k => k, StringComparer.Ordinal).SequenceEqual(new[] { "google", "openrouter" }), "The key file's providers are wrong: " + string.Join(", ", keys.Keys));
            Assert(Json.Str(Json.Obj(keys, "google"), "key") == GeminiKey && Json.Str(Json.Obj(keys, "google"), "type") == "api_key", "Gemini's key is not under google.");
            Assert(!File.ReadAllText(KeyFile).Contains(LabsKey), "A key for a service Pi cannot call was copied.");
            Assert(ManagedPi.KeyFor(labs, out provider) == null && provider == null, "KeyFor gives a key for a service Pi cannot call.");

            // Unticking removes the entry, and the file once it is empty.
            settings.ManagedPiServices = new List<string> { "openrouter" };
            ManagedPi.SyncKeys(settings);
            Assert(!File.ReadAllText(KeyFile).Contains(GeminiKey) && File.ReadAllText(KeyFile).Contains(RouterKey), "Unticking Gemini did not remove only its key.");
            settings.ManagedPiServices = new List<string>();
            Assert(ManagedPi.SyncKeys(settings) && !File.Exists(KeyFile), "Unticking every service left the key file.");
            Assert(ManagedPi.Stamp == "0", "The stamp does not show that the keys are gone.");

            // A key removed in AI Meter leaves the copy at the next sync.
            settings.ManagedPiServices = new List<string> { "openrouter" };
            ManagedPi.SyncKeys(settings);
            Assert(File.Exists(KeyFile), "The key was not copied again.");
            ApiKeys.Remove("openrouter");
            Assert(ManagedPi.SyncKeys(settings) && !File.Exists(KeyFile), "A key removed in AI Meter stayed in the copy.");
            Assert(!ManagedPi.ServicesWithKey().Any(d => d.Id == "openrouter"), "A service without a key can still be copied.");

            // Remove deletes the keys, the program and the choices.
            ApiKeys.Save("openrouter", RouterKey);
            settings.ManagedPiServices = new List<string> { "openrouter", "gemini" };
            ManagedPi.SyncKeys(settings);
            ManagedPi.Remove(settings);
            Assert(!File.Exists(KeyFile) && !File.Exists(ManagedPi.ProgramPath) && !Directory.Exists(ManagedPi.Root), "Remove left files of the copy.");
            Assert(settings.ManagedPiServices != null && settings.ManagedPiServices.Count == 0, "Remove did not forget the ticked services.");
            Assert(!ManagedPi.Installed && ManagedPi.Stamp == "", "The copy still counts as installed after Remove.");
            Assert(ManagedPi.SyncKeys(settings) && !Directory.Exists(ManagedPi.Root), "A sync after Remove wrote files again.");
        }

        // ------------------------------------------------------------ scan

        /// <summary>The copy installed, with OpenRouter's and Gemini's keys copied into it.</summary>
        private static void InstallWithKeys()
        {
            Install(Good, ReleaseOf(Good));
            ApiKeys.Save("openrouter", RouterKey);
            ApiKeys.Save("gemini", GeminiKey);
            Settings.Current.ManagedPiServices = new List<string> { "openrouter", "gemini" };
            Assert(ManagedPi.SyncKeys(Settings.Current), "The keys could not be copied.");
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
                Assert(a.Runtime == "pi" && a.Where == "windows" && a.Program == ManagedPi.ProgramPath && a.ConfigFolder == ManagedPi.ConfigFolder
                       && a.Version == ManagedPi.Pinned.Version, "The managed access for " + a.Service + " lacks its program, folder or version.");
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
            ManagedPi.Remove(Settings.Current);
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

        private static void DescribesManagedCopy()
        {
            InstallWithKeys();
            var router = ManagedAccess(ScanOf(Home(), false), "openrouter");
            string workspace = @"C:\src\project";

            var modify = Dispatch.Describe(router, Model, Dispatch.Modify, workspace, "windows", null, router.Version);
            Assert(modify.Supported && modify.Managed && modify.VersionVerified, "The managed copy was not described: " + modify.Reason);
            Assert(modify.Program == ManagedPi.ProgramPath && modify.WorkingDirectory == workspace && modify.Where == "windows", "The managed copy's program or folder is wrong.");
            Assert(modify.Environment["PI_CODING_AGENT_DIR"] == ManagedPi.ConfigFolder && modify.Environment["PI_SKIP_VERSION_CHECK"] == "1" && modify.Environment["PI_TELEMETRY"] == "0",
                "The managed copy's environment is wrong.");
            Assert(After(modify.Arguments, "--tools") == "read,powershell,edit,write" && After(modify.Arguments, "--provider") == "openrouter" && After(modify.Arguments, "--model") == Model,
                "The managed copy's modify arguments are wrong: " + string.Join(" ", modify.Arguments));
            Assert(modify.Arguments.Contains("--no-context-files") && !modify.Arguments.Any(a => a.Contains("{")), "The managed copy is not isolated, or a placeholder was left.");
            Assert(modify.Commands == "powershell", "The modify mode does not say it runs PowerShell.");
            Assert(modify.Alternatives.Keys.SequenceEqual(new[] { "git_bash" }), "The git_bash alternative is missing.");
            var bash = modify.Alternatives["git_bash"];
            Assert(After(bash, "--tools") == "read,bash,edit,write" && After(bash, "--provider") == "openrouter" && After(bash, "--model") == Model,
                "The git_bash alternative is wrong: " + string.Join(" ", bash));

            var gemini = Dispatch.Describe(ManagedAccess(ScanOf(Home(), false), "gemini"), "gemini-2.5-flash", Dispatch.Modify, workspace, "windows", null, ManagedPi.Pinned.Version);
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

        private static void McpShowsManagedCopy()
        {
            NetworkBackoff.Clear();
            Settings.Current.WslDistro = "Ubuntu-24.04";
            var snap = Fixture();
            string home = Home();
            var server = new McpServer(() => snap, () => false, null, null, () => ScanOf(home, false), new LeaseBook(null));
            string raw;
            var answers = new List<string>();

            // Not installed: the block says so, with no services, and there is no candidate.
            var before = Call(server, "list_candidates", "{}", out raw);
            var block = Json.Obj(Json.Obj(before, "runtimes"), "managed");
            Assert(block != null && !Json.Bool(block, "installed") && Json.Str(block, "version") == ManagedPi.Pinned.Version && Items(before, "candidates").Count == 0,
                "runtimes.managed does not say the copy is not installed.");
            Assert(((IList)block["services"]).Count == 0 && Json.Str(block, "set_up_in") != null, "runtimes.managed lists services, or not where to set it up.");

            InstallWithKeys();
            var listed = Call(server, "list_candidates", "{}", out raw);
            answers.Add(raw);
            var candidate = Items(listed, "candidates").Single();
            Assert(Json.Str(candidate, "service") == "openrouter" && IsManaged(Json.Obj(candidate, "runtime")) && Json.Str(Json.Obj(candidate, "runtime"), "where") == "windows",
                "The candidate does not say it runs on the managed copy.");
            Assert(Items(Json.Obj(listed, "runtimes"), "found").Count(IsManaged) == 1, "runtimes.found does not list the managed copy.");
            block = Json.Obj(Json.Obj(listed, "runtimes"), "managed");
            Assert(Json.Bool(block, "installed") && ((IList)block["services"]).Cast<string>().SequenceEqual(new[] { "gemini", "openrouter" }),
                "runtimes.managed does not list the installed copy's services.");
            Assert(Json.Bool(Json.Obj(listed, "runtimes"), "key_discovery") == false, "The fixture should run with key discovery off.");

            var fromWsl = Call(server, "list_candidates", "{\"from\":\"wsl\"}", out raw);
            answers.Add(raw);
            Assert(!Items(fromWsl, "candidates").Any(c => IsManaged(Json.Obj(c, "runtime"))), "A caller in WSL was offered the managed copy.");

            string candidateArgs = "{\"service\":\"openrouter\",\"model\":\"" + Model + "\",\"runtime\":\"pi\"";
            var described = Call(server, "describe_dispatch", candidateArgs + ",\"managed\":true,\"mode\":\"modify\",\"workspace\":\"C:\\\\w\"}", out raw);
            answers.Add(raw);
            Assert(Json.Bool(described, "supported") && Json.Bool(described, "managed") && Json.Str(described, "program") == ManagedPi.ProgramPath, "describe_dispatch did not describe the copy: " + raw);
            Assert(Json.Str(described, "commands") == "powershell" && Json.Str(Json.Obj(described, "environment"), "PI_CODING_AGENT_DIR") == ManagedPi.ConfigFolder,
                "describe_dispatch lacks the copy's shell or configuration folder.");
            var alternative = Items(described, "alternatives").Single();
            Assert(Json.Str(alternative, "shell") == "git_bash" && ((IList)alternative["arguments"]).Cast<string>().Contains("read,bash,edit,write"), "describe_dispatch lacks the git_bash alternative.");
            var implicitly = Call(server, "describe_dispatch", candidateArgs + "}", out raw);
            answers.Add(raw);
            Assert(Json.Bool(implicitly, "managed") && Json.Str(implicitly, "commands") == "none", "Without managed, the only Pi here was not described read-only.");
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
            Assert(answers[2].Contains(Json.Serialize(ManagedPi.ConfigFolder).Trim('"')), "The configuration folder should be in the description.");
        }
    }
}
