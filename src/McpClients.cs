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
            McpServers,   // { "mcpServers": { name: { command, args } } }: Claude, Cursor, Gemini CLI, Pi's MCP adapter.
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
                             ConfigPath = Path.Combine(appData, @"Claude\claude_desktop_config.json"), Marker = Path.Combine(appData, "Claude") },
                new Client { Id = "vscode", Name = "Visual Studio Code", Where = "Windows", Format = Format.VsCode,
                             ConfigPath = Path.Combine(appData, @"Code\User\mcp.json"), Marker = Path.Combine(appData, @"Code\User") },
            };
            list.AddRange(HomeClients(windowsHome, "Windows"));
            if (wslHome != null) list.AddRange(HomeClients(wslHome, "WSL").Where(c => c.Id != "cursor"));
            return list;
        }

        /// <summary>Clients configured in the user's home folder, the same on Windows and in Linux.</summary>
        private static IEnumerable<Client> HomeClients(string home, string where)
        {
            yield return new Client { Id = "claude-code", Name = "Claude Code", Where = where, Format = Format.McpServers, StdioType = true,
                                      ConfigPath = Path.Combine(home, ".claude.json"), Marker = Path.Combine(home, ".claude.json") };
            yield return new Client { Id = "codex", Name = "Codex", Where = where, Format = Format.CodexToml,
                                      ConfigPath = Path.Combine(home, @".codex\config.toml"), Marker = Path.Combine(home, ".codex") };
            yield return new Client { Id = "gemini", Name = "Gemini CLI", Where = where, Format = Format.McpServers,
                                      ConfigPath = Path.Combine(home, @".gemini\settings.json"), Marker = Path.Combine(home, ".gemini") };
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
            if (c.Format == Format.CodexToml) return TomlHeader.IsMatch(File.ReadAllText(c.ConfigPath, Encoding.UTF8));
            var doc = Json.Parse(File.ReadAllText(c.ConfigPath, Encoding.UTF8));
            var servers = Json.Obj(doc, ServersKey(c.Format));
            return servers != null && servers.ContainsKey(EntryName);
        }

        public static void Connect(Client c, string relay)
        {
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

        // [mcp_servers.ai-meter] and its sub-tables, such as [mcp_servers.ai-meter.env], with or without quotes.
        private static readonly Regex TomlHeader = new Regex(@"^\s*\[\s*mcp_servers\s*\.\s*(ai-meter|""ai-meter"")\s*(\.[^\]]*)?\]\s*(#.*)?$", RegexOptions.Multiline);
        private static readonly Regex AnyHeader = new Regex(@"^\s*\[");

        internal static string WithToml(string text, string command, List<string> args)
        {
            string rest = (WithoutToml(text ?? "") ?? text ?? "").TrimEnd('\r', '\n');
            var sb = new StringBuilder(rest);
            if (rest.Length > 0) sb.Append("\n\n");
            sb.Append("[mcp_servers.").Append(EntryName).Append("]\n");
            sb.Append("command = ").Append(TomlString(command)).Append('\n');
            sb.Append("args = [").Append(string.Join(", ", args.Select(TomlString))).Append("]\n");
            return sb.ToString();
        }

        /// <summary>The text without AI Meter's tables, or null when there were none.</summary>
        internal static string WithoutToml(string text)
        {
            var lines = text.Replace("\r\n", "\n").Split('\n');
            var kept = new List<string>();
            bool inOwn = false, removed = false;
            foreach (var line in lines)
            {
                if (AnyHeader.IsMatch(line)) inOwn = TomlHeader.IsMatch(line);
                if (inOwn) { removed = true; continue; }
                kept.Add(line);
            }
            if (!removed) return null;
            return string.Join("\n", kept).TrimEnd('\n') + "\n";
        }

        private static string TomlString(string s)
        {
            return "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
        }

        // ------------------------------------------------------------ files

        /// <summary>Write the new text through a temporary file, keeping the previous content as a backup.</summary>
        private static void Save(Client c, string text)
        {
            if (text == null) return;   // Nothing to change.
            if (c.Wsl) text = text.Replace("\r\n", "\n");
            else text = text.Replace("\r\n", "\n").Replace("\n", "\r\n");
            Directory.CreateDirectory(Path.GetDirectoryName(c.ConfigPath));
            string temporary = c.ConfigPath + ".aimeter-new";
            File.WriteAllText(temporary, text, new UTF8Encoding(false));
            if (File.Exists(c.ConfigPath)) File.Copy(c.ConfigPath, c.ConfigPath + ".aimeter-backup", true);
            // Copying over the file, rather than moving, keeps its links and WSL permissions.
            File.Copy(temporary, c.ConfigPath, true);
            File.Delete(temporary);
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
