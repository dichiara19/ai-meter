using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace AIMeter
{
    /// <summary>
    /// Incremental local usage reader. Source documents exist only while parsing a line; the archive receives
    /// numeric counters and opaque identifiers. Neither prompts, titles, paths nor credentials are retained.
    /// </summary>
    internal sealed class HistoryCollector
    {
        private readonly HistoryStore store;
        private DateTime scannedAt = DateTime.MinValue;
        private const int MaximumBatchBytes = 2 * 1024 * 1024;
        private const int MaximumLineBytes = 1024 * 1024;
        private readonly Dictionary<string, List<string>> codexPaths = new Dictionary<string, List<string>>();
        private DateTime indexedAt = DateTime.MinValue;
        private int remainingBytes = 8 * 1024 * 1024;

        internal HistoryCollector(HistoryStore store) { this.store = store; }

        internal void Collect(Snapshot snapshot, Settings settings, DateTime? pausedSince = null)
        {
            bool scan = (snapshot.Taken - scannedAt).TotalSeconds >= 60;
            store.Record(snapshot, scan ? (Action<HistoryDatabase>)(db => Scan(db, settings, snapshot.Taken)) : null, pausedSince);
            if (scan) scannedAt = snapshot.Taken;
        }

        private void Scan(HistoryDatabase db, Settings settings, DateTime now)
        {
            remainingBytes = 8 * 1024 * 1024;
            var homes = new List<string> { Probe.Home };
            // Probe sets this only after checking that the selected distribution is already running.
            string wslHome = Probe.WslHomeUp;
            if (!string.IsNullOrEmpty(wslHome)) homes.Add(wslHome);
            foreach (string home in homes)
            {
                if (settings.ClaudeEnabled) ScanFiles(db, ClaudeFiles(home), "Claude Code", now);
                if (settings.OpenAIEnabled)
                {
                    List<string> paths;
                    if (!codexPaths.TryGetValue(home, out paths) || (now - indexedAt).TotalHours >= 1)
                    {
                        paths = CodexFiles(home, now).Select(file => file.FullName).ToList();
                        codexPaths[home] = paths;
                    }
                    var current = new List<FileInfo>();
                    for (int day = 0; day <= 1; day++)
                    {
                        DateTime date = now.AddDays(-day);
                        current.AddRange(Probe.SafeFiles(Path.Combine(home, ".codex", "sessions", date.ToString("yyyy"), date.ToString("MM"), date.ToString("dd")), "*.jsonl"));
                    }
                    current.AddRange(paths.Select(path => new FileInfo(path)).Where(file => file.Exists));
                    ScanFiles(db, current.GroupBy(file => file.FullName).Select(group => group.First()), "Codex", now);
                }
                if (!settings.LocalAgents || !settings.LocalEnabled) continue;
                string pi = home == Probe.Home ? Environment.GetEnvironmentVariable("PI_CODING_AGENT_SESSION_DIR")
                    ?? Path.Combine(Environment.GetEnvironmentVariable("PI_CODING_AGENT_DIR") ?? Path.Combine(home, @".pi\agent"), "sessions") : Path.Combine(home, @".pi\agent\sessions");
                ScanFiles(db, Probe.SafeDirs(pi).SelectMany(directory => Probe.SafeFiles(directory, "*.jsonl")), "Pi", now);
                string data = home == Probe.Home ? Environment.GetEnvironmentVariable("XDG_DATA_HOME") ?? Path.Combine(home, @".local\share") : Path.Combine(home, @".local\share");
                ReadOpenCode(db, Path.Combine(data, @"opencode\opencode.db"), "OpenCode", now);
                ReadOpenCode(db, Path.Combine(data, @"kilo\kilo.db"), "Kilo", now);
            }
            if ((now - indexedAt).TotalHours >= 1) indexedAt = now;
        }

        private static IEnumerable<FileInfo> ClaudeFiles(string home)
        {
            foreach (string project in Probe.SafeDirs(Path.Combine(home, @".claude\projects")))
            {
                foreach (var file in Probe.SafeFiles(project, "*.jsonl")) yield return file;
                foreach (string session in Probe.SafeDirs(project))
                    foreach (var file in Probe.SafeFiles(Path.Combine(session, "subagents"), "*.jsonl")) yield return file;
            }
        }

        private static IEnumerable<FileInfo> CodexFiles(string home, DateTime now)
        {
            // Session folders are dated at creation, so include recent modified files in older folders too.
            string root = Path.Combine(home, @".codex\sessions");
            foreach (string year in Probe.SafeDirs(root))
                foreach (string month in Probe.SafeDirs(year))
                    foreach (string day in Probe.SafeDirs(month))
                        foreach (var file in Probe.SafeFiles(day, "*.jsonl"))
                            if (file.LastWriteTime >= now.AddDays(-1)) yield return file;
        }

        private void ScanFiles(HistoryDatabase db, IEnumerable<FileInfo> files, string agent, DateTime now)
        {
            int count = 0;
            var cursors = db.Query("SELECT id,position,modified FROM cursors").ToDictionary(row => (string)row[0]);
            foreach (var file in files.Where(file => file.LastWriteTime >= store.Started.AddMinutes(-1)).OrderByDescending(file => file.LastWriteTime))
            {
                object[] previous;
                string key = HistoryStore.Hash(agent + "|" + file.FullName);
                if (cursors.TryGetValue(key, out previous) && Math.Abs(Convert.ToInt64(previous[1])) == file.Length
                    && Convert.ToInt64(previous[2]) == HistoryStore.Stamp(file.LastWriteTimeUtc)) continue;
                if (++count > 200 || remainingBytes <= 0) { HistoryStore.Gap(db, HistoryStore.Stamp(now), HistoryStore.Stamp(now), "source-file-limit"); break; }
                try { ReadFile(db, file, agent, now); }
                catch (IOException) { HistoryStore.Gap(db, HistoryStore.Stamp(now), HistoryStore.Stamp(now), "source-unavailable"); }
                catch (UnauthorizedAccessException) { HistoryStore.Gap(db, HistoryStore.Stamp(now), HistoryStore.Stamp(now), "source-unavailable"); }
            }
        }

        internal void ReadFile(HistoryDatabase db, FileInfo file, string agent, DateTime now)
        {
            string key = HistoryStore.Hash(agent + "|" + file.FullName);
            var cursor = db.Query("SELECT position,modified FROM cursors WHERE id=?", key);
            long position = cursor.Count == 0 ? 0 : Convert.ToInt64(cursor[0][0]);
            bool discardFirst = position < 0;
            position = Math.Abs(position);
            long modified = HistoryStore.Stamp(file.LastWriteTimeUtc);
            if (cursor.Count > 0 && position == file.Length && Convert.ToInt64(cursor[0][1]) == modified) return;
            if (file.Length < position || (cursor.Count > 0 && position == file.Length))
            {
                position = 0;
                HistoryStore.Gap(db, HistoryStore.Stamp(now), HistoryStore.Stamp(now), "source-rewritten");
            }
            // First encounter only: avoid loading a multi-megabyte historical transcript to find today's usage.
            // Subsequent batches resume exactly, including across a restart; partial lines are never committed.
            if (cursor.Count == 0 && file.Length > MaximumBatchBytes)
            {
                position = file.Length - MaximumBatchBytes;
                discardFirst = true;
                HistoryStore.Gap(db, HistoryStore.Stamp(store.Started), HistoryStore.Stamp(now), "initial-tail-only");
            }
            using (var stream = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                var savedModel = db.Query("SELECT value FROM metadata WHERE key=?", "model:" + key);
                string model = savedModel.Count == 0 ? null : savedModel[0][0] as string;
                stream.Position = position;
                byte[] buffer = new byte[(int)Math.Min(MaximumBatchBytes, stream.Length - position)];
                int read = 0, part;
                while (read < buffer.Length && (part = stream.Read(buffer, read, buffer.Length - read)) > 0) read += part;
                remainingBytes -= read;
                int start = 0;
                for (int i = 0; i < read; i++)
                {
                    if (buffer[i] != 10) continue;
                    int length = i - start;
                    if (discardFirst) discardFirst = false;
                    else if (length <= MaximumLineBytes)
                    {
                        string line = Encoding.UTF8.GetString(buffer, start, length).TrimStart('\uFEFF');
                        if (agent == "Codex")
                        {
                            var context = Json.Parse(line);
                            if (Json.Str(context, "type") == "turn_context") model = JPath.Str(JPath.Get(context, "payload.model")) ?? model;
                        }
                        HistoryEvent item = Parse(line, agent, key);
                        if (item != null && item.At >= store.Started && item.At <= now.AddMinutes(5))
                        {
                            if (item.Model == null) item.Model = model;
                            HistoryStore.AddEvent(db, item);
                        }
                    }
                    else HistoryStore.Gap(db, HistoryStore.Stamp(now), HistoryStore.Stamp(now), "oversized-source-line");
                    start = i + 1;
                }
                if (start == 0 && read == MaximumBatchBytes)
                {
                    // Drop a pathological line without repeatedly allocating or stalling every future scan.
                    // The negative position encodes discard-until-newline across batches.
                    db.Execute("INSERT OR REPLACE INTO cursors VALUES(?,?,?)", key, -(position + read), modified);
                    HistoryStore.Gap(db, HistoryStore.Stamp(now), HistoryStore.Stamp(now), "oversized-source-line");
                    return;
                }
                db.Execute("INSERT OR REPLACE INTO cursors VALUES(?,?,?)", key, position + start, modified);
                if (model != null) db.Execute("INSERT OR REPLACE INTO metadata VALUES(?,?)", "model:" + key, model);
            }
        }

        private static long? Tokens(object usage, string path)
        {
            double? value = JPath.Num(JPath.Get(usage, path));
            return value.HasValue && HistoryStore.Finite(value.Value) && value >= 0 && value <= 9007199254740991 ? (long?)value.Value : null;
        }

        internal static HistoryEvent Parse(string line, string agent, string sessionKey)
        {
            try { return ParseEvent(line, agent, sessionKey); }
            catch (ArgumentException) { return null; }
            catch (FormatException) { return null; }
            catch (OverflowException) { return null; }
            catch (InvalidCastException) { return null; }
        }

        private static HistoryEvent ParseEvent(string line, string agent, string sessionKey)
        {
            var document = Json.Parse(line);
            if (document == null) return null;
            string type = Json.Str(document, "type");
            var item = new HistoryEvent { Agent = agent, SessionId = sessionKey };
            object usage;
            string identity;
            if (agent == "Claude Code")
            {
                if (type != "assistant") return null;
                usage = JPath.Get(document, "message.usage");
                string messageId = JPath.Str(JPath.Get(document, "message.id"));
                if (usage == null || messageId == null) return null;
                item.At = Json.FromIso(Json.Str(document, "timestamp")) ?? DateTime.MinValue;
                item.Provider = "claude";
                item.Model = JPath.Str(JPath.Get(document, "message.model"));
                item.Input = Tokens(usage, "input_tokens"); item.Output = Tokens(usage, "output_tokens");
                item.CacheRead = Tokens(usage, "cache_read_input_tokens"); item.CacheWrite = Tokens(usage, "cache_creation_input_tokens");
                identity = "claude|" + messageId + "|" + Json.Str(document, "requestId");
            }
            else if (agent == "Codex")
            {
                if (type != "event_msg" || JPath.Str(JPath.Get(document, "payload.type")) != "token_count") return null;
                usage = JPath.Get(document, "payload.info.last_token_usage");
                object total = JPath.Get(document, "payload.info.total_token_usage");
                if (usage == null || total == null) return null;
                item.At = Json.FromIso(Json.Str(document, "timestamp")) ?? DateTime.MinValue;
                item.Provider = "openai";
                item.Model = JPath.Str(JPath.Get(document, "payload.info.model"));
                long? input = Tokens(usage, "input_tokens");
                item.CacheRead = Tokens(usage, "cached_input_tokens");
                item.Input = input.HasValue && item.CacheRead.HasValue ? Math.Max(0, input.Value - item.CacheRead.Value) : input;
                item.Output = Tokens(usage, "output_tokens");
                // Codex can emit the same cumulative state again after a turn ends.
                identity = "codex|" + sessionKey + "|" + Json.Serialize(total);
            }
            else if (agent == "Pi")
            {
                if (type != "message" || JPath.Str(JPath.Get(document, "message.role")) != "assistant") return null;
                usage = JPath.Get(document, "message.usage");
                if (usage == null) return null;
                item.At = Json.FromUnixMs(JPath.Num(JPath.Get(document, "message.timestamp"))) ?? Json.FromIso(Json.Str(document, "timestamp")) ?? DateTime.MinValue;
                item.Provider = ProviderId(JPath.Str(JPath.Get(document, "message.provider")));
                item.Model = JPath.Str(JPath.Get(document, "message.model"));
                item.Input = Tokens(usage, "input"); item.Output = Tokens(usage, "output");
                item.CacheRead = Tokens(usage, "cacheRead"); item.CacheWrite = Tokens(usage, "cacheWrite");
                double? cost = JPath.Num(JPath.Get(usage, "cost.total"));
                if (cost.HasValue && HistoryStore.Finite(cost.Value) && cost >= 0) item.Cost = cost;
                // Forks preserve the message even when entry IDs and parent links change. Hash only in memory.
                identity = "pi|" + HistoryStore.Stamp(item.At).ToString(CultureInfo.InvariantCulture) + "|" + Json.Serialize(JPath.Get(document, "message"));
            }
            else return null;
            if (item.Input == null && item.Output == null && item.CacheRead == null && item.CacheWrite == null && item.Cost == null) return null;
            item.Id = HistoryStore.Hash(identity);
            return item;
        }

        private static string ProviderId(string provider)
        {
            if (provider == "anthropic") return "claude";
            if (provider == "openai-codex") return "openai";
            return provider ?? "unknown";
        }

        private void ReadOpenCode(HistoryDatabase db, string path, string agent, DateTime now)
        {
            if (!File.Exists(path)) return;
            try
            {
                string key = HistoryStore.Hash(agent + "|" + path);
                var file = new FileInfo(File.Exists(path + "-wal") ? path + "-wal" : path);
                long modified = HistoryStore.Stamp(file.LastWriteTimeUtc);
                var stamp = db.Query("SELECT position,modified FROM cursors WHERE id=?", key);
                if (stamp.Count > 0 && Convert.ToInt64(stamp[0][0]) == file.Length && Convert.ToInt64(stamp[0][1]) == modified) return;
                long since = (long)(store.Started.ToUniversalTime() - new DateTime(1970, 1, 1)).TotalMilliseconds;
                // Revisit the latest responses: database messages can be updated in place during streaming.
                var rows = Sqlite.Rows(CodingAgents.Openable(path), "SELECT id,session_id,data,time_created FROM message WHERE time_created >= " + since.ToString(CultureInfo.InvariantCulture) + " ORDER BY time_created DESC LIMIT 5000", 5000);
                if (rows == null) { HistoryStore.Gap(db, HistoryStore.Stamp(now), HistoryStore.Stamp(now), "source-unavailable"); return; }
                if (rows.Count == 5000) HistoryStore.Gap(db, HistoryStore.Stamp(store.Started), HistoryStore.Stamp(now), "source-row-limit");
                foreach (var row in rows)
                {
                    var message = Json.Parse(Convert.ToString(row[2], CultureInfo.InvariantCulture));
                    if (Json.Str(message, "role") != "assistant") continue;
                    object usage = JPath.Get(message, "tokens");
                    var item = new HistoryEvent { Id = HistoryStore.Hash(key + "|" + row[0]), SessionId = HistoryStore.Hash(key + "|" + row[1]),
                        Agent = agent, Provider = ProviderId(Json.Str(message, "providerID")), Model = Json.Str(message, "modelID"),
                        At = Json.FromUnixMs(JPath.Num(row[3])) ?? DateTime.MinValue,
                        Input = Tokens(usage, "input"), Output = Tokens(usage, "output"), CacheRead = Tokens(usage, "cache.read"), CacheWrite = Tokens(usage, "cache.write") };
                    double? cost = Json.Num(message, "cost");
                    if (cost.HasValue && HistoryStore.Finite(cost.Value) && cost >= 0) item.Cost = cost;
                    HistoryStore.AddEvent(db, item);
                }
                db.Execute("INSERT OR REPLACE INTO cursors VALUES(?,?,?)", key, file.Length, modified);
            }
            catch (IOException) { HistoryStore.Gap(db, HistoryStore.Stamp(now), HistoryStore.Stamp(now), "source-unavailable"); }
            catch (UnauthorizedAccessException) { HistoryStore.Gap(db, HistoryStore.Stamp(now), HistoryStore.Stamp(now), "source-unavailable"); }
        }
    }
}
