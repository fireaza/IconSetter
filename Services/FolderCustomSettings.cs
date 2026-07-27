using System;
using System.Runtime.InteropServices;

namespace IconSetter.Services
{
    /// <summary>
    /// Wraps SHGetSetFolderCustomSettings - the official Windows shell API for registering a
    /// folder's custom icon - instead of only hand-writing desktop.ini and hoping SHChangeNotify
    /// picks it up.
    ///
    /// This exists because the hand-written-desktop.ini + manual SHChangeNotify approach turned
    /// out to be unreliable in real testing: icons could take anywhere from a few seconds to a
    /// couple of minutes to appear after a refresh, or not appear at all until Explorer was
    /// restarted - for both brand-new and re-customized folders. A previous PowerShell version of
    /// this tool called this API instead (for brand-new folders specifically) and got instant
    /// updates on a plain refresh, every time. That's a strong signal this is the code path
    /// Explorer actually expects folder customization to go through, and it evidently handles
    /// whatever internal cache/notification bookkeeping the manual approach was missing.
    ///
    /// desktop.ini is still used for this app's own extra bookkeeping (see ApplyCustomIcon) - this
    /// API call happens first and does the part that actually matters for Explorer noticing.
    /// </summary>
    public static class FolderCustomSettings
    {
        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern int SHGetSetFolderCustomSettings(ref SHFOLDERCUSTOMSETTINGS pfcs, string pszPath, int dwReadWrite);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct SHFOLDERCUSTOMSETTINGS
        {
            public int dwSize;
            public uint dwMask;
            public IntPtr pvid;
            public string? pszWebViewTemplate;
            public uint cchWebViewTemplate;
            public string? pszWebViewTemplateVersion;
            public string? pszInfoTip;
            public uint cchInfoTip;
            public IntPtr pclsid;
            public uint dwFlags;
            public string? pszIconFile;
            public uint cchIconFile;
            public int iIconIndex;
            public string? pszLogo;
            public uint cchLogo;
        }

        private const uint FCSM_ICONFILE = 0x00000010;
        private const int FCS_READ = 0x00000001;
        private const int FCS_FORCEWRITE = 0x00000002;

        /// <summary>
        /// Registers a folder's custom icon through the shell API. <paramref name="iconFileName"/>
        /// should be just the file name (e.g. "icon.ico"), not a full path - matching how
        /// IconResource is normally stored when the icon lives alongside desktop.ini in the same
        /// folder. This call creates or overwrites desktop.ini itself as part of registering the
        /// setting.
        /// </summary>
        public static void SetFolderIcon(string folderPath, string iconFileName)
        {
            var settings = new SHFOLDERCUSTOMSETTINGS
            {
                dwMask = FCSM_ICONFILE,
                pszIconFile = iconFileName,
                cchIconFile = (uint)iconFileName.Length
            };
            settings.dwSize = Marshal.SizeOf(settings);

            SHGetSetFolderCustomSettings(ref settings, folderPath, FCS_READ | FCS_FORCEWRITE);
        }
    }
}
