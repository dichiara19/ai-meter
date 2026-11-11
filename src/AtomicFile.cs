using System.IO;

namespace AIMeter
{
    /// <summary>
    /// Swapping a finished temporary file into place, so that a crash never leaves half a file. Inside AI Meter's MSIX
    /// package, File.Replace without a backup file throws ArgumentException (.NET Framework 4.8 on Windows 11, seen on
    /// 2026-11-11), which no save caught: the Store build kept no setting and no lease. With a backup file it works, so
    /// every swap goes through here.
    /// </summary>
    internal static class AtomicFile
    {
        /// <summary>
        /// Put <paramref name="temporary"/> in place of <paramref name="path"/>, or move it there when there is no file
        /// yet. The previous file is kept as <c>path.bak</c> only for the moment of the swap. Throws IOException or
        /// UnauthorizedAccessException when the swap fails, as File.Replace does.
        /// </summary>
        public static void Replace(string temporary, string path)
        {
            if (!File.Exists(path))
            {
                File.Move(temporary, path);
                return;
            }
            string backup = path + ".bak";
            File.Replace(temporary, path, backup, true);
            try { File.Delete(backup); }
            catch (IOException) { }
            catch (System.UnauthorizedAccessException) { }
        }
    }
}
