
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using WebGrabberPro.Models;
using WebGrabberPro.Services;

namespace WebGrabberPro;

public sealed class MainForm : Form
{
    private readonly WebView2 _web = new() { Dock = DockStyle.Fill };
    private readonly WebScraperService _scraper = new();
    private CancellationTokenSource? _cancel;

    public MainForm()
    {
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(900, 620);
        Size = new Size(1120, 760);
        BackColor = Color.FromArgb(12, 17, 28);

        Controls.Add(_web);
        Load += OnLoad;
    }

    private async void OnLoad(object? sender, EventArgs e)
    {
        try
        {
            await _web.EnsureCoreWebView2Async();

            _web.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
            _web.CoreWebView2.Settings.AreDevToolsEnabled = false;
            _web.CoreWebView2.WebMessageReceived += OnWebMessage;

            var page = Path.Combine(
                AppContext.BaseDirectory,
                "wwwroot",
                "index.html"
            );

            _web.CoreWebView2.Navigate(new Uri(page).AbsoluteUri);
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                "ไม่สามารถเริ่ม WebView2 ได้ กรุณาติดตั้ง Microsoft Edge WebView2 Runtime\n\n" + ex.Message,
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
            var action = root.GetProperty("action").GetString();

            if (action == "minimize")
            {
                WindowState = FormWindowState.Minimized;
                return;
            }

            if (action == "close")
            {
                Close();
                return;
            }

            if (action == "browse")
            {
                using var dialog = new FolderBrowserDialog
                {
                    Description = "เลือกโฟลเดอร์บันทึกเว็บไซต์"
                };

                if (dialog.ShowDialog(this) == DialogResult.OK)
                {
                    Post(new
                    {
                        type = "folder",
                        path = dialog.SelectedPath
                    });
                }

                return;
            }

            if (action == "cancel")
            {
                _cancel?.Cancel();
                return;
            }

            if (action == "download")
            {
                var request = JsonSerializer.Deserialize<DownloadRequest>(
                    root.GetProperty("data").GetRawText(),
                    new JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true
                    }
                );

                if (request == null) return;

                await StartDownloadAsync(request);
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

    private async Task StartDownloadAsync(DownloadRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.OutputFolder))
            throw new InvalidOperationException("กรุณาเลือกโฟลเดอร์บันทึกไฟล์");

        if (!Uri.TryCreate(request.Url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != "http" && uri.Scheme != "https"))
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

        _cancel = new CancellationTokenSource();

        var progress = new Progress<(int percent, string message)>(p =>
            Post(new
            {
                type = "progress",
                percent = p.percent,
                message = p.message
            })
        );

        try
        {
            Post(new { type = "started", message = "เริ่มดาวน์โหลด..." });

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
                message = "บันทึกสำเร็จ",
                path = output
            });
        }
        catch (OperationCanceledException)
        {
            Post(new
            {
                type = "cancelled",
                message = "ยกเลิกแล้ว"
            });
        }
        finally
        {
            _cancel.Dispose();
            _cancel = null;
        }
    }

    private void Post(object value)
    {
        if (_web.CoreWebView2 == null) return;

        _web.CoreWebView2.PostWebMessageAsJson(
            JsonSerializer.Serialize(value)
        );
    }
}

