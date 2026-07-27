using System;
using System.Runtime.InteropServices;

namespace IconSetter.Services
{
    /// <summary>
    /// Deletes files to the Recycle Bin instead of permanently, so "delete source images after
    /// conversion" (and icon cleanup) is undoable if someone picks the wrong option.
    /// </summary>
    public static class RecycleBinHelper
    {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct SHFILEOPSTRUCT
        {
            public IntPtr hwnd;
            public uint wFunc;
            public string pFrom;
            public string? pTo;
            public ushort fFlags;
            public bool fAnyOperationsAborted;
            public IntPtr hNameMappings;
            public string? lpszProgressTitle;
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern int SHFileOperation(ref SHFILEOPSTRUCT FileOp);

        private const uint FO_DELETE = 0x0003;
        private const ushort FOF_ALLOWUNDO = 0x0040;
        private const ushort FOF_NOCONFIRMATION = 0x0010;
        private const ushort FOF_SILENT = 0x0004;
        private const ushort FOF_NOERRORUI = 0x0400;

        /// <summary>
        /// Sends a single file to the Recycle Bin. Returns true on success.
        /// Falls back to nothing - caller decides whether to hard-delete on failure.
        /// </summary>
        public static bool Delete(string path)
        {
            // pFrom must be double-null-terminated.
            var shf = new SHFILEOPSTRUCT
            {
                wFunc = FO_DELETE,
                pFrom = path + '\0' + '\0',
                fFlags = (ushort)(FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_SILENT | FOF_NOERRORUI)
            };

            int result = SHFileOperation(ref shf);
            return result == 0 && !shf.fAnyOperationsAborted;
        }
    }
}
