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
    /// <summary>Read SQLite values using the Windows-provided winsqlite3.dll library in read-only mode.</summary>
    internal static class Sqlite
    {
        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.StdCall)] private static extern int sqlite3_open_v2(byte[] file, out IntPtr db, int flags, IntPtr vfs);
        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.StdCall)] private static extern int sqlite3_prepare_v2(IntPtr db, byte[] sql, int n, out IntPtr stmt, IntPtr tail);
        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.StdCall)] private static extern int sqlite3_step(IntPtr stmt);
        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.StdCall)] private static extern IntPtr sqlite3_column_blob(IntPtr stmt, int col);
        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.StdCall)] private static extern int sqlite3_column_bytes(IntPtr stmt, int col);
        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.StdCall)] private static extern int sqlite3_finalize(IntPtr stmt);
        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.StdCall)] private static extern int sqlite3_close(IntPtr db);
        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.StdCall)] private static extern int sqlite3_busy_timeout(IntPtr db, int ms);

        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.StdCall)] private static extern int sqlite3_column_count(IntPtr stmt);
        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.StdCall)] private static extern int sqlite3_column_type(IntPtr stmt, int col);
        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.StdCall)] private static extern long sqlite3_column_int64(IntPtr stmt, int col);
        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.StdCall)] private static extern double sqlite3_column_double(IntPtr stmt, int col);

        private const int SQLITE_OPEN_READONLY = 1, SQLITE_ROW = 100;

        /// <summary>
        /// Return up to maxRows from a read-only query, preserving integer, float, text and null values.
        /// Return null if the database cannot be opened or the query fails, including unsupported schemas.
        /// </summary>
        public static List<object[]> Rows(string path, string sql, int maxRows)
        {
            if (string.IsNullOrEmpty(sql) || !File.Exists(path)) return null;
            IntPtr db = IntPtr.Zero, stmt = IntPtr.Zero;
            try
            {
                if (sqlite3_open_v2(Z(path), out db, SQLITE_OPEN_READONLY, IntPtr.Zero) != 0) return null;
                sqlite3_busy_timeout(db, 1000);
                if (sqlite3_prepare_v2(db, Z(sql), -1, out stmt, IntPtr.Zero) != 0) return null;
                var rows = new List<object[]>();
                int cols = sqlite3_column_count(stmt);
                while (rows.Count < maxRows && sqlite3_step(stmt) == SQLITE_ROW)
                {
                    var row = new object[cols];
                    for (int c = 0; c < cols; c++)
                    {
                        switch (sqlite3_column_type(stmt, c))
                        {
                            case 1: row[c] = sqlite3_column_int64(stmt, c); break;
                            case 2: row[c] = sqlite3_column_double(stmt, c); break;
                            case 5: row[c] = null; break;
                            default:
                                int n = sqlite3_column_bytes(stmt, c);
                                IntPtr p = sqlite3_column_blob(stmt, c);
                                if (n <= 0 || p == IntPtr.Zero) { row[c] = ""; break; }
                                var bytes = new byte[n];
                                Marshal.Copy(p, bytes, 0, n);
                                row[c] = Encoding.UTF8.GetString(bytes);
                                break;
                        }
                    }
                    rows.Add(row);
                }
                return rows;
            }
            catch (DllNotFoundException) { return null; }
            catch (EntryPointNotFoundException) { return null; }
            finally
            {
                if (stmt != IntPtr.Zero) sqlite3_finalize(stmt);
                if (db != IntPtr.Zero) sqlite3_close(db);
            }
        }

        /// <summary>Quote a value as a single-quoted SQL string literal, doubling embedded quotes.</summary>
        public static string Quote(string s) { return "'" + (s ?? "").Replace("'", "''") + "'"; }

        public static string ReadValue(string path, string sql)
        {
            if (string.IsNullOrEmpty(sql) || !File.Exists(path)) return null;
            IntPtr db = IntPtr.Zero, stmt = IntPtr.Zero;
            try
            {
                if (sqlite3_open_v2(Z(path), out db, SQLITE_OPEN_READONLY, IntPtr.Zero) != 0) return null;
                sqlite3_busy_timeout(db, 2000);
                if (sqlite3_prepare_v2(db, Z(sql), -1, out stmt, IntPtr.Zero) != 0) return null;
                if (sqlite3_step(stmt) != SQLITE_ROW) return null;
                int n = sqlite3_column_bytes(stmt, 0);
                IntPtr p = sqlite3_column_blob(stmt, 0);
                if (n <= 0 || p == IntPtr.Zero) return null;
                var bytes = new byte[n];
                Marshal.Copy(p, bytes, 0, n);
                // VS Code and its derivatives store text as UTF-8 or, occasionally, UTF-16.
                bool utf16 = n >= 2 && bytes[1] == 0;
                return (utf16 ? Encoding.Unicode : Encoding.UTF8).GetString(bytes);
            }
            catch (DllNotFoundException) { return null; }
            catch (EntryPointNotFoundException) { return null; }
            finally
            {
                if (stmt != IntPtr.Zero) sqlite3_finalize(stmt);
                if (db != IntPtr.Zero) sqlite3_close(db);
            }
        }

        private static byte[] Z(string s) { return Encoding.UTF8.GetBytes(s + "\0"); }
    }
}
