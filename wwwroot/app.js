"use strict";

const $ = (id) => document.getElementById(id);

const MAX_LOG = 200;
const MAX_HISTORY = 6;

const STORE_FOLDER = "webgrabber.folder";
const STORE_MODE = "webgrabber.mode";
const STORE_HISTORY = "webgrabber.history";
const STORE_THEME = "webgrabber.theme";
const STORE_GREETING = "webgrabber.greeting";
const STORE_PARTY = "webgrabber.party";

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

/* ---------- toast with undo, and screen reader announcements ---------- */

let toastTimer = null;
let toastUndo = null;

function announce(message) {
    const live = $("live");

    live.textContent = "";
    setTimeout(() => { live.textContent = message; }, 50);
}

function hideToast() {
    clearTimeout(toastTimer);
    toastTimer = null;
    toastUndo = null;
    $("toast").hidden = true;
}

// stays for 10 s, and while the pointer or keyboard focus is on it
function showToast(message, undo) {
    const box = $("toast");

    $("toastText").textContent = message;
    $("toastAction").hidden = !undo;
    toastUndo = undo || null;
    box.hidden = false;
    announce(message + (undo ? " Undo is available." : ""));

    clearTimeout(toastTimer);
    toastTimer = setTimeout(hideToast, 10000);
}

function holdToast() {
    clearTimeout(toastTimer);
}

function releaseToast() {
    clearTimeout(toastTimer);
    toastTimer = setTimeout(hideToast, 4000);
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
    celebrate();
    announce("Saved " + hostOf(currentRequest?.url || "") + ", " +
        formatBytes(data.bytes) + ", " + plural(files, "file") + ".");
    setTimeout(() => $("openFile").focus(), 60);

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

    $("retry").hidden = !currentRequest;

    setScreen("error");
    announce("Download failed. " + (message || ""));
    setTimeout(() => ($("retry").hidden ? $("backError") : $("retry")).focus(), 60);
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

    currentRequest = { url, mode: getMode(), folder };

    send("download", {
        url,
        outputFolder: folder,
        mode: currentRequest.mode
    });
}

