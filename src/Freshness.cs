using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Threading;

namespace AIMeter
{
    /// <summary>
    /// Lets MCP callers ask for a fresh reading without starting a second collector. A request marks the chosen
    /// providers for a network read, wakes the existing collection worker and waits until that worker publishes a
    /// snapshot started after the request. Callers waiting at the same time therefore share one collection, a caller
    /// that gives up does not cancel it, and periodic and requested collection never run side by side.
    ///
    /// A provider is read again at most once per MinimumSeconds on request, and never before the time its service
    /// asked for with Retry-After (NetworkBackoff).
    /// </summary>
    internal sealed class RefreshGate
    {
        public const int MinimumSeconds = 60;
        public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(20);

        private readonly Action<string, bool> invalidate;   // Provider id, whether the free-model list is wanted.
        private readonly Action wake;
        private readonly Func<bool> paused;
        private readonly object sync = new object();
        private readonly Dictionary<string, DateTime> requestedAt = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        private Snapshot published;
        private bool networkWanted;

        public RefreshGate(Action<string, bool> invalidate, Action wake, Func<bool> paused)
        {
            this.invalidate = invalidate;
            this.wake = wake;
            this.paused = paused;
        }

        /// <summary>Worker side: whether the next collection must be allowed to use the network. Clears the request.</summary>
        public bool TakeNetworkRequest()
        {
            lock (sync)
            {
                bool wanted = networkWanted;
                networkWanted = false;
                return wanted;
            }
        }

        /// <summary>
        /// The newer of the given snapshot and the last one published. The worker publishes before it records history
        /// and before the UI applies the snapshot, so a published reading can be newer than the one surfaces show.
        /// </summary>
        public Snapshot Newest(Snapshot shown)
        {
            lock (sync) return published != null && (shown == null || published.Taken > shown.Taken) ? published : shown;
        }

        /// <summary>Worker side: release every caller waiting for a collection that started before this snapshot.</summary>
        public void Publish(Snapshot snapshot)
        {
            lock (sync)
            {
                published = snapshot;
                Monitor.PulseAll(sync);
            }
        }

        /// <summary>
        /// Read the given providers again (network ids), or only re-read local state when the list is empty, and wait
        /// up to timeout for the collection. Returns the current snapshot unchanged when collection is paused, when
        /// every provider was skipped, or, marked TimedOut, when the collection did not finish in time.
        /// </summary>
        public RefreshResult Request(IEnumerable<string> networkIds, bool catalogues, Snapshot current, TimeSpan timeout)
        {
            var result = new RefreshResult { Snapshot = Newest(current) };
            var ids = networkIds.ToList();
            if (paused())
            {
                result.Paused = true;
                foreach (var id in ids) result.Skipped[id] = "paused";
                return result;
            }

            bool startCollection;
            lock (sync)
            {
                DateTime now = DateTime.Now;
                var fresh = new List<string>();
                foreach (var id in ids)
                {
                    DateTime until = NetworkBackoff.Until(id);
                    DateTime previous;
                    if (until > now) { result.Skipped[id] = "backoff"; result.RetryAt[id] = until; continue; }
                    if (requestedAt.TryGetValue(Key(id, catalogues), out previous) && (now - previous).TotalSeconds < MinimumSeconds)
                    {
                        if (published == null || published.Taken < previous)
                            result.AskedAt[id] = previous;   // A read is already on its way: wait for it too.
                        else
                        {
                            result.Skipped[id] = "too_soon";
                            result.RetryAt[id] = previous.AddSeconds(MinimumSeconds);
                        }
                        continue;
                    }
                    invalidate(id, catalogues);
                    fresh.Add(id);
                }
                if (ids.Count > 0 && result.AskedAt.Count == 0 && fresh.Count == 0) return result;   // Nothing to read: answer at once.

                // Take the time after invalidating, so any snapshot started later has seen the invalidation.
                DateTime stamp = DateTime.Now;
                foreach (var id in fresh) requestedAt[Key(id, catalogues)] = result.AskedAt[id] = stamp;
                if (fresh.Count > 0) networkWanted = true;
                startCollection = ids.Count == 0 || fresh.Count > 0;
                result.RequestedAt = startCollection ? stamp : result.AskedAt.Values.Max();
            }

            if (startCollection) wake();   // Joining reads already on their way needs no new collection.
            DateTime deadline = DateTime.UtcNow + timeout;
            lock (sync)
            {
                while (published == null || published.Taken < result.RequestedAt)
                {
                    int left = (int)(deadline - DateTime.UtcNow).TotalMilliseconds;
                    if (left <= 0) { result.TimedOut = true; return result; }
                    if (paused()) { result.Paused = true; return result; }
                    Monitor.Wait(sync, Math.Min(left, 1000));
                }
                result.Snapshot = published;
            }
            return result;
        }

