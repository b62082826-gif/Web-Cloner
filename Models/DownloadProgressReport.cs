
namespace WebGrabberPro.Models;

public sealed class DownloadProgressReport
{
    public int Percent { get; set; }

    public string Message { get; set; } = string.Empty;

    public int CompletedFiles { get; set; }

    public int TotalFiles { get; set; }
}

