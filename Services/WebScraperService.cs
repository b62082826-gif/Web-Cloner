
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using HtmlAgilityPack;

namespace WebGrabberPro.Services;

public sealed class WebScraperService
{
    private const int MaxFiles = 250;
    private const int MaxFileSize = 25 * 1024 * 1024;
    private const int MaxConcurrentDownloads = 6;

    private static readonly HttpClient Http = new(
        new HttpClientHandler
        {
            AllowAutoRedirect = true
        })
    {
        Timeout = TimeSpan.FromSeconds(40)
    };

    private readonly ConcurrentDictionary<string, string> _savedFiles = new();
    private readonly SemaphoreSlim _semaphore =
        new(MaxConcurrentDownloads);

    private int _fileCount;

    private static readonly Regex CssResourceRegex = new(
        @"url\(\s*(['""]?)(?<url>.*?)\1\s*\)|@import\s+(['""])(?<import>.*?)\3",
        RegexOptions.IgnoreCase | RegexOptions.Compiled
    );

    public async Task DownloadAsync(
        string url,
        string outputFolder,
        string mode,
        IProgress<(int percent, string message)>? progress,
        CancellationToken token)
    {
        _savedFiles.Clear();
        _fileCount = 0;

        if (!Uri.TryCreate(url, UriKind.Absolute, out var baseUri) ||
            (baseUri.Scheme != "http" && baseUri.Scheme != "https"))
        {
            throw new InvalidOperationException("URL ไม่ถูกต้อง");
        }

        Directory.CreateDirectory(outputFolder);

        progress?.Report((5, "กำลังดาวน์โหลด HTML..."));

        using var response = await Http.GetAsync(
            baseUri,
            HttpCompletionOption.ResponseHeadersRead,
            token
        );

        response.EnsureSuccessStatusCode();

        var finalUri = response.RequestMessage?.RequestUri ?? baseUri;
        var html = await response.Content.ReadAsStringAsync(token);

        var document = new HtmlDocument();
        document.LoadHtml(html);

        progress?.Report((15, "กำลังตรวจสอบไฟล์เว็บไซต์..."));

        await ProcessHtmlAsync(
            document,
            finalUri,
            outputFolder,
            progress,
            token
        );

        token.ThrowIfCancellationRequested();

        var outputHtml = document.DocumentNode.OuterHtml;

        await File.WriteAllTextAsync(
            Path.Combine(outputFolder, "index.html"),
            outputHtml,
            new UTF8Encoding(false),
            token
        );

        progress?.Report((100, "บันทึกเว็บไซต์เรียบร้อย"));
    }

    private async Task ProcessHtmlAsync(
        HtmlDocument document,
        Uri baseUri,
        string outputFolder,
        IProgress<(int percent, string message)>? progress,
        CancellationToken token)
    {
        var nodes = document.DocumentNode.SelectNodes(
            "//*[@src or @href or @poster or @srcset or @style]"
        );

        if (nodes != null)
        {
            foreach (var node in nodes)
            {
                token.ThrowIfCancellationRequested();

                foreach (var attributeName in new[]
                {
                    "src", "poster"
                })
                {
                    var attribute = node.Attributes[attributeName];

                    if (attribute != null)
                    {
                        attribute.Value = await DownloadAndReplaceAsync(
                            attribute.Value,
                            baseUri,
                            outputFolder,
                            token
                        );
                    }
                }

                var href = node.Attributes["href"];

                if (href != null)
                {
                    var rel = node.GetAttributeValue("rel", "")
                        .ToLowerInvariant();

                    if (rel.Contains("stylesheet") ||
                        rel.Contains("icon") ||
                        rel.Contains("preload") ||
                        rel.Contains("modulepreload"))
                    {
                        href.Value = await DownloadAndReplaceAsync(
                            href.Value,
                            baseUri,
                            outputFolder,
                            token
                        );
                    }
                }

                var srcset = node.Attributes["srcset"];

                if (srcset != null)
                {
                    srcset.Value = await ProcessSrcSetAsync(
                        srcset.Value,
                        baseUri,
                        outputFolder,
                        token
                    );
                }

                var style = node.Attributes["style"];

                if (style != null)
                {
                    style.Value = await RewriteCssAsync(
                        style.Value,
                        baseUri,
                        outputFolder,
                        token
                    );
                }
            }
        }

        var styleNodes = document.DocumentNode.SelectNodes("//style");

        if (styleNodes != null)
        {
            foreach (var styleNode in styleNodes)
            {
                styleNode.InnerHtml = await RewriteCssAsync(
                    styleNode.InnerHtml,
                    baseUri,
                    outputFolder,
                    token
                );
            }
        }
    }

