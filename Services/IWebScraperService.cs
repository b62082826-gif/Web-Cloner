
namespace WebGrabberPro.Services;

public interface IWebScraperService
{
    Task DownloadAsync(
        string url,
        string outputFolder,
        string mode,
        IProgress<(int percent, string message)>? progress,
        CancellationToken token
    );
}

