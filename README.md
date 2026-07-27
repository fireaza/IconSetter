# IconSetter

*A portable Windows utility that automatically assigns custom folder icons in bulk.*

![IconSetter Screenshot](images/screenshot.png)

[⬇ Download Latest Release](../../releases/latest)

---

## Features

- Automatically assigns folder icons based on the .ico file in each folder
- Processes subfolders recursively
- Can automatically generate .ico files from image files
- Optionally randomizes icons when multiple are available
- New icons appear right away, no need to restart Explorer
- Fast and lightweight
- Portable, all-in-one EXE
- Open source

---

## Why?

Manually assigning folder icons to hundreds of folders is tedious.

IconSetter automates the process, scanning folder trees and applying unique icons to all your folders in just a few clicks.

---

## Installation

There isn't one!

Simply download `IconSetter.exe` from the latest release and run it.

---

## Building

Requires the .NET 8 SDK.

```bat
build.bat
```

or

```bash
dotnet publish -c Release
```

---

## How to Use

1. Place an .ico file or image file named "icon" (you can add numbers to the end), inside the folders you want to customize.
2. Launch IconSetter.
3. Select the parent folder.
4. Click "Apply".
5. Done!

---

## License

MIT License.
