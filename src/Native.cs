using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace AIMeter
{
    /// <summary>Win32 calls used by the app. Anything expensive is avoided or cached.</summary>
    internal static class Native
    {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct PROCESSENTRY32
        {
            public uint dwSize, cntUsage, th32ProcessID;
            public IntPtr th32DefaultHeapID;
            public uint th32ModuleID, cntThreads, th32ParentProcessID;
            public int pcPriClassBase;
            public uint dwFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szExeFile;
        }

        [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint pid);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern bool Process32FirstW(IntPtr h, ref PROCESSENTRY32 e);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern bool Process32NextW(IntPtr h, ref PROCESSENTRY32 e);
        [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr h);
        [DllImport("kernel32.dll")] private static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern bool QueryFullProcessImageNameW(IntPtr h, uint flags, StringBuilder buf, ref uint size);

        /// <summary>PID → lower-case executable name. A single Toolhelp snapshot: ~1 ms, no WMI.</summary>
        public static Dictionary<uint, string> Processes()
        {
            var map = new Dictionary<uint, string>();
            IntPtr snap = CreateToolhelp32Snapshot(0x2, 0);
            if (snap == IntPtr.Zero || snap == new IntPtr(-1)) return map;
            try
            {
                var e = new PROCESSENTRY32();
                e.dwSize = (uint)Marshal.SizeOf(typeof(PROCESSENTRY32));
                bool ok = Process32FirstW(snap, ref e);
                while (ok)
                {
                    map[e.th32ProcessID] = (e.szExeFile ?? "").ToLowerInvariant();
                    ok = Process32NextW(snap, ref e);
                }
            }
            finally { CloseHandle(snap); }
            return map;
        }

        // --- listening TCP ports with their owning process (IPv4 and IPv6) -------------------
        [DllImport("iphlpapi.dll")] private static extern uint GetExtendedTcpTable(IntPtr table, ref int size, bool sort, int af, int tableClass, uint reserved);

        /// <summary>TCP ports listening on this PC: port → owning process PID.</summary>
        public static Dictionary<int, uint> ListeningPorts()
        {
            var map = new Dictionary<int, uint>();
            foreach (int af in new[] { 2, 23 })   // AF_INET, AF_INET6
            {
                int rowSize = af == 2 ? 24 : 56, portOffset = af == 2 ? 8 : 20, pidOffset = af == 2 ? 20 : 52;
                int size = 0;
                GetExtendedTcpTable(IntPtr.Zero, ref size, false, af, 3 /* TCP_TABLE_OWNER_PID_LISTENER */, 0);
                if (size <= 0) continue;
                IntPtr buf = Marshal.AllocHGlobal(size);
                try
                {
                    if (GetExtendedTcpTable(buf, ref size, false, af, 3, 0) != 0) continue;
                    int n = Marshal.ReadInt32(buf);
                    for (int i = 0; i < n; i++)
                    {
                        IntPtr row = buf + 4 + i * rowSize;
                        int raw = Marshal.ReadInt32(row, portOffset);
                        int port = ((raw & 0xFF) << 8) | ((raw >> 8) & 0xFF);   // Network byte order.
                        if (!map.ContainsKey(port)) map[port] = (uint)Marshal.ReadInt32(row, pidOffset);
                    }
                }
                finally { Marshal.FreeHGlobal(buf); }
            }
            return map;
        }

        /// <summary>Parent of every process: PID → parent PID.</summary>
        public static Dictionary<uint, uint> ProcessParents()
        {
            var map = new Dictionary<uint, uint>();
            IntPtr snap = CreateToolhelp32Snapshot(0x2, 0);
            if (snap == IntPtr.Zero || snap == new IntPtr(-1)) return map;
            try
            {
                var e = new PROCESSENTRY32();
                e.dwSize = (uint)Marshal.SizeOf(typeof(PROCESSENTRY32));
                bool ok = Process32FirstW(snap, ref e);
                while (ok) { map[e.th32ProcessID] = e.th32ParentProcessID; ok = Process32NextW(snap, ref e); }
            }
            finally { CloseHandle(snap); }
            return map;
        }

        public static string ProcessPath(uint pid)
        {
            IntPtr h = OpenProcess(0x1000, false, pid); // PROCESS_QUERY_LIMITED_INFORMATION
            if (h == IntPtr.Zero) return "";
            try
            {
                var sb = new StringBuilder(1024);
                uint size = 1024;
                return QueryFullProcessImageNameW(h, 0, sb, ref size) ? sb.ToString() : "";
            }
            finally { CloseHandle(h); }
        }

        [DllImport("ntdll.dll")] private static extern int NtQueryInformationProcess(IntPtr h, int cls, IntPtr info, int size, out int returned);
        [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr CommandLineToArgvW(string line, out int count);
        [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr h);

        /// <summary>A process's arguments, split as the C runtime does, or null if it cannot be read (other users, protected processes).</summary>
        public static string[] ProcessArguments(uint pid)
        {
            IntPtr h = OpenProcess(0x1000, false, pid); // PROCESS_QUERY_LIMITED_INFORMATION
            if (h == IntPtr.Zero) return null;
            IntPtr buf = IntPtr.Zero;
            try
            {
                // ProcessCommandLineInformation (60, Windows 8.1 and later) returns a UNICODE_STRING followed by its text.
                int size = 4096, returned;
                for (int attempt = 0; attempt < 2; attempt++)
                {
                    buf = Marshal.AllocHGlobal(size);
                    int status = NtQueryInformationProcess(h, 60, buf, size, out returned);
                    if (status == 0) break;
                    Marshal.FreeHGlobal(buf);
                    buf = IntPtr.Zero;
                    if (returned <= size || returned > 1 << 20) return null;
                    size = returned;
                }
                if (buf == IntPtr.Zero) return null;
                int length = Marshal.ReadInt16(buf) & 0xFFFF;
                IntPtr text = Marshal.ReadIntPtr(buf, IntPtr.Size);
                if (length == 0 || text == IntPtr.Zero) return null;
                return SplitArguments(Marshal.PtrToStringUni(text, length / 2));
            }
            finally
            {
                if (buf != IntPtr.Zero) Marshal.FreeHGlobal(buf);
                CloseHandle(h);
            }
        }

        private static string[] SplitArguments(string line)
        {
            int count;
            IntPtr argv = CommandLineToArgvW(line, out count);
            if (argv == IntPtr.Zero) return null;
            try
            {
                var args = new string[count];
                for (int i = 0; i < count; i++) args[i] = Marshal.PtrToStringUni(Marshal.ReadIntPtr(argv, i * IntPtr.Size));
                return args;
            }
            finally { LocalFree(argv); }
        }

        // --- power saving ------------------------------------------------
        [StructLayout(LayoutKind.Sequential)]
        private struct POWER_THROTTLING_STATE { public uint Version, ControlMask, StateMask; }

        [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentThread();
        [DllImport("kernel32.dll")] private static extern bool SetThreadInformation(IntPtr h, int cls, ref POWER_THROTTLING_STATE info, int size);

        /// <summary>EcoQoS on the current thread: Windows runs it on the efficient, low-frequency cores.</summary>
        public static void EnableEcoQoSForCurrentThread()
        {
            try
            {
                var s = new POWER_THROTTLING_STATE { Version = 1, ControlMask = 1, StateMask = 1 };
                SetThreadInformation(GetCurrentThread(), 3 /* ThreadPowerThrottling */, ref s, Marshal.SizeOf(s));
            }
            catch (EntryPointNotFoundException) { }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct SYSTEM_POWER_STATUS { public byte ACLineStatus, BatteryFlag, BatteryLifePercent, SystemStatusFlag; public int BatteryLifeTime, BatteryFullLifeTime; }
        [DllImport("kernel32.dll")] private static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS s);

        /// <summary>True if the PC is on battery or Battery Saver is on.</summary>
        public static bool SavingPower()
        {
            SYSTEM_POWER_STATUS s;
            if (!GetSystemPowerStatus(out s)) return false;
            return s.SystemStatusFlag == 1 || s.ACLineStatus == 0;
        }

        [DllImport("user32.dll")] private static extern bool SetProcessDpiAwarenessContext(IntPtr value);

        /// <summary>Per-monitor DPI (V2). Needed when the executable manifest is missing, i.e. when launched from PowerShell.</summary>
        public static void EnablePerMonitorDpi()
        {
            try { SetProcessDpiAwarenessContext(new IntPtr(-4)); }
            catch (EntryPointNotFoundException) { }
        }

        // --- windows and DWM -------------------------------------------------------
        [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int val, int size);

        public const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
        public const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
        public const int DWMWA_BORDER_COLOR = 34;
        public const int DWMWA_SYSTEMBACKDROP_TYPE = 38;

        public static bool SetDwm(IntPtr hwnd, int attr, int value)
        {
            try { return DwmSetWindowAttribute(hwnd, attr, ref value, 4) == 0; }
            catch (DllNotFoundException) { return false; }
        }

        [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
        [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] public static extern int GetWindowLong(IntPtr h, int idx);
        [DllImport("user32.dll", EntryPoint = "SetWindowLongW")] public static extern int SetWindowLong(IntPtr h, int idx, int val);
        [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
        [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] public static extern IntPtr GetShellWindow();
        [DllImport("user32.dll")] public static extern IntPtr GetDesktopWindow();
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassNameW(IntPtr h, StringBuilder sb, int max);

        public const int GWL_EXSTYLE = -20;
        public const int GWL_STYLE = -16;
        public const int WS_SYSMENU = 0x80000, WS_MINIMIZEBOX = 0x20000, WS_MAXIMIZEBOX = 0x10000;
        public const int WS_EX_TOOLWINDOW = 0x80;
        public const int WS_EX_APPWINDOW = 0x40000;
        public const int WS_EX_NOACTIVATE = 0x08000000;
        public static readonly IntPtr HWND_BOTTOM = new IntPtr(1);
        public static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
        public static readonly IntPtr HWND_NOTOPMOST = new IntPtr(-2);
        public const uint SWP_NOSIZE = 0x1, SWP_NOMOVE = 0x2, SWP_NOZORDER = 0x4, SWP_NOACTIVATE = 0x10;
        public const int WM_WINDOWPOSCHANGING = 0x46;

        [StructLayout(LayoutKind.Sequential)]
        public struct WINDOWPOS { public IntPtr hwnd, hwndInsertAfter; public int x, y, cx, cy; public uint flags; }

        public static void AddExStyle(IntPtr hwnd, int add, int remove)
        {
            int ex = GetWindowLong(hwnd, GWL_EXSTYLE);
            SetWindowLong(hwnd, GWL_EXSTYLE, (ex | add) & ~remove);
        }

        // --- full screen and presentations -------------------------------------
        [DllImport("shell32.dll")] private static extern int SHQueryUserNotificationState(out int state);

        /// <summary>True when the user is gaming, presenting or watching a video full screen.</summary>
        public static bool UserIsBusyFullscreen()
        {
            int st;
            if (SHQueryUserNotificationState(out st) != 0) return false;
            return st == 2 || st == 3 || st == 4; // BUSY, RUNNING_D3D_FULL_SCREEN, PRESENTATION_MODE
        }

        public delegate void WinEventProc(IntPtr hook, uint ev, IntPtr hwnd, int obj, int child, uint thread, uint time);
        [DllImport("user32.dll")] public static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr mod, WinEventProc proc, uint pid, uint tid, uint flags);
        public const uint EVENT_SYSTEM_FOREGROUND = 3;

        // --- notification area ----------------------------------------------------
        [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential)]
        private struct APPBARDATA { public int cbSize; public IntPtr hWnd; public uint uCallbackMessage, uEdge; public RECT rc; public IntPtr lParam; }
        [DllImport("shell32.dll")] private static extern IntPtr SHAppBarMessage(uint msg, ref APPBARDATA data);

        /// <summary>Rectangle (physical pixels), edge and auto-hide state of the primary monitor's taskbar.</summary>
        public static void TaskbarPos(out RECT rect, out int edge, out bool autoHide)
        {
            var d = new APPBARDATA();
            d.cbSize = Marshal.SizeOf(d);
            SHAppBarMessage(5 /* ABM_GETTASKBARPOS */, ref d);
            rect = d.rc;
            edge = (int)d.uEdge;
            var s = new APPBARDATA();
            s.cbSize = Marshal.SizeOf(s);
            autoHide = ((long)SHAppBarMessage(4 /* ABM_GETSTATE */, ref s) & 1) != 0;
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindowW(string cls, string title);
        [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
        [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr h);
        public const int WS_CHILD = 0x40000000, WS_VISIBLE = 0x10000000, WS_CLIPSIBLINGS = 0x04000000;

        /// <summary>The primary monitor's taskbar.</summary>
        public static IntPtr FindTaskbar() { return FindWindowW("Shell_TrayWnd", null); }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern int SetCurrentProcessExplicitAppUserModelID(string id);

        // --- window identity in the taskbar (System.AppUserModel.* properties) ------------------
        [StructLayout(LayoutKind.Sequential, Pack = 4)]
        private struct PROPERTYKEY { public Guid fmtid; public uint pid; }

        [StructLayout(LayoutKind.Explicit)]
        private struct PROPVARIANT { [FieldOffset(0)] public ushort vt; [FieldOffset(8)] public IntPtr p; [FieldOffset(16)] public IntPtr pad; }

        [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IPropertyStore
        {
            void GetCount(out uint c);
            void GetAt(uint i, out PROPERTYKEY k);
            void GetValue(ref PROPERTYKEY k, out PROPVARIANT v);
            void SetValue(ref PROPERTYKEY k, ref PROPVARIANT v);
            void Commit();
        }

        [DllImport("shell32.dll")] private static extern int SHGetPropertyStoreForWindow(IntPtr hwnd, ref Guid iid, [MarshalAs(UnmanagedType.Interface)] out IPropertyStore store);

        /// <summary>
        /// Gives the window AI Meter's identity in the taskbar: its own button and icon instead of those of the host
        /// process (PowerShell, when launched from source). icon = "path,index" of the icon to show.
        /// </summary>
        public static void SetWindowAppId(IntPtr hwnd, string id, string icon, string name, string command)
        {
            try
            {
                var iid = new Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99");
                IPropertyStore store;
                if (SHGetPropertyStoreForWindow(hwnd, ref iid, out store) != 0 || store == null) return;
                var fmt = new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3");
                // Relaunch command, name and icon first, the identity last (as the documentation recommends).
                SetString(store, fmt, 2, command);
                SetString(store, fmt, 4, name);
                SetString(store, fmt, 3, icon);
                SetString(store, fmt, 5, id);
                store.Commit();
                Marshal.ReleaseComObject(store);
            }
            catch (COMException) { }
            catch (EntryPointNotFoundException) { }
        }

        private static void SetString(IPropertyStore store, Guid fmt, uint pid, string value)
        {
            if (value == null) return;
            var key = new PROPERTYKEY { fmtid = fmt, pid = pid };
            var v = new PROPVARIANT { vt = 31 /* VT_LPWSTR */, p = Marshal.StringToCoTaskMemUni(value) };
            try { store.SetValue(ref key, ref v); }
            finally { Marshal.FreeCoTaskMem(v.p); }
        }

        /// <summary>App identity for the taskbar: AI Meter windows group under its name and icon, not under PowerShell.</summary>
        public static void SetAppId(string id)
        {
            if (IsPackaged) return;   // In the MSIX package the identity is already the package's.
            try { SetCurrentProcessExplicitAppUserModelID(id); }
            catch (EntryPointNotFoundException) { }
        }

        /// <summary>
        /// Name and icon Windows uses for the app's notifications (per-user registry, as for unpackaged apps):
        /// without them, notifications started from PowerShell would appear as "Windows PowerShell".
        /// </summary>
        public static void RegisterAppIdentity(string id, string name, string iconPath)
        {
            if (IsPackaged) return;
            try
            {
                using (var k = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(@"Software\Classes\AppUserModelId\" + id))
                {
                    if (k == null) return;
                    k.SetValue("DisplayName", name);
                    if (iconPath != null) k.SetValue("IconUri", iconPath);
                }
            }
            catch (UnauthorizedAccessException) { }
            catch (System.Security.SecurityException) { }
        }

        // --- Start menu shortcut carrying the app identity (notification name and icon) -----------
        [ComImport, Guid("000214F9-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IShellLinkW
        {
            void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder file, int max, IntPtr fd, uint flags);
            void GetIDList(out IntPtr pidl);
            void SetIDList(IntPtr pidl);
            void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder name, int max);
            void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string name);
            void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder dir, int max);
            void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string dir);
            void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder args, int max);
            void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string args);
            void GetHotkey(out short key);
            void SetHotkey(short key);
            void GetShowCmd(out int cmd);
            void SetShowCmd(int cmd);
            void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder path, int max, out int index);
            void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string path, int index);
            void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string path, uint reserved);
            void Resolve(IntPtr hwnd, uint flags);
            void SetPath([MarshalAs(UnmanagedType.LPWStr)] string file);
        }

        [ComImport, Guid("0000010B-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IPersistFileW
        {
            void GetClassID(out Guid clsid);
            [PreserveSig] int IsDirty();
            void Load([MarshalAs(UnmanagedType.LPWStr)] string file, uint mode);
            void Save([MarshalAs(UnmanagedType.LPWStr)] string file, bool remember);
            void SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string file);
            void GetCurFile([MarshalAs(UnmanagedType.LPWStr)] out string file);
        }

        /// <summary>
        /// Creates (or updates) the Start menu shortcut carrying the app identity: Windows takes the name and icon of
        /// notifications and taskbar buttons from it. Without it, notifications show the technical identifier and no icon.
        /// </summary>
        public static void EnsureStartShortcut(string lnk, string target, string args, string workDir, string icon, int iconIndex, string appId)
        {
            if (IsPackaged) return;
            try
            {
                var link = (IShellLinkW)Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("00021401-0000-0000-C000-000000000046")));
                bool exists = System.IO.File.Exists(lnk);
                if (exists) ((IPersistFileW)link).Load(lnk, 2 /* STGM_READWRITE */);
                else
                {
                    link.SetPath(target);
                    link.SetArguments(args ?? "");
                    link.SetWorkingDirectory(workDir ?? "");
                    link.SetIconLocation(icon, iconIndex);
                    link.SetDescription("AI Meter");
                }
                var store = (IPropertyStore)link;
                var key = new PROPERTYKEY { fmtid = new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), pid = 5 };
                PROPVARIANT current;
                store.GetValue(ref key, out current);
                string have = current.vt == 31 ? Marshal.PtrToStringUni(current.p) : null;
                if (exists && have == appId) { Marshal.ReleaseComObject(link); return; }
                SetString(store, key.fmtid, 5, appId);
                store.Commit();
                System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(lnk));
                ((IPersistFileW)link).Save(lnk, true);
                Marshal.ReleaseComObject(link);
            }
            catch (COMException) { }
            catch (UnauthorizedAccessException) { }
            catch (System.IO.IOException) { }
            catch (InvalidCastException) { }
        }

        /// <summary>Rewrites a plain shortcut from scratch, without an identity (Startup folder).</summary>
        public static void SaveShortcut(string lnk, string target, string args, string workDir, string icon, int iconIndex)
        {
            if (IsPackaged) return;
            try
            {
                var link = (IShellLinkW)Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("00021401-0000-0000-C000-000000000046")));
                link.SetPath(target);
                link.SetArguments(args ?? "");
                link.SetWorkingDirectory(workDir ?? "");
                link.SetIconLocation(icon, iconIndex);
                link.SetDescription("AI Meter");
                System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(lnk));
                ((IPersistFileW)link).Save(lnk, true);
                Marshal.ReleaseComObject(link);
            }
            catch (COMException) { }
            catch (UnauthorizedAccessException) { }
            catch (System.IO.IOException) { }
            catch (InvalidCastException) { }
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern int GetCurrentPackageFullName(ref int length, StringBuilder name);

        private static int packaged = -1;

        /// <summary>True if the app runs from the MSIX package (Microsoft Store), i.e. it has a package identity.</summary>
        public static bool IsPackaged
        {
            get
            {
                if (packaged < 0)
                {
                    int n = 0;
                    try { packaged = GetCurrentPackageFullName(ref n, null) == 15700 /* APPMODEL_ERROR_NO_PACKAGE */ ? 0 : 1; }
                    catch (EntryPointNotFoundException) { packaged = 0; }
                }
                return packaged == 1;
            }
        }
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindowExW(IntPtr parent, IntPtr after, string cls, string title);
        [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr h, out RECT r);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern uint RegisterWindowMessage(string name);

        /// <summary>Position and size of a window as text, to detect whether it has changed.</summary>
        public static string RectKey(IntPtr h)
        {
            RECT r;
            return GetWindowRect(h, out r) ? r.Left + "," + r.Top + "," + r.Right + "," + r.Bottom : "";
        }

        /// <summary>Left edge of the primary taskbar's notification area (physical pixels), 0 if it cannot be found.</summary>
        public static int TrayLeft()
        {
            IntPtr bar = FindWindowW("Shell_TrayWnd", null);
            IntPtr tray = bar == IntPtr.Zero ? IntPtr.Zero : FindWindowExW(bar, IntPtr.Zero, "TrayNotifyWnd", null);
            RECT r;
            return tray != IntPtr.Zero && GetWindowRect(tray, out r) && r.Right > r.Left ? r.Left : 0;
        }

        /// <summary>True if the window is a taskbar (primary or secondary).</summary>
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);

        public static bool IsOwnWindow(IntPtr hwnd)
        {
            uint pid;
            GetWindowThreadProcessId(hwnd, out pid);
            return pid == (uint)System.Diagnostics.Process.GetCurrentProcess().Id;
        }

        public static bool IsTaskbar(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return false;
            var sb = new StringBuilder(64);
            GetClassNameW(hwnd, sb, sb.Capacity);
            string c = sb.ToString();
            return c == "Shell_TrayWnd" || c == "Shell_SecondaryTrayWnd";
        }

        /// <summary>Taskbar edge: 0 left, 1 top, 2 right, 3 bottom.</summary>
        public static int TaskbarEdge()
        {
            var d = new APPBARDATA();
            d.cbSize = Marshal.SizeOf(d);
            SHAppBarMessage(5 /* ABM_GETTASKBARPOS */, ref d);
            return (int)d.uEdge;
        }
    }
}
