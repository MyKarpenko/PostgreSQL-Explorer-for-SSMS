using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace SsmsPgExplorer.Connection
{
    /// <summary>
    /// Windows Credential Manager (generic credentials), per-user, DPAPI-protected by Windows.
    /// Same place SSMS keeps "Remember password". Target: SsmsPgExplorer/&lt;registration id&gt;.
    /// </summary>
    internal static class CredentialStore
    {
        private const int CRED_TYPE_GENERIC = 1;
        private const int CRED_PERSIST_LOCAL_MACHINE = 2;
        private const int ERROR_NOT_FOUND = 1168;

        private static string Target(string id) => "SsmsPgExplorer/" + id;

        public static string Read(string id)
        {
            if (!CredRead(Target(id), CRED_TYPE_GENERIC, 0, out var ptr))
            {
                var err = Marshal.GetLastWin32Error();
                if (err == ERROR_NOT_FOUND) return null;
                throw new Win32Exception(err);
            }
            try
            {
                var cred = Marshal.PtrToStructure<CREDENTIAL>(ptr);
                if (cred.CredentialBlob == IntPtr.Zero || cred.CredentialBlobSize == 0) return "";
                return Marshal.PtrToStringUni(cred.CredentialBlob, (int)cred.CredentialBlobSize / 2);
            }
            finally { CredFree(ptr); }
        }

        public static void Write(string id, string userName, string secret)
        {
            var bytes = Encoding.Unicode.GetBytes(secret ?? "");
            var blob = Marshal.AllocHGlobal(bytes.Length);
            try
            {
                Marshal.Copy(bytes, 0, blob, bytes.Length);
                var cred = new CREDENTIAL
                {
                    Type = CRED_TYPE_GENERIC,
                    TargetName = Target(id),
                    UserName = userName ?? "",
                    CredentialBlob = blob,
                    CredentialBlobSize = (uint)bytes.Length,
                    Persist = CRED_PERSIST_LOCAL_MACHINE,
                    Comment = "PostgreSQL Explorer for SSMS",
                };
                if (!CredWrite(ref cred, 0)) throw new Win32Exception(Marshal.GetLastWin32Error());
            }
            finally
            {
                // Zero the unmanaged copy before freeing.
                Marshal.Copy(new byte[bytes.Length], 0, blob, bytes.Length);
                Marshal.FreeHGlobal(blob);
            }
        }

        public static void Delete(string id)
        {
            if (!CredDelete(Target(id), CRED_TYPE_GENERIC, 0))
            {
                var err = Marshal.GetLastWin32Error();
                if (err != ERROR_NOT_FOUND) throw new Win32Exception(err);
            }
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct CREDENTIAL
        {
            public uint Flags;
            public int Type;
            public string TargetName;
            public string Comment;
            public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
            public uint CredentialBlobSize;
            public IntPtr CredentialBlob;
            public int Persist;
            public uint AttributeCount;
            public IntPtr Attributes;
            public string TargetAlias;
            public string UserName;
        }

        [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool CredRead(string target, int type, int flags, out IntPtr credential);

        [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool CredWrite(ref CREDENTIAL credential, int flags);

        [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool CredDelete(string target, int type, int flags);

        [DllImport("advapi32.dll")]
        private static extern void CredFree(IntPtr buffer);
    }
}
