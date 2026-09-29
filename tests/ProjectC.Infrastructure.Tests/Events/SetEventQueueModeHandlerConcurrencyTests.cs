using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using ProjectC.Application.Common;
using ProjectC.Application.Events.SetEventQueueMode;
using ProjectC.Domain.Events;
using ProjectC.Domain.Venues;
using ProjectC.Infrastructure.Persistence;
using ProjectC.Infrastructure.Persistence.Repositories;
using ProjectC.Infrastructure.Tests.TestSupport;

namespace ProjectC.Infrastructure.Tests.Events;

// purchase-queue-organizer-scoping tasks.md 4.15（design.md Decision 1）：SetEventQueueModeHandler 以 FOR UPDATE 鎖定後
// 才判斷存在性與歸屬，因此每條離開路徑（正常 commit、取消、例外）都必須結束交易並釋放列鎖。
// 「await using 會 rollback」只是對 IUnitOfWork 契約的推論，Npgsql 在取消時的連線處理屬驅動程式行為，須以真實 Postgres 驗證。
[Collection(PostgresCollection.Name)]
public class SetEventQueueModeHandlerConcurrencyTests
{
    private static readonly TimeSpan LockReleaseTimeout = TimeSpan.FromSeconds(5);

    private readonly PostgresFixture _fixture;

    public SetEventQueueModeHandlerConcurrencyTests(PostgresFixture fixture)
    {
        _fixture = fixture;
    }

    private async Task<(Guid EventId, Guid OrganizerId)> SeedQueueModeDisabledEventAsync()
    {
        await using var dbContext = _fixture.CreateDbContext();
        var organizerId = await OrganizerTestData.SeedApprovedOrganizerAsync(dbContext);
        var venue = new Venue(Guid.NewGuid(), "Test Venue");
        var seatMap = new SeatMap(Guid.NewGuid(), venue.Id);
        seatMap.AddSeat("A", "1");
        var @event = new Event(Guid.NewGuid(), "Concert", DateTime.UtcNow.AddDays(1), venue.Id, seatMap.Id, organizerId);
        dbContext.Venues.Add(venue);
        dbContext.SeatMaps.Add(seatMap);
        dbContext.Events.Add(@event);
        await dbContext.SaveChangesAsync();
        return (@event.Id, organizerId);
    }

    private static SetEventQueueModeHandler CreateHandler(ApplicationDbContext dbContext, IEventRepository eventRepository)
        => new(eventRepository, new UnitOfWork(dbContext), new SetEventQueueModeRequestValidator(), new FakeQueryCache());

    private async Task<bool> ReadIsQueueModeEnabledAsync(Guid eventId)
    {
        await using var dbContext = _fixture.CreateDbContext();
        return (await dbContext.Events.AsNoTracking().SingleAsync(e => e.Id == eventId)).IsQueueModeEnabled;
    }

    /// <summary>以獨立連線重新 FOR UPDATE 鎖定並寫入；若先前的交易遺留列鎖，這裡會被阻塞直到逾時。</summary>
    private async Task EnableQueueModeWithIndependentConnectionAsync(Guid eventId)
    {
        await using var dbContext = _fixture.CreateDbContext();
        var eventRepository = new EventRepository(dbContext);
        await using var transaction = await new UnitOfWork(dbContext).BeginTransactionAsync(CancellationToken.None);
        var @event = await eventRepository.GetForUpdateAsync(eventId, CancellationToken.None);
        @event!.EnableQueueMode();
        eventRepository.Update(@event);
        await transaction.CommitAsync(CancellationToken.None);
    }

    // 4.15a [PQ-ADMIN-001／002]
    [Fact]
    public async Task SetQueueMode_WhenEventRowLockedByAnotherTransaction_WaitsAndAppliesAfterCommit()
    {
        var (eventId, organizerId) = await SeedQueueModeDisabledEventAsync();

        await using var lockingDbContext = _fixture.CreateDbContext();
        var lockingRepository = new EventRepository(lockingDbContext);
        await using var lockingTransaction = await lockingDbContext.Database.BeginTransactionAsync();
        var lockedEvent = await lockingRepository.GetForUpdateAsync(eventId, CancellationToken.None);
        lockedEvent!.EnableQueueMode();
        lockingRepository.Update(lockedEvent);
        await lockingDbContext.SaveChangesAsync();

        await using var handlerDbContext = _fixture.CreateDbContext();
        var handler = CreateHandler(handlerDbContext, new EventRepository(handlerDbContext));
        var handlerTask = handler.HandleAsync(eventId, organizerId, new SetEventQueueModeRequest(false), CancellationToken.None);

        var completedFirst = await Task.WhenAny(handlerTask, Task.Delay(TimeSpan.FromMilliseconds(500)));
        completedFirst.Should().NotBeSameAs(handlerTask, "Handler 的 FOR UPDATE 讀取必須被交易 A 的列鎖阻擋");

        await lockingTransaction.CommitAsync();
        var result = await handlerTask.WaitAsync(TimeSpan.FromSeconds(10));

        result.IsSuccess.Should().BeTrue();
        (await ReadIsQueueModeEnabledAsync(eventId)).Should().BeFalse(
            "Handler 的寫入必須序列化在交易 A 提交之後（A 設 true → Handler 設 false），不得被 A 覆寫");
    }

