namespace WebGrabberPro.Models;

/// <summary>สรุปผลหลังดาวน์โหลดเว็บไซต์เสร็จ</summary>
/// <param name="EntryFile">พาธเต็มของ index.html ที่บันทึกไว้</param>
/// <param name="Files">จำนวนไฟล์ (CSS/JS/รูป/ฟอนต์ ฯลฯ) ที่ดาวน์โหลดสำเร็จ</param>
/// <param name="Failed">จำนวนไฟล์ที่ดาวน์โหลดไม่สำเร็จ (ลิงก์เดิมจะถูกคงไว้เป็น URL เต็ม)</param>
/// <param name="Bytes">ขนาดรวมของไฟล์ที่ดาวน์โหลดมา</param>
public sealed record DownloadResult(
    string EntryFile,
    int Files,
    int Failed,
    long Bytes);
