"use strict";

const $ = (id) => document.getElementById(id);

const MAX_LOG = 200;
const MAX_HISTORY = 8;

const STORE_FOLDER = "webgrabber.folder";
const STORE_MODE = "webgrabber.mode";
const STORE_HISTORY = "webgrabber.history";

const MODE_HINTS = {
    separated: "บันทึกเป็น index.html พร้อมโฟลเดอร์ css, js, images และ fonts แก้ไขต่อได้ง่าย",
    single: "รวม CSS, JavaScript, รูปและฟอนต์ไว้ในไฟล์ .html ไฟล์เดียว ส่งต่อง่าย แต่ไฟล์จะใหญ่"
};

let isDownloading = false;
let startedAt = 0;
let timer = null;
let currentRequest = null; // งานที่กำลังทำ ใช้บันทึกประวัติ
let lastDone = null;

/* ---------- ตัวช่วย ---------- */

function store(key, value) {
    try {
        localStorage.setItem(key, value);
    } catch { /* บันทึกไม่ได้ก็ไม่เป็นไร */ }
}

function load(key, fallback = "") {
    try {
        return localStorage.getItem(key) ?? fallback;
    } catch {
        return fallback;
    }
}

function send(action, data = {}) {
    if (!window.chrome?.webview) {
        showNote("หน้านี้ต้องเปิดผ่านโปรแกรม WebGrabber (ต้องมี Microsoft WebView2)");
        return false;
    }

    window.chrome.webview.postMessage({ action, data });
    return true;
}

function formatBytes(bytes) {
    const value = Number(bytes) || 0;

    if (value < 1024) return value + " B";
    if (value < 1024 * 1024) return (value / 1024).toFixed(1) + " KB";
    if (value < 1024 ** 3) return (value / 1024 / 1024).toFixed(1) + " MB";

    return (value / 1024 ** 3).toFixed(2) + " GB";
}

function formatDuration(ms) {
    const seconds = Math.max(0, Math.round(ms / 1000));

    if (seconds < 60) return seconds + " วินาที";

    return Math.floor(seconds / 60) + " นาที " + (seconds % 60) + " วินาที";
}

function timeAgo(timestamp) {
    const seconds = Math.round((Date.now() - timestamp) / 1000);

    if (seconds < 60) return "เมื่อสักครู่";
    if (seconds < 3600) return Math.floor(seconds / 60) + " นาทีที่แล้ว";
    if (seconds < 86400) return Math.floor(seconds / 3600) + " ชั่วโมงที่แล้ว";

    return Math.floor(seconds / 86400) + " วันที่แล้ว";
}

function hostOf(url) {
    try {
        return new URL(url).hostname;
    } catch {
        return url;
    }
}

function getMode() {
    return document.querySelector('input[name="mode"]:checked')?.value ||
        "separated";
}

function normalizeUrl(value) {
    let text = value.trim();

    if (!text) return "";

    // พิมพ์ example.com โดยไม่ใส่ https:// ก็ใช้ได้
    if (!/^[a-z][a-z0-9+.-]*:/i.test(text)) {
        text = "https://" + text;
    }

    return text;
}

function svgIcon(path) {
    const svg = document.createElementNS("http://www.w3.org/2000/svg", "svg");

    svg.setAttribute("viewBox", "0 0 24 24");
    svg.setAttribute("fill", "none");
    svg.setAttribute("stroke", "currentColor");
    svg.setAttribute("stroke-width", "1.6");
    svg.setAttribute("stroke-linecap", "round");
    svg.setAttribute("stroke-linejoin", "round");

    const p = document.createElementNS("http://www.w3.org/2000/svg", "path");
    p.setAttribute("d", path);
    svg.appendChild(p);

    return svg;
}

const ICON_FOLDER = "M3 7a2 2 0 0 1 2-2h4l2 2h8a2 2 0 0 1 2 2v8a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2V7z";
const ICON_FILE = "M14 3H7a2 2 0 0 0-2 2v14a2 2 0 0 0 2 2h10a2 2 0 0 0 2-2V8l-5-5zM14 3v5h5";

/* ---------- แสดงผล ---------- */

function setScreen(name) {
    document.body.dataset.screen = name;
}

function showNote(message, info = false) {
    const note = $("note");

    note.textContent = message || "";
    note.style.color = info ? "var(--muted)" : "";
}

function markInvalid(invalid) {
    $("urlWrap").classList.toggle("invalid", invalid);
}

function setProgress(percent) {
    const value = Math.max(0, Math.min(100, Number(percent) || 0));

    $("barFill").style.width = value + "%";
    $("bigPercent").textContent = String(Math.round(value));
}

function startTimer() {
    startedAt = Date.now();
    $("elapsed").textContent = "";

    clearInterval(timer);
    timer = setInterval(() => {
        $("elapsed").textContent =
            "ใช้เวลา " + formatDuration(Date.now() - startedAt);
    }, 500);
}

