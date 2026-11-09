using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using Forms = System.Windows.Forms;

namespace AIMeter
{
    /// <summary>
    /// Public entry point for the PowerShell launcher (AIMeter.ps1). Smart App Control blocks unsigned
    /// executables, whilst Microsoft-signed PowerShell can compile and execute the sources.
    /// </summary>
    public static class Entry
    {
        public static void Run(string[] args)
        {
            // Configure per-monitor DPI before WPF initialises when the executable manifest is unavailable.
            Native.EnablePerMonitorDpi();
            Native.SetAppId("AIMeter.App");
            AppContext.SetSwitch("Switch.System.Windows.DoNotScaleForDpiChanges", false);
            // Touch and pen through the Windows pointer messages (WM_POINTER) instead of the older WISP stack, which
            // did not reliably bring taps and finger drags to WPF, unless a device would make that stack throw.
            TouchStack.Choose();
            Program.Run(args);
        }
    }

    internal static class Program
    {
        /// <summary>Installation directory, provided by the PowerShell launcher or containing the executable.</summary>
        public static string Home
        {
            get { return Environment.GetEnvironmentVariable("AIMETER_HOME") ?? AppDomain.CurrentDomain.BaseDirectory; }
        }

        /// <summary>
        /// Command that starts this copy of the app: for the start-up shortcut, the taskbar identity and the MCP relay.
        /// The packaged app is started through its execution alias (windows.appExecutionAlias in the manifest), whose
        /// path stays the same across Store updates, whilst the executable's folder names the package version.
        /// </summary>
        public static string LaunchCommand
        {
            get
            {
                string launch = Environment.GetEnvironmentVariable("AIMETER_LAUNCH");
                if (launch != null) return launch;
                if (Native.IsPackaged)
                    return "\"" + Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Microsoft\WindowsApps\AIMeter.exe") + "\"";
                return "\"" + System.Reflection.Assembly.GetExecutingAssembly().Location + "\"";
            }
        }

