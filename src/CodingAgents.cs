using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace AIMeter
{
    /// <summary>
    /// Sessions of coding CLIs (OpenCode, Pi, Gemini CLI, Antigravity CLI, Qwen Code, Goose, Crush, Cline, Kilo): title,
    /// folder, model in use and context occupied, from their local files on Windows and in WSL (if already running).
    /// Pi sessions are also read from the folders that running Pi processes name (RunningAgents). Sessions active in
    /// the last 30 minutes are shown. Read-only.
    /// </summary>
    internal sealed class CodingAgents
    {
        private const int RecentMinutes = 30;
        private readonly Settings S;
        private List<Session> last = new List<Session>();
        private DateTime scannedAt = DateTime.MinValue;

        /// <summary>A session read from files, before it becomes an interface row.</summary>
        private sealed class Found
        {
            public string Agent, Title, Model, Cwd;
            public string Provider;   // The agent's provider id, such as "openrouter", when the session records it.
            public string File;       // Source file, so a folder reached in two ways is read once.
            public DateTime Updated;
            public int Context;
            /// <summary>The content says the reply is in progress; valid only if the file was written less than Fresh minutes ago.</summary>
            public bool Pending;
            public double Fresh = 10;
            public bool Working { get { return Pending && (DateTime.Now - Updated).TotalMinutes < Fresh; } }
        }

        private sealed class Place { public string Home, Where; }

        public CodingAgents(Settings s) { S = s; }

        public List<Session> Collect(Probe p)
        {
            if ((DateTime.Now - scannedAt).TotalSeconds < (p.SavingPower ? 120 : 30)) return last;
            scannedAt = DateTime.Now;
            var places = new List<Place> { new Place { Home = Probe.Home, Where = "Windows" } };
            if (p.WslUp) places.Add(new Place { Home = p.WslHome, Where = "WSL" });
            last = Scan(p, places);
            return last;
        }

        /// <summary>Sessions found in any user folder (also used to test the formats).</summary>
        internal List<Session> ScanHome(Probe p, string home) { return Scan(p, new List<Place> { new Place { Home = home, Where = "Windows" } }); }

        private List<Session> Scan(Probe p, List<Place> places)
        {
            var all = new List<Tuple<Found, string>>();
            foreach (var pl in places)
            {
                Action<IEnumerable<Found>> add = items => { foreach (var f in items) all.Add(Tuple.Create(f, pl.Where)); };
                try { add(OpenCodeLike(p, pl, "OpenCode", @".local\share\opencode", "opencode.db")); } catch (Exception) { }
                try { add(OpenCodeLike(p, pl, "Kilo", @".local\share\kilo", "kilo.db")); } catch (Exception) { }
                try { add(Pi(p, PiRoot(pl))); } catch (Exception) { }
                try { add(Gemini(p, pl)); } catch (Exception) { }
                try { add(Antigravity(p, pl)); } catch (Exception) { }
                try { add(Qwen(p, pl)); } catch (Exception) { }
                try { add(Goose(p, pl)); } catch (Exception) { }
                try { add(Crush(p, pl)); } catch (Exception) { }
                try { add(Cline(p, pl)); } catch (Exception) { }
            }
            try
            {
                foreach (var folder in RunningAgents.PiSessionFolders(p))
                    foreach (var f in Pi(p, folder.Path)) all.Add(Tuple.Create(f, folder.Where));
            }
            catch (Exception) { }
            return all.GroupBy(t => t.Item1.File ?? Guid.NewGuid().ToString(), StringComparer.OrdinalIgnoreCase).Select(g => g.First())
                .Where(t => (DateTime.Now - t.Item1.Updated).TotalMinutes <= RecentMinutes)
                .OrderByDescending(t => t.Item1.Updated).Take(12)
                .Select(t => new Session
                {
                    Kind = t.Item2 == "WSL" ? t.Item1.Agent + " · WSL" : t.Item1.Agent,
                    Title = string.IsNullOrEmpty(t.Item1.Title) ? L.T("Untitled session") : Shorten(t.Item1.Title),
                    Model = t.Item1.Model,
                    Provider = t.Item1.Provider,
                    Status = t.Item1.Working ? "busy" : "idle",
                    Updated = t.Item1.Updated,
                    Note = t.Item1.Context > 0 ? L.F("{0} tokens in context", Ui.Tokens(t.Item1.Context)) : null,
                }).ToList();
        }

        private static string Shorten(string s)
        {
            s = s.Replace('\r', ' ').Replace('\n', ' ').Trim();
            return s.Length > 70 ? s.Substring(0, 67) + "…" : s;
        }

        private static bool Recent(FileInfo f) { return f.Exists && (DateTime.Now - f.LastWriteTime).TotalMinutes <= RecentMinutes; }

        // ================================================================ SQLite databases
        /// <summary>
        /// The database to open. WAL databases inside WSL cannot be opened through \\wsl.localhost (shared memory is not
        /// available): the file and its WAL are copied to a temporary folder, only when they change.
        /// </summary>
        internal static string Openable(string path)
        {
            if (!path.StartsWith(@"\\") || !File.Exists(path)) return path;
            try
            {
                var db = new FileInfo(path);
                if (db.Length > 64L * 1024 * 1024) return null;
                string dir = Path.Combine(Path.GetTempPath(), "AIMeter", Math.Abs(path.ToLowerInvariant().GetHashCode()).ToString(CultureInfo.InvariantCulture));
                Directory.CreateDirectory(dir);
                string copy = Path.Combine(dir, db.Name);
                foreach (var ext in new[] { "", "-wal" })
                {
                    var src = new FileInfo(path + ext);
                    var dst = new FileInfo(copy + ext);
                    if (!src.Exists) { if (dst.Exists) dst.Delete(); continue; }
                    if (dst.Exists && dst.Length == src.Length && dst.LastWriteTimeUtc == src.LastWriteTimeUtc) continue;
                    File.Copy(src.FullName, dst.FullName, true);
                    File.SetLastWriteTimeUtc(dst.FullName, src.LastWriteTimeUtc);
                    if (File.Exists(copy + "-shm")) File.Delete(copy + "-shm");
                }
                return copy;
            }
            catch (IOException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
        }

        /// <summary>The file that changes on every write: the WAL if present, otherwise the database.</summary>
        private static FileInfo Stamp(string db)
        {
            var wal = new FileInfo(db + "-wal");
            return wal.Exists && wal.Length > 0 ? wal : new FileInfo(db);
        }

        private static string S0(object[] row, int i) { return row.Length > i && row[i] != null ? Convert.ToString(row[i], CultureInfo.InvariantCulture) : null; }
        private static double N0(object[] row, int i) { return row.Length > i ? (JPath.Num(row[i]) ?? 0) : 0; }

        // ================================================================ OpenCode (and Kilo, a fork of it)
        private static IEnumerable<Found> OpenCodeLike(Probe p, Place pl, string agent, string dir, string file)
        {
            string db = Path.Combine(pl.Home, dir, file);
            if (pl.Where == "Windows" && !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("XDG_DATA_HOME")))
                db = Path.Combine(Environment.GetEnvironmentVariable("XDG_DATA_HOME"), Path.GetFileName(dir), file);
            if (!File.Exists(db)) return new Found[0];
            var stamp = Stamp(db);
            if (!Recent(stamp)) return new Found[0];
            return (List<Found>)p.Cached("agent:" + agent + ":" + db, stamp, () =>
            {
                var list = new List<Found>();
                string path = Openable(db);
                long cutoff = (long)(DateTime.UtcNow.AddMinutes(-RecentMinutes) - new DateTime(1970, 1, 1)).TotalMilliseconds;
                var rows = Sqlite.Rows(path, "SELECT id, title, directory, model, time_updated FROM session WHERE parent_id IS NULL AND time_archived IS NULL AND time_updated > "
                                             + cutoff.ToString(CultureInfo.InvariantCulture) + " ORDER BY time_updated DESC LIMIT 4", 4);
                if (rows == null) return list;
                foreach (var r in rows)
                {
                    var f = new Found { Agent = agent, Title = S0(r, 1), Cwd = S0(r, 2), Updated = Json.FromUnixMs(N0(r, 4)) ?? DateTime.MinValue };
                    f.Model = JPath.Str(JPath.Get(Json.ParseAny(S0(r, 3) ?? ""), "id"));
                    // Last message: actual model, tokens and whether the reply is still in progress.
                    var msg = Sqlite.Rows(path, "SELECT data FROM message WHERE session_id = " + Sqlite.Quote(S0(r, 0)) + " ORDER BY time_created DESC LIMIT 1", 1);
                    var data = msg != null && msg.Count > 0 ? Json.ParseAny(S0(msg[0], 0) ?? "") : null;
                    if (JPath.Str(JPath.Get(data, "role")) == "assistant")
                    {
                        f.Model = JPath.Str(JPath.Get(data, "modelID")) ?? f.Model;
                        f.Pending = JPath.Get(data, "time.completed") == null;
                        f.Context = (int)((JPath.Num(JPath.Get(data, "tokens.input")) ?? 0) + (JPath.Num(JPath.Get(data, "tokens.cache.read")) ?? 0)
                                        + (JPath.Num(JPath.Get(data, "tokens.cache.write")) ?? 0) + (JPath.Num(JPath.Get(data, "tokens.output")) ?? 0));
                    }
                    else if (JPath.Str(JPath.Get(data, "role")) == "user") f.Pending = true;
                    list.Add(f);
                }
                return list;
            });
        }

        // ================================================================ Pi
        private static string PiRoot(Place pl)
        {
            return pl.Where == "Windows"
                ? (Environment.GetEnvironmentVariable("PI_CODING_AGENT_SESSION_DIR")
                   ?? Path.Combine(Environment.GetEnvironmentVariable("PI_CODING_AGENT_DIR") ?? Path.Combine(pl.Home, @".pi\agent"), "sessions"))
                : Path.Combine(pl.Home, @".pi\agent\sessions");
        }

        /// <summary>
        /// The folders of a Pi session root: the default root keeps one folder per working directory, while
        /// --session-dir puts the files directly in the folder given.
        /// </summary>
        internal static IEnumerable<string> PiSessionDirs(string root)
        {
            yield return root;
            foreach (var dir in Probe.SafeDirs(root)) yield return dir;
        }

        private static IEnumerable<Found> Pi(Probe p, string root)
        {
            var list = new List<Found>();
            foreach (var dir in PiSessionDirs(root))
                foreach (var fi in Probe.SafeFiles(dir, "*.jsonl").Where(Recent))
                {
                    var f = (Found)p.Cached("agent:pi:" + fi.FullName, fi, () => PiFile(fi));
                    if (f != null) list.Add(f);
                }
            return list;
        }

        private static Found PiFile(FileInfo fi)
        {
            var f = new Found { Agent = "Pi", Updated = fi.LastWriteTime, File = fi.FullName };
            var header = Json.ParseAny(Probe.ReadFirstLine(fi.FullName, 65536) ?? "");
            f.Cwd = JPath.Str(JPath.Get(header, "cwd"));
            string lastRole = null, lastStop = null;
            foreach (var line in Lines(Probe.TailText(fi.FullName, 262144)))
            {
                var e = Json.ParseAny(line);
                string type = JPath.Str(JPath.Get(e, "type"));
                if (type == "session_info" && JPath.Str(JPath.Get(e, "name")) != null) f.Title = JPath.Str(JPath.Get(e, "name"));
                else if (type == "model_change")
                {
                    f.Model = JPath.Str(JPath.Get(e, "modelId")) ?? f.Model;
                    f.Provider = JPath.Str(JPath.Get(e, "provider")) ?? f.Provider;
                }
                else if (type == "message")
                {
                    lastRole = JPath.Str(JPath.Get(e, "message.role"));
                    if (lastRole != "assistant") continue;
                    lastStop = JPath.Str(JPath.Get(e, "message.stopReason"));
                    f.Model = JPath.Str(JPath.Get(e, "message.model")) ?? f.Model;
                    f.Provider = JPath.Str(JPath.Get(e, "message.provider")) ?? f.Provider;
                    var u = JPath.Get(e, "message.usage");
                    // Same formula as Pi; interrupted or failed replies do not count.
                    if (u != null && lastStop != "aborted" && lastStop != "error")
                    {
                        int ctx = (int)(JPath.Num(JPath.Get(u, "totalTokens")) ?? 0);
                        if (ctx == 0) ctx = (int)((JPath.Num(JPath.Get(u, "input")) ?? 0) + (JPath.Num(JPath.Get(u, "output")) ?? 0) + (JPath.Num(JPath.Get(u, "cacheRead")) ?? 0) + (JPath.Num(JPath.Get(u, "cacheWrite")) ?? 0));
                        if (ctx > 0) f.Context = ctx;
                    }
                }
            }
            if (f.Title == null) f.Title = FirstUserText(fi.FullName, e => JPath.Str(JPath.Get(e, "message.role")) == "user" ? JPath.Get(e, "message.content") : null);
            // The agent loop is in progress if the last message is not a completed reply.
            f.Pending = !(lastRole == "assistant" && (lastStop == "stop" || lastStop == "length" || lastStop == "aborted" || lastStop == "error"));
            f.Fresh = 3;
            return f;
        }

        // ================================================================ Gemini CLI
        private static IEnumerable<Found> Gemini(Probe p, Place pl)
        {
            var list = new List<Found>();
            foreach (var proj in Probe.SafeDirs(Path.Combine(pl.Home, @".gemini\tmp")))
                foreach (var fi in Probe.SafeFiles(Path.Combine(proj, "chats"), "session-*.jsonl").Where(Recent))
                {
                    var f = (Found)p.Cached("agent:gemini:" + fi.FullName, fi, () => GeminiFile(fi, proj));
                    if (f != null) list.Add(f);
                }
            return list;
        }

        private static Found GeminiFile(FileInfo fi, string projectDir)
        {
            var f = new Found { Agent = "Gemini CLI", Updated = fi.LastWriteTime };
            try { string root = Path.Combine(projectDir, ".project_root"); if (File.Exists(root)) f.Cwd = File.ReadAllText(root).Trim(); }
            catch (IOException) { }
            string lastType = null;
            var first = Json.ParseAny(Probe.ReadFirstLine(fi.FullName, 65536) ?? "");
            f.Title = JPath.Str(JPath.Get(first, "summary"));
            foreach (var line in Lines(Probe.TailText(fi.FullName, 262144)))
            {
                var e = Json.ParseAny(line);
                var set = JPath.Get(e, "$set");
                if (set != null)
                {
                    f.Title = JPath.Str(JPath.Get(set, "summary")) ?? f.Title;
                    var t = JPath.Date(JPath.Get(set, "lastUpdated"));
                    if (t != null) f.Updated = t.Value;
                    continue;
                }
                string type = JPath.Str(JPath.Get(e, "type"));
                if (type == null) continue;
                lastType = type;
                if (type == "gemini")
                {
                    f.Model = JPath.Str(JPath.Get(e, "model")) ?? f.Model;
                    int input = (int)(JPath.Num(JPath.Get(e, "tokens.input")) ?? 0);
                    if (input > 0) f.Context = input;
                }
            }
            if (f.Title == null) f.Title = FirstUserText(fi.FullName, e => JPath.Str(JPath.Get(e, "type")) == "user" ? JPath.Get(e, "content") : null);
            f.Pending = lastType == "user";
            f.Fresh = 2;
            return f;
        }

        // ================================================================ Antigravity CLI (agy)
        /// <summary>
        /// agy keeps one row per conversation in conversation_summaries.db, with title, workspace, run status and last
        /// change as plain columns. The model is only in the conversation's own database, inside a protobuf blob.
        /// </summary>
        private static IEnumerable<Found> Antigravity(Probe p, Place pl)
        {
            string root = Path.Combine(pl.Home, @".gemini\antigravity-cli");
            string db = Path.Combine(root, "conversation_summaries.db");
            if (!File.Exists(db) || !Recent(Stamp(db))) return new Found[0];
            return (List<Found>)p.Cached("agent:antigravity:" + db, Stamp(db), () =>
            {
                var list = new List<Found>();
                // Subagents have a parent; killed conversations are over.
                var rows = Sqlite.Rows(Openable(db), "SELECT conversation_id, title, workspace_uris, status, last_modified_time FROM conversation_summaries"
                                                    + " WHERE parent_conversation_id = '' AND killed = 0 ORDER BY last_modified_time DESC LIMIT 4", 4);
                if (rows == null) return list;
                foreach (var r in rows)
                {
                    var f = new Found { Agent = "Antigravity CLI", Title = S0(r, 1), Updated = GoTime(S0(r, 4)) ?? DateTime.MinValue };
                    var uris = Json.ParseAny(S0(r, 2) ?? "") as IList;
                    string uri = uris != null && uris.Count > 0 ? uris[0] as string : null;
                    if (uri != null && uri.StartsWith("file://", StringComparison.Ordinal)) f.Cwd = Uri.UnescapeDataString(uri.Substring(7));
                    string status = S0(r, 3);
                    f.Pending = status == "CASCADE_RUN_STATUS_RUNNING" || status == "CASCADE_RUN_STATUS_BUSY" || status == "CASCADE_RUN_STATUS_CANCELING";
                    if ((DateTime.Now - f.Updated).TotalMinutes <= RecentMinutes) f.Model = AntigravityModel(Path.Combine(root, "conversations", S0(r, 0) + ".db"));
                    list.Add(f);
                }
                return list;
            });
        }

        /// <summary>
        /// The model id chosen for the conversation, such as "gemini-3.8-flash-high", from the latest executor_metadata
        /// row (field 10.1.28, seen in agy 1.4). Null if the file or the field is missing: agy publishes no schema.
        /// </summary>
        private static string AntigravityModel(string conversation)
        {
            if (!File.Exists(conversation)) return null;
            var rows = Sqlite.Rows(Openable(conversation), "SELECT hex(data) FROM executor_metadata ORDER BY idx DESC LIMIT 1", 1);
            if (rows == null || rows.Count == 0) return null;
            string hex = S0(rows[0], 0) ?? "";
            var data = new byte[hex.Length / 2];
            for (int i = 0; i < data.Length; i++) data[i] = byte.Parse(hex.Substring(i * 2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            string model = ProtoText(data, 10, 1, 28);
            return string.IsNullOrEmpty(model) ? null : model;
        }

        /// <summary>
        /// The text at a path of protobuf field numbers, following the first length-delimited field with each number.
        /// Needs no schema; returns null when a field is missing or the bytes are not a well-formed message.
        /// </summary>
        internal static string ProtoText(byte[] data, params int[] path)
        {
            int start = 0, end = data.Length;
            foreach (int wanted in path)
            {
                int pos = start, found = -1, length = 0;
                while (pos < end && found < 0)
                {
                    ulong key;
                    if (!Varint(data, ref pos, end, out key)) return null;
                    int wire = (int)(key & 7);
                    ulong value;
                    switch (wire)
                    {
                        case 0: if (!Varint(data, ref pos, end, out value)) return null; break;
                        case 1: pos += 8; break;
                        case 5: pos += 4; break;
                        case 2:
                            if (!Varint(data, ref pos, end, out value) || value > (ulong)(end - pos)) return null;
                            if ((int)(key >> 3) == wanted) { found = pos; length = (int)value; }
                            pos += (int)value;
                            break;
                        default: return null;   // Groups are not used by current encoders.
                    }
                    if (pos > end) return null;
                }
                if (found < 0) return null;
                start = found;
                end = found + length;
            }
            try { return new UTF8Encoding(false, true).GetString(data, start, end - start); }
            catch (ArgumentException) { return null; }
        }

        private static bool Varint(byte[] data, ref int pos, int end, out ulong value)
        {
            value = 0;
            for (int shift = 0; shift < 64 && pos < end; shift += 7)
            {
                byte b = data[pos++];
                value |= (ulong)(b & 0x7f) << shift;
                if (b < 0x80) return true;
            }
            return false;
        }

        /// <summary>A Go time as stored by agy, "2026-11-02 12:04:30.669840086+00:00"; fractions of a second are dropped.</summary>
        internal static DateTime? GoTime(string s)
        {
            if (s == null || s.Length < 25) return null;
            DateTimeOffset t;
            if (!DateTimeOffset.TryParseExact(s.Substring(0, 19) + s.Substring(s.Length - 6), "yyyy-MM-dd HH:mm:sszzz", CultureInfo.InvariantCulture, DateTimeStyles.None, out t)) return null;
            // A conversation that never ran is stored as year 1.
            return t.Year < 2000 ? (DateTime?)null : t.LocalDateTime;
        }

        // ================================================================ Qwen Code
        private static IEnumerable<Found> Qwen(Probe p, Place pl)
        {
            string root = pl.Where == "Windows"
                ? (Environment.GetEnvironmentVariable("QWEN_RUNTIME_DIR") ?? Environment.GetEnvironmentVariable("QWEN_HOME") ?? Path.Combine(pl.Home, ".qwen"))
                : Path.Combine(pl.Home, ".qwen");
            var list = new List<Found>();
            foreach (var proj in Probe.SafeDirs(Path.Combine(root, "projects")))
            {
                string chats = Path.Combine(proj, "chats");
                foreach (var fi in Probe.SafeFiles(chats, "*.jsonl").Where(Recent))
                {
                    var f = (Found)p.Cached("agent:qwen:" + fi.FullName, fi, () => QwenFile(fi));
                    if (f == null) continue;
                    // "runtime" file written while the interactive session is open, with the process PID.
                    var rt = Json.ParseFile(Path.Combine(chats, Path.GetFileNameWithoutExtension(fi.Name) + ".runtime.json"));
                    uint pid = (uint)(JPath.Num(JPath.Get(rt, "pid")) ?? 0);
                    bool open = rt != null && (pl.Where != "Windows" || p.Procs.ContainsKey(pid));
                    f.Pending = open;
                    f.Fresh = 2;
                    list.Add(f);
                }
            }
            return list;
        }

        private static Found QwenFile(FileInfo fi)
        {
            var f = new Found { Agent = "Qwen Code", Updated = fi.LastWriteTime };
            foreach (var line in Lines(Probe.TailText(fi.FullName, 262144)))
            {
                var e = Json.ParseAny(line);
                f.Cwd = JPath.Str(JPath.Get(e, "cwd")) ?? f.Cwd;
                string type = JPath.Str(JPath.Get(e, "type")), sub = JPath.Str(JPath.Get(e, "subtype"));
                if (type == "system" && sub == "custom_title") f.Title = JPath.Str(JPath.Get(e, "systemPayload.customTitle")) ?? f.Title;
                f.Model = JPath.Str(JPath.Get(e, "model")) ?? JPath.Str(JPath.Get(e, "executionContext.modelId")) ?? f.Model;
                if (type == "assistant")
                {
                    int ctx = (int)(JPath.Num(JPath.Get(e, "usageMetadata.promptTokenCount")) ?? 0);
                    if (ctx > 0) f.Context = ctx;
                }
            }
            if (f.Title == null) f.Title = FirstUserText(fi.FullName, e => JPath.Str(JPath.Get(e, "type")) == "user" ? JPath.Get(e, "message.parts") : null);
            return f;
        }

        // ================================================================ Goose
        private static IEnumerable<Found> Goose(Probe p, Place pl)
        {
            string db = pl.Where == "Windows"
                ? Path.Combine(Environment.GetEnvironmentVariable("GOOSE_PATH_ROOT") != null ? Path.Combine(Environment.GetEnvironmentVariable("GOOSE_PATH_ROOT"), "data") : Path.Combine(Probe.AppData, @"Block\goose\data"), @"sessions\sessions.db")
                : Path.Combine(pl.Home, @".local\share\goose\sessions\sessions.db");
            if (!File.Exists(db) || !Recent(Stamp(db))) return new Found[0];
            return (List<Found>)p.Cached("agent:goose:" + db, Stamp(db), () =>
            {
                var list = new List<Found>();
                var rows = Sqlite.Rows(Openable(db), "SELECT id, name, working_dir, provider_name, model_config_json, total_tokens, updated_at FROM sessions WHERE archived_at IS NULL ORDER BY updated_at DESC LIMIT 4", 4);
                if (rows == null) return list;
                foreach (var r in rows)
                {
                    DateTime t;
                    var f = new Found { Agent = "Goose", Title = S0(r, 1), Cwd = S0(r, 2), Context = (int)N0(r, 5) };
                    f.Model = JPath.Str(JPath.Get(Json.ParseAny(S0(r, 4) ?? ""), "model_name"));
                    if (DateTime.TryParseExact(S0(r, 6) ?? "", "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out t))
                        f.Updated = t.ToLocalTime();
                    f.Pending = true;
                    f.Fresh = 1;
                    list.Add(f);
                }
                return list;
            });
        }

        // ================================================================ Crush (one database per project, listed in projects.json)
        private static IEnumerable<Found> Crush(Probe p, Place pl)
        {
            string index = pl.Where == "Windows"
                ? Path.Combine(Environment.GetEnvironmentVariable("CRUSH_GLOBAL_DATA") ?? Path.Combine(Probe.LocalAppData, "crush"), "projects.json")
                : Path.Combine(pl.Home, @".local\share\crush\projects.json");
            var projects = JPath.Get(Json.ParseFile(index), "projects") as IList;
            var list = new List<Found>();
            if (projects == null) return list;
            foreach (var proj in projects)
            {
                string dataDir = JPath.Str(JPath.Get(proj, "data_dir"));
                if (string.IsNullOrEmpty(dataDir)) continue;
                if (pl.Where == "WSL") dataDir = pl.Home.Substring(0, pl.Home.IndexOf(@"\home\", StringComparison.Ordinal)) + dataDir.Replace('/', '\\');
                string db = Path.Combine(dataDir, "crush.db");
                if (!File.Exists(db) || !Recent(Stamp(db))) continue;
                string cwd = JPath.Str(JPath.Get(proj, "path"));
                list.AddRange((List<Found>)p.Cached("agent:crush:" + db, Stamp(db), () =>
                {
                    var found = new List<Found>();
                    string path = Openable(db);
                    var rows = Sqlite.Rows(path, "SELECT id, title, prompt_tokens, updated_at FROM sessions WHERE parent_session_id IS NULL ORDER BY updated_at DESC LIMIT 3", 3);
                    if (rows == null) return found;
                    foreach (var r in rows)
                    {
                        var f = new Found { Agent = "Crush", Title = S0(r, 1), Cwd = cwd, Context = (int)N0(r, 2), Updated = Json.FromUnix(N0(r, 3)) ?? DateTime.MinValue };
                        var m = Sqlite.Rows(path, "SELECT model, role, finished_at FROM messages WHERE session_id = " + Sqlite.Quote(S0(r, 0)) + " ORDER BY created_at DESC LIMIT 1", 1);
                        if (m != null && m.Count > 0)
                        {
                            f.Model = S0(m[0], 0);
                            f.Pending = S0(m[0], 1) == "assistant" && m[0][2] == null;
                        }
                        found.Add(f);
                    }
                    return found;
                }));
            }
            return list;
        }

        // ================================================================ Cline CLI
        private static IEnumerable<Found> Cline(Probe p, Place pl)
        {
            string db = Path.Combine(pl.Home, @".cline\data\db\sessions.db");
            if (!File.Exists(db) || !Recent(Stamp(db))) return new Found[0];
            var rows = (List<object[]>)p.Cached("agent:cline:" + db, Stamp(db), () =>
                Sqlite.Rows(Openable(db), "SELECT session_id, status, model, cwd, prompt, updated_at, pid, ended_at FROM sessions WHERE is_subagent = 0 ORDER BY updated_at DESC LIMIT 4", 4) ?? new List<object[]>());
            return rows.Select(r =>
            {
                var f = new Found { Agent = "Cline", Model = S0(r, 2), Cwd = S0(r, 3), Title = S0(r, 4), Updated = JPath.Date(S0(r, 5)) ?? DateTime.MinValue };
                bool alive = r[7] == null && (pl.Where != "Windows" || p.Procs.ContainsKey((uint)N0(r, 6)));
                string status = (S0(r, 1) ?? "").ToLowerInvariant();
                f.Pending = alive && (status == "running" || status == "active" || status == "busy");
                f.Fresh = double.MaxValue;
                return f;
            }).ToList();
        }

        // ================================================================ utilities
        private static IEnumerable<string> Lines(string text)
        {
            return (text ?? "").Split('\n').Select(l => l.Trim().TrimStart('\uFEFF')).Where(l => l.StartsWith("{"));
        }

        /// <summary>The text of the user's first message, used as the title when the session has none.</summary>
        private static string FirstUserText(string path, Func<object, object> content)
        {
            string head = HeadText(path, 65536);
            foreach (var line in Lines(head))
            {
                var c = content(Json.ParseAny(line));
                if (c == null) continue;
                if (c is string) return (string)c;
                var list = c as IList;
                if (list == null) continue;
                foreach (var part in list)
                {
                    string t = JPath.Str(JPath.Get(part, "text"));
                    if (!string.IsNullOrEmpty(t)) return t;
                }
            }
            return null;
        }

        private static string HeadText(string path, int maxBytes)
        {
            try
            {
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    var buf = new byte[Math.Min(maxBytes, fs.Length)];
                    int read = fs.Read(buf, 0, buf.Length);
                    return Encoding.UTF8.GetString(buf, 0, read);
                }
            }
            catch (IOException) { return ""; }
            catch (UnauthorizedAccessException) { return ""; }
        }
    }
}