    private async Task<string> ProcessSrcSetAsync(
        string srcset,
        Uri baseUri,
        string outputFolder,
        CancellationToken token)
    {
        var parts = srcset.Split(',');

        for (int i = 0; i < parts.Length; i++)
        {
            token.ThrowIfCancellationRequested();

            var item = parts[i].Trim();
            var split = item.Split(
                ' ',
                2,
                StringSplitOptions.RemoveEmptyEntries
            );

            if (split.Length == 0)
                continue;

            var local = await DownloadAndReplaceAsync(
                split[0],
                baseUri,
                outputFolder,
                token
            );

            parts[i] = split.Length > 1
                ? local + " " + split[1]
                : local;
        }

        return string.Join(", ", parts);
    }

    private async Task<string> DownloadAndReplaceAsync(
        string originalUrl,
        Uri baseUri,
        string outputFolder,
        CancellationToken token)
    {
        var value = originalUrl.Trim();

        if (string.IsNullOrWhiteSpace(value) ||
            value.StartsWith("data:", StringComparison.OrdinalIgnoreCase) ||
            value.StartsWith("blob:", StringComparison.OrdinalIgnoreCase) ||
            value.StartsWith("#") ||
            value.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase) ||
            value.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase))
        {
            return originalUrl;
        }

        if (!Uri.TryCreate(baseUri, value, out var assetUri))
            return originalUrl;

        if (assetUri.Scheme != "http" && assetUri.Scheme != "https")
            return originalUrl;

        var fragment = assetUri.Fragment;
        var cleanUri = new UriBuilder(assetUri)
        {
            Fragment = ""
        }.Uri;

        var localPath = await DownloadAssetAsync(
            cleanUri,
            outputFolder,
            token
        );

        return localPath == null
            ? originalUrl
            : localPath + fragment;
    }

    private async Task<string?> DownloadAssetAsync(
        Uri assetUri,
        string outputFolder,
        CancellationToken token)
    {
        var key = assetUri.AbsoluteUri;

        if (_savedFiles.TryGetValue(key, out var existing))
            return existing;

        string filePath;

        await _semaphore.WaitAsync(token);

        try
        {
            if (_savedFiles.TryGetValue(key, out existing))
                return existing;

            if (Interlocked.Increment(ref _fileCount) > MaxFiles)
                return null;

            using var response = await Http.GetAsync(
                assetUri,
                HttpCompletionOption.ResponseHeadersRead,
                token
            );

            if (!response.IsSuccessStatusCode)
                return null;

            if (response.Content.Headers.ContentLength is long length &&
                length > MaxFileSize)
            {
                return null;
            }

            var bytes = await response.Content.ReadAsByteArrayAsync(token);

            if (bytes.Length > MaxFileSize)
                return null;

            var finalUri = response.RequestMessage?.RequestUri ?? assetUri;

            var extension = Path.GetExtension(finalUri.AbsolutePath);

            if (!Regex.IsMatch(extension, @"^\.[a-zA-Z0-9]{1,10}$"))
                extension = ".bin";

            var hash = Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(key))
            )[..16].ToLowerInvariant();

            var assetFolder = Path.Combine(outputFolder, "assets");

            Directory.CreateDirectory(assetFolder);

            filePath = Path.Combine(
                assetFolder,
                hash + extension
            );

            await File.WriteAllBytesAsync(filePath, bytes, token);

            var relativePath = "assets/" + hash + extension;

            _savedFiles.TryAdd(key, relativePath);
        }
        finally
        {
            _semaphore.Release();
        }

        // ถ้าเป็น CSS ให้ค้นหาและดาวน์โหลดไฟล์ที่ CSS อ้างอิงต่อ
        if (Path.GetExtension(filePath).Equals(
            ".css",
            StringComparison.OrdinalIgnoreCase))
        {
            var css = await File.ReadAllTextAsync(filePath, token);

            var rewritten = await RewriteCssAsync(
                css,
                assetUri,
                outputFolder,
                token
            );

            await File.WriteAllTextAsync(
                filePath,
                rewritten,
                new UTF8Encoding(false),
                token
            );
        }

        return _savedFiles.TryGetValue(key, out var saved)
            ? saved
            : null;
    }

    private async Task<string> RewriteCssAsync(
        string css,
        Uri baseUri,
        string outputFolder,
        CancellationToken token)
    {
        var matches = CssResourceRegex.Matches(css);

        if (matches.Count == 0)
            return css;

        var result = new StringBuilder();
        var lastIndex = 0;

        foreach (Match match in matches)
        {
            token.ThrowIfCancellationRequested();

            result.Append(
                css.Substring(lastIndex, match.Index - lastIndex)
            );

            var urlGroup = match.Groups["url"];
            var importGroup = match.Groups["import"];

            var group = urlGroup.Success ? urlGroup : importGroup;
            var original = group.Value.Trim();

            var replacement = await DownloadAndReplaceAsync(
                original,
                baseUri,
                outputFolder,
                token
            );

            if (urlGroup.Success)
            {
                result.Append(
                    match.Value.Replace(original, replacement)
                );
            }
            else
            {
                result.Append(
                    match.Value.Replace(original, replacement)
                );
            }

            lastIndex = match.Index + match.Length;
        }

        result.Append(css.Substring(lastIndex));

        return result.ToString();
    }
}