        /// <summary>Split LaunchCommand into an absolute executable path and shortcut arguments.</summary>
        public static void SplitLaunch(out string target, out string args)
        {
            string cmd = LaunchCommand.Trim();
            if (cmd.StartsWith("\"")) { int q = cmd.IndexOf('"', 1); target = cmd.Substring(1, q - 1); args = cmd.Substring(q + 1).Trim(); }
            else { int sp = cmd.IndexOf(' '); target = sp < 0 ? cmd : cmd.Substring(0, sp); args = sp < 0 ? "" : cmd.Substring(sp + 1); }
            if (!Path.IsPathRooted(target)) target = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), target);
        }

        /// <summary>App icon as path,index: the executable or assets\app.ico when running from source.</summary>
        public static string IconPath
        {
            get
            {
                string ico = Path.Combine(Home, @"assets\app.ico");
                if (File.Exists(ico)) return ico + ",0";
                return System.Reflection.Assembly.GetExecutingAssembly().Location + ",0";
            }
        }

        /// <summary>Windows shown in the taskbar use the AI Meter name and icon.</summary>
        public static void Brand(Window w)
        {
            if (Native.IsPackaged) return;   // The MSIX package already supplies the window identity.
            w.SourceInitialized += (o, e) =>
                Native.SetWindowAppId(new System.Windows.Interop.WindowInteropHelper(w).Handle, "AIMeter.App", IconPath, "AI Meter", LaunchCommand);
        }

        /// <summary>
        /// Sample data for previews and screenshots, without network access or notifications.
        /// Set by --demo, or switched at run time from the welcome window, the tray menu or Settings → About.
        /// </summary>
        public static bool Demo { get; internal set; }

        public static Stream OpenAsset(string name)
        {
            var s = typeof(Program).Assembly.GetManifestResourceStream(name);
            if (s != null) return s;
            string f = Path.Combine(Home, "assets", name);
            return File.Exists(f) ? File.OpenRead(f) : null;
        }

        [STAThread]
        private static void Main(string[] args) { Native.SetAppId("AIMeter.App"); TouchStack.Choose(); Run(args); }

        /// <summary>Handled-error log (%APPDATA%\AIMeter\errors.log, rotated at 256 KB), including exception stacks.</summary>
        internal static void LogError(string where, Exception ex) { LogNote(where, ex.ToString()); }

        /// <summary>Write one entry to errors.log; failures to write are ignored.</summary>
        internal static void LogNote(string where, string text)
        {
            try
            {
                string f = Path.Combine(Settings.Folder, "errors.log");
                Directory.CreateDirectory(Settings.Folder);
                if (File.Exists(f) && new FileInfo(f).Length > 256 * 1024) File.Delete(f);
                File.AppendAllText(f, DateTime.Now.ToString("s", System.Globalization.CultureInfo.InvariantCulture) + "  [" + where + "] " + text + Environment.NewLine + Environment.NewLine);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        /// <summary>Marks a copy restarted by RestartOutsidePackage, so that it never restarts again.</summary>
        private const string OutsidePackageArg = "--outside-package";

        /// <summary>
        /// Start this copy again outside another app's MSIX package and return true, so that the caller exits. Inside such
        /// a package, for instance when the Claude app starts AI Meter, Windows redirects writes to %APPDATA%\AIMeter into
        /// that package's private folder: settings, keys and history would split into two diverging copies. A packaged
        /// app's children start outside its package unless it says otherwise, so an ordinary start is enough; the marker
        /// argument stops a second attempt if they do not, and that copy runs where it is.
        /// </summary>
        private static bool RestartOutsidePackage(string[] args)
        {
            if (!Native.InForeignPackage || args.Contains(OutsidePackageArg)) return false;
            string target, launchArgs;
            SplitLaunch(out target, out launchArgs);
            var all = new List<string> { launchArgs };
            all.AddRange(args.Select(a => "\"" + a.Replace("\"", "\\\"") + "\""));
            all.Add(OutsidePackageArg);
            try
            {
                Process.Start(new ProcessStartInfo(target, string.Join(" ", all.Where(a => a.Length > 0))) { UseShellExecute = false, WorkingDirectory = Home });
                return true;
            }
            catch (System.ComponentModel.Win32Exception) { return false; }
        }

        /// <summary>Open a folder in File Explorer, creating it first: Explorer shows Documents for a path that does not exist.</summary>
        internal static void OpenFolder(string folder)
        {
            Directory.CreateDirectory(folder);
            Process.Start("explorer.exe", "\"" + folder + "\"");
        }

        internal static void Run(string[] args)
        {
            if (RestartOutsidePackage(args)) return;
            bool created;
            using (var mutex = new Mutex(true, @"Local\AIMeter.Instance", out created))
            {
                var showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\AIMeter.Show");
                var settingsSignal = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\AIMeter.Settings");
                if (!created)
                {
                    // An existing instance handles requests to open the panel or settings.
                    (args.Contains("--settings") ? settingsSignal : showSignal).Set();
                    return;
                }

                Demo = args.Contains("--demo");
                ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12 | (SecurityProtocolType)12288;
                // Software rendering avoids a Direct3D device (roughly 60 MB) and GPU work.
                // Windows still composites Mica, acrylic and shadows. --gpu enables hardware rendering.
                if (!args.Contains("--gpu")) System.Windows.Media.RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
                var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                // Log unexpected UI errors, such as Windows refusing a window during taskbar recreation,
                // and keep the app running.
                app.DispatcherUnhandledException += (o, e) => { LogError("ui", e.Exception); e.Handled = true; };
                // Errors in the tray icon's events come through the WinForms window procedure, which the dispatcher
                // handler never sees; without this handler WinForms shows its own crash dialog.
                Forms.Application.ThreadException += (o, e) => LogError("tray", e.Exception);
                if (TouchStack.WispReason != null) LogNote("touch", TouchStack.WispReason);
                bool atLogin = args.Contains("--startup");
#if MSIX
                atLogin = atLogin || (Native.IsPackaged && PackageStartup.LaunchedAtLogin);
#endif
                var controller = new Controller(atLogin, showSignal, settingsSignal);
                app.Startup += (o, e) => controller.Start();
                app.Run();
                GC.KeepAlive(mutex);
            }
        }
    }
}
