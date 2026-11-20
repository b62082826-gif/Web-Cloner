# WebGrabber Pro

Windows Desktop (`.exe`) website downloader built with **C# (.NET 8 + Windows Forms + WebView2)** for backend scraping and **HTML / CSS / JavaScript** for a dark-mode UI.

## Features
- **Single HTML Bundle:** Downloads and embeds CSS, JavaScript, and images into a standalone `.html` file.
- **Separated Assets Mode:** Saves structured `index.html`, `css/`, `js/`, and `images/` folders for easy editing.
- **Concurrent Fetching:** Uses `SemaphoreSlim` with configurable timeout and user-agent headers.
- **Custom Frameless Window:** Dark-mode UI with custom titlebar controls and live progress reporting.

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
