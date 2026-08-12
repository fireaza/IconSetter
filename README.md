# Icon Setter

Give every folder in a tree a unique icon, generated from an image file that lives inside it —
and see it appear immediately, with no Explorer restart or reboot.

## What's new vs. your original version

**1. The refresh finally works reliably.**
The old code called `SHChangeNotify(SHCNE_ASSOCCHANGED, SHCNF_IDLIST, 0, 0)` once, at the very
end of the whole batch. That's a "flush the *entire system's* icon/association cache" event —
slow, causes taskbar/desktop flicker, and isn't actually the notification Explorer listens for
when *one specific folder's* icon changes, so it sometimes just didn't take effect in already-open
windows.

The fix: right after each folder's `desktop.ini` and attributes are written, the app now fires
`SHChangeNotify(SHCNE_UPDATEITEM, SHCNF_PATHW, <that folder's path>, 0)` — the targeted "this item
changed" event. That's what makes Explorer redraw the icon right away, including in windows
that are already open, without restarting anything. See `Services/ShellNotify.cs` for the details
and comments. The old global flush is still there as a manual "⟳ Force Explorer refresh" button
for the rare stubborn case, but it's no longer needed by default.

**2. Remove/revert mode.**
A mode switch at the top now lets you either *set* icons (as before) or *remove* custom icons and
revert affected folders back to Explorer's default icon — it finds every `desktop.ini` your tool
created (or any that specifies `IconResource=`), strips it, and clears the folder attributes.

**3. Multi-icon folders are no longer a dead dropdown.**
"Keep current / first found / random / most recently modified" now actually does something —
previously it was wired up in the XAML but never read by the code.

**4. Existing single-size `.ico` files can be enriched.**
If a folder's `.ico` only has a 256px frame, checking "Add missing sizes" rebuilds it with the
full 256/128/64/48/32/16 set (so it stays sharp when Explorer shows folders at a smaller size),
optionally keeping a `.bak` of the original.

**5. Safer deletes.**
Source images and old icons are now sent to the Recycle Bin (`Services/RecycleBinHelper.cs`)
instead of being permanently deleted.

**6. Drag & drop, remembered settings, CSV export.**
Drag a folder onto the window instead of only using Browse. Your last folder and checkbox choices
are remembered between runs (`IconSetter.settings.json`, saved next to the exe so it stays fully
portable). The results screen has an "Export log…" button for a CSV audit trail.

**7. A visual refresh.**
New color palette, card-based layout, and the old broken image references (the XAML pointed at
`Assets/folder_yellow.png` etc. that didn't actually exist in the project, so those icons were
silently blank) were removed in favor of a cleaner, dependency-free layout.

## Building a standalone .exe

You need the **.NET 8 SDK** (not just the runtime) on the machine you build with:
https://dotnet.microsoft.com/download/dotnet/8.0

Then either:
- double-click **`build.bat`**, or
- run manually:
  ```
  dotnet publish -c Release
  ```

The output is a **single file**, `bin\Release\net8.0-windows\win-x64\publish\IconSetter.exe`.
Copy just that one file anywhere — it bundles the .NET runtime, so it runs on a bare Windows
10/11 x64 machine with nothing else installed. (It's a self-contained single-file publish; the
`.csproj` already has `SelfContained`, `PublishSingleFile`, and `RuntimeIdentifier=win-x64` set,
so no extra flags are required.)

## How it works, in short

- Point it at a folder. It scans that folder (and, optionally, every subfolder) for image files
  named `icon*.png/.jpg/.bmp` and existing `icon*.ico` files.
- Non-.ico images get converted into proper multi-resolution `.ico` files.
  A folder with several `icon*.ico` candidates can be paged through, shuffled, or auto-selected.
- **Apply** writes a hidden `desktop.ini` with `IconResource=<file>,0` into each folder, marks the
  folder with the (Explorer-specific) "customized" attribute, and immediately notifies Explorer
  about that exact folder so the new icon shows up right away.
- **Remove** mode does the reverse: deletes the `desktop.ini`, clears the attribute, and notifies
  Explorer again.

## Known limits

- Windows-only (uses WPF, WinForms' folder browser, and Shell32 APIs) — this can't be built or run
  on macOS/Linux.
- Network drives and some cloud-synced folders (OneDrive, etc.) can be slower to reflect changes,
  since Explorer's remote-file caching behaves a bit differently there.
