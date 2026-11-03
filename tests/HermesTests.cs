using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace AIMeter
{
    /// <summary>Hermes Agent: keys in its dotenv file, sessions in its state.db, and its "mcp add" command.</summary>
    internal static class HermesTests
    {
        internal static void Register(Action<string, Action> test)
        {
            test("Keys and an account ID are found in the .env file of Hermes, natively and in WSL", DotenvKeys);
            test("Hermes sessions come from state.db: recent ones only, working while the reply is pending", Sessions);
            test("The Hermes command adds AI Meter with --command and its dash-prefixed --args last", Command);
        }

        private static void Assert(bool condition, string message) { if (!condition) throw new Exception(message); }

        private const string Key = "sk-or-v1-synthetic0123456789";
        private const string Account = "0123456789abcdef0123456789abcdef";

        private static void DotenvKeys()
        {
            var openRouter = ServiceDefinition.FromJson("{ \"id\": \"openrouter\", \"auth\": { \"type\": \"key\", \"env\": [\"OPENROUTER_API_KEY\"] } }", "test");

            // Native Windows keeps its home in %LOCALAPPDATA%\hermes; comments and quotes are those of a dotenv file.
            string home = Home(@"AppData\Local\hermes\.env", "# Hermes\nHF_TOKEN=hf_synthetic0123456789\nOPENROUTER_API_KEY=\"" + Key + "\"\n");
            var found = KeyFinder.SearchAll(openRouter, home, false).FirstOrDefault();
            Assert(found != null && found.Key == Key && found.App == "Hermes", "The key in the native Hermes .env was not found.");

            // In WSL the home is ~/.hermes; the account ID a service needs besides the key is read from the same file.
            var cloudflare = ServiceDefinition.FromJson("{ \"id\": \"cloudflare-workers-ai\", \"auth\": { \"type\": \"key\", \"env\": [\"CLOUDFLARE_API_KEY\"] } }", "test");
            home = Home(@".hermes\.env", "CLOUDFLARE_API_KEY=cf-synthetic-token-0123456789\nCLOUDFLARE_ACCOUNT_ID=" + Account + "\n");
            found = KeyFinder.SearchAll(cloudflare, home, true).FirstOrDefault();
            Assert(found != null && found.App == "Hermes" && found.Source.EndsWith("(WSL)"), "The key in ~/.hermes/.env was not found.");
            Assert(KeyFinder.Beside(found, "metadata.accountId", new[] { "CLOUDFLARE_ACCOUNT_ID" }) == Account, "The account ID beside the key in the .env file was not found.");
            Assert(KeyFinder.SearchAll(openRouter, home, true).FirstOrDefault() == null, "A key was given to a service whose variable is not in the file.");
        }

        private static void Sessions()
        {
            string home = Home(@"AppData\Local\hermes\.env", "");
            string db = Path.Combine(home, @"AppData\Local\hermes\state.db");
            double now = (DateTime.UtcNow - new DateTime(1970, 1, 1)).TotalSeconds;
            Func<double, string> at = minutesAgo => (now - minutesAgo * 60).ToString(System.Globalization.CultureInfo.InvariantCulture);
            Exec(db,
                "CREATE TABLE sessions (id TEXT PRIMARY KEY, source TEXT NOT NULL, model TEXT, parent_session_id TEXT, started_at REAL NOT NULL, ended_at REAL, cwd TEXT," +
                " billing_provider TEXT, title TEXT, last_activity_at REAL, archived INTEGER NOT NULL DEFAULT 0, hidden INTEGER NOT NULL DEFAULT 0);" +
                "CREATE TABLE messages (id INTEGER PRIMARY KEY AUTOINCREMENT, session_id TEXT NOT NULL, role TEXT NOT NULL, content TEXT, timestamp REAL NOT NULL," +
                " finish_reason TEXT, active INTEGER NOT NULL DEFAULT 1);" +
                // Working: the user's message is the last one.
                "INSERT INTO sessions VALUES ('a', 'cli', 'moonshotai/kimi-k3', NULL, " + at(20) + ", NULL, 'C:\\work', 'openrouter', 'Port the parser', " + at(1) + ", 0, 0);" +
                "INSERT INTO messages (session_id, role, timestamp, finish_reason) VALUES ('a', 'assistant', " + at(2) + ", 'stop'), ('a', 'user', " + at(1) + ", NULL);" +
                // Finished: ended, with a complete reply.
                "INSERT INTO sessions VALUES ('b', 'cli', 'openai/gpt-oss-120b', NULL, " + at(25) + ", " + at(5) + ", NULL, 'huggingface', NULL, " + at(6) + ", 0, 0);" +
                "INSERT INTO messages (session_id, role, timestamp, finish_reason) VALUES ('b', 'assistant', " + at(6) + ", 'stop');" +
                // Left out: archived, a subagent, and one older than 30 minutes.
                "INSERT INTO sessions VALUES ('c', 'cli', 'x', NULL, " + at(3) + ", NULL, NULL, NULL, 'Archived', " + at(3) + ", 1, 0);" +
                "INSERT INTO sessions VALUES ('d', 'cli', 'x', 'a', " + at(3) + ", NULL, NULL, NULL, 'Subagent', " + at(3) + ", 0, 0);" +
                "INSERT INTO sessions VALUES ('e', 'cli', 'x', NULL, " + at(120) + ", NULL, NULL, NULL, 'Old', " + at(90) + ", 0, 0);");

            var sessions = new CodingAgents(Settings.Current).ScanHome(new Probe(Settings.Current), home).Where(s => s.Kind == "Hermes").ToList();
            Assert(sessions.Count == 2, "Expected the two recent top-level Hermes sessions, found " + sessions.Count + ".");
            var working = sessions[0];
            Assert(working.Title == "Port the parser" && working.Model == "moonshotai/kimi-k3" && working.Provider == "openrouter" && working.Status == "busy",
                "The working Hermes session was not read with its title, model, provider and status.");
            Assert(sessions[1].Status == "idle" && sessions[1].Title == L.T("Untitled session"), "An ended Hermes session is shown as working or with a title it lacks.");
        }

        private static void Command()
        {
            string relay = @"C:\Users\O'Brien\AppData\Roaming\AIMeter\mcp.ps1";
            Assert(McpClients.Command("hermes", relay, false) == "hermes mcp add ai-meter --command '" + McpClients.PowerShell(false) + "' --args -NoProfile -NonInteractive -ExecutionPolicy Bypass -File 'C:\\Users\\O''Brien\\AppData\\Roaming\\AIMeter\\mcp.ps1'",
                "The Hermes command for PowerShell is wrong.");
            Assert(McpClients.Command("hermes", relay, true) == "hermes mcp add ai-meter --command '" + McpClients.PowerShell(true) + "' --args -NoProfile -NonInteractive -ExecutionPolicy Bypass -File 'C:\\Users\\O'\\''Brien\\AppData\\Roaming\\AIMeter\\mcp.ps1'",
                "The Hermes command for WSL is wrong.");
            Assert(McpClients.CommandLineClients.Any(c => c[0] == "hermes"), "Settings does not list Hermes among the command-line agents.");
        }

        // ------------------------------------------------------------ helpers

        /// <summary>A temporary home folder with the given relative files and contents.</summary>
        private static string Home(params string[] files)
        {
            string home = Path.Combine(Settings.Folder, "hermes-home-" + Guid.NewGuid().ToString("N"));
            for (int i = 0; i + 1 < files.Length; i += 2)
            {
                string path = Path.Combine(home, files[i]);
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllText(path, files[i + 1]);
            }
            return home;
        }

        // The app opens databases read-only; the fixture is written with the same Windows SQLite library.
        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.StdCall)] private static extern int sqlite3_open_v2(byte[] file, out IntPtr db, int flags, IntPtr vfs);
        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.StdCall)] private static extern int sqlite3_exec(IntPtr db, byte[] sql, IntPtr callback, IntPtr argument, IntPtr error);
        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.StdCall)] private static extern int sqlite3_close(IntPtr db);

        private static void Exec(string path, string sql)
        {
            Func<string, byte[]> z = s => Encoding.UTF8.GetBytes(s + "\0");
            IntPtr db;
            const int ReadWriteCreate = 2 | 4;
            Assert(sqlite3_open_v2(z(path), out db, ReadWriteCreate, IntPtr.Zero) == 0, "The fixture database could not be created.");
            try { Assert(sqlite3_exec(db, z(sql), IntPtr.Zero, IntPtr.Zero, IntPtr.Zero) == 0, "The fixture SQL failed."); }
            finally { sqlite3_close(db); }
        }
    }
}
