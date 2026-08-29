using FluentAssertions;
using ProjectC.Application.Common;
using ProjectC.Application.PurchaseQueue.GetMyQueueStatus;
using ProjectC.Application.Tests.TestSupport;
using ProjectC.Domain.Events;
using ProjectC.Domain.PurchaseQueue;

namespace ProjectC.Application.Tests.PurchaseQueue.GetMyQueueStatus;

public class GetMyQueueStatusHandlerTests
{
    private static readonly DateTime Now = new(2026, 1, 1, 10, 0, 0, DateTimeKind.Utc);

    private sealed class Fixture
    {
        public FakeEventRepository EventRepository { get; } = new();
        public FakePurchaseQueueRepository PurchaseQueueRepository { get; } = new();
        public FakeDateTimeProvider DateTimeProvider { get; } = new() { UtcNow = Now };

        public GetMyQueueStatusHandler CreateHandler() => new(EventRepository, PurchaseQueueRepository, DateTimeProvider);

        public Event SeedEvent(bool isQueueModeEnabled = true)
        {
            var @event = new Event(Guid.NewGuid(), "Concert", Now.AddDays(1), Guid.NewGuid(), Guid.NewGuid());
            if (isQueueModeEnabled) @event.EnableQueueMode();
            EventRepository.Data.Add(@event);
            return @event;
        }
    }

    [Fact]
    public async Task HandleAsync_WhenAdmittedButPastAdmissionExpiresAtUtc_ReturnsExpiredEvenThoughDbStatusIsStillAdmitted()
    {
        var fixture = new Fixture();
        var @event = fixture.SeedEvent();
        var memberId = Guid.NewGuid();
        var entry = new PurchaseQueueEntry(Guid.NewGuid(), @event.Id, memberId, Now.AddMinutes(-20));
        entry.Admit(Now.AddMinutes(-15), Now.AddMinutes(-1));
        fixture.PurchaseQueueRepository.Data.Add(entry);

        var result = await fixture.CreateHandler().HandleAsync(@event.Id, memberId, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value!.Status.Should().Be("Expired");
        entry.Status.Should().Be(PurchaseQueueEntryStatus.Admitted, "查詢本身不落地寫回，落地寫回統一由背景服務／自我修復流程負責");
    }

    [Fact]
    public async Task HandleAsync_WhenWaiting_ReturnsWaitingStatusWithWaitingCount()
    {
        var fixture = new Fixture();
        var @event = fixture.SeedEvent();
        var earlier = new PurchaseQueueEntry(Guid.NewGuid(), @event.Id, Guid.NewGuid(), Now.AddMinutes(-10));
        var memberId = Guid.NewGuid();
        var entry = new PurchaseQueueEntry(Guid.NewGuid(), @event.Id, memberId, Now.AddMinutes(-5));
        fixture.PurchaseQueueRepository.Data.Add(earlier);
        fixture.PurchaseQueueRepository.Data.Add(entry);

        var result = await fixture.CreateHandler().HandleAsync(@event.Id, memberId, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value!.Status.Should().Be("Waiting");
        result.Value.WaitingCount.Should().Be(1);
    }

    [Fact]
    public async Task HandleAsync_WhenAdmittedAndNotExpired_ReturnsAdmittedStatusWithNullWaitingCount()
    {
        var fixture = new Fixture();
        var @event = fixture.SeedEvent();
        var memberId = Guid.NewGuid();
        var entry = new PurchaseQueueEntry(Guid.NewGuid(), @event.Id, memberId, Now.AddMinutes(-10));
        entry.Admit(Now.AddMinutes(-5), Now.AddMinutes(5));
        fixture.PurchaseQueueRepository.Data.Add(entry);

        var result = await fixture.CreateHandler().HandleAsync(@event.Id, memberId, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value!.Status.Should().Be("Admitted");
        result.Value.WaitingCount.Should().BeNull();
    }

    [Fact]
    public async Task HandleAsync_WhenRecordAlreadyMarkedExpired_ReturnsExpiredStatus()
    {
        var fixture = new Fixture();
        var @event = fixture.SeedEvent();
        var memberId = Guid.NewGuid();
        var entry = new PurchaseQueueEntry(Guid.NewGuid(), @event.Id, memberId, Now.AddMinutes(-20));
        entry.Admit(Now.AddMinutes(-15), Now.AddMinutes(-10));
        entry.Expire();
        fixture.PurchaseQueueRepository.Data.Add(entry);

        var result = await fixture.CreateHandler().HandleAsync(@event.Id, memberId, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value!.Status.Should().Be("Expired");
    }

    [Fact]
    public async Task HandleAsync_WhenNeverJoined_ReturnsNotJoinedStatus()
    {
        var fixture = new Fixture();
        var @event = fixture.SeedEvent();

        var result = await fixture.CreateHandler().HandleAsync(@event.Id, Guid.NewGuid(), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value!.Status.Should().Be("NotJoined");
        result.Value.WaitingCount.Should().BeNull();
    }

    [Fact]
    public async Task HandleAsync_WhenOnlyHistoricalRecordIsCompleted_ReturnsNotJoinedStatus()
    {
        var fixture = new Fixture();
        var @event = fixture.SeedEvent();
        var memberId = Guid.NewGuid();
        var completed = new PurchaseQueueEntry(Guid.NewGuid(), @event.Id, memberId, Now.AddDays(-1));
        completed.Admit(Now.AddDays(-1).AddMinutes(1), Now.AddDays(-1).AddMinutes(10));
        completed.Complete();
        fixture.PurchaseQueueRepository.Data.Add(completed);

        var result = await fixture.CreateHandler().HandleAsync(@event.Id, memberId, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value!.Status.Should().Be("NotJoined", "已成功下單的 Completed 紀錄對後續查詢不再有意義，應視同尚未加入排隊");
    }

    [Fact]
    public async Task HandleAsync_WhenEventDoesNotExist_ReturnsNotFound()
    {
        var fixture = new Fixture();

        var result = await fixture.CreateHandler().HandleAsync(Guid.NewGuid(), Guid.NewGuid(), CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Error!.Type.Should().Be(ErrorType.NotFound);
    }

    [Fact]
    public async Task HandleAsync_WhenQueueModeDisabledWhileStillWaiting_ReflectsFalseWithoutAlteringEntry()
    {
        var fixture = new Fixture();
        var @event = fixture.SeedEvent(isQueueModeEnabled: true);
        var memberId = Guid.NewGuid();
        var entry = new PurchaseQueueEntry(Guid.NewGuid(), @event.Id, memberId, Now.AddMinutes(-5));
        fixture.PurchaseQueueRepository.Data.Add(entry);

        @event.DisableQueueMode();

        var result = await fixture.CreateHandler().HandleAsync(@event.Id, memberId, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value!.QueueModeEnabled.Should().BeFalse();
        result.Value.Status.Should().Be("Waiting", "排隊紀錄本身如實回傳，不因活動已關閉熱門搶購模式而竄改或清理");
    }
}
