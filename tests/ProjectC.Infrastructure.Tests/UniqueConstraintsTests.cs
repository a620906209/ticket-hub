using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using ProjectC.Domain.Members;
using ProjectC.Domain.Orders;
using ProjectC.Domain.Venues;
using ProjectC.Infrastructure.Tests.TestSupport;

namespace ProjectC.Infrastructure.Tests;

[Collection(PostgresCollection.Name)]
public class UniqueConstraintsTests
{
    private readonly PostgresFixture _fixture;

    public UniqueConstraintsTests(PostgresFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task AddSeat_WithDuplicateZoneAndNumberInSameSeatMap_ViolatesUniqueIndex()
    {
        await using var dbContext = _fixture.CreateDbContext();

        var venue = new Venue(Guid.NewGuid(), $"Venue {Guid.NewGuid():N}");
        var seatMap = new SeatMap(Guid.NewGuid(), venue.Id);
        seatMap.AddSeat("A", "1");
        dbContext.Venues.Add(venue);
        dbContext.SeatMaps.Add(seatMap);
        await dbContext.SaveChangesAsync();

        // 繞過 Domain 的 AddSeat 唯一性檢查，直接插入重複列，驗證資料庫層級也擋得住。
        await using var duplicateDbContext = _fixture.CreateDbContext();

        var act = () => duplicateDbContext.Database.ExecuteSqlInterpolatedAsync(
            $"""INSERT INTO "Seats" ("Id", "SeatMapId", "ZoneCode", "SeatNumber") VALUES ({Guid.NewGuid()}, {seatMap.Id}, 'A', '1')""");

        await act.Should().ThrowAsync<Exception>();
    }

    [Fact]
    public async Task AddEventSeat_WithDuplicateEventAndSeat_ViolatesUniqueIndex()
    {
        await using var dbContext = _fixture.CreateDbContext();
        var (_, eventSeatIds) = await TicketingTestData.SeedEventWithSeatsAsync(dbContext, seatCount: 1);
        var eventSeat = await TicketingTestData.ReloadAsync(dbContext, eventSeatIds[0]);

        await using var duplicateDbContext = _fixture.CreateDbContext();
        var act = () => duplicateDbContext.Database.ExecuteSqlInterpolatedAsync(
            $"""INSERT INTO "EventSeats" ("Id", "EventId", "SeatId") VALUES ({Guid.NewGuid()}, {eventSeat.EventId}, {eventSeat.SeatId})""");

        await act.Should().ThrowAsync<Exception>();
    }

    [Fact]
    public async Task Order_PersistedStatus_NeverBecomesExpired()
    {
        await using var dbContext = _fixture.CreateDbContext();
        var (eventId, eventSeatIds) = await TicketingTestData.SeedEventWithSeatsAsync(dbContext, seatCount: 1);
        var ticketTypeId = await TicketingTestData.SeedTicketTypeAsync(dbContext, eventId, price: 100m);
        var buyer = Member.Register($"buyer-{Guid.NewGuid():N}@example.com", "Test Buyer", "hash");
        dbContext.Members.Add(buyer);
        await dbContext.SaveChangesAsync();

        // 已經逾期的 Pending 訂單：查詢時 GetStatus(now) 會推導成 Expired，但持久化的 Status 欄位本身不能是 Expired。
        var pastHeldUntilUtc = DateTime.UtcNow.AddMinutes(-10);
        var order = new Order(Guid.NewGuid(), eventId, buyer.Id, pastHeldUntilUtc,
            [new OrderItem(Guid.NewGuid(), ticketTypeId, eventSeatIds[0], 1, 100m)]);

        dbContext.Orders.Add(order);
        await dbContext.SaveChangesAsync();

        await using var readDbContext = _fixture.CreateDbContext();
        var reloaded = await readDbContext.Orders.AsNoTracking().SingleAsync(o => o.Id == order.Id);

        reloaded.Status.Should().Be(OrderStatus.Pending);
        reloaded.GetStatus(DateTime.UtcNow).Should().Be(OrderStatus.Expired);
    }
}
