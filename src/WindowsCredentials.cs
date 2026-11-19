using System;
using System.Runtime.InteropServices;

namespace AIMeter
{
    /// <summary>
    /// Read-only access to the user's generic credentials in Windows Credential Manager, where some CLIs keep their
    /// sign-in instead of a file. AI Meter never writes or deletes an entry here: the application that owns it does.
    /// </summary>
    internal static class WindowsCredentials
    {
        private const int GenericType = 1; // CRED_TYPE_GENERIC

        // A class with a fixed layout rather than a struct: it is only ever filled from Windows' buffer, never compared.
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private sealed class CREDENTIAL
        {
            public uint Flags;
            public uint Type;
            public string TargetName;
            public string Comment;
            public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
            public uint CredentialBlobSize;
            public IntPtr CredentialBlob;
            public uint Persist;
            public uint AttributeCount;
            public IntPtr Attributes;
            public string TargetAlias;
            public string UserName;
        }

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool CredReadW(string target, int type, int flags, out IntPtr credential);
        [DllImport("advapi32.dll")] private static extern void CredFree(IntPtr buffer);

        /// <summary>
        /// The secret stored under a generic credential target, as the bytes its owner wrote (the encoding is the
        /// owner's choice); null when there is no such entry or it cannot be read.
        /// </summary>
        public static byte[] Read(string target)
        {
            IntPtr credential;
            if (!CredReadW(target, GenericType, 0, out credential)) return null;
            try
            {
                var c = (CREDENTIAL)Marshal.PtrToStructure(credential, typeof(CREDENTIAL));
                var secret = new byte[c.CredentialBlobSize];
                if (secret.Length > 0) Marshal.Copy(c.CredentialBlob, secret, 0, secret.Length);
                return secret;
            }
            finally { CredFree(credential); }
        }

        public static bool Exists(string target) { return Read(target) != null; }
    }
}
