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

    public MainForm()
    {
        Text = "WebGrabber Pro";
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(900, 620);
        Size = new Size(1120, 760);
        BackColor = Color.FromArgb(12, 17, 28);

        Controls.Add(_web);
        Load += OnLoad;
        FormClosing += OnFormClosing;
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

            var page = Path.Combine(
                AppContext.BaseDirectory,
                "wwwroot",
                "index.html"
            );

            if (!File.Exists(page))
                throw new FileNotFoundException(
                    "ไม่พบไฟล์ wwwroot/index.html"
                );

            _web.CoreWebView2.Navigate(new Uri(page).AbsoluteUri);
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                "ไม่สามารถเปิดโปรแกรมได้\n\n" + ex.Message +
                "\n\nหากยังไม่มี Microsoft Edge WebView2 Runtime " +
                "ให้ติดตั้งจาก https://developer.microsoft.com/microsoft-edge/webview2/",
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

    private void BrowseFolder()
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = "เลือกโฟลเดอร์สำหรับบันทึกเว็บไซต์"
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

    private async Task StartDownloadAsync(DownloadRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.OutputFolder))
            throw new InvalidOperationException(
                "กรุณาเลือกโฟลเดอร์บันทึกไฟล์"
            );

        if (!Uri.TryCreate(request.Url?.Trim(), UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp &&
             uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new InvalidOperationException("URL ไม่ถูกต้อง");
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
                message = "กำลังเริ่มดาวน์โหลด..."
            });

            var result = await _scraper.DownloadAsync(
                uri.AbsoluteUri,
                output,
                request.Mode,
                progress,
                cancel.Token
            );

            _lastEntry = result.EntryFile;

            Post(new
            {
                type = "done",
                message = "ดาวน์โหลดสำเร็จ",
                path = output,
                entry = result.EntryFile,
                files = result.Files,
                failed = result.Failed,
                bytes = result.Bytes
            });
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested)
        {
            TryDeleteFolder(output);

            Post(new
            {
                type = "cancelled",
                message = "ยกเลิกการดาวน์โหลดแล้ว"
            });
        }
        catch (Exception ex)
        {
            TryDeleteFolder(output);

            // HttpClient หมดเวลาจะโยน TaskCanceledException โดยที่ผู้ใช้ไม่ได้กดยกเลิก
            var message = ex is OperationCanceledException
                ? "เซิร์ฟเวอร์ตอบสนองช้าเกินไป (หมดเวลา)"
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
