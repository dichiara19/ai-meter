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
    /// Sessioni delle CLI di programmazione (OpenCode, Pi, Gemini CLI, Qwen Code, Goose, Crush, Cline, Kilo): titolo,
    /// cartella, modello usato e contesto occupato, dai loro file locali su Windows e in WSL (se è già acceso).
    /// Si mostrano le sessioni attive negli ultimi 30 minuti. Solo lettura.
    /// </summary>
    internal sealed class CodingAgents
    {
        private const int RecentMinutes = 30;
        private readonly Settings S;
        private List<Session> last = new List<Session>();
        private DateTime scannedAt = DateTime.MinValue;

        /// <summary>Una sessione letta dai file, prima di diventare una riga dell'interfaccia.</summary>
        private sealed class Found
        {
            public string Agent, Title, Model, Cwd;
            public DateTime Updated;
            public int Context;
            /// <summary>Il contenuto dice che la risposta è in corso; vale solo se il file è stato scritto da meno di Fresh minuti.</summary>
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

        /// <summary>Sessioni trovate in una cartella utente qualsiasi (usato anche per provare i formati).</summary>
        internal List<Session> ScanHome(Probe p, string home) { return Scan(p, new List<Place> { new Place { Home = home, Where = "Windows" } }); }

        private List<Session> Scan(Probe p, List<Place> places)
        {
            var all = new List<Tuple<Found, string>>();
            foreach (var pl in places)
            {
                Action<IEnumerable<Found>> add = items => { foreach (var f in items) all.Add(Tuple.Create(f, pl.Where)); };
                try { add(OpenCodeLike(p, pl, "OpenCode", @".local\share\opencode", "opencode.db")); } catch (Exception) { }
                try { add(OpenCodeLike(p, pl, "Kilo", @".local\share\kilo", "kilo.db")); } catch (Exception) { }
                try { add(Pi(p, pl)); } catch (Exception) { }
                try { add(Gemini(p, pl)); } catch (Exception) { }
                try { add(Qwen(p, pl)); } catch (Exception) { }
                try { add(Goose(p, pl)); } catch (Exception) { }
                try { add(Crush(p, pl)); } catch (Exception) { }
                try { add(Cline(p, pl)); } catch (Exception) { }
            }
            return all.Where(t => (DateTime.Now - t.Item1.Updated).TotalMinutes <= RecentMinutes)
                .OrderByDescending(t => t.Item1.Updated).Take(12)
                .Select(t => new Session
                {
                    Kind = t.Item2 == "WSL" ? t.Item1.Agent + " · WSL" : t.Item1.Agent,
                    Title = string.IsNullOrEmpty(t.Item1.Title) ? L.T("Untitled session") : Shorten(t.Item1.Title),
                    Model = t.Item1.Model,
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

        // ================================================================ database SQLite
        /// <summary>
        /// Il database da aprire. Quelli in WAL dentro WSL non si aprono via \\wsl.localhost (memoria condivisa non
        /// disponibile): se ne copia il file con il suo WAL in una cartella temporanea, solo quando cambiano.
        /// </summary>
        private static string Openable(string path)
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

        /// <summary>Il file che cambia a ogni scrittura: il WAL se c'è, altrimenti il database.</summary>
        private static FileInfo Stamp(string db)
        {
            var wal = new FileInfo(db + "-wal");
            return wal.Exists && wal.Length > 0 ? wal : new FileInfo(db);
        }

        private static string S0(object[] row, int i) { return row.Length > i && row[i] != null ? Convert.ToString(row[i], CultureInfo.InvariantCulture) : null; }
        private static double N0(object[] row, int i) { return row.Length > i ? (JPath.Num(row[i]) ?? 0) : 0; }

        // ================================================================ OpenCode (e Kilo, che ne è un fork)
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
                    // ultimo messaggio: modello effettivo, token e se la risposta è ancora in corso
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
        private static IEnumerable<Found> Pi(Probe p, Place pl)
        {
            string root = pl.Where == "Windows"
                ? (Environment.GetEnvironmentVariable("PI_CODING_AGENT_SESSION_DIR")
                   ?? Path.Combine(Environment.GetEnvironmentVariable("PI_CODING_AGENT_DIR") ?? Path.Combine(pl.Home, @".pi\agent"), "sessions"))
                : Path.Combine(pl.Home, @".pi\agent\sessions");
            var list = new List<Found>();
            foreach (var dir in Probe.SafeDirs(root))
                foreach (var fi in Probe.SafeFiles(dir, "*.jsonl").Where(Recent))
                {
                    var f = (Found)p.Cached("agent:pi:" + fi.FullName, fi, () => PiFile(fi));
                    if (f != null) list.Add(f);
                }
            return list;
        }

        private static Found PiFile(FileInfo fi)
        {
            var f = new Found { Agent = "Pi", Updated = fi.LastWriteTime };
            var header = Json.ParseAny(Probe.ReadFirstLine(fi.FullName, 65536) ?? "");
            f.Cwd = JPath.Str(JPath.Get(header, "cwd"));
            string lastRole = null, lastStop = null;
            foreach (var line in Lines(Probe.TailText(fi.FullName, 262144)))
            {
                var e = Json.ParseAny(line);
                string type = JPath.Str(JPath.Get(e, "type"));
                if (type == "session_info" && JPath.Str(JPath.Get(e, "name")) != null) f.Title = JPath.Str(JPath.Get(e, "name"));
                else if (type == "model_change") f.Model = JPath.Str(JPath.Get(e, "modelId")) ?? f.Model;
                else if (type == "message")
                {
                    lastRole = JPath.Str(JPath.Get(e, "message.role"));
                    if (lastRole != "assistant") continue;
                    lastStop = JPath.Str(JPath.Get(e, "message.stopReason"));
                    f.Model = JPath.Str(JPath.Get(e, "message.model")) ?? f.Model;
                    var u = JPath.Get(e, "message.usage");
                    // stessa formula di Pi; le risposte interrotte o con errore non contano
                    if (u != null && lastStop != "aborted" && lastStop != "error")
                    {
                        int ctx = (int)(JPath.Num(JPath.Get(u, "totalTokens")) ?? 0);
                        if (ctx == 0) ctx = (int)((JPath.Num(JPath.Get(u, "input")) ?? 0) + (JPath.Num(JPath.Get(u, "output")) ?? 0) + (JPath.Num(JPath.Get(u, "cacheRead")) ?? 0) + (JPath.Num(JPath.Get(u, "cacheWrite")) ?? 0));
                        if (ctx > 0) f.Context = ctx;
                    }
                }
            }
            if (f.Title == null) f.Title = FirstUserText(fi.FullName, e => JPath.Str(JPath.Get(e, "message.role")) == "user" ? JPath.Get(e, "message.content") : null);
            // il ciclo dell'agente è in corso se l'ultimo messaggio non è una risposta conclusa
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
                    // file "runtime" scritto finché la sessione interattiva è aperta, con il PID del processo
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

        // ================================================================ Crush (un database per progetto, elencati in projects.json)
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

        // ================================================================ utilità
        private static IEnumerable<string> Lines(string text)
        {
            return (text ?? "").Split('\n').Select(l => l.Trim().TrimStart('\uFEFF')).Where(l => l.StartsWith("{"));
        }

        /// <summary>Il testo del primo messaggio dell'utente, come titolo quando la sessione non ne ha uno.</summary>
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
