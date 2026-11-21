using WebGrabberPro.Models;

namespace WebGrabberPro.Services;

public interface IWebScraperService
{
    /// <param name="mode">"separated" = index.html + โฟลเดอร์ css/js/images/fonts, "single" = ไฟล์ HTML เดียว</param>
    Task<DownloadResult> DownloadAsync(
        string url,
        string outputFolder,
        string mode,
        IProgress<(int percent, string message)>? progress,
        CancellationToken token
    );
}
