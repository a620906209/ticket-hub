using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using ProjectC.Application.Common;
using ProjectC.Application.Common.Interfaces;
using ProjectC.Application.PurchaseQueue.GetMyQueueStatus;
using ProjectC.Application.PurchaseQueue.JoinPurchaseQueue;
using ProjectC.Domain.Events;
using ProjectC.Domain.Members;
using ProjectC.Domain.PurchaseQueue;
using ProjectC.Domain.Venues;
using ProjectC.Infrastructure.Persistence;
using ProjectC.Infrastructure.Persistence.Repositories;
using ProjectC.Infrastructure.PurchaseQueue;
using ProjectC.Infrastructure.Tests.TestSupport;
using StackExchange.Redis;

namespace ProjectC.Infrastructure.Tests.PurchaseQueue;

// 驗證 JoinPurchaseQueueHandler 搭配「真實」EventRepository／PurchaseQueueRepository／UnitOfWork（而非
// Application.Tests 用的 in-memory Fake）在真實 PostgreSQL 上的行為（tasks.md 5.1a／12.4c，對應
// purchase-queue spec PQ-JOIN-003；Medium 問題 3 對應 tasks.md 5.1 步驟 2）——這是本次審查發現的重點：
// FakePurchaseQueueRepository 是單執行緒、共用參考的記憶體實作，existing.Expire() 對它而言是立即生效的
// 同步變更，完全無法重現真實 PurchaseQueueRepository（raw SQL INSERT ... ON CONFLICT 繞過 ChangeTracker）
// 對寫入順序的要求，Application.Tests 的綠燈不能作為「資料庫實際落地結果正確」的證據。
[Collection(PostgresCollection.Name)]
public class JoinPurchaseQueueHandlerIntegrationTests
{
    private readonly PostgresFixture _fixture;

    public JoinPurchaseQueueHandlerIntegrationTests(PostgresFixture fixture)
    {
        _fixture = fixture;
    }

    private sealed class FixedDateTimeProvider : IDateTimeProvider
    {
        public FixedDateTimeProvider(DateTime utcNow) => UtcNow = utcNow;

        public DateTime UtcNow { get; }
    }

    /// <summary>包住真正的 EventRepository，在第一次 GetByIdAsync 回傳「之後」（模擬 JoinPurchaseQueueHandler
    /// 交易前快速失敗檢查已完成）觸發一次由呼叫端指定的併發寫入，藉此在不修改 Handler 本身的情況下精確控制
    /// 「交易前檢查」與「交易內鎖定」之間的交錯時機——比照 OrderServiceQueueModeLinearizationTests 的手法。</summary>
    private sealed class GetByIdInterceptingEventRepository : IEventRepository
    {
        private readonly IEventRepository _inner;
        private readonly Func<Task> _onFirstGetByIdAsync;
        private int _triggered;

        public GetByIdInterceptingEventRepository(IEventRepository inner, Func<Task> onFirstGetByIdAsync)
        {
            _inner = inner;
            _onFirstGetByIdAsync = onFirstGetByIdAsync;
        }

        public async Task<Event?> GetByIdAsync(Guid id, CancellationToken cancellationToken)
        {
            var result = await _inner.GetByIdAsync(id, cancellationToken);
            if (Interlocked.Exchange(ref _triggered, 1) == 0)
            {
                await _onFirstGetByIdAsync();
            }

            return result;
        }

        public Task<IReadOnlyList<Event>> GetAllAsync(CancellationToken cancellationToken) => _inner.GetAllAsync(cancellationToken);

        public Task<IReadOnlyList<Event>> GetByOrganizerIdAsync(Guid organizerId, CancellationToken cancellationToken) => _inner.GetByOrganizerIdAsync(organizerId, cancellationToken);

        public Task<IReadOnlyList<Event>> GetByIdsAsync(IReadOnlyList<Guid> eventIds, CancellationToken cancellationToken) => _inner.GetByIdsAsync(eventIds, cancellationToken);

        public void Add(Event @event) => _inner.Add(@event);

        public void Update(Event @event) => _inner.Update(@event);

        public Task<Event?> GetForUpdateAsync(Guid eventId, CancellationToken cancellationToken) => _inner.GetForUpdateAsync(eventId, cancellationToken);
    }

