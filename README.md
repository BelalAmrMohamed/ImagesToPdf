# Images to PDF

A Windows desktop utility for turning image collections into PDFs while normalizing every exported page to one shared size. It is designed especially for phone scans, lecture notes, screenshots, diagrams, and other image-based documents.

## Requirements

- Windows 10/11
- .NET 8 SDK (or the matching .NET 8 Desktop Runtime for a framework-dependent published build)
- Visual Studio 2022 with the **.NET desktop development** workload, or the `dotnet` CLI

## Build and run

From the repository root:

```powershell
dotnet restore ImagesToPdf.sln
dotnet build ImagesToPdf.sln -c Release
dotnet run --project ImagesToPdf/ImagesToPdf.csproj
```

The project targets `net8.0-windows` and uses WPF with SkiaSharp 3.116.1. It is a Windows-only application.

## Main workflow

1. Add one or more supported images, or add a folder. Dragging files and folders onto the queue is also supported.
2. Reorder images in the queue. Multi-select and drag moves the selected group together. `Delete` removes the selected images.
3. Choose the page layout and image-fit behavior. Select an image in the queue to preview its final PDF page.
4. Choose the PDF background, resolution strategy, DPI, and quality mode.
5. Create the PDF. Export is written to a temporary file and only replaces the destination after the complete PDF has been closed and checked.

## Page sizing and normalization

### Original (default)

The first image establishes the output aspect ratio for the entire document. This is intentionally **not** a separate page size for every source image: all PDF pages must share one aspect ratio and one pixel resolution.

The resolution strategy chooses the common pixel size using the source image dimensions:

- **Highest** — use the largest source resolution; smaller images are upscaled.
- **Median** — use the median effective source resolution.
- **Average** — use the average effective source resolution.
- **Lowest** — use the smallest effective source resolution; larger images are downscaled.

For images with the same aspect ratio but different pixel resolutions, the dimensions are normalized without changing the shared ratio. When source aspect ratios differ, the first image still establishes the default shared ratio; use the per-page Fit/Crop selector to control how each page is placed, or choose a standard page size instead.

### Standard page sizes

Available fixed layouts are A4 Portrait/Landscape, Letter Portrait/Landscape, 8×8 inch Square, 12×6.75 inch 16:9, and 10×7.5 inch 4:3. At these sizes, the selected DPI determines the raster pixel dimensions. The source-resolution strategy is disabled because paper size and DPI define the output resolution.

### Per-page Fit/Crop

The global **How images fit** setting is the default for every page:

- **Fit** — preserves the whole image and fills unused areas with the selected PDF page background.
- **Fill/Crop** — scales to cover the page and crops overflow from the center.

The preview includes a **This page** override. Choose **Use global default**, **Fit this page**, or **Crop this page** for the selected image/frame. This override is used by both preview and export.

## Other settings

- **DPI:** 150, 200, or 300. In Original mode it controls the physical PDF page size; in standard-size mode it controls raster output resolution while preserving the selected paper dimensions.
- **PDF theme / page background:** White, Red PDF theme, or Light gray. White and Light gray appear in fitted margins and transparent image areas. Red PDF theme adds a visible red frame around every page with a white content area, so the theme remains visible even when all source images share the page ratio.
- **Quality:** Best detail (lossless PNG) or Smaller file (JPEG at quality 90).
- **GIF/TIFF frames:** First frame/page only by default; enable **Include every GIF/TIFF frame or page** to export every decoder frame/page. The preview lets you inspect each included frame.
- **Folder import:** Top-level only by default. Enable **Include subfolders** for recursive traversal. Reparse-point directories are skipped to avoid junction/symlink loops.
- **Phone-photo orientation:** EXIF orientation is applied before thumbnailing, previewing, and exporting.
- **App appearance:** Follow Windows, Light, or Dark. Dark now uses near-black surfaces rather than slate, and the ComboBoxes, popup lists, checkboxes, scrollbars, progress bar, and native Windows title bar follow the selected theme. Follow Windows is updated when Windows switches its app color preference while the app is open. The preference and PDF settings are saved under `%AppData%\ImagesToPdf\settings.json`.
- **Application branding:** The same custom document mark is used in the app header, title bar, and compiled executable icon. Its editable SVG source and PNG/ICO assets are in `ImagesToPdf/Assets/`.

## Keyboard shortcuts

| Shortcut | Action |
|---|---|
| `Ctrl+O` | Add images |
| `Ctrl+Shift+O` | Add a folder |
| `Ctrl+A` | Select all queued images |
| `Delete` | Remove selected images |
| `Ctrl+↑` / `Ctrl+↓` | Move selected images up/down |
| `Ctrl+Enter` | Create PDF |
| `Esc` during export | Cancel export |

## Supported input formats

PNG, JPG/JPEG, JFIF, WebP, BMP, GIF, ICO, TIF and TIFF, subject to the Windows imaging codecs installed on the system. Unsupported or unreadable files are skipped and reported with their error messages.

## Notes and limitations

- The queue and per-frame Fit/Crop overrides are session data; app display/export preferences are persisted. The image list is not saved between launches.
- Fit/Crop overrides apply to individual PDF frames (including individual frames in animated or multi-page sources), but the PDF background and global page ratio are document-wide.
- Crop uses a centered crop. There is not yet an interactive crop-position editor, image rotation editor, deskew, OCR, or page cleanup.
- PNG lossless mode can create substantially larger PDFs than JPEG mode.
- The project has not been packaged with an installer or self-contained runtime. Build/publish on Windows using the .NET 8 SDK for deployment.
