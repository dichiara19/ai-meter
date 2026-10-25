#if MSIX
using System;
using System.Diagnostics;
using System.Threading;
using Windows.ApplicationModel;
using Windows.Foundation;

namespace AIMeter
{
    /// <summary>
    /// Avvio automatico nel pacchetto MSIX: la chiave Run del registro è virtualizzata, quindi si usa l'attività di avvio
    /// dichiarata nel manifest (windows.startupTask), la stessa che l'utente vede in Impostazioni > App > Avvio.
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
                // disattivata dall'utente in Impostazioni: solo lui può riattivarla, da lì
                if (state == StartupTaskState.DisabledByUser)
                    Process.Start(new ProcessStartInfo("ms-settings:startupapps") { UseShellExecute = true });
            }
        }

        /// <summary>Vero se Windows ha avviato l'app all'accesso tramite l'attività di avvio.</summary>
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
