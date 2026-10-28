using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;

namespace AIMeter
{
    /// <summary>
    /// English source text is the translation key. L.T("Settings") resolves the active language,
    /// falling back to English. Core tables are defined in StringsTable.cs.
    /// </summary>
    internal static partial class L
    {
        /// <summary>Supported language codes and their native names.</summary>
        public static readonly KeyValuePair<string, string>[] Languages =
        {
            new KeyValuePair<string, string>("en", "English"),
            new KeyValuePair<string, string>("it", "Italiano"),
            new KeyValuePair<string, string>("es", "Español"),
            new KeyValuePair<string, string>("fr", "Français"),
            new KeyValuePair<string, string>("de", "Deutsch"),
            new KeyValuePair<string, string>("pt", "Português"),
        };

        public static string Lang { get; private set; }
        public static CultureInfo Culture { get; private set; }
        private static Dictionary<string, string> table;

        static L() { RegisterHistoryStrings(); Init("system"); }

        /// <summary>Use the Windows language for "system", otherwise a supported language code.</summary>
        public static void Init(string setting)
        {
            var ui = CultureInfo.CurrentUICulture;
            string code = string.IsNullOrEmpty(setting) || setting == "system" ? ui.TwoLetterISOLanguageName : setting;
            if (!Languages.Any(l => l.Key == code)) code = "en";
            Lang = code;
            // Preserve regional formats when the language matches, otherwise use that language’s usual culture.
            var current = CultureInfo.CurrentCulture;
            Culture = current.TwoLetterISOLanguageName == code ? current
                : CultureInfo.CreateSpecificCulture(code == "pt" ? "pt-BR" : code == "en" ? "en-US" : code);
            table = Tables.ContainsKey(code) ? Tables[code] : null;
            if (Application.Current != null)
            {
                Application.Current.Resources["StrOn"] = T("On");
                Application.Current.Resources["StrOff"] = T("Off");
            }
        }

        public static string T(string en)
        {
            string s;
            if (en == null) return null;
            if (table != null && table.TryGetValue(en, out s)) return s;
            Dictionary<string, string> more;
            return Extra.TryGetValue(Lang, out more) && more.TryGetValue(en, out s) ? s : en;
        }

        /// <summary>Additional translations from service definitions and feature-specific tables.</summary>
        private static readonly Dictionary<string, Dictionary<string, string>> Extra = new Dictionary<string, Dictionary<string, string>>();

        public static void Register(string lang, string en, string text)
        {
            Dictionary<string, string> more;
            if (!Extra.TryGetValue(lang, out more)) Extra[lang] = more = new Dictionary<string, string>();
            if (!more.ContainsKey(en)) more[en] = text;
        }

        public static string F(string en, params object[] args) { return string.Format(Culture, T(en), args); }

        /// <summary>Choose the singular or plural form, formatting n as {0}.</summary>
        public static string N(int n, string one, string many) { return F(n == 1 ? one : many, n); }
    }
}
