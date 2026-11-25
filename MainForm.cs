using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using WebGrabberPro.Models;
using WebGrabberPro.Services;

namespace WebGrabberPro;

public sealed class MainForm : Form
{
    [DllImport("user32.dll")]
    private static extern bool ReleaseCapture();

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(
        IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(
        IntPtr hwnd, int attribute, ref int value, int size);

    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWCP_ROUND = 2;

    private const int WM_NCLBUTTONDOWN = 0xA1;
    private const int HTCAPTION = 0x2;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly WebView2 _web = new()
    {
        Dock = DockStyle.Fill
    };

    private readonly IWebScraperService _scraper = new WebScraperService();
    private CancellationTokenSource? _cancel;
    private bool _isDownloading;
    private string? _lastEntry;
    private UpdateInfo? _update;
    private bool _updating;
    private bool _updateChecked;

    public MainForm()
    {
        Text = "WebGrabber Pro";
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(900, 620);

        var area = Screen.PrimaryScreen?.WorkingArea
            ?? new Rectangle(0, 0, 1280, 800);

        Size = new Size(
            Math.Min(1160, area.Width - 40),
            Math.Min(800, area.Height - 40));
        BackColor = Color.FromArgb(12, 17, 28);

        try
        {
            // ใช้ไอคอนเดียวกับไฟล์ .exe สำหรับ Taskbar / Alt+Tab
            Icon = Icon.ExtractAssociatedIcon(
                Environment.ProcessPath ?? Application.ExecutablePath);
        }
        catch
        {
            // ไม่มีไอคอนก็ใช้ค่าเริ่มต้น
        }

        Controls.Add(_web);
        Load += OnLoad;
        FormClosing += OnFormClosing;
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);

        try
        {
            // มุมโค้งแบบ Windows 11 (Windows 10 จะไม่มีผล)
            var preference = DWMWCP_ROUND;

            DwmSetWindowAttribute(
                Handle,
                DWMWA_WINDOW_CORNER_PREFERENCE,
                ref preference,
                sizeof(int));
        }
        catch
        {
            // ไม่รองรับก็ไม่เป็นไร
        }
    }

