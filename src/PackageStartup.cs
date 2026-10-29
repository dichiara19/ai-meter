#if MSIX
using System;
using System.Diagnostics;
using System.Threading;
using Windows.ApplicationModel;
using Windows.Foundation;

namespace AIMeter
{
    /// <summary>
    /// Start-up in the MSIX package: the registry Run key is virtualised, so the startup task declared in the manifest
    /// (windows.startupTask) is used, the same one the user sees in Settings > Apps > Startup.
    /// </summary>
    internal static class PackageStartup
    {
        private const string TaskId = "AIMeterStartup";

        public static bool Enabled
        {
            get
            {
                var t = Task();
                return t != null && (t.State == StartupTaskState.Enabled || t.State == StartupTaskState.EnabledByPolicy);
            }
            set
            {
                var t = Task();
                if (t == null) return;
                if (!value) { t.Disable(); return; }
                var state = Wait(t.RequestEnableAsync());
                // Disabled by the user in Settings: only the user can re-enable it, from there.
                if (state == StartupTaskState.DisabledByUser)
                    Process.Start(new ProcessStartInfo("ms-settings:startupapps") { UseShellExecute = true });
            }
        }

        /// <summary>True if Windows started the app at sign-in through the startup task.</summary>
        public static bool LaunchedAtLogin
        {
            get
            {
                try
                {
                    var a = AppInstance.GetActivatedEventArgs();
                    return a != null && a.Kind == Windows.ApplicationModel.Activation.ActivationKind.StartupTask;
                }
                catch (Exception) { return false; }
            }
        }

        private static StartupTask Task()
        {
            try { return Wait(StartupTask.GetAsync(TaskId)); }
            catch (Exception) { return null; }
        }

        private static R Wait<R>(IAsyncOperation<R> op)
        {
            using (var done = new ManualResetEvent(false))
            {
                op.Completed = (o, s) => done.Set();
                done.WaitOne(5000);
                return op.GetResults();
            }
        }
    }
}
#endif
