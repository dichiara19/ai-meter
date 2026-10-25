using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace AIMeter
{
    /// <summary>
    /// Memoria della GPU: quanta ne usa ogni processo e quanta ne ha la scheda video, dai contatori di Windows (PDH)
    /// (gli stessi di Gestione attività). I modelli in WSL risultano a carico del processo vmmemWSL.
    /// </summary>
    internal static class GpuMemory
    {
        private static IntPtr query, counter;
        private static DateTime readAt = DateTime.MinValue;
        private static Dictionary<uint, long> byPid = new Dictionary<uint, long>();
        private static long used;
        private static long total = -1;

        /// <summary>Memoria video in uso per processo (byte), letta al massimo ogni 15 secondi.</summary>
        public static Dictionary<uint, long> ByProcess { get { Refresh(); return byPid; } }

        /// <summary>Memoria video in uso su tutta la scheda principale (byte).</summary>
        public static long Used { get { Refresh(); return used; } }

        /// <summary>Memoria video della scheda più capiente (byte), 0 se sconosciuta.</summary>
        public static long Total
        {
            get
            {
                if (total < 0) total = ReadTotal();
                return total;
            }
        }

        [DllImport("pdh.dll", CharSet = CharSet.Unicode)] private static extern uint PdhOpenQuery(string source, IntPtr user, out IntPtr q);
        [DllImport("pdh.dll", CharSet = CharSet.Unicode)] private static extern uint PdhAddEnglishCounter(IntPtr q, string path, IntPtr user, out IntPtr c);
        [DllImport("pdh.dll")] private static extern uint PdhCollectQueryData(IntPtr q);
        [DllImport("pdh.dll", CharSet = CharSet.Unicode)] private static extern uint PdhGetRawCounterArray(IntPtr c, ref uint size, out uint count, IntPtr buffer);

        [StructLayout(LayoutKind.Sequential)]
        private struct RawCounter { public uint Status; public uint TimeLow, TimeHigh; public long First, Second; public uint MultiCount; }

        [StructLayout(LayoutKind.Sequential)]
        private struct RawItem { public IntPtr Name; public RawCounter Raw; }

        /// <summary>
        /// Contatore "Dedicated Usage" di "GPU Process Memory" (la VRAM, come in Gestione attività), con PDH: nomi inglesi
        /// su ogni lingua di Windows e un costo di avvio molto minore della lettura dell'intera categoria.
        /// </summary>
        private static void Refresh()
        {
            if ((DateTime.Now - readAt).TotalSeconds < 15) return;
            readAt = DateTime.Now;
            var map = new Dictionary<uint, long>();
            try
            {
                if (query == IntPtr.Zero)
                {
                    if (PdhOpenQuery(null, IntPtr.Zero, out query) != 0) { query = IntPtr.Zero; return; }
                    if (PdhAddEnglishCounter(query, @"\GPU Process Memory(*)\Dedicated Usage", IntPtr.Zero, out counter) != 0) return;
                }
                if (PdhCollectQueryData(query) != 0) return;
                uint size = 0, count;
                PdhGetRawCounterArray(counter, ref size, out count, IntPtr.Zero);
                if (size == 0) return;
                IntPtr buf = Marshal.AllocHGlobal((int)size);
                try
                {
                    if (PdhGetRawCounterArray(counter, ref size, out count, buf) != 0) return;
                    int step = Marshal.SizeOf(typeof(RawItem));
                    for (int k = 0; k < count; k++)
                    {
                        var item = (RawItem)Marshal.PtrToStructure(buf + k * step, typeof(RawItem));
                        string name = Marshal.PtrToStringUni(item.Name) ?? "";   // pid_1234_luid_0x…_phys_0[#n]
                        int us = name.IndexOf('_', 4);
                        uint pid;
                        if (!name.StartsWith("pid_", StringComparison.OrdinalIgnoreCase) || us < 0 || !uint.TryParse(name.Substring(4, us - 4), out pid)) continue;
                        long v;
                        map.TryGetValue(pid, out v);
                        map[pid] = v + item.Raw.First;
                    }
                }
                finally { Marshal.FreeHGlobal(buf); }
                // occupazione della scheda: la somma dei processi (approssima per eccesso la memoria condivisa tra processi)
                used = Math.Min(map.Values.Sum(), Total > 0 ? Total : long.MaxValue);
            }
            catch (DllNotFoundException) { }
            catch (EntryPointNotFoundException) { }
            finally { if (map.Count > 0) byPid = map; }
        }
        /// <summary>Dalla configurazione del driver video (la stessa fonte delle informazioni di sistema).</summary>
        private static long ReadTotal()
        {
            long best = 0;
            try
            {
                using (var cls = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}"))
                {
                    if (cls == null) return 0;
                    foreach (var sub in cls.GetSubKeyNames())
                    {
                        if (sub.Length != 4) continue;
                        using (var k = cls.OpenSubKey(sub))
                        {
                            if (k == null) continue;
                            object v = k.GetValue("HardwareInformation.qwMemorySize") ?? k.GetValue("HardwareInformation.MemorySize");
                            long size = 0;
                            if (v is long) size = (long)v;
                            else if (v is int) size = (uint)(int)v;
                            else if (v is byte[] && ((byte[])v).Length >= 8) size = BitConverter.ToInt64((byte[])v, 0);
                            else if (v is byte[] && ((byte[])v).Length >= 4) size = BitConverter.ToUInt32((byte[])v, 0);
                            best = Math.Max(best, size);
                        }
                    }
                }
            }
            catch (System.Security.SecurityException) { }
            catch (UnauthorizedAccessException) { }
            return best;
        }

        public static string Format(long bytes)
        {
            double gb = bytes / 1073741824.0;
            return gb >= 10 ? L.F("{0:0} GB", gb) : gb >= 1 ? L.F("{0:0.0} GB", gb) : L.F("{0:0} MB", bytes / 1048576.0);
        }
    }
}
