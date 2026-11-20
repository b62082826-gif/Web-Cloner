
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

    private readonly WebView2 _web = new()
    {
        Dock = DockStyle.Fill
    };

    private readonly WebScraperService _scraper = new();
    private CancellationTokenSource? _cancel;
    private bool _isDownloading;

    public MainForm()
    {
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
            await _web.EnsureCoreWebView2Async();

            var settings = _web.CoreWebView2.Settings;
            settings.AreDefaultContextMenusEnabled = false;
            settings.AreDevToolsEnabled = false;
            settings.IsStatusBarEnabled = false;

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
                "ไม่สามารถเปิดโปรแกรมได้\n\n" + ex.Message,
                "WebGrabber Pro",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error
            );
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

            var action = actionElement.GetString();

            switch (action)
            {
                case "minimize":
                    WindowState = FormWindowState.Minimized;
                    break;

                case "close":
                    Close();
                    break;

                case "browse":
                    BrowseFolder();
                    break;

                case "cancel":
                    _cancel?.Cancel();
                    break;

                case "download":
                    if (_isDownloading)
                        return;

                    if (!root.TryGetProperty("data", out var data))
                        return;

                    var request = JsonSerializer.Deserialize<DownloadRequest>(
                        data.GetRawText(),
                        new JsonSerializerOptions
                        {
                            PropertyNameCaseInsensitive = true
                        }
                    );

                    if (request != null)
                        await StartDownloadAsync(request);

                    break;
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

    private async Task StartDownloadAsync(DownloadRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.OutputFolder))
            throw new InvalidOperationException(
                "กรุณาเลือกโฟลเดอร์บันทึกไฟล์"
            );

        if (!Uri.TryCreate(request.Url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp &&
             uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new InvalidOperationException("URL ไม่ถูกต้อง");
        }

        var hostFolder = string.Concat(
            uri.Host.Select(c =>
                Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)
        );

        var output = Path.Combine(
            request.OutputFolder,
            hostFolder + "_" + DateTime.Now.ToString("yyyyMMdd_HHmmss")
        );

        _isDownloading = true;
        _cancel = new CancellationTokenSource();

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

            await _scraper.DownloadAsync(
                request.Url,
                output,
                request.Mode,
                progress,
                _cancel.Token
            );

            Post(new
            {
                type = "done",
                message = "ดาวน์โหลดสำเร็จ",
                path = output
            });
        }
        catch (OperationCanceledException)
        {
            Post(new
            {
                type = "cancelled",
                message = "ยกเลิกการดาวน์โหลดแล้ว"
            });
        }
        catch (Exception ex)
        {
            Post(new
            {
                type = "error",
                message = ex.Message
            });
        }
        finally
        {
            _cancel?.Dispose();
            _cancel = null;
            _isDownloading = false;
        }
    }

    private void OnFormClosing(object? sender, FormClosingEventArgs e)
    {
        _cancel?.Cancel();
    }

    private void Post(object value)
    {
        if (_web.CoreWebView2 == null || _web.IsDisposed)
            return;

        _web.CoreWebView2.PostWebMessageAsJson(
            JsonSerializer.Serialize(value)
        );
    }
}

