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
    /// <summary>Store user-supplied API keys encrypted with the current user's DPAPI protection.</summary>
    internal static class ApiKeys
    {
        private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("AIMeter.ApiKey");
        private static string FilePath(string id) { return Path.Combine(Settings.Folder, "keys", id + ".bin"); }

        public static bool Has(string id) { return File.Exists(FilePath(id)); }

        public static string Get(string id)
        {
            try
            {
                if (!Has(id)) return null;
                return Encoding.UTF8.GetString(ProtectedData.Unprotect(File.ReadAllBytes(FilePath(id)), Entropy, DataProtectionScope.CurrentUser));
            }
            catch (CryptographicException) { return null; }
            catch (IOException) { return null; }
        }

        public static void Save(string id, string key)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath(id)));
            File.WriteAllBytes(FilePath(id), ProtectedData.Protect(Encoding.UTF8.GetBytes(key.Trim()), Entropy, DataProtectionScope.CurrentUser));
        }

        public static void Remove(string id) { try { File.Delete(FilePath(id)); } catch (IOException) { } }
    }
}
