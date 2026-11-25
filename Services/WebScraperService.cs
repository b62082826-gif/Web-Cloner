using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using HtmlAgilityPack;
using HtmlDocument = HtmlAgilityPack.HtmlDocument;
using WebGrabberPro.Models;

namespace WebGrabberPro.Services;

public sealed class WebScraperService : IWebScraperService
{
    private const int MaxFiles = 400;
    private const int MaxFilesMultiPage = 1500;
    private const int MaxPages = 40;
    private const int MaxFileSize = 25 * 1024 * 1024;
    private const int MaxConcurrentDownloads = 8;

    private const string UserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 " +
        "(KHTML, like Gecko) Chrome/124.0 Safari/537.36";

    private static readonly HttpClient Http = CreateClient();

    static WebScraperService()
    {
        // ให้อ่านหน้าเว็บที่ใช้ charset เก่า เช่น windows-874 / tis-620 ได้
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    private static HttpClient CreateClient()
    {
        var handler = new HttpClientHandler
        {
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 10,
            AutomaticDecompression = DecompressionMethods.All,
            UseCookies = true
        };

        var client = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(40)
        };

        client.DefaultRequestHeaders.TryAddWithoutValidation(
            "User-Agent", UserAgent);
        client.DefaultRequestHeaders.TryAddWithoutValidation(
            "Accept",
            "text/html,application/xhtml+xml,application/xml;q=0.9," +
            "image/avif,image/webp,*/*;q=0.8");
        client.DefaultRequestHeaders.TryAddWithoutValidation(
            "Accept-Language", "th,en;q=0.9");

        return client;
    }

    public async Task<DownloadResult> DownloadAsync(
        string url,
        string outputFolder,
        string mode,
        int depth,
        IProgress<(int percent, string message)>? progress,
        CancellationToken token)
    {
        if (!Uri.TryCreate(url?.Trim(), UriKind.Absolute, out var baseUri) ||
            !IsHttp(baseUri))
        {
            throw new InvalidOperationException("That URL is not valid.");
        }

        Directory.CreateDirectory(outputFolder);

        var single = string.Equals(
            mode, "single", StringComparison.OrdinalIgnoreCase);

        var session = new Session(
            single, outputFolder, Math.Clamp(depth, 1, 3), progress);

        return await session.RunAsync(baseUri, token);
    }

    // ------------------------------------------------------------------
    // Helpers ที่ใช้ร่วมกัน
    // ------------------------------------------------------------------

    private enum AssetKind { Css, Js, Image, Font, Media, Other }

    private sealed record Asset(
        string Key,
        Uri FinalUri,
        byte[] Bytes,
        string Mime,
        AssetKind Kind,
        string LocalPath);

