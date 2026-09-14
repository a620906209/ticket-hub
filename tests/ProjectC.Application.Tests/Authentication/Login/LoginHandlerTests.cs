using FluentAssertions;
using ProjectC.Application.Authentication.Login;
using ProjectC.Application.Common;
using ProjectC.Application.Tests.TestSupport;
using ProjectC.Domain.Members;

namespace ProjectC.Application.Tests.Authentication.Login;

public class LoginHandlerTests
{
    private readonly FakeApplicationDbContext _dbContext = new();
    private readonly FakePasswordHasher _passwordHasher = new();
    private readonly FakeCaptchaService _captchaService = new();
    private readonly LoginHandler _handler;

    public LoginHandlerTests()
    {
        _handler = new LoginHandler(
            _dbContext,
            _passwordHasher,
            new FakeTokenService(),
            new FakeDateTimeProvider(),
            new AuthOptions(),
            new LoginRequestValidator(),
            _captchaService);
    }

    private Member SeedActiveMember(string email = "user@example.com", string password = "Password123")
    {
        var member = Member.Register(email, "Alice", _passwordHasher.HashPassword(password));
        _dbContext.MemberData.Add(member);
        return member;
    }

    private static LoginRequest ValidRequest(string email, string password)
        => new(email, password, FakeCaptchaService.ValidToken, FakeCaptchaService.ValidAnswer);

