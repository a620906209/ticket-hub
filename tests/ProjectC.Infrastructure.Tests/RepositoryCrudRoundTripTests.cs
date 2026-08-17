using FluentAssertions;
using ProjectC.Domain.Events;
using ProjectC.Domain.Members;
using ProjectC.Domain.Orders;
using ProjectC.Domain.Venues;
using ProjectC.Infrastructure.Persistence;
using ProjectC.Infrastructure.Persistence.Repositories;
using ProjectC.Infrastructure.Tests.TestSupport;

namespace ProjectC.Infrastructure.Tests;

[Collection(PostgresCollection.Name)]
public class RepositoryCrudRoundTripTests
{
    private readonly PostgresFixture _fixture;

    public RepositoryCrudRoundTripTests(PostgresFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task Repositories_WriteThenReadBack_ReturnsMatchingData()
    {
        var venueId = Guid.NewGuid();
        var seatMapId = Guid.NewGuid();
        var eventId = Guid.NewGuid();
        var orderId = Guid.NewGuid();
        Guid eventSeatId;
        Guid ticketTypeId;

        // ---- 寫入：全部包在同一筆交易裡，皆透過 Repository + IUnitOfWork（design.md 決策 4）----
        await using (var dbContext = _fixture.CreateDbContext())
        {
            var unitOfWork = new UnitOfWork(dbContext);
            var venueRepo = new VenueRepository(dbContext);
            var seatMapRepo = new SeatMapRepository(dbContext);
            var eventRepo = new EventRepository(dbContext);
            var eventSeatRepo = new EventSeatRepository(dbContext);
            var ticketTypeRepo = new TicketTypeRepository(dbContext);
            var orderRepo = new OrderRepository(dbContext);

            await using var tx = await unitOfWork.BeginTransactionAsync(CancellationToken.None);

            var venue = new Venue(venueId, "CRUD Venue");
            venueRepo.Add(venue);

            var seatMap = new SeatMap(seatMapId, venueId);
            seatMap.AddSeat("A", "1");
            seatMapRepo.Add(seatMap);

            var @event = new Event(eventId, "CRUD Event", DateTime.UtcNow.AddDays(10), venueId, seatMapId);
            eventRepo.Add(@event);

            var eventSeats = @event.CreateEventSeats(seatMap);
            eventSeatRepo.AddRange(eventSeats);
            eventSeatId = eventSeats[0].Id;

            var ticketType = @event.CreateTicketType("A", 500m, seatMap);
            ticketTypeRepo.Add(ticketType);
            ticketTypeId = ticketType.Id;

            var buyer = Member.Register($"buyer-{Guid.NewGuid():N}@example.com", "Test Buyer", "hash");
            dbContext.Members.Add(buyer);

            var order = new Order(orderId, eventId, buyer.Id, DateTime.UtcNow.AddMinutes(10),
                [new OrderItem(Guid.NewGuid(), eventSeatId, ticketType.Price)]);
            orderRepo.Add(order);

            await tx.CommitAsync(CancellationToken.None);
        }

        // ---- 讀回：用全新的 DbContext/Repository instance，確保不是讀到同一個 change tracker 快取 ----
        await using var readDbContext = _fixture.CreateDbContext();
        var readVenueRepo = new VenueRepository(readDbContext);
        var readSeatMapRepo = new SeatMapRepository(readDbContext);
        var readEventRepo = new EventRepository(readDbContext);
        var readEventSeatRepo = new EventSeatRepository(readDbContext);
        var readTicketTypeRepo = new TicketTypeRepository(readDbContext);
        var readOrderRepo = new OrderRepository(readDbContext);

        var reloadedVenue = await readVenueRepo.GetByIdAsync(venueId, CancellationToken.None);
        reloadedVenue.Should().NotBeNull();
        reloadedVenue!.Name.Should().Be("CRUD Venue");

        var reloadedSeatMap = await readSeatMapRepo.GetByIdAsync(seatMapId, CancellationToken.None);
        reloadedSeatMap.Should().NotBeNull();
        reloadedSeatMap!.Seats.Should().ContainSingle(s => s.ZoneCode == "A" && s.SeatNumber == "1");

        var reloadedEvent = await readEventRepo.GetByIdAsync(eventId, CancellationToken.None);
        reloadedEvent.Should().NotBeNull();
        reloadedEvent!.Title.Should().Be("CRUD Event");

        var reloadedEventSeat = await readEventSeatRepo.GetByIdAsync(eventSeatId, CancellationToken.None);
        reloadedEventSeat.Should().NotBeNull();
        reloadedEventSeat!.GetStatus(DateTime.UtcNow).Should().Be(EventSeatStatus.Available);

        var reloadedTicketType = await readTicketTypeRepo.GetByIdAsync(ticketTypeId, CancellationToken.None);
        reloadedTicketType.Should().NotBeNull();
        reloadedTicketType!.Price.Should().Be(500m);

        var reloadedOrder = await readOrderRepo.GetByIdAsync(orderId, CancellationToken.None);
        reloadedOrder.Should().NotBeNull();
        reloadedOrder!.Status.Should().Be(OrderStatus.Pending);
        reloadedOrder.Items.Should().ContainSingle(i => i.EventSeatId == eventSeatId && i.UnitPrice == 500m);
    }

    [Fact]
    public async Task EventSeat_HoldThenReload_PersistsPrivateLockingFields()
    {
        var orderId = Guid.NewGuid();
        var now = DateTime.UtcNow;
        var heldUntilUtc = now.AddMinutes(10);

        await using var seedDbContext = _fixture.CreateDbContext();
        var (_, eventSeatIds) = await TicketingTestData.SeedEventWithSeatsAsync(seedDbContext, seatCount: 1);
        var eventSeatId = eventSeatIds[0];

        await using (var dbContext = _fixture.CreateDbContext())
        {
            var unitOfWork = new UnitOfWork(dbContext);
            var repository = new EventSeatRepository(dbContext);
            await using var tx = await unitOfWork.BeginTransactionAsync(CancellationToken.None);

            var seat = (await repository.GetForUpdateAsync([eventSeatId], CancellationToken.None)).Single();
            seat.Hold(orderId, heldUntilUtc, now);

            await tx.CommitAsync(CancellationToken.None);
        }

        await using var readDbContext = _fixture.CreateDbContext();
        var readRepository = new EventSeatRepository(readDbContext);
        var reloaded = await readRepository.GetByIdAsync(eventSeatId, CancellationToken.None);

        reloaded.Should().NotBeNull();
        reloaded!.IsHeldBy(orderId, now).Should().BeTrue();
        reloaded.GetStatus(now).Should().Be(EventSeatStatus.Held);
    }
}
