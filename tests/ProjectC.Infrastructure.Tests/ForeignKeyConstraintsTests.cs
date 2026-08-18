using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using ProjectC.Domain.Members;
using ProjectC.Infrastructure.Tests.TestSupport;

namespace ProjectC.Infrastructure.Tests;

[Collection(PostgresCollection.Name)]
public class ForeignKeyConstraintsTests
{
    private readonly PostgresFixture _fixture;

    public ForeignKeyConstraintsTests(PostgresFixture fixture)
    {
        _fixture = fixture;
    }

    /// <summary>種一個合法的 Member，回傳其 Id，供 Orders.BuyerId 的 raw SQL insert 使用（Orders.BuyerId 現在是 NOT NULL + FK）。</summary>
    private async Task<Guid> SeedMemberAsync()
    {
        await using var dbContext = _fixture.CreateDbContext();
        var member = Member.Register($"fk-test-{Guid.NewGuid():N}@example.com", "FK Test Member", "hash");
        dbContext.Members.Add(member);
        await dbContext.SaveChangesAsync();
        return member.Id;
    }

    [Fact]
    public async Task InsertSeatMap_WithNonExistentVenueId_ViolatesForeignKey()
    {
        await using var dbContext = _fixture.CreateDbContext();

        var act = () => dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"""INSERT INTO "SeatMaps" ("Id", "VenueId") VALUES ({Guid.NewGuid()}, {Guid.NewGuid()})""");