function stopTimer() {
    clearInterval(timer);
    timer = null;

    return startedAt ? Date.now() - startedAt : 0;
}

function clearLog() {
    $("log").innerHTML = "";
    $("logCount").textContent = "รอรายการแรก";
}

function addLog(message, kind = "") {
    const list = $("log");
    const item = document.createElement("li");

    item.textContent = message;

    if (kind) item.className = kind;

    list.appendChild(item);

    while (list.children.length > MAX_LOG) {
        list.firstElementChild.remove();
    }

    list.scrollTop = list.scrollHeight;
    $("logCount").textContent = list.children.length + " รายการ";
}

function showDone(data) {
    const files = Number(data.files) || 0;
    const failed = Number(data.failed) || 0;
    const total = Math.max(files + failed, 1);
    const took = stopTimer();

    $("statSize").textContent = formatBytes(data.bytes);
    $("statFiles").textContent = files + " ไฟล์";
    $("statFailed").textContent = failed + " ไฟล์";
    $("statFailed").classList.toggle("has-bad", failed > 0);

    $("doneSub").textContent =
        hostOf(currentRequest?.url || "") +
        (took ? " · ใช้เวลา " + formatDuration(took) : "");

    $("donePath").textContent = data.path || "";

    // รีเซ็ตแถบก่อน แล้วค่อยเลื่อนให้เห็นแอนิเมชัน
    $("barOk").style.width = "0";
    $("barFail").style.width = "0";

    setScreen("done");

    requestAnimationFrame(() => requestAnimationFrame(() => {
        $("barOk").style.width = (files / total * 100) + "%";
        $("barFail").style.width = (failed / total * 100) + "%";
    }));
}

function showError(title, message, eyebrow = "ดาวน์โหลดไม่สำเร็จ") {
    stopTimer();

    $("errEyebrow").textContent = eyebrow;
    $("errTitle").textContent = title;
    $("errMsg").textContent = message || "";

    setScreen("error");
}

/* ---------- ประวัติ ---------- */

function readHistory() {
    try {
        const list = JSON.parse(load(STORE_HISTORY, "[]"));

        return Array.isArray(list) ? list : [];
    } catch {
        return [];
    }
}

function pushHistory(entry) {
    const list = readHistory().filter((item) => item.url !== entry.url);

    list.unshift(entry);
    store(STORE_HISTORY, JSON.stringify(list.slice(0, MAX_HISTORY)));

    renderHistory();
}

function renderHistory() {
    const list = $("history");
    const items = readHistory();
    const maxBytes = Math.max(1, ...items.map((item) => Number(item.bytes) || 0));

    list.innerHTML = "";
    $("clearHistory").style.visibility = items.length ? "visible" : "hidden";

    if (items.length === 0) {
        const empty = document.createElement("li");

        empty.className = "history-empty";
        empty.textContent = "ยังไม่มีประวัติการดาวน์โหลด";
        list.appendChild(empty);

        return;
    }

    for (const item of items) {
        const row = document.createElement("li");
        row.title = item.url;

        const top = document.createElement("div");
        top.className = "hist-top";

        const name = document.createElement("span");
        name.className = "hist-name";
        name.textContent = hostOf(item.url);

        const size = document.createElement("span");
        size.className = "hist-size";
        size.textContent = formatBytes(item.bytes);

        top.append(
            svgIcon(item.mode === "single" ? ICON_FILE : ICON_FOLDER),
            name,
            size
        );

        const meta = document.createElement("div");
        meta.className = "hist-meta";
        meta.textContent =
            (item.mode === "single" ? "ไฟล์เดียว" : "แยกไฟล์") + " · " +
            item.files + " ไฟล์ · " + timeAgo(item.time);

        const track = document.createElement("div");
        track.className = "track hist-bar";

        const fill = document.createElement("div");
        fill.className = "track-fill";
        fill.style.width =
            Math.max(3, (Number(item.bytes) || 0) / maxBytes * 100) + "%";

        track.appendChild(fill);
        row.append(top, meta, track);

        row.addEventListener("click", () => {
            $("url").value = item.url;
            markInvalid(false);
            showNote("");
            $("url").focus();
        });

        list.appendChild(row);
    }
}

/* ---------- การทำงานหลัก ---------- */

function downloadWebsite() {
    if (isDownloading) return;

    const url = normalizeUrl($("url").value);
    const folder = $("folder").value.trim();

    let valid = false;

    try {
        const parsed = new URL(url);

        valid = ["http:", "https:"].includes(parsed.protocol) &&
            (parsed.hostname.includes(".") || parsed.hostname === "localhost");
    } catch {
        valid = false;
    }

    markInvalid(!valid);

    if (!valid) {
        showNote("URL ไม่ถูกต้อง ลองตรวจสอบอีกครั้ง เช่น https://example.com");
        $("url").focus();
        return;
    }

    if (!folder) {
        showNote("กรุณาเลือกโฟลเดอร์ที่จะบันทึกไฟล์ก่อน");
        return;
    }

    showNote("");
    $("url").value = url;

    currentRequest = { url, mode: getMode() };

    send("download", {
        url,
        outputFolder: folder,
        mode: currentRequest.mode
    });
}

