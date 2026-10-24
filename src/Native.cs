using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace AIMeter
{
    /// <summary>Chiamate Win32 usate dall'app. Tutto ciò che è costoso è evitato o messo in cache.</summary>
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

        /// <summary>pid → nome eseguibile in minuscolo. Una sola istantanea Toolhelp: ~1 ms, nessuna WMI.</summary>
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

        // --- risparmio energetico ------------------------------------------------
        [StructLayout(LayoutKind.Sequential)]
        private struct POWER_THROTTLING_STATE { public uint Version, ControlMask, StateMask; }

        [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentThread();
        [DllImport("kernel32.dll")] private static extern bool SetThreadInformation(IntPtr h, int cls, ref POWER_THROTTLING_STATE info, int size);

        /// <summary>EcoQoS sul thread corrente: Windows lo esegue sui core efficienti a bassa frequenza.</summary>
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

        /// <summary>Vero se il PC va a batteria o il Risparmio energia è attivo.</summary>
        public static bool SavingPower()
        {
            SYSTEM_POWER_STATUS s;
            if (!GetSystemPowerStatus(out s)) return false;
            return s.SystemStatusFlag == 1 || s.ACLineStatus == 0;
        }

        [DllImport("user32.dll")] private static extern bool SetProcessDpiAwarenessContext(IntPtr value);

        /// <summary>DPI per monitor (V2). Serve quando manca il manifest dell'eseguibile, cioè nell'avvio da PowerShell.</summary>
        public static void EnablePerMonitorDpi()
        {
            try { SetProcessDpiAwarenessContext(new IntPtr(-4)); }
            catch (EntryPointNotFoundException) { }
        }

        // --- finestre e DWM -------------------------------------------------------
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

        // --- schermo intero / presentazioni -------------------------------------
        [DllImport("shell32.dll")] private static extern int SHQueryUserNotificationState(out int state);

        /// <summary>Vero quando l'utente gioca, presenta o guarda un video a schermo intero.</summary>
        public static bool UserIsBusyFullscreen()
        {
            int st;
            if (SHQueryUserNotificationState(out st) != 0) return false;
            return st == 2 || st == 3 || st == 4; // BUSY, RUNNING_D3D_FULL_SCREEN, PRESENTATION_MODE
        }

        public delegate void WinEventProc(IntPtr hook, uint ev, IntPtr hwnd, int obj, int child, uint thread, uint time);
        [DllImport("user32.dll")] public static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr mod, WinEventProc proc, uint pid, uint tid, uint flags);
        public const uint EVENT_SYSTEM_FOREGROUND = 3;

        // --- area di notifica ----------------------------------------------------
        [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential)]
        private struct APPBARDATA { public int cbSize; public IntPtr hWnd; public uint uCallbackMessage, uEdge; public RECT rc; public IntPtr lParam; }
        [DllImport("shell32.dll")] private static extern IntPtr SHAppBarMessage(uint msg, ref APPBARDATA data);

        /// <summary>Rettangolo (pixel fisici), lato e nascondimento automatico della barra del monitor principale.</summary>
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

        /// <summary>La barra delle applicazioni del monitor principale.</summary>
        public static IntPtr FindTaskbar() { return FindWindowW("Shell_TrayWnd", null); }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern int SetCurrentProcessExplicitAppUserModelID(string id);

        // --- identità della finestra nella barra (proprietà System.AppUserModel.*) ------------------
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
        /// Dà alla finestra l'identità di AI Meter nella barra: pulsante e icona propri invece di quelli del processo
        /// che la ospita (PowerShell, nell'avvio da sorgente). icon = "percorso,indice" dell'icona da mostrare.
        /// </summary>
        public static void SetWindowAppId(IntPtr hwnd, string id, string icon, string name, string command)
        {
            try
            {
                var iid = new Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99");
                IPropertyStore store;
                if (SHGetPropertyStoreForWindow(hwnd, ref iid, out store) != 0 || store == null) return;
                var fmt = new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3");
                // prima comando, nome e icona di rilancio, l'identità per ultima (come raccomanda la documentazione)
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

        /// <summary>Identità dell'app per la barra: le finestre di AI Meter si raggruppano col suo nome e la sua icona, non con PowerShell.</summary>
        public static void SetAppId(string id)
        {
            try { SetCurrentProcessExplicitAppUserModelID(id); }
            catch (EntryPointNotFoundException) { }
        }
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindowExW(IntPtr parent, IntPtr after, string cls, string title);
        [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr h, out RECT r);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern uint RegisterWindowMessage(string name);

        /// <summary>Bordo sinistro dell'area di notifica della barra principale (pixel fisici), 0 se non rilevabile.</summary>
        public static int TrayLeft()
        {
            IntPtr bar = FindWindowW("Shell_TrayWnd", null);
            IntPtr tray = bar == IntPtr.Zero ? IntPtr.Zero : FindWindowExW(bar, IntPtr.Zero, "TrayNotifyWnd", null);
            RECT r;
            return tray != IntPtr.Zero && GetWindowRect(tray, out r) && r.Right > r.Left ? r.Left : 0;
        }

        /// <summary>Vero se la finestra è la barra delle applicazioni (principale o secondaria).</summary>
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

        /// <summary>Lato della barra delle applicazioni: 0 sinistra, 1 alto, 2 destra, 3 basso.</summary>
        public static int TaskbarEdge()
        {
            var d = new APPBARDATA();
            d.cbSize = Marshal.SizeOf(d);
            SHAppBarMessage(5 /* ABM_GETTASKBARPOS */, ref d);
            return (int)d.uEdge;
        }
    }
}
