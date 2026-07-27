using System;
using System.Runtime.InteropServices;

namespace IconSetter.Services
{
    /// <summary>
    /// Talks to Explorer's shell change-notification system.
    ///
    /// Two notifications are used together, because they cover two different cases:
    ///
    /// 1. <see cref="NotifyItemUpdated"/> fires SHCNE_UPDATEITEM for one specific folder, right
    ///    after that folder's desktop.ini/attributes are written. This is what makes a folder
    ///    Explorer has never shown a custom icon for before redraw immediately - a global
    ///    ASSOCCHANGED alone doesn't reliably do that, because it tells Explorer "the icon
    ///    association table changed", not "go re-fetch the icon for this exact item". Critically,
    ///    this call must also include SHCNF_FLUSHNOWAIT - without a flush flag, SHChangeNotify
    ///    queues the event internally and Explorer picks it up on its own schedule, which shows
    ///    up as an unpredictable delay rather than an instant update (easy to miss in casual
    ///    testing, since the queued delay is sometimes short enough to not be noticed).
    ///
    /// 2. <see cref="ForceGlobalRefresh"/> fires SHCNE_ASSOCCHANGED once after the whole batch.
    ///    A folder that already had a *different* custom icon can keep an old cached icon handle
    ///    that a single-item notification doesn't evict; the heavier global flush is what clears
    ///    that. It's more expensive, which is why it's still only called once per batch rather
    ///    than per folder - the manual "Force Explorer refresh" button uses the same call for the
    ///    rare case where even that isn't enough.
    /// </summary>
    public static class ShellNotify
    {
        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern void SHChangeNotify(uint wEventId, uint uFlags, IntPtr dwItem1, IntPtr dwItem2);

        private const uint SHCNE_ASSOCCHANGED = 0x08000000; // re-read all associations/icons
        private const uint SHCNE_UPDATEITEM = 0x00002000;   // one specific item's attributes/icon changed

        private const uint SHCNF_IDLIST = 0x0000;
        private const uint SHCNF_PATHW = 0x0005; // dwItem1 is a pointer to a null-terminated wide-char path
        private const uint SHCNF_FLUSHNOWAIT = 0x3000;

        /// <summary>
        /// Tells Explorer that one specific folder's icon/attributes changed, so any open window
        /// showing it redraws right away. Call this immediately after writing that folder's
        /// desktop.ini and setting its attributes.
        /// </summary>
        public static void NotifyItemUpdated(string folderPath)
        {
            IntPtr pPath = Marshal.StringToHGlobalUni(folderPath);
            try
            {
                // SHCNF_FLUSHNOWAIT forces this out immediately instead of letting it sit in the
                // shell's internal notification queue - see the class comment for why this matters.
                SHChangeNotify(SHCNE_UPDATEITEM, SHCNF_PATHW | SHCNF_FLUSHNOWAIT, pPath, IntPtr.Zero);
            }
            finally
            {
                Marshal.FreeHGlobal(pPath);
            }
        }

        /// <summary>
        /// Refreshes Explorer's icon/association state globally. Called once after a whole Apply
        /// batch finishes (not per-folder) - see the class comment for why.
        /// </summary>
        public static void ForceGlobalRefresh()
        {
            SHChangeNotify(SHCNE_ASSOCCHANGED, SHCNF_IDLIST | SHCNF_FLUSHNOWAIT, IntPtr.Zero, IntPtr.Zero);
        }
    }
}
