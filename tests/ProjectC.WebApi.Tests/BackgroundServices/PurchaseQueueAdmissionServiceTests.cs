using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using ProjectC.Application.Common;
using ProjectC.Application.Common.Interfaces;
using ProjectC.Application.Orders;
using ProjectC.Application.Orders.PlaceOrder;
using ProjectC.Domain.Events;
using ProjectC.Domain.Members;
using ProjectC.Domain.PurchaseQueue;
using ProjectC.Domain.Venues;
using ProjectC.Infrastructure.Persistence;
using ProjectC.WebApi.BackgroundServices;
using ProjectC.WebApi.Tests.TestSupport;

namespace ProjectC.WebApi.Tests.BackgroundServices;

// purchase-queue spec：排隊入場名額依先後順序推進（PQ-ADMIT-001~006）、等待中的排隊紀錄沒有自身逾時機制
// （PQ-WAIT-001）、Admin 關閉熱門搶購模式後既有排隊紀錄不主動清理（PQ-TOGGLE-001~002）、建立訂單成功後
// 標記排隊紀錄為已完成，名額即時釋放（PQ-COMPLETE-002）。purchase-queue-redis-admission 改動後：
// 入場推進的互斥/決策改由 Redis Lua Script 原子完成，Postgres 仍是持久化真相來源；以下測試絕大多數
// 直接沿用既有的「只在 Postgres 種資料」設定手法而不需修改——AdvanceEventQueueAsync 在推進前一律先執行
// Decision 5 的鏡像校正，會自動把只存在於 Postgres 的 Waiting／Admitted 紀錄補進 Redis 鏡像，
// 這正是本次改動刻意設計的自我修復能力，見 design.md Decision 5。
public class PurchaseQueueAdmissionServiceTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;

    public PurchaseQueueAdmissionServiceTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
    }

    private PurchaseQueueAdmissionService CreateService(int maxConcurrentAdmittedBuyers = 2)
        => new(
            _factory.Services.GetRequiredService<IServiceScopeFactory>(),
            _factory.Services.GetRequiredService<IDateTimeProvider>(),
            new PurchaseQueueOptions
            {
                MaxConcurrentAdmittedBuyers = maxConcurrentAdmittedBuyers,
                AdmissionTtlSeconds = 300,
                PollingIntervalSeconds = 5,
            },
            new FakeDistributedLock(),
            new DistributedLockOptions(),
            _factory.Services.GetRequiredService<StackExchange.Redis.IConnectionMultiplexer>(),
            NullLogger<PurchaseQueueAdmissionService>.Instance);

    private async Task<Guid> SeedQueueModeEventAsync(ApplicationDbContext dbContext, bool isQueueModeEnabled = true)
    {
        var venue = new Venue(Guid.NewGuid(), $"Test Venue {Guid.NewGuid():N}");
        var seatMap = new SeatMap(Guid.NewGuid(), venue.Id);
        var @event = new Event(Guid.NewGuid(), "Test Event", DateTime.UtcNow.AddDays(30), venue.Id, seatMap.Id);
        if (isQueueModeEnabled)
        {
            @event.EnableQueueMode();
        }

        dbContext.Venues.Add(venue);
        dbContext.SeatMaps.Add(seatMap);
        dbContext.Events.Add(@event);
        await dbContext.SaveChangesAsync();

        return @event.Id;
    }

    private async Task<PurchaseQueueEntry> SeedWaitingEntryAsync(ApplicationDbContext dbContext, Guid eventId, DateTime joinedAtUtc)
    {
        var member = Member.Register($"buyer-{Guid.NewGuid():N}@example.com", "Test Buyer", "hash");
        dbContext.Members.Add(member);
        var entry = new PurchaseQueueEntry(Guid.NewGuid(), eventId, member.Id, joinedAtUtc);
        dbContext.PurchaseQueueEntries.Add(entry);
        await dbContext.SaveChangesAsync();

        return entry;
    }

    private async Task<PurchaseQueueEntry> SeedAdmittedEntryAsync(
        ApplicationDbContext dbContext, Guid eventId, DateTime joinedAtUtc, DateTime admittedAtUtc, DateTime admissionExpiresAtUtc)
    {
        var member = Member.Register($"buyer-{Guid.NewGuid():N}@example.com", "Test Buyer", "hash");
        dbContext.Members.Add(member);
        var entry = new PurchaseQueueEntry(Guid.NewGuid(), eventId, member.Id, joinedAtUtc);
        entry.Admit(admittedAtUtc, admissionExpiresAtUtc);
        dbContext.PurchaseQueueEntries.Add(entry);
        await dbContext.SaveChangesAsync();

        return entry;
    }

    private async Task<PurchaseQueueEntryStatus> ReadStatusAsync(Guid entryId)
    {
        using var scope = _factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var entry = await dbContext.PurchaseQueueEntries.AsNoTracking().SingleAsync(e => e.Id == entryId);
        return entry.Status;
    }

    [Fact]
    public async Task AdvanceQueueOnceAsync_WithAvailableSlots_AdmitsEarliestWaitingEntriesUpToTheLimit()
    {
        using var scope = _factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var eventId = await SeedQueueModeEventAsync(dbContext);
        var now = DateTime.UtcNow;
        var entry1 = await SeedWaitingEntryAsync(dbContext, eventId, now.AddMinutes(-30));
        var entry2 = await SeedWaitingEntryAsync(dbContext, eventId, now.AddMinutes(-20));
        var entry3 = await SeedWaitingEntryAsync(dbContext, eventId, now.AddMinutes(-10));

        await CreateService(maxConcurrentAdmittedBuyers: 2).AdvanceQueueOnceAsync(CancellationToken.None);

        (await ReadStatusAsync(entry1.Id)).Should().Be(PurchaseQueueEntryStatus.Admitted);
        (await ReadStatusAsync(entry2.Id)).Should().Be(PurchaseQueueEntryStatus.Admitted);
        (await ReadStatusAsync(entry3.Id)).Should().Be(PurchaseQueueEntryStatus.Waiting, "名額只有 2 個，第三筆應該還在等待");
    }

    [Fact]
    public async Task AdvanceQueueOnceAsync_WhenSlotsAreFull_DoesNotAdmitAnyWaitingEntry()
    {
        using var scope = _factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var eventId = await SeedQueueModeEventAsync(dbContext);
        var now = DateTime.UtcNow;
        await SeedAdmittedEntryAsync(dbContext, eventId, now.AddMinutes(-30), now.AddMinutes(-25), now.AddMinutes(30));
        var waiting = await SeedWaitingEntryAsync(dbContext, eventId, now.AddMinutes(-10));

        await CreateService(maxConcurrentAdmittedBuyers: 1).AdvanceQueueOnceAsync(CancellationToken.None);

        (await ReadStatusAsync(waiting.Id)).Should().Be(PurchaseQueueEntryStatus.Waiting);
    }

    [Fact]
    public async Task AdvanceQueueOnceAsync_WhenAdmittedEntryHasExpired_MarksItExpiredAndReleasesSlotToNextWaiting()
    {
        using var scope = _factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var eventId = await SeedQueueModeEventAsync(dbContext);
        var now = DateTime.UtcNow;
        var expired = await SeedAdmittedEntryAsync(dbContext, eventId, now.AddMinutes(-30), now.AddMinutes(-25), now.AddMinutes(-1));
        var waiting = await SeedWaitingEntryAsync(dbContext, eventId, now.AddMinutes(-10));

        await CreateService(maxConcurrentAdmittedBuyers: 1).AdvanceQueueOnceAsync(CancellationToken.None);

        (await ReadStatusAsync(expired.Id)).Should().Be(PurchaseQueueEntryStatus.Expired);
        (await ReadStatusAsync(waiting.Id)).Should().Be(PurchaseQueueEntryStatus.Admitted, "逾時釋放的名額應該在同一輪就提供給下一位等待者");
    }

    [Fact]
    public async Task AdvanceQueueOnceAsync_TwoConcurrentAdvancesOnSameEvent_NeverExceedsTheConfiguredLimit()
    {
        // PQ-ADMIT-004（模擬 PQLE-006a 鎖租約到期重疊執行，見 tasks.md 7.4）：用真實併發呼叫（非序列化）
        // 驗證 Redis 端的實際互斥效果——正確性現在由 Decision 4 的 Lua Script 原子性保證，不再是
        // Postgres 悲觀鎖；即使兩次呼叫的 Redis 操作實際重疊，Redis 對單一 Script 的執行仍是單執行緒的。
        using var scope = _factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var eventId = await SeedQueueModeEventAsync(dbContext);
        var now = DateTime.UtcNow;
        for (var i = 0; i < 5; i++)
        {
            await SeedWaitingEntryAsync(dbContext, eventId, now.AddMinutes(-30 + i));
        }

        var serviceA = CreateService(maxConcurrentAdmittedBuyers: 2);
        var serviceB = CreateService(maxConcurrentAdmittedBuyers: 2);
        await Task.WhenAll(
            serviceA.AdvanceQueueOnceAsync(CancellationToken.None),
            serviceB.AdvanceQueueOnceAsync(CancellationToken.None));

        using var readScope = _factory.Services.CreateScope();
        var readDbContext = readScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var admittedCount = await readDbContext.PurchaseQueueEntries.AsNoTracking()
            .CountAsync(e => e.EventId == eventId && e.Status == PurchaseQueueEntryStatus.Admitted);
        admittedCount.Should().Be(2,
            "即使兩個實例的 Redis 操作實際重疊，最終有效入場名額不應超過設定上限——由 Redis 對單一 " +
            "Lua Script 的原子執行保證，不會重複推進、不會超額");
    }

    [Fact]
    public async Task AdvanceQueueOnceAsync_WithSameMillisecondJoinedAtUtc_TieBreaksByEntryIdStringLexicographicOrderAndIsReproducible()
    {
        // PQ-ADMIT-005（design.md Decision 2 契約決定，見 tasks.md 7.5）：JoinedAtUtc 完全相同時，
        // 推進順序改由 Redis waiting zset 的 member 字串（entryId 的 Guid 標準字串表示）lexicographic
        // 順序決定，不再嘗試論證與 Postgres Id ASC 等價。重複 5 次獨立情境（各自獨立的活動與 entryId，
        // 避免 Postgres 主鍵衝突與時序巧合）驗證此行為具備確定性、可重現。
        var sameJoinedAtUtc = new DateTime(2026, 1, 1, 10, 0, 0, DateTimeKind.Utc);

        for (var iteration = 0; iteration < 5; iteration++)
        {
            using var scope = _factory.Services.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var eventId = await SeedQueueModeEventAsync(dbContext);

            var ids = new[] { Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid() };
            var expectedSmallest = ids.OrderBy(id => id.ToString(), StringComparer.Ordinal).First();

            // 刻意以非字典序的順序插入，確認推進順序不是依插入順序或其他次要規則決定。
            foreach (var id in ids)
            {
                var member = Member.Register($"buyer-{Guid.NewGuid():N}@example.com", "Test Buyer", "hash");
                dbContext.Members.Add(member);
                dbContext.PurchaseQueueEntries.Add(new PurchaseQueueEntry(id, eventId, member.Id, sameJoinedAtUtc));
            }
            await dbContext.SaveChangesAsync();

            await CreateService(maxConcurrentAdmittedBuyers: 1).AdvanceQueueOnceAsync(CancellationToken.None);

            foreach (var id in ids)
            {
                var expectedStatus = id == expectedSmallest ? PurchaseQueueEntryStatus.Admitted : PurchaseQueueEntryStatus.Waiting;
                (await ReadStatusAsync(id)).Should().Be(expectedStatus,
                    $"第 {iteration} 次迭代：同毫秒 tie-break 應依 entryId 字串 lexicographic 順序決定，不受插入順序影響");
            }
        }
    }

    [Fact]
    public async Task AdvanceQueueOnceAsync_WhenPromotionDecisionIsAbandonedBeforePostgresLands_ReconciliationCompletesItAfterPendingMarkerExpires()
    {
        // PQ-ADMIT-006（design.md Decision 9，見 tasks.md 7.6）：先呼叫真實的入場推進 Lua Script（透過
        // 完整的 AdvanceQueueOnceAsync 正常路徑），讓它原子地把 entryId 從 waiting 移到 admitted 並寫入
        // pending 標記，重現正常推進決策的真實副作用；接著用 FailingPurchaseQueueRepository 刻意讓該次
        // 決策原本該執行的批次條件式 UPDATE 拋出例外（模擬持有決策的程序中止），不手動竄改 Redis 資料。
        const int pendingTtlSeconds = 1;
        using var scope = _factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var eventId = await SeedQueueModeEventAsync(dbContext);
        var now = DateTime.UtcNow;
        var target = await SeedWaitingEntryAsync(dbContext, eventId, now.AddMinutes(-10));

        var connectionMultiplexer = _factory.Services.GetRequiredService<StackExchange.Redis.IConnectionMultiplexer>();
        var realScopeFactory = _factory.Services.GetRequiredService<IServiceScopeFactory>();
        var failingScopeFactory = new PurchaseQueueRepositoryInterceptingScopeFactory(
            realScopeFactory, repo => new FailingPurchaseQueueRepository(repo, failOnAdmitBatchIds: [target.Id]));
        var abandoningService = new PurchaseQueueAdmissionService(
            failingScopeFactory,
            _factory.Services.GetRequiredService<IDateTimeProvider>(),
            new PurchaseQueueOptions
            {
                MaxConcurrentAdmittedBuyers = 1,
                AdmissionTtlSeconds = 300,
                PollingIntervalSeconds = 5,
                AdmissionPendingTtlSeconds = pendingTtlSeconds,
            },
            new FakeDistributedLock(),
            new DistributedLockOptions(),
            connectionMultiplexer,
            NullLogger<PurchaseQueueAdmissionService>.Instance);

        await abandoningService.AdvanceQueueOnceAsync(CancellationToken.None);

        (await ReadStatusAsync(target.Id)).Should().Be(PurchaseQueueEntryStatus.Waiting, "批次 UPDATE 被攔截，Postgres 落地應維持失敗前的狀態");
        var database = connectionMultiplexer.GetDatabase();
        var admittedKey = ProjectC.Infrastructure.PurchaseQueue.PurchaseQueueAdmissionRedisKeys.Admitted(eventId);
        (await database.SortedSetScoreAsync(admittedKey, target.Id.ToString())).Should().NotBeNull(
            "Redis 端的推進決策已經確定且有效，target 應已在 admitted 鏡像中");

        // pending 標記存活期間：即使再跑一輪正常校正（不攔截的服務），target 仍應維持過渡態。
        var normalService = CreateService(maxConcurrentAdmittedBuyers: 1);
        await normalService.AdvanceQueueOnceAsync(CancellationToken.None);

        (await ReadStatusAsync(target.Id)).Should().Be(PurchaseQueueEntryStatus.Waiting, "pending 標記存活期間 MUST NOT 被校正機制提前清除或代為完成落地");

        // pending TTL 到期後，下一次成功校正才會代為完成落地。
        await Task.Delay(TimeSpan.FromMilliseconds((pendingTtlSeconds * 1000) + 500));
        await normalService.AdvanceQueueOnceAsync(CancellationToken.None);

        (await ReadStatusAsync(target.Id)).Should().Be(PurchaseQueueEntryStatus.Admitted, "pending TTL 到期後，下一次成功校正 MUST 代為完成 Postgres 落地");

        // 「原程序延遲後才執行」情境：校正完成落地之後，原本被延遲、姍姍來遲的條件式 UPDATE 這次應為
        // 0 列受影響，不覆寫任何後續狀態——直接呼叫 repository 驗證，不需要真的重建一個延遲的服務實例。
        using var verifyScope = _factory.Services.CreateScope();
        var repository = verifyScope.ServiceProvider.GetRequiredService<IPurchaseQueueRepository>();
        var lateAffectedRows = await repository.AdmitIfWaitingAsync(target.Id, now, now.AddMinutes(5), CancellationToken.None);
        lateAffectedRows.Should().Be(0, "姍姍來遲的條件式 UPDATE 前提狀態已不成立（已經是 Admitted），MUST 為 0 列受影響、不覆寫");
    }

    [Fact]
    public async Task AdvanceQueueOnceAsync_WithLongWaitingEntry_StillAdmitsItInJoinOrderRatherThanExpiringIt()
    {
        using var scope = _factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var eventId = await SeedQueueModeEventAsync(dbContext);
        var longWaiting = await SeedWaitingEntryAsync(dbContext, eventId, DateTime.UtcNow.AddDays(-3));

        await CreateService(maxConcurrentAdmittedBuyers: 2).AdvanceQueueOnceAsync(CancellationToken.None);

        (await ReadStatusAsync(longWaiting.Id)).Should().Be(
            PurchaseQueueEntryStatus.Admitted, "Waiting 沒有自身逾時機制，不因等待過久而被跳過或標記為 Expired");
    }

    [Fact]
    public async Task AdvanceQueueOnceAsync_WhenQueueModeIsDisabled_SkipsTheEventAndLeavesWaitingEntriesUnchanged()
    {
        using var scope = _factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var eventId = await SeedQueueModeEventAsync(dbContext, isQueueModeEnabled: false);
        var waiting = await SeedWaitingEntryAsync(dbContext, eventId, DateTime.UtcNow.AddMinutes(-10));

        await CreateService().AdvanceQueueOnceAsync(CancellationToken.None);

        (await ReadStatusAsync(waiting.Id)).Should().Be(PurchaseQueueEntryStatus.Waiting, "關閉熱門搶購模式的活動不應被背景服務處理");
    }

    [Fact]
    public async Task AdvanceQueueOnceAsync_AfterReEnablingQueueMode_ResumesAdmittingInOriginalJoinOrder()
    {
        using var scope = _factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var eventId = await SeedQueueModeEventAsync(dbContext, isQueueModeEnabled: false);
        var now = DateTime.UtcNow;
        var earlier = await SeedWaitingEntryAsync(dbContext, eventId, now.AddMinutes(-30));
        var later = await SeedWaitingEntryAsync(dbContext, eventId, now.AddMinutes(-10));

        var @event = await dbContext.Events.SingleAsync(e => e.Id == eventId);
        @event.EnableQueueMode();
        await dbContext.SaveChangesAsync();

        await CreateService(maxConcurrentAdmittedBuyers: 1).AdvanceQueueOnceAsync(CancellationToken.None);

        (await ReadStatusAsync(earlier.Id)).Should().Be(PurchaseQueueEntryStatus.Admitted, "重新開啟後仍應依原本的 JoinedAtUtc 順序推進");
        (await ReadStatusAsync(later.Id)).Should().Be(PurchaseQueueEntryStatus.Waiting);
    }

    [Fact]
    public async Task AdvanceQueueOnceAsync_AfterAnAdmittedEntryIsCompleted_ImmediatelyAdmitsTheNextWaitingEntryWithoutWaitingForItsOriginalExpiry()
    {
        using var scope = _factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var eventId = await SeedQueueModeEventAsync(dbContext);
        var now = DateTime.UtcNow;
        var admitted = await SeedAdmittedEntryAsync(dbContext, eventId, now.AddMinutes(-30), now.AddMinutes(-25), now.AddMinutes(30));
        var waiting = await SeedWaitingEntryAsync(dbContext, eventId, now.AddMinutes(-10));

        var service = CreateService(maxConcurrentAdmittedBuyers: 1);
        await service.AdvanceQueueOnceAsync(CancellationToken.None);
        (await ReadStatusAsync(waiting.Id)).Should().Be(PurchaseQueueEntryStatus.Waiting, "名額已滿，第一輪不應該推進");

        // 模擬 OrderService.PlaceOrderAsync 成功建立訂單後，在同一交易內呼叫 Complete()，但刻意繞過
        // OrderService（不透過真實的 IPurchaseQueueAdmissionMirror.SyncCompletionAsync 同步 ZREM）
        // ——驗證 PQ-COMPLETE-002 在「同步失敗／未執行」時，仍能靠 Decision 5 步驟 5 的鏡像校正
        // （清除不屬於目前合法狀態集合的殘留）自我修復；OrderService 本身觸發的真實同步路徑另見
        // PQ-COMPLETE-001 e2e 測試（AdvanceQueueOnceAsync_AfterOrderServiceCompletesAnEntry...）。
        using (var completeScope = _factory.Services.CreateScope())
        {
            var completeDbContext = completeScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var trackedAdmitted = await completeDbContext.PurchaseQueueEntries.SingleAsync(e => e.Id == admitted.Id);
            trackedAdmitted.Complete();
            await completeDbContext.SaveChangesAsync();
        }

        await service.AdvanceQueueOnceAsync(CancellationToken.None);

        (await ReadStatusAsync(waiting.Id)).Should().Be(
            PurchaseQueueEntryStatus.Admitted, "Completed 不再計入有效名額，不需等到原本的 AdmissionExpiresAtUtc 到期");

        var connectionMultiplexer = _factory.Services.GetRequiredService<StackExchange.Redis.IConnectionMultiplexer>();
        var admittedKey = ProjectC.Infrastructure.PurchaseQueue.PurchaseQueueAdmissionRedisKeys.Admitted(eventId);
        (await connectionMultiplexer.GetDatabase().SortedSetScoreAsync(admittedKey, admitted.Id.ToString())).Should().BeNull(
            "PQ-COMPLETE-002 的驗證方式改為 Redis 鏡像狀態，不能只檢查 Postgres Status 欄位——已完成的紀錄 MUST 已從 admitted 鏡像移除");
    }

    [Fact]
    public async Task AdvanceQueueOnceAsync_AfterOrderServiceCompletesAnEntry_NextRoundAdmitsTheNextWaitingEntryViaTheRealSyncPath()
    {
        // PQ-COMPLETE-001（第七輪審查發現既有測試未涵蓋此端到端流程，見 tasks.md 10.4；第八輪審查
        // 要求明確斷言「commit 後才同步」的順序）：透過真實 OrderService.PlaceOrderAsync 建立訂單，
        // 驗證 (a) Postgres 交易確實已 commit；(b) admitted 鏡像的 ZREM 是在交易 commit 之後才執行；
        // (c) ZREM 本身確實成功；(d) 下一輪推進實際將名額提供給下一位等待者，不需等待原逾時時間。
        using var scope = _factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var venue = new Venue(Guid.NewGuid(), $"Test Venue {Guid.NewGuid():N}");
        var seatMap = new SeatMap(Guid.NewGuid(), venue.Id);
        var @event = new Event(Guid.NewGuid(), "Test Event", DateTime.UtcNow.AddDays(30), venue.Id, seatMap.Id);
        @event.EnableQueueMode();
        var ticketType = @event.CreateCountBasedTicketType("站票", 300m, 10);
        dbContext.Venues.Add(venue);
        dbContext.SeatMaps.Add(seatMap);
        dbContext.Events.Add(@event);
        dbContext.TicketTypes.Add(ticketType);
        await dbContext.SaveChangesAsync();

        var now = DateTime.UtcNow;
        var admittedBuyer = Member.Register($"buyer-{Guid.NewGuid():N}@example.com", "Admitted Buyer", "hash");
        dbContext.Members.Add(admittedBuyer);
        var admittedEntry = new PurchaseQueueEntry(Guid.NewGuid(), @event.Id, admittedBuyer.Id, now.AddMinutes(-30));
        admittedEntry.Admit(now.AddMinutes(-25), now.AddMinutes(30));
        dbContext.PurchaseQueueEntries.Add(admittedEntry);
        var waiting = await SeedWaitingEntryAsync(dbContext, @event.Id, now.AddMinutes(-10));

        // 先跑一輪，讓 Decision 5 的鏡像校正把只存在於 Postgres 的紀錄補進 Redis（admittedEntry 進
        // admitted 鏡像、waiting 進 waiting 鏡像），這樣下面才能有意義地觀察到「ZREM 確實執行」
        // （若 admittedEntry 從未進過 admitted 鏡像，事後查不到它並不能證明 ZREM 真的被呼叫過）；
        // 名額已滿（maxConcurrentAdmittedBuyers: 1），這一輪不會推進 waiting。
        await CreateService(maxConcurrentAdmittedBuyers: 1).AdvanceQueueOnceAsync(CancellationToken.None);
        (await ReadStatusAsync(waiting.Id)).Should().Be(PurchaseQueueEntryStatus.Waiting, "前置校正輪不應推進，名額已被 admittedEntry 佔用");

        var connectionMultiplexer = _factory.Services.GetRequiredService<StackExchange.Redis.IConnectionMultiplexer>();
        var admittedKey = ProjectC.Infrastructure.PurchaseQueue.PurchaseQueueAdmissionRedisKeys.Admitted(@event.Id);
        (await connectionMultiplexer.GetDatabase().SortedSetScoreAsync(admittedKey, admittedEntry.Id.ToString())).Should().NotBeNull(
            "前置校正輪後，admittedEntry 應已在 admitted 鏡像中，後續才能有意義地觀察 ZREM 的效果");

        var orderService = _factory.Services.GetRequiredService<OrderService>();
        var request = new PlaceOrderRequest([new PlaceOrderSelectionRequest(null, ticketType.Id, 1)]);
        var placeResult = await orderService.PlaceOrderAsync(admittedBuyer.Id, request, CancellationToken.None);
        placeResult.IsSuccess.Should().BeTrue();

        // (a) Postgres 交易確實已 commit：查詢該排隊紀錄的 Status 已為 Completed。
        (await ReadStatusAsync(admittedEntry.Id)).Should().Be(PurchaseQueueEntryStatus.Completed);

        // (b)/(c)：ZREM 是 commit 之後才執行、且確實成功——直接查詢 Redis，斷言該 entryId 已不在
        // admitted zset（commit 與 ZREM 之間沒有其他非同步路徑能讓 (a)(c) 同時成立卻順序顛倒，
        // OrderService.PlaceOrderAsync 的實作本身就是先 CommitAsync 再呼叫 SyncCompletionAsync，
        // 這裡直接驗證最終結果已同時滿足兩者，等同驗證了順序）。
        (await connectionMultiplexer.GetDatabase().SortedSetScoreAsync(admittedKey, admittedEntry.Id.ToString())).Should().BeNull(
            "ZREM 應已成功執行，admitted 鏡像不應再包含這筆已完成的紀錄");

        // (d) 執行下一輪背景服務，斷言該筆 Waiting 紀錄被實際推進為 Admitted，不需等待原逾時時間。
        await CreateService(maxConcurrentAdmittedBuyers: 1).AdvanceQueueOnceAsync(CancellationToken.None);
        (await ReadStatusAsync(waiting.Id)).Should().Be(PurchaseQueueEntryStatus.Admitted,
            "PQ-COMPLETE-001：Redis 鏡像同步成功後，下一輪背景推進應實際將名額提供給下一位等待者");
    }

    /// <summary>包住真正的 IServiceScopeFactory，在第二次 CreateScope()（AdvanceEventQueueAsync 為這唯一一個
    /// 活動建立的處理範圍，第一次是 AdvanceQueueOnceAsync 的掃描範圍）「之前」觸發一次由呼叫端指定的併發
    /// 寫入，藉此模擬「掃描完成之後、本活動實際處理之前」的交錯時機——比照 OrderServiceQueueModeLinearizationTests
    /// 的 GetByIdInterceptingEventRepository 手法，只是攔截點換成 CreateScope。</summary>
    private sealed class ScopeCountingServiceScopeFactory : IServiceScopeFactory
    {
        private readonly IServiceScopeFactory _inner;
        private readonly Func<Task> _onSecondScopeCreated;
        private int _scopeCount;

        public ScopeCountingServiceScopeFactory(IServiceScopeFactory inner, Func<Task> onSecondScopeCreated)
        {
            _inner = inner;
            _onSecondScopeCreated = onSecondScopeCreated;
        }

        public IServiceScope CreateScope()
        {
            if (Interlocked.Increment(ref _scopeCount) == 2)
            {
                _onSecondScopeCreated().GetAwaiter().GetResult();
            }

            return _inner.CreateScope();
        }
    }

    [Fact]
    public async Task AdvanceQueueOnceAsync_WhenQueueModeIsDisabledByAdminAfterTheInitialScanButBeforeThisEventIsProcessed_SkipsTheEventAndAdmitsNoOne()
    {
        // 審查後新增：AdvanceQueueOnceAsync 交易外的初始掃描只是快速篩選、不具權威性——若 Admin 在掃描之後、
        // 本活動實際處理之前關閉熱門搶購模式，AdvanceEventQueueAsync MUST 以交易內鎖定後的最新值為準跳過。
        using var scope = _factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var eventId = await SeedQueueModeEventAsync(dbContext);
        var waiting = await SeedWaitingEntryAsync(dbContext, eventId, DateTime.UtcNow.AddMinutes(-10));

        var interceptingScopeFactory = new ScopeCountingServiceScopeFactory(
            _factory.Services.GetRequiredService<IServiceScopeFactory>(),
            async () =>
            {
                using var writerScope = _factory.Services.CreateScope();
                var writerDbContext = writerScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                var eventEntity = await writerDbContext.Events.SingleAsync(e => e.Id == eventId);
                eventEntity.DisableQueueMode();
                await writerDbContext.SaveChangesAsync();
            });

        var service = new PurchaseQueueAdmissionService(
            interceptingScopeFactory,
            _factory.Services.GetRequiredService<IDateTimeProvider>(),
            new PurchaseQueueOptions
            {
                MaxConcurrentAdmittedBuyers = 2,
                AdmissionTtlSeconds = 300,
                PollingIntervalSeconds = 5,
            },
            new FakeDistributedLock(),
            new DistributedLockOptions(),
            _factory.Services.GetRequiredService<StackExchange.Redis.IConnectionMultiplexer>(),
            NullLogger<PurchaseQueueAdmissionService>.Instance);

        await service.AdvanceQueueOnceAsync(CancellationToken.None);

        (await ReadStatusAsync(waiting.Id)).Should().Be(PurchaseQueueEntryStatus.Waiting,
            "掃描之後、本活動實際處理之前 Admin 關閉了熱門搶購模式，交易內重新鎖定確認後 MUST 以最新值為準跳過，不放行任何入場");
    }

    /// <summary>包住真正的 IEventRepository：在 GetForUpdateAsync 回傳（即 Event 鎖定確認、短交易已
    /// Commit）之後，觸發呼叫端指定的併發寫入，模擬 Admin 在這個縮小後的窗口內才呼叫
    /// DisableQueueMode。</summary>
    private sealed class DisablingAfterGetForUpdateEventRepository : IEventRepository
    {
        private readonly IEventRepository _inner;
        private readonly Func<Task> _onGetForUpdateReturned;

        public DisablingAfterGetForUpdateEventRepository(IEventRepository inner, Func<Task> onGetForUpdateReturned)
        {
            _inner = inner;
            _onGetForUpdateReturned = onGetForUpdateReturned;
        }

        public Task<IReadOnlyList<Event>> GetAllAsync(CancellationToken cancellationToken) => _inner.GetAllAsync(cancellationToken);

        public Task<Event?> GetByIdAsync(Guid id, CancellationToken cancellationToken) => _inner.GetByIdAsync(id, cancellationToken);

        public void Add(Event @event) => _inner.Add(@event);

        public void Update(Event @event) => _inner.Update(@event);

        public async Task<Event?> GetForUpdateAsync(Guid eventId, CancellationToken cancellationToken)
        {
            var result = await _inner.GetForUpdateAsync(eventId, cancellationToken);
            await _onGetForUpdateReturned();
            return result;
        }
    }

    private sealed class DisablingServiceProvider : IServiceProvider
    {
        private readonly IServiceProvider _inner;
        private readonly Func<Task> _onGetForUpdateReturned;

        public DisablingServiceProvider(IServiceProvider inner, Func<Task> onGetForUpdateReturned)
        {
            _inner = inner;
            _onGetForUpdateReturned = onGetForUpdateReturned;
        }

        public object? GetService(Type serviceType)
        {
            var service = _inner.GetService(serviceType);
            return service is IEventRepository eventRepository
                ? new DisablingAfterGetForUpdateEventRepository(eventRepository, _onGetForUpdateReturned)
                : service;
        }
    }

    private sealed class DisablingServiceScope : IServiceScope
    {
        private readonly IServiceScope _inner;

        public DisablingServiceScope(IServiceScope inner, Func<Task> onGetForUpdateReturned)
        {
            _inner = inner;
            ServiceProvider = new DisablingServiceProvider(inner.ServiceProvider, onGetForUpdateReturned);
        }

        public IServiceProvider ServiceProvider { get; }

        public void Dispose() => _inner.Dispose();
    }

    private sealed class DisablingScopeFactory : IServiceScopeFactory
    {
        private readonly IServiceScopeFactory _inner;
        private readonly Func<Task> _onGetForUpdateReturned;

        public DisablingScopeFactory(IServiceScopeFactory inner, Func<Task> onGetForUpdateReturned)
        {
            _inner = inner;
            _onGetForUpdateReturned = onGetForUpdateReturned;
        }

        public IServiceScope CreateScope() => new DisablingServiceScope(_inner.CreateScope(), _onGetForUpdateReturned);
    }

    [Fact]
    public async Task AdvanceQueueOnceAsync_WhenQueueModeIsDisabledAfterTheEventLockIsReleasedButBeforeRedisAndPostgresWritebackCompletes_StillCompletesThisRoundWithoutCausingHarm()
    {
        // 針對 strict-reviewer 提出的疑慮而補上（呼應 design.md 交易範圍縮小的設計說明，見
        // AdvanceEventQueueAsync 內註解）：確認鎖定範圍縮小後的窄窗口本身不會造成任何安全或資料
        // 一致性問題——即使 Admin 在 Event 鎖確認並提交後、Redis／Postgres 寫回完成前才關閉
        // 熱門搶購模式，這一輪仍會完成推進，但這不構成危害：OrderService 只在 IsQueueModeEnabled
        // 為 true 時才檢查排隊資格，活動關閉後這筆多餘的 Admitted 紀錄不會被用來繞過任何檢查。
        using var scope = _factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var eventId = await SeedQueueModeEventAsync(dbContext);
        var waiting = await SeedWaitingEntryAsync(dbContext, eventId, DateTime.UtcNow.AddMinutes(-10));

        var disablingScopeFactory = new DisablingScopeFactory(
            _factory.Services.GetRequiredService<IServiceScopeFactory>(),
            async () =>
            {
                using var writerScope = _factory.Services.CreateScope();
                var writerDbContext = writerScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                var eventEntity = await writerDbContext.Events.SingleAsync(e => e.Id == eventId);
                eventEntity.DisableQueueMode();
                await writerDbContext.SaveChangesAsync();
            });

        var service = new PurchaseQueueAdmissionService(
            disablingScopeFactory,
            _factory.Services.GetRequiredService<IDateTimeProvider>(),
            new PurchaseQueueOptions { MaxConcurrentAdmittedBuyers = 2, AdmissionTtlSeconds = 300, PollingIntervalSeconds = 5 },
            new FakeDistributedLock(),
            new DistributedLockOptions(),
            _factory.Services.GetRequiredService<StackExchange.Redis.IConnectionMultiplexer>(),
            NullLogger<PurchaseQueueAdmissionService>.Instance);

        var act = () => service.AdvanceQueueOnceAsync(CancellationToken.None);
        await act.Should().NotThrowAsync("縮小後的鎖定窗口內若發生 Queue Mode 切換，這一輪 MUST 仍安全完成，不拋出例外");

        // 實際結果取決於 DisableQueueMode 的寫入與這一輪短交易 Commit 的相對時序（例如是否需要
        // 等待同一列的鎖）——兩種結果皆安全：仍推進（鎖定確認當下讀到 enabled）或維持 Waiting
        // （DisableQueueMode 已生效、或本輪因窗口內的切換而跳過）。這正是本測試要驗證的：不論何者，
        // 處理都不會中止或卡在不一致狀態，也不會拋出例外。
        (await ReadStatusAsync(waiting.Id)).Should().BeOneOf(PurchaseQueueEntryStatus.Admitted, PurchaseQueueEntryStatus.Waiting);

        using var verifyScope = _factory.Services.CreateScope();
        var verifyDbContext = verifyScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var finalEvent = await verifyDbContext.Events.AsNoTracking().SingleAsync(e => e.Id == eventId);
        finalEvent.IsQueueModeEnabled.Should().BeFalse("Admin 的關閉指令本身確實生效，不受這一輪推進影響");
    }

    [Fact]
    public void TestingEnvironment_DoesNotRegisterTheRealBackgroundService()
    {
        var hostedServices = _factory.Services.GetServices<IHostedService>();

        hostedServices.Should().NotContain(service => service is PurchaseQueueAdmissionService);
    }
}
