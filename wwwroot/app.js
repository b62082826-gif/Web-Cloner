
"use strict";

const $ = (id) => document.getElementById(id);

let isDownloading = false;

// ส่งคำสั่งไปยัง C#
function send(action, data = {}) {
    if (!window.chrome?.webview) {
        showStatus("WebView2 connection is unavailable.", "error");
        return;
    }

    window.chrome.webview.postMessage({
        action,
        data
    });
}

// แสดงสถานะ
function showStatus(message, type = "") {
    $("status").textContent = message;
    $("status").className = type;
}

// เริ่มดาวน์โหลดเว็บไซต์
function downloadWebsite() {
    if (isDownloading) return;

    const url = $("url").value.trim();
    const folder = $("folder").value.trim();

    if (!url || !folder) {
        showStatus("Please enter URL and select folder.", "error");
        return;
    }

    try {
        const parsedUrl = new URL(url);

        if (!["http:", "https:"].includes(parsedUrl.protocol)) {
            throw new Error("Invalid protocol");
        }
    } catch {
        showStatus("Please enter a valid website URL.", "error");
        return;
    }

    send("download", {
        url,
        outputFolder: folder,
        mode: $("mode").value
    });
}

// ยกเลิกดาวน์โหลด
function cancelDownload() {
    if (!isDownloading) return;

    send("cancel");
    showStatus("Cancelling download...");
}

// เลือกโฟลเดอร์
function browseFolder() {
    send("browse");
}

// จัดการสถานะปุ่ม
function setDownloading(value) {
    isDownloading = value;

    $("download").disabled = value;
    $("cancel").style.display = value ? "block" : "none";
}

// รีเซ็ตหน้าดาวน์โหลด
function finish() {
    setDownloading(false);
}

// รับข้อความจาก C#
function handleWebMessage(event) {
    const data = event.data;

    if (!data || typeof data.type !== "string") return;

    switch (data.type) {
        case "folder":
            $("folder").value = data.path || "";
            break;

        case "started":
            setDownloading(true);

            $("progress").style.width = "0%";
            $("percent").textContent = "0%";
            $("progressText").textContent = "Starting...";

            showStatus(data.message || "Download started.");
            break;

        case "progress": {
            const percent = Math.max(
                0,
                Math.min(100, Number(data.percent) || 0)
            );

            $("progress").style.width = percent + "%";
            $("percent").textContent = percent + "%";
            $("progressText").textContent = data.message || "Downloading...";

            break;
        }

        case "done":
            finish();

            $("progress").style.width = "100%";
            $("percent").textContent = "100%";
            $("progressText").textContent = "Completed";

            showStatus(
                (data.message || "Download completed.") +
                (data.path ? ": " + data.path : ""),
                "success"
            );
            break;

        case "cancelled":
            finish();
            $("progressText").textContent = "Cancelled";
            showStatus(data.message || "Download cancelled.");
            break;

        case "error":
            finish();
            showStatus(data.message || "An unknown error occurred.", "error");
            break;
    }
}

// เริ่มต้นระบบ
function initializeApp() {
    $("download").addEventListener("click", downloadWebsite);
    $("cancel").addEventListener("click", cancelDownload);

    document.querySelector(".folder-row .secondary")
        .addEventListener("click", browseFolder);

    if (window.chrome?.webview) {
        window.chrome.webview.addEventListener(
            "message",
            handleWebMessage
        );
    } else {
        showStatus(
            "This application requires Microsoft WebView2.",
            "error"
        );
    }
}

document.addEventListener("DOMContentLoaded", initializeApp);

