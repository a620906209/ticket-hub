using ProjectC.Domain.Events;
using ProjectC.Domain.Members;
using ProjectC.Domain.PurchaseQueue;
using ProjectC.Domain.Venues;
using ProjectC.Infrastructure.Persistence;

namespace ProjectC.WebApi.Tests.TestSupport;

// purchase-queue-leader-election 測試共用的資料建立邏輯，供本次新增的多個測試檔案共用
// （PurchaseQueueAdmissionServiceLeaderElectionBranchTests／PurchaseQueueAdmissionServiceLeaderElectionTests），
// 不動既有 PurchaseQueueAdmissionServiceTests.cs 的私有方法（各自獨立，避免非必要改動既有已通過審查的測試）。
internal static class PurchaseQueueLeaderElectionTestData
{
    public static async Task<Guid> SeedQueueModeEventAsync(ApplicationDbContext dbContext, int maxConcurrentAdmittedBuyers = 1)
    {
        var venue = new Venue(Guid.NewGuid(), $"Test Venue {Guid.NewGuid():N}");
        var seatMap = new SeatMap(Guid.NewGuid(), venue.Id);
        var @event = new Event(Guid.NewGuid(), "Test Event", DateTime.UtcNow.AddDays(30), venue.Id, seatMap.Id);
        @event.EnableQueueMode();

        dbContext.Venues.Add(venue);
        dbContext.SeatMaps.Add(seatMap);
        dbContext.Events.Add(@event);
        await dbContext.SaveChangesAsync();

        return @event.Id;
    }

    public static async Task<PurchaseQueueEntry> SeedWaitingEntryAsync(ApplicationDbContext dbContext, Guid eventId, DateTime joinedAtUtc)
    {
        var member = Member.Register($"buyer-{Guid.NewGuid():N}@example.com", "Test Buyer", "hash");
        dbContext.Members.Add(member);
        var entry = new PurchaseQueueEntry(Guid.NewGuid(), eventId, member.Id, joinedAtUtc);
        dbContext.PurchaseQueueEntries.Add(entry);
        await dbContext.SaveChangesAsync();

        return entry;
    }

    public static async Task SeedManyWaitingEntriesAsync(ApplicationDbContext dbContext, Guid eventId, int count)
    {
        var now = DateTime.UtcNow;
        for (var i = 0; i < count; i++)
        {
            await SeedWaitingEntryAsync(dbContext, eventId, now.AddMinutes(-30 + i));
        }
    }
}
