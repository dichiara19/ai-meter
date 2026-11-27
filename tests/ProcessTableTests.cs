using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace AIMeter
{
    /// <summary>
    /// The listing of processes every collection starts with: one system query read at fixed offsets, so it is checked
    /// against the Toolhelp snapshot it replaced and against what Windows says of this very process.
    /// </summary>
    internal static class ProcessTableTests
    {
        internal static void Register(Action<string, Action> test)
        {
            test("One system query lists the same processes, names and parents as a Toolhelp snapshot", AgreesWithToolhelp);
            test("The listing carries each process's working set, and a listing without them asks the process", WorkingSets);
        }

        private static void Assert(bool condition, string message) { if (!condition) throw new Exception(message); }

        private static void AgreesWithToolhelp()
        {
            Assert(IntPtr.Size == 8, "The tests run as a 64-bit process, where the query's layout is the one read.");
            var queried = Native.QueryProcessTable();
            var snapshot = Native.ToolhelpProcessTable();
            Assert(queried != null && queried.Count > 20 && snapshot.Count > 20, "A listing is empty.");
            // Processes start and end between the two listings: nearly all are in both, and those must agree.
            var both = snapshot.Keys.Where(queried.ContainsKey).ToList();
            Assert(both.Count >= snapshot.Count * 0.9, "The two listings share only " + both.Count + " of " + snapshot.Count + " processes.");
            // The idle and system processes (0 and 4) have no image name in the query.
            var named = both.Where(pid => pid > 4).ToList();
            var wrongName = named.Where(pid => queried[pid].Name != snapshot[pid].Name).ToList();
            var wrongParent = named.Where(pid => queried[pid].Parent != snapshot[pid].Parent).ToList();
            Assert(wrongName.Count == 0, wrongName.Count + " names differ, first: '" + (wrongName.Count > 0 ? queried[wrongName[0]].Name + "' and '" + snapshot[wrongName[0]].Name : "") + "'.");
            Assert(wrongParent.Count == 0, wrongParent.Count + " parents differ.");

            uint self = (uint)Process.GetCurrentProcess().Id;
            Assert(queried.ContainsKey(self) && queried[self].Name.EndsWith(".exe", StringComparison.Ordinal) && queried[self].Name == queried[self].Name.ToLowerInvariant(),
                "This process is not listed under its lower-case executable name.");
            Assert(Native.ProcessTable().ContainsKey(self), "The listing the app uses lacks this process.");
            Assert(snapshot.Values.All(entry => entry.WorkingSet == -1), "A Toolhelp listing claims working sets it does not have.");
        }

        private static void WorkingSets()
        {
            uint self = (uint)Process.GetCurrentProcess().Id;
            long asked = Native.WorkingSet(self), listed = Native.QueryProcessTable()[self].WorkingSet;
            Assert(asked > (8L << 20), "This process's working set was not read from the process.");
            // Read moments apart, so not equal, but the same quantity: an offset one field off would give the peak or a count.
            Assert(listed > asked / 2 && listed < asked * 2, "The listing's working set (" + (listed >> 20) + " MB) is not this process's (" + (asked >> 20) + " MB).");
            Assert(Native.WorkingSet(0xFFFFFFF0) == 0, "A process that does not exist has a working set.");

            var probe = new Probe(Settings.Current);
            probe.Table = new Dictionary<uint, Native.ProcessEntry>
            {
                { 10, new Native.ProcessEntry { Name = "a.exe", WorkingSet = 100 } },
                { 11, new Native.ProcessEntry { Name = "b.exe", WorkingSet = 250 } },
                { self, new Native.ProcessEntry { Name = "self.exe", WorkingSet = -1 } },
            };
            Assert(probe.WorkingSet(new uint[] { 10, 11, 999 }) == 350, "Working sets are not summed over the processes still listed.");
            Assert(probe.WorkingSet(new uint[] { 999 }) == 0 && probe.WorkingSet(new uint[0]) == 0, "A process that ended counts something.");
            Assert(probe.WorkingSet(new[] { self }) > (8L << 20), "A listing without working sets did not ask the process.");
        }
    }
}