function cancelDownload() {
    if (!isDownloading) return;

    send("cancel");
    $("cancel").disabled = true;
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
        case "version":
            $("ver").textContent = "build " + data.current;
            break;

        case "update":
            showUpdate(data);
            break;

        case "updateProgress":
            setUpdateProgress(Number(data.percent) || 0);
            break;

        case "updateError":
            showUpdateError(data.message);
            break;

        case "folder":
            $("folder").value = data.path || "";
            store(STORE_FOLDER, data.path || "");
            showNote("");
            break;

        case "started":
            isDownloading = true;
            $("cancel").disabled = false;
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
            $("cancel").disabled = false;
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

const THEMES = ["dark", "light", "loy", "party"];
const FESTIVE = ["loy", "party"];
const THEME_NAMES = { dark: "Dark", light: "Light", loy: "Loy Krathong", party: "Party" };

// Loy Krathong 2026 falls on 24 November: open in the festival theme for those days
function isLoyKrathong() {
    const now = new Date();

    return now.getFullYear() === 2026 &&
        now.getMonth() === 10 &&
        now.getDate() >= 24 &&
        now.getDate() <= 26;
}

// confetti on the finish screen, festival theme only
function celebrate() {
    if (!FESTIVE.includes(document.documentElement.dataset.theme)) return;

    const box = $("confetti");
    const colors = ["#fbbf24", "#fde68a", "#f472b6", "#fb923c", "#fff7e6", "#93c5fd", "#34d399", "#a78bfa"];

    box.innerHTML = "";

    for (let i = 0; i < 46; i++) {
        const piece = document.createElement("i");

        piece.style.left = Math.random() * 100 + "%";
        piece.style.background = colors[i % colors.length];
        piece.style.animationDelay = Math.random() * 0.9 + "s";
        piece.style.animationDuration = 2.6 + Math.random() * 1.8 + "s";
        piece.style.setProperty("--drift", (Math.random() * 160 - 80) + "px");
        box.appendChild(piece);
    }

    setTimeout(() => { box.innerHTML = ""; }, 5200);
}

function applyTheme(theme) {
    document.documentElement.dataset.theme = theme;
    $("theme").title = "Theme: " + (THEME_NAMES[theme] || theme) + " (click to change)";

    if (!FESTIVE.includes(theme)) $("festPanel").hidden = true;
}

/* ---------- festive message and color (editable) ---------- */

function getGreeting() {
    return load(STORE_GREETING, "Happy Birthday!");
}

function renderGreeting() {
    const text = getGreeting().trim();

    $("bday").textContent = text || "Add a message";
    $("doneGreet").textContent = text;
}

function applyParty(color) {
    document.documentElement.style.setProperty("--party", color);

    document.querySelectorAll(".sw").forEach((button) => {
        button.classList.toggle("on", button.dataset.color === color);
    });
}

function initFestive() {
    const input = $("greetInput");
    const panel = $("festPanel");

    input.value = getGreeting();
    renderGreeting();
    applyParty(load(STORE_PARTY, "#f472b6"));

    $("bday").addEventListener("click", () => {
        panel.hidden = !panel.hidden;

        if (!panel.hidden) {
            input.focus();
            input.select();
        }
    });

    input.addEventListener("input", () => {
        store(STORE_GREETING, input.value);
        renderGreeting();
    });

    input.addEventListener("keydown", (event) => {
        if (event.key === "Enter" || event.key === "Escape") panel.hidden = true;
    });

    $("festDone").addEventListener("click", () => { panel.hidden = true; });

    document.querySelectorAll(".sw").forEach((button) => {
        button.addEventListener("click", () => {
            store(STORE_PARTY, button.dataset.color);
            applyParty(button.dataset.color);
        });
    });

    document.addEventListener("mousedown", (event) => {
        if (panel.hidden) return;
        if (event.target.closest("#festPanel, #bday")) return;

        panel.hidden = true;
    });
}

/* ---------- auto update ---------- */

let updating = false;

function showUpdate(data) {
    $("updateTitle").textContent = "Update available";
    $("updateSub").textContent =
        "Build " + data.build + " · " + formatBytes(data.size) +
        ". WebGrabber restarts when it is installed.";
    $("updateBar").hidden = true;
    $("updateNow").hidden = false;
    $("updateNow").disabled = false;

    const notes = $("updateNotes");
    const items = String(data.notes || "").split("\n").filter(Boolean);

    notes.innerHTML = "";

    for (const text of items) {
        const li = document.createElement("li");

        li.textContent = text;
        notes.appendChild(li);
    }

    notes.hidden = items.length === 0;
    $("update").hidden = false;
}

function setUpdateProgress(percent) {
    updating = true;
    $("updateTitle").textContent = "Downloading update " + percent + "%";
    $("updateSub").textContent = "Keep this window open.";
    $("updateNotes").hidden = true;
    $("updateBar").hidden = false;
    $("updateFill").style.width = percent + "%";
    $("updateNow").hidden = true;
    $("updateLater").hidden = true;
}

function showUpdateError(message) {
    updating = false;
    $("updateTitle").textContent = "Update failed";
    $("updateSub").textContent = message || "Try again later.";
    $("updateBar").hidden = true;
    $("updateNow").hidden = false;
    $("updateNow").disabled = false;
    $("updateLater").hidden = false;
}

function initializeApp() {
    $("updateNow").addEventListener("click", () => {
        if (updating) return;

        if (isDownloading) {
            $("updateTitle").textContent = "Finish your download first";
            $("updateSub").textContent =
                "Update after the download ends, or cancel it.";
            return;
        }

        $("updateNow").disabled = true;
        setUpdateProgress(0);
        send("update");
    });

    $("updateLater").addEventListener("click", () => {
        $("update").hidden = true;
    });

    initFestive();

    const savedTheme = load(STORE_THEME, "");

    applyTheme(THEMES.includes(savedTheme)
        ? savedTheme
        : (isLoyKrathong() ? "loy" : "dark"));

    $("theme").addEventListener("click", () => {
        const current = document.documentElement.dataset.theme;
        const next = THEMES[(THEMES.indexOf(current) + 1) % THEMES.length];

        applyTheme(next);
        store(STORE_THEME, next);
    });

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
        const before = readHistory();

        if (!before.length) return;

        store(STORE_HISTORY, "[]");
        renderHistory();

        showToast("Cleared " + plural(before.length, "recent site") + ".", () => {
            store(STORE_HISTORY, JSON.stringify(before));
            renderHistory();
            announce("Recent sites restored.");
        });
    });

    $("toastAction").addEventListener("click", () => {
        const undo = toastUndo;

        hideToast();
        if (undo) undo();
    });

    $("toastClose").addEventListener("click", hideToast);

    $("toast").addEventListener("mouseenter", holdToast);
    $("toast").addEventListener("mouseleave", releaseToast);
    $("toast").addEventListener("focusin", holdToast);
    $("toast").addEventListener("focusout", releaseToast);

    $("retry").addEventListener("click", () => {
        if (!currentRequest || isDownloading) return;

        send("download", {
            url: currentRequest.url,
            outputFolder: currentRequest.folder,
            mode: currentRequest.mode
        });
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
        if (event.target.closest(".window-buttons, .bday")) return;

        send("drag");
    });

    $("titlebar").addEventListener("dblclick", (event) => {
        if (event.target.closest(".window-buttons, .bday")) return;

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