        await act.Should().ThrowAsync<Exception>("SeatMaps.VenueId 應該有 FK 約束，不存在的 VenueId 不該插入成功");
    }

    [Fact]
    public async Task InsertEvent_WithNonExistentVenueId_ViolatesForeignKey()
    {
        // 準備一個合法的 Venue + SeatMap，讓 SeatMapId 這邊合法，才能確定接下來失敗的原因是 VenueId。
        var venueId = Guid.NewGuid();
        var seatMapId = Guid.NewGuid();
        await using var seedDbContext = _fixture.CreateDbContext();
        await seedDbContext.Database.ExecuteSqlInterpolatedAsync(
            $"""INSERT INTO "Venues" ("Id", "Name") VALUES ({venueId}, 'FK Test Venue')""");
        await seedDbContext.Database.ExecuteSqlInterpolatedAsync(
            $"""INSERT INTO "SeatMaps" ("Id", "VenueId") VALUES ({seatMapId}, {venueId})""");

        await using var dbContext = _fixture.CreateDbContext();
        var act = () => dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"""INSERT INTO "Events" ("Id", "Title", "StartAtUtc", "VenueId", "SeatMapId") VALUES ({Guid.NewGuid()}, 'FK Test Event', {DateTime.UtcNow.AddDays(1)}, {Guid.NewGuid()}, {seatMapId})""");

        await act.Should().ThrowAsync<Exception>("Events.VenueId 應該有 FK 約束，不存在的 VenueId 不該插入成功");
    }

    [Fact]
    public async Task InsertEvent_WithNonExistentSeatMapId_ViolatesForeignKey()
    {
        var venueId = Guid.NewGuid();
        await using var venueDbContext = _fixture.CreateDbContext();
        await venueDbContext.Database.ExecuteSqlInterpolatedAsync(
            $"""INSERT INTO "Venues" ("Id", "Name") VALUES ({venueId}, 'FK Test Venue 2')""");

        await using var dbContext = _fixture.CreateDbContext();
        var act = () => dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"""INSERT INTO "Events" ("Id", "Title", "StartAtUtc", "VenueId", "SeatMapId") VALUES ({Guid.NewGuid()}, 'FK Test Event 2', {DateTime.UtcNow.AddDays(1)}, {venueId}, {Guid.NewGuid()})""");

        await act.Should().ThrowAsync<Exception>("Events.SeatMapId 應該有 FK 約束，不存在的 SeatMapId 不該插入成功");
    }

    [Fact]
    public async Task InsertEvent_WithNonExistentCreatedByMemberId_ViolatesForeignKey()
    {
        var venueId = Guid.NewGuid();
        var seatMapId = Guid.NewGuid();
        await using var seedDbContext = _fixture.CreateDbContext();
        await seedDbContext.Database.ExecuteSqlInterpolatedAsync(
            $"""INSERT INTO "Venues" ("Id", "Name") VALUES ({venueId}, 'Audit FK Test Venue')""");
        await seedDbContext.Database.ExecuteSqlInterpolatedAsync(
            $"""INSERT INTO "SeatMaps" ("Id", "VenueId") VALUES ({seatMapId}, {venueId})""");

        await using var dbContext = _fixture.CreateDbContext();
        var act = () => dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"""INSERT INTO "Events" ("Id", "Title", "StartAtUtc", "VenueId", "SeatMapId", "CreatedByMemberId") VALUES ({Guid.NewGuid()}, 'Audit FK Test Event', {DateTime.UtcNow.AddDays(1)}, {venueId}, {seatMapId}, {Guid.NewGuid()})""");

        await act.Should().ThrowAsync<Exception>("Events.CreatedByMemberId 應該有 FK 約束，不存在的 CreatedByMemberId 不該插入成功");
    }

    [Fact]
    public async Task InsertEventSeat_WithNonExistentEventId_ViolatesForeignKey()
    {
        await using var seedDbContext = _fixture.CreateDbContext();
        var (_, eventSeatIds) = await TicketingTestData.SeedEventWithSeatsAsync(seedDbContext, seatCount: 1);
        var seat = await TicketingTestData.ReloadAsync(seedDbContext, eventSeatIds[0]);

        await using var dbContext = _fixture.CreateDbContext();
        var act = () => dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"""INSERT INTO "EventSeats" ("Id", "EventId", "SeatId") VALUES ({Guid.NewGuid()}, {Guid.NewGuid()}, {seat.SeatId})""");

        await act.Should().ThrowAsync<Exception>("EventSeats.EventId 應該有 FK 約束，不存在的 EventId 不該插入成功");
    }

    [Fact]
    public async Task InsertTicketType_WithNonExistentEventId_ViolatesForeignKey()
    {
        await using var dbContext = _fixture.CreateDbContext();

        var act = () => dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"""INSERT INTO "TicketTypes" ("Id", "EventId", "ZoneCode", "Price") VALUES ({Guid.NewGuid()}, {Guid.NewGuid()}, 'A', 100)""");

        await act.Should().ThrowAsync<Exception>("TicketTypes.EventId 應該有 FK 約束，不存在的 EventId 不該插入成功");
    }

    [Fact]
    public async Task InsertEventSeat_WithNonExistentSeatId_ViolatesForeignKey()
    {
        await using var seedDbContext = _fixture.CreateDbContext();
        var (eventId, _) = await TicketingTestData.SeedEventWithSeatsAsync(seedDbContext, seatCount: 1);

        await using var dbContext = _fixture.CreateDbContext();
        var act = () => dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"""INSERT INTO "EventSeats" ("Id", "EventId", "SeatId") VALUES ({Guid.NewGuid()}, {eventId}, {Guid.NewGuid()})""");

        await act.Should().ThrowAsync<Exception>("EventSeats.SeatId 應該有 FK 約束，不存在的 SeatId 不該插入成功");
    }

    [Fact]
    public async Task InsertOrder_WithNonExistentEventId_ViolatesForeignKey()
    {
        var buyerId = await SeedMemberAsync();
        await using var dbContext = _fixture.CreateDbContext();

        var act = () => dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"""INSERT INTO "Orders" ("Id", "EventId", "BuyerId", "HeldUntilUtc", "Status") VALUES ({Guid.NewGuid()}, {Guid.NewGuid()}, {buyerId}, {DateTime.UtcNow.AddMinutes(10)}, 0)""");

        await act.Should().ThrowAsync<Exception>("Orders.EventId 應該有 FK 約束，不存在的 EventId 不該插入成功");
    }

    [Fact]
    public async Task InsertOrder_WithNonExistentBuyerId_ViolatesForeignKey()
    {
        await using var seedDbContext = _fixture.CreateDbContext();
        var (eventId, _) = await TicketingTestData.SeedEventWithSeatsAsync(seedDbContext, seatCount: 1);

        await using var dbContext = _fixture.CreateDbContext();
        var act = () => dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"""INSERT INTO "Orders" ("Id", "EventId", "BuyerId", "HeldUntilUtc", "Status") VALUES ({Guid.NewGuid()}, {eventId}, {Guid.NewGuid()}, {DateTime.UtcNow.AddMinutes(10)}, 0)""");

        await act.Should().ThrowAsync<Exception>("Orders.BuyerId 應該有 FK 約束，不存在的 BuyerId 不該插入成功");
    }

    [Fact]
    public async Task InsertOrderItem_WithNonExistentEventSeatId_ViolatesForeignKey()
    {
        await using var seedDbContext = _fixture.CreateDbContext();
        var (eventId, _) = await TicketingTestData.SeedEventWithSeatsAsync(seedDbContext, seatCount: 1);
        var buyerId = await SeedMemberAsync();

        var orderId = Guid.NewGuid();
        await using var orderDbContext = _fixture.CreateDbContext();
        await orderDbContext.Database.ExecuteSqlInterpolatedAsync(
            $"""INSERT INTO "Orders" ("Id", "EventId", "BuyerId", "HeldUntilUtc", "Status") VALUES ({orderId}, {eventId}, {buyerId}, {DateTime.UtcNow.AddMinutes(10)}, 0)""");

        await using var dbContext = _fixture.CreateDbContext();
        var act = () => dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"""INSERT INTO "OrderItems" ("Id", "EventSeatId", "UnitPrice", "OrderId") VALUES ({Guid.NewGuid()}, {Guid.NewGuid()}, 100, {orderId})""");

        await act.Should().ThrowAsync<Exception>("OrderItems.EventSeatId 應該有 FK 約束，不存在的 EventSeatId 不該插入成功");
    }
}
