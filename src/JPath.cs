using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Shell;

namespace AIMeter
{
    /// <summary>
    /// JSON paths support dotted fields, array indices, filters and "$" for the root.
    /// </summary>
    internal static class JPath
    {
        private static readonly Regex Token = new Regex(@"([^.\[\]]+)|\[([^\]]*)\]");

        public static object Get(object root, string path)
        {
            if (string.IsNullOrEmpty(path) || path == "$") return root;
            object cur = root;
            foreach (Match m in Token.Matches(path.StartsWith("$.") ? path.Substring(2) : path))
            {
                if (cur == null) return null;
                if (m.Groups[1].Success)
                {
                    var d = cur as Dictionary<string, object>;
                    object v;
                    cur = d != null && d.TryGetValue(m.Groups[1].Value, out v) ? v : null;
                    continue;
                }
                var list = cur as IList;
                if (list == null) return null;
                string sel = m.Groups[2].Value;
                int i;
                if (int.TryParse(sel, NumberStyles.Integer, CultureInfo.InvariantCulture, out i))
                {
                    if (i < 0) i += list.Count;
                    cur = i >= 0 && i < list.Count ? list[i] : null;
                }
                else cur = list.Cast<object>().FirstOrDefault(x => Matches(x, sel));
            }
            return cur;
        }

        /// <summary>Match an array item using field=value or field!=value.</summary>
        public static bool Matches(object item, string filter)
        {
            if (string.IsNullOrEmpty(filter)) return true;
            bool not = filter.Contains("!=");
            var parts = filter.Split(new[] { not ? "!=" : "=" }, 2, StringSplitOptions.None);
            if (parts.Length < 2) return Truthy(Get(item, filter));
            bool eq = string.Equals(Str(Get(item, parts[0].Trim())), parts[1].Trim(), StringComparison.OrdinalIgnoreCase);
            return not ? !eq : eq;
        }

        public static string Str(object v)
        {
            if (v == null) return null;
            if (v is bool) return (bool)v ? "true" : "false";
            return Convert.ToString(v, CultureInfo.InvariantCulture);
        }

        public static double? Num(object v)
        {
            if (v == null || v is bool) return null;
            if (v is string)
            {
                double d;
                return double.TryParse((string)v, NumberStyles.Float, CultureInfo.InvariantCulture, out d) ? d : (double?)null;
            }
            try { return Convert.ToDouble(v, CultureInfo.InvariantCulture); }
            catch (FormatException) { return null; }
            catch (InvalidCastException) { return null; }
        }

        public static bool Truthy(object v)
        {
            if (v == null) return false;
            if (v is bool) return (bool)v;
            if (v is string) return ((string)v).Length > 0 && (string)v != "false" && (string)v != "0";
            var n = Num(v);
            if (n != null) return n.Value != 0;
            var c = v as ICollection;
            return c == null || c.Count > 0;
        }

        /// <summary>Parse ISO dates or Unix timestamps, distinguishing seconds from milliseconds by magnitude.</summary>
        public static DateTime? Date(object v)
        {
            var n = Num(v);
            if (n != null && !(v is string && ((string)v).Contains("-"))) return n.Value > 1e11 ? Json.FromUnixMs(n) : Json.FromUnix(n);
            return Json.FromIso(Str(v));
        }
    }
}
