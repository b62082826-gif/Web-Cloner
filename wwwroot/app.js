"use strict";

const $ = (id) => document.getElementById(id);

const MAX_LOG = 200;
const MAX_HISTORY = 6;

const STORE_FOLDER = "webgrabber.folder";
const STORE_MODE = "webgrabber.mode";
const STORE_HISTORY = "webgrabber.history";

const MODE_HINTS = {
    separated: "Saves index.html plus css, js, images and fonts folders. Easy to edit.",
    single: "Packs everything into one .html file. Easy to share, but larger."
};

let isDownloading = false;
let startedAt = 0;
let timer = null;
let currentRequest = null; // the job in progress, used for the history entry

/* ---------- helpers ---------- */

function store(key, value) {
    try {
        localStorage.setItem(key, value);
    } catch { /* fine if it can't be saved */ }
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
        showNote("Open this page through the WebGrabber app (Microsoft WebView2 is required).");
        return false;
    }

    window.chrome.webview.postMessage({ action, data });
    return true;
}

function plural(count, word) {
    return count + " " + word + (count === 1 ? "" : "s");
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

    if (seconds < 60) return seconds + " s";

    return Math.floor(seconds / 60) + " min " + (seconds % 60) + " s";
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

    // typing example.com without https:// works too
    if (!/^[a-z][a-z0-9+.-]*:/i.test(text)) {
        text = "https://" + text;
    }

    return text;
}

function isValidUrl(value) {
    try {
        const parsed = new URL(normalizeUrl(value));

        return ["http:", "https:"].includes(parsed.protocol) &&
            (parsed.hostname.includes(".") || parsed.hostname === "localhost");
    } catch {
        return false;
    }
}

/* ---------- display ---------- */

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

// the thick bar under the address fills once the link looks valid
function updateUrlFill() {
    const text = $("url").value.trim();

    $("urlFill").style.width =
        text && (isValidUrl(text) || $("urlWrap").classList.contains("invalid"))
            ? "100%"
            : "0";
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
            "Elapsed " + formatDuration(Date.now() - startedAt);
    }, 500);
}

function stopTimer() {
    clearInterval(timer);
    timer = null;

    return startedAt ? Date.now() - startedAt : 0;
}

function clearLog() {
    $("log").innerHTML = "";
    $("logCount").textContent = "Waiting for the first file";
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
    $("logCount").textContent = plural(list.children.length, "item");
}

function showDone(data) {
    const files = Number(data.files) || 0;
    const failed = Number(data.failed) || 0;
    const total = Math.max(files + failed, 1);
    const took = stopTimer();

    $("statSize").textContent = formatBytes(data.bytes);
    $("statFiles").textContent = plural(files, "file");
    $("statFailed").textContent = plural(failed, "file");
    $("statFailed").classList.toggle("has-bad", failed > 0);

    $("doneSub").textContent =
        hostOf(currentRequest?.url || "") +
        (took ? " · took " + formatDuration(took) : "");

    $("donePath").textContent = data.path || "";

    // reset the bars first so the fill animates
    $("barOk").style.width = "0";
    $("barFail").style.width = "0";

    setScreen("done");

    requestAnimationFrame(() => requestAnimationFrame(() => {
        $("barOk").style.width = (files / total * 100) + "%";
        $("barFail").style.width = (failed / total * 100) + "%";
    }));
}

function showError(title, message, eyebrow = "Download failed") {
    stopTimer();

    $("errEyebrow").textContent = eyebrow;
    $("errTitle").textContent = title;
    $("errMsg").textContent = message || "";

    setScreen("error");
}

/* ---------- recent downloads ---------- */

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

    list.innerHTML = "";
    $("recent").style.display = items.length ? "flex" : "none";

    for (const item of items) {
        const chip = document.createElement("button");

        chip.type = "button";
        chip.className = "recent-chip";
        chip.textContent = hostOf(item.url);
        chip.title = item.url + " · " + plural(item.files, "file") +
            " · " + formatBytes(item.bytes);

        chip.addEventListener("click", () => {
            $("url").value = item.url;
            markInvalid(false);
            updateUrlFill();
            showNote("");
            $("url").focus();
        });

        list.appendChild(chip);
    }
}

/* ---------- main actions ---------- */

function downloadWebsite() {
    if (isDownloading) return;

    const url = normalizeUrl($("url").value);
    const folder = $("folder").value.trim();
    const valid = isValidUrl(url);

    markInvalid(!valid);

    if (!valid) {
        updateUrlFill();
        showNote("That doesn't look like a valid link. Try https://example.com");
        $("url").focus();
        return;
    }

    if (!folder) {
        showNote("Choose a folder to save into first.");
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
    $("progressText").textContent = "Cancelling...";
}

async function pasteUrl() {
    try {
        const text = (await navigator.clipboard.readText()).trim();

        if (text) {
            $("url").value = text;
            markInvalid(false);
            updateUrlFill();
            showNote("");
        }
    } catch {
        // WebView2 sometimes blocks clipboard reads: fall back to Ctrl+V
        $("url").focus();
        showNote("Press Ctrl+V in the box to paste the link.", true);
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

// messages from C#
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
                "Downloading " + hostOf(currentRequest?.url || "");
            $("progressText").textContent = data.message || "Starting...";
            startTimer();
            setScreen("running");
            addLog(data.message || "Starting...");
            break;

        case "progress":
            setProgress(data.percent);

            if (data.message) {
                $("progressText").textContent = data.message;

                addLog(
                    data.message,
                    data.message.startsWith("Failed") ? "bad" : ""
                );
            }
            break;

        case "done":
            isDownloading = false;
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
            showNote(data.message || "Download cancelled.", true);
            break;

        case "error":
            isDownloading = false;
            showError("Please try again", data.message || "Something went wrong.");
            break;
    }
}

function initializeApp() {
    $("download").addEventListener("click", downloadWebsite);
    $("cancel").addEventListener("click", cancelDownload);
    $("browse").addEventListener("click", () => send("browse"));
    $("paste").addEventListener("click", pasteUrl);
    $("minimize").addEventListener("click", () => send("minimize"));
    $("maximize").addEventListener("click", () => send("maximize"));
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
        updateUrlFill();
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

    // frameless window: hold the top bar to drag
    $("titlebar").addEventListener("mousedown", (event) => {
        if (event.button !== 0) return;
        if (event.target.closest(".window-buttons")) return;

        send("drag");
    });

    $("titlebar").addEventListener("dblclick", (event) => {
        if (event.target.closest(".window-buttons")) return;

        send("maximize");
    });

    $("folder").value = load(STORE_FOLDER);

    const savedMode = load(STORE_MODE, "separated");
    const radio = document.querySelector(
        'input[name="mode"][value="' + savedMode + '"]');

    if (radio) radio.checked = true;

    updateModeHint();
    updateUrlFill();
    setProgress(0);
    renderHistory();
    setScreen("home");

    if (window.chrome?.webview) {
        window.chrome.webview.addEventListener("message", handleWebMessage);
    } else {
        showNote("Open this page through the WebGrabber app (Microsoft WebView2 is required).");
    }
}

document.addEventListener("DOMContentLoaded", initializeApp);