    private async void OnLoad(object? sender, EventArgs e)
    {
        try
        {
            // เก็บข้อมูล WebView2 ไว้ใน LocalAppData ไม่ใช่ข้างไฟล์ .exe
            // (ถ้าติดตั้งในโฟลเดอร์ที่เขียนไม่ได้ เช่น Program Files โปรแกรมจะเปิดไม่ขึ้น)
            var userData = Path.Combine(
                Environment.GetFolderPath(
                    Environment.SpecialFolder.LocalApplicationData),
                "WebGrabberPro",
                "WebView2");

            var environment = await CoreWebView2Environment.CreateAsync(
                null, userData);

            await _web.EnsureCoreWebView2Async(environment);

            var settings = _web.CoreWebView2.Settings;
            settings.AreDefaultContextMenusEnabled = false;
            settings.AreDevToolsEnabled = false;
            settings.IsStatusBarEnabled = false;
            settings.IsZoomControlEnabled = false;

            // UI ต้องอยู่ในไฟล์ภายในเครื่องเท่านั้น ห้ามนำทางหรือเปิดหน้าต่างใหม่ไปเว็บอื่น
            _web.CoreWebView2.NavigationStarting += (_, args) =>
            {
                if (!args.Uri.StartsWith(
                        "file:", StringComparison.OrdinalIgnoreCase))
                {
                    args.Cancel = true;
                }
            };

            _web.CoreWebView2.NewWindowRequested += (_, args) =>
                args.Handled = true;

            _web.CoreWebView2.WebMessageReceived += OnWebMessage;

            _web.CoreWebView2.NavigationCompleted += async (s, a) =>
                await CheckForUpdateAsync();

            var page = Path.Combine(
                AppContext.BaseDirectory,
                "wwwroot",
                "index.html"
            );

            if (!File.Exists(page))
                throw new FileNotFoundException(
                    "Could not find wwwroot/index.html"
                );

            _web.CoreWebView2.Navigate(new Uri(page).AbsoluteUri);
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                "WebGrabber could not start.\n\n" + ex.Message +
                "\n\nIf the Microsoft Edge WebView2 Runtime is not installed, " +
                "install it from https://developer.microsoft.com/microsoft-edge/webview2/",
                "WebGrabber Pro",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error
            );

            Close();
        }
    }

    private async void OnWebMessage(
        object? sender,
        CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            using var json = JsonDocument.Parse(e.WebMessageAsJson);
            var root = json.RootElement;

            if (!root.TryGetProperty("action", out var actionElement))
                return;

            switch (actionElement.GetString())
            {
                case "minimize":
                    WindowState = FormWindowState.Minimized;
                    break;

                case "maximize":
                    if (WindowState == FormWindowState.Maximized)
                    {
                        WindowState = FormWindowState.Normal;
                    }
                    else
                    {
                        // ไม่ให้หน้าต่างแบบไม่มีกรอบทับ Taskbar ตอนขยาย
                        MaximizedBounds = Screen.FromControl(this).WorkingArea;
                        WindowState = FormWindowState.Maximized;
                    }
                    break;

                case "close":
                    Close();
                    break;

                case "drag":
                    // หน้าต่างไม่มีกรอบ จึงต้องส่งสัญญาณลากหน้าต่างเอง
                    ReleaseCapture();
                    SendMessage(
                        Handle, WM_NCLBUTTONDOWN,
                        (IntPtr)HTCAPTION, IntPtr.Zero);
                    break;

                case "browse":
                    BrowseFolder();
                    break;

                case "cancel":
                    _cancel?.Cancel();
                    break;

                case "open":
                    OpenResult(root);
                    break;

                case "openLink":
                    OpenLink(root);
                    break;

                case "openPath":
                    OpenPath(root);
                    break;

                case "update":
                    await ApplyUpdateAsync();
                    break;

                case "download":
                {
                    if (_isDownloading)
                        return;

                    if (!root.TryGetProperty("data", out var data))
                        return;

                    var request = JsonSerializer.Deserialize<DownloadRequest>(
                        data.GetRawText(), JsonOptions);

                    if (request != null)
                        await StartDownloadAsync(request);

                    break;
                }
            }
        }
        catch (Exception ex)
        {
            Post(new
            {
                type = "error",
                message = ex.Message
            });
        }
    }

    private async Task CheckForUpdateAsync()
    {
        Post(new { type = "version", current = UpdateService.CurrentBuild });

        if (_updateChecked)
            return;

        _updateChecked = true;

        var info = await UpdateService.CheckAsync();

        if (info == null)
            return;

        _update = info;

        Post(new
        {
            type = "update",
            build = info.Build,
            size = info.Size,
            notes = info.Notes
        });
    }

    private async Task ApplyUpdateAsync()
    {
        if (_update == null || _updating)
            return;

        if (_isDownloading)
        {
            Post(new
            {
                type = "updateError",
                message = "Finish or cancel the current download first."
            });

            return;
        }

        _updating = true;

        try
        {
            var progress = new Progress<int>(
                p => Post(new { type = "updateProgress", percent = p }));

            await UpdateService.DownloadAndLaunchAsync(
                _update, Environment.ProcessId, progress, CancellationToken.None);

            // the update script waits for this process to exit, then restarts it
            Close();
        }
        catch (Exception ex)
        {
            Post(new { type = "updateError", message = ex.Message });
        }
        finally
        {
            _updating = false;
        }
    }

    private void BrowseFolder()
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = "Choose a folder to save websites into"
        };

        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            Post(new
            {
                type = "folder",
                path = dialog.SelectedPath
            });
        }
    }

    private void OpenResult(JsonElement root)
    {
        // เปิดได้เฉพาะผลลัพธ์ล่าสุดที่โปรแกรมสร้างเอง ไม่รับพาธจากหน้า UI
        if (_lastEntry == null || !File.Exists(_lastEntry))
            return;

        var target = "folder";

        if (root.TryGetProperty("data", out var data) &&
            data.ValueKind == JsonValueKind.Object &&
            data.TryGetProperty("target", out var targetElement))
        {
            target = targetElement.GetString() ?? "folder";
        }

        if (target == "file")
        {
            Process.Start(new ProcessStartInfo(_lastEntry)
            {
                UseShellExecute = true
            });
        }
        else
        {
            Process.Start(new ProcessStartInfo(
                "explorer.exe", "/select,\"" + _lastEntry + "\"")
            {
                UseShellExecute = true
            });
        }
    }

    // เปิดเบราว์เซอร์ได้เฉพาะลิงก์ของโปรเจกต์นี้ ไม่รับ URL อื่นจากหน้า UI
    private static readonly string[] AllowedLinks =
    {
        "https://discord.com/invite/cwxeXehAFX",
        "https://github.com/b62082826-gif/Web-Cloner",
        "https://b62082826-gif.github.io/Web-Cloner"
    };

    private static string? ReadString(JsonElement root, string name)
    {
        if (root.TryGetProperty("data", out var data) &&
            data.ValueKind == JsonValueKind.Object &&
            data.TryGetProperty(name, out var value) &&
            value.ValueKind == JsonValueKind.String)
        {
            return value.GetString();
        }

        return null;
    }

    private static void OpenLink(JsonElement root)
    {
        var url = ReadString(root, "url");

        if (url == null)
            return;

        var allowed = AllowedLinks.Any(prefix =>
            url == prefix ||
            (url.StartsWith(prefix, StringComparison.Ordinal) &&
             url[prefix.Length] is '/' or '?' or '#'));

        if (!allowed)
            return;

        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }

    // เปิดโฟลเดอร์ผลลัพธ์จากประวัติ: ต้องเป็นโฟลเดอร์ที่มี index.html อยู่จริงเท่านั้น
    private void OpenPath(JsonElement root)
    {
        var path = ReadString(root, "path");

        if (string.IsNullOrWhiteSpace(path) ||
            !Path.IsPathRooted(path) ||
            !File.Exists(Path.Combine(path, "index.html")))
        {
            Post(new { type = "pathMissing", path });
            return;
        }

        Process.Start(new ProcessStartInfo("explorer.exe", "\"" + path + "\"")
        {
            UseShellExecute = true
        });
    }

    private async Task StartDownloadAsync(DownloadRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.OutputFolder))
            throw new InvalidOperationException(
                "Please choose a folder to save into."
            );

        if (!Uri.TryCreate(request.Url?.Trim(), UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp &&
             uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new InvalidOperationException("That URL is not valid.");
        }

        var output = Path.Combine(
            request.OutputFolder,
            PathSanitizer.FolderName(uri.Host) + "_" +
            DateTime.Now.ToString("yyyyMMdd_HHmmss")
        );

        var cancel = new CancellationTokenSource();

        _isDownloading = true;
        _cancel = cancel;
        _lastEntry = null;

        var progress = new Progress<(int percent, string message)>(
            p => Post(new
            {
                type = "progress",
                percent = p.percent,
                message = p.message
            })
        );

        try
        {
            Post(new
            {
                type = "started",
                message = "Starting download..."
            });

            var result = await _scraper.DownloadAsync(
                uri.AbsoluteUri,
                output,
                request.Mode,
                request.Depth,
                progress,
                cancel.Token
            );

            _lastEntry = result.EntryFile;

            Post(new
            {
                type = "done",
                message = "Download complete",
                path = output,
                entry = result.EntryFile,
                files = result.Files,
                failed = result.Failed,
                bytes = result.Bytes,
                pages = result.Pages
            });
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested)
        {
            TryDeleteFolder(output);

            Post(new
            {
                type = "cancelled",
                message = "Download cancelled."
            });
        }
        catch (Exception ex)
        {
            TryDeleteFolder(output);

            // HttpClient หมดเวลาจะโยน TaskCanceledException โดยที่ผู้ใช้ไม่ได้กดยกเลิก
            var message = ex is OperationCanceledException
                ? "The server took too long to respond (timed out)."
                : ex.Message;

            Post(new
            {
                type = "error",
                message
            });
        }
        finally
        {
            cancel.Dispose();
            _cancel = null;
            _isDownloading = false;
        }
    }

    private static void TryDeleteFolder(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, true);
        }
        catch
        {
            // ลบโฟลเดอร์ที่ค้างไม่ได้ก็ไม่เป็นไร
        }
    }

    private void OnFormClosing(object? sender, FormClosingEventArgs e)
    {
        _cancel?.Cancel();
    }

    private void Post(object value)
    {
        if (IsDisposed || _web.IsDisposed || _web.CoreWebView2 == null)
            return;

        _web.CoreWebView2.PostWebMessageAsJson(
            JsonSerializer.Serialize(value)
        );
    }
}
