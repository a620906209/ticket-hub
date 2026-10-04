using FluentAssertions;
using ProjectC.Application.Common;
using ProjectC.Application.PurchaseQueue.JoinPurchaseQueue;
using ProjectC.Application.Tests.TestSupport;
using ProjectC.Domain.Events;
using ProjectC.Domain.Members;
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
        public FakeMemberRealNameRepository MemberRealNameRepository { get; } = new();

        public JoinPurchaseQueueHandler CreateHandler() => new(
            EventRepository,
            PurchaseQueueRepository,
            UnitOfWork,
            DateTimeProvider,
            new JoinPurchaseQueueRequestValidator(),
            CaptchaService,
            AdmissionMirror,
            MemberRealNameRepository);

        public Event SeedEvent(bool isQueueModeEnabled = true, bool isRealNameRequired = false,
            DateTime? startAtUtc = null, DateTime? salesStartAtUtc = null, DateTime? salesEndAtUtc = null)
        {
            var @event = new Event(
                Guid.NewGuid(), "Concert", startAtUtc ?? Now.AddDays(1), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), isRealNameRequired: isRealNameRequired,
                salesStartAtUtc: salesStartAtUtc, salesEndAtUtc: salesEndAtUtc);
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

    // ---- 實名閘門（real-name-verification PQ-RN-JOIN-*）----
    // 排隊閘門只是提早告知；未登記者若能排隊，會白白佔用名額、入場後才在下單被擋。

    private static Event CopyWithRealNameRequired(Event source, bool isRealNameRequired)
    {
        var copy = new Event(
            source.Id, source.Title, source.StartAtUtc, source.VenueId, source.SeatMapId, source.OrganizerId,
            isRealNameRequired: isRealNameRequired);
        if (source.IsQueueModeEnabled) copy.EnableQueueMode();
        return copy;
    }

    // PQ-RN-JOIN-001
    [Fact]
    public async Task HandleAsync_WhenRealNameRequiredAndMemberNotRegistered_ReturnsRealNameRequiredBeforeTransaction()
    {
        var fixture = new Fixture();
        var @event = fixture.SeedEvent(isRealNameRequired: true);
        var memberId = Guid.NewGuid();
        using var cancellationTokenSource = new CancellationTokenSource();

        var result = await fixture.CreateHandler().HandleAsync(@event.Id, memberId, ValidRequest, cancellationTokenSource.Token);

        result.Error!.Type.Should().Be(ErrorType.RealNameRequired);
        result.Error.Message.Should().Contain(@event.Id.ToString());
        fixture.UnitOfWork.BeginTransactionCallCount.Should().Be(0);
        fixture.PurchaseQueueRepository.Data.Should().BeEmpty();
        fixture.MemberRealNameRepository.LastGetMemberId.Should().Be(memberId);
        fixture.MemberRealNameRepository.LastGetToken.Should().Be(cancellationTokenSource.Token);
    }

    // PQ-RN-JOIN-002
    [Fact]
    public async Task HandleAsync_WhenRealNameRequiredAndMemberRegistered_CreatesWaitingEntry()
    {
        var fixture = new Fixture();
        var @event = fixture.SeedEvent(isRealNameRequired: true);
        var memberId = Guid.NewGuid();
        fixture.MemberRealNameRepository.Data[memberId] = new MemberRealName("王小明", "1234");

        var result = await fixture.CreateHandler().HandleAsync(@event.Id, memberId, ValidRequest, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        fixture.PurchaseQueueRepository.Data.Should().ContainSingle()
            .Which.Status.Should().Be(PurchaseQueueEntryStatus.Waiting);
        fixture.MemberRealNameRepository.GetCallCount.Should().Be(1);
    }

    // PQ-RN-JOIN-003：驗證碼先於實名，避免未通過驗證碼的請求就能探測會員是否已登記實名。
    [Fact]
    public async Task HandleAsync_WhenCaptchaWrongAndRealNameMissing_ReturnsCaptchaInvalidWithoutQueryingRealName()
    {
        var fixture = new Fixture();
        var @event = fixture.SeedEvent(isRealNameRequired: true);

        var result = await fixture.CreateHandler().HandleAsync(
            @event.Id, Guid.NewGuid(), new JoinPurchaseQueueRequest(FakeCaptchaService.ValidToken, "WRONG"), CancellationToken.None);

        result.Error!.Type.Should().Be(ErrorType.CaptchaInvalid);
        fixture.MemberRealNameRepository.GetCallCount.Should().Be(0);
    }

    // PQ-RN-JOIN-004
    [Fact]
    public async Task HandleAsync_WhenRealNameNotRequiredAndMemberNotRegistered_CreatesWaitingEntryWithoutQueryingRealName()
    {
        var fixture = new Fixture();
        var @event = fixture.SeedEvent();

        var result = await fixture.CreateHandler().HandleAsync(@event.Id, Guid.NewGuid(), ValidRequest, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        fixture.MemberRealNameRepository.GetCallCount.Should().Be(0);
    }

    // PQ-RN-JOIN-005
    [Fact]
    public async Task HandleAsync_WhenEventDoesNotExistAndRealNameMissing_ReturnsNotFoundWithoutQueryingRealName()
    {
        var fixture = new Fixture();

        var result = await fixture.CreateHandler().HandleAsync(Guid.NewGuid(), Guid.NewGuid(), ValidRequest, CancellationToken.None);

        result.Error!.Type.Should().Be(ErrorType.NotFound);
        fixture.MemberRealNameRepository.GetCallCount.Should().Be(0);
    }

    // PQ-RN-JOIN-006
    [Fact]
    public async Task HandleAsync_WhenRealNameRequiredButQueueModeDisabled_ReturnsConflictWithoutQueryingRealName()
    {
        var fixture = new Fixture();
        var @event = fixture.SeedEvent(isQueueModeEnabled: false, isRealNameRequired: true);

        var result = await fixture.CreateHandler().HandleAsync(@event.Id, Guid.NewGuid(), ValidRequest, CancellationToken.None);

        result.Error!.Type.Should().Be(ErrorType.Conflict);
        fixture.MemberRealNameRepository.GetCallCount.Should().Be(0);
    }

    // PQ-RN-JOIN-007：兩次讀取不一致代表 I1 被破壞，不得靜默採信任一方。
    [Fact]
    public async Task HandleAsync_WhenRealNameFlagFlipsFromFalseToTrueBetweenReads_ThrowsWithoutCreatingEntry()
    {
        var fixture = new Fixture();
        var @event = fixture.SeedEvent();
        var memberId = Guid.NewGuid();
        fixture.MemberRealNameRepository.Data[memberId] = new MemberRealName("王小明", "1234");
        fixture.EventRepository.GetForUpdateOverride = _ => CopyWithRealNameRequired(@event, true);

        var act = () => fixture.CreateHandler().HandleAsync(@event.Id, memberId, ValidRequest, CancellationToken.None);

        (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Message.Should().Contain(@event.Id.ToString());
        fixture.PurchaseQueueRepository.Data.Should().BeEmpty();
        fixture.UnitOfWork.LastTransaction!.Committed.Should().BeFalse();
    }

    // PQ-RN-JOIN-008
    [Fact]
    public async Task HandleAsync_WhenRealNameFlagFlipsFromTrueToFalseBetweenReads_ThrowsWithoutCreatingEntry()
    {
        var fixture = new Fixture();
        var @event = fixture.SeedEvent(isRealNameRequired: true);
        var memberId = Guid.NewGuid();
        fixture.MemberRealNameRepository.Data[memberId] = new MemberRealName("王小明", "1234");
        fixture.EventRepository.GetForUpdateOverride = _ => CopyWithRealNameRequired(@event, false);

        var act = () => fixture.CreateHandler().HandleAsync(@event.Id, memberId, ValidRequest, CancellationToken.None);

        (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Message.Should().Contain(@event.Id.ToString());
        fixture.PurchaseQueueRepository.Data.Should().BeEmpty();
        fixture.UnitOfWork.LastTransaction!.Committed.Should().BeFalse();
    }

    // ---- 販售期間（event-sales-window PQ-SALES-JOIN-*）----

    // 已逾時的 Admitted 紀錄：販售期間檢查若放在排隊紀錄查詢之後，這筆會先被轉為 Expired。
    private static PurchaseQueueEntry SeedExpiredAdmittedEntry(Fixture fixture, Guid eventId, Guid memberId)
    {
        var entry = new PurchaseQueueEntry(Guid.NewGuid(), eventId, memberId, Now.AddMinutes(-30));
        entry.Admit(Now.AddMinutes(-20), Now.AddMinutes(-10));
        fixture.PurchaseQueueRepository.Data.Add(entry);
        return entry;
    }

    // PQ-SALES-JOIN-001
    [Fact]
    public async Task HandleAsync_WhenSalesNotOpen_ReturnsSalesNotOpenWithoutAddingEntry()
    {
        var fixture = new Fixture();
        var @event = fixture.SeedEvent(salesStartAtUtc: Now.AddHours(1));

        var result = await fixture.CreateHandler().HandleAsync(@event.Id, Guid.NewGuid(), ValidRequest, CancellationToken.None);

        result.Error!.Type.Should().Be(ErrorType.SalesNotOpen);
        result.Error.Message.Should().Contain(@event.Id.ToString());
        fixture.PurchaseQueueRepository.AddOrGetExistingCallCount.Should().Be(0);
        fixture.UnitOfWork.BeginTransactionCallCount.Should().Be(0);
    }

    // PQ-SALES-JOIN-002
    [Fact]
    public async Task HandleAsync_WhenSalesClosed_ReturnsSalesClosedWithoutAddingEntry()
    {
        var fixture = new Fixture();
        var @event = fixture.SeedEvent(salesEndAtUtc: Now.AddHours(-1));

        var result = await fixture.CreateHandler().HandleAsync(@event.Id, Guid.NewGuid(), ValidRequest, CancellationToken.None);

        result.Error!.Type.Should().Be(ErrorType.SalesClosed);
        result.Error.Message.Should().Contain(@event.Id.ToString());
        fixture.PurchaseQueueRepository.AddOrGetExistingCallCount.Should().Be(0);
    }

    // PQ-SALES-JOIN-003
    [Fact]
    public async Task HandleAsync_WithinSalesWindow_AddsWaitingEntry()
    {
        var fixture = new Fixture();
        var @event = fixture.SeedEvent(salesStartAtUtc: Now.AddHours(-1), salesEndAtUtc: Now.AddHours(1));

        var result = await fixture.CreateHandler().HandleAsync(@event.Id, Guid.NewGuid(), ValidRequest, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        fixture.PurchaseQueueRepository.Data.Should().ContainSingle(e => e.Id == result.Value && e.Status == PurchaseQueueEntryStatus.Waiting);
    }

    // PQ-SALES-JOIN-004：驗證碼仍是第一道檢查，避免未過驗證碼即可探測活動販售狀態。
    [Fact]
    public async Task HandleAsync_WhenCaptchaInvalidAndSalesNotOpen_ReturnsCaptchaInvalid()
    {
        var fixture = new Fixture();
        var @event = fixture.SeedEvent(salesStartAtUtc: Now.AddHours(1));
        var invalidRequest = new JoinPurchaseQueueRequest(FakeCaptchaService.ValidToken, "WRONG");

        var result = await fixture.CreateHandler().HandleAsync(@event.Id, Guid.NewGuid(), invalidRequest, CancellationToken.None);

        result.Error!.Type.Should().Be(ErrorType.CaptchaInvalid);
    }

    // PQ-SALES-JOIN-005：未開賣時是否開啟熱門搶購模式無從得知也無關，先告知未開賣。
    [Fact]
    public async Task HandleAsync_WhenQueueModeDisabledAndSalesNotOpen_ReturnsSalesNotOpen()
    {
        var fixture = new Fixture();
        var @event = fixture.SeedEvent(isQueueModeEnabled: false, salesStartAtUtc: Now.AddHours(1));

        var result = await fixture.CreateHandler().HandleAsync(@event.Id, Guid.NewGuid(), ValidRequest, CancellationToken.None);

        result.Error!.Type.Should().Be(ErrorType.SalesNotOpen);
    }

    // PQ-SALES-JOIN-006
    [Fact]
    public async Task HandleAsync_WhenRealNameMissingAndSalesNotOpen_ReturnsSalesNotOpen()
    {
        var fixture = new Fixture();
        var @event = fixture.SeedEvent(isRealNameRequired: true, salesStartAtUtc: Now.AddHours(1));

        var result = await fixture.CreateHandler().HandleAsync(@event.Id, Guid.NewGuid(), ValidRequest, CancellationToken.None);

        result.Error!.Type.Should().Be(ErrorType.SalesNotOpen);
        fixture.MemberRealNameRepository.GetCallCount.Should().Be(0);
    }

    // PQ-SALES-JOIN-007：停售後不得再改動排隊紀錄（含 Admitted→Expired 自我修復）。
    [Fact]
    public async Task HandleAsync_WhenExpiredAdmittedEntryAndSalesClosed_ReturnsSalesClosedWithoutExpiringEntry()
    {
        var fixture = new Fixture();
        var @event = fixture.SeedEvent(salesEndAtUtc: Now.AddHours(-1));
        var memberId = Guid.NewGuid();
        var entry = SeedExpiredAdmittedEntry(fixture, @event.Id, memberId);

        var result = await fixture.CreateHandler().HandleAsync(@event.Id, memberId, ValidRequest, CancellationToken.None);

        result.Error!.Type.Should().Be(ErrorType.SalesClosed);
        entry.Status.Should().Be(PurchaseQueueEntryStatus.Admitted);
        fixture.PurchaseQueueRepository.GetForUpdateCallCount.Should().Be(0);
        fixture.PurchaseQueueRepository.AddOrGetExistingCallCount.Should().Be(0);
    }

    // PQ-SALES-JOIN-008：等待活動鎖期間跨過停售時點，交易內必須以鎖定後重新取得的 now 判斷。
    [Fact]
    public async Task HandleAsync_WhenSalesClosesWhileWaitingForEventLock_ReturnsSalesClosedWithoutAddingEntry()
    {
        var fixture = new Fixture();
        var salesEndAtUtc = Now.AddMinutes(1);
        var @event = fixture.SeedEvent(salesEndAtUtc: salesEndAtUtc);
        fixture.EventRepository.GetForUpdateOverride = _ =>
        {
            fixture.DateTimeProvider.UtcNow = salesEndAtUtc;
            return @event;
        };

        var result = await fixture.CreateHandler().HandleAsync(@event.Id, Guid.NewGuid(), ValidRequest, CancellationToken.None);

        result.Error!.Type.Should().Be(ErrorType.SalesClosed);
        fixture.UnitOfWork.BeginTransactionCallCount.Should().Be(1, "交易外檢查時仍可售，必須進入交易才會被權威檢查擋下");
        fixture.UnitOfWork.LastTransaction!.Committed.Should().BeFalse();
        fixture.PurchaseQueueRepository.GetForUpdateCallCount.Should().Be(0);
        fixture.PurchaseQueueRepository.AddOrGetExistingCallCount.Should().Be(0);
    }

    // PQ-SALES-JOIN-008：交易內販售期間檢查先於熱門搶購模式重驗，否則已停售活動會回 Conflict。
    [Fact]
    public async Task HandleAsync_WhenClosedInsideTransactionAndQueueModeDisabled_ReturnsSalesClosed()
    {
        var fixture = new Fixture();
        var @event = fixture.SeedEvent();
        var lockedEvent = new Event(@event.Id, @event.Title, @event.StartAtUtc, @event.VenueId, @event.SeatMapId, @event.OrganizerId,
            salesEndAtUtc: Now.AddHours(-1));
        fixture.EventRepository.GetForUpdateOverride = _ => lockedEvent;

        var result = await fixture.CreateHandler().HandleAsync(@event.Id, Guid.NewGuid(), ValidRequest, CancellationToken.None);

        lockedEvent.IsQueueModeEnabled.Should().BeFalse();
        result.Error!.Type.Should().Be(ErrorType.SalesClosed);
        fixture.PurchaseQueueRepository.AddOrGetExistingCallCount.Should().Be(0);
    }

    // PQ-SALES-JOIN-010：既有活動（兩欄位皆 null）在開始前維持可加入排隊。
    [Fact]
    public async Task HandleAsync_WhenSalesWindowNotSetAndEventNotStarted_AddsWaitingEntry()
    {
        var fixture = new Fixture();
        var @event = fixture.SeedEvent(startAtUtc: Now.AddDays(1));

        var result = await fixture.CreateHandler().HandleAsync(@event.Id, Guid.NewGuid(), ValidRequest, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        fixture.PurchaseQueueRepository.Data.Should().ContainSingle(e => e.Id == result.Value && e.Status == PurchaseQueueEntryStatus.Waiting);
    }

    // PQ-SALES-JOIN-011：未設定停售時間時以活動開始時間為停售點；開始時間等於 now 也已停售。
    [Theory]
    [InlineData(0)]
    [InlineData(-60)]
    public async Task HandleAsync_WhenSalesEndIsNullAndEventStartedWithExpiredAdmittedEntry_ReturnsSalesClosed(int startAtOffsetMinutes)
    {
        var fixture = new Fixture();
        var @event = fixture.SeedEvent(startAtUtc: Now.AddMinutes(startAtOffsetMinutes));
        var memberId = Guid.NewGuid();
        var entry = SeedExpiredAdmittedEntry(fixture, @event.Id, memberId);

        var result = await fixture.CreateHandler().HandleAsync(@event.Id, memberId, ValidRequest, CancellationToken.None);

        result.Error!.Type.Should().Be(ErrorType.SalesClosed);
        entry.Status.Should().Be(PurchaseQueueEntryStatus.Admitted);
        fixture.PurchaseQueueRepository.GetForUpdateCallCount.Should().Be(0);
        fixture.PurchaseQueueRepository.AddOrGetExistingCallCount.Should().Be(0);
    }
}
