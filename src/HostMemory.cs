using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace AIMeter
{
    /// <summary>
    /// System memory: of this PC, of the WSL virtual machine and of one process inside WSL (ADR 0006). Everything in WSL
    /// is read as files of /proc through the share, so no process is started there. Sizes are bytes; zero means unknown.
    /// </summary>
    internal static class HostMemory
    {
        /// <summary>MEMORYSTATUSEX. A class, passed by reference as Windows expects, so that it is never compared or copied by value.</summary>
        [StructLayout(LayoutKind.Sequential)]
        private sealed class MemoryStatus
        {
            public uint Length = (uint)Marshal.SizeOf(typeof(MemoryStatus));
            public uint Load;
            public ulong TotalPhysical, AvailablePhysical, TotalPageFile, AvailablePageFile, TotalVirtual, AvailableVirtual, AvailableExtended;
        }

        [DllImport("kernel32.dll")] private static extern bool GlobalMemoryStatusEx([In, Out] MemoryStatus status);

        /// <summary>Physical memory of this PC, in use and in total. False when Windows does not answer.</summary>
        public static bool Windows(out long used, out long total)
        {
            var status = new MemoryStatus();
            used = total = 0;
            if (!GlobalMemoryStatusEx(status)) return false;
            total = (long)status.TotalPhysical;
            used = total - (long)status.AvailablePhysical;
            return true;
        }

        /// <summary>
        /// Memory of the WSL virtual machine as Linux sees it: its total, and what is not available to a new program.
        /// False when /proc/meminfo cannot be read.
        /// </summary>
        public static bool Wsl(string root, out long used, out long total)
        {
            string text = ReadText(root + @"\proc\meminfo");
            total = Field(text, "MemTotal");
            long available = Field(text, "MemAvailable");
            used = total > 0 && available >= 0 ? Math.Max(0, total - available) : 0;
            if (total <= 0 || available < 0) { total = 0; return false; }
            return true;
        }

        /// <summary>Resident memory (VmRSS) of a process in WSL, or zero when it is gone or is a kernel thread.</summary>
        public static long Resident(string root, uint pid)
        {
            return Math.Max(0, Field(ReadText(ProcessFile(root, pid, "status")), "VmRSS"));
        }

        /// <summary>The arguments of a process in WSL; empty when it is gone or is a kernel thread.</summary>
        public static string[] CommandLine(string root, uint pid)
        {
            string text = ReadText(ProcessFile(root, pid, "cmdline"));
            return text == null ? new string[0] : text.Split(new[] { '\0' }, StringSplitOptions.RemoveEmptyEntries);
        }

        /// <summary>
        /// The process in WSL that serves a port: the one whose command line names the port or, when none does, the
        /// model file. Several can name the same port (a wrapper script, a client): the one holding the most memory is
        /// the server. Zero when none is found, as for a port set through the environment.
        /// </summary>
        public static uint FindServer(string root, int port, string modelFile)
        {
            uint byPort = 0, byFile = 0;
            long byPortSize = -1, byFileSize = -1;
            foreach (var directory in Probe.SafeDirs(root + @"\proc"))
            {
                uint pid;
                if (!uint.TryParse(Path.GetFileName(directory), NumberStyles.None, CultureInfo.InvariantCulture, out pid)) continue;
                var arguments = CommandLine(root, pid);
                bool namesPort = NamesPort(arguments, port), namesFile = NamesFile(arguments, modelFile);
                if (!namesPort && !namesFile) continue;
                long size = Resident(root, pid);
                if (namesPort && size > byPortSize) { byPort = pid; byPortSize = size; }
                if (namesFile && size > byFileSize) { byFile = pid; byFileSize = size; }
            }
            return byPort != 0 ? byPort : byFile;
        }

        /// <summary>The process still serves the port or the model file it was found by (its number may have passed to another).</summary>
        public static bool Serves(string[] arguments, int port, string modelFile)
        {
            return NamesPort(arguments, port) || NamesFile(arguments, modelFile);
        }

        /// <summary>
        /// A command line that opens the port: "--port 8080", "--port=8080" or an address such as "127.0.0.1:8080".
        /// A URL is what a client names, and docker-proxy only forwards the port to a container.
        /// </summary>
        internal static bool NamesPort(string[] arguments, int port)
        {
            if (arguments.Length == 0 || port <= 0 || Path.GetFileName(arguments[0]) == "docker-proxy") return false;
            string number = port.ToString(CultureInfo.InvariantCulture);
            for (int i = 0; i < arguments.Length; i++)
            {
                string argument = arguments[i];
                if (argument == number && i > 0 && IsPortOption(arguments[i - 1])) return true;
                int equals = argument.IndexOf('=');
                if (equals > 0 && argument.Substring(equals + 1) == number && IsPortOption(argument.Substring(0, equals))) return true;
                if (argument.EndsWith(":" + number, StringComparison.Ordinal) && argument.IndexOf("://", StringComparison.Ordinal) < 0) return true;
            }
            return false;
        }

        private static bool IsPortOption(string option)
        {
            return option.StartsWith("-", StringComparison.Ordinal) && (option == "-p" || option.IndexOf("port", StringComparison.OrdinalIgnoreCase) >= 0);
        }

        private static bool NamesFile(string[] arguments, string modelFile)
        {
            return !string.IsNullOrEmpty(modelFile) && arguments.Contains(modelFile);
        }

        /// <summary>A "Name:   123 kB" line of /proc/meminfo or /proc/&lt;pid&gt;/status, in bytes; -1 when absent.</summary>
        internal static long Field(string text, string name)
        {
            if (text == null) return -1;
            foreach (var line in text.Split('\n'))
            {
                if (!line.StartsWith(name + ":", StringComparison.Ordinal)) continue;
                var parts = line.Substring(name.Length + 1).Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                long kilobytes;
                if (parts.Length == 0 || !long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out kilobytes)) return -1;
                return kilobytes * 1024;
            }
            return -1;
        }

        private static string ProcessFile(string root, uint pid, string name)
        {
            return root + @"\proc\" + pid.ToString(CultureInfo.InvariantCulture) + @"\" + name;
        }

        /// <summary>A file of /proc, or null: a process can end between the listing and the read.</summary>
        private static string ReadText(string path)
        {
            try
            {
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (var reader = new StreamReader(stream, Encoding.UTF8)) return reader.ReadToEnd();
            }
            catch (IOException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
        }
    }
}