        /// <summary>Usage and free-model lists are read on separate timers, so their requests are tracked apart.</summary>
        private static string Key(string id, bool catalogue) { return catalogue ? "catalogue:" + id : id; }
    }

    /// <summary>The snapshot a refresh produced and the providers it could not read again.</summary>
    internal sealed class RefreshResult
    {
        public Snapshot Snapshot;
        public DateTime RequestedAt = DateTime.MinValue;   // The collection awaited started after this; MinValue when none was.
        public bool TimedOut, Paused;
        /// <summary>Provider id to the time its read was requested, by this caller or by one it joined.</summary>
        public readonly Dictionary<string, DateTime> AskedAt = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        /// <summary>Provider id to the reason it was not read again: "backoff", "too_soon" or "paused".</summary>
        public readonly Dictionary<string, string> Skipped = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        /// <summary>Provider id to the earliest time a new read can be requested.</summary>
        public readonly Dictionary<string, DateTime> RetryAt = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Upstream rate limits, per provider id. A service that answers 429, or 503 with Retry-After, is not contacted
    /// again before the time it asked for, whether the read is periodic or requested through MCP.
    /// </summary>
    internal static class NetworkBackoff
    {
        public static readonly TimeSpan DefaultDelay = TimeSpan.FromMinutes(5);
        private static readonly TimeSpan MaximumDelay = TimeSpan.FromHours(24);
        private static readonly Dictionary<string, DateTime> until = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);

        public static bool Ready(string id) { return Until(id) <= DateTime.Now; }

        public static DateTime Until(string id)
        {
            DateTime t;
            lock (until) return until.TryGetValue(id, out t) ? t : DateTime.MinValue;
        }

        /// <summary>Record a rate-limit answer; other failures are left to each provider's own handling.</summary>
        public static void Note(string id, WebException e)
        {
            var response = e.Response as HttpWebResponse;
            if (response == null) return;
            int status = (int)response.StatusCode;
            TimeSpan? asked = Parse(response.Headers["Retry-After"], DateTime.UtcNow);
            if (status != 429 && !(status == 503 && asked.HasValue)) return;
            Set(id, DateTime.Now + (asked ?? DefaultDelay));
        }

        public static void Set(string id, DateTime time)
        {
            lock (until) until[id] = time;
        }

        public static void Clear() { lock (until) until.Clear(); }

        /// <summary>Retry-After as delay seconds or an HTTP date, capped at a day; null when absent or invalid.</summary>
        internal static TimeSpan? Parse(string value, DateTime nowUtc)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;
            value = value.Trim();
            int seconds;
            DateTime date;
            TimeSpan delay;
            if (int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out seconds)) delay = TimeSpan.FromSeconds(seconds);
            else if (DateTime.TryParseExact(value, "r", CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out date))
                delay = date - nowUtc;
            else return null;
            if (delay < TimeSpan.Zero) delay = TimeSpan.Zero;
            return delay > MaximumDelay ? MaximumDelay : delay;
        }
    }
}
