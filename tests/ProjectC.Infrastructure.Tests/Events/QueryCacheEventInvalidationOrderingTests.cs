using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using ProjectC.Application.Common;
using ProjectC.Application.Events.CreateEvent;
using ProjectC.Application.Events.GetEvents;
using ProjectC.Application.Events.SetEventQueueMode;
using ProjectC.Domain.Events;
using ProjectC.Domain.Members;
using ProjectC.Domain.Venues;
using ProjectC.Infrastructure.Persistence;
using ProjectC.Infrastructure.Persistence.Repositories;
using ProjectC.Infrastructure.Security;
using ProjectC.Infrastructure.Tests.TestSupport;

namespace ProjectC.Infrastructure.Tests.Events;

// query-caching tasks.md 4.3／4.4：驗證「交易提交成功後才觸發快取失效」，須用獨立資料庫連線
// 在 RemoveAsync 回呼內確認已讀到 commit 後的新值（見 tasks.md 第 4 節開頭的回呼手法）。
[Collection(PostgresCollection.Name)]
public class QueryCacheEventInvalidationOrderingTests
{
    private readonly PostgresFixture _fixture;
    private static readonly QueryCacheOptions Options = new() { EventListTtlSeconds = 30, TicketTypesTtlSeconds = 10 };

    public QueryCacheEventInvalidationOrderingTests(PostgresFixture fixture)
    {
        _fixture = fixture;
    }

    private async Task<Guid> SeedMemberAsync()
    {
        await using var dbContext = _fixture.CreateDbContext();
        var member = Member.Register($"{Guid.NewGuid():N}@example.com", "Admin", "hash");
        dbContext.Members.Add(member);
        await dbContext.SaveChangesAsync();
        return member.Id;
    }

    private async Task<(Guid VenueId, Guid SeatMapId)> SeedVenueAndSeatMapAsync()
    {
        await using var dbContext = _fixture.CreateDbContext();
        var venue = new Venue(Guid.NewGuid(), "Test Venue");
        var seatMap = new SeatMap(Guid.NewGuid(), venue.Id);
        seatMap.AddSeat("A", "1");
        dbContext.Venues.Add(venue);
        dbContext.SeatMaps.Add(seatMap);
        await dbContext.SaveChangesAsync();
        return (venue.Id, seatMap.Id);
    }

    private async Task<Guid> SeedEventAsync(Guid venueId, Guid seatMapId)
    {
        await using var dbContext = _fixture.CreateDbContext();
        var @event = new Event(Guid.NewGuid(), "Existing Concert", DateTime.UtcNow.AddDays(1), venueId, seatMapId);
        dbContext.Events.Add(@event);
        await dbContext.SaveChangesAsync();
        return @event.Id;
    }

    // QC-EVT-INV-001（tasks.md 4.3）。
    [Fact]
    public async Task CreateEventHandler_AfterCommit_InvalidatesEventListCache_AndCommitPrecedesInvalidation()
    {
        var (venueId, seatMapId) = await SeedVenueAndSeatMapAsync();
        var memberId = await SeedMemberAsync();
        var queryCache = new FakeQueryCache();

        await using (var readDbContext = _fixture.CreateDbContext())
        {
            var getEventsHandler = new GetEventsHandler(new EventRepository(readDbContext), queryCache, Options);
            await getEventsHandler.HandleAsync(CancellationToken.None);
        }
        queryCache.ContainsKey(GetEventsHandler.CacheKey).Should().BeTrue();

        Event? committedNewEventSeenDuringCallback = null;
        queryCache.OnRemoveAsync = async key =>
        {
            if (key != GetEventsHandler.CacheKey)
            {
                return;
            }

            await using var independentDbContext = _fixture.CreateDbContext();
            committedNewEventSeenDuringCallback =
                await independentDbContext.Events.FirstOrDefaultAsync(e => e.Title == "New Concert");
        };

        Guid newEventId;
        await using (var writeDbContext = _fixture.CreateDbContext())
        {
            var createEventHandler = new CreateEventHandler(
                new VenueRepository(writeDbContext),
                new SeatMapRepository(writeDbContext),
                new EventRepository(writeDbContext),
                new EventSeatRepository(writeDbContext),
                new UnitOfWork(writeDbContext),
                new CreateEventRequestValidator(),
                new SystemDateTimeProvider(),
                queryCache);

            var result = await createEventHandler.HandleAsync(
                memberId,
                new CreateEventRequest("New Concert", DateTime.UtcNow.AddDays(30), venueId, seatMapId),
                CancellationToken.None);

            result.IsSuccess.Should().BeTrue();
            newEventId = result.Value;
        }

        queryCache.ContainsKey(GetEventsHandler.CacheKey).Should().BeFalse();
        committedNewEventSeenDuringCallback.Should().NotBeNull("commit 必須先於 invalidation 發生，否則獨立連線在回呼當下查不到新活動");

        await using var verifyDbContext = _fixture.CreateDbContext();
        var getEventsHandlerAgain = new GetEventsHandler(new EventRepository(verifyDbContext), queryCache, Options);
        var events = await getEventsHandlerAgain.HandleAsync(CancellationToken.None);
        events.Should().Contain(e => e.Id == newEventId);
    }

    // QC-EVT-INV-002（tasks.md 4.4）。
    [Fact]
    public async Task SetEventQueueModeHandler_AfterCommit_InvalidatesEventListCache_AndCommitPrecedesInvalidation()
    {
        var (venueId, seatMapId) = await SeedVenueAndSeatMapAsync();
        var eventId = await SeedEventAsync(venueId, seatMapId);
        var queryCache = new FakeQueryCache();

        await using (var readDbContext = _fixture.CreateDbContext())
        {
            var getEventsHandler = new GetEventsHandler(new EventRepository(readDbContext), queryCache, Options);
            var initialEvents = await getEventsHandler.HandleAsync(CancellationToken.None);
            initialEvents.Should().ContainSingle(e => e.Id == eventId && !e.IsQueueModeEnabled);
        }

        bool? committedIsQueueModeEnabledSeenDuringCallback = null;
        queryCache.OnRemoveAsync = async key =>
        {
            if (key != GetEventsHandler.CacheKey)
            {
                return;
            }

            await using var independentDbContext = _fixture.CreateDbContext();
            var updatedEvent = await independentDbContext.Events.AsNoTracking().SingleAsync(e => e.Id == eventId);
            committedIsQueueModeEnabledSeenDuringCallback = updatedEvent.IsQueueModeEnabled;
        };

        await using (var writeDbContext = _fixture.CreateDbContext())
        {
            var handler = new SetEventQueueModeHandler(
                new EventRepository(writeDbContext), new UnitOfWork(writeDbContext), new SetEventQueueModeRequestValidator(), queryCache);

            var result = await handler.HandleAsync(eventId, new SetEventQueueModeRequest(true), CancellationToken.None);
            result.IsSuccess.Should().BeTrue();
        }

        queryCache.ContainsKey(GetEventsHandler.CacheKey).Should().BeFalse();
        committedIsQueueModeEnabledSeenDuringCallback.Should().BeTrue("commit 必須先於 invalidation 發生");

        await using var verifyDbContext = _fixture.CreateDbContext();
        var getEventsHandlerAgain = new GetEventsHandler(new EventRepository(verifyDbContext), queryCache, Options);
        var events = await getEventsHandlerAgain.HandleAsync(CancellationToken.None);
        events.Should().ContainSingle(e => e.Id == eventId && e.IsQueueModeEnabled);
    }
}