    [Fact]
    public async Task HandleAsync_WithCorrectCredentialsAndActiveAccount_ReturnsTokens()
    {
        SeedActiveMember();

        var result = await _handler.HandleAsync(ValidRequest("user@example.com", "Password123"), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value!.AccessToken.Should().NotBeNullOrEmpty();
        result.Value!.RefreshToken.Should().NotBeNullOrEmpty();
        _dbContext.RefreshTokenData.Should().ContainSingle();
    }

    [Fact]
    public async Task HandleAsync_WithWrongPassword_ReturnsUnauthorized()
    {
        SeedActiveMember();

        var result = await _handler.HandleAsync(ValidRequest("user@example.com", "WrongPassword1"), CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Error!.Type.Should().Be(ErrorType.Unauthorized);
    }

    [Fact]
    public async Task HandleAsync_WithUnknownEmail_ReturnsUnauthorized()
    {
        var result = await _handler.HandleAsync(ValidRequest("nobody@example.com", "Password123"), CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Error!.Type.Should().Be(ErrorType.Unauthorized);
    }

    [Fact]
    public async Task HandleAsync_WithDeactivatedAccount_ReturnsForbidden()
    {
        var member = SeedActiveMember();
        member.Deactivate();

        var result = await _handler.HandleAsync(ValidRequest("user@example.com", "Password123"), CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Error!.Type.Should().Be(ErrorType.Forbidden);
    }

    // CAPTCHA-AUTH-001：驗證碼錯誤時回傳可區分的 CaptchaInvalid 錯誤（而非泛用 Validation），
    // 且不繼續進行帳密比對——若檢查順序錯誤、誤先查詢帳密，未知 Email 會回傳 Unauthorized 而非
    // CaptchaInvalid，兩者可觀察行為不同，足以驗證順序（captcha-verification design.md 決策 5、8）。
    // 使用獨立的 ErrorType（比照 QueueAdmissionRequired／InvalidTicketSignature 既有慣例）而非
    // Validation，讓前端能依錯誤類型（而非泛用 400 狀態碼）判斷是否為驗證碼答案錯誤，避免其他語意的
    // 400（例如 Email／密碼欄位驗證失敗）被誤判成驗證碼錯誤。
    [Fact]
    public async Task HandleAsync_WithWrongCaptchaAnswer_ReturnsCaptchaInvalidBeforeCredentialCheck()
    {
        var result = await _handler.HandleAsync(
            new LoginRequest("nobody@example.com", "Password123", FakeCaptchaService.ValidToken, "WRONG"),
            CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Error!.Type.Should().Be(ErrorType.CaptchaInvalid);
    }

    // CAPTCHA-AUTH-002：缺漏 CaptchaToken／CaptchaAnswer 任一欄位時回傳 Validation 錯誤，
    // 且不呼叫 ICaptchaService.VerifyAsync。
    [Theory]
    [InlineData("", "TEST")]
    [InlineData("11111111-1111-1111-1111-111111111111", "")]
    public async Task HandleAsync_WithMissingCaptchaField_ReturnsValidationAndDoesNotCallVerify(string token, string answer)
    {
        var result = await _handler.HandleAsync(new LoginRequest("user@example.com", "Password123", token, answer), CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Error!.Type.Should().Be(ErrorType.Validation);
        _captchaService.VerifyCallCount.Should().Be(0);
        _dbContext.SaveChangesCallCount.Should().Be(0);
    }

    // CAPTCHA-INPUT-001：CaptchaToken 超過 64 字元（trim 後）時回傳 Validation 錯誤，不呼叫 VerifyAsync。
    [Fact]
    public async Task HandleAsync_WithCaptchaTokenExceedingMaxLength_ReturnsValidationAndDoesNotCallVerify()
    {
        var tooLongToken = new string('a', 65);

        var result = await _handler.HandleAsync(
            new LoginRequest("user@example.com", "Password123", tooLongToken, "TEST"),
            CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Error!.Type.Should().Be(ErrorType.Validation);
        _captchaService.VerifyCallCount.Should().Be(0);
        _dbContext.SaveChangesCallCount.Should().Be(0);
    }

    // CAPTCHA-INPUT-002：CaptchaAnswer 超過 16 字元（trim 後）時回傳 Validation 錯誤，不呼叫 VerifyAsync。
    [Fact]
    public async Task HandleAsync_WithCaptchaAnswerExceedingMaxLength_ReturnsValidationAndDoesNotCallVerify()
    {
        var tooLongAnswer = new string('a', 17);

        var result = await _handler.HandleAsync(
            new LoginRequest("user@example.com", "Password123", FakeCaptchaService.ValidToken, tooLongAnswer),
            CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Error!.Type.Should().Be(ErrorType.Validation);
        _captchaService.VerifyCallCount.Should().Be(0);
        _dbContext.SaveChangesCallCount.Should().Be(0);
    }

    // CAPTCHA-INPUT-004：原始輸入含大量前後空白但 trim 後仍在長度上限內，MUST 通過長度驗證、
    // 繼續呼叫 VerifyAsync（驗證 RuleFor lambda 內 trim 順序正確）。注意：這裡只斷言 VerifyAsync
    // 有被呼叫到（證明長度驗證沒有誤擋），不斷言整體結果成功——Handler 把 CaptchaAnswer 原樣（未 trim）
    // 傳給 ICaptchaService.VerifyAsync（design.md 決策 8：trim 後比對是 RedisCaptchaService 內部的
    // 職責，不是 Handler 或 Validator 的），FakeCaptchaService 採精確字串比對，帶空白的原始值不會
    // 命中固定答案，這是測試替身本身的限制，不是 CAPTCHA-INPUT-003（trim 後比對）的驗證範疇——
    // 那條由 RedisCaptchaServiceTests 用真實服務驗證。
    [Fact]
    public async Task HandleAsync_WithCaptchaAnswerPaddedWithinLimitAfterTrim_PassesValidationAndCallsVerify()
    {
        SeedActiveMember();
        var paddedAnswer = new string(' ', 13) + FakeCaptchaService.ValidAnswer;

        await _handler.HandleAsync(
            new LoginRequest("user@example.com", "Password123", FakeCaptchaService.ValidToken, paddedAnswer),
            CancellationToken.None);

        _captchaService.VerifyCallCount.Should().Be(1);
    }

    // CAPTCHA-CANCEL-003：Handler MUST 把自己收到的 CancellationToken 原樣轉傳給 ICaptchaService，
    // 不得改用 CancellationToken.None（design.md 決策 10）。
    [Fact]
    public async Task HandleAsync_WithAlreadyCancelledToken_ThrowsOperationCanceledException()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var act = () => _handler.HandleAsync(ValidRequest("user@example.com", "Password123"), cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }
}
