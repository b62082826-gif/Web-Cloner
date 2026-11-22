# WebGrabber Pro

Windows Desktop (`.exe`) website downloader built with **C# (.NET 8 + Windows Forms + WebView2)** for backend scraping and **HTML / CSS / JavaScript** for a dark-mode UI.

## Features
- **Single HTML Bundle:** Embeds CSS, JavaScript, images and fonts into one standalone `.html` file (`@import`ed stylesheets are inlined too).
- **Separated Assets Mode:** Saves `index.html` plus `css/`, `js/`, `images/`, `fonts/` and `media/` folders for easy editing. Paths inside CSS files are rewritten correctly, including nested `@import`s.
- **Concurrent Fetching:** Downloads up to 8 files in parallel with a 40 s timeout, browser-like headers, cookies, gzip/brotli support and live progress.
- **Robust rewriting:** Handles `srcset`, lazy-load attributes (`data-src`), inline `style`, `<base href>`, SVG `<use>`/`<image>`, and non-UTF-8 pages (e.g. `windows-874`). Files with no extension (e.g. Google Fonts CSS) are detected from `Content-Type`.
- **Offline-safe:** Removes `integrity` / `crossorigin` attributes that would block the saved files, and keeps the live URL for any file that could not be downloaded. Links to other pages are turned into absolute URLs.
- **Custom Frameless Window:** Dark-mode UI with draggable titlebar, remembered output folder, and "Show in folder" / "Open in browser" after a download.

## Build
Requires the .NET 8 SDK on Windows.

```bash
dotnet publish WebGrabber.Pro.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o publish
```

The app needs the [Microsoft Edge WebView2 Runtime](https://developer.microsoft.com/microsoft-edge/webview2/) (already included in Windows 11). Pushing a tag such as `v1.0.0` builds and attaches a zip to a GitHub Release via `.github/workflows/build-release.yml`.

## Limitations
- Downloads one page (not a whole crawl); links to other pages stay online.
- Content that JavaScript loads after the page starts (single-page apps) is only captured if the files are referenced in the HTML/CSS.
- Files larger than 25 MB are skipped, with at most 400 files per download.
- `type="module"` scripts may not run when a saved page is opened directly from disk (`file://`) because of browser security rules.

## Project Structure
```text
WebGrabber.Pro/
├── .github/workflows/build-release.yml
├── Models/
├── Services/
├── wwwroot/
├── MainForm.cs
├── Program.cs
└── WebGrabber.Pro.csproj
```

## Website

The landing page lives in `docs/index.html`. To publish it with GitHub Pages: Settings > Pages > Deploy from a branch > `main` / `/docs`.
