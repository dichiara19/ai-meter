using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using Microsoft.Win32;

namespace AIMeter
{
    /// <summary>
    /// Memoria della GPU: quanta ne usa ogni processo e quanta ne ha la scheda video, dai contatori di Windows
    /// (gli stessi di Gestione attività). I modelli in WSL risultano a carico del processo vmmemWSL.
    /// </summary>
    internal static class GpuMemory
    {
        private static PerformanceCounterCategory processCat;
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

        private static void Refresh()
        {
            if ((DateTime.Now - readAt).TotalSeconds < 15) return;
            readAt = DateTime.Now;
            var map = new Dictionary<uint, long>();
            try
            {
                if (processCat == null) processCat = new PerformanceCounterCategory("GPU Process Memory");
                var data = processCat.ReadCategory();
                // "dedicated usage" = VRAM della scheda dedicata (sulla scheda integrata vale ~0: la sua memoria è condivisa)
                var dedicated = data["dedicated usage"];
                if (dedicated != null)
                    foreach (InstanceData d in dedicated.Values)
                    {
                        uint pid;
                        string name = d.InstanceName;   // pid_1234_luid_0x…_phys_0
                        int us = name.IndexOf('_', 4);
                        if (!name.StartsWith("pid_", StringComparison.OrdinalIgnoreCase) || us < 0 || !uint.TryParse(name.Substring(4, us - 4), out pid)) continue;
                        long v;
                        map.TryGetValue(pid, out v);
                        map[pid] = v + d.RawValue;
                    }
                // occupazione della scheda: la somma dei processi (approssima per eccesso la memoria condivisa tra processi)
                used = Math.Min(map.Values.Sum(), Total > 0 ? Total : long.MaxValue);
            }
            catch (InvalidOperationException) { }
            catch (UnauthorizedAccessException) { }
            catch (System.ComponentModel.Win32Exception) { }
            byPid = map;
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
