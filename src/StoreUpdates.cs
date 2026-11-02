#if MSIX
using System;
using Windows.Services.Store;

namespace AIMeter
{
    /// <summary>
    /// Microsoft Store package only: asks the Store whether an update of this package is waiting, so Settings can say so.
    /// It never downloads or installs anything; the Store does that by itself.
    /// </summary>
    internal static class StoreUpdates
    {
        /// <summary>True when the Store reports a pending update; false when there is none or the Store cannot answer.</summary>
        public static bool Waiting()
        {
            try
            {
                var updates = PackageStartup.Wait(StoreContext.GetDefault().GetAppAndOptionalStorePackageUpdatesAsync(), 30000);
                return updates != null && updates.Count > 0;
            }
            catch (Exception) { return false; }   // Sideloaded package, Store unavailable or slow: say nothing.
        }
    }
}
#endif
