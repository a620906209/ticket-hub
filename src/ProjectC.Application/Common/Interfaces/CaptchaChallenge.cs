using System.Diagnostics.CodeAnalysis;

namespace ProjectC.Application.Common.Interfaces;

[SuppressMessage("Performance", "CA1819", Justification = "驗證碼圖片的一次性傳輸資料，建立後只讀取並轉成 base64 回應，不會被呼叫端修改。")]
public sealed record CaptchaChallenge(string Token, byte[] ImageBytes);
