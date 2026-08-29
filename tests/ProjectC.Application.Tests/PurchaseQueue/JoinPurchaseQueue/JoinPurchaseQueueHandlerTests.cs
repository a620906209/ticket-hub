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

    private sealed class Fixture
    {
        public FakeEventRepository EventRepository { get; } = new();
        public FakePurchaseQueueRepository PurchaseQueueRepository { get; } = new();
        public FakeUnitOfWork UnitOfWork { get; } = new();
        public FakeDateTimeProvider DateTimeProvider { get; } = new() { UtcNow = Now };

        public JoinPurchaseQueueHandler CreateHandler() => new(EventRepository, PurchaseQueueRepository, UnitOfWork, DateTimeProvider);

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

        var result = await fixture.CreateHandler().HandleAsync(@event.Id, memberId, CancellationToken.None);

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

        var result = await fixture.CreateHandler().HandleAsync(@event.Id, memberId, CancellationToken.None);

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

        var result = await fixture.CreateHandler().HandleAsync(@event.Id, memberId, CancellationToken.None);

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

        var result = await fixture.CreateHandler().HandleAsync(@event.Id, memberId, CancellationToken.None);

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

        var result = await fixture.CreateHandler().HandleAsync(@event.Id, Guid.NewGuid(), CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Error!.Type.Should().Be(ErrorType.Conflict);
        fixture.PurchaseQueueRepository.Data.Should().BeEmpty();
        fixture.UnitOfWork.BeginTransactionCallCount.Should().Be(0, "未開啟熱門搶購模式時不應該開始交易");
    }

    [Fact]
    public async Task HandleAsync_WhenEventDoesNotExist_ReturnsNotFound()
    {
        var fixture = new Fixture();

        var result = await fixture.CreateHandler().HandleAsync(Guid.NewGuid(), Guid.NewGuid(), CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Error!.Type.Should().Be(ErrorType.NotFound);
        fixture.PurchaseQueueRepository.Data.Should().BeEmpty();
    }
}