function cancelDownload() {
    if (!isDownloading) return;

    send("cancel");
    $("progressText").textContent = "กำลังยกเลิก...";
}

async function pasteUrl() {
    try {
        const text = (await navigator.clipboard.readText()).trim();

        if (text) {
            $("url").value = text;
            markInvalid(false);
            showNote("");
        }
    } catch {
        // บางครั้ง WebView2 ไม่อนุญาตให้อ่านคลิปบอร์ด ให้กด Ctrl+V แทน
        $("url").focus();
        showNote("กด Ctrl+V ในช่อง URL เพื่อวางลิงก์", true);
    }
}

function updateModeHint() {
    $("modeHint").textContent = MODE_HINTS[getMode()] || "";
}

function backHome() {
    isDownloading = false;
    setScreen("home");
    $("url").focus();
}

// รับข้อความจาก C#
function handleWebMessage(event) {
    const data = event.data;

    if (!data || typeof data.type !== "string") return;

    switch (data.type) {
        case "folder":
            $("folder").value = data.path || "";
            store(STORE_FOLDER, data.path || "");
            showNote("");
            break;

        case "started":
            isDownloading = true;
            clearLog();
            setProgress(0);
            $("runHost").textContent =
                "กำลังดาวน์โหลด " + hostOf(currentRequest?.url || "");
            $("progressText").textContent =
                data.message || "กำลังเริ่มดาวน์โหลด...";
            startTimer();
            setScreen("running");
            addLog(data.message || "เริ่มดาวน์โหลด");
            break;

        case "progress":
            setProgress(data.percent);

            if (data.message) {
                $("progressText").textContent = data.message;

                addLog(
                    data.message,
                    data.message.includes("ไม่สำเร็จ") ? "bad" : ""
                );
            }
            break;

        case "done":
            isDownloading = false;
            lastDone = data;
            setProgress(100);
            showDone(data);

            if (currentRequest) {
                pushHistory({
                    url: currentRequest.url,
                    mode: currentRequest.mode,
                    files: Number(data.files) || 0,
                    bytes: Number(data.bytes) || 0,
                    time: Date.now()
                });
            }
            break;

        case "cancelled":
            isDownloading = false;
            stopTimer();
            setScreen("home");
            showNote(data.message || "ยกเลิกการดาวน์โหลดแล้ว", true);
            break;

        case "error":
            isDownloading = false;
            showError(
                "ลองใหม่อีกครั้ง",
                data.message || "เกิดข้อผิดพลาดที่ไม่ทราบสาเหตุ"
            );
            break;
    }
}

function initializeApp() {
    $("download").addEventListener("click", downloadWebsite);
    $("cancel").addEventListener("click", cancelDownload);
    $("browse").addEventListener("click", () => send("browse"));
    $("paste").addEventListener("click", pasteUrl);
    $("minimize").addEventListener("click", () => send("minimize"));
    $("close").addEventListener("click", () => send("close"));
    $("openFolder").addEventListener("click", () => send("open", { target: "folder" }));
    $("openFile").addEventListener("click", () => send("open", { target: "file" }));
    $("backDone").addEventListener("click", backHome);
    $("backError").addEventListener("click", backHome);

    $("clearHistory").addEventListener("click", () => {
        store(STORE_HISTORY, "[]");
        renderHistory();
    });

    $("url").addEventListener("input", () => {
        markInvalid(false);
        showNote("");
    });

    $("url").addEventListener("keydown", (event) => {
        if (event.key === "Enter") downloadWebsite();
    });

    document.querySelectorAll('input[name="mode"]').forEach((radio) => {
        radio.addEventListener("change", () => {
            store(STORE_MODE, getMode());
            updateModeHint();
        });
    });

    // หน้าต่างไม่มีกรอบ: กดค้างที่แถบด้านบนเพื่อลากหน้าต่าง
    $("titlebar").addEventListener("mousedown", (event) => {
        if (event.button !== 0) return;
        if (event.target.closest(".window-buttons")) return;

        send("drag");
    });

    $("folder").value = load(STORE_FOLDER);

    const savedMode = load(STORE_MODE, "separated");
    const radio = document.querySelector(
        'input[name="mode"][value="' + savedMode + '"]');

    if (radio) radio.checked = true;

    updateModeHint();
    setProgress(0);
    renderHistory();
    setScreen("home");

    if (window.chrome?.webview) {
        window.chrome.webview.addEventListener("message", handleWebMessage);
    } else {
        showNote("หน้านี้ต้องเปิดผ่านโปรแกรม WebGrabber (ต้องมี Microsoft WebView2)");
    }
}

document.addEventListener("DOMContentLoaded", initializeApp);
