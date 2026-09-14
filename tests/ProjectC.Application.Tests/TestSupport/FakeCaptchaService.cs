using ProjectC.Application.Common.Interfaces;

namespace ProjectC.Application.Tests.TestSupport;

// 固定 token／答案的測試替身（比照 FakeQueryCache／FakeDateTimeProvider 手法），讓測試不需要程式化
// 解出真實 RedisCaptchaService 產生的隨機圖形驗證碼內容（captcha-verification design.md 決策 9）。
// 兩個方法開頭皆呼叫 ThrowIfCancellationRequested，讓既有「傳入已取消 token 應拋例外」的測試手法
// 能驗證 Handler 是否把自己收到的 CancellationToken 轉傳給 ICaptchaService（design.md 決策 10）。
public sealed class FakeCaptchaService : ICaptchaService
{
    public const string ValidToken = "11111111-1111-1111-1111-111111111111";
    public const string ValidAnswer = "TEST";

    public int GenerateCallCount { get; private set; }
    public int VerifyCallCount { get; private set; }

    public Task<CaptchaChallenge> GenerateAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        GenerateCallCount++;
        return Task.FromResult(new CaptchaChallenge(ValidToken, new byte[] { 1, 2, 3 }));
    }

    public Task<bool> VerifyAsync(string token, string answer, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        VerifyCallCount++;
        return Task.FromResult(token == ValidToken && answer == ValidAnswer);
    }
}
