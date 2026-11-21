"use strict";

const $ = (id) => document.getElementById(id);

const MODE_HINTS = {
    separated:
        "Saves index.html with css/, js/, images/ and fonts/ folders. Easy to edit.",
    single:
        "Embeds CSS, JavaScript, images and fonts into one .html file. Easy to share, but the file can be large."
};

let isDownloading = false;

// ส่งคำสั่งไปยัง C#
function send(action, data = {}) {
    if (!window.chrome?.webview) {
        showStatus("WebView2 connection is unavailable.", "error");
        return;
    }

    window.chrome.webview.postMessage({ action, data });
}

function showStatus(message, type = "") {
    $("status").textContent = message;
    $("status").className = type;
}

function formatBytes(bytes) {
    const value = Number(bytes) || 0;

    if (value < 1024) return value + " B";
    if (value < 1024 * 1024) return (value / 1024).toFixed(1) + " KB";

    return (value / 1024 / 1024).toFixed(1) + " MB";
}

function setProgress(percent, text) {
    const value = Math.max(0, Math.min(100, Number(percent) || 0));

    $("progress").style.width = value + "%";
    $("percent").textContent = Math.round(value) + "%";

    if (text) $("progressText").textContent = text;
}

function setDownloading(value) {
    isDownloading = value;

    $("download").disabled = value;
    $("url").disabled = value;
    $("mode").disabled = value;
    $("browse").disabled = value;
    $("cancel").style.display = value ? "block" : "none";
}

function normalizeUrl(value) {
    let text = value.trim();

    if (!text) return "";

    // ผู้ใช้พิมพ์ example.com โดยไม่ใส่ https:// ก็ใช้ได้
    if (!/^[a-z][a-z0-9+.-]*:/i.test(text)) {
        text = "https://" + text;
    }

    return text;
}

function downloadWebsite() {
    if (isDownloading) return;

    const url = normalizeUrl($("url").value);
    const folder = $("folder").value.trim();

    if (!url || !folder) {
        showStatus("Please enter URL and select folder.", "error");
        return;
    }

    try {
        const parsed = new URL(url);

        if (!["http:", "https:"].includes(parsed.protocol)) {
            throw new Error("Invalid protocol");
        }

        if (!parsed.hostname) {
            throw new Error("Missing host");
        }
    } catch {
        showStatus("Please enter a valid website URL.", "error");
        return;
    }

    $("url").value = url;
    $("result").style.display = "none";

    send("download", {
        url,
        outputFolder: folder,
        mode: $("mode").value
    });
}

function cancelDownload() {
    if (!isDownloading) return;

    send("cancel");
    showStatus("Cancelling download...");
}

function saveFolder(path) {
    try {
        localStorage.setItem("webgrabber.folder", path);
    } catch { /* ไม่เป็นไรถ้าบันทึกไม่ได้ */ }
}

function loadFolder() {
    try {
        return localStorage.getItem("webgrabber.folder") || "";
    } catch {
        return "";
    }
}

function showResult(data) {
    const files = Number(data.files) || 0;
    const failed = Number(data.failed) || 0;

    let text = files + " files · " + formatBytes(data.bytes);

    if (failed > 0) {
        text += " · " + failed + " could not be downloaded " +
            "(links to the live site were kept)";
    }

    $("resultStats").textContent = text;
    $("result").style.display = data.entry ? "block" : "none";
}

// รับข้อความจาก C#
function handleWebMessage(event) {
    const data = event.data;

    if (!data || typeof data.type !== "string") return;

    switch (data.type) {
        case "folder":
            $("folder").value = data.path || "";
            saveFolder(data.path || "");
            break;

        case "started":
            setDownloading(true);
            setProgress(0, "Starting...");
            showStatus(data.message || "Download started.");
            break;

        case "progress":
            setProgress(data.percent, data.message || "Downloading...");
            break;

        case "done":
            setDownloading(false);
            setProgress(100, "Completed");
            showStatus(
                (data.message || "Download completed.") +
                (data.path ? ": " + data.path : ""),
                "success"
            );
            showResult(data);
            break;

        case "cancelled":
            setDownloading(false);
            setProgress(0, "Cancelled");
            showStatus(data.message || "Download cancelled.");
            break;

        case "error":
            setDownloading(false);
            showStatus(data.message || "An unknown error occurred.", "error");
            break;
    }
}

function updateModeHint() {
    $("modeHint").textContent = MODE_HINTS[$("mode").value] || "";
}

function initializeApp() {
    $("download").addEventListener("click", downloadWebsite);
    $("cancel").addEventListener("click", cancelDownload);
    $("browse").addEventListener("click", () => send("browse"));
    $("minimize").addEventListener("click", () => send("minimize"));
    $("close").addEventListener("click", () => send("close"));
    $("openFolder").addEventListener("click", () => send("open", { target: "folder" }));
    $("openFile").addEventListener("click", () => send("open", { target: "file" }));
    $("mode").addEventListener("change", updateModeHint);

    $("url").addEventListener("keydown", (event) => {
        if (event.key === "Enter") downloadWebsite();
    });

    // หน้าต่างไม่มีกรอบ: กดค้างที่แถบด้านบนเพื่อลากหน้าต่าง
    $("titlebar").addEventListener("mousedown", (event) => {
        if (event.button !== 0) return;
        if (event.target.closest(".window-buttons")) return;

        send("drag");
    });

    $("folder").value = loadFolder();
    updateModeHint();

    if (window.chrome?.webview) {
        window.chrome.webview.addEventListener("message", handleWebMessage);
    } else {
        showStatus("This application requires Microsoft WebView2.", "error");
    }
}

document.addEventListener("DOMContentLoaded", initializeApp);
