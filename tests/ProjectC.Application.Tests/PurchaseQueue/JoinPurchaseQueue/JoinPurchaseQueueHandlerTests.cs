using FluentAssertions;
using ProjectC.Application.Common;
using ProjectC.Application.PurchaseQueue.JoinPurchaseQueue;
using ProjectC.Application.Tests.TestSupport;
using ProjectC.Domain.Events;
using ProjectC.Domain.PurchaseQueue;

namespace ProjectC.Application.Tests.PurchaseQueue.JoinPurchaseQueue;

public class JoinPurchaseQueueHandlerTests
{
    private static readonly DateTime Now = new(2026, 1, 1, 10, 0, 0, DateTimeKind.Utc);

    private static readonly JoinPurchaseQueueRequest ValidRequest =
        new(FakeCaptchaService.ValidToken, FakeCaptchaService.ValidAnswer);

    private sealed class Fixture
    {
        public FakeEventRepository EventRepository { get; } = new();
        public FakePurchaseQueueRepository PurchaseQueueRepository { get; } = new();
        public FakeUnitOfWork UnitOfWork { get; } = new();
        public FakeDateTimeProvider DateTimeProvider { get; } = new() { UtcNow = Now };
        public FakeCaptchaService CaptchaService { get; } = new();
        public FakePurchaseQueueAdmissionMirror AdmissionMirror { get; } = new();

        public JoinPurchaseQueueHandler CreateHandler() => new(
            EventRepository,
            PurchaseQueueRepository,
            UnitOfWork,
            DateTimeProvider,
            new JoinPurchaseQueueRequestValidator(),
            CaptchaService,
            AdmissionMirror);

        public Event SeedEvent(bool isQueueModeEnabled = true)
        {
            var @event = new Event(Guid.NewGuid(), "Concert", Now.AddDays(1), Guid.NewGuid(), Guid.NewGuid());
            if (isQueueModeEnabled) @event.EnableQueueMode();
            EventRepository.Data.Add(@event);
            return @event;
        }
    }

