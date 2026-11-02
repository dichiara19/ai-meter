using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace AIMeter
{
    /// <summary>A Semantic Versioning 2.0 version such as 0.9.0 or 0.9.0-beta.2; build metadata is ignored.</summary>
    internal sealed class SemVersion : IComparable<SemVersion>
    {
        private static readonly Regex Pattern = new Regex(
            @"^v?(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)" +
            @"(?:-((?:0|[1-9][0-9]*|[0-9]*[A-Za-z-][0-9A-Za-z-]*)(?:\.(?:0|[1-9][0-9]*|[0-9]*[A-Za-z-][0-9A-Za-z-]*))*))?" +
            @"(?:\+[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?$", RegexOptions.CultureInvariant);

        public int Major { get; private set; }
        public int Minor { get; private set; }
        public int Patch { get; private set; }
        /// <summary>Pre-release identifiers, empty for a stable version.</summary>
        public string[] PreRelease { get; private set; }
        public bool IsPreRelease { get { return PreRelease.Length > 0; } }

        /// <summary>Parse a version or a tag with a leading "v"; null when it is not valid SemVer.</summary>
        public static SemVersion Parse(string text)
        {
            if (string.IsNullOrEmpty(text)) return null;
            var m = Pattern.Match(text.Trim());
            if (!m.Success) return null;
            int major, minor, patch;
            if (!int.TryParse(m.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out major)
                || !int.TryParse(m.Groups[2].Value, NumberStyles.None, CultureInfo.InvariantCulture, out minor)
                || !int.TryParse(m.Groups[3].Value, NumberStyles.None, CultureInfo.InvariantCulture, out patch)) return null;
            return new SemVersion
            {
                Major = major, Minor = minor, Patch = patch,
                PreRelease = m.Groups[4].Success ? m.Groups[4].Value.Split('.') : new string[0],
            };
        }

        /// <summary>SemVer precedence: a pre-release sorts before its stable version, identifiers compare one by one.</summary>
        public int CompareTo(SemVersion other)
        {
            int c = Major.CompareTo(other.Major);
            if (c == 0) c = Minor.CompareTo(other.Minor);
            if (c == 0) c = Patch.CompareTo(other.Patch);
            if (c != 0) return c;
            if (!IsPreRelease || !other.IsPreRelease) return other.PreRelease.Length.CompareTo(PreRelease.Length);
            for (int i = 0; i < Math.Min(PreRelease.Length, other.PreRelease.Length); i++)
            {
                c = CompareIdentifier(PreRelease[i], other.PreRelease[i]);
                if (c != 0) return c;
            }
            return PreRelease.Length.CompareTo(other.PreRelease.Length);
        }

        private static int CompareIdentifier(string a, string b)
        {
            bool numericA = a.All(char.IsDigit), numericB = b.All(char.IsDigit);
            // Numeric identifiers have no leading zeros, so length then digits orders them without overflow.
            if (numericA && numericB) return a.Length != b.Length ? a.Length.CompareTo(b.Length) : string.CompareOrdinal(a, b);
            if (numericA != numericB) return numericA ? -1 : 1;
            return Math.Sign(string.CompareOrdinal(a, b));
        }

        public override string ToString()
        {
            string core = Major.ToString(CultureInfo.InvariantCulture) + "." + Minor.ToString(CultureInfo.InvariantCulture) + "." + Patch.ToString(CultureInfo.InvariantCulture);
            return IsPreRelease ? core + "-" + string.Join(".", PreRelease) : core;
        }
    }

    /// <summary>
    /// Daily check for a newer release on GitHub, for the zip and script builds. It sends one anonymous GET to the
    /// GitHub API and only reports what it found: nothing is downloaded or installed. The Microsoft Store package
    /// never calls it, because the Store updates the app.
    /// </summary>
    internal static class UpdateCheck
    {
        private const string Repository = "https://github.com/dichiara19/ai-meter";
        /// <summary>The latest stable release: GitHub excludes drafts and pre-releases from this endpoint.</summary>
        internal const string LatestUrl = "https://api.github.com/repos/dichiara19/ai-meter/releases/latest";
        /// <summary>Recent releases including pre-releases, for the opt-in pre-release channel.</summary>
        internal const string RecentUrl = "https://api.github.com/repos/dichiara19/ai-meter/releases?per_page=20";

        internal static bool Enabled(Settings s) { return s.UpdateCheck && !Program.Demo; }

        /// <summary>At most one automatic check a day; a clock moved back a day or more also allows one.</summary>
        internal static bool Due(DateTime lastUtc, DateTime nowUtc)
        {
            return Math.Abs((nowUtc - lastUtc).TotalHours) >= 24;
        }

        /// <summary>Whether candidate is a valid version newer than the running one; false if either is malformed.</summary>
        internal static bool IsNewer(string candidate, string running)
        {
            var a = SemVersion.Parse(candidate);
            var b = SemVersion.Parse(running);
            return a != null && b != null && a.CompareTo(b) > 0;
        }

        /// <summary>Release page of a version; built from the parsed version, never from a URL in the response.</summary>
        internal static string PageFor(string version)
        {
            var v = SemVersion.Parse(version);
            return v == null ? Repository + "/releases" : Repository + "/releases/tag/v" + v;
        }

        /// <summary>
        /// Highest published version, or null when packaged, offline, rate-limited or the answer is unreadable.
        /// Without preReleases only stable versions count, even if a pre-release tag was published without the flag.
        /// </summary>
        internal static SemVersion Find(bool preReleases, Func<string, string> download)
        {
            if (Native.IsPackaged) return null;   // Store policy: the Store alone updates the package.
            string body = download(preReleases ? RecentUrl : LatestUrl);
            if (string.IsNullOrEmpty(body)) return null;
            object parsed = Json.ParseAny(body);
            var releases = parsed is object[] ? ((object[])parsed).OfType<Dictionary<string, object>>() : new[] { parsed as Dictionary<string, object> };
            SemVersion best = null;
            foreach (var r in releases)
            {
                if (r == null || Json.Bool(r, "draft") || (!preReleases && Json.Bool(r, "prerelease"))) continue;
                var v = SemVersion.Parse(Json.Str(r, "tag_name"));
                if (v == null || (!preReleases && v.IsPreRelease)) continue;
                if (best == null || v.CompareTo(best) > 0) best = v;
            }
            return best;
        }

        /// <summary>The plain request: no cookies, credentials or identifiers; GitHub only requires a User-Agent.</summary>
        internal static string Download(string url)
        {
            try
            {
                var req = (HttpWebRequest)WebRequest.Create(url);
                req.Accept = "application/vnd.github+json";
                req.UserAgent = "ai-meter";
                req.Timeout = 15000;
                req.ReadWriteTimeout = 15000;
                using (var resp = (HttpWebResponse)req.GetResponse())
                using (var r = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                    return r.ReadToEnd();
            }
            catch (WebException) { return null; }   // Offline, rate-limited (403/429) or no release yet (404).
            catch (IOException) { return null; }
        }
    }
}
