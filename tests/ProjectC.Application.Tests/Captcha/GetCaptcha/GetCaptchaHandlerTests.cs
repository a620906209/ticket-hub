using FluentAssertions;
using ProjectC.Application.Captcha.GetCaptcha;
using ProjectC.Application.Tests.TestSupport;

namespace ProjectC.Application.Tests.Captcha.GetCaptcha;

public class GetCaptchaHandlerTests
{
    private readonly FakeCaptchaService _captchaService = new();
    private readonly GetCaptchaHandler _handler;

    public GetCaptchaHandlerTests()
    {
        _handler = new GetCaptchaHandler(_captchaService);
    }

    // CAPTCHA-GEN-001：呼叫 ICaptchaService.GenerateAsync 並原樣回傳其結果。
    [Fact]
    public async Task HandleAsync_CallsGenerateAsync_AndReturnsTokenAndBase64Image()
    {
        var result = await _handler.HandleAsync(CancellationToken.None);

        result.Token.Should().Be(FakeCaptchaService.ValidToken);
        result.ImageBase64.Should().Be(Convert.ToBase64String(new byte[] { 1, 2, 3 }));
        _captchaService.GenerateCallCount.Should().Be(1);
    }

    // CAPTCHA-CANCEL-003：Handler MUST 把自己收到的 CancellationToken 原樣轉傳給 GenerateAsync，
    // 不得改用 CancellationToken.None（design.md 決策 10）。
    [Fact]
    public async Task HandleAsync_WithAlreadyCancelledToken_ThrowsOperationCanceledException()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var act = () => _handler.HandleAsync(cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }
}
