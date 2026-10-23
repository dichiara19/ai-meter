using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;

namespace AIMeter
{
    /// <summary>
    /// Traduzioni. Il testo sorgente è l'inglese e fa da chiave: L.T("Settings") restituisce la traduzione
    /// nella lingua attiva, o l'inglese se manca. Le tabelle sono in StringsTable.cs.
    /// </summary>
    internal static partial class L
    {
        /// <summary>Lingue supportate: codice → nome nella lingua stessa.</summary>
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

        static L() { Init("system"); }

        /// <summary>"system" segue la lingua di Windows; altrimenti un codice di Languages.</summary>
        public static void Init(string setting)
        {
            var ui = CultureInfo.CurrentUICulture;
            string code = string.IsNullOrEmpty(setting) || setting == "system" ? ui.TwoLetterISOLanguageName : setting;
            if (!Languages.Any(l => l.Key == code)) code = "en";
            Lang = code;
            // formati regionali dell'utente se la lingua coincide (es. it-CH), altrimenti quelli tipici della lingua
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
            return table != null && en != null && table.TryGetValue(en, out s) ? s : en;
        }

        public static string F(string en, params object[] args) { return string.Format(Culture, T(en), args); }

        /// <summary>Singolare o plurale secondo n, con n come {0}.</summary>
        public static string N(int n, string one, string many) { return F(n == 1 ? one : many, n); }
    }
}
