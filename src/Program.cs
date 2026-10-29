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

        /// <summary>Command Windows uses to launch the app at sign-in.</summary>
        public static string LaunchCommand
        {
            get
            {
                return Environment.GetEnvironmentVariable("AIMETER_LAUNCH")
                       ?? "\"" + System.Reflection.Assembly.GetExecutingAssembly().Location + "\"";
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

        /// <summary>--demo uses sample data for previews and screenshots, without network access or notifications.</summary>
        public static bool Demo { get; private set; }

        public static Stream OpenAsset(string name)
        {
            var s = typeof(Program).Assembly.GetManifestResourceStream(name);
            if (s != null) return s;
            string f = Path.Combine(Home, "assets", name);
            return File.Exists(f) ? File.OpenRead(f) : null;
        }

        [STAThread]
        private static void Main(string[] args) { Native.SetAppId("AIMeter.App"); Run(args); }

        /// <summary>Handled-error log (%APPDATA%\AIMeter\errors.log, rotated at 256 KB), including exception stacks.</summary>
        internal static void LogError(string where, Exception ex)
        {
            try
            {
                string f = Path.Combine(Settings.Folder, "errors.log");
                Directory.CreateDirectory(Settings.Folder);
                if (File.Exists(f) && new FileInfo(f).Length > 256 * 1024) File.Delete(f);
                File.AppendAllText(f, DateTime.Now.ToString("s", System.Globalization.CultureInfo.InvariantCulture) + "  [" + where + "] " + ex + Environment.NewLine + Environment.NewLine);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        internal static void Run(string[] args)
        {
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
