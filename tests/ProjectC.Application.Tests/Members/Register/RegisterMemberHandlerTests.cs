using FluentAssertions;
using ProjectC.Application.Common;
using ProjectC.Application.Members.Register;
using ProjectC.Application.Tests.TestSupport;
using ProjectC.Domain.Members;

namespace ProjectC.Application.Tests.Members.Register;

public class RegisterMemberHandlerTests
{
    private readonly FakeApplicationDbContext _dbContext = new();
    private readonly FakeCaptchaService _captchaService = new();
    private readonly RegisterMemberHandler _handler;

    public RegisterMemberHandlerTests()
    {
        _handler = new RegisterMemberHandler(_dbContext, new FakePasswordHasher(), new RegisterMemberRequestValidator(), _captchaService);
    }

    private static RegisterMemberRequest ValidRequest(string email, string password, string displayName = "Alice")
        => new(email, password, displayName, FakeCaptchaService.ValidToken, FakeCaptchaService.ValidAnswer);

    [Fact]
    public async Task HandleAsync_WithUnusedEmailAndStrongPassword_CreatesMember()
    {
        var request = ValidRequest("new@example.com", "Password123");

        var result = await _handler.HandleAsync(request, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        _dbContext.MemberData.Should().ContainSingle(m => m.Email == "new@example.com" && m.IsActive);
    }

    [Fact]
    public async Task HandleAsync_WithEmailAlreadyRegistered_ReturnsConflict()
    {
        _dbContext.MemberData.Add(Member.Register("existing@example.com", "Bob", "hashed:Password123"));
        var request = ValidRequest("existing@example.com", "Password123");

        var result = await _handler.HandleAsync(request, CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Error!.Type.Should().Be(ErrorType.Conflict);
        _dbContext.MemberData.Should().ContainSingle();
    }

    [Fact]
    public async Task HandleAsync_WithWeakPassword_ReturnsValidationError()
    {
        var request = ValidRequest("new@example.com", "short");

        var result = await _handler.HandleAsync(request, CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Error!.Type.Should().Be(ErrorType.Validation);
        _dbContext.MemberData.Should().BeEmpty();
    }

    // CAPTCHA-REG-001：驗證碼錯誤時回傳可區分的 CaptchaInvalid 錯誤（而非泛用 Validation），且不繼續
    // 進行 Email 唯一性檢查——若檢查順序錯誤、誤先查詢唯一性，已存在的 Email 會回傳 Conflict 而非
    // CaptchaInvalid，兩者可觀察行為不同（captcha-verification design.md 決策 5、8）。使用獨立的
    // ErrorType（比照 QueueAdmissionRequired／InvalidTicketSignature 既有慣例）讓前端能依錯誤類型判斷
    // 是否為驗證碼答案錯誤，避免其他語意的 400 被誤判成驗證碼錯誤。
    [Fact]
    public async Task HandleAsync_WithWrongCaptchaAnswer_ReturnsCaptchaInvalidBeforeUniquenessCheck()
    {
        _dbContext.MemberData.Add(Member.Register("existing@example.com", "Bob", "hashed:Password123"));
        var request = new RegisterMemberRequest("existing@example.com", "Password123", "Alice", FakeCaptchaService.ValidToken, "WRONG");

        var result = await _handler.HandleAsync(request, CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Error!.Type.Should().Be(ErrorType.CaptchaInvalid);
    }

    // CAPTCHA-REG-002：缺漏 CaptchaToken／CaptchaAnswer 任一欄位時回傳 Validation 錯誤，
    // 且不呼叫 ICaptchaService.VerifyAsync。
    [Theory]
    [InlineData("", "TEST")]
    [InlineData("11111111-1111-1111-1111-111111111111", "")]
    public async Task HandleAsync_WithMissingCaptchaField_ReturnsValidationAndDoesNotCallVerify(string token, string answer)
    {
        var request = new RegisterMemberRequest("new@example.com", "Password123", "Alice", token, answer);

        var result = await _handler.HandleAsync(request, CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Error!.Type.Should().Be(ErrorType.Validation);
        _captchaService.VerifyCallCount.Should().Be(0);
        _dbContext.MemberData.Should().BeEmpty();
    }

    // CAPTCHA-INPUT-001：CaptchaToken 超過 64 字元（trim 後）時回傳 Validation 錯誤，不呼叫 VerifyAsync。
    [Fact]
    public async Task HandleAsync_WithCaptchaTokenExceedingMaxLength_ReturnsValidationAndDoesNotCallVerify()
    {
        var request = new RegisterMemberRequest("new@example.com", "Password123", "Alice", new string('a', 65), "TEST");

        var result = await _handler.HandleAsync(request, CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Error!.Type.Should().Be(ErrorType.Validation);
        _captchaService.VerifyCallCount.Should().Be(0);
        _dbContext.MemberData.Should().BeEmpty();
    }

    // CAPTCHA-INPUT-002：CaptchaAnswer 超過 16 字元（trim 後）時回傳 Validation 錯誤，不呼叫 VerifyAsync。
    [Fact]
    public async Task HandleAsync_WithCaptchaAnswerExceedingMaxLength_ReturnsValidationAndDoesNotCallVerify()
    {
        var request = new RegisterMemberRequest("new@example.com", "Password123", "Alice", FakeCaptchaService.ValidToken, new string('a', 17));

        var result = await _handler.HandleAsync(request, CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Error!.Type.Should().Be(ErrorType.Validation);
        _captchaService.VerifyCallCount.Should().Be(0);
        _dbContext.MemberData.Should().BeEmpty();
    }

    // CAPTCHA-INPUT-004：原始輸入含大量前後空白但 trim 後仍在長度上限內，MUST 通過長度驗證、
    // 繼續呼叫 VerifyAsync（驗證 RuleFor lambda 內 trim 順序正確）。注意：只斷言 VerifyAsync 有被
    // 呼叫（證明長度驗證沒有誤擋），不斷言整體結果成功——理由見 LoginHandlerTests 同名測試的註解。
    [Fact]
    public async Task HandleAsync_WithCaptchaAnswerPaddedWithinLimitAfterTrim_PassesValidationAndCallsVerify()
    {
        var paddedAnswer = new string(' ', 13) + FakeCaptchaService.ValidAnswer;
        var request = new RegisterMemberRequest("new@example.com", "Password123", "Alice", FakeCaptchaService.ValidToken, paddedAnswer);

        await _handler.HandleAsync(request, CancellationToken.None);

        _captchaService.VerifyCallCount.Should().Be(1);
    }

    // CAPTCHA-CANCEL-003：Handler MUST 把自己收到的 CancellationToken 原樣轉傳給 ICaptchaService，
    // 不得改用 CancellationToken.None（design.md 決策 10）。
    [Fact]
    public async Task HandleAsync_WithAlreadyCancelledToken_ThrowsOperationCanceledException()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var act = () => _handler.HandleAsync(ValidRequest("new@example.com", "Password123"), cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }
}