    [Fact]
    public async Task HandleAsync_FirstTimeJoining_CreatesWaitingEntryWithJoinedAtUtc()
    {
        var fixture = new Fixture();
        var @event = fixture.SeedEvent();
        var memberId = Guid.NewGuid();

        var result = await fixture.CreateHandler().HandleAsync(@event.Id, memberId, ValidRequest, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        var entry = fixture.PurchaseQueueRepository.Data.Should().ContainSingle().Subject;
        entry.Id.Should().Be(result.Value);
        entry.EventId.Should().Be(@event.Id);
        entry.MemberId.Should().Be(memberId);
        entry.Status.Should().Be(PurchaseQueueEntryStatus.Waiting);
        entry.JoinedAtUtc.Should().Be(Now);
        fixture.UnitOfWork.LastTransaction!.Committed.Should().BeTrue();
    }

    [Fact]
    public async Task HandleAsync_WhenAlreadyWaiting_ReturnsExistingEntryWithoutCreatingNew()
    {
        var fixture = new Fixture();
        var @event = fixture.SeedEvent();
        var memberId = Guid.NewGuid();
        var existing = new PurchaseQueueEntry(Guid.NewGuid(), @event.Id, memberId, Now.AddMinutes(-5));
        fixture.PurchaseQueueRepository.Data.Add(existing);

        var result = await fixture.CreateHandler().HandleAsync(@event.Id, memberId, ValidRequest, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be(existing.Id);
        fixture.PurchaseQueueRepository.Data.Should().ContainSingle();
    }

    [Fact]
    public async Task HandleAsync_WhenAlreadyAdmittedAndNotExpired_ReturnsExistingEntryWithoutCreatingNew()
    {
        var fixture = new Fixture();
        var @event = fixture.SeedEvent();
        var memberId = Guid.NewGuid();
        var existing = new PurchaseQueueEntry(Guid.NewGuid(), @event.Id, memberId, Now.AddMinutes(-10));
        existing.Admit(Now.AddMinutes(-5), Now.AddMinutes(5));
        fixture.PurchaseQueueRepository.Data.Add(existing);

        var result = await fixture.CreateHandler().HandleAsync(@event.Id, memberId, ValidRequest, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be(existing.Id);
        existing.Status.Should().Be(PurchaseQueueEntryStatus.Admitted, "未逾時的既有資格不應被本次呼叫改變");
        fixture.PurchaseQueueRepository.Data.Should().ContainSingle();
    }

    [Fact]
    public async Task HandleAsync_WhenExistingAdmittedEntryIsExpired_ExpiresItAndCreatesNewWaitingEntry()
    {
        var fixture = new Fixture();
        var @event = fixture.SeedEvent();
        var memberId = Guid.NewGuid();
        var expired = new PurchaseQueueEntry(Guid.NewGuid(), @event.Id, memberId, Now.AddMinutes(-20));
        expired.Admit(Now.AddMinutes(-15), Now.AddMinutes(-1));
        fixture.PurchaseQueueRepository.Data.Add(expired);

        var result = await fixture.CreateHandler().HandleAsync(@event.Id, memberId, ValidRequest, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().NotBe(expired.Id, "逾時後重新加入應建立一筆新紀錄，不是回傳舊紀錄");
        expired.Status.Should().Be(PurchaseQueueEntryStatus.Expired);
        var newEntry = fixture.PurchaseQueueRepository.Data.Single(e => e.Id == result.Value);
        newEntry.Status.Should().Be(PurchaseQueueEntryStatus.Waiting);
        newEntry.JoinedAtUtc.Should().Be(Now);
        fixture.PurchaseQueueRepository.Data.Should().HaveCount(2, "舊的逾時紀錄與新紀錄都應該保留");
    }

    [Fact]
    public async Task HandleAsync_WhenQueueModeIsDisabled_ReturnsConflictAndDoesNotCreateEntry()
    {
        var fixture = new Fixture();
        var @event = fixture.SeedEvent(isQueueModeEnabled: false);

        var result = await fixture.CreateHandler().HandleAsync(@event.Id, Guid.NewGuid(), ValidRequest, CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Error!.Type.Should().Be(ErrorType.Conflict);
        fixture.PurchaseQueueRepository.Data.Should().BeEmpty();
        fixture.UnitOfWork.BeginTransactionCallCount.Should().Be(0, "未開啟熱門搶購模式時不應該開始交易");
    }

    [Fact]
    public async Task HandleAsync_WhenEventDoesNotExist_ReturnsNotFound()
    {
        var fixture = new Fixture();

        var result = await fixture.CreateHandler().HandleAsync(Guid.NewGuid(), Guid.NewGuid(), ValidRequest, CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Error!.Type.Should().Be(ErrorType.NotFound);
        fixture.PurchaseQueueRepository.Data.Should().BeEmpty();
    }

    // CAPTCHA-QUEUE-001：驗證碼錯誤時回傳可區分的 CaptchaInvalid 錯誤（而非泛用 Validation），且不
    // 繼續進行排隊資格檢查——若檢查順序錯誤、誤先查詢活動，不存在的活動會回傳 NotFound 而非
    // CaptchaInvalid，兩者可觀察行為不同（captcha-verification design.md 決策 5、8）。使用獨立的
    // ErrorType（比照 QueueAdmissionRequired／InvalidTicketSignature 既有慣例）讓前端能依錯誤類型判斷
    // 是否為驗證碼答案錯誤，避免其他語意的 400 被誤判成驗證碼錯誤。
    [Fact]
    public async Task HandleAsync_WithWrongCaptchaAnswer_ReturnsCaptchaInvalidBeforeEventLookup()
    {
        var fixture = new Fixture();
        var invalidRequest = new JoinPurchaseQueueRequest(FakeCaptchaService.ValidToken, "WRONG");

        var result = await fixture.CreateHandler().HandleAsync(Guid.NewGuid(), Guid.NewGuid(), invalidRequest, CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Error!.Type.Should().Be(ErrorType.CaptchaInvalid);
        fixture.UnitOfWork.BeginTransactionCallCount.Should().Be(0);
    }

    // CAPTCHA-QUEUE-002：缺漏 CaptchaToken／CaptchaAnswer 任一欄位時回傳 Validation 錯誤，
    // 且不呼叫 ICaptchaService.VerifyAsync。
    [Theory]
    [InlineData("", "TEST")]
    [InlineData("11111111-1111-1111-1111-111111111111", "")]
    public async Task HandleAsync_WithMissingCaptchaField_ReturnsValidationAndDoesNotCallVerify(string token, string answer)
    {
        var fixture = new Fixture();
        var @event = fixture.SeedEvent();

        var result = await fixture.CreateHandler().HandleAsync(@event.Id, Guid.NewGuid(), new JoinPurchaseQueueRequest(token, answer), CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Error!.Type.Should().Be(ErrorType.Validation);
        fixture.CaptchaService.VerifyCallCount.Should().Be(0);
        fixture.UnitOfWork.BeginTransactionCallCount.Should().Be(0);
        fixture.PurchaseQueueRepository.Data.Should().BeEmpty();
    }

    // CAPTCHA-INPUT-001：CaptchaToken 超過 64 字元（trim 後）時回傳 Validation 錯誤，不呼叫 VerifyAsync。
    [Fact]
    public async Task HandleAsync_WithCaptchaTokenExceedingMaxLength_ReturnsValidationAndDoesNotCallVerify()
    {
        var fixture = new Fixture();
        var @event = fixture.SeedEvent();
        var request = new JoinPurchaseQueueRequest(new string('a', 65), "TEST");

        var result = await fixture.CreateHandler().HandleAsync(@event.Id, Guid.NewGuid(), request, CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Error!.Type.Should().Be(ErrorType.Validation);
        fixture.CaptchaService.VerifyCallCount.Should().Be(0);
        fixture.UnitOfWork.BeginTransactionCallCount.Should().Be(0);
    }

    // CAPTCHA-INPUT-002：CaptchaAnswer 超過 16 字元（trim 後）時回傳 Validation 錯誤，不呼叫 VerifyAsync。
    [Fact]
    public async Task HandleAsync_WithCaptchaAnswerExceedingMaxLength_ReturnsValidationAndDoesNotCallVerify()
    {
        var fixture = new Fixture();
        var @event = fixture.SeedEvent();
        var request = new JoinPurchaseQueueRequest(FakeCaptchaService.ValidToken, new string('a', 17));

        var result = await fixture.CreateHandler().HandleAsync(@event.Id, Guid.NewGuid(), request, CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Error!.Type.Should().Be(ErrorType.Validation);
        fixture.CaptchaService.VerifyCallCount.Should().Be(0);
        fixture.UnitOfWork.BeginTransactionCallCount.Should().Be(0);
    }

    // CAPTCHA-INPUT-004：原始輸入含大量前後空白但 trim 後仍在長度上限內，MUST 通過長度驗證、
    // 繼續呼叫 VerifyAsync（驗證 RuleFor lambda 內 trim 順序正確）。注意：只斷言 VerifyAsync 有被
    // 呼叫（證明長度驗證沒有誤擋），不斷言整體結果成功——理由見 LoginHandlerTests 同名測試的註解。
    [Fact]
    public async Task HandleAsync_WithCaptchaAnswerPaddedWithinLimitAfterTrim_PassesValidationAndCallsVerify()
    {
        var fixture = new Fixture();
        var @event = fixture.SeedEvent();
        var paddedAnswer = new string(' ', 13) + FakeCaptchaService.ValidAnswer;
        var request = new JoinPurchaseQueueRequest(FakeCaptchaService.ValidToken, paddedAnswer);

        await fixture.CreateHandler().HandleAsync(@event.Id, Guid.NewGuid(), request, CancellationToken.None);

        fixture.CaptchaService.VerifyCallCount.Should().Be(1);
    }

    // CAPTCHA-CANCEL-003：Handler MUST 把自己收到的 CancellationToken 原樣轉傳給 ICaptchaService，
    // 不得改用 CancellationToken.None（design.md 決策 10）。
    [Fact]
    public async Task HandleAsync_WithAlreadyCancelledToken_ThrowsOperationCanceledException()
    {
        var fixture = new Fixture();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var act = () => fixture.CreateHandler().HandleAsync(Guid.NewGuid(), Guid.NewGuid(), ValidRequest, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }
}
