using System.Diagnostics.CodeAnalysis;

namespace ProjectC.Infrastructure.Captcha;

[SuppressMessage("Performance", "CA1819", Justification = "驗證碼圖片的一次性傳輸資料，產生後只讀取並轉交給 CaptchaChallenge，不會被呼叫端修改。")]
public sealed record CaptchaImage(string Content, byte[] ImageBytes);

// Infrastructure 內部的技術性測試替身邊界，讓測試能注入假的圖片產生器、控制「圖片產生完成的那一刻
// 讓 token 被取消」這個時間點，與 Application 層介面放置規則無關（captcha-verification design.md 決策 10）。
public interface ICaptchaImageGenerator
{
    CaptchaImage Generate();
}
