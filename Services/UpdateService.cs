using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Text;
using System.Text.Json;

namespace WebGrabberPro.Services;

public sealed record UpdateInfo(int Build, string Tag, string AssetUrl, long Size, string Notes);

/// <summary>
/// Checks GitHub Releases for a newer build and swaps the app's files in place.
/// Releases are tagged build-N, where N is the CI run number that is also
/// stamped into the exe as its version (1.0.N).
/// </summary>
public static class UpdateService
{
    private const string Repo = "b62082826-gif/Web-Cloner";
    private const string AssetName = "WebGrabberPro-win-x64.zip";

    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("WebGrabberPro-Updater");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
    }

    public static int CurrentBuild =>
        Math.Max(0, Assembly.GetExecutingAssembly().GetName().Version?.Build ?? 0);

    /// <summary>Returns the newest release if it is newer than this build, otherwise null (also null when offline).</summary>
    public static async Task<UpdateInfo?> CheckAsync()
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));

            using var stream = await Http.GetStreamAsync(
                $"https://api.github.com/repos/{Repo}/releases?per_page=30",
                timeout.Token);

            using var doc = await JsonDocument.ParseAsync(
                stream, cancellationToken: timeout.Token);

            UpdateInfo? best = null;

            foreach (var release in doc.RootElement.EnumerateArray())
            {
                if (release.TryGetProperty("draft", out var draft) &&
                    draft.ValueKind == JsonValueKind.True)
                    continue;

                var tag = release.GetProperty("tag_name").GetString() ?? "";

                if (!tag.StartsWith("build-", StringComparison.Ordinal) ||
                    !int.TryParse(tag.AsSpan(6), out var build))
                    continue;

                if (best != null && build <= best.Build)
                    continue;

                foreach (var asset in release.GetProperty("assets").EnumerateArray())
                {
                    if (asset.GetProperty("name").GetString() != AssetName)
                        continue;

                    best = new UpdateInfo(
                        build,
                        tag,
                        asset.GetProperty("browser_download_url").GetString() ?? "",
                        asset.GetProperty("size").GetInt64(),
                        ExtractNotes(release.TryGetProperty("body", out var body)
                            ? body.GetString()
                            : null));

                    break;
                }
            }

            return best != null && best.Build > CurrentBuild ? best : null;
        }
        catch
        {
            // offline, rate limited, unexpected JSON: just skip the check
            return null;
        }
    }

    /// <summary>First "## " section of the release text, as up to six plain bullet lines.</summary>
    private static string ExtractNotes(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
            return "";

        var lines = new List<string>();
        var inSection = false;

        foreach (var raw in body.Split('\n'))
        {
            var line = raw.Trim();

            if (line.StartsWith("## ", StringComparison.Ordinal))
            {
                if (inSection)
                    break;

                inSection = true;
                continue;
            }

            if (!inSection)
                continue;

            if (line.StartsWith("- ", StringComparison.Ordinal) ||
                line.StartsWith("* ", StringComparison.Ordinal))
            {
                lines.Add(line[2..].Replace("**", "").Trim());

                if (lines.Count == 6)
                    break;
            }
        }

        return string.Join("\n", lines);
    }

    /// <summary>
    /// Downloads and unpacks the update, then starts a small script that waits
    /// for this process to exit, copies the files over and restarts the app.
    /// The caller must close the app right after this returns.
    /// </summary>
    public static async Task DownloadAndLaunchAsync(
        UpdateInfo info,
        int processId,
        IProgress<int> progress,
        CancellationToken token)
    {
        var exePath = Environment.ProcessPath
            ?? throw new InvalidOperationException("Cannot find the running program.");

        var appDir = Path.GetDirectoryName(exePath)
            ?? throw new InvalidOperationException("Cannot find the install folder.");

        EnsureWritable(appDir);

        var work = Path.Combine(Path.GetTempPath(), "WebGrabberUpdate");

        if (Directory.Exists(work))
            Directory.Delete(work, true);

        Directory.CreateDirectory(work);

        var zip = Path.Combine(work, "update.zip");
        var staging = Path.Combine(work, "files");

        using (var response = await Http.GetAsync(
                   info.AssetUrl, HttpCompletionOption.ResponseHeadersRead, token))
        {
            response.EnsureSuccessStatusCode();

            var total = response.Content.Headers.ContentLength ?? info.Size;

            await using var source = await response.Content.ReadAsStreamAsync(token);
            await using var target = File.Create(zip);

            var buffer = new byte[81920];
            long received = 0;
            var last = -1;
            int read;

            while ((read = await source.ReadAsync(buffer, token)) > 0)
            {
                await target.WriteAsync(buffer.AsMemory(0, read), token);
                received += read;

                var percent = total > 0 ? (int)(received * 100 / total) : 0;

                if (percent != last)
                {
                    last = percent;
                    progress.Report(Math.Min(percent, 100));
                }
            }
        }

        await Task.Run(() => ZipFile.ExtractToDirectory(zip, staging, true), token);

        var exeName = Path.GetFileName(exePath);

        if (!File.Exists(Path.Combine(staging, exeName)))
            throw new InvalidOperationException(
                "The update package is missing " + exeName + ".");

        var script = Path.Combine(Path.GetTempPath(), "WebGrabberUpdate.cmd");

        var sb = new StringBuilder();
        sb.AppendLine("@echo off");
        sb.AppendLine(":wait");
        sb.AppendLine($"tasklist /fi \"PID eq {processId}\" 2>nul | find \"{processId}\" >nul");
        sb.AppendLine("if not errorlevel 1 (");
        sb.AppendLine("  timeout /t 1 /nobreak >nul");
        sb.AppendLine("  goto wait");
        sb.AppendLine(")");
        sb.AppendLine("timeout /t 1 /nobreak >nul");
        sb.AppendLine($"xcopy \"{staging}\\*\" \"{appDir}\\\" /e /y /q /i >nul");
        sb.AppendLine($"start \"\" \"{exePath}\"");
        sb.AppendLine($"rmdir /s /q \"{work}\"");
        sb.AppendLine("del \"%~f0\"");

        await File.WriteAllTextAsync(script, sb.ToString(), token);

        Process.Start(new ProcessStartInfo("cmd.exe", $"/c \"\"{script}\"\"")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            WindowStyle = ProcessWindowStyle.Hidden
        });
    }

    private static void EnsureWritable(string folder)
    {
        try
        {
            var probe = Path.Combine(folder, ".write-test-" + Guid.NewGuid().ToString("N"));
            File.WriteAllText(probe, "");
            File.Delete(probe);
        }
        catch
        {
            throw new InvalidOperationException(
                "WebGrabber can't write to its own folder. " +
                "Move it somewhere like Documents and try again.");
        }
    }
}
