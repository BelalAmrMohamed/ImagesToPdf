# Manual QA checklist (Windows / .NET 8)

## Import and queue

- [ ] Add one PNG, JPEG, WebP and BMP.
- [ ] Add a folder containing images and non-image files; confirm only supported image types are queued.
- [ ] Confirm folder import is top-level only by default.
- [ ] Enable recursive import and confirm nested images are included.
- [ ] Include a protected/inaccessible nested folder; confirm import completes and reports the access problem.
- [ ] Import the same file/folder twice; confirm there are no duplicates.
- [ ] Add one corrupted image; confirm it is skipped and its filename/error is reported.
- [ ] Drag a selected group of non-adjacent images; confirm order and relative group order are correct.
- [ ] Move multiple selected images up/down and remove them with Delete.
- [ ] Sort A–Z and verify selected items remain selected.

## Page sizing and preview

- [ ] Add images with identical aspect ratios and different resolutions. In Original mode, verify every page reports the same pixel dimensions and ratio.
- [ ] Test Highest, Median, Average and Lowest resolution strategies.
- [ ] Add images with different aspect ratios. Confirm the mixed-ratio warning appears.
- [ ] Preview Fit on a portrait and landscape image; verify no content is cropped and the page background is visible.
- [ ] Override an individual page to Crop; verify the preview shows a centered crop and export matches it.
- [ ] Set a different override on a second page and verify the first page's setting is retained.
- [ ] Select A4 and Letter in both orientations; verify exported PDF page dimensions in a PDF reader.
- [ ] Test 150, 200 and 300 DPI in standard-page mode.
- [ ] Test White, Red PDF theme and Light gray backgrounds, including transparent PNGs.

## Image decoding

- [ ] Test a JPEG phone photo with EXIF orientation 6 and one with orientation 8; verify thumbnail, preview and PDF are correctly oriented.
- [ ] Test EXIF mirror orientations 2, 4, 5 and 7 if available.
- [ ] Test an animated GIF with multiple frames in first-frame-only and all-frames modes.
- [ ] Test a multi-page TIFF in first-page-only and all-pages modes.
- [ ] Test a TIFF whose pages have different pixel sizes.

## Export safety and quality

- [ ] Export a mixed collection and verify the PDF page count.
- [ ] Compare Best detail (PNG) with Smaller file (JPEG) for text-heavy scans and photographic images.
- [ ] Export over an existing PDF, then cancel or force a decode failure before completion; confirm the existing PDF remains unchanged.
- [ ] Cancel an export and confirm no temporary PDF remains in the destination folder.
- [ ] Export a large image and check that allocation errors are reported clearly rather than crashing the app.
- [ ] Confirm Explorer selection is best-effort and does not change the reported export outcome.

## App preferences and shortcuts

- [ ] Confirm Follow Windows uses the current Windows app theme.
- [ ] Switch to Light and Dark and restart; confirm the choice persists.
- [ ] Confirm page size, DPI, quality, background, recursive import and multi-frame options persist.
- [ ] Test Ctrl+O, Ctrl+Shift+O, Ctrl+A, Delete, Ctrl+Up/Down, Ctrl+Enter, and Esc during export.


## Themes, native controls and branding

- [ ] Set App appearance to Dark. Confirm the window background is near-black rather than slate.
- [ ] Open every ComboBox. Confirm both the closed control and popup list use dark backgrounds and readable text.
- [ ] Scroll the image queue and settings pane; confirm scrollbars are dark and visible.
- [ ] Confirm both folder/frame CheckBoxes have themed boxes and visible check marks.
- [ ] Confirm the status ProgressBar track and fill match the selected theme.
- [ ] Confirm the Windows title bar changes to dark in Dark mode and light in Light mode on supported Windows versions.
- [ ] Switch between Dark and Light while the app remains open; confirm all controls update without restarting.
- [ ] Confirm the branded mark appears in the header, title bar, and taskbar/executable icon.
- [ ] Select Red PDF theme with a same-aspect-ratio image. Confirm the preview and exported PDF both show a red frame around the white image area.
- [ ] Switch between White, Red PDF theme, and Light gray. Confirm the preview changes immediately and exported pages match it.