    private static readonly Dictionary<string, string> MimeToExt =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["text/css"] = ".css",
            ["text/javascript"] = ".js",
            ["application/javascript"] = ".js",
            ["application/x-javascript"] = ".js",
            ["application/ecmascript"] = ".js",
            ["image/png"] = ".png",
            ["image/jpeg"] = ".jpg",
            ["image/gif"] = ".gif",
            ["image/webp"] = ".webp",
            ["image/avif"] = ".avif",
            ["image/svg+xml"] = ".svg",
            ["image/x-icon"] = ".ico",
            ["image/vnd.microsoft.icon"] = ".ico",
            ["image/bmp"] = ".bmp",
            ["font/woff2"] = ".woff2",
            ["font/woff"] = ".woff",
            ["font/ttf"] = ".ttf",
            ["font/otf"] = ".otf",
            ["application/font-woff"] = ".woff",
            ["application/font-woff2"] = ".woff2",
            ["application/vnd.ms-fontobject"] = ".eot",
            ["video/mp4"] = ".mp4",
            ["video/webm"] = ".webm",
            ["audio/mpeg"] = ".mp3",
            ["audio/ogg"] = ".ogg",
            ["audio/wav"] = ".wav",
            ["application/json"] = ".json",
            ["text/vtt"] = ".vtt"
        };

    private static readonly Dictionary<string, string> ExtToMime =
        BuildExtToMime();

    private static Dictionary<string, string> BuildExtToMime()
    {
        var map = new Dictionary<string, string>(
            StringComparer.OrdinalIgnoreCase);

        foreach (var pair in MimeToExt)
            map.TryAdd(pair.Value, pair.Key);

        map[".jpeg"] = "image/jpeg";
        map[".mjs"] = "text/javascript";

        return map;
    }

    // ตรงกับทั้ง @import และ url(...) ในไฟล์ CSS
    //   กลุ่ม "a"   = URL ของ @import   (กลุ่ม "tail" = media query ท้าย @import)
    //   กลุ่ม "b"   = URL ของ url(...)
    private static readonly Regex CssRefRegex = new(
        @"@import\s+(?:url\(\s*(?:""(?<a>[^""]*)""|'(?<a>[^']*)'|(?<a>[^)\s'""]*))\s*\)|""(?<a>[^""]*)""|'(?<a>[^']*)')(?<tail>[^;{}]*);" +
        @"|url\(\s*(?:""(?<b>[^""]*)""|'(?<b>[^']*)'|(?<b>[^)\s'""]*))\s*\)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex CharsetRegex = new(
        @"charset\s*=\s*[""']?\s*([A-Za-z0-9_\-]+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex ExtensionRegex = new(
        @"^\.[a-z0-9]{1,8}$", RegexOptions.Compiled);

    private static readonly Action Noop = () => { };

    private static bool IsHttp(Uri uri) =>
        uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps;

    private static bool IsFetchable(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;

        value = value.TrimStart();

        return !(value.StartsWith("#") ||
                 value.StartsWith("data:", StringComparison.OrdinalIgnoreCase) ||
                 value.StartsWith("blob:", StringComparison.OrdinalIgnoreCase) ||
                 value.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase) ||
                 value.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase) ||
                 value.StartsWith("tel:", StringComparison.OrdinalIgnoreCase) ||
                 value.StartsWith("sms:", StringComparison.OrdinalIgnoreCase) ||
                 value.StartsWith("about:", StringComparison.OrdinalIgnoreCase));
    }

    private static readonly HashSet<string> PageExtensions = new(
        StringComparer.OrdinalIgnoreCase)
    {
        "", ".html", ".htm", ".shtml", ".php", ".asp", ".aspx", ".jsp"
    };

    private static string HostKey(Uri uri) =>
        uri.Host.ToLowerInvariant().StartsWith("www.")
            ? uri.Host.ToLowerInvariant()[4..]
            : uri.Host.ToLowerInvariant();

    // หน้าเดียวกันที่สะกดต่างกัน (/a กับ /a/) ต้องได้ key เดียวกัน
    private static string PageKey(Uri uri)
    {
        var path = uri.AbsolutePath.TrimEnd('/');

        return HostKey(uri) + (path.Length == 0 ? "/" : path) + uri.Query;
    }

    private static bool IsPageLink(Uri uri, Uri site) =>
        IsHttp(uri) &&
        HostKey(uri) == HostKey(site) &&
        PageExtensions.Contains(Path.GetExtension(uri.AbsolutePath));

    private sealed class PageDoc
    {
        public Uri Uri { get; init; } = null!;
        public HtmlDocument Document { get; init; } = null!;
        public List<(HtmlNode node, Uri target)> Links { get; init; } = new();
        public string FileName { get; set; } = "";
    }

    private static string EscapeAttr(string value) =>
        value.Replace("&", "&amp;").Replace("\"", "&quot;");

    private static string DecodeUtf8(byte[] bytes) =>
        Encoding.UTF8.GetString(bytes).TrimStart('﻿');

    private static string RelPath(string fromDir, string toPath)
    {
        var relative = fromDir.Length == 0
            ? toPath
            : Path.GetRelativePath(fromDir, toPath);

        return relative.Replace('\\', '/');
    }

    private static string DecodeHtml(byte[] bytes, string? headerCharset)
    {
        Encoding? encoding = null;

        if (!string.IsNullOrWhiteSpace(headerCharset))
            encoding = TryGetEncoding(headerCharset.Trim('"', '\'', ' '));

        if (encoding == null)
        {
            var head = Encoding.Latin1.GetString(
                bytes, 0, Math.Min(bytes.Length, 4096));

            var match = CharsetRegex.Match(head);

            if (match.Success)
                encoding = TryGetEncoding(match.Groups[1].Value);
        }

        encoding ??= Encoding.UTF8;

        return encoding.GetString(bytes).TrimStart('﻿');
    }

    private static Encoding? TryGetEncoding(string name)
    {
        try
        {
            return Encoding.GetEncoding(name);
        }
        catch
        {
            return null;
        }
    }

    private static List<(string url, string desc)> ParseSrcSet(string value)
    {
        var list = new List<(string, string)>();
        var i = 0;

        while (i < value.Length)
        {
            while (i < value.Length &&
                   (char.IsWhiteSpace(value[i]) || value[i] == ','))
            {
                i++;
            }

            if (i >= value.Length)
                break;

            var start = i;

            while (i < value.Length && !char.IsWhiteSpace(value[i]))
                i++;

            var url = value[start..i];
            var desc = string.Empty;

            if (url.EndsWith(','))
            {
                url = url.TrimEnd(',');
            }
            else
            {
                var descStart = i;

                while (i < value.Length && value[i] != ',')
                    i++;

                desc = value[descStart..i].Trim();
            }

            if (url.Length > 0)
                list.Add((url, desc));
        }

        return list;
    }

    private static AssetKind Classify(string mime, string ext)
    {
        mime = mime.ToLowerInvariant();

        if (mime.Contains("css") || ext == ".css")
            return AssetKind.Css;

        if (mime.Contains("javascript") || mime.Contains("ecmascript") ||
            ext == ".js" || ext == ".mjs")
            return AssetKind.Js;

        if (mime.StartsWith("image/") ||
            ext is ".png" or ".jpg" or ".jpeg" or ".gif" or ".webp" or
                ".avif" or ".svg" or ".ico" or ".bmp")
            return AssetKind.Image;

        if (mime.Contains("font") ||
            ext is ".woff" or ".woff2" or ".ttf" or ".otf" or ".eot")
            return AssetKind.Font;

        if (mime.StartsWith("video/") || mime.StartsWith("audio/") ||
            ext is ".mp4" or ".webm" or ".mp3" or ".ogg" or ".wav")
            return AssetKind.Media;

        return AssetKind.Other;
    }

    private static string FolderFor(AssetKind kind) => kind switch
    {
        AssetKind.Css => "css",
        AssetKind.Js => "js",
        AssetKind.Image => "images",
        AssetKind.Font => "fonts",
        AssetKind.Media => "media",
        _ => "other"
    };

    private static string MimeFor(Asset asset)
    {
        if (!string.IsNullOrWhiteSpace(asset.Mime) &&
            !asset.Mime.Equals(
                "application/octet-stream",
                StringComparison.OrdinalIgnoreCase))
        {
            return asset.Mime;
        }

        var ext = Path.GetExtension(asset.LocalPath);

        return ExtToMime.TryGetValue(ext, out var mime)
            ? mime
            : "application/octet-stream";
    }

    private static string ToDataUri(Asset asset) =>
        "data:" + MimeFor(asset) + ";base64," +
        Convert.ToBase64String(asset.Bytes);

    // HtmlAgilityPack จะ parse ใหม่ถ้าตั้ง InnerHtml ซึ่งทำให้ JS/CSS ที่มี '<' เสียหาย
    // จึงใส่เป็น text node ดิบแทน
    private static void SetRawText(HtmlNode node, string text)
    {
        node.RemoveAllChildren();
        node.AppendChild(node.OwnerDocument.CreateTextNode(text));
    }

    private static void StripIntegrity(HtmlNode node)
    {
        // integrity จะไม่ตรงเมื่อไฟล์ถูกแก้/ฝังลงหน้า และ crossorigin ทำให้โหลดจาก file:// ไม่ได้
        node.Attributes.Remove("integrity");
        node.Attributes.Remove("crossorigin");
        node.Attributes.Remove("nonce");
    }

    private static Action MakeAbsolute(
        HtmlNode node, string attribute, Uri baseUri)
    {
        var attr = node.Attributes[attribute];

        if (attr == null)
            return Noop;

        var raw = HtmlEntity.DeEntitize(attr.Value).Trim();

        if (!IsFetchable(raw) ||
            !Uri.TryCreate(baseUri, raw, out var uri) ||
            !IsHttp(uri))
        {
            return Noop;
        }

        var absolute = uri.AbsoluteUri;

        return () => node.SetAttributeValue(attribute, EscapeAttr(absolute));
    }

    private static void NormalizeCharset(HtmlDocument document)
    {
        var metas = document.DocumentNode.SelectNodes(
            "//meta[@charset or translate(@http-equiv, 'CONTENT-TYPE', 'content-type')='content-type']");

        if (metas != null)
        {
            foreach (var meta in metas.ToList())
                meta.Remove();
        }

        var head = document.DocumentNode.SelectSingleNode("//head");

        head?.PrependChild(
            HtmlNode.CreateNode("<meta charset=\"utf-8\">")
        );
    }

    // ------------------------------------------------------------------
    // Session = สถานะของการดาวน์โหลด 1 ครั้ง
    // ------------------------------------------------------------------

    private sealed class Session
    {
        private readonly bool _single;
        private readonly int _depth;
        private readonly int _maxFiles;
        private readonly string _out;
        private List<(HtmlNode node, Uri target)> _links = new();
        private readonly IProgress<(int percent, string message)>? _progress;

        private readonly ConcurrentDictionary<string, Lazy<Task<Asset?>>>
            _assets = new();

        private readonly ConcurrentBag<Task> _pendingCss = new();
        private readonly SemaphoreSlim _gate = new(MaxConcurrentDownloads);
        private readonly object _progressLock = new();

        private Uri _pageUri = null!;
        private CancellationToken _ct;

        private int _total;
        private int _done;
        private int _ok;
        private int _failed;
        private long _bytes;
        private int _lastPercent;

        public Session(
            bool single,
            string outputFolder,
            int depth,
            IProgress<(int percent, string message)>? progress)
        {
            _single = single;
            _depth = depth;
            _maxFiles = depth > 1 ? MaxFilesMultiPage : MaxFiles;
            _out = outputFolder;
            _progress = progress;
        }

        public async Task<DownloadResult> RunAsync(
            Uri pageUri, CancellationToken ct)
        {
            _ct = ct;

            var pages = new List<PageDoc>();
            var seen = new HashSet<string> { PageKey(pageUri) };
            var queue = new Queue<(Uri uri, int level)>();

            queue.Enqueue((pageUri, 1));

            while (queue.Count > 0)
            {
                ct.ThrowIfCancellationRequested();

                var (uri, level) = queue.Dequeue();
                var isRoot = pages.Count == 0;

                PageDoc? page;

                try
                {
                    page = await ProcessPageAsync(uri, isRoot, pages.Count + 1);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch when (!isRoot)
                {
                    // หน้าลูกหน้าไหนโหลดไม่ได้ก็ข้าม ลิงก์เดิมยังเป็น URL เต็ม
                    continue;
                }

                if (page == null)
                    continue;

                pages.Add(page);

                if (level >= _depth)
                    continue;

                foreach (var (_, target) in page.Links)
                {
                    if (seen.Count >= MaxPages)
                        break;

                    if (IsPageLink(target, pageUri) &&
                        seen.Add(PageKey(target)))
                    {
                        queue.Enqueue((target, level + 1));
                    }
                }
            }

            // ตั้งชื่อไฟล์ของแต่ละหน้า แล้วแก้ลิงก์ระหว่างหน้าให้ชี้ไฟล์ในเครื่อง
            var files = new Dictionary<string, string>();

            foreach (var page in pages)
            {
                var key = PageKey(page.Uri);

                page.FileName = page == pages[0]
                    ? "index.html"
                    : PathSanitizer.FileStem(
                          page.Uri.AbsolutePath.TrimEnd('/')) + "-" +
                      Convert.ToHexString(SHA256.HashData(
                          Encoding.UTF8.GetBytes(key)))[..8]
                          .ToLowerInvariant() + ".html";

                files[key] = page.FileName;
                files[PageKey(pageUri)] = pages[0].FileName;
            }

            foreach (var page in pages)
            {
                foreach (var (node, target) in page.Links)
                {
                    if (files.TryGetValue(PageKey(target), out var local))
                        node.SetAttributeValue("href", local + target.Fragment);
                }

                NormalizeCharset(page.Document);

                await File.WriteAllTextAsync(
                    Path.Combine(_out, page.FileName),
                    page.Document.DocumentNode.OuterHtml,
                    new UTF8Encoding(false),
                    ct);
            }

            _progress?.Report((100, "Website saved"));

            return new DownloadResult(
                Path.Combine(_out, pages[0].FileName),
                _ok,
                _failed,
                Interlocked.Read(ref _bytes),
                pages.Count);
        }

        private async Task<PageDoc?> ProcessPageAsync(
            Uri pageUri, bool isRoot, int number)
        {
            var ct = _ct;

            if (isRoot)
                _progress?.Report((5, "Downloading the page..."));
            else
                Report("Page " + number + ": " + pageUri.AbsolutePath);

            using var request = new HttpRequestMessage(HttpMethod.Get, pageUri);
            using var response = await Http.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, ct);

            response.EnsureSuccessStatusCode();

            var mediaType = response.Content.Headers.ContentType?.MediaType;

            // หน้าลูกต้องเป็น HTML จริง (ลิงก์อาจชี้ไปไฟล์ PDF/รูป)
            if (!isRoot && mediaType != null &&
                !mediaType.Contains("html", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            _pageUri = response.RequestMessage?.RequestUri ?? pageUri;

            var bytes = await response.Content.ReadAsByteArrayAsync(ct);
            var html = DecodeHtml(
                bytes, response.Content.Headers.ContentType?.CharSet);

            var document = new HtmlDocument();
            document.LoadHtml(html);

            // <base href> ต้องถูกนำมาใช้คำนวณ URL และลบทิ้ง ไม่เช่นนั้นพาธในเครื่องจะเพี้ยน
            var baseUri = _pageUri;
            var baseNode = document.DocumentNode
                .SelectSingleNode("//base[@href]");

            if (baseNode != null)
            {
                var href = HtmlEntity.DeEntitize(
                    baseNode.GetAttributeValue("href", ""));

                if (Uri.TryCreate(_pageUri, href, out var b) && IsHttp(b))
                    baseUri = b;

                baseNode.Remove();
            }

            if (isRoot)
                _progress?.Report((15, "Looking for files in the page..."));

            _links = new List<(HtmlNode node, Uri target)>();

            var jobs = new List<Task<Action>>();

            var nodes = document.DocumentNode
                .Descendants()
                .Where(n => n.NodeType == HtmlNodeType.Element)
                .ToList();

            foreach (var node in nodes)
                CollectJobs(node, baseUri, jobs);

            // ดาวน์โหลดทุกไฟล์พร้อมกัน แล้วค่อยแก้ DOM ทีละอย่างตามลำดับ
            var actions = await Task.WhenAll(jobs);

            foreach (var action in actions)
                action();

            // โหมดแยกไฟล์: รอให้ไฟล์ CSS ทุกไฟล์ถูกแก้พาธเสร็จ
            // (CSS อาจลากไฟล์ CSS อื่นตามมาเพิ่มระหว่างที่รออยู่)
            while (_pendingCss.TryTake(out var pending))
                await pending;

            ct.ThrowIfCancellationRequested();

            return new PageDoc
            {
                Uri = _pageUri,
                Document = document,
                Links = _links
            };
        }

        // -------------------- รวบรวมงานจาก DOM --------------------

        private void CollectJobs(
            HtmlNode node, Uri baseUri, List<Task<Action>> jobs)
        {
            var name = node.Name.ToLowerInvariant();

            if (node.Attributes["style"] != null)
                jobs.Add(Safe(() => RewriteStyleAttributeAsync(node, baseUri)));

            switch (name)
            {
                case "style":
                    jobs.Add(Safe(() => RewriteStyleTagAsync(node, baseUri)));
                    break;

                case "link":
                    CollectLink(node, baseUri, jobs);
                    break;

                case "script":
                    if (node.Attributes["src"] != null)
                        jobs.Add(Safe(() => ProcessScriptAsync(node, baseUri)));
                    break;

                case "a":
                case "area":
                    // ลิงก์ไปหน้าอื่นแปลงเป็น URL เต็มให้กดแล้วไปเว็บจริง
                    // ถ้าเปิดโหลดหลายหน้า หน้าที่โหลดมาด้วยจะถูกแก้ให้ชี้ไฟล์ในเครื่องทีหลัง
                    if (_depth > 1 && name == "a")
                        NoteLink(node, baseUri);

                    jobs.Add(Task.FromResult(
                        MakeAbsolute(node, "href", baseUri)));
                    break;

                case "iframe":
                case "frame":
                case "embed":
                    jobs.Add(Task.FromResult(
                        MakeAbsolute(node, "src", baseUri)));
                    break;

                case "object":
                    jobs.Add(Task.FromResult(
                        MakeAbsolute(node, "data", baseUri)));
                    break;

                case "form":
                    jobs.Add(Task.FromResult(
                        MakeAbsolute(node, "action", baseUri)));
                    break;

                case "image":
                case "use":
                    foreach (var attribute in new[] { "href", "xlink:href" })
                    {
                        if (node.Attributes[attribute] != null)
                        {
                            var captured = attribute;

                            jobs.Add(Safe(() =>
                                ReplaceAttributeAsync(node, captured, baseUri)));
                        }
                    }
                    break;

                default:
                    foreach (var attribute in new[]
                    {
                        "src", "poster", "data-src", "data-lazy-src",
                        "data-original", "data-poster"
                    })
                    {
                        if (node.Attributes[attribute] != null)
                        {
                            var captured = attribute;

                            jobs.Add(Safe(() =>
                                ReplaceAttributeAsync(node, captured, baseUri)));
                        }
                    }

                    foreach (var attribute in new[] { "srcset", "data-srcset" })
                    {
                        if (node.Attributes[attribute] != null)
                        {
                            var captured = attribute;

                            jobs.Add(Safe(() =>
                                ReplaceSrcSetAsync(node, captured, baseUri)));
                        }
                    }
                    break;
            }
        }

        private void NoteLink(HtmlNode node, Uri baseUri)
        {
            var raw = HtmlEntity.DeEntitize(
                node.GetAttributeValue("href", "")).Trim();

            if (IsFetchable(raw) &&
                Uri.TryCreate(baseUri, raw, out var uri) &&
                IsHttp(uri))
            {
                _links.Add((node, uri));
            }
        }

        private void CollectLink(
            HtmlNode node, Uri baseUri, List<Task<Action>> jobs)
        {
            var tokens = node.GetAttributeValue("rel", "")
                .ToLowerInvariant()
                .Split(' ', StringSplitOptions.RemoveEmptyEntries);

            if (tokens.Contains("stylesheet"))
            {
                jobs.Add(Safe(() => ProcessStylesheetLinkAsync(node, baseUri)));
            }
            else if (tokens.Any(t => t is "preload" or "modulepreload" or
                         "prefetch" or "preconnect" or "dns-prefetch" or
                         "prerender"))
            {
                // เป็นแค่ตัวช่วยเร่งความเร็วของเว็บจริง ไม่จำเป็นกับไฟล์ที่บันทึก
                jobs.Add(Task.FromResult<Action>(() => node.Remove()));
            }
            else if (tokens.Any(t => t.Contains("icon")))
            {
                jobs.Add(Safe(() =>
                    ReplaceAttributeAsync(node, "href", baseUri)));
            }
            else if (tokens.Contains("manifest"))
            {
                jobs.Add(Task.FromResult(
                    MakeAbsolute(node, "href", baseUri)));
            }
        }

        private async Task<Action> Safe(Func<Task<Action>> job)
        {
            try
            {
                return await job();
            }
            catch (OperationCanceledException) when (_ct.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                // งานหนึ่งพังไม่ควรทำให้ทั้งเว็บล้มเหลว
                return Noop;
            }
        }

        // -------------------- แปลง attribute / srcset --------------------

        private async Task<string?> ResolveAsync(
            string raw, Uri baseUri, string ownerDir)
        {
            raw = raw.Trim();

            if (!IsFetchable(raw) ||
                !Uri.TryCreate(baseUri, raw, out var uri) ||
                !IsHttp(uri))
            {
                return null;
            }

            var asset = await GetAssetAsync(uri);

            // โหลดไม่ได้ -> ใช้ URL เต็มของเว็บจริงแทน หน้าที่บันทึกจะยังแสดงผลได้เมื่อออนไลน์
            if (asset == null)
                return uri.AbsoluteUri;

            return _single
                ? ToDataUri(asset) + uri.Fragment
                : RelPath(ownerDir, asset.LocalPath) + uri.Fragment;
        }

        private async Task<Action> ReplaceAttributeAsync(
            HtmlNode node, string attribute, Uri baseUri)
        {
            var attr = node.Attributes[attribute];

            if (attr == null)
                return Noop;

            var raw = HtmlEntity.DeEntitize(attr.Value);
            var replacement = await ResolveAsync(raw, baseUri, "");

            if (replacement == null)
                return Noop;

            return () =>
            {
                node.SetAttributeValue(attribute, EscapeAttr(replacement));
                StripIntegrity(node);
            };
        }

        private async Task<Action> ReplaceSrcSetAsync(
            HtmlNode node, string attribute, Uri baseUri)
        {
            var attr = node.Attributes[attribute];

            if (attr == null)
                return Noop;

            var items = ParseSrcSet(HtmlEntity.DeEntitize(attr.Value));

            if (items.Count == 0)
                return Noop;

            var parts = await Task.WhenAll(items.Select(async item =>
            {
                var url = await ResolveAsync(item.url, baseUri, "") ?? item.url;

                return string.IsNullOrEmpty(item.desc)
                    ? url
                    : url + " " + item.desc;
            }));

            var joined = string.Join(", ", parts);

            return () => node.SetAttributeValue(attribute, EscapeAttr(joined));
        }

        // -------------------- <script src> --------------------

        private async Task<Action> ProcessScriptAsync(
            HtmlNode node, Uri baseUri)
        {
            var raw = HtmlEntity.DeEntitize(node.GetAttributeValue("src", ""))
                .Trim();

            if (!IsFetchable(raw) ||
                !Uri.TryCreate(baseUri, raw, out var uri) ||
                !IsHttp(uri))
            {
                return Noop;
            }

            var asset = await GetAssetAsync(uri);

            if (asset == null)
            {
                var absolute = uri.AbsoluteUri;

                return () =>
                {
                    node.SetAttributeValue("src", EscapeAttr(absolute));
                    StripIntegrity(node);
                };
            }

            if (!_single)
            {
                var relative = RelPath("", asset.LocalPath);

                return () =>
                {
                    node.SetAttributeValue("src", relative);
                    StripIntegrity(node);
                };
            }

            var code = DecodeUtf8(asset.Bytes)
                .Replace("</script", "<\\/script",
                    StringComparison.OrdinalIgnoreCase);

            return () =>
            {
                var isModule = node.GetAttributeValue("type", "")
                    .Equals("module", StringComparison.OrdinalIgnoreCase);

                var defer = node.Attributes["defer"] != null;

                node.Attributes.Remove("src");
                node.Attributes.Remove("async");
                node.Attributes.Remove("defer");
                StripIntegrity(node);

                SetRawText(node, code);

                // สคริปต์แบบ defer ต้องรันหลังโหลด DOM เสร็จ
                // เมื่อฝังลงหน้าแล้วต้องย้ายไปท้าย <body> เพื่อให้ลำดับการทำงานเหมือนเดิม
                if (defer && !isModule && node.ParentNode != null)
                {
                    var body = node.OwnerDocument.DocumentNode
                        .SelectSingleNode("//body");

                    if (body != null)
                    {
                        node.Remove();
                        body.AppendChild(node);
                    }
                }
            };
        }

        // -------------------- CSS --------------------

        private async Task<Action> ProcessStylesheetLinkAsync(
            HtmlNode node, Uri baseUri)
        {
            var raw = HtmlEntity.DeEntitize(node.GetAttributeValue("href", ""))
                .Trim();

            if (!IsFetchable(raw) ||
                !Uri.TryCreate(baseUri, raw, out var uri) ||
                !IsHttp(uri))
            {
                return Noop;
            }

            var asset = await GetAssetAsync(uri);

            if (asset == null)
            {
                var absolute = uri.AbsoluteUri;

                return () =>
                {
                    node.SetAttributeValue("href", EscapeAttr(absolute));
                    StripIntegrity(node);
                };
            }

            if (!_single)
            {
                var relative = RelPath("", asset.LocalPath);

                return () =>
                {
                    node.SetAttributeValue("href", relative);
                    StripIntegrity(node);
                };
            }

            var css = await RewriteCssAsync(
                DecodeUtf8(asset.Bytes),
                asset.FinalUri,
                null,
                new HashSet<string> { asset.Key });

            css = css.Replace("</style", "<\\/style",
                StringComparison.OrdinalIgnoreCase);

            return () =>
            {
                if (node.ParentNode == null)
                    return;

                var style = node.OwnerDocument.CreateElement("style");
                var media = node.GetAttributeValue("media", "");

                if (media.Length > 0)
                    style.SetAttributeValue("media", media);

                SetRawText(style, css);
                node.ParentNode.ReplaceChild(style, node);
            };
        }

        private async Task<Action> RewriteStyleTagAsync(
            HtmlNode node, Uri baseUri)
        {
            var css = node.InnerHtml;

            var rewritten = await RewriteCssAsync(
                css, baseUri, _single ? null : "", new HashSet<string>());

            if (rewritten == css)
                return Noop;

            return () => SetRawText(node, rewritten);
        }

        private async Task<Action> RewriteStyleAttributeAsync(
            HtmlNode node, Uri baseUri)
        {
            var attr = node.Attributes["style"];

            if (attr == null)
                return Noop;

            var raw = HtmlEntity.DeEntitize(attr.Value);

            if (!raw.Contains("url(", StringComparison.OrdinalIgnoreCase))
                return Noop;

            var rewritten = await RewriteCssAsync(
                raw, baseUri, _single ? null : "", new HashSet<string>());

            if (rewritten == raw)
                return Noop;

            return () => node.SetAttributeValue(
                "style", EscapeAttr(rewritten));
        }

        /// <param name="ownerDir">
        /// โฟลเดอร์ (เทียบกับ root ของผลลัพธ์) ของไฟล์ที่มี CSS นี้อยู่
        /// "" = index.html, "css" = ไฟล์ใน css/ ; null = โหมดฝังลงไฟล์เดียว
        /// </param>
        private async Task<string> RewriteCssAsync(
            string css, Uri baseUri, string? ownerDir, HashSet<string> stack)
        {
            var matches = CssRefRegex.Matches(css);

            if (matches.Count == 0)
                return css;

            var tasks = new List<Task<string>>(matches.Count);

            foreach (Match match in matches)
                tasks.Add(RewriteCssRefAsync(match, baseUri, ownerDir, stack));

            var parts = await Task.WhenAll(tasks);

            var builder = new StringBuilder();
            var last = 0;

            for (var i = 0; i < matches.Count; i++)
            {
                var match = matches[i];

                builder.Append(css, last, match.Index - last);
                builder.Append(parts[i]);

                last = match.Index + match.Length;
            }

            builder.Append(css, last, css.Length - last);

            return builder.ToString();
        }

        private async Task<string> RewriteCssRefAsync(
            Match match, Uri baseUri, string? ownerDir, HashSet<string> stack)
        {
            var importGroup = match.Groups["a"];
            var isImport = importGroup.Success;

            var raw = (isImport
                ? importGroup.Value
                : match.Groups["b"].Value).Trim();

            if (!IsFetchable(raw) ||
                !Uri.TryCreate(baseUri, raw, out var uri) ||
                !IsHttp(uri))
            {
                return match.Value;
            }

            var tail = isImport ? match.Groups["tail"].Value : "";
            var asset = await GetAssetAsync(uri);

            string Import(string target) =>
                "@import url(\"" + target + "\")" + tail + ";";

            string Url(string target) => "url(\"" + target + "\")";

            if (asset == null)
            {
                return isImport
                    ? Import(uri.AbsoluteUri)
                    : Url(uri.AbsoluteUri);
            }

            if (!_single)
            {
                var relative =
                    RelPath(ownerDir ?? "", asset.LocalPath) + uri.Fragment;

                return isImport ? Import(relative) : Url(relative);
            }

            if (isImport)
            {
                // ฝัง CSS ที่ @import ลงไปตรงนั้นเลย (กันวนลูปด้วย stack)
                if (asset.Kind != AssetKind.Css || stack.Contains(asset.Key))
                    return Import(uri.AbsoluteUri);

                var next = new HashSet<string>(stack) { asset.Key };

                var inner = await RewriteCssAsync(
                    DecodeUtf8(asset.Bytes), asset.FinalUri, null, next);

                var media = tail.Trim();

                return media.Length == 0
                    ? inner
                    : "@media " + media + " {\n" + inner + "\n}";
            }

            return Url(ToDataUri(asset) + uri.Fragment);
        }

        private async Task ProcessCssFileAsync(Asset asset)
        {
            try
            {
                var slash = asset.LocalPath.LastIndexOf('/');
                var dir = slash < 0 ? "" : asset.LocalPath[..slash];

                var css = DecodeUtf8(asset.Bytes);

                var rewritten = await RewriteCssAsync(
                    css,
                    asset.FinalUri,
                    dir,
                    new HashSet<string> { asset.Key });

                if (rewritten != css)
                {
                    var path = Path.Combine(
                        _out,
                        asset.LocalPath.Replace('/', Path.DirectorySeparatorChar));

                    await File.WriteAllTextAsync(
                        path, rewritten, new UTF8Encoding(false), _ct);
                }
            }
            catch (OperationCanceledException) when (_ct.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                // ถ้าแก้พาธไม่สำเร็จ ไฟล์ CSS ต้นฉบับที่บันทึกไว้แล้วจะยังคงอยู่
            }
        }

        // -------------------- ดาวน์โหลดไฟล์ --------------------

        private Task<Asset?> GetAssetAsync(Uri uri)
        {
            var clean = new UriBuilder(uri) { Fragment = "" }.Uri;
            var key = clean.AbsoluteUri;

            return _assets.GetOrAdd(
                key,
                k => new Lazy<Task<Asset?>>(() => LoadAssetAsync(clean, k))
            ).Value;
        }

        private void Report(string message)
        {
            if (_progress == null)
                return;

            int percent;

            lock (_progressLock)
            {
                var total = Math.Max(_total, 1);
                var value = 15 + (int)(80.0 * Math.Min(_done, total) / total);

                _lastPercent = Math.Max(_lastPercent, Math.Min(value, 95));
                percent = _lastPercent;
            }

            _progress.Report((percent, message));
        }

        private async Task<(byte[]? bytes, Uri finalUri, string mime)>
            DownloadBytesAsync(Uri uri)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.Referrer = _pageUri;

            using var response = await Http.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, _ct);

            var finalUri = response.RequestMessage?.RequestUri ?? uri;
            var mime = response.Content.Headers.ContentType?.MediaType ?? "";

            if (!response.IsSuccessStatusCode)
                return (null, finalUri, mime);

            if (response.Content.Headers.ContentLength is long length &&
                length > MaxFileSize)
            {
                return (null, finalUri, mime);
            }

            await using var stream =
                await response.Content.ReadAsStreamAsync(_ct);

            using var memory = new MemoryStream();
            var buffer = new byte[81920];
            int read;

            while ((read = await stream.ReadAsync(buffer, _ct)) > 0)
            {
                memory.Write(buffer, 0, read);

                if (memory.Length > MaxFileSize)
                    return (null, finalUri, mime);
            }

            return (memory.ToArray(), finalUri, mime);
        }

        private async Task<Asset?> LoadAssetAsync(Uri uri, string key)
        {
            if (Interlocked.Increment(ref _total) > _maxFiles)
            {
                Interlocked.Decrement(ref _total);
                return null;
            }

            byte[]? bytes = null;
            var finalUri = uri;
            var mime = "";

            try
            {
                await _gate.WaitAsync(_ct);

                try
                {
                    (bytes, finalUri, mime) = await DownloadBytesAsync(uri);
                }
                finally
                {
                    _gate.Release();
                }
            }
            catch (OperationCanceledException) when (_ct.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                bytes = null;
            }

            Interlocked.Increment(ref _done);

            if (bytes == null)
            {
                Interlocked.Increment(ref _failed);
                Report("Failed: " + uri.Host);
                return null;
            }

            var extFromUrl = Path.GetExtension(finalUri.AbsolutePath)
                .ToLowerInvariant();

            if (!ExtensionRegex.IsMatch(extFromUrl))
                extFromUrl = "";

            // เชื่อ Content-Type ก่อน เพราะ URL อย่าง image.php?id=3 หรือ fonts.googleapis.com/css2 ไม่มีนามสกุลจริง
            var ext = MimeToExt.TryGetValue(mime, out var mimeExt)
                ? mimeExt
                : extFromUrl;

            var kind = Classify(mime, ext);

            if (ext.Length == 0)
            {
                ext = kind switch
                {
                    AssetKind.Css => ".css",
                    AssetKind.Js => ".js",
                    _ => ".bin"
                };
            }

            var hash = Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(key))
            )[..8].ToLowerInvariant();

            var folder = FolderFor(kind);

            var fileName = PathSanitizer.FileStem(finalUri.AbsolutePath) +
                           "-" + hash + ext;

            var asset = new Asset(
                key, finalUri, bytes, mime, kind, folder + "/" + fileName);

            if (!_single)
            {
                var directory = Path.Combine(_out, folder);

                Directory.CreateDirectory(directory);

                await File.WriteAllBytesAsync(
                    Path.Combine(directory, fileName), bytes, _ct);

                if (kind == AssetKind.Css)
                {
                    // Task.Run กัน Lazy ถูกเรียกซ้อนในตัวเอง (กรณี CSS import ตัวเอง)
                    _pendingCss.Add(Task.Run(() => ProcessCssFileAsync(asset)));
                }
            }

            Interlocked.Increment(ref _ok);
            Interlocked.Add(ref _bytes, bytes.LongLength);

            Report("Downloaded " + _done + "/" + _total + ": " + fileName);

            return asset;
        }
    }
}
