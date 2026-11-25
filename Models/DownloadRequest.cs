
namespace WebGrabberPro.Models;

public sealed class DownloadRequest
{
    public string Url { get; set; } = string.Empty;

    public string OutputFolder { get; set; } = string.Empty;

    public string Mode { get; set; } = "separated";

    public int Depth { get; set; } = 1;
}

