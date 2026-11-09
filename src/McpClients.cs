using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace AIMeter
{
    /// <summary>
    /// AI apps and coding agents that can start AI Meter's MCP server, and the edits that connect or disconnect them.
    /// Each edit touches only AI Meter's own entry ("ai-meter"), keeps a copy of the previous file next to it and
    /// refuses files it cannot parse (for example JSON with comments) instead of rewriting them.
    /// </summary>
    internal static class McpClients
    {
        public const string EntryName = "ai-meter";

        /// <summary>How a client stores its MCP servers.</summary>
        internal enum Format
        {
            McpServers,   // { "mcpServers": { name: { command, args } } }: Claude, Cursor, Antigravity CLI, Pi's MCP adapter.
            VsCode,       // { "servers": { name: { type, command, args } } }
            OpenCode,     // { "mcp": { name: { type: "local", command: [..], enabled } } }
            CodexToml,    // [mcp_servers.name] command = "..", args = [..]
        }

        internal sealed class Client
        {
            public string Id, Name, Where;   // Where: "Windows" or "WSL".
            public string ConfigPath;        // File to edit, as a Windows path.
            public string Marker;            // File or folder whose presence shows the client is installed.
            public Format Format;
            public bool StdioType;           // Whether the entry needs "type": "stdio" (Claude Code).
            public string Note;              // Extra requirement shown with the client, in English.
            // How to make the client create its own file, in English. Set for files in the user's AppData: there the
            // Store build can change a file that exists, but a file it creates lands in the package's private folder,
            // where the client never sees it (AppData write virtualisation).
            public string CreateFirst;

            public bool Wsl { get { return Where == "WSL"; } }
            public bool Installed { get { return File.Exists(Marker) || Directory.Exists(Marker); } }
        }

        /// <summary>Error that the Settings page shows to the user as it is.</summary>
        internal sealed class ConfigException : Exception
        {
            public ConfigException(string message) : base(message) { }
        }

        // ------------------------------------------------------------ known clients

        /// <summary>Clients on Windows, and in WSL when a home folder there is readable (WSL already running).</summary>
        public static List<Client> Known(string windowsHome, string appData, string wslHome)
        {
            var list = new List<Client>
            {
                new Client { Id = "claude-desktop", Name = "Claude Desktop", Where = "Windows", Format = Format.McpServers,
                             ConfigPath = Path.Combine(appData, @"Claude\claude_desktop_config.json"), Marker = Path.Combine(appData, "Claude"),
                             CreateFirst = ClaudeDesktopCreateFirst },
                new Client { Id = "vscode", Name = "Visual Studio Code", Where = "Windows", Format = Format.VsCode,
                             ConfigPath = Path.Combine(appData, @"Code\User\mcp.json"), Marker = Path.Combine(appData, @"Code\User"),
                             CreateFirst = VsCodeCreateFirst },
            };
            list.AddRange(HomeClients(windowsHome, "Windows"));
            if (wslHome != null) list.AddRange(HomeClients(wslHome, "WSL").Where(c => c.Id != "cursor"));
            return list;
        }

        internal const string ClaudeDesktopCreateFirst = "In Claude Desktop, open Settings → Developer → Edit Config.";
        internal const string VsCodeCreateFirst = "In Visual Studio Code, run MCP: Open User Configuration from the Command Palette and save the file.";
        internal const string CreateFirstMessage = "{0} has no configuration file yet, and AI Meter from the Microsoft Store can change only files that already exist. {1} Then connect it again.";

        /// <summary>Clients configured in the user's home folder, the same on Windows and in Linux.</summary>
        private static IEnumerable<Client> HomeClients(string home, string where)
        {
            yield return new Client { Id = "claude-code", Name = "Claude Code", Where = where, Format = Format.McpServers, StdioType = true,
                                      ConfigPath = Path.Combine(home, ".claude.json"), Marker = Path.Combine(home, ".claude.json") };
            yield return new Client { Id = "codex", Name = "Codex", Where = where, Format = Format.CodexToml,
                                      ConfigPath = Path.Combine(home, @".codex\config.toml"), Marker = Path.Combine(home, ".codex") };
            // Antigravity CLI (agy) keeps its own data in .gemini\antigravity-cli and its MCP servers in a file it may create
            // empty, read as {}. The bare .gemini folder is not a marker: the discontinued Gemini CLI created it too.
            yield return new Client { Id = "antigravity-cli", Name = "Antigravity CLI", Where = where, Format = Format.McpServers,
                                      ConfigPath = Path.Combine(home, @".gemini\config\mcp_config.json"), Marker = Path.Combine(home, @".gemini\antigravity-cli") };
            string openCode = Path.Combine(home, @".config\opencode");
            yield return new Client { Id = "opencode", Name = "OpenCode", Where = where, Format = Format.OpenCode,
                                      ConfigPath = File.Exists(Path.Combine(openCode, "opencode.jsonc")) && !File.Exists(Path.Combine(openCode, "opencode.json"))
                                          ? Path.Combine(openCode, "opencode.jsonc") : Path.Combine(openCode, "opencode.json"),
                                      Marker = openCode };
            yield return new Client { Id = "pi", Name = "Pi", Where = where, Format = Format.McpServers,
                                      ConfigPath = Path.Combine(home, @".pi\agent\mcp.json"), Marker = Path.Combine(home, @".pi\agent"),
                                      Note = "Needs the pi-mcp-adapter extension: pi install npm:pi-mcp-adapter" };
            yield return new Client { Id = "cursor", Name = "Cursor", Where = where, Format = Format.McpServers,
                                      ConfigPath = Path.Combine(home, @".cursor\mcp.json"), Marker = Path.Combine(home, ".cursor") };
        }

        // ------------------------------------------------------------ command

        /// <summary>Windows PowerShell, as Windows or Linux (WSL interop) sees it.</summary>
        public static string PowerShell(bool wsl)
        {
            string windows = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"WindowsPowerShell\v1.0\powershell.exe");
            return wsl ? ToWsl(windows) : windows;
        }

        public static List<string> Arguments(string relay)
        {
            return new List<string> { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", relay };
        }

        /// <summary>C:\Windows\x as /mnt/c/Windows/x.</summary>
        internal static string ToWsl(string windowsPath)
        {
            return "/mnt/" + char.ToLowerInvariant(windowsPath[0]) + windowsPath.Substring(2).Replace('\\', '/');
        }

        // ------------------------------------------------------------ connect, disconnect, state

        public static bool IsConnected(Client c)
        {
            if (!File.Exists(c.ConfigPath)) return false;
            if (c.Format == Format.CodexToml) return HasTomlEntry(File.ReadAllText(c.ConfigPath, Encoding.UTF8));
            var doc = Json.Parse(File.ReadAllText(c.ConfigPath, Encoding.UTF8));
            var servers = Json.Obj(doc, ServersKey(c.Format));
            return servers != null && servers.ContainsKey(EntryName);
        }

        public static void Connect(Client c, string relay)
        {
            if (Native.IsPackaged && c.CreateFirst != null && !File.Exists(c.ConfigPath))
                throw new ConfigException(L.F(CreateFirstMessage, c.Name, L.T(c.CreateFirst)));
            string command = PowerShell(c.Wsl);
            var args = Arguments(relay);
            string text = File.Exists(c.ConfigPath) ? File.ReadAllText(c.ConfigPath, Encoding.UTF8) : null;
            Save(c, c.Format == Format.CodexToml ? WithToml(text, command, args) : WithJson(c, text, command, args));
        }

        public static void Disconnect(Client c)
        {
            if (!File.Exists(c.ConfigPath)) return;
            string text = File.ReadAllText(c.ConfigPath, Encoding.UTF8);
            Save(c, c.Format == Format.CodexToml ? WithoutToml(text) : WithoutJson(c, text));
        }

        /// <summary>The entry a person can paste into a client that AI Meter does not edit.</summary>
        public static string Snippet(string relay)
        {
            var entry = new Dictionary<string, object> { { "command", PowerShell(false) }, { "args", Arguments(relay) } };
            return JsonText.Indented(new Dictionary<string, object> { { "mcpServers", new Dictionary<string, object> { { EntryName, entry } } } });
        }

        // ------------------------------------------------------------ command-line installation

        /// <summary>Command-line agents whose own CLI can add an MCP server, by client id and display name.</summary>
        public static readonly string[][] CommandLineClients =
        {
            new[] { "claude-code", "Claude Code" },
            new[] { "codex", "Codex" },
            new[] { "antigravity-cli", "Antigravity CLI" },
            new[] { "hermes", "Hermes" },
        };

        /// <summary>
        /// The agent's own "mcp add" command, for Windows PowerShell or for a WSL (bash) shell.
        /// Claude Code and Codex take the server command after "--"; Antigravity CLI takes it before, with its
        /// dash-prefixed arguments after "--". In PowerShell the separator is quoted: PowerShell drops a bare "--"
        /// when the agent is an npm-installed .ps1 shim, and the agent would then read -NoProfile as its own option.
        /// Hermes takes --command and then --args, which must come last and keeps every following word, dashes included.
        /// </summary>
        public static string Command(string clientId, string relay, bool wsl)
        {
            Func<string, string> quote = wsl
                ? (Func<string, string>)(s => "'" + s.Replace("'", "'\\''") + "'")
                : s => "'" + s.Replace("'", "''") + "'";
            string separator = wsl ? "--" : "'--'";
            var args = Arguments(relay);
            args[args.Count - 1] = quote(relay);
            string powerShell = quote(PowerShell(wsl));
            string flags = string.Join(" ", args);
            switch (clientId)
            {
                case "claude-code": return "claude mcp add --scope user " + EntryName + " " + separator + " " + powerShell + " " + flags;
                case "codex": return "codex mcp add " + EntryName + " " + separator + " " + powerShell + " " + flags;
                case "antigravity-cli": return "agy mcp add " + EntryName + " " + powerShell + " " + separator + " " + flags;
                case "hermes": return "hermes mcp add " + EntryName + " --command " + powerShell + " --args " + flags;
                default: throw new ArgumentException("No command-line installation for " + clientId, "clientId");
            }
        }

        // ------------------------------------------------------------ JSON

        private static string ServersKey(Format f)
        {
            return f == Format.VsCode ? "servers" : f == Format.OpenCode ? "mcp" : "mcpServers";
        }

        internal static string WithJson(Client c, string text, string command, List<string> args)
        {
            var doc = ParseConfig(c, text) ?? new Dictionary<string, object>();
            string key = ServersKey(c.Format);
            var servers = Json.Obj(doc, key);
            if (servers == null) { servers = new Dictionary<string, object>(); doc[key] = servers; }
            Dictionary<string, object> entry;
            if (c.Format == Format.OpenCode)
                entry = new Dictionary<string, object> { { "type", "local" }, { "command", new List<string> { command }.Concat(args).ToList() }, { "enabled", true } };
            else
            {
                entry = new Dictionary<string, object>();
                if (c.StdioType || c.Format == Format.VsCode) entry["type"] = "stdio";
                entry["command"] = command;
                entry["args"] = args;
            }
            servers[EntryName] = entry;
            return JsonText.Indented(doc) + "\n";
        }

        internal static string WithoutJson(Client c, string text)
        {
            var doc = ParseConfig(c, text);
            var servers = Json.Obj(doc, ServersKey(c.Format));
            if (servers == null || !servers.Remove(EntryName)) return null;
            return JsonText.Indented(doc) + "\n";
        }

        private static Dictionary<string, object> ParseConfig(Client c, string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            var doc = Json.Parse(text);
            if (doc == null) throw new ConfigException(L.F("{0} could not be read as plain JSON (it may contain comments). Add AI Meter by hand with the configuration below.", c.ConfigPath));
            return doc;
        }

        // ------------------------------------------------------------ Codex TOML

        // Codex's config is edited line by line, not parsed and rewritten, so that comments and formatting stay as the
        // user wrote them. ScanToml follows strings, comments, arrays and inline tables across lines, so that only a real
        // table header starts or ends AI Meter's section; a file it cannot follow is refused, and every edited text is
        // scanned again before it is written.

        internal const string UnreadableToml = "Codex's config.toml contains text that AI Meter cannot follow safely, such as an unclosed string or array, or AI Meter's entry written in another form. Edit the file by hand with the configuration below.";
        internal const string UncheckedToml = "AI Meter's edit of Codex's config.toml did not pass its own check, so the file was left as it was. Edit it by hand with the configuration below.";

        // [mcp_servers.ai-meter] and its sub-tables, such as [mcp_servers.ai-meter.env]. Only a fallback for showing the
        // state of a file ScanToml cannot follow: unlike the scanner, it is fooled by such a line inside a string.
        private static readonly Regex TomlHeader = new Regex(@"^\s*\[\s*mcp_servers\s*\.\s*(ai-meter|""ai-meter"")\s*(\.[^\]]*)?\]\s*(#.*)?$", RegexOptions.Multiline);

        /// <summary>Table headers found by ScanToml, by line.</summary>
        private sealed class TomlScan
        {
            public bool[] Headers;       // Lines that are [table] or [[array.of.tables]] headers.
            public bool[] Own;           // Header lines of AI Meter's table or of its sub-tables.
            public int OwnTables;        // [mcp_servers.ai-meter] headers themselves.
            public int OwnSubTables;     // Headers below it, such as [mcp_servers.ai-meter.env].
        }

        /// <summary>Where the scan is between lines.</summary>
        private sealed class TomlState
        {
            public char Quote;           // '"' inside a """ string, '\'' inside a ''' string, otherwise '\0'.
            public int Depth;            // Arrays and inline tables still open.
        }

        /// <summary>Whether the text has AI Meter's table or one of its sub-tables.</summary>
        internal static bool HasTomlEntry(string text)
        {
            var scan = ScanToml(TomlLines(text));
            return scan != null ? scan.OwnTables + scan.OwnSubTables > 0 : TomlHeader.IsMatch(text);
        }

        internal static string WithToml(string text, string command, List<string> args)
        {
            string rest = (WithoutToml(text ?? "") ?? text ?? "").TrimEnd('\r', '\n');
            var sb = new StringBuilder(rest);
            if (rest.Length > 0) sb.Append("\n\n");
            sb.Append("[mcp_servers.").Append(EntryName).Append("]\n");
            sb.Append("command = ").Append(TomlString(command)).Append('\n');
            sb.Append("args = [").Append(string.Join(", ", args.Select(TomlString))).Append("]\n");
            string result = sb.ToString();
            CheckToml(result, true);
            return result;
        }

        /// <summary>
        /// The text without AI Meter's tables, or null when there were none. Throws ConfigException for a file the
        /// scanner cannot follow, even when there is nothing to remove, since WithToml relies on that check.
        /// </summary>
        internal static string WithoutToml(string text)
        {
            var lines = TomlLines(text);
            var scan = ScanToml(lines);
            if (scan == null) throw new ConfigException(L.T(UnreadableToml));
            var kept = new List<string>();
            bool inOwn = false, removed = false;
            for (int n = 0; n < lines.Length; n++)
            {
                if (scan.Headers[n]) inOwn = scan.Own[n];
                if (inOwn) { removed = true; continue; }
                kept.Add(lines[n]);
            }
            if (!removed) return null;
            string result = string.Join("\n", kept).TrimEnd('\n') + "\n";
            CheckToml(result, false);
            return result;
        }

        /// <summary>Refuse an edited text that the scanner cannot follow, or that lacks exactly one AI Meter table (none when disconnected).</summary>
        private static void CheckToml(string text, bool connected)
        {
            var scan = ScanToml(TomlLines(text));
            if (scan == null || scan.OwnTables != (connected ? 1 : 0) || scan.OwnSubTables != 0) throw new ConfigException(L.T(UncheckedToml));
        }

        private static string[] TomlLines(string text)
        {
            return text.Replace("\r\n", "\n").Split('\n');
        }

        /// <summary>
        /// Find the table headers, or return null for anything this scanner cannot classify: an unclosed string, array
        /// or inline table, a line that is neither a header nor a key/value pair, or AI Meter's entry defined in a form
        /// the edit would clash with (dotted keys, an inline table, an array of tables). It is not a full TOML parser:
        /// values are only followed far enough to know where strings, comments and brackets end.
        /// </summary>
        private static TomlScan ScanToml(string[] lines)
        {
            var scan = new TomlScan { Headers = new bool[lines.Length], Own = new bool[lines.Length] };
            var state = new TomlState();
            var table = new List<string>();
            bool inOwn = false;
            for (int n = 0; n < lines.Length; n++)
            {
                string line = lines[n];
                int i = 0;
                if (state.Quote == '\0' && state.Depth == 0)
                {
                    // At top level a line starts a statement: a header, a key/value pair, a comment or nothing.
                    i = SkipTomlSpace(line, 0);
                    if (i == line.Length || line[i] == '#') continue;
                    if (line[i] == '[')
                    {
                        bool array = i + 1 < line.Length && line[i + 1] == '[';
                        i += array ? 2 : 1;
                        var path = ReadTomlKey(line, ref i);
                        string close = array ? "]]" : "]";
                        if (path == null || string.CompareOrdinal(line, i, close, 0, close.Length) != 0) return null;
                        i = SkipTomlSpace(line, i + close.Length);
                        if (i < line.Length && line[i] != '#') return null;
                        if (array && ClashesWithOwn(path)) return null;
                        bool own = path.Count >= 2 && path[0] == "mcp_servers" && path[1] == EntryName;
                        scan.Headers[n] = true;
                        scan.Own[n] = own;
                        if (own && path.Count == 2) scan.OwnTables++;
                        else if (own) scan.OwnSubTables++;
                        table = path;
                        inOwn = own;
                        continue;
                    }
                    var key = ReadTomlKey(line, ref i);
                    if (key == null || i == line.Length || line[i] != '=') return null;
                    if (!inOwn && ClashesWithOwn(table.Concat(key).ToList())) return null;
                    i++;
                }
                if (!ScanTomlValue(line, i, state)) return null;
            }
            return state.Quote == '\0' && state.Depth == 0 ? scan : null;
        }

        /// <summary>
        /// Whether a key defined outside AI Meter's tables, or an array of tables, would clash with [mcp_servers.ai-meter]:
        /// mcp_servers itself (as an inline table or another value) or anything at or below mcp_servers.ai-meter.
        /// </summary>
        private static bool ClashesWithOwn(List<string> path)
        {
            return path.Count >= 1 && path[0] == "mcp_servers" && (path.Count == 1 || path[1] == EntryName);
        }

        /// <summary>
        /// Follow the rest of a line from i, updating whether a multi-line string, an array or an inline table is still
        /// open; false for a single-line string left unclosed or a closing bracket with nothing open.
        /// </summary>
        private static bool ScanTomlValue(string line, int i, TomlState state)
        {
            while (i < line.Length)
            {
                char ch = line[i];
                if (state.Quote != '\0')
                {
                    // An escape in a """ string, including \""" and a line-ending backslash, never closes it.
                    if (ch == '\\' && state.Quote == '"') { i += 2; continue; }
                    int run = TomlRun(line, i, state.Quote);
                    if (run >= 3)
                    {
                        if (run > 5) return false;   // Up to two quotes may stand just before the closing three.
                        state.Quote = '\0';
                    }
                    i += Math.Max(run, 1);
                    continue;
                }
                if (ch == '#') return true;   // A comment runs to the end of the line.
                if (ch == '"' || ch == '\'')
                {
                    if (TomlRun(line, i, ch) >= 3) { state.Quote = ch; i += 3; continue; }
                    i = EndOfTomlString(line, i);
                    if (i < 0) return false;
                    continue;
                }
                if (ch == '[' || ch == '{') state.Depth++;
                else if ((ch == ']' || ch == '}') && --state.Depth < 0) return false;
                i++;
            }
            return true;
        }

        /// <summary>
        /// Read a dotted key, such as mcp_servers."ai-meter".env, from i, leaving i after it and the spaces that follow;
        /// null when there is none. Quoted parts are unescaped, so that "ai-meter" is recognised as ai-meter.
        /// </summary>
        private static List<string> ReadTomlKey(string line, ref int i)
        {
            var path = new List<string>();
            while (true)
            {
                i = SkipTomlSpace(line, i);
                if (i == line.Length) return null;
                char ch = line[i];
                string part;
                if (ch == '"' || ch == '\'')
                {
                    int end = EndOfTomlString(line, i);
                    if (end < 0) return null;
                    part = line.Substring(i + 1, end - i - 2);
                    if (ch == '"') part = UnescapeToml(part);
                    if (part == null) return null;
                    i = end;
                }
                else
                {
                    int start = i;
                    while (i < line.Length && IsBareKeyChar(line[i])) i++;
                    if (i == start) return null;
                    part = line.Substring(start, i - start);
                }
                path.Add(part);
                i = SkipTomlSpace(line, i);
                if (i == line.Length || line[i] != '.') return path;
                i++;
            }
        }

        private static bool IsBareKeyChar(char ch)
        {
            return ch >= 'A' && ch <= 'Z' || ch >= 'a' && ch <= 'z' || ch >= '0' && ch <= '9' || ch == '_' || ch == '-';
        }

        /// <summary>A basic string's content with its escapes resolved, or null for an escape TOML does not define.</summary>
        private static string UnescapeToml(string s)
        {
            if (s.IndexOf('\\') < 0) return s;
            var sb = new StringBuilder();
            for (int i = 0; i < s.Length; i++)
            {
                if (s[i] != '\\') { sb.Append(s[i]); continue; }
                if (++i == s.Length) return null;
                int simple = "btnfre\"\\".IndexOf(s[i]);
                if (simple >= 0) { sb.Append("\b\t\n\f\r\u001B\"\\"[simple]); continue; }
                int digits = s[i] == 'x' ? 2 : s[i] == 'u' ? 4 : s[i] == 'U' ? 8 : 0;
                if (digits == 0 || i + digits >= s.Length) return null;
                long code = 0;
                for (int k = 1; k <= digits; k++)
                {
                    if (!Uri.IsHexDigit(s[i + k])) return null;
                    code = code * 16 + Convert.ToInt32(s[i + k].ToString(), 16);
                }
                if (code > 0x10FFFF || code >= 0xD800 && code <= 0xDFFF) return null;
                sb.Append(char.ConvertFromUtf32((int)code));
                i += digits;
            }
            return sb.ToString();
        }

        /// <summary>The index just after a single-line string that starts at i, or -1 when it is not closed on the line.</summary>
        private static int EndOfTomlString(string line, int i)
        {
            char quote = line[i];
            for (int j = i + 1; j < line.Length; j++)
            {
                if (quote == '"' && line[j] == '\\') j++;
                else if (line[j] == quote) return j + 1;
            }
            return -1;
        }

        /// <summary>How many times ch repeats from i.</summary>
        private static int TomlRun(string line, int i, char ch)
        {
            int j = i;
            while (j < line.Length && line[j] == ch) j++;
            return j - i;
        }

        private static int SkipTomlSpace(string line, int i)
        {
            while (i < line.Length && (line[i] == ' ' || line[i] == '\t')) i++;
            return i;
        }

        private static string TomlString(string s)
        {
            return "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
        }

        // ------------------------------------------------------------ files

        /// <summary>
        /// Write the new text through a temporary file, keeping the previous content as a backup. Whatever fails, the
        /// temporary file is removed and the previous content survives, in the file itself or in the backup.
        /// </summary>
        private static void Save(Client c, string text)
        {
            if (text == null) return;   // Nothing to change.
            if (c.Wsl) text = text.Replace("\r\n", "\n");
            else text = text.Replace("\r\n", "\n").Replace("\n", "\r\n");
            Directory.CreateDirectory(Path.GetDirectoryName(c.ConfigPath));
            string temporary = c.ConfigPath + ".aimeter-new", backup = c.ConfigPath + ".aimeter-backup";
            try
            {
                File.WriteAllText(temporary, text, new UTF8Encoding(false));
                bool backedUp = File.Exists(c.ConfigPath);
                if (backedUp) File.Copy(c.ConfigPath, backup, true);
                // Copying over the file, rather than moving, keeps its links and WSL permissions. A copy that fails part
                // way may leave the file truncated, so the backup is put back, when possible, before the error goes on.
                try { File.Copy(temporary, c.ConfigPath, true); }
                catch
                {
                    if (backedUp) TryCopy(backup, c.ConfigPath);
                    throw;
                }
            }
            finally
            {
                try { File.Delete(temporary); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }

        private static void TryCopy(string source, string target)
        {
            try { File.Copy(source, target, true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>
    /// The relay script that AI apps start: it connects to AI Meter's pipe, starting AI Meter when it is not running,
    /// and forwards one line at a time. It is written by AI Meter so that it always names the current launch command,
    /// outside the installation folder so that updates and moves do not break connected apps.
    /// </summary>
    internal static class McpRelay
    {
        public static string FilePath
        {
            get
            {
                // A packaged app's AppData is private to the package, so other apps could not read the script there.
                string folder = Native.IsPackaged ? Path.Combine(Probe.Home, ".aimeter") : Settings.Folder;
                return Path.Combine(folder, "mcp.ps1");
            }
        }

        /// <summary>Write or refresh the script; returns its path.</summary>
        public static string Write()
        {
            string target, args;
            Program.SplitLaunch(out target, out args);
            string path = FilePath;
            string text = Script(McpPipeHost.PipePrefix, target, args);
            if (!File.Exists(path) || File.ReadAllText(path, Encoding.UTF8) != text)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllText(path, text, new UTF8Encoding(true));   // A BOM makes Windows PowerShell read the file as UTF-8.
            }
            return path;
        }

        internal static string Script(string pipePrefix, string target, string args)
        {
            Func<string, string> quote = s => "'" + (s ?? "").Replace("'", "''") + "'";
            return string.Join("\r\n", new[]
            {
                "# AI Meter MCP relay, written by AI Meter: AI apps start this script to read AI Meter's data.",
                "# It forwards each JSON-RPC line between this process and AI Meter's named pipe, starting AI Meter if needed.",
                "$ErrorActionPreference = 'Stop'",
                "$pipeName = " + quote(pipePrefix) + " + $env:USERNAME",
                "$launch = " + quote(target),
                "$launchArgs = " + quote(args),
                "function Connect-Meter([int]$milliseconds) {",
                "    $pipe = New-Object IO.Pipes.NamedPipeClientStream '.', $pipeName, ([IO.Pipes.PipeDirection]::InOut)",
                "    try { $pipe.Connect($milliseconds); return $pipe } catch { $pipe.Dispose(); return $null }",
                "}",
                "$pipe = Connect-Meter 1000",
                "if (-not $pipe) {",
                "    if ($launchArgs) { Start-Process -FilePath $launch -ArgumentList $launchArgs } else { Start-Process -FilePath $launch }",
                "    $pipe = Connect-Meter 60000",
                "}",
                "if (-not $pipe) { [Console]::Error.WriteLine('AI Meter is not running and could not be started.'); exit 1 }",
                "$utf8 = New-Object Text.UTF8Encoding $false",
                "$stdin = New-Object IO.StreamReader ([Console]::OpenStandardInput()), $utf8",
                "$stdout = New-Object IO.StreamWriter ([Console]::OpenStandardOutput()), $utf8",
                "$toMeter = New-Object IO.StreamWriter $pipe, $utf8",
                "$fromMeter = New-Object IO.StreamReader $pipe, $utf8",
                "$stdout.AutoFlush = $true; $stdout.NewLine = \"`n\"; $toMeter.AutoFlush = $true; $toMeter.NewLine = \"`n\"",
                "while ($null -ne ($line = $stdin.ReadLine())) {",
                "    if ($line.Trim().Length -eq 0) { continue }",
                "    $toMeter.WriteLine($line)",
                "    $reply = $fromMeter.ReadLine()",
                "    if ($null -eq $reply) { break }",
                "    if ($reply.Length -gt 0) { $stdout.WriteLine($reply) }",
                "}",
                "",
            });
        }
    }
}
