using WebGrabberPro.Models;

namespace WebGrabberPro.Services;

public interface IWebScraperService
{
    /// <param name="depth">1 = หน้าเดียว, 2-3 = ตามลิงก์ภายในเว็บเดียวกันลงไปกี่ชั้น</param>
    /// <param name="mode">"separated" = index.html + โฟลเดอร์ css/js/images/fonts, "single" = ไฟล์ HTML เดียว</param>
    Task<DownloadResult> DownloadAsync(
        string url,
        string outputFolder,
        string mode,
        int depth,
        IProgress<(int percent, string message)>? progress,
        CancellationToken token
    );
}
