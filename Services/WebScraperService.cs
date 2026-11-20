
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using HtmlAgilityPack;

namespace WebGrabberPro.Services;

public sealed class WebScraperService
{
    private const int MaxFiles = 250;
    private const int MaxFileSize = 15 * 1024 * 1024;

    private readonly HttpClient _http = new()
    {
        Timeout = TimeSpan.FromSeconds(30)
    };

    public WebScraperService()
    {
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) WebGrabberPro/1.0"
        );
    }

    public async Task DownloadAsync(
        string url,
        string output,
        string mode,
        IProgress<(int percent, string message)>? progress,
        CancellationToken token)
    {
        var baseUri = new Uri(url);

        Directory.CreateDirectory(output);

        using var response = await _http.GetAsync(
            baseUri,
            HttpCompletionOption.ResponseHeadersRead,
            token
        );

        response.EnsureSuccessStatusCode();

        var html = await response.Content.ReadAsStringAsync(token);
        var document = new HtmlDocument();
        document.LoadHtml(html);

        var assets = document.DocumentNode.SelectNodes(
            "//link[@href] | //script[@src] | //img[@src] | //source[@src]"
        );

        var total = assets?.Count ?? 0;
        var completed = 0;

        if (assets != null)
        {
            foreach (var node in assets)
            {
                token.ThrowIfCancellationRequested();

                var attribute = node.Name == "link" ? "href" : "src";
                var original = node.GetAttributeValue(attribute, "");

                if (!Uri.TryCreate(baseUri, original, out var assetUri))
                    continue;

                if (assetUri.Scheme != Uri.UriSchemeHttp &&
                    assetUri.Scheme != Uri.UriSchemeHttps)
                    continue;

                // ดาวน์โหลดเฉพาะไฟล์จากโดเมนเดียวกัน
                if (!assetUri.Host.Equals(
                    baseUri.Host,
                    StringComparison.OrdinalIgnoreCase))
                    continue;

                try
                {
                    var localPath = await DownloadAssetAsync(
                        assetUri,
                        output,
                        token
                    );

                    if (localPath != null)
                    {
                        var relative = Path.GetRelativePath(
                            output,
                            localPath
                        ).Replace('\\', '/');

                        node.SetAttributeValue(attribute, relative);
                    }
                }
                catch (Exception ex) when (
                    ex is HttpRequestException ||
                    ex is IOException ||
                    ex is TaskCanceledException)
                {
                    // ข้ามไฟล์ที่ดาวน์โหลดไม่ได้
                }

                completed++;

                progress?.Report((
                    total == 0 ? 90 : 10 + completed * 80 / total,
                    $"กำลังดาวน์โหลดไฟล์ {completed}/{total}"
                ));
            }
        }

        token.ThrowIfCancellationRequested();

        var indexPath = Path.Combine(output, "index.html");

        await File.WriteAllTextAsync(
            indexPath,
            document.DocumentNode.OuterHtml,
            new UTF8Encoding(false),
            token
        );

        progress?.Report((100, "บันทึกเว็บไซต์เรียบร้อย"));
    }

    private async Task<string?> DownloadAssetAsync(
        Uri uri,
        string output,
        CancellationToken token)
    {
        using var response = await _http.GetAsync(
            uri,
            HttpCompletionOption.ResponseHeadersRead,
            token
        );

        if (!response.IsSuccessStatusCode)
            return null;

        if (response.Content.Headers.ContentLength > MaxFileSize)
            return null;

        await using var stream = await response.Content.ReadAsStreamAsync(token);
        using var memory = new MemoryStream();

        var buffer = new byte[81920];
        int read;

        while ((read = await stream.ReadAsync(buffer, token)) > 0)
        {
            if (memory.Length + read > MaxFileSize)
                return null;

            await memory.WriteAsync(buffer.AsMemory(0, read), token);
        }

        var extension = Path.GetExtension(uri.AbsolutePath);

        if (string.IsNullOrWhiteSpace(extension))
            extension = ".bin";

        var hash = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(uri.AbsoluteUri))
        )[..16];

        var folder = GetAssetFolder(extension);
        var directory = Path.Combine(output, folder);

        Directory.CreateDirectory(directory);

        var filePath = Path.Combine(
            directory,
            hash + extension.ToLowerInvariant()
        );

        await File.WriteAllBytesAsync(
            filePath,
            memory.ToArray(),
            token
        );

        return filePath;
    }

    private static string GetAssetFolder(string extension)
    {
        return extension.ToLowerInvariant() switch
        {
            ".css" => "css",
            ".js" => "js",
            ".png" or ".jpg" or ".jpeg" or ".gif" or ".webp" or ".svg"
                => "images",
            _ => "assets"
        };
    }
}

