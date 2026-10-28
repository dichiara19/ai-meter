using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace AIMeter
{
    /// <summary>Small, parameterised SQLite connection. Only the history repository opens it for writing.</summary>
    internal sealed class HistoryDatabase : IDisposable
    {
        private IntPtr handle;
        private static readonly IntPtr Transient = new IntPtr(-1);

        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_open_v2(byte[] path, out IntPtr db, int flags, IntPtr vfs);
        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_close(IntPtr db);
        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_busy_timeout(IntPtr db, int milliseconds);
        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] private static extern IntPtr sqlite3_errmsg(IntPtr db);
        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_prepare_v2(IntPtr db, byte[] sql, int length, out IntPtr statement, IntPtr tail);
        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_bind_null(IntPtr statement, int index);
        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_bind_int64(IntPtr statement, int index, long value);
        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_bind_double(IntPtr statement, int index, double value);
        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_bind_text(IntPtr statement, int index, byte[] value, int length, IntPtr destructor);
        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_step(IntPtr statement);
        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_finalize(IntPtr statement);
        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_column_count(IntPtr statement);
        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_column_type(IntPtr statement, int index);
        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] private static extern long sqlite3_column_int64(IntPtr statement, int index);
        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] private static extern double sqlite3_column_double(IntPtr statement, int index);
        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] private static extern IntPtr sqlite3_column_text(IntPtr statement, int index);
        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_column_bytes(IntPtr statement, int index);

        internal HistoryDatabase(string path)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            int result = sqlite3_open_v2(Utf8(path), out handle, 2 | 4 | 0x10000, IntPtr.Zero);
            if (result != 0) { string error = Error(); Dispose(); throw new IOException(error); }
            sqlite3_busy_timeout(handle, 1500);
        }

        private static byte[] Utf8(string text) { return Encoding.UTF8.GetBytes(text + "\0"); }
        private string Error()
        {
            // SQLite errors contain the SQL operation, never bound values or source documents.
            IntPtr error = sqlite3_errmsg(handle);
            return "History database: " + (error == IntPtr.Zero ? "unavailable" : Marshal.PtrToStringAnsi(error));
        }

        internal void Execute(string sql, params object[] values) { Read(sql, null, values); }
        internal List<object[]> Query(string sql, params object[] values)
        {
            var rows = new List<object[]>();
            Read(sql, row => rows.Add(row), values);
            return rows;
        }

        internal void Read(string sql, Action<object[]> consume, params object[] values)
        {
            IntPtr statement = IntPtr.Zero;
            try
            {
                byte[] command = Utf8(sql);
                if (sqlite3_prepare_v2(handle, command, command.Length, out statement, IntPtr.Zero) != 0) throw new IOException(Error());
                for (int i = 0; i < values.Length; i++)
                {
                    object value = values[i];
                    int result;
                    if (value == null) result = sqlite3_bind_null(statement, i + 1);
                    else if (value is double || value is float || value is decimal) result = sqlite3_bind_double(statement, i + 1, Convert.ToDouble(value));
                    else if (value is long || value is int || value is bool) result = sqlite3_bind_int64(statement, i + 1, Convert.ToInt64(value));
                    else { byte[] text = Encoding.UTF8.GetBytes(Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture)); result = sqlite3_bind_text(statement, i + 1, text, text.Length, Transient); }
                    if (result != 0) throw new IOException(Error());
                }
                int status;
                while ((status = sqlite3_step(statement)) == 100)
                {
                    if (consume == null) continue;
                    var row = new object[sqlite3_column_count(statement)];
                    for (int i = 0; i < row.Length; i++)
                    {
                        int type = sqlite3_column_type(statement, i);
                        if (type == 1) row[i] = sqlite3_column_int64(statement, i);
                        else if (type == 2) row[i] = sqlite3_column_double(statement, i);
                        else if (type == 3)
                        {
                            int count = sqlite3_column_bytes(statement, i);
                            byte[] text = new byte[count];
                            if (count > 0) Marshal.Copy(sqlite3_column_text(statement, i), text, 0, count);
                            row[i] = Encoding.UTF8.GetString(text);
                        }
                    }
                    consume(row);
                }
                if (status != 101) throw new IOException(Error());
            }
            finally { if (statement != IntPtr.Zero) sqlite3_finalize(statement); }
        }

        public void Dispose() { if (handle != IntPtr.Zero) { sqlite3_close(handle); handle = IntPtr.Zero; } }
    }
}