    private async Task<(Guid EventId, Guid MemberId)> SeedEventAndMemberAsync(ApplicationDbContext dbContext, bool isQueueModeEnabled = true)
    {
        var venue = new Venue(Guid.NewGuid(), $"Test Venue {Guid.NewGuid():N}");
        var seatMap = new SeatMap(Guid.NewGuid(), venue.Id);
        var organizerId = await OrganizerTestData.SeedApprovedOrganizerAsync(dbContext);
        var @event = new Event(Guid.NewGuid(), "Test Event", DateTime.UtcNow.AddDays(30), venue.Id, seatMap.Id, organizerId);
        if (isQueueModeEnabled)
        {
            @event.EnableQueueMode();
        }

        var member = Member.Register($"buyer-{Guid.NewGuid():N}@example.com", "Test Buyer", "hash");

        dbContext.Venues.Add(venue);
        dbContext.SeatMaps.Add(seatMap);
        dbContext.Events.Add(@event);
        dbContext.Members.Add(member);
        await dbContext.SaveChangesAsync();

        return (@event.Id, member.Id);
    }

    [Fact]
    public async Task HandleAsync_WhenExistingAdmittedEntryIsExpired_PersistsBothTheExpiredOldEntryAndANewWaitingEntry()
    {
        // PQ-JOIN-003：驗證 tasks.md 5.1a（AddOrGetExistingAsync 必須在 INSERT 前先 flush ChangeTracker）
        // 確實生效——若沒有正確實作，資料庫裡舊紀錄在 INSERT 當下仍是 Admitted，ON CONFLICT DO NOTHING
        // 會誤判撞到既有進行中紀錄而略過插入，新的 Waiting 紀錄不會被建立，回傳值會是舊紀錄的 Id。
        var now = new DateTime(2026, 1, 1, 10, 0, 0, DateTimeKind.Utc);

        await using var seedDbContext = _fixture.CreateDbContext();
        var (eventId, memberId) = await SeedEventAndMemberAsync(seedDbContext);

        var expiredEntry = new PurchaseQueueEntry(Guid.NewGuid(), eventId, memberId, now.AddMinutes(-20));
        expiredEntry.Admit(now.AddMinutes(-15), now.AddMinutes(-1));
        await using (var entryDbContext = _fixture.CreateDbContext())
        {
            entryDbContext.PurchaseQueueEntries.Add(expiredEntry);
            await entryDbContext.SaveChangesAsync();
        }

        await using var dbContext = _fixture.CreateDbContext();
        var handler = new JoinPurchaseQueueHandler(
            new EventRepository(dbContext),
            new PurchaseQueueRepository(dbContext),
            new UnitOfWork(dbContext),
            new FixedDateTimeProvider(now),
            new JoinPurchaseQueueRequestValidator(),
            new FakeCaptchaService(),
            new FakePurchaseQueueAdmissionMirror(),
            new MemberRealNameRepository(dbContext));

        var request = new JoinPurchaseQueueRequest(FakeCaptchaService.ValidToken, FakeCaptchaService.ValidAnswer);
        var result = await handler.HandleAsync(eventId, memberId, request, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().NotBe(expiredEntry.Id, "逾時後重新加入應該建立一筆新紀錄，不是回傳舊紀錄的 Id");

        await using var readDbContext = _fixture.CreateDbContext();
        var entries = await readDbContext.PurchaseQueueEntries.AsNoTracking()
            .Where(e => e.EventId == eventId && e.MemberId == memberId)
            .ToListAsync();

        entries.Should().HaveCount(2, "舊的逾時紀錄與新紀錄都應該保留在資料庫中");

        var persistedOldEntry = entries.Single(e => e.Id == expiredEntry.Id);
        persistedOldEntry.Status.Should().Be(PurchaseQueueEntryStatus.Expired);

        var persistedNewEntry = entries.SingleOrDefault(e => e.Id == result.Value);
        persistedNewEntry.Should().NotBeNull(
            "5.1a 若未正確實作，raw SQL INSERT 會在舊紀錄仍是 Admitted 時執行，ON CONFLICT DO NOTHING 讓新紀錄從未真正寫入資料庫");
        persistedNewEntry!.Status.Should().Be(PurchaseQueueEntryStatus.Waiting);
    }

    [Fact]
    public async Task HandleAsync_WhenQueueModeIsDisabledByAdminAfterThePreCheckButBeforeTheTransactionLock_RejectsUsingTheLatestValue()
    {
        // Medium 問題 3（design.md 決策 3 第 3 點；tasks.md 5.1 步驟 2）：交易前的快速失敗檢查不具權威性，
        // 若 Admin 在該次讀取之後、交易內鎖定之前關閉熱門搶購模式，MUST 以鎖定後的最新值為準拒絕加入，
        // 不得讓一筆活動已關閉排隊的 Waiting 紀錄意外建立成功。
        var now = DateTime.UtcNow;

        await using var seedDbContext = _fixture.CreateDbContext();
        var (eventId, memberId) = await SeedEventAndMemberAsync(seedDbContext, isQueueModeEnabled: true);

        await using var dbContext = _fixture.CreateDbContext();
        var interceptingEventRepository = new GetByIdInterceptingEventRepository(new EventRepository(dbContext), async () =>
        {
            await using var writerDbContext = _fixture.CreateDbContext();
            var eventEntity = await writerDbContext.Events.SingleAsync(e => e.Id == eventId);
            eventEntity.DisableQueueMode();
            await writerDbContext.SaveChangesAsync();
        });
        var handler = new JoinPurchaseQueueHandler(
            interceptingEventRepository,
            new PurchaseQueueRepository(dbContext),
            new UnitOfWork(dbContext),
            new FixedDateTimeProvider(now),
            new JoinPurchaseQueueRequestValidator(),
            new FakeCaptchaService(),
            new FakePurchaseQueueAdmissionMirror(),
            new MemberRealNameRepository(dbContext));

        var request = new JoinPurchaseQueueRequest(FakeCaptchaService.ValidToken, FakeCaptchaService.ValidAnswer);
        var result = await handler.HandleAsync(eventId, memberId, request, CancellationToken.None);

        result.IsSuccess.Should().BeFalse(
            "交易前的檢查讀到 IsQueueModeEnabled = true，若沒有交易內重新鎖定確認，會誤判成功並建立一筆不該存在的排隊紀錄");
        result.Error!.Type.Should().Be(ErrorType.Conflict);

        await using var readDbContext = _fixture.CreateDbContext();
        var entryCount = await readDbContext.PurchaseQueueEntries.AsNoTracking()
            .CountAsync(e => e.EventId == eventId && e.MemberId == memberId);
        entryCount.Should().Be(0, "被拒絕的加入排隊請求不應該在資料庫留下任何紀錄");
    }

    private static async Task<Dictionary<string, double>> ReadSortedSetAsync(IDatabase database, string key)
        => (await database.SortedSetRangeByRankWithScoresAsync(key)).ToDictionary(e => e.Element.ToString(), e => e.Score);

    // PQ-SALES-JOIN-009：跨停售流程面——販售期間內加入、時間越過停售後查詢，仍回傳原本那筆 Waiting，
    // 紀錄與鏡像都不因查詢而改變（停售只擋新加入，不清理已排隊者）。HTTP 端點面見 WebApi 的 EventQueueControllerTests。
    // 兩個 Handler 以 new 組裝並共用同一個可變時間替身；不啟動入場推進服務，鏡像任何變化只可能來自查詢路徑。
    [Fact]
    public async Task JoinThenGetMyQueueStatus_WhenTimeCrossesSalesEnd_ReturnsOriginalWaitingEntryUnchanged()
    {
        var t0 = DateTime.UtcNow;
        var redis = new RedisFixture();
        await redis.InitializeAsync();
        try
        {
            var connection = redis.CreateConnection();
            var database = connection.GetDatabase();
            var realMirror = new RedisPurchaseQueueAdmissionMirror(connection, NullLogger<RedisPurchaseQueueAdmissionMirror>.Instance);
            var fakeDateTimeProvider = new FakeDateTimeProvider { UtcNow = t0 };

            // Given：販售期間 [T0 − 1h, T0 + 1h)、開演 T0 + 2h；M2 為查詢當下未逾時的 Admitted（admitted 鏡像基準）。
            Guid eventId;
            Guid memberId;
            var admittedEntryId = Guid.NewGuid();
            var m2AdmissionExpiresAtUtc = t0.AddHours(3);
            await using (var seedDbContext = _fixture.CreateDbContext())
            {
                var venue = new Venue(Guid.NewGuid(), $"Test Venue {Guid.NewGuid():N}");
                var seatMap = new SeatMap(Guid.NewGuid(), venue.Id);
                var organizerId = await OrganizerTestData.SeedApprovedOrganizerAsync(seedDbContext);
                var @event = new Event(
                    Guid.NewGuid(), "Sales Window Event", t0.AddHours(2), venue.Id, seatMap.Id, organizerId,
                    isRealNameRequired: false, salesStartAtUtc: t0.AddHours(-1), salesEndAtUtc: t0.AddHours(1));
                @event.EnableQueueMode();
                var member = Member.Register($"buyer-{Guid.NewGuid():N}@example.com", "Test Buyer", "hash");
                var m2 = Member.Register($"admitted-{Guid.NewGuid():N}@example.com", "Admitted Buyer", "hash");
                var m2Entry = new PurchaseQueueEntry(admittedEntryId, @event.Id, m2.Id, t0.AddMinutes(-20));
                m2Entry.Admit(t0.AddMinutes(-10), m2AdmissionExpiresAtUtc);

                seedDbContext.Venues.Add(venue);
                seedDbContext.SeatMaps.Add(seatMap);
                seedDbContext.Events.Add(@event);
                seedDbContext.Members.AddRange(member, m2);
                seedDbContext.PurchaseQueueEntries.Add(m2Entry);
                await seedDbContext.SaveChangesAsync();
                eventId = @event.Id;
                memberId = member.Id;
            }

            var waitingKey = PurchaseQueueAdmissionRedisKeys.Waiting(eventId);
            var admittedKey = PurchaseQueueAdmissionRedisKeys.Admitted(eventId);
            var admittedScore = PurchaseQueueAdmissionTimeConversion.ToUnixMilliseconds(m2AdmissionExpiresAtUtc);
            await database.SortedSetAddAsync(admittedKey, admittedEntryId.ToString(), admittedScore);

            // When 1：販售期間內加入（DbContext A）。
            Guid entryId;
            await using (var dbContextA = _fixture.CreateDbContext())
            {
                var joinHandler = new JoinPurchaseQueueHandler(
                    new EventRepository(dbContextA),
                    new PurchaseQueueRepository(dbContextA),
                    new UnitOfWork(dbContextA),
                    fakeDateTimeProvider,
                    new JoinPurchaseQueueRequestValidator(),
                    new FakeCaptchaService(),
                    realMirror,
                    new MemberRealNameRepository(dbContextA));
                var joinResult = await joinHandler.HandleAsync(
                    eventId, memberId, new JoinPurchaseQueueRequest(FakeCaptchaService.ValidToken, FakeCaptchaService.ValidAnswer), CancellationToken.None);
                joinResult.IsSuccess.Should().BeTrue(joinResult.Error?.Message);
                entryId = joinResult.Value;
            }

            DateTime joinedAtUtc;
            await using (var dbContextC = _fixture.CreateDbContext())
            {
                var joinedEntry = await dbContextC.PurchaseQueueEntries.AsNoTracking().SingleAsync(e => e.Id == entryId);
                joinedEntry.Status.Should().Be(PurchaseQueueEntryStatus.Waiting);
                joinedAtUtc = joinedEntry.JoinedAtUtc;
            }

            // 前置條件：真實鏡像對 Redis 例外 fail-open，不能以「未拋例外」判斷同步成功，必須直接讀 Redis。
            (await database.SortedSetScoreAsync(waitingKey, entryId.ToString()))
                .Should().Be(PurchaseQueueAdmissionTimeConversion.ToUnixMilliseconds(joinedAtUtc));
            var waitingBefore = await ReadSortedSetAsync(database, waitingKey);
            var admittedBefore = await ReadSortedSetAsync(database, admittedKey);
            waitingBefore.Keys.Should().Equal(entryId.ToString());
            admittedBefore.Should().Equal(new Dictionary<string, double> { [admittedEntryId.ToString()] = admittedScore });

            // When 2：時間越過停售（仍早於開演），以全新的 DbContext B 查詢。
            fakeDateTimeProvider.UtcNow = t0.AddMinutes(90);
            Result<QueueStatusDto> statusResult;
            await using (var dbContextB = _fixture.CreateDbContext())
            {
                var statusHandler = new GetMyQueueStatusHandler(
                    new EventRepository(dbContextB), new PurchaseQueueRepository(dbContextB), fakeDateTimeProvider);
                statusResult = await statusHandler.HandleAsync(eventId, memberId, CancellationToken.None);
            }

            statusResult.IsSuccess.Should().BeTrue(statusResult.Error?.Message);
            statusResult.Value!.Status.Should().Be("Waiting");
            statusResult.Value.WaitingCount.Should().Be(0, "前方沒有其他 Waiting，M2 為 Admitted 不計入");
            statusResult.Value.QueueModeEnabled.Should().BeTrue();

            await using (var dbContextC = _fixture.CreateDbContext())
            {
                var entries = await dbContextC.PurchaseQueueEntries.AsNoTracking()
                    .Where(e => e.EventId == eventId && e.MemberId == memberId)
                    .ToListAsync();
                var entry = entries.Should().ContainSingle().Subject;
                entry.Id.Should().Be(entryId);
                entry.JoinedAtUtc.Should().Be(joinedAtUtc);
                entry.Status.Should().Be(PurchaseQueueEntryStatus.Waiting);
                entry.AdmittedAtUtc.Should().BeNull();
                entry.AdmissionExpiresAtUtc.Should().BeNull();
            }

            (await ReadSortedSetAsync(database, waitingKey)).Should().Equal(waitingBefore);
            (await ReadSortedSetAsync(database, admittedKey)).Should().Equal(admittedBefore);
        }
        finally
        {
            await redis.DisposeAsync();
        }
    }
}
