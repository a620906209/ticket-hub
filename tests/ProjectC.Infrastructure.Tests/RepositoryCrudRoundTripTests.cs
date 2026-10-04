using System.Globalization;
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

            var organizerId = await OrganizerTestData.SeedApprovedOrganizerAsync(dbContext);
            var @event = new Event(eventId, "CRUD Event", DateTime.UtcNow.AddDays(10), venueId, seatMapId, organizerId);
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
                [new OrderItem(Guid.NewGuid(), ticketTypeId, eventSeatId, 1, ticketType.Price)]);
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

    // event-sales-window tasks 2.4：EF 具現化 Event 時也會執行建構子的 Kind == Utc 檢查，
    // 若 Npgsql 讀回 timestamptz 不是 Utc，所有設定販售期間的活動都會在讀取時丟例外。
    [Fact]
    public async Task Event_WithSalesWindow_RoundTripsValuesWithUtcKind()
    {
        // 取整天：timestamptz 只有微秒精度，避免 tick 截斷造成假性不相等；須早於 helper 的 StartAtUtc（UtcNow + 30 天）。
        var salesStartAtUtc = DateTime.UtcNow.Date.AddDays(1);
        var salesEndAtUtc = DateTime.UtcNow.Date.AddDays(20);
        await using var seedDbContext = _fixture.CreateDbContext();
        var (eventId, _) = await TicketingTestData.SeedEventWithSeatsAsync(
            seedDbContext, seatCount: 1, salesStartAtUtc: salesStartAtUtc, salesEndAtUtc: salesEndAtUtc);

        await using var readDbContext = _fixture.CreateDbContext();
        var reloaded = await new EventRepository(readDbContext).GetByIdAsync(eventId, CancellationToken.None);

        reloaded.Should().NotBeNull();
        reloaded!.SalesStartAtUtc.Should().Be(salesStartAtUtc);
        reloaded.SalesEndAtUtc.Should().Be(salesEndAtUtc);
        reloaded.SalesStartAtUtc!.Value.Kind.Should().Be(DateTimeKind.Utc);
        reloaded.SalesEndAtUtc!.Value.Kind.Should().Be(DateTimeKind.Utc);
    }

    // Npgsql 以 2000-01-01 為基準向零取整（2000 年前往上、之後往下）；Validator 只拒絕次微秒的前提是
    // 整微秒值在基準兩側都原樣寫入讀回（event-sales-window design.md 決策 2「精度」）。
    [Theory]
    [InlineData("1999-06-01T12:34:56.123457Z", "1999-12-31T23:59:59.999999Z")]
    [InlineData("2026-01-01T00:00:00.000001Z", "2026-06-30T12:34:56.654321Z")]
    public async Task Event_WithWholeMicrosecondSalesWindow_RoundTripsExactlyOnBothSidesOf2000(string salesStart, string salesEnd)
    {
        var salesStartAtUtc = DateTime.Parse(salesStart, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);
        var salesEndAtUtc = DateTime.Parse(salesEnd, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);
        await using var seedDbContext = _fixture.CreateDbContext();
        var (eventId, _) = await TicketingTestData.SeedEventWithSeatsAsync(
            seedDbContext, seatCount: 1, salesStartAtUtc: salesStartAtUtc, salesEndAtUtc: salesEndAtUtc);

        await using var readDbContext = _fixture.CreateDbContext();
        var reloaded = await new EventRepository(readDbContext).GetByIdAsync(eventId, CancellationToken.None);

        reloaded.Should().NotBeNull();
        reloaded!.SalesStartAtUtc.Should().Be(salesStartAtUtc);
        reloaded.SalesEndAtUtc.Should().Be(salesEndAtUtc);
        reloaded.SalesStartAtUtc!.Value.Kind.Should().Be(DateTimeKind.Utc);
        reloaded.SalesEndAtUtc!.Value.Kind.Should().Be(DateTimeKind.Utc);
    }
}
