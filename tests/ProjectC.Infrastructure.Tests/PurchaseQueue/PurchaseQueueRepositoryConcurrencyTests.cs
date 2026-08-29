using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using ProjectC.Domain.Events;
using ProjectC.Domain.Members;
using ProjectC.Domain.PurchaseQueue;
using ProjectC.Domain.Venues;
using ProjectC.Infrastructure.Persistence;
using ProjectC.Infrastructure.Persistence.Repositories;
using ProjectC.Infrastructure.Tests.TestSupport;

namespace ProjectC.Infrastructure.Tests.PurchaseQueue;

// 驗證 PurchaseQueueRepository.AddOrGetExistingAsync 改用 ON CONFLICT DO NOTHING 之後的核心行為
// （rate-limiting-queue design.md 決策 3；tasks.md 12.4a，對應 purchase-queue spec PQ-JOIN-004）：
// 比照既有 OrderServiceConcurrencyTests 的手法，用兩個獨立的 DbContext/Repository instance 模擬併發。
[Collection(PostgresCollection.Name)]
public class PurchaseQueueRepositoryConcurrencyTests
{
    private readonly PostgresFixture _fixture;

    public PurchaseQueueRepositoryConcurrencyTests(PostgresFixture fixture)
    {
        _fixture = fixture;
    }

    private async Task<(Guid EventId, Guid MemberId)> SeedEventAndMemberAsync(ApplicationDbContext dbContext)
    {
        var venue = new Venue(Guid.NewGuid(), $"Test Venue {Guid.NewGuid():N}");
        var seatMap = new SeatMap(Guid.NewGuid(), venue.Id);
        var @event = new Event(Guid.NewGuid(), "Test Event", DateTime.UtcNow.AddDays(30), venue.Id, seatMap.Id);
        var member = Member.Register($"buyer-{Guid.NewGuid():N}@example.com", "Test Buyer", "hash");

        dbContext.Venues.Add(venue);
        dbContext.SeatMaps.Add(seatMap);
        dbContext.Events.Add(@event);
        dbContext.Members.Add(member);
        await dbContext.SaveChangesAsync();

        return (@event.Id, member.Id);
    }

    [Fact]
    public async Task AddOrGetExistingAsync_TwoConcurrentFirstTimeJoins_OnlyOneRowPersistedAndBothCallersCanContinueTheirOwnTransaction()
    {
        await using var seedDbContext = _fixture.CreateDbContext();
        var (eventId, memberId) = await SeedEventAndMemberAsync(seedDbContext);
        var joinedAtUtc = DateTime.UtcNow;

        // PostgreSQL 的 ON CONFLICT 「等待/重新檢查」語意需要對方交易真的 Commit／Rollback 才能解除等待——
        // 若把「取得鎖」與「Commit」拆到 Task.WhenAll 的兩側各自控制（比照 GetForUpdateAsyncTests 那種
        // 「先確認卡住、再手動 Commit」的手法），兩邊會互相卡住彼此的 INSERT，形成測試本身造成的死鎖。
        // 這裡改比照 OrderServiceConcurrencyTests：整個「開交易→呼叫→（後續查詢）→Commit」包在同一個
        // async local function 內一次性 await，讓贏家能先完成 Commit，解除輸家的等待。
        async Task<(PurchaseQueueEntry Result, int FollowUpCount)> JoinAsync()
        {
            await using var dbContext = _fixture.CreateDbContext();
            var repository = new PurchaseQueueRepository(dbContext);
            await using var tx = await dbContext.Database.BeginTransactionAsync();
            var newEntry = new PurchaseQueueEntry(Guid.NewGuid(), eventId, memberId, joinedAtUtc);

            var result = await repository.AddOrGetExistingAsync(newEntry, CancellationToken.None);

            // 核心行為：ON CONFLICT DO NOTHING 讓「撞到既有紀錄」不是例外，交易不會進入 aborted 狀態——
            // 呼叫端（不論輸贏）都能立即在同一交易內繼續查詢／提交，不拋出「current transaction is
            // aborted」之類的錯誤（這是改用 ON CONFLICT 取代 catch-retry 要驗證的核心行為）。
            var followUpCount = await dbContext.PurchaseQueueEntries.AsNoTracking().CountAsync(e => e.EventId == eventId);
            await tx.CommitAsync();

            return (result, followUpCount);
        }

        var taskA = JoinAsync();
        var taskB = JoinAsync();
        var results = await Task.WhenAll(taskA, taskB);

        results[0].Result.Id.Should().Be(results[1].Result.Id, "兩次並發呼叫最終應該回傳同一筆紀錄的 Id，不產生重複紀錄");
        results[0].FollowUpCount.Should().BeGreaterThanOrEqualTo(1, "輸贏雙方都應該能在同一交易內成功執行後續查詢");
        results[1].FollowUpCount.Should().BeGreaterThanOrEqualTo(1, "輸贏雙方都應該能在同一交易內成功執行後續查詢");

        await using var readDbContext = _fixture.CreateDbContext();
        var count = await readDbContext.PurchaseQueueEntries.AsNoTracking()
            .CountAsync(e => e.EventId == eventId && e.MemberId == memberId);
        count.Should().Be(1, "資料庫最終只應該有一筆進行中紀錄，partial unique index 生效");
    }
}
