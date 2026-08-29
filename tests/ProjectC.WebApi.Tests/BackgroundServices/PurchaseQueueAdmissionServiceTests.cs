using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using ProjectC.Application.Common;
using ProjectC.Application.Common.Interfaces;
using ProjectC.Domain.Events;
using ProjectC.Domain.Members;
using ProjectC.Domain.PurchaseQueue;
using ProjectC.Domain.Venues;
using ProjectC.Infrastructure.Persistence;
using ProjectC.WebApi.BackgroundServices;
using ProjectC.WebApi.Tests.TestSupport;

namespace ProjectC.WebApi.Tests.BackgroundServices;

// purchase-queue spec：排隊入場名額依先後順序推進（PQ-ADMIT-001~004）、等待中的排隊紀錄沒有自身逾時機制
// （PQ-WAIT-001）、Admin 關閉熱門搶購模式後既有排隊紀錄不主動清理（PQ-TOGGLE-001~002）、建立訂單成功後
// 標記排隊紀錄為已完成，名額即時釋放（PQ-COMPLETE-002）。
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
        admittedCount.Should().Be(2, "同一活動同時只能有一次推進在進行，最終有效入場名額不應超過設定上限");
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

        // 模擬 OrderService.PlaceOrderAsync 成功建立訂單後，在同一交易內呼叫 Complete()（PQ-COMPLETE-001 已於
        // OrderServiceTests 驗證該呼叫本身；這裡驗證 PQ-COMPLETE-002：名額於交易提交後立即可供下一位使用）。
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
            NullLogger<PurchaseQueueAdmissionService>.Instance);

        await service.AdvanceQueueOnceAsync(CancellationToken.None);

        (await ReadStatusAsync(waiting.Id)).Should().Be(PurchaseQueueEntryStatus.Waiting,
            "掃描之後、本活動實際處理之前 Admin 關閉了熱門搶購模式，交易內重新鎖定確認後 MUST 以最新值為準跳過，不放行任何入場");
    }

    [Fact]
    public void TestingEnvironment_DoesNotRegisterTheRealBackgroundService()
    {
        var hostedServices = _factory.Services.GetServices<IHostedService>();

        hostedServices.Should().NotContain(service => service is PurchaseQueueAdmissionService);
    }
}
