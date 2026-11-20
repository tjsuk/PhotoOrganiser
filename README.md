# Photo Organiser v1.0.0

A Windows desktop app built with C# and WPF for .NET 10. It scans a source folder, optionally including its subfolders, then copies or moves photos into `Destination\YYYY\MM` after you review the proposed changes.

## Install on Windows

You need a Windows x64 PC and the **.NET 10 Desktop Runtime (x64)**. If the runtime is already installed, go straight to step 2.

1. Open [Microsoft's .NET 10 download page](https://dotnet.microsoft.com/download/dotnet/10.0). Under **.NET Desktop Runtime 10**, choose the **Windows x64 installer**, open it and follow the prompts.
2. Download `PhotoOrganiser-Windows-x64.zip`. In File Explorer, right-click the ZIP, choose **Extract All**, and select a folder you can write to, such as a folder under Documents.
3. Open the extracted folder and double-click `PhotoOrganiser.exe`. Keep the files in that folder together. You can create a shortcut to the executable for future use.
4. If Windows reports a missing .NET runtime, install the **Desktop Runtime** from step 1 and reopen the app.

## Organise your photos

1. In **Source**, use **Browse** to select the folder that holds your originals. Leave **Include source subfolders** checked to scan the entire source folder tree. Clear it to scan only the photos directly inside the source folder.
2. In **Destination**, browse to a separate folder or type a new path. For example, choose `C:\Photos\Camera imports` as the source and `C:\Photos\Organised` as the destination. Keep the two folders outside each other's folder trees.
3. Select **Preview**. This checks the files and shows the proposed actions without writing files. While the app is working, the progress bar shows the current phase and the file or folder being read. Hover over the path to see it in full. The bar animates while the app discovers or checks files, then shows how many entries it has processed. Click a column header to sort the table. Check **Date used** and **Date source** to see how each month was chosen. Scroll right for **Destination** and **Details** if needed.
4. Review the **Action** column.

   | Action | What it means |
   | --- | --- |
   | **Copy** | The app plans to copy this photo to the displayed path. A numbered suffix means that the original filename is already taken. |
   | **Duplicate** | An identical file is already in the destination or is scheduled earlier in this preview. |
   | **Skipped** | The app excluded the entry, typically because it is a link or a cloud placeholder. |
   | **Error** | The app could not read or plan the entry. See **Details** for the cause. |

5. Select the blue **Copy photos** button. A photo dated 28 July 2022 goes under `C:\Photos\Organised\2022\07` in the example above. The app creates the necessary folders and leaves the originals in place. The button stays disabled if the preview has no photos to copy.
6. Read the copied, skipped and error totals when the run finishes. Select **Show run log** to find the log in File Explorer. You can open the `.jsonl` file in Notepad and search for a filename to see its result.

Select **About** in the top-right corner to see the app's purpose, version, required .NET Desktop Runtime, and the .NET version currently running it.

### Move photos

After reviewing the preview, select the red **Move photos** button. Choose **Yes** in the confirmation dialog to continue. **No** returns to the preview. The app verifies each published destination file before permanently removing its original. Removed originals do not go to the Recycle Bin. It leaves duplicates and any original it could not safely transfer in place. Review **Show run log** for any incomplete moves. A move can be interrupted after a destination copy is published, leaving both files in place.

### Cancel or run again

Select **Cancel** during preview or copying and wait for the operation to stop. Copies already completed stay in the destination. To resume, select **Preview** again so the app can identify completed transfers, then select **Copy photos** or **Move photos**. If you change either folder or the **Include source subfolders** setting, run a fresh preview before copying.

## Build and run from source

Install the **.NET 10 SDK** on Windows. From the extracted source folder, run:

```powershell
dotnet build .\App\PhotoOrganiser.csproj -c Release
dotnet run --project .\App\PhotoOrganiser.csproj -c Release
dotnet run --project .\Tests\Tests.csproj -c Release
```

The tests create and remove a dedicated scratch folder under your Windows temporary directory. No third-party packages are needed. To publish a framework-dependent Windows x64 build:

```powershell
dotnet publish .\App\PhotoOrganiser.csproj -c Release -r win-x64 --self-contained false -o .\publish
```

## Code structure

- **`App/Models/`** — `Organiser`, `PhotoDates`, and `MoveSource` handle scanning, metadata, duplicate checks, file transfers, and logging.
- **`App/Views/`** — `MainWindow.xaml` and its code-behind collect input, display the preview and progress, and show confirmation dialogs.
- **`App/Controllers/`** — `PhotoController` owns the reviewed preview, requires confirmation for moves, and dispatches background work.

`App.xaml` remains at the project root and starts `Views/MainWindow.xaml`. `App/Assets/` contains the application icon and header image.

## How it handles your photos

- **Dates** — The app tries original EXIF and XMP capture dates first, then Windows Date Taken and other supported creation or digitization fields. It uses the recorded calendar month where a timestamp includes an offset. If metadata is missing or unreadable, the app uses the file's local modification time and falls back to creation time if the modification date is unusable. The preview identifies the field used.
- **Copies and collisions** — The source stays in place. The app writes each copy to a temporary file, checks the SHA-256 against the preview, and publishes without overwriting. It adds suffixes such as ` (2)` for name collisions. A changed or unreadable source is reported in the log. The app preserves the bytes and last-modified time. Windows permissions and alternate data streams are outside the copy scope.
- **Duplicates** — The app compares complete file contents using SHA-256 against earlier photos in the preview and supported photo files already anywhere under the destination. The source subfolder setting does not change the destination duplicate check. An edited, recompressed, or metadata-modified copy can have a different hash. The app does not relocate identical photos already in the destination. After changes to your folders, preview again to see the latest results.
- **Cancellation and logs** — Completed copies remain in place. The app discards in-progress temporary copies on normal cancellation. An abrupt shutdown may leave a `.tmp` file. Windows I/O and metadata calls can delay cancellation. Each transfer run gets a JSON Lines log in `%LOCALAPPDATA%\PhotoOrganiser\Logs` containing file paths and outcomes. Preview reports issues directly in the table.
- **Formats** — Scans JPEG, PNG, TIFF, HEIC/HEIF, WebP, AVIF, BMP, GIF, and selected RAW extensions. Metadata reading depends on Windows codecs and the tags they expose. Photos without readable metadata can still be copied using filesystem dates. The app skips linked files and directories and some cloud placeholders. It does not parse sidecar files or validate image contents before copying. On large libraries, each scan reads every recognised photo in both folder trees to compute hashes.

The preview is a sortable file list. Use the blue **Copy photos** button to keep all originals. Avoid changing the folder structure while a copy is running.