    // 4.15b
    [Fact]
    public async Task SetQueueMode_WhenCancelledAfterAcquiringRowLock_RollsBackAndReleasesLock()
    {
        var (eventId, organizerId) = await SeedQueueModeDisabledEventAsync();
        using var cancellationTokenSource = new CancellationTokenSource();

        await using (var handlerDbContext = _fixture.CreateDbContext())
        {
            var eventRepository = new InterceptingEventRepository(new EventRepository(handlerDbContext))
            {
                AfterGetForUpdate = cancellationTokenSource.Cancel,
            };
            var handler = CreateHandler(handlerDbContext, eventRepository);

            var act = () => handler.HandleAsync(eventId, organizerId, new SetEventQueueModeRequest(true), cancellationTokenSource.Token);

            await act.Should().ThrowAsync<OperationCanceledException>();

            await AssertRolledBackAndRowLockReleasedAsync(eventId);
        }
    }

    // 4.15c
    [Fact]
    public async Task SetQueueMode_WhenExceptionThrownAfterAcquiringRowLock_RollsBackAndReleasesLock()
    {
        var (eventId, organizerId) = await SeedQueueModeDisabledEventAsync();
        var injectedException = new InvalidOperationException("injected after row lock");

        await using (var handlerDbContext = _fixture.CreateDbContext())
        {
            var eventRepository = new InterceptingEventRepository(new EventRepository(handlerDbContext))
            {
                OnUpdate = () => throw injectedException,
            };
            var handler = CreateHandler(handlerDbContext, eventRepository);

            var act = () => handler.HandleAsync(eventId, organizerId, new SetEventQueueModeRequest(true), CancellationToken.None);

            (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Should().BeSameAs(injectedException);

            await AssertRolledBackAndRowLockReleasedAsync(eventId);
        }
    }

    // 4.15d [PQ-ADMIN-008]：歸屬核對不通過的早退路徑同樣在持有列鎖後離開，必須由 Handler 自己的交易釋放鎖
    [Fact]
    public async Task SetQueueMode_ForOtherOrganizerEventAfterAcquiringRowLock_ReturnsNotFoundAndReleasesLock()
    {
        var (eventId, _) = await SeedQueueModeDisabledEventAsync();
        var otherOrganizerId = Guid.NewGuid();

        await using (var handlerDbContext = _fixture.CreateDbContext())
        {
            var handler = CreateHandler(handlerDbContext, new EventRepository(handlerDbContext));

            var result = await handler.HandleAsync(eventId, otherOrganizerId, new SetEventQueueModeRequest(true), CancellationToken.None);

            result.IsSuccess.Should().BeFalse();
            result.Error!.Type.Should().Be(ErrorType.NotFound);
            await AssertRolledBackAndRowLockReleasedAsync(eventId);
        }
    }

    /// <summary>
    /// 必須在 Handler 的 DbContext 仍存活時呼叫：DbContext Dispose 會連帶結束交易並將連線還給連線池，
    /// 若等到 Dispose 之後才檢查，即使 Handler 漏掉 rollback，列鎖也會被釋放，測試將無法偵測。
    /// </summary>
    private async Task AssertRolledBackAndRowLockReleasedAsync(Guid eventId)
    {
        (await ReadIsQueueModeEnabledAsync(eventId)).Should().BeFalse("未 commit 離開的交易必須 rollback，不得落地");
        await EnableQueueModeWithIndependentConnectionAsync(eventId).WaitAsync(LockReleaseTimeout);
        (await ReadIsQueueModeEnabledAsync(eventId)).Should().BeTrue();
    }
}
