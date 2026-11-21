using System.Text;

namespace WebGrabberPro.Services;

internal static class PathSanitizer
{
    private const int MaxStemLength = 40;

    /// <summary>
    /// สร้างชื่อไฟล์ (ไม่รวมนามสกุล) ที่ปลอดภัยจากพาธของ URL
    /// ใช้เฉพาะ a-z, A-Z, 0-9, '-' และ '_' เพื่อให้เปิดผ่าน file:// ได้โดยไม่ต้อง encode
    /// </summary>
    public static string FileStem(string urlPath)
    {
        string name;

        try
        {
            name = Path.GetFileNameWithoutExtension(
                Uri.UnescapeDataString(urlPath)
            );
        }
        catch
        {
            name = string.Empty;
        }

        var builder = new StringBuilder();

        foreach (var c in name)
        {
            if (c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or
                (>= '0' and <= '9') or '-' or '_')
            {
                builder.Append(c);
            }
            else
            {
                builder.Append('_');
            }

            if (builder.Length >= MaxStemLength)
                break;
        }

        var stem = builder.ToString().Trim('_');

        return stem.Length == 0 ? "file" : stem;
    }

    /// <summary>แปลงข้อความ (เช่น ชื่อโฮสต์) ให้ใช้เป็นชื่อโฟลเดอร์ได้</summary>
    public static string FolderName(string text)
    {
        var invalid = Path.GetInvalidFileNameChars();

        var cleaned = string.Concat(
            text.Select(c => invalid.Contains(c) ? '_' : c)
        ).Trim();

        return cleaned.Length == 0 ? "website" : cleaned;
    }
}
