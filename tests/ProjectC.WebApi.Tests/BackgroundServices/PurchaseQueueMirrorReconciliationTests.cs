using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ProjectC.Application.Common;
using ProjectC.Application.Common.Interfaces;
using ProjectC.Application.Orders;
using ProjectC.Application.Orders.PlaceOrder;
using ProjectC.Domain.Events;
using ProjectC.Domain.PurchaseQueue;
using ProjectC.Infrastructure.Persistence;
using ProjectC.Infrastructure.PurchaseQueue;
using ProjectC.WebApi.BackgroundServices;
using ProjectC.WebApi.Tests.TestSupport;
using Serilog;
using Serilog.Events;
using Serilog.Extensions.Logging;
using StackExchange.Redis;

namespace ProjectC.WebApi.Tests.BackgroundServices;

// purchase-queue-redis-admission spec：Redis 鏡像一致性（PQLE-REBUILD-*，design.md Decision 4／5／8／9）
// 與第 9a 節的補充故障邊界測試。真實 Redis（_factory 正式 DI 容器解析出的 docker-compose redis 服務）
// ＋真實 Postgres（CustomWebApplicationFactory）。
public class PurchaseQueueMirrorReconciliationTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;

    public PurchaseQueueMirrorReconciliationTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
    }

    private PurchaseQueueAdmissionService CreateService(
        int maxConcurrentAdmittedBuyers = 2,
        int admissionPendingTtlSeconds = 30,
        int reconciliationFailureLogUpgradeThreshold = 10,
        IConnectionMultiplexer? connectionMultiplexer = null,
        IServiceScopeFactory? scopeFactory = null,
        ILogger<PurchaseQueueAdmissionService>? logger = null,
        IDistributedLock? distributedLock = null,
        int pollingIntervalSeconds = 5)
        => new(
            scopeFactory ?? _factory.Services.GetRequiredService<IServiceScopeFactory>(),
            _factory.Services.GetRequiredService<IDateTimeProvider>(),
            new PurchaseQueueOptions
            {
                MaxConcurrentAdmittedBuyers = maxConcurrentAdmittedBuyers,
                AdmissionTtlSeconds = 300,
                PollingIntervalSeconds = pollingIntervalSeconds,
                AdmissionPendingTtlSeconds = admissionPendingTtlSeconds,
                ReconciliationFailureLogUpgradeThreshold = reconciliationFailureLogUpgradeThreshold,
            },
            distributedLock ?? new FakeDistributedLock(),
            new DistributedLockOptions(),
            connectionMultiplexer ?? _factory.Services.GetRequiredService<IConnectionMultiplexer>(),
            logger ?? NullLogger<PurchaseQueueAdmissionService>.Instance);

    private async Task<PurchaseQueueEntryStatus> ReadStatusAsync(Guid entryId)
    {
        using var scope = _factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var entry = await dbContext.PurchaseQueueEntries.AsNoTracking().SingleAsync(e => e.Id == entryId);
        return entry.Status;
    }

    private IDatabase GetDatabase() => _factory.Services.GetRequiredService<IConnectionMultiplexer>().GetDatabase();

    private static async Task WaitUntilAsync(Func<Task<bool>> condition, string timeoutMessage, int timeoutMs = 10000, int pollIntervalMs = 50)
    {
        var elapsed = 0;
        while (!await condition())
        {
            if (elapsed >= timeoutMs)
            {
                throw new TimeoutException($"等待條件在 {timeoutMs}ms 內未成立：{timeoutMessage}");
            }

            await Task.Delay(pollIntervalMs);
            elapsed += pollIntervalMs;
        }
    }

    // PQLE-REBUILD-001（第八輪審查要求，見 tasks.md 9.1）：明確驗證「不阻塞」本身，且校正最終完成、
    // 涵蓋所有 IsQueueModeEnabled = true 的活動、與 Postgres 真相一致。
    [Fact]
    public async Task StartAsync_PerformsFullStartupReconciliationAcrossAllQueueModeEventsWithoutBlockingStartup()
    {
        using var scope = _factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var eventId1 = await PurchaseQueueLeaderElectionTestData.SeedQueueModeEventAsync(dbContext);
        // 用兩筆已佔滿名額（maxConcurrentAdmittedBuyers: 2）的 Admitted 紀錄讓 waiting1 沒有空位可被
        // 立即推進——否則校正把它補進 waiting 鏡像後，緊接著的入場推進 Lua Script 會在同一輪就把它
        // 從 waiting 彈出、推進為 Admitted，讓下面「waiting1 應出現在 waiting 鏡像」的等待永遠等不到。
        await PurchaseQueueLeaderElectionTestData.SeedAdmittedEntryAsync(
            dbContext, eventId1, DateTime.UtcNow.AddMinutes(-40), DateTime.UtcNow.AddMinutes(-35), DateTime.UtcNow.AddMinutes(30));
        await PurchaseQueueLeaderElectionTestData.SeedAdmittedEntryAsync(
            dbContext, eventId1, DateTime.UtcNow.AddMinutes(-40), DateTime.UtcNow.AddMinutes(-35), DateTime.UtcNow.AddMinutes(30));
        var waiting1 = await PurchaseQueueLeaderElectionTestData.SeedWaitingEntryAsync(dbContext, eventId1, DateTime.UtcNow.AddMinutes(-10));
        var eventId2 = await PurchaseQueueLeaderElectionTestData.SeedQueueModeEventAsync(dbContext);
        var admitted2 = await PurchaseQueueLeaderElectionTestData.SeedAdmittedEntryAsync(
            dbContext, eventId2, DateTime.UtcNow.AddMinutes(-30), DateTime.UtcNow.AddMinutes(-25), DateTime.UtcNow.AddMinutes(30));

        var service = CreateService();
        using var cts = new CancellationTokenSource();

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        await service.StartAsync(cts.Token);
        stopwatch.Stop();
        stopwatch.ElapsedMilliseconds.Should().BeLessThan(1000, "StartAsync 不應等待啟動時的全量校正完成才返回");

        var database = GetDatabase();
        var waitingKey = PurchaseQueueAdmissionRedisKeys.Waiting(eventId1);
        var admittedKey = PurchaseQueueAdmissionRedisKeys.Admitted(eventId2);
        await WaitUntilAsync(
            async () => (await database.SortedSetScoreAsync(waitingKey, waiting1.Id.ToString())) is not null
                && (await database.SortedSetScoreAsync(admittedKey, admitted2.Id.ToString())) is not null,
            "等待啟動時的全量校正完成，涵蓋所有 IsQueueModeEnabled 活動");

        await service.StopAsync(CancellationToken.None);
    }

    // PQLE-REBUILD-002（見 tasks.md 9.2）：pending 標記存活期間，「Postgres 仍為 Waiting、entryId 已在
    // admitted」不被清除或打回 waiting。
    [Fact]
    public async Task ReconciliationRound_WhilePendingMarkerIsAlive_DoesNotClearOrRevertTheTransitionState()
    {
        using var scope = _factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var eventId = await PurchaseQueueLeaderElectionTestData.SeedQueueModeEventAsync(dbContext);
        var target = await PurchaseQueueLeaderElectionTestData.SeedWaitingEntryAsync(dbContext, eventId, DateTime.UtcNow.AddMinutes(-10));

        var realScopeFactory = _factory.Services.GetRequiredService<IServiceScopeFactory>();
        var failingScopeFactory = new PurchaseQueueRepositoryInterceptingScopeFactory(
            realScopeFactory, repo => new FailingPurchaseQueueRepository(repo, failOnAdmitBatchIds: [target.Id]));
        await CreateService(admissionPendingTtlSeconds: 30, scopeFactory: failingScopeFactory).AdvanceQueueOnceAsync(CancellationToken.None);

        (await ReadStatusAsync(target.Id)).Should().Be(PurchaseQueueEntryStatus.Waiting);
        var database = GetDatabase();
        var admittedKey = PurchaseQueueAdmissionRedisKeys.Admitted(eventId);
        var waitingKey = PurchaseQueueAdmissionRedisKeys.Waiting(eventId);
        (await database.SortedSetScoreAsync(admittedKey, target.Id.ToString())).Should().NotBeNull();

        // pending 存活期間（TTL 30 秒），跑一輪正常校正。
        await CreateService(admissionPendingTtlSeconds: 30).AdvanceQueueOnceAsync(CancellationToken.None);

        (await ReadStatusAsync(target.Id)).Should().Be(PurchaseQueueEntryStatus.Waiting, "pending 標記存活期間 MUST NOT 被清除或代為完成落地");
        (await database.SortedSetScoreAsync(admittedKey, target.Id.ToString())).Should().NotBeNull("MUST NOT 被從 admitted 清除");
        (await database.SortedSetScoreAsync(waitingKey, target.Id.ToString())).Should().BeNull("MUST NOT 同時出現在 waiting 鏡像");
    }

    // PQLE-REBUILD-003（見 tasks.md 9.4）：promotedIds 批次 Postgres 寫回遇到例外時，Redis admitted
    // 鏡像與 pending 標記維持不動（不做反向補償）。PQLE-REBUILD-002a（pending 過期後校正完成落地、
    // 已處理過的紀錄 0 列受影響）已由 7.6 完整涵蓋，這裡不重複。
    [Fact]
    public async Task PromotedBatchUpdate_WhenItThrows_LeavesRedisAdmittedAndPendingMarkerUntouched()
    {
        using var scope = _factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var eventId = await PurchaseQueueLeaderElectionTestData.SeedQueueModeEventAsync(dbContext);
        var target = await PurchaseQueueLeaderElectionTestData.SeedWaitingEntryAsync(dbContext, eventId, DateTime.UtcNow.AddMinutes(-10));

        var realScopeFactory = _factory.Services.GetRequiredService<IServiceScopeFactory>();
        var failingScopeFactory = new PurchaseQueueRepositoryInterceptingScopeFactory(
            realScopeFactory, repo => new FailingPurchaseQueueRepository(repo, failOnAdmitBatchIds: [target.Id]));
        await CreateService(scopeFactory: failingScopeFactory).AdvanceQueueOnceAsync(CancellationToken.None);

        (await ReadStatusAsync(target.Id)).Should().Be(PurchaseQueueEntryStatus.Waiting, "批次 UPDATE 例外，不做反向補償，Postgres 維持失敗前狀態");
        var database = GetDatabase();
        (await database.SortedSetScoreAsync(PurchaseQueueAdmissionRedisKeys.Admitted(eventId), target.Id.ToString())).Should().NotBeNull("Redis 端不做反向補償，仍留在 admitted");
        (await database.KeyExistsAsync(PurchaseQueueAdmissionRedisKeys.Pending(target.Id))).Should().BeTrue("pending 標記不因 Postgres 寫回失敗而被清除");
    }

    // PQLE-REBUILD-003a／003b（見 tasks.md 9.5／9.6）：expiredIds 批次 Postgres 寫回遇到例外時，
    // Redis 端不做反向補償（ZREM 是 Lua Script 本身的原子效果，不是事後補償）；下一次成功的校正
    // （步驟 8「偵測並修復放棄的逾時標記」）以條件式 UPDATE 完成落地為 Expired。
    [Fact]
    public async Task ExpiredBatchUpdate_WhenItThrows_LeavesEntryAdmittedUntilNextReconciliationMarksItExpired()
    {
        using var scope = _factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var eventId = await PurchaseQueueLeaderElectionTestData.SeedQueueModeEventAsync(dbContext);
        var now = DateTime.UtcNow;
        // 短暫的未來到期時間：先讓校正把它正常補進 Redis admitted（此時尚未逾時），再等待真實時間
        // 經過到期時間，讓下一輪 Lua Script 的 ZRANGEBYSCORE 真正把它判定為逾時。
        var target = await PurchaseQueueLeaderElectionTestData.SeedAdmittedEntryAsync(
            dbContext, eventId, now.AddMinutes(-10), now.AddMinutes(-5), now.AddSeconds(1.5));

        await CreateService().AdvanceQueueOnceAsync(CancellationToken.None);
        var database = GetDatabase();
        var admittedKey = PurchaseQueueAdmissionRedisKeys.Admitted(eventId);
        (await database.SortedSetScoreAsync(admittedKey, target.Id.ToString())).Should().NotBeNull("校正應已把尚未逾時的紀錄補進 admitted 鏡像");

        await Task.Delay(TimeSpan.FromMilliseconds(2000));

        var realScopeFactory = _factory.Services.GetRequiredService<IServiceScopeFactory>();
        var failingScopeFactory = new PurchaseQueueRepositoryInterceptingScopeFactory(
            realScopeFactory, repo => new FailingPurchaseQueueRepository(repo, failOnExpireBatchIds: [target.Id]));
        await CreateService(scopeFactory: failingScopeFactory).AdvanceQueueOnceAsync(CancellationToken.None);

        (await ReadStatusAsync(target.Id)).Should().Be(PurchaseQueueEntryStatus.Admitted, "批次 UPDATE 例外，Postgres 維持失敗前狀態（PQLE-REBUILD-003a）");

        // 下一次成功的校正（步驟 8）偵測到 A_pg_overdue 不在 A_redis（Lua Script 已原子 ZREM），
        // 以條件式 UPDATE 完成落地為 Expired（PQLE-REBUILD-003b）。
        await CreateService().AdvanceQueueOnceAsync(CancellationToken.None);
        (await ReadStatusAsync(target.Id)).Should().Be(PurchaseQueueEntryStatus.Expired, "下一次成功的校正 MUST 代為完成落地為 Expired");
    }

    // PQLE-REBUILD-007（本輪審查發現，範圍從「只清 Completed」擴大為「不屬於目前合法狀態集合」，
    // 見 tasks.md 9.6a）：對已入場的 entry 執行正常的逾時流程，接著人為讓一筆「已為 Expired」的
    // Postgres 紀錄仍殘留在 Redis admitted，驗證清除規則的一般化（不只清 Completed）。
    [Fact]
    public async Task Reconciliation_RemovesAdmittedMirrorEntryWhosePostgresStatusIsAlreadyExpired()
    {
        using var scope = _factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var eventId = await PurchaseQueueLeaderElectionTestData.SeedQueueModeEventAsync(dbContext);
        var now = DateTime.UtcNow;
        var expired = await PurchaseQueueLeaderElectionTestData.SeedAdmittedEntryAsync(
            dbContext, eventId, now.AddMinutes(-30), now.AddMinutes(-25), now.AddMinutes(-1));

        // Postgres 已經是 Expired，但直接在 Redis admitted 插入殘留（模擬混合批次中一筆生效、
        // 一筆因故未同步清除，重現「Postgres 已為 Expired、Redis admitted 仍殘留」的狀態）。
        using (var writeScope = _factory.Services.CreateScope())
        {
            var writeDbContext = writeScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var tracked = await writeDbContext.PurchaseQueueEntries.SingleAsync(e => e.Id == expired.Id);
            tracked.Expire();
            await writeDbContext.SaveChangesAsync();
        }
        var database = GetDatabase();
        var admittedKey = PurchaseQueueAdmissionRedisKeys.Admitted(eventId);
        await database.SortedSetAddAsync(admittedKey, expired.Id.ToString(), PurchaseQueueAdmissionTimeConversion.ToUnixMilliseconds(now.AddMinutes(-1)));

        await CreateService().AdvanceQueueOnceAsync(CancellationToken.None);

        (await database.SortedSetScoreAsync(admittedKey, expired.Id.ToString())).Should().BeNull(
            "已為 Expired 的殘留 MUST 被步驟 5 的一般化規則清除，不因「不是 Completed」而被略過");
    }

    // PQLE-REBUILD-007（見 tasks.md 9.6b）：模擬 Redis 重啟／資料局部遺失後的校正——直接插入一筆
    // entryId，其 Postgres 紀錄狀態為 Expired，驗證清除且不影響同一活動其他合法紀錄的鏡像狀態。
    [Fact]
    public async Task Reconciliation_RemovesStaleAdmittedMirrorEntryFromSimulatedRedisRestartWithoutAffectingOtherLegalEntries()
    {
        using var scope = _factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var eventId = await PurchaseQueueLeaderElectionTestData.SeedQueueModeEventAsync(dbContext, maxConcurrentAdmittedBuyers: 2);
        var now = DateTime.UtcNow;
        var staleExpired = await PurchaseQueueLeaderElectionTestData.SeedAdmittedEntryAsync(dbContext, eventId, now.AddMinutes(-40), now.AddMinutes(-35), now.AddMinutes(-10));
        using (var writeScope = _factory.Services.CreateScope())
        {
            var writeDbContext = writeScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var tracked = await writeDbContext.PurchaseQueueEntries.SingleAsync(e => e.Id == staleExpired.Id);
            tracked.Expire();
            await writeDbContext.SaveChangesAsync();
        }
        var legalOverdue = await PurchaseQueueLeaderElectionTestData.SeedAdmittedEntryAsync(dbContext, eventId, now.AddMinutes(-30), now.AddMinutes(-25), now.AddMinutes(-1));
        var legalActive = await PurchaseQueueLeaderElectionTestData.SeedAdmittedEntryAsync(dbContext, eventId, now.AddMinutes(-20), now.AddMinutes(-15), now.AddMinutes(30));

        var database = GetDatabase();
        var admittedKey = PurchaseQueueAdmissionRedisKeys.Admitted(eventId);
        // 模擬 Redis 重啟後從舊快照恢復的髒資料：只有 staleExpired 這筆殘留，其餘合法紀錄尚未補齊。
        await database.SortedSetAddAsync(admittedKey, staleExpired.Id.ToString(), PurchaseQueueAdmissionTimeConversion.ToUnixMilliseconds(now.AddMinutes(-10)));

        await CreateService(maxConcurrentAdmittedBuyers: 2).AdvanceQueueOnceAsync(CancellationToken.None);

        (await database.SortedSetScoreAsync(admittedKey, staleExpired.Id.ToString())).Should().BeNull("Expired 的殘留 MUST 被清除");
        (await database.SortedSetScoreAsync(admittedKey, legalActive.Id.ToString())).Should().NotBeNull("合法的 Admitted 紀錄不受影響，應被補齊");
        // legalOverdue 屬於 A_pg_overdue，MUST NOT 被步驟 4/5 補齊或清除，留給 Decision 4 步驟 1 自然收斂——
        // 這裡只驗證它沒有被「誤補」進 admitted（若被誤補，代表步驟 4 沒有正確排除已逾時的紀錄）。
        (await database.SortedSetScoreAsync(admittedKey, legalOverdue.Id.ToString())).Should().BeNull(
            "A_pg_overdue 不屬於本步驟的補齊範圍，MUST NOT 被提前補進 admitted 鏡像");
    }

    // PQLE-REBUILD-007（孤兒 entryId，見 tasks.md 9.6c）：Redis admitted 中插入一個 Postgres 完全查無
    // 對應紀錄的隨機 Guid，驗證清除且不拋例外、不影響其他合法 entry。
    [Fact]
    public async Task Reconciliation_RemovesOrphanAdmittedMirrorMemberWithNoCorrespondingPostgresRecord()
    {
        using var scope = _factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var eventId = await PurchaseQueueLeaderElectionTestData.SeedQueueModeEventAsync(dbContext, maxConcurrentAdmittedBuyers: 2);
        var now = DateTime.UtcNow;
        var legalActive = await PurchaseQueueLeaderElectionTestData.SeedAdmittedEntryAsync(dbContext, eventId, now.AddMinutes(-20), now.AddMinutes(-15), now.AddMinutes(30));

        var orphanId = Guid.NewGuid();
        var database = GetDatabase();
        var admittedKey = PurchaseQueueAdmissionRedisKeys.Admitted(eventId);
        await database.SortedSetAddAsync(admittedKey, orphanId.ToString(), PurchaseQueueAdmissionTimeConversion.ToUnixMilliseconds(now.AddMinutes(10)));

        var act = () => CreateService(maxConcurrentAdmittedBuyers: 2).AdvanceQueueOnceAsync(CancellationToken.None);
        await act.Should().NotThrowAsync("孤兒 member 的清除不應拋出例外");

        (await database.SortedSetScoreAsync(admittedKey, orphanId.ToString())).Should().BeNull("孤兒 member MUST 被清除");
        (await database.SortedSetScoreAsync(admittedKey, legalActive.Id.ToString())).Should().NotBeNull("其他合法 entry 的校正結果不受影響");
    }

    // PQ-COMPLETE-003／PQLE-REBUILD-004（見 tasks.md 9.7）：訂單完成同步失敗（ZREM 失敗，模擬 Redis
    // 暫時不可用）時，Postgres 端立即為 Completed；Redis 端名額短暫仍被佔用；持續故障期間名額不會
    // 被錯誤釋放（維持保守，不超額）；Redis／同步恢復後的下一次校正正確 ZREM 釋放。
    [Fact]
    public async Task OrderCompletion_WhenMirrorSyncFails_KeepsSlotOccupiedUntilNextSuccessfulReconciliationReleasesIt()
    {
        using var scope = _factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var venue = new ProjectC.Domain.Venues.Venue(Guid.NewGuid(), $"Test Venue {Guid.NewGuid():N}");
        var seatMap = new ProjectC.Domain.Venues.SeatMap(Guid.NewGuid(), venue.Id);
        var @event = new ProjectC.Domain.Events.Event(Guid.NewGuid(), "Test Event", DateTime.UtcNow.AddDays(30), venue.Id, seatMap.Id);
        @event.EnableQueueMode();
        var ticketType = @event.CreateCountBasedTicketType("站票", 300m, 10);
        dbContext.Venues.Add(venue);
        dbContext.SeatMaps.Add(seatMap);
        dbContext.Events.Add(@event);
        dbContext.TicketTypes.Add(ticketType);
        await dbContext.SaveChangesAsync();

        var now = DateTime.UtcNow;
        var admittedBuyer = ProjectC.Domain.Members.Member.Register($"buyer-{Guid.NewGuid():N}@example.com", "Admitted Buyer", "hash");
        dbContext.Members.Add(admittedBuyer);
        var admittedEntry = new PurchaseQueueEntry(Guid.NewGuid(), @event.Id, admittedBuyer.Id, now.AddMinutes(-30));
        admittedEntry.Admit(now.AddMinutes(-25), now.AddMinutes(30));
        dbContext.PurchaseQueueEntries.Add(admittedEntry);
        var waiting = await PurchaseQueueLeaderElectionTestData.SeedWaitingEntryAsync(dbContext, @event.Id, now.AddMinutes(-10));

        // 前置校正，讓 admittedEntry 進入 Redis admitted 鏡像（理由同 PQ-COMPLETE-001 測試）。
        await CreateService(maxConcurrentAdmittedBuyers: 1).AdvanceQueueOnceAsync(CancellationToken.None);
        var database = GetDatabase();
        var admittedKey = PurchaseQueueAdmissionRedisKeys.Admitted(@event.Id);
        (await database.SortedSetScoreAsync(admittedKey, admittedEntry.Id.ToString())).Should().NotBeNull();

        // 用一個指向不可連線 Redis 的 IPurchaseQueueAdmissionMirror 建立 OrderService，模擬「ZREM
        // 失敗（Redis 暫時不可用）」——只影響這次同步呼叫，不影響 OrderService 其餘依賴（皆沿用正式
        // DI 容器解析），也不影響 PurchaseQueueAdmissionService 自己的 Redis 連線。
        var unreachableOptions = ConfigurationOptions.Parse("127.0.0.1:1");
        unreachableOptions.AbortOnConnectFail = false;
        unreachableOptions.ConnectTimeout = 300;
        unreachableOptions.SyncTimeout = 300;
        var unreachableConnection = ConnectionMultiplexer.Connect(unreachableOptions);
        var failingMirror = new RedisPurchaseQueueAdmissionMirror(unreachableConnection, NullLogger<RedisPurchaseQueueAdmissionMirror>.Instance);
        var mirrorOverridingProvider = new MirrorOverridingServiceProvider(scope.ServiceProvider, failingMirror);
        var orderService = ActivatorUtilities.CreateInstance<OrderService>(mirrorOverridingProvider);

        var request = new PlaceOrderRequest([new PlaceOrderSelectionRequest(null, ticketType.Id, 1)]);
        var placeResult = await orderService.PlaceOrderAsync(admittedBuyer.Id, request, CancellationToken.None);
        placeResult.IsSuccess.Should().BeTrue("ZREM 失敗不影響訂單建立本身的成功與否");

        (await ReadStatusAsync(admittedEntry.Id)).Should().Be(PurchaseQueueEntryStatus.Completed, "Postgres 端立即為 Completed，不受 Redis 同步失敗影響");
        (await database.SortedSetScoreAsync(admittedKey, admittedEntry.Id.ToString())).Should().NotBeNull("ZREM 失敗，Redis 端名額短暫仍被佔用");

        // 持續故障期間（這裡以「尚未執行任何成功的校正」代表持續故障，見 design.md Decision 5「重複
        // 執行的冪等性」——只要沒有一次成功的校正執行，名額就不會被錯誤釋放，維持保守）：
        (await ReadStatusAsync(waiting.Id)).Should().Be(PurchaseQueueEntryStatus.Waiting, "名額尚未真正釋放，不應被錯誤推進");

        // Redis／同步恢復後（下一次用正常連線執行的校正）正確 ZREM 釋放。
        await CreateService(maxConcurrentAdmittedBuyers: 1).AdvanceQueueOnceAsync(CancellationToken.None);

        (await database.SortedSetScoreAsync(admittedKey, admittedEntry.Id.ToString())).Should().BeNull("下一次成功的校正 MUST 完成 ZREM，釋放名額");
        (await ReadStatusAsync(waiting.Id)).Should().Be(PurchaseQueueEntryStatus.Admitted, "名額釋放後，下一輪推進應提供給下一位等待者");
    }

    private sealed class MirrorOverridingServiceProvider : IServiceProvider
    {
        private readonly IServiceProvider _inner;
        private readonly IPurchaseQueueAdmissionMirror _mirror;

        public MirrorOverridingServiceProvider(IServiceProvider inner, IPurchaseQueueAdmissionMirror mirror)
        {
            _inner = inner;
            _mirror = mirror;
        }

        public object? GetService(Type serviceType) => serviceType == typeof(IPurchaseQueueAdmissionMirror) ? _mirror : _inner.GetService(serviceType);
    }

    // Decision 3（見 tasks.md 9.8）：ZADD 鏡像寫入失敗不影響 PQ-JOIN 本身成功，且該筆紀錄能被下一輪
    // 校正機制（步驟 3「補齊 waiting」）補上。
    [Fact]
    public async Task JoinPurchaseQueue_WhenMirrorSyncFails_StillSucceedsAndIsReconciledIntoRedisNextRound()
    {
        using var scope = _factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var eventId = await PurchaseQueueLeaderElectionTestData.SeedQueueModeEventAsync(dbContext);

        var eventRepository = scope.ServiceProvider.GetRequiredService<IEventRepository>();
        var purchaseQueueRepository = scope.ServiceProvider.GetRequiredService<IPurchaseQueueRepository>();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var dateTimeProvider = scope.ServiceProvider.GetRequiredService<IDateTimeProvider>();
        var captchaService = scope.ServiceProvider.GetRequiredService<ICaptchaService>();

        var unreachableOptions = ConfigurationOptions.Parse("127.0.0.1:1");
        unreachableOptions.AbortOnConnectFail = false;
        unreachableOptions.ConnectTimeout = 300;
        unreachableOptions.SyncTimeout = 300;
        var unreachableConnection = ConnectionMultiplexer.Connect(unreachableOptions);
        var failingMirror = new RedisPurchaseQueueAdmissionMirror(unreachableConnection, NullLogger<RedisPurchaseQueueAdmissionMirror>.Instance);

        var handler = new ProjectC.Application.PurchaseQueue.JoinPurchaseQueue.JoinPurchaseQueueHandler(
            eventRepository, purchaseQueueRepository, unitOfWork, dateTimeProvider,
            new ProjectC.Application.PurchaseQueue.JoinPurchaseQueue.JoinPurchaseQueueRequestValidator(),
            captchaService, failingMirror);

        var request = new ProjectC.Application.PurchaseQueue.JoinPurchaseQueue.JoinPurchaseQueueRequest(
            FakeCaptchaService.ValidToken, FakeCaptchaService.ValidAnswer);
        var memberId = await SeedBuyerAsync(dbContext);

        var result = await handler.HandleAsync(eventId, memberId, request, CancellationToken.None);

        result.IsSuccess.Should().BeTrue("Redis 鏡像寫入失敗不影響加入排隊本身的成功與否");

        var database = GetDatabase();
        var waitingKey = PurchaseQueueAdmissionRedisKeys.Waiting(eventId);
        (await database.SortedSetScoreAsync(waitingKey, result.Value.ToString())).Should().BeNull("這次 ZADD 失敗，尚未進入 Redis 鏡像");

        // 用已佔滿名額（CreateService 預設 maxConcurrentAdmittedBuyers: 2）的兩筆 Admitted 紀錄讓這筆
        // 新紀錄沒有空位可被立即推進——否則校正補進 waiting 鏡像後，緊接著的入場推進 Lua Script 會在
        // 同一輪就把它彈出、推進為 Admitted，讓下面「應出現在 waiting 鏡像」的斷言恆假。
        await PurchaseQueueLeaderElectionTestData.SeedAdmittedEntryAsync(
            dbContext, eventId, DateTime.UtcNow.AddMinutes(-40), DateTime.UtcNow.AddMinutes(-35), DateTime.UtcNow.AddMinutes(30));
        await PurchaseQueueLeaderElectionTestData.SeedAdmittedEntryAsync(
            dbContext, eventId, DateTime.UtcNow.AddMinutes(-40), DateTime.UtcNow.AddMinutes(-35), DateTime.UtcNow.AddMinutes(30));

        await CreateService().AdvanceQueueOnceAsync(CancellationToken.None);

        (await database.SortedSetScoreAsync(waitingKey, result.Value.ToString())).Should().NotBeNull("下一輪校正（步驟 3）MUST 把只存在於 Postgres 的紀錄補進 Redis waiting 鏡像");
    }

    private async Task<Guid> SeedBuyerAsync(ApplicationDbContext dbContext)
    {
        var member = ProjectC.Domain.Members.Member.Register($"buyer-{Guid.NewGuid():N}@example.com", "Test Buyer", "hash");
        dbContext.Members.Add(member);
        await dbContext.SaveChangesAsync();
        return member.Id;
    }

    // PQLE-REBUILD-005（結構性驗證，元件層，見 tasks.md 9.10a）：直接對 ReadRedisMirrorSnapshotAsync
    // 傳入 Mock<IDatabase>，不需要建立完整的 PurchaseQueueAdmissionService 執行環境。
    [Fact]
    public async Task ReadRedisMirrorSnapshotAsync_CallsScriptEvaluateExactlyOnceAndNeverCallsSeparateRangeCommands()
    {
        var mockDatabase = new Mock<IDatabase>();
        var emptyArray = RedisResult.Create(Array.Empty<RedisValue>());
        var scriptResult = RedisResult.Create(new[] { emptyArray, emptyArray });
        mockDatabase
            .Setup(d => d.ScriptEvaluateAsync(It.IsAny<string>(), It.IsAny<RedisKey[]>(), It.IsAny<RedisValue[]>(), It.IsAny<CommandFlags>()))
            .ReturnsAsync(scriptResult);

        var service = CreateService();
        var snapshot = await service.ReadRedisMirrorSnapshotAsync(Guid.NewGuid(), mockDatabase.Object, CancellationToken.None);

        snapshot.WaitingMemberIds.Should().BeEmpty();
        snapshot.AdmittedMemberScores.Should().BeEmpty();

        mockDatabase.Verify(
            d => d.ScriptEvaluateAsync(It.IsAny<string>(), It.IsAny<RedisKey[]>(), It.IsAny<RedisValue[]>(), It.IsAny<CommandFlags>()),
            Times.Once, "應恰好呼叫一次單一原子 EVAL");
        mockDatabase.Verify(
            d => d.SortedSetRangeByScoreAsync(It.IsAny<RedisKey>(), It.IsAny<double>(), It.IsAny<double>(), It.IsAny<Exclude>(), It.IsAny<Order>(), It.IsAny<long>(), It.IsAny<long>(), It.IsAny<CommandFlags>()),
            Times.Never, "MUST NOT 呼叫 ZRANGEBYSCORE，直接、結構性地證明實作確實依單一原子 EVAL 讀取");
        mockDatabase.Verify(
            d => d.SortedSetRangeByRankAsync(It.IsAny<RedisKey>(), It.IsAny<long>(), It.IsAny<long>(), It.IsAny<Order>(), It.IsAny<CommandFlags>()),
            Times.Never, "MUST NOT 呼叫 ZRANGE(ByRank)，直接、結構性地證明實作確實依單一原子 EVAL 讀取");
    }

    // PQLE-REBUILD-005（原序列情境，整合層，真實 Redis，見 tasks.md 9.10b）：先呼叫真實入場推進
    // Lua Script，讓某 entryId 從 waiting 移到 admitted（刻意不執行對應的 Postgres UPDATE）；驗證
    // 校正後該 entryId 只存在於 admitted 鏡像，不會同時出現在 waiting；也不會被下一輪 ZPOPMIN
    // 重複選中、不影響其他 Waiting 紀錄的推進結果。
    [Fact]
    public async Task Reconciliation_WhenEntryHasBeenAtomicallyPromotedButPostgresSnapshotStillShowsWaiting_DoesNotDuplicateOrRePromoteIt()
    {
        using var scope = _factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var eventId = await PurchaseQueueLeaderElectionTestData.SeedQueueModeEventAsync(dbContext, maxConcurrentAdmittedBuyers: 1);
        var target = await PurchaseQueueLeaderElectionTestData.SeedWaitingEntryAsync(dbContext, eventId, DateTime.UtcNow.AddMinutes(-10));

        var realScopeFactory = _factory.Services.GetRequiredService<IServiceScopeFactory>();
        var failingScopeFactory = new PurchaseQueueRepositoryInterceptingScopeFactory(
            realScopeFactory, repo => new FailingPurchaseQueueRepository(repo, failOnAdmitBatchIds: [target.Id]));
        await CreateService(maxConcurrentAdmittedBuyers: 1, scopeFactory: failingScopeFactory).AdvanceQueueOnceAsync(CancellationToken.None);

        (await ReadStatusAsync(target.Id)).Should().Be(PurchaseQueueEntryStatus.Waiting, "Postgres 快照仍顯示 Waiting（批次 UPDATE 被攔截）");

        await CreateService(maxConcurrentAdmittedBuyers: 1).AdvanceQueueOnceAsync(CancellationToken.None);

        var database = GetDatabase();
        var waitingKey = PurchaseQueueAdmissionRedisKeys.Waiting(eventId);
        var admittedKey = PurchaseQueueAdmissionRedisKeys.Admitted(eventId);
        (await database.SortedSetScoreAsync(admittedKey, target.Id.ToString())).Should().NotBeNull();
        (await database.SortedSetScoreAsync(waitingKey, target.Id.ToString())).Should().BeNull("MUST NOT 同時出現在 waiting 鏡像");
        (await ReadStatusAsync(target.Id)).Should().Be(PurchaseQueueEntryStatus.Waiting, "pending 標記尚未過期，MUST NOT 被提前代為落地");

        // 名額已滿（maxConcurrentAdmittedBuyers: 1，target 佔用中），新增另一筆 Waiting，驗證不會被
        // ZPOPMIN 誤選（因為 target 已不在 waiting 鏡像裡，名額也已被佔用）。
        var other = await PurchaseQueueLeaderElectionTestData.SeedWaitingEntryAsync(dbContext, eventId, DateTime.UtcNow.AddMinutes(-5));
        await CreateService(maxConcurrentAdmittedBuyers: 1).AdvanceQueueOnceAsync(CancellationToken.None);
        (await ReadStatusAsync(other.Id)).Should().Be(PurchaseQueueEntryStatus.Waiting, "名額仍被 target 佔用，other 不應被推進");
    }

    // PQLE-REBUILD-005（真實併發壓力測試，整合層，補強經驗證據，見 tasks.md 9.10c）：對同一活動同時
    // 觸發入場推進與校正，重複多次迭代，驗證每次迭代結束後鏡像互斥、不超額、不重複選中。
    [Fact]
    public async Task ConcurrentAdvanceCalls_NeverProduceDualMembershipOrExceedTheAdmissionLimit()
    {
        for (var iteration = 0; iteration < 10; iteration++)
        {
            using var scope = _factory.Services.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var eventId = await PurchaseQueueLeaderElectionTestData.SeedQueueModeEventAsync(dbContext, maxConcurrentAdmittedBuyers: 3);
            await PurchaseQueueLeaderElectionTestData.SeedManyWaitingEntriesAsync(dbContext, eventId, count: 3);

            var serviceA = CreateService(maxConcurrentAdmittedBuyers: 3);
            var serviceB = CreateService(maxConcurrentAdmittedBuyers: 3);
            await Task.WhenAll(
                serviceA.AdvanceQueueOnceAsync(CancellationToken.None),
                serviceB.AdvanceQueueOnceAsync(CancellationToken.None));

            // design.md Decision 5「不需要嚴格線性化」：快照讀取（單一原子 EVAL）與後續的個別寫入
            // （ZADD／ZREM）之間仍有時間差，若一個並發的完整推進（reconcile+advance）恰好插入這個
            // 窗口，快照可能過時，短暫讓某 entryId 同時出現在 waiting 與 admitted 兩個鏡像——這是
            // 刻意接受的「最終收斂」而非「絕對不發生」，下一輪校正的步驟 6（此時該 entryId 在
            // Postgres 已是 Admitted，不再屬於 W_pg）會清除這個過時殘留。因此在斷言鏡像互斥之前，
            // 先跑一輪不並發的校正讓其收斂，這裡驗證的是「最終」不重複歸屬，不是「當下立即」。
            await CreateService(maxConcurrentAdmittedBuyers: 3).AdvanceQueueOnceAsync(CancellationToken.None);

            using var readScope = _factory.Services.CreateScope();
            var readDbContext = readScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var entryIds = await readDbContext.PurchaseQueueEntries.AsNoTracking()
                .Where(e => e.EventId == eventId).Select(e => e.Id).ToListAsync();
            var admittedCount = await readDbContext.PurchaseQueueEntries.AsNoTracking()
                .CountAsync(e => e.EventId == eventId && e.Status == PurchaseQueueEntryStatus.Admitted);
            admittedCount.Should().BeLessThanOrEqualTo(3, $"iteration {iteration}: 不應超額入場");

            var database = GetDatabase();
            var waitingKey = PurchaseQueueAdmissionRedisKeys.Waiting(eventId);
            var admittedKey = PurchaseQueueAdmissionRedisKeys.Admitted(eventId);
            foreach (var id in entryIds)
            {
                var inWaiting = (await database.SortedSetScoreAsync(waitingKey, id.ToString())) is not null;
                var inAdmitted = (await database.SortedSetScoreAsync(admittedKey, id.ToString())) is not null;
                (inWaiting && inAdmitted).Should().BeFalse($"iteration {iteration}: entry {id} MUST NOT 同時出現在 waiting 與 admitted 兩個鏡像");
            }
        }
    }

    private sealed class ThrowingForEventEventRepository : IEventRepository
    {
        private readonly IEventRepository _inner;
        private readonly Guid _throwingEventId;

        public ThrowingForEventEventRepository(IEventRepository inner, Guid throwingEventId)
        {
            _inner = inner;
            _throwingEventId = throwingEventId;
        }

        public Task<IReadOnlyList<Event>> GetAllAsync(CancellationToken cancellationToken) => _inner.GetAllAsync(cancellationToken);

        public Task<Event?> GetByIdAsync(Guid id, CancellationToken cancellationToken) => _inner.GetByIdAsync(id, cancellationToken);

        public void Add(Event @event) => _inner.Add(@event);

        public void Update(Event @event) => _inner.Update(@event);

        public Task<Event?> GetForUpdateAsync(Guid eventId, CancellationToken cancellationToken)
            => eventId == _throwingEventId
                ? throw new InvalidOperationException("Simulated per-activity connection failure for test.")
                : _inner.GetForUpdateAsync(eventId, cancellationToken);
    }

    private sealed class EventRepositoryInterceptingScopeFactory : IServiceScopeFactory
    {
        private readonly IServiceScopeFactory _inner;
        private readonly Guid _throwingEventId;

        public EventRepositoryInterceptingScopeFactory(IServiceScopeFactory inner, Guid throwingEventId)
        {
            _inner = inner;
            _throwingEventId = throwingEventId;
        }

        public IServiceScope CreateScope() => new InterceptingScope(_inner.CreateScope(), _throwingEventId);

        private sealed class InterceptingScope : IServiceScope
        {
            private readonly IServiceScope _inner;

            public InterceptingScope(IServiceScope inner, Guid throwingEventId)
            {
                _inner = inner;
                ServiceProvider = new InterceptingProvider(inner.ServiceProvider, throwingEventId);
            }

            public IServiceProvider ServiceProvider { get; }

            public void Dispose() => _inner.Dispose();
        }

        private sealed class InterceptingProvider : IServiceProvider
        {
            private readonly IServiceProvider _inner;
            private readonly Guid _throwingEventId;

            public InterceptingProvider(IServiceProvider inner, Guid throwingEventId)
            {
                _inner = inner;
                _throwingEventId = throwingEventId;
            }

            public object? GetService(Type serviceType)
            {
                var service = _inner.GetService(serviceType);
                return service is IEventRepository eventRepository ? new ThrowingForEventEventRepository(eventRepository, _throwingEventId) : service;
            }
        }
    }

    // PQLE-REBUILD-006（第十輪審查要求，見 tasks.md 9.11）：某一活動的處理拋出例外，斷言該活動的
    // 異常產生 Warning 等級結構化 log，且該活動被跳過，其餘活動仍被正常推進，不因單一活動失敗而
    // 整輪中止。
    [Fact]
    public async Task AdvanceQueueOnceAsync_WhenOneActivityThrows_LogsWarningForThatActivityAndStillProcessesOthers()
    {
        using var scope = _factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var failingEventId = await PurchaseQueueLeaderElectionTestData.SeedQueueModeEventAsync(dbContext);
        await PurchaseQueueLeaderElectionTestData.SeedWaitingEntryAsync(dbContext, failingEventId, DateTime.UtcNow.AddMinutes(-10));
        var healthyEventId = await PurchaseQueueLeaderElectionTestData.SeedQueueModeEventAsync(dbContext);
        var healthyWaiting = await PurchaseQueueLeaderElectionTestData.SeedWaitingEntryAsync(dbContext, healthyEventId, DateTime.UtcNow.AddMinutes(-10));

        var sink = new InMemoryLogEventSink();
        var serilogLogger = new LoggerConfiguration().Enrich.FromLogContext().WriteTo.Sink(sink).CreateLogger();
        using var loggerFactory = new SerilogLoggerFactory(serilogLogger, dispose: true);
        var logger = loggerFactory.CreateLogger<PurchaseQueueAdmissionService>();

        var realScopeFactory = _factory.Services.GetRequiredService<IServiceScopeFactory>();
        var eventFailingScopeFactory = new EventRepositoryInterceptingScopeFactory(realScopeFactory, failingEventId);
        var service = CreateService(scopeFactory: eventFailingScopeFactory, logger: logger);

        await service.AdvanceQueueOnceAsync(CancellationToken.None);

        var warningEvents = sink.Events.Where(e => e.Level == LogEventLevel.Warning).ToList();
        warningEvents.Should().Contain(e => e.RenderMessage().Contains("Unexpected error while advancing purchase queue", StringComparison.Ordinal),
            "該活動的異常 MUST 記錄為 Warning 等級結構化 log");

        (await ReadStatusAsync(healthyWaiting.Id)).Should().Be(PurchaseQueueEntryStatus.Admitted, "其餘活動不因單一活動失敗而中止處理，仍應正常推進");
    }

    /// <summary>建立一筆「Postgres 已 Completed，但 Redis admitted 鏡像同步失敗、仍佔用名額」的紀錄
    /// （比照 9.7 的手法），供 9.12／9.13／9.13a 共用。</summary>
    private async Task<(Guid EventId, Guid StaleEntryId, Guid WaitingId)> SeedCompletedButUnsyncedEntryAsync(ApplicationDbContext dbContext)
    {
        var venue = new ProjectC.Domain.Venues.Venue(Guid.NewGuid(), $"Test Venue {Guid.NewGuid():N}");
        var seatMap = new ProjectC.Domain.Venues.SeatMap(Guid.NewGuid(), venue.Id);
        var @event = new Event(Guid.NewGuid(), "Test Event", DateTime.UtcNow.AddDays(30), venue.Id, seatMap.Id);
        @event.EnableQueueMode();
        var ticketType = @event.CreateCountBasedTicketType("站票", 300m, 10);
        dbContext.Venues.Add(venue);
        dbContext.SeatMaps.Add(seatMap);
        dbContext.Events.Add(@event);
        dbContext.TicketTypes.Add(ticketType);
        await dbContext.SaveChangesAsync();

        var now = DateTime.UtcNow;
        var admittedBuyer = ProjectC.Domain.Members.Member.Register($"buyer-{Guid.NewGuid():N}@example.com", "Admitted Buyer", "hash");
        dbContext.Members.Add(admittedBuyer);
        var admittedEntry = new PurchaseQueueEntry(Guid.NewGuid(), @event.Id, admittedBuyer.Id, now.AddMinutes(-30));
        admittedEntry.Admit(now.AddMinutes(-25), now.AddMinutes(30));
        dbContext.PurchaseQueueEntries.Add(admittedEntry);
        var waiting = await PurchaseQueueLeaderElectionTestData.SeedWaitingEntryAsync(dbContext, @event.Id, now.AddMinutes(-10));

        await CreateService(maxConcurrentAdmittedBuyers: 1).AdvanceQueueOnceAsync(CancellationToken.None);

        var unreachableOptions = ConfigurationOptions.Parse("127.0.0.1:1");
        unreachableOptions.AbortOnConnectFail = false;
        unreachableOptions.ConnectTimeout = 300;
        unreachableOptions.SyncTimeout = 300;
        var unreachableConnection = ConnectionMultiplexer.Connect(unreachableOptions);
        var failingMirror = new RedisPurchaseQueueAdmissionMirror(unreachableConnection, NullLogger<RedisPurchaseQueueAdmissionMirror>.Instance);
        using var orderScope = _factory.Services.CreateScope();
        var mirrorOverridingProvider = new MirrorOverridingServiceProvider(orderScope.ServiceProvider, failingMirror);
        var orderService = ActivatorUtilities.CreateInstance<OrderService>(mirrorOverridingProvider);
        var request = new PlaceOrderRequest([new PlaceOrderSelectionRequest(null, ticketType.Id, 1)]);
        var placeResult = await orderService.PlaceOrderAsync(admittedBuyer.Id, request, CancellationToken.None);
        placeResult.IsSuccess.Should().BeTrue();

        return (@event.Id, admittedEntry.Id, waiting.Id);
    }

    // PQ-COMPLETE-003／PQLE-REBUILD-004（見 tasks.md 9.12）：不論哪個實例搶到鎖執行該輪，該輪的校正
    // 都會涵蓋到目標活動，目標活動的名額最終被釋放，不因鎖被哪個實例取得而遺漏。
    [Fact]
    public async Task LockCompetition_WhicheverInstanceAcquiresTheLock_StillReconcilesAndReleasesTheTargetActivitysSlot()
    {
        using var scope = _factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var (eventId, staleEntryId, waitingId) = await SeedCompletedButUnsyncedEntryAsync(dbContext);

        var connectionMultiplexer = _factory.Services.GetRequiredService<IConnectionMultiplexer>();
        var lockA = new ProjectC.Infrastructure.DistributedLocking.RedisDistributedLock(connectionMultiplexer, NullLogger<ProjectC.Infrastructure.DistributedLocking.RedisDistributedLock>.Instance);
        var lockB = new ProjectC.Infrastructure.DistributedLocking.RedisDistributedLock(connectionMultiplexer, NullLogger<ProjectC.Infrastructure.DistributedLocking.RedisDistributedLock>.Instance);
        var serviceA = CreateService(maxConcurrentAdmittedBuyers: 1, distributedLock: lockA);
        var serviceB = CreateService(maxConcurrentAdmittedBuyers: 1, distributedLock: lockB);

        await Task.WhenAll(
            serviceA.AdvanceQueueOnceWithLeaderElectionAsync(CancellationToken.None),
            serviceB.AdvanceQueueOnceWithLeaderElectionAsync(CancellationToken.None));

        var database = GetDatabase();
        (await database.SortedSetScoreAsync(PurchaseQueueAdmissionRedisKeys.Admitted(eventId), staleEntryId.ToString())).Should().BeNull(
            "不論哪個實例取得鎖，該輪的校正都涵蓋全部活動，目標活動的名額最終被釋放");
        (await ReadStatusAsync(waitingId)).Should().Be(PurchaseQueueEntryStatus.Admitted, "釋放的名額正常供其他 Waiting 紀錄使用");
    }

    /// <summary>在 GetForUpdateAsync 被呼叫、且目標 eventId 相符時，先觸發取消再往外拋
    /// OperationCanceledException，不委派給真正的實作——確保目標活動這一輪完全沒有被處理到
    /// （不像依呼叫「次數」判斷的手法，這個判斷不受同一測試類別內其他測試遺留、仍為
    /// IsQueueModeEnabled 的活動數量影響，Postgres 掃描順序不保證、也不需要保證）。</summary>
    private sealed class CancelOnTargetEventGetForUpdateEventRepository : IEventRepository
    {
        private readonly IEventRepository _inner;
        private readonly Guid _targetEventId;
        private readonly CancellationTokenSource _cts;

        public CancelOnTargetEventGetForUpdateEventRepository(IEventRepository inner, Guid targetEventId, CancellationTokenSource cts)
        {
            _inner = inner;
            _targetEventId = targetEventId;
            _cts = cts;
        }

        public Task<IReadOnlyList<Event>> GetAllAsync(CancellationToken cancellationToken) => _inner.GetAllAsync(cancellationToken);

        public Task<Event?> GetByIdAsync(Guid id, CancellationToken cancellationToken) => _inner.GetByIdAsync(id, cancellationToken);

        public void Add(Event @event) => _inner.Add(@event);

        public void Update(Event @event) => _inner.Update(@event);

        public Task<Event?> GetForUpdateAsync(Guid eventId, CancellationToken cancellationToken)
        {
            if (eventId == _targetEventId)
            {
                _cts.Cancel();
                cancellationToken.ThrowIfCancellationRequested();
            }

            return _inner.GetForUpdateAsync(eventId, cancellationToken);
        }
    }

    private sealed class CancelOnTargetEventScopeFactory : IServiceScopeFactory
    {
        private readonly IServiceScopeFactory _inner;
        private readonly Guid _targetEventId;
        private readonly CancellationTokenSource _cts;

        public CancelOnTargetEventScopeFactory(IServiceScopeFactory inner, Guid targetEventId, CancellationTokenSource cts)
        {
            _inner = inner;
            _targetEventId = targetEventId;
            _cts = cts;
        }

        public IServiceScope CreateScope() => new InterceptingScope(_inner.CreateScope(), _targetEventId, _cts);

        private sealed class InterceptingScope : IServiceScope
        {
            private readonly IServiceScope _inner;

            public InterceptingScope(IServiceScope inner, Guid targetEventId, CancellationTokenSource cts)
            {
                _inner = inner;
                ServiceProvider = new InterceptingProvider(inner.ServiceProvider, targetEventId, cts);
            }

            public IServiceProvider ServiceProvider { get; }

            public void Dispose() => _inner.Dispose();
        }

        private sealed class InterceptingProvider : IServiceProvider
        {
            private readonly IServiceProvider _inner;
            private readonly Guid _targetEventId;
            private readonly CancellationTokenSource _cts;

            public InterceptingProvider(IServiceProvider inner, Guid targetEventId, CancellationTokenSource cts)
            {
                _inner = inner;
                _targetEventId = targetEventId;
                _cts = cts;
            }

            public object? GetService(Type serviceType)
            {
                var service = _inner.GetService(serviceType);
                return service is IEventRepository eventRepository
                    ? new CancelOnTargetEventGetForUpdateEventRepository(eventRepository, _targetEventId, _cts)
                    : service;
            }
        }
    }

    // PQ-COMPLETE-003／PQLE-REBUILD-004／PQLE-REBUILD-008（見 tasks.md 9.13／9.13a，兩者合併：手法
    // 相同，都需要「不可攔截的整程序中止」的可控制模擬——用外部可控制的 CancellationToken，鎖定
    // 目標活動一開始被處理（GetForUpdateAsync 被呼叫）時就取消）：驗證 (a) 取消透過
    // OperationCanceledException 往外傳遞；(b) 目標活動完全未被處理到，其鏡像／狀態與取消前一致；
    // (c) 這個未處理到的活動仍可被下一輪重新嘗試處理，不因「曾經有一輪取得過鎖」就被誤判為
    // 「已檢查」，最終正確完成收斂——呼應 PQ-COMPLETE-003／PQLE-REBUILD-004「下一次成功執行的校正」
    // 的精確邊界。不斷言其他活動（含同一測試類別內其他測試方法遺留的 IsQueueModeEnabled 活動）
    // 是否先於目標活動被處理到——Postgres 掃描順序本來就不保證，也不是本測試要驗證的重點。
    [Fact]
    public async Task Cancellation_BetweenActivities_PropagatesAndLeavesTheTargetActivityUntouchedUntilRetried()
    {
        using var scope = _factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        // 目標活動：對應 PQ-COMPLETE-003 情境的「完成同步失敗、名額待釋放」紀錄。
        var (targetEventId, staleEntryId, targetWaitingId) = await SeedCompletedButUnsyncedEntryAsync(dbContext);

        using var cts = new CancellationTokenSource();
        var realScopeFactory = _factory.Services.GetRequiredService<IServiceScopeFactory>();
        var cancellingScopeFactory = new CancelOnTargetEventScopeFactory(realScopeFactory, targetEventId, cts);
        var service = CreateService(maxConcurrentAdmittedBuyers: 1, scopeFactory: cancellingScopeFactory);

        var act = () => service.AdvanceQueueOnceAsync(cts.Token);
        await act.Should().ThrowAsync<OperationCanceledException>("(a) 取消 MUST 透過 OperationCanceledException 往外傳遞，不能被吞掉");

        // (b) 目標活動完全未被處理到，其鏡像／狀態與取消前一致（取消發生在 GetForUpdateAsync 被呼叫、
        // 任何 Redis／Postgres 寫回動作之前，見 CancelOnTargetEventGetForUpdateEventRepository）。
        (await GetDatabase().SortedSetScoreAsync(PurchaseQueueAdmissionRedisKeys.Admitted(targetEventId), staleEntryId.ToString())).Should().NotBeNull(
            "目標活動完全未被處理到，鏡像狀態應維持取消前的樣子");
        (await ReadStatusAsync(targetWaitingId)).Should().Be(PurchaseQueueEntryStatus.Waiting);

        // (c) 未處理到的活動仍可被下一輪（不帶取消）重新嘗試處理，最終正確完成收斂。
        await CreateService(maxConcurrentAdmittedBuyers: 1).AdvanceQueueOnceAsync(CancellationToken.None);

        (await GetDatabase().SortedSetScoreAsync(PurchaseQueueAdmissionRedisKeys.Admitted(targetEventId), staleEntryId.ToString())).Should().BeNull(
            "下一輪重新嘗試後，目標活動的名額最終被釋放，不因曾經取消就被視為「已檢查、不再重試」");
        (await ReadStatusAsync(targetWaitingId)).Should().Be(PurchaseQueueEntryStatus.Admitted, "目標活動釋放的名額正常供其等待紀錄使用");
    }

    // design.md Decision 6 第三點／Decision 8「重試與升級策略」（見 tasks.md 9.14）：校正本身於執行
    // 期間再度遇到個別 Redis 呼叫逾時，不因這次個別呼叫逾時而中止整個校正流程；目標活動持續可被
    // 下一輪重試，不設重試次數上限；名額釋放延後到下一次真正成功執行完該筆操作的校正。這裡重用
    // PQ-COMPLETE-003 情境（同 9.7／9.12）驗證：即使 ZREM 持續失敗數輪，其他同時進行的活動不受影響、
    // 目標活動最終仍會在故障排除後收斂，不做反向補償、不記錄為需要人工介入的錯誤。
    [Fact]
    public async Task IndividualRedisOperationTimeoutDuringReconciliation_DoesNotAbortTheRoundAndConvergesOnceItSucceeds()
    {
        using var scope = _factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var (targetEventId, staleEntryId, targetWaitingId) = await SeedCompletedButUnsyncedEntryAsync(dbContext);
        var otherEventId = await PurchaseQueueLeaderElectionTestData.SeedQueueModeEventAsync(dbContext);
        // 佔用名額，讓 otherWaiting 沒有空位可被立即推進，才能在下面的 for 迴圈中間觀察到它仍是
        // Waiting（否則第一輪就會被推進為 Admitted，後面的斷言雖然仍會通過，但不足以證明「不受目標
        // 活動的同步問題影響」，因為它本來就會在完全無關的情況下被推進）。
        await PurchaseQueueLeaderElectionTestData.SeedAdmittedEntryAsync(
            dbContext, otherEventId, DateTime.UtcNow.AddMinutes(-30), DateTime.UtcNow.AddMinutes(-25), DateTime.UtcNow.AddMinutes(30));
        var otherWaiting = await PurchaseQueueLeaderElectionTestData.SeedWaitingEntryAsync(dbContext, otherEventId, DateTime.UtcNow.AddMinutes(-10));

        // 連續兩輪：真正注入 ZREM 持續失敗（比照 9.15 的技巧，只讓目標 entryId 的 ZREM 呼叫逾時，
        // 其餘呼叫轉發至真實 Redis），目標活動不收斂，但同一輪的其他活動不受影響。
        var admittedKey = PurchaseQueueAdmissionRedisKeys.Admitted(targetEventId);
        var failingConnection = CreateFailNTimesOnSortedSetRemoveConnectionMultiplexer(admittedKey, staleEntryId.ToString(), failuresBeforeSuccess: 2);
        for (var i = 0; i < 2; i++)
        {
            await CreateService(maxConcurrentAdmittedBuyers: 1, connectionMultiplexer: failingConnection).AdvanceQueueOnceAsync(CancellationToken.None);
        }

        (await GetDatabase().SortedSetScoreAsync(admittedKey, staleEntryId.ToString())).Should().NotBeNull(
            "目標活動的名額尚未釋放（因為 ZREM 持續失敗），維持保守，不超額");
        (await ReadStatusAsync(otherWaiting.Id)).Should().Be(PurchaseQueueEntryStatus.Waiting, "不因目標活動持續存在同步問題而影響其他活動的正常推進，仍受限於其自身的名額");

        // 「故障排除」：下一次校正本身（Redis 端呼叫）正常執行，收斂。
        await CreateService(maxConcurrentAdmittedBuyers: 1, connectionMultiplexer: failingConnection).AdvanceQueueOnceAsync(CancellationToken.None);
        (await GetDatabase().SortedSetScoreAsync(admittedKey, staleEntryId.ToString())).Should().BeNull(
            "故障排除後，下一次成功執行的校正 MUST 完成收斂");
        (await ReadStatusAsync(targetWaitingId)).Should().Be(PurchaseQueueEntryStatus.Admitted);
    }

    /// <summary>包住真正的 IDatabase：除了目標 key/member 的 SortedSetRemoveAsync 呼叫會依設定的失敗次數
    /// 拋出 RedisTimeoutException 外，其餘所有呼叫（本服務實際會用到的方法）皆轉發至真實 Redis，供
    /// 9.15 驗證 Decision 8「Log 等級升級門檻」（見 tasks.md 9.15）。</summary>
    private IConnectionMultiplexer CreateFailNTimesOnSortedSetRemoveConnectionMultiplexer(RedisKey admittedKey, RedisValue targetMember, int failuresBeforeSuccess)
    {
        var realDatabase = _factory.Services.GetRequiredService<IConnectionMultiplexer>().GetDatabase();
        var mockDatabase = new Mock<IDatabase>();
        mockDatabase
            .Setup(d => d.ScriptEvaluateAsync(It.IsAny<string>(), It.IsAny<RedisKey[]>(), It.IsAny<RedisValue[]>(), It.IsAny<CommandFlags>()))
            .Returns<string, RedisKey[], RedisValue[], CommandFlags>((s, k, v, f) => realDatabase.ScriptEvaluateAsync(s, k, v, f));
        mockDatabase
            .Setup(d => d.SortedSetAddAsync(It.IsAny<RedisKey>(), It.IsAny<RedisValue>(), It.IsAny<double>(), It.IsAny<CommandFlags>()))
            .Returns<RedisKey, RedisValue, double, CommandFlags>((k, m, s, f) => realDatabase.SortedSetAddAsync(k, m, s, f));
        mockDatabase
            .Setup(d => d.SortedSetScoreAsync(It.IsAny<RedisKey>(), It.IsAny<RedisValue>(), It.IsAny<CommandFlags>()))
            .Returns<RedisKey, RedisValue, CommandFlags>((k, m, f) => realDatabase.SortedSetScoreAsync(k, m, f));
        mockDatabase
            .Setup(d => d.KeyExistsAsync(It.IsAny<RedisKey>(), It.IsAny<CommandFlags>()))
            .Returns<RedisKey, CommandFlags>((k, f) => realDatabase.KeyExistsAsync(k, f));

        var failCount = 0;
        mockDatabase
            .Setup(d => d.SortedSetRemoveAsync(It.IsAny<RedisKey>(), It.IsAny<RedisValue>(), It.IsAny<CommandFlags>()))
            .Returns<RedisKey, RedisValue, CommandFlags>((k, m, f) =>
            {
                if (k == admittedKey && m == targetMember && Interlocked.Increment(ref failCount) <= failuresBeforeSuccess)
                {
                    throw new RedisTimeoutException(CommandFlags.None, "Simulated timeout for test.", CommandStatus.Unknown);
                }

                return realDatabase.SortedSetRemoveAsync(k, m, f);
            });

        var mockMultiplexer = new Mock<IConnectionMultiplexer>();
        mockMultiplexer.Setup(m => m.GetDatabase(It.IsAny<int>(), It.IsAny<object?>())).Returns(mockDatabase.Object);
        return mockMultiplexer.Object;
    }

    // PQ-COMPLETE-003／PQLE-REBUILD-004（design.md Decision 8「Log 等級升級門檻」，見 tasks.md 9.15）：
    // 單一實例範圍內，同一 entryId 的 ZREM 連續失敗達門檻時，當次失敗升級為 Error；升級不影響重試
    // 邏輯，成功後仍正常完成並釋放名額。
    [Fact]
    public async Task ZRemFailureDuringReconciliation_EscalatesLogLevelToErrorAtThresholdWithoutAffectingRetry()
    {
        const int threshold = 3;
        using var scope = _factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var (eventId, staleEntryId, waitingId) = await SeedCompletedButUnsyncedEntryAsync(dbContext);

        var admittedKey = PurchaseQueueAdmissionRedisKeys.Admitted(eventId);
        var failingConnection = CreateFailNTimesOnSortedSetRemoveConnectionMultiplexer(admittedKey, staleEntryId.ToString(), failuresBeforeSuccess: threshold);

        var sink = new InMemoryLogEventSink();
        var serilogLogger = new LoggerConfiguration().Enrich.FromLogContext().WriteTo.Sink(sink).CreateLogger();
        using var loggerFactory = new SerilogLoggerFactory(serilogLogger, dispose: true);
        var logger = loggerFactory.CreateLogger<PurchaseQueueAdmissionService>();

        // 連續失敗計數是服務實例內的記憶體狀態，MUST 重複使用同一個實例跨輪呼叫，否則每輪都是全新
        // 計數歸零，永遠不會達到門檻（見 design.md Decision 8「儲存範圍與資料結構」）。
        var service = CreateService(
            maxConcurrentAdmittedBuyers: 1, connectionMultiplexer: failingConnection,
            reconciliationFailureLogUpgradeThreshold: threshold, logger: logger);

        for (var i = 0; i < threshold - 1; i++)
        {
            await service.AdvanceQueueOnceAsync(CancellationToken.None);
        }
        sink.Events.Should().NotContain(e => e.Level == LogEventLevel.Error, "未達門檻前每一輪的失敗僅記錄 Warning");

        await service.AdvanceQueueOnceAsync(CancellationToken.None);
        sink.Events.Should().Contain(e => e.Level == LogEventLevel.Error, "達到門檻的那一輪起，當次失敗 MUST 升級為 Error");

        await service.AdvanceQueueOnceAsync(CancellationToken.None);

        (await GetDatabase().SortedSetScoreAsync(admittedKey, staleEntryId.ToString())).Should().BeNull("升級為 Error 不影響重試邏輯，成功後仍應完成 ZREM 並釋放名額");
        (await ReadStatusAsync(waitingId)).Should().Be(PurchaseQueueEntryStatus.Admitted);
    }

    // PQ-ADMIT-006／PQLE-REBUILD-002a（見 tasks.md 9.15a，測試結構比照 9.15）：步驟 7「偵測並修復
    // 放棄的推進決策」的條件式 UPDATE 連續失敗，同樣適用 Decision 8 的計數/升級機制。
    [Fact]
    public async Task AbandonedPromotionUpdateFailure_EscalatesLogLevelToErrorAtThresholdWithoutAffectingRetry()
    {
        const int threshold = 3;
        using var scope = _factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var eventId = await PurchaseQueueLeaderElectionTestData.SeedQueueModeEventAsync(dbContext);
        var target = await PurchaseQueueLeaderElectionTestData.SeedWaitingEntryAsync(dbContext, eventId, DateTime.UtcNow.AddMinutes(-10));

        // 先讓真實 Lua Script 把 target 從 waiting 移到 admitted、pending TTL 設極短，加速使其過期，
        // 讓後續校正進入步驟 7「放棄的推進決策」判定路徑。
        var realScopeFactory = _factory.Services.GetRequiredService<IServiceScopeFactory>();
        var admitFailingScopeFactory = new PurchaseQueueRepositoryInterceptingScopeFactory(
            realScopeFactory, repo => new FailingPurchaseQueueRepository(repo, failOnAdmitBatchIds: [target.Id]));
        await CreateService(maxConcurrentAdmittedBuyers: 1, admissionPendingTtlSeconds: 1, scopeFactory: admitFailingScopeFactory)
            .AdvanceQueueOnceAsync(CancellationToken.None);
        await Task.Delay(TimeSpan.FromMilliseconds(1500));

        var sink = new InMemoryLogEventSink();
        var serilogLogger = new LoggerConfiguration().Enrich.FromLogContext().WriteTo.Sink(sink).CreateLogger();
        using var loggerFactory = new SerilogLoggerFactory(serilogLogger, dispose: true);
        var logger = loggerFactory.CreateLogger<PurchaseQueueAdmissionService>();

        var conditionalUpdateFailingScopeFactory = new PurchaseQueueRepositoryInterceptingScopeFactory(
            realScopeFactory, repo => new FailingPurchaseQueueRepository(repo, failOnAdmitIfWaitingIds: [target.Id]));

        // 連續失敗計數是服務實例內的記憶體狀態，MUST 重複使用同一個實例跨輪呼叫。
        var service = CreateService(
            maxConcurrentAdmittedBuyers: 1, admissionPendingTtlSeconds: 1,
            reconciliationFailureLogUpgradeThreshold: threshold, logger: logger, scopeFactory: conditionalUpdateFailingScopeFactory);

        for (var i = 0; i < threshold - 1; i++)
        {
            await service.AdvanceQueueOnceAsync(CancellationToken.None);
        }
        sink.Events.Should().NotContain(e => e.Level == LogEventLevel.Error, "未達門檻前每一輪的失敗僅記錄 Warning");

        await service.AdvanceQueueOnceAsync(CancellationToken.None);
        sink.Events.Should().Contain(e => e.Level == LogEventLevel.Error, "達到門檻的那一輪起，當次失敗 MUST 升級為 Error");

        await CreateService(maxConcurrentAdmittedBuyers: 1, admissionPendingTtlSeconds: 1, reconciliationFailureLogUpgradeThreshold: threshold)
            .AdvanceQueueOnceAsync(CancellationToken.None);
        (await ReadStatusAsync(target.Id)).Should().Be(PurchaseQueueEntryStatus.Admitted, "升級為 Error 不影響重試邏輯，Postgres 操作成功時仍正常完成落地");
    }

    // PQLE-REBUILD-003b（見 tasks.md 9.15b，理由同 9.15a）：步驟 8「偵測並修復放棄的逾時標記」的
    // 條件式 UPDATE 連續失敗，同樣適用 Decision 8 的計數/升級機制。
    [Fact]
    public async Task AbandonedExpiryUpdateFailure_EscalatesLogLevelToErrorAtThresholdWithoutAffectingRetry()
    {
        const int threshold = 3;
        using var scope = _factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var eventId = await PurchaseQueueLeaderElectionTestData.SeedQueueModeEventAsync(dbContext);
        var now = DateTime.UtcNow;
        var target = await PurchaseQueueLeaderElectionTestData.SeedAdmittedEntryAsync(dbContext, eventId, now.AddMinutes(-30), now.AddMinutes(-25), now.AddSeconds(1.5));

        // 前置校正讓 target 進入 Redis admitted，再等真實時間經過到期時間，讓下一輪 Lua Script 的
        // ZRANGEBYSCORE 把它原子 ZREM，重現「Redis 已 ZREM、Postgres 未落地」的放棄逾時標記狀態。
        await CreateService().AdvanceQueueOnceAsync(CancellationToken.None);
        await Task.Delay(TimeSpan.FromMilliseconds(2000));
        var realScopeFactory = _factory.Services.GetRequiredService<IServiceScopeFactory>();
        var expireFailingScopeFactory = new PurchaseQueueRepositoryInterceptingScopeFactory(
            realScopeFactory, repo => new FailingPurchaseQueueRepository(repo, failOnExpireBatchIds: [target.Id]));
        await CreateService(scopeFactory: expireFailingScopeFactory).AdvanceQueueOnceAsync(CancellationToken.None);
        (await ReadStatusAsync(target.Id)).Should().Be(PurchaseQueueEntryStatus.Admitted, "批次逾時 UPDATE 例外，Postgres 維持失敗前狀態");

        var sink = new InMemoryLogEventSink();
        var serilogLogger = new LoggerConfiguration().Enrich.FromLogContext().WriteTo.Sink(sink).CreateLogger();
        using var loggerFactory = new SerilogLoggerFactory(serilogLogger, dispose: true);
        var logger = loggerFactory.CreateLogger<PurchaseQueueAdmissionService>();

        var conditionalUpdateFailingScopeFactory = new PurchaseQueueRepositoryInterceptingScopeFactory(
            realScopeFactory, repo => new FailingPurchaseQueueRepository(repo, failOnExpireBatchIds: [target.Id]));

        // 連續失敗計數是服務實例內的記憶體狀態，MUST 重複使用同一個實例跨輪呼叫。
        var service = CreateService(
            reconciliationFailureLogUpgradeThreshold: threshold, logger: logger, scopeFactory: conditionalUpdateFailingScopeFactory);

        for (var i = 0; i < threshold - 1; i++)
        {
            await service.AdvanceQueueOnceAsync(CancellationToken.None);
        }
        sink.Events.Should().NotContain(e => e.Level == LogEventLevel.Error, "未達門檻前每一輪的失敗僅記錄 Warning");

        await service.AdvanceQueueOnceAsync(CancellationToken.None);
        sink.Events.Should().Contain(e => e.Level == LogEventLevel.Error, "達到門檻的那一輪起，當次失敗 MUST 升級為 Error");

        await CreateService(reconciliationFailureLogUpgradeThreshold: threshold).AdvanceQueueOnceAsync(CancellationToken.None);
        (await ReadStatusAsync(target.Id)).Should().Be(PurchaseQueueEntryStatus.Expired, "升級為 Error 不影響重試邏輯，Postgres 操作成功時仍正常完成落地");
    }

    // ## 9a. 補充故障邊界測試（第六輪審查要求）

    // 9a.1：驗證 A_pg 與 A_pg_overdue 的互斥性。
    [Fact]
    public async Task Reconciliation_OverdueAdmittedEntryNeverSyncedToRedis_IsHandledOnlyByAbandonedExpiryStepNotGapFill()
    {
        using var scope = _factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var eventId = await PurchaseQueueLeaderElectionTestData.SeedQueueModeEventAsync(dbContext);
        var now = DateTime.UtcNow;
        var overdue = await PurchaseQueueLeaderElectionTestData.SeedAdmittedEntryAsync(dbContext, eventId, now.AddMinutes(-30), now.AddMinutes(-25), now.AddMinutes(-1));

        await CreateService().AdvanceQueueOnceAsync(CancellationToken.None);

        (await ReadStatusAsync(overdue.Id)).Should().Be(PurchaseQueueEntryStatus.Expired, "應只被步驟 8 處理為 Expired");
        (await GetDatabase().SortedSetScoreAsync(PurchaseQueueAdmissionRedisKeys.Admitted(eventId), overdue.Id.ToString())).Should().BeNull(
            "屬於 A_pg_overdue，MUST NOT 被步驟 4 誤判為需要補回 admitted");
    }

    // 9a.2：只刪除某活動的 pending:{entryId} key，執行校正，斷言僅該筆放棄判定被觸發，其餘紀錄不受影響。
    [Fact]
    public async Task Reconciliation_WhenOnlyOnePendingMarkerKeyIsMissing_OnlyThatEntrysAbandonedDecisionIsTriggered()
    {
        using var scope = _factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var eventId = await PurchaseQueueLeaderElectionTestData.SeedQueueModeEventAsync(dbContext, maxConcurrentAdmittedBuyers: 2);
        var target = await PurchaseQueueLeaderElectionTestData.SeedWaitingEntryAsync(dbContext, eventId, DateTime.UtcNow.AddMinutes(-10));
        var other = await PurchaseQueueLeaderElectionTestData.SeedWaitingEntryAsync(dbContext, eventId, DateTime.UtcNow.AddMinutes(-5));

        var realScopeFactory = _factory.Services.GetRequiredService<IServiceScopeFactory>();
        var failingScopeFactory = new PurchaseQueueRepositoryInterceptingScopeFactory(
            realScopeFactory, repo => new FailingPurchaseQueueRepository(repo, failOnAdmitBatchIds: [target.Id, other.Id]));
        await CreateService(maxConcurrentAdmittedBuyers: 2, scopeFactory: failingScopeFactory).AdvanceQueueOnceAsync(CancellationToken.None);

        var database = GetDatabase();
        await database.KeyDeleteAsync(PurchaseQueueAdmissionRedisKeys.Pending(target.Id));

        await CreateService(maxConcurrentAdmittedBuyers: 2).AdvanceQueueOnceAsync(CancellationToken.None);

        (await ReadStatusAsync(target.Id)).Should().Be(PurchaseQueueEntryStatus.Admitted, "target 的 pending 標記遺失，MUST 被代為完成落地");
        (await ReadStatusAsync(other.Id)).Should().Be(PurchaseQueueEntryStatus.Waiting, "other 的 pending 標記仍存在，MUST NOT 被誤判為放棄");
    }

    // 9a.3：只刪除某活動的 waiting zset（admitted 正常），執行校正，斷言 waiting 被正確從 Postgres
    // 重建，admitted 不受影響。
    [Fact]
    public async Task Reconciliation_WhenOnlyWaitingMirrorIsMissing_RebuildsWaitingWithoutAffectingAdmitted()
    {
        using var scope = _factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var eventId = await PurchaseQueueLeaderElectionTestData.SeedQueueModeEventAsync(dbContext, maxConcurrentAdmittedBuyers: 1);
        var waiting = await PurchaseQueueLeaderElectionTestData.SeedWaitingEntryAsync(dbContext, eventId, DateTime.UtcNow.AddMinutes(-10));
        var admitted = await PurchaseQueueLeaderElectionTestData.SeedAdmittedEntryAsync(dbContext, eventId, DateTime.UtcNow.AddMinutes(-30), DateTime.UtcNow.AddMinutes(-25), DateTime.UtcNow.AddMinutes(30));

        await CreateService(maxConcurrentAdmittedBuyers: 1).AdvanceQueueOnceAsync(CancellationToken.None);

        var database = GetDatabase();
        var waitingKey = PurchaseQueueAdmissionRedisKeys.Waiting(eventId);
        var admittedKey = PurchaseQueueAdmissionRedisKeys.Admitted(eventId);
        await database.KeyDeleteAsync(waitingKey);

        await CreateService(maxConcurrentAdmittedBuyers: 1).AdvanceQueueOnceAsync(CancellationToken.None);

        (await database.SortedSetScoreAsync(waitingKey, waiting.Id.ToString())).Should().NotBeNull("waiting 應被正確從 Postgres 重建");
        (await database.SortedSetScoreAsync(admittedKey, admitted.Id.ToString())).Should().NotBeNull("admitted 不受影響");
        (await ReadStatusAsync(waiting.Id)).Should().Be(PurchaseQueueEntryStatus.Waiting, "名額已滿，重建後不應被誤推進");
    }

    // 9a.4：只刪除某活動的 admitted zset（waiting 正常），執行校正，斷言 admitted 被正確從 Postgres
    // 重建（含逾時判斷），waiting 不受影響。
    [Fact]
    public async Task Reconciliation_WhenOnlyAdmittedMirrorIsMissing_RebuildsAdmittedIncludingOverdueJudgmentWithoutAffectingWaiting()
    {
        using var scope = _factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var eventId = await PurchaseQueueLeaderElectionTestData.SeedQueueModeEventAsync(dbContext, maxConcurrentAdmittedBuyers: 2);
        var waiting = await PurchaseQueueLeaderElectionTestData.SeedWaitingEntryAsync(dbContext, eventId, DateTime.UtcNow.AddMinutes(-10));
        var active = await PurchaseQueueLeaderElectionTestData.SeedAdmittedEntryAsync(dbContext, eventId, DateTime.UtcNow.AddMinutes(-30), DateTime.UtcNow.AddMinutes(-25), DateTime.UtcNow.AddMinutes(30));
        var overdue = await PurchaseQueueLeaderElectionTestData.SeedAdmittedEntryAsync(dbContext, eventId, DateTime.UtcNow.AddMinutes(-40), DateTime.UtcNow.AddMinutes(-35), DateTime.UtcNow.AddMinutes(-1));
        // 第二筆佔用名額的 Admitted 紀錄，讓 waiting 完全沒有空位可被立即推進（active 只占了 1 個，
        // maxConcurrentAdmittedBuyers: 2 還留 1 個空位，會讓 waiting 在下面的校正輪被立即推進，
        // 使「waiting 鏡像不受影響」的斷言恆假）。
        var active2 = await PurchaseQueueLeaderElectionTestData.SeedAdmittedEntryAsync(dbContext, eventId, DateTime.UtcNow.AddMinutes(-30), DateTime.UtcNow.AddMinutes(-25), DateTime.UtcNow.AddMinutes(30));

        await CreateService(maxConcurrentAdmittedBuyers: 2).AdvanceQueueOnceAsync(CancellationToken.None);

        var database = GetDatabase();
        var waitingKey = PurchaseQueueAdmissionRedisKeys.Waiting(eventId);
        var admittedKey = PurchaseQueueAdmissionRedisKeys.Admitted(eventId);
        await database.KeyDeleteAsync(admittedKey);

        await CreateService(maxConcurrentAdmittedBuyers: 2).AdvanceQueueOnceAsync(CancellationToken.None);

        (await database.SortedSetScoreAsync(admittedKey, active.Id.ToString())).Should().NotBeNull("尚未逾時的 Admitted 紀錄應被重建進 admitted 鏡像");
        (await database.SortedSetScoreAsync(admittedKey, active2.Id.ToString())).Should().NotBeNull("尚未逾時的 Admitted 紀錄應被重建進 admitted 鏡像");
        (await database.SortedSetScoreAsync(admittedKey, overdue.Id.ToString())).Should().BeNull("已逾時的紀錄不屬於補齊範圍（A_pg_overdue），應改由放棄逾時標記步驟處理");
        (await ReadStatusAsync(overdue.Id)).Should().Be(PurchaseQueueEntryStatus.Expired, "已逾時但未落地的紀錄應被步驟 8 標記為 Expired");
        (await database.SortedSetScoreAsync(waitingKey, waiting.Id.ToString())).Should().NotBeNull("waiting 鏡像不受影響");
    }

    // 9a.5：部分 FLUSH（模擬同時清空多個活動中的其中一個活動的所有 key，其餘活動不受影響），執行校正，
    // 斷言只有受影響活動被重建，其餘活動的鏡像與有效名額不受干擾。
    [Fact]
    public async Task Reconciliation_WhenOnlyOneEventsKeysAreFlushed_RebuildsOnlyThatEventWithoutAffectingOthers()
    {
        using var scope = _factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var flushedEventId = await PurchaseQueueLeaderElectionTestData.SeedQueueModeEventAsync(dbContext, maxConcurrentAdmittedBuyers: 1);
        var flushedWaiting = await PurchaseQueueLeaderElectionTestData.SeedWaitingEntryAsync(dbContext, flushedEventId, DateTime.UtcNow.AddMinutes(-10));
        // 佔用名額的 Admitted 紀錄，讓 flushedWaiting 沒有空位可被立即推進（同下方 CreateService 的
        // maxConcurrentAdmittedBuyers: 1，否則校正補齊 waiting 鏡像後會在同一輪被立即推進為 Admitted）。
        await PurchaseQueueLeaderElectionTestData.SeedAdmittedEntryAsync(
            dbContext, flushedEventId, DateTime.UtcNow.AddMinutes(-30), DateTime.UtcNow.AddMinutes(-25), DateTime.UtcNow.AddMinutes(30));
        var intactEventId = await PurchaseQueueLeaderElectionTestData.SeedQueueModeEventAsync(dbContext, maxConcurrentAdmittedBuyers: 1);
        var intactAdmitted = await PurchaseQueueLeaderElectionTestData.SeedAdmittedEntryAsync(dbContext, intactEventId, DateTime.UtcNow.AddMinutes(-30), DateTime.UtcNow.AddMinutes(-25), DateTime.UtcNow.AddMinutes(30));

        await CreateService(maxConcurrentAdmittedBuyers: 1).AdvanceQueueOnceAsync(CancellationToken.None);

        var database = GetDatabase();
        var flushedWaitingKey = PurchaseQueueAdmissionRedisKeys.Waiting(flushedEventId);
        var intactAdmittedKey = PurchaseQueueAdmissionRedisKeys.Admitted(intactEventId);
        await database.KeyDeleteAsync(flushedWaitingKey);

        await CreateService(maxConcurrentAdmittedBuyers: 1).AdvanceQueueOnceAsync(CancellationToken.None);

        (await database.SortedSetScoreAsync(flushedWaitingKey, flushedWaiting.Id.ToString())).Should().NotBeNull("受影響活動的鏡像應被重建");
        (await database.SortedSetScoreAsync(intactAdmittedKey, intactAdmitted.Id.ToString())).Should().NotBeNull("未受影響活動的鏡像不應被干擾");
    }

    // 9a.6：模擬 Redis 命令結果未知（呼叫逾時但伺服器端其實已執行成功），斷言重複執行同一命令為安全
    // no-op，不產生重複或矛盾狀態——直接對真實 Redis 重複呼叫 ZADD／ZREM 驗證其原生冪等性。
    [Fact]
    public async Task RepeatedZAddAndZRemCalls_AreSafeNoOpsRegardlessOfWhetherThePreviousCallsResultWasObserved()
    {
        var database = GetDatabase();
        var key = $"pq:admit:test:{Guid.NewGuid():N}:waiting";
        var member = Guid.NewGuid().ToString();

        var first = await database.SortedSetAddAsync(key, member, 100d);
        var second = await database.SortedSetAddAsync(key, member, 100d);
        first.Should().BeTrue();
        second.Should().BeFalse("成員已存在，重複 ZADD 為 no-op（回傳新增數量 0）");
        (await database.SortedSetScoreAsync(key, member)).Should().Be(100d);

        var firstRemove = await database.SortedSetRemoveAsync(key, member);
        var secondRemove = await database.SortedSetRemoveAsync(key, member);
        firstRemove.Should().BeTrue();
        secondRemove.Should().BeFalse("成員已不存在，重複 ZREM 為 no-op（回傳 false）");

        await database.KeyDeleteAsync(key);
    }

    // 9a.7：複合情境——校正執行期間，另一背景服務實例同時對同一活動執行推進，且過程中一次個別操作
    // 因 Postgres 寫回失敗而中斷，斷言最終收斂正確（不超額、不遺漏、不重複推進），不需要校正與推進
    // 互相等待或線性化。
    [Fact]
    public async Task ConcurrentAdvanceWithOneTransientFailure_StillConvergesCorrectlyWithoutRequiringMutualExclusion()
    {
        using var scope = _factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var eventId = await PurchaseQueueLeaderElectionTestData.SeedQueueModeEventAsync(dbContext, maxConcurrentAdmittedBuyers: 2);
        var w1 = await PurchaseQueueLeaderElectionTestData.SeedWaitingEntryAsync(dbContext, eventId, DateTime.UtcNow.AddMinutes(-10));
        var w2 = await PurchaseQueueLeaderElectionTestData.SeedWaitingEntryAsync(dbContext, eventId, DateTime.UtcNow.AddMinutes(-5));

        var realScopeFactory = _factory.Services.GetRequiredService<IServiceScopeFactory>();
        var failingScopeFactory = new PurchaseQueueRepositoryInterceptingScopeFactory(
            realScopeFactory, repo => new FailingPurchaseQueueRepository(repo, failOnAdmitBatchIds: [w1.Id]));

        var serviceA = CreateService(maxConcurrentAdmittedBuyers: 2, scopeFactory: failingScopeFactory);
        var serviceB = CreateService(maxConcurrentAdmittedBuyers: 2);
        await Task.WhenAll(
            serviceA.AdvanceQueueOnceAsync(CancellationToken.None),
            serviceB.AdvanceQueueOnceAsync(CancellationToken.None));

        // 不論交錯順序為何，最終收斂：靠後續正常輪次完成。
        await CreateService(maxConcurrentAdmittedBuyers: 2).AdvanceQueueOnceAsync(CancellationToken.None);

        var admittedCount = await ReadAdmittedCountAsync(eventId);
        admittedCount.Should().BeLessThanOrEqualTo(2, "不應超額入場");

        var database = GetDatabase();
        var waitingKey = PurchaseQueueAdmissionRedisKeys.Waiting(eventId);
        var admittedKey = PurchaseQueueAdmissionRedisKeys.Admitted(eventId);
        foreach (var id in new[] { w1.Id, w2.Id })
        {
            var inWaiting = (await database.SortedSetScoreAsync(waitingKey, id.ToString())) is not null;
            var inAdmitted = (await database.SortedSetScoreAsync(admittedKey, id.ToString())) is not null;
            (inWaiting && inAdmitted).Should().BeFalse($"entry {id} MUST NOT 同時出現在兩個鏡像");
        }
    }

    private async Task<int> ReadAdmittedCountAsync(Guid eventId)
    {
        using var scope = _factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        return await dbContext.PurchaseQueueEntries.AsNoTracking()
            .CountAsync(e => e.EventId == eventId && e.Status == PurchaseQueueEntryStatus.Admitted);
    }

    // 9a.8：重複校正的冪等性——對同一活動連續執行兩次校正（中間沒有任何狀態變化），斷言第二次執行
    // 對 Redis／Postgres 完全無副作用（no-op）。
    [Fact]
    public async Task ConsecutiveReconciliationRoundsWithNoStateChangeInBetween_TheSecondRoundIsANoOp()
    {
        using var scope = _factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var eventId = await PurchaseQueueLeaderElectionTestData.SeedQueueModeEventAsync(dbContext, maxConcurrentAdmittedBuyers: 2);
        var waiting = await PurchaseQueueLeaderElectionTestData.SeedWaitingEntryAsync(dbContext, eventId, DateTime.UtcNow.AddMinutes(-10));
        var admitted = await PurchaseQueueLeaderElectionTestData.SeedAdmittedEntryAsync(dbContext, eventId, DateTime.UtcNow.AddMinutes(-30), DateTime.UtcNow.AddMinutes(-25), DateTime.UtcNow.AddMinutes(30));

        await CreateService(maxConcurrentAdmittedBuyers: 2).AdvanceQueueOnceAsync(CancellationToken.None);

        var database = GetDatabase();
        var waitingKey = PurchaseQueueAdmissionRedisKeys.Waiting(eventId);
        var admittedKey = PurchaseQueueAdmissionRedisKeys.Admitted(eventId);
        var waitingScoreBefore = await database.SortedSetScoreAsync(waitingKey, waiting.Id.ToString());
        var admittedScoreBefore = await database.SortedSetScoreAsync(admittedKey, admitted.Id.ToString());
        var statusBefore = await ReadStatusAsync(waiting.Id);

        await CreateService(maxConcurrentAdmittedBuyers: 2).AdvanceQueueOnceAsync(CancellationToken.None);

        (await database.SortedSetScoreAsync(waitingKey, waiting.Id.ToString())).Should().Be(waitingScoreBefore, "第二次執行對 Redis 完全無副作用");
        (await database.SortedSetScoreAsync(admittedKey, admitted.Id.ToString())).Should().Be(admittedScoreBefore);
        (await ReadStatusAsync(waiting.Id)).Should().Be(statusBefore, "第二次執行對 Postgres 完全無副作用");
    }

    // PQ-ADMIT-004（本輪審查要求，見 tasks.md 9.16）：驗證批次 ExecuteUpdateAsync 只回傳總影響列數、
    // 無法逐筆判定的前提下，混合批次（部分前置狀態已被搶先處理）仍能正確收斂。
    [Fact]
    public async Task AdmitBatchAsync_WhenSomeEntriesPreemptedBeforeTheBatchRuns_LeavesThemUntouchedAndStillAdmitsTheRest()
    {
        using var scope = _factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var eventId = await PurchaseQueueLeaderElectionTestData.SeedQueueModeEventAsync(dbContext);
        var now = DateTime.UtcNow;
        var entries = new List<PurchaseQueueEntry>();
        for (var i = 0; i < 5; i++)
        {
            entries.Add(await PurchaseQueueLeaderElectionTestData.SeedWaitingEntryAsync(dbContext, eventId, now.AddMinutes(-30 + i)));
        }

        var repository = scope.ServiceProvider.GetRequiredService<IPurchaseQueueRepository>();
        // 模擬「批次送出時前置狀態已不成立」：3 筆搶先被其他路徑處理掉（2 筆先行落地為 Admitted，
        // 1 筆直接改寫為 Expired，涵蓋不同的前置狀態失效方式）。
        await repository.AdmitIfWaitingAsync(entries[0].Id, now, now.AddMinutes(10), CancellationToken.None);
        await repository.AdmitIfWaitingAsync(entries[1].Id, now, now.AddMinutes(20), CancellationToken.None);
        using (var writeScope = _factory.Services.CreateScope())
        {
            var writeDbContext = writeScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var tracked = await writeDbContext.PurchaseQueueEntries.SingleAsync(e => e.Id == entries[2].Id);
            tracked.Admit(now, now.AddMinutes(5));
            tracked.Expire();
            await writeDbContext.SaveChangesAsync();
        }

        var allIds = entries.Select(e => e.Id).ToList();
        var act = () => repository.AdmitBatchAsync(allIds, now, now.AddMinutes(30), CancellationToken.None);
        var affected = await act.Should().NotThrowAsync("批次呼叫本身應正常完成、不拋例外，即使部分前置狀態已不成立");
        affected.Subject.Should().Be(2, "只有 2 筆前置狀態仍是 Waiting，只有這 2 筆實際受影響");

        (await ReadStatusAsync(entries[0].Id)).Should().Be(PurchaseQueueEntryStatus.Admitted, "搶先落地的狀態不應被批次 UPDATE 的舊決策蓋掉");
        (await ReadStatusAsync(entries[2].Id)).Should().Be(PurchaseQueueEntryStatus.Expired, "已是 Expired 的紀錄不應被覆寫");
        (await ReadStatusAsync(entries[3].Id)).Should().Be(PurchaseQueueEntryStatus.Admitted, "其餘前置狀態仍成立的紀錄正確推進");
        (await ReadStatusAsync(entries[4].Id)).Should().Be(PurchaseQueueEntryStatus.Admitted);
    }
}
