using System.Collections.Concurrent;
using System.Globalization;
using Microsoft.Extensions.Hosting;
using ProjectC.Application.Common;
using ProjectC.Application.Common.Interfaces;
using ProjectC.Domain.Events;
using ProjectC.Domain.PurchaseQueue;
using ProjectC.Infrastructure.PurchaseQueue;
using Serilog.Context;
using StackExchange.Redis;

namespace ProjectC.WebApi.BackgroundServices;

/// <summary>
/// 週期性推進每個開啟熱門搶購模式活動的排隊入場名額，並將已逾時的 Admitted 紀錄標記為 Expired。
/// 併發控制核心改為 Redis Lua Script 的原子操作，Postgres 仍是持久化真相來源
/// （見 purchase-queue-redis-admission design.md 決策 4）。
/// </summary>
public sealed class PurchaseQueueAdmissionService : BackgroundService
{
    // 涵蓋整輪推進的單一固定 key，不分活動（design.md 決策 1）。
    private const string LeaderElectionLockKey = "purchase-queue-admission:lock";

    // Decision 4：入場推進 Lua Script。KEYS[1]=waiting zset，KEYS[2]=admitted zset；
    // ARGV[1]=now(ms)，ARGV[2]=maxConcurrentAdmittedBuyers，ARGV[3]=新入場逾時時間(ms)，
    // ARGV[4]=pending 標記 TTL(秒)，ARGV[5]=pending key 前綴。回傳 { expiredIds, promotedIds,
    // dedupedIds }，三者皆為 entryId 字串陣列。所有輸入一律透過 KEYS/ARGV 傳遞，不字串拼接組出
    // Script 文字。
    //
    // dedupedIds（strict-reviewer 事後審查修正）：校正步驟 3 的 gap-fill 是「依快照做決策、之後才
    // 執行寫入」，決策與寫入之間若插入一次完整的並發推進，可能讓同一個 entryId 短暫真的同時存在於
    // waiting 與 admitted（design.md Decision 5 已承認、接受此暫態，交由下一輪校正步驟 6 收斂）。
    // ZPOPMIN 本身若對此毫無防禦，會把這個已入場的 member 當成新候選，重設其 admitted score（等同
    // 非預期延長 AdmissionExpiresAtUtc，違反 TTL 固定窗口語意）。因此 popped member 寫入 admitted
    // 前 MUST 先用 ZSCORE 檢查是否已在 admitted：已存在者只當成清除其 waiting 端殘留（不重設
    // score、不計入 promoted、不佔用本輪名額），並繼續從 waiting 補取下一位，直到填滿可用名額或
    // waiting 耗盡。
    private const string AdvanceScript = """
        local expired = redis.call('ZRANGEBYSCORE', KEYS[2], '-inf', ARGV[1])
        if #expired > 0 then
            redis.call('ZREM', KEYS[2], unpack(expired))
        end

        local currentAdmitted = redis.call('ZCARD', KEYS[2])
        local maxAdmitted = tonumber(ARGV[2])
        local available = maxAdmitted - currentAdmitted

        local promoted = {}
        local deduped = {}
        if available > 0 then
            local remaining = available
            while remaining > 0 do
                local popped = redis.call('ZPOPMIN', KEYS[1], remaining)
                if #popped == 0 then
                    break
                end
                local i = 1
                while i <= #popped do
                    local member = popped[i]
                    if redis.call('ZSCORE', KEYS[2], member) then
                        table.insert(deduped, member)
                    else
                        table.insert(promoted, member)
                        redis.call('ZADD', KEYS[2], ARGV[3], member)
                        redis.call('SET', ARGV[5] .. member, '1', 'EX', ARGV[4])
                        remaining = remaining - 1
                    end
                    i = i + 2
                end
            end
        end

        return { expired, promoted, deduped }
        """;

    // Decision 5 步驟 2：單一 Lua Script 原子讀取 waiting／admitted 兩個 zset 的全部成員
    // （admitted 含 score，供步驟 7 判斷放棄決策時直接取用逾時時間，不需額外往返）。
    private const string SnapshotReadScript = """
        local waiting = redis.call('ZRANGE', KEYS[1], 0, -1)
        local admitted = redis.call('ZRANGE', KEYS[2], 0, -1, 'WITHSCORES')
        return { waiting, admitted }
        """;

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IDateTimeProvider _dateTimeProvider;
    private readonly PurchaseQueueOptions _options;
    private readonly IDistributedLock _distributedLock;
    private readonly DistributedLockOptions _lockOptions;
    private readonly IConnectionMultiplexer _connectionMultiplexer;
    private readonly ILogger<PurchaseQueueAdmissionService> _logger;

    // Decision 8「Log 等級升級門檻」：純觀察性、程序內記憶體狀態，不寫入 Postgres／Redis，
    // 重啟或鎖易主至其他實例時自然歸零（design.md Decision 8／tasks.md 4.8）。
    private readonly ConcurrentDictionary<Guid, int> _consecutiveFailureCounts = new();

    public PurchaseQueueAdmissionService(
        IServiceScopeFactory scopeFactory,
        IDateTimeProvider dateTimeProvider,
        PurchaseQueueOptions options,
        IDistributedLock distributedLock,
        DistributedLockOptions lockOptions,
        IConnectionMultiplexer connectionMultiplexer,
        ILogger<PurchaseQueueAdmissionService> logger)
    {
        _scopeFactory = scopeFactory;
        _dateTimeProvider = dateTimeProvider;
        _options = options;
        _distributedLock = distributedLock;
        _lockOptions = lockOptions;
        _connectionMultiplexer = connectionMultiplexer;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Decision 5「觸發時機 1」：應用程式啟動時執行一次全量校正。ExecuteAsync 本身以背景 Task
        // 執行、不被 Generic Host 的 StartAsync 等待完成，天然滿足「不阻塞啟動流程」
        // （design.md Decision 5／tasks.md 4.6），不需要額外的非阻塞包裝。
        using (LogContext.PushProperty("TraceId", Guid.NewGuid().ToString()))
        {
            try
            {
                await ReconcileAllEventsOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                _logger.LogWarning(exception, "Purchase queue admission startup reconciliation failed; will retry via the regular polling cycle.");
            }
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            // TraceId scope 必須包住整個 try/catch（含週期層級失敗的 LogError），理由與
            // ExpiredOrderCleanupService.ExecuteAsync 相同（實測發現，見該檔案註解）。
            using (LogContext.PushProperty("TraceId", Guid.NewGuid().ToString()))
            {
                try
                {
                    await AdvanceQueueOnceWithLeaderElectionAsync(stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception exception)
                {
                    _logger.LogError(exception, "Purchase queue admission cycle failed; will retry next interval.");
                }
            }

            await Task.Delay(TimeSpan.FromSeconds(Math.Max(1, _options.PollingIntervalSeconds)), stoppingToken);
        }
    }

    /// <summary>供整合測試直接呼叫（不透過 DI 容器解析這個服務本身），公開一輪完整推進的邏輯，
    /// 含這一輪專屬的 TraceId scope（與 <see cref="ExecuteAsync"/> 走的正式排程路徑各自獨立產生一個
    /// 新值，語意一致：兩者都代表「一輪」，只是觸發來源不同）。刻意不含取鎖邏輯，供既有
    /// purchase-queue 測試沿用，避免意外依賴 Redis 連線以外的分散式鎖狀態。</summary>
    public async Task AdvanceQueueOnceAsync(CancellationToken cancellationToken)
    {
        using (LogContext.PushProperty("TraceId", Guid.NewGuid().ToString()))
        {
            await AdvanceQueueOnceCoreAsync(cancellationToken);
        }
    }

    /// <summary>正式輪詢路徑（<see cref="ExecuteAsync"/>）與本次所有涉及分散式鎖的整合測試的
    /// 正確進入點：先嘗試取得本輪的 leader election 鎖，取得成功時才執行既有的
    /// <see cref="AdvanceQueueOnceAsync"/>；鎖已被其他實例持有、或 Redis 不可用時直接跳過本輪。
    /// Redis 不可用時的行為由既有 <c>purchase-queue-leader-election</c> 的 fail-open 改為
    /// fail-closed——Redis Lua Script 是入場推進唯一的互斥/決策機制，不可用時繼續執行會失去
    /// 「不超額」的保證（design.md Decision 6，偏離既有 PQLE-007／008 的 fail-open 慣例）。</summary>
    public async Task AdvanceQueueOnceWithLeaderElectionAsync(CancellationToken cancellationToken)
    {
        var ttl = TimeSpan.FromSeconds(Math.Max(1, _options.PollingIntervalSeconds) * _lockOptions.LockTtlMultiplier);
        var lockResult = await _distributedLock.TryAcquireAsync(LeaderElectionLockKey, ttl, cancellationToken);

        if (lockResult.LockResult == LockResult.HeldByOther)
        {
            _logger.LogDebug("Skipping this purchase queue admission cycle; lock {LockKey} is held by another instance.", LeaderElectionLockKey);
            return;
        }

        if (lockResult.LockResult == LockResult.RedisUnavailable)
        {
            _logger.LogWarning(
                "Skipping this purchase queue admission cycle (advance and reconciliation) because Redis is unavailable; " +
                "admission correctness now depends solely on Redis (design.md Decision 6, fail-closed).");
            return;
        }

        try
        {
            await AdvanceQueueOnceAsync(cancellationToken);
        }
        finally
        {
            await _distributedLock.ReleaseAsync(LeaderElectionLockKey, lockResult.OwnerToken!, cancellationToken);
        }
    }

    /// <summary>Decision 5 步驟 2 的原子快照讀取，獨立、可單獨呼叫，只依賴傳入的 <see cref="IDatabase"/>，
    /// 不摻雜差集比對或寫入邏輯（design.md Decision 5「可測試性要求」）。公開方法，比照既有
    /// <c>CleanupOnceAsync</c> 慣例，不引入本專案未使用過的 <c>InternalsVisibleTo</c>。</summary>
    public async Task<PurchaseQueueRedisMirrorSnapshot> ReadRedisMirrorSnapshotAsync(Guid eventId, IDatabase database, CancellationToken cancellationToken)
    {
        var result = await database.ScriptEvaluateAsync(
            SnapshotReadScript,
            [PurchaseQueueAdmissionRedisKeys.Waiting(eventId), PurchaseQueueAdmissionRedisKeys.Admitted(eventId)]);

        var waitingMembers = (string[]?)result[0] ?? [];
        var admittedFlat = (string[]?)result[1] ?? [];

        var admittedScores = new Dictionary<string, double>(admittedFlat.Length / 2);
        for (var i = 0; i + 1 < admittedFlat.Length; i += 2)
        {
            admittedScores[admittedFlat[i]] = double.Parse(admittedFlat[i + 1], CultureInfo.InvariantCulture);
        }

        return new PurchaseQueueRedisMirrorSnapshot(waitingMembers, admittedScores);
    }

    private async Task AdvanceQueueOnceCoreAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<Guid> queueModeEventIds;
        using (var scanScope = _scopeFactory.CreateScope())
        {
            var eventRepository = scanScope.ServiceProvider.GetRequiredService<IEventRepository>();
            var events = await eventRepository.GetAllAsync(cancellationToken);
            queueModeEventIds = events.Where(e => e.IsQueueModeEnabled).Select(e => e.Id).ToList();
        }

        foreach (var eventId in queueModeEventIds)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                await AdvanceEventQueueAsync(eventId, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                // 呼叫端主動取消（design.md Decision 6「不可攔截的整程序中止」的可控制模擬方式）：
                // MUST 讓例外往外傳遞、中止本輪剩餘活動的處理，不視為單一活動的業務例外
                // （tasks.md 3.4／PQLE-REBUILD-008）。
                throw;
            }
            catch (Exception exception)
            {
                // 單一活動的業務例外（連線問題、逾時等）MUST 只記錄 Warning 並跳過該活動，
                // MUST NOT 中止主迴圈、MUST NOT 影響同一輪其餘活動的處理結果（design.md Decision 6）。
                _logger.LogWarning(exception, "Unexpected error while advancing purchase queue for event {EventId}; will retry next round.", eventId);
            }
        }
    }

    private async Task AdvanceEventQueueAsync(Guid eventId, CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var eventRepository = scope.ServiceProvider.GetRequiredService<IEventRepository>();
        var purchaseQueueRepository = scope.ServiceProvider.GetRequiredService<IPurchaseQueueRepository>();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

        // Queue Mode 切換的線性化時點，沿用既有 GetForUpdateAsync 呼叫本身，但鎖定範圍刻意縮小為
        // 只包住這次確認、立即 Commit 釋放，不像舊版程式碼把整個交易一路開到方法結尾（strict-reviewer
        // 審查後確認的刻意設計，非疏漏，原因有二）：
        // 1. 正確性面（比效能考量更根本）：Decision 4 明確要求 expiredIds／promotedIds 兩個批次
        //    UPDATE 各自獨立、互不耦合失敗（各自 try/catch，一個失敗不影響另一個）。若把兩者包在
        //    同一個 Postgres 交易內，任一批次遇到真正的資料庫層級錯誤（非本機 C# 例外，而是連線中斷
        //    等會讓 Postgres 交易進入 aborted 狀態的錯誤）會讓交易本身無法再執行任何後續指令，
        //    連帶讓「獨立處理」的另一批次也被迫失敗——這與 Decision 4 的設計意圖相違背，比 Queue
        //    Mode 切換窄化窗口更根本，故本次改動的交易結構本來就不可能維持舊版「整段包在同一交易」
        //    的寫法。
        // 2. 窄化窗口的影響範圍：即使 Admin 在本次確認之後、Redis／Postgres 寫回完成之前才切換
        //    Queue Mode，OrderService.PlaceOrderAsync 只在 IsQueueModeEnabled = true 時才檢查排隊
        //    資格（OrderService.cs），活動關閉後這批多餘的 Admitted 紀錄不會被用來繞過任何資格檢查、
        //    不會造成超賣，只是良性的、下一輪即可自然收斂的暫時性多餘紀錄——不變更任何安全或資料
        //    一致性不變量。
        await using var transaction = await unitOfWork.BeginTransactionAsync(cancellationToken);
        var lockedEvent = await eventRepository.GetForUpdateAsync(eventId, cancellationToken);
        if (lockedEvent is null || !lockedEvent.IsQueueModeEnabled)
        {
            return;
        }

        await transaction.CommitAsync(cancellationToken);

        var database = _connectionMultiplexer.GetDatabase();

        // Decision 5「觸發時機 2」：每輪推進前先執行校正（design.md／tasks.md 4.7）。校正本身可能
        // 耗時，其比較基準時間與下面入場推進 Lua Script 使用的「now」刻意分開取得（strict-reviewer
        // 事後審查修正）：若沿用校正前的舊 now 計算 admissionExpiresAtUtc，校正耗時會讓使用者實際
        // 拿到的入場視窗短於 AdmissionTtlSeconds。
        var reconciliationNow = _dateTimeProvider.UtcNow;
        await ReconcileEventQueueAsync(eventId, purchaseQueueRepository, database, reconciliationNow, cancellationToken);

        // Decision 4：入場推進 Lua Script。EVAL 呼叫本身失敗／逾時（結果未知）MUST NOT 在同一輪內
        // 重試，直接往外拋，交由呼叫端（AdvanceQueueOnceCoreAsync 的逐活動 try/catch）記錄 Warning
        // 並跳過本活動這一輪（design.md Decision 4／6）。
        var now = _dateTimeProvider.UtcNow;
        var admissionExpiresAtUtc = now.AddSeconds(_options.AdmissionTtlSeconds);
        var scriptResult = await database.ScriptEvaluateAsync(
            AdvanceScript,
            [PurchaseQueueAdmissionRedisKeys.Waiting(eventId), PurchaseQueueAdmissionRedisKeys.Admitted(eventId)],
            [
                PurchaseQueueAdmissionTimeConversion.ToUnixMilliseconds(now),
                _options.MaxConcurrentAdmittedBuyers,
                PurchaseQueueAdmissionTimeConversion.ToUnixMilliseconds(admissionExpiresAtUtc),
                _options.AdmissionPendingTtlSeconds,
                PurchaseQueueAdmissionRedisKeys.PendingKeyPrefix,
            ]);

        var expiredIds = ((string[]?)scriptResult[0] ?? []).Select(Guid.Parse).ToList();
        var promotedIds = ((string[]?)scriptResult[1] ?? []).Select(Guid.Parse).ToList();
        var dedupedIds = ((string[]?)scriptResult[2] ?? []).Select(Guid.Parse).ToList();

        if (dedupedIds.Count > 0)
        {
            _logger.LogWarning(
                "Purchase queue admission script found {Count} entries for event {EventId} already present in the admitted mirror while still in the waiting mirror (dual-membership race); removed from waiting without re-promoting or resetting admission expiry: {EntryIds}",
                dedupedIds.Count, eventId, dedupedIds);
        }

        if (expiredIds.Count > 0)
        {
            try
            {
                var affected = await purchaseQueueRepository.ExpireBatchAsync(expiredIds, cancellationToken);
                _logger.LogDebug(
                    "Purchase queue admission expired {Attempted} entries for event {EventId}; {Affected} rows actually updated.",
                    expiredIds.Count, eventId, affected);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // 結果未知也 MUST NOT 反向補償 Redis；收斂交給下一輪校正步驟 8（design.md Decision 4 修正）。
                _logger.LogWarning(exception,
                    "Failed to persist {Count} expired purchase queue entries for event {EventId}; will be reconciled next round.",
                    expiredIds.Count, eventId);
            }
        }

        if (promotedIds.Count > 0)
        {
            try
            {
                var affected = await purchaseQueueRepository.AdmitBatchAsync(promotedIds, now, admissionExpiresAtUtc, cancellationToken);
                _logger.LogDebug(
                    "Purchase queue admission admitted {Attempted} entries for event {EventId}; {Affected} rows actually updated.",
                    promotedIds.Count, eventId, affected);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                _logger.LogWarning(exception,
                    "Failed to persist {Count} promoted purchase queue entries for event {EventId}; will be reconciled next round via the pending marker.",
                    promotedIds.Count, eventId);
            }
        }
    }

    /// <summary>應用程式啟動時的全量校正（Decision 5「觸發時機 1」）：不透過分散式鎖競爭，
    /// 每個實例各自對全部活動執行一次，校正操作本身冪等，多實例同時執行不會造成資料錯誤
    /// （design.md Decision 5「重複執行的冪等性」）。</summary>
    private async Task ReconcileAllEventsOnceAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<Guid> queueModeEventIds;
        using (var scanScope = _scopeFactory.CreateScope())
        {
            var eventRepository = scanScope.ServiceProvider.GetRequiredService<IEventRepository>();
            var events = await eventRepository.GetAllAsync(cancellationToken);
            queueModeEventIds = events.Where(e => e.IsQueueModeEnabled).Select(e => e.Id).ToList();
        }

        var database = _connectionMultiplexer.GetDatabase();
        var now = _dateTimeProvider.UtcNow;

        foreach (var eventId in queueModeEventIds)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                using var scope = _scopeFactory.CreateScope();
                var purchaseQueueRepository = scope.ServiceProvider.GetRequiredService<IPurchaseQueueRepository>();
                await ReconcileEventQueueAsync(eventId, purchaseQueueRepository, database, now, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                _logger.LogWarning(exception,
                    "Startup reconciliation failed for purchase queue event {EventId}; will be retried by the regular polling cycle.", eventId);
            }
        }
    }

    /// <summary>Decision 5 的 Redis 鏡像逐筆定向校正：補齊缺漏、清除不屬於目前合法狀態集合的殘留、
    /// 偵測並修復放棄的推進決策／逾時標記。全部步驟皆為冪等操作，重複執行對已符合目標狀態的資料
    /// 沒有副作用（design.md Decision 5）。</summary>
    private async Task ReconcileEventQueueAsync(
        Guid eventId,
        IPurchaseQueueRepository purchaseQueueRepository,
        IDatabase database,
        DateTime now,
        CancellationToken cancellationToken)
    {
        var activeEntries = await purchaseQueueRepository.GetActiveForReconciliationAsync(eventId, cancellationToken);

        // 步驟 1：同一次查詢、同一個比較基準時間切分 W_pg／A_pg／A_pg_overdue，三者互斥
        // （design.md Decision 5 步驟 1）。
        var waitingPg = activeEntries.Where(e => e.Status == PurchaseQueueEntryStatus.Waiting).ToList();
        var admittedNotOverduePg = activeEntries
            .Where(e => e.Status == PurchaseQueueEntryStatus.Admitted && e.AdmissionExpiresAtUtc > now).ToList();
        var admittedOverduePg = activeEntries
            .Where(e => e.Status == PurchaseQueueEntryStatus.Admitted && e.AdmissionExpiresAtUtc <= now).ToList();

        PurchaseQueueRedisMirrorSnapshot snapshot;
        try
        {
            snapshot = await ReadRedisMirrorSnapshotAsync(eventId, database, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogWarning(exception, "Failed to read Redis admission mirror snapshot for event {EventId}; skipping reconciliation this round.", eventId);
            return;
        }

        var waitingRedis = snapshot.WaitingMemberIds.ToHashSet();
        var admittedRedis = snapshot.AdmittedMemberScores;

        var waitingKey = PurchaseQueueAdmissionRedisKeys.Waiting(eventId);
        var admittedKey = PurchaseQueueAdmissionRedisKeys.Admitted(eventId);

        // 步驟 3：補齊 waiting，排除已在 A_redis 的 entryId（第十輪審查修正，避免同時存在於兩集合，
        // 見 design.md Decision 5 步驟 3／PQLE-REBUILD-005）。
        foreach (var entry in waitingPg)
        {
            var idStr = entry.Id.ToString();
            if (waitingRedis.Contains(idStr) || admittedRedis.ContainsKey(idStr))
            {
                continue;
            }

            await TryReconcileOperationAsync(entry.Id, "ZADD waiting (gap-fill)",
                () => database.SortedSetAddAsync(waitingKey, idStr, PurchaseQueueAdmissionTimeConversion.ToUnixMilliseconds(entry.JoinedAtUtc)));
        }

        // 步驟 4：補齊 admitted。
        foreach (var entry in admittedNotOverduePg)
        {
            var idStr = entry.Id.ToString();
            if (admittedRedis.ContainsKey(idStr))
            {
                continue;
            }

            await TryReconcileOperationAsync(entry.Id, "ZADD admitted (gap-fill)",
                () => database.SortedSetAddAsync(admittedKey, idStr, PurchaseQueueAdmissionTimeConversion.ToUnixMilliseconds(entry.AdmissionExpiresAtUtc!.Value)));
        }

        // 步驟 5 之前先算出「Postgres 仍 Waiting、但已在 admitted 鏡像」的候選過渡態集合——這整個集合
        // （不只是 pending 標記仍存活的子集）MUST 從步驟 5 的「不屬於目前合法狀態集合」判定中排除。
        // 本輪審查再修正：先前版本只排除「pending 存活」的子集，但「pending 已過期、待步驟 7 代為
        // 完成落地」的子集若不排除，會被步驟 5（在程式碼順序上先於步驟 7）誤判為 stale/orphan 提前
        // ZREM——這不只是白白刪除，還會讓該筆紀錄從「已確定的推進決策，等待落地」錯誤地退化成「查無
        // 任何 Redis 蹤跡」，下一輪步驟 3 會把它當成全新的 Waiting 候選重新 ZADD 回 waiting，可能被
        // ZPOPMIN 二次選中、重複推進，比原本的 bug 更嚴重。這整個集合（不論 pending 是否仍存活）
        // 一律只交給步驟 7 處理，步驟 5 完全不涉入。
        var waitingButAdmittedIds = waitingPg
            .Select(e => e.Id.ToString())
            .Where(admittedRedis.ContainsKey)
            .ToHashSet();

        // 步驟 5：清除 admitted 中不屬於目前合法狀態集合（Completed／Expired／孤兒，統一以集合差
        // 運算判定，不逐一枚舉終態）的殘留；A_pg_overdue 與上方的候選過渡態集合本身仍合法，
        // MUST NOT 被本步驟清除（design.md Decision 5 步驟 5／Decision 9）。Decision 8「重試與升級
        // 策略」適用於此步驟（見下方 TryTrackedReconcileOperationAsync）——涵蓋 Completed 訂單完成
        // 同步失敗的收斂情境（PQ-COMPLETE-003／PQLE-REBUILD-004），統一套用到 Expired／孤兒兩種情況
        // 不影響正確性、避免額外查詢區分「為何需要清除」的複雜度（CLAUDE.md Simplicity First）。
        var legalAdmittedIds = admittedNotOverduePg.Select(e => e.Id.ToString())
            .Concat(admittedOverduePg.Select(e => e.Id.ToString()))
            .Concat(waitingButAdmittedIds)
            .ToHashSet();
        foreach (var member in admittedRedis.Keys)
        {
            if (legalAdmittedIds.Contains(member))
            {
                continue;
            }

            // 無法解析為 Guid 的殘留（格式損壞的 member）一律視為孤兒直接清除，不得因為解析失敗就
            // 略過——否則會永久佔用 ZCARD 計算出的名額（strict-reviewer 事後審查修正）。
            if (!Guid.TryParse(member, out var entryId))
            {
                await TryRemoveMalformedMemberAsync(admittedKey, member, "admitted", database);
                continue;
            }

            await TryTrackedReconcileOperationAsync(entryId, "ZREM admitted (stale/orphan)",
                () => database.SortedSetRemoveAsync(admittedKey, member));
        }

        // 步驟 6：清除 waiting 中不屬於 W_pg 的殘留。
        var waitingIdSet = waitingPg.Select(e => e.Id.ToString()).ToHashSet();
        foreach (var member in waitingRedis)
        {
            if (waitingIdSet.Contains(member))
            {
                continue;
            }

            if (!Guid.TryParse(member, out var entryId))
            {
                await TryRemoveMalformedMemberAsync(waitingKey, member, "waiting", database);
                continue;
            }

            await TryReconcileOperationAsync(entryId, "ZREM waiting (orphan)",
                () => database.SortedSetRemoveAsync(waitingKey, member));
        }

        // 步驟 7：偵測並修復放棄的推進決策——pending 標記存活期間視為合法過渡態，不動；已過期則
        // 代為完成 Postgres 落地（design.md Decision 5 步驟 7／Decision 9）。步驟 5 已把這整個
        // waitingButAdmittedIds 集合排除在清除範圍外，這裡才是唯一處理它們的地方。
        foreach (var entry in waitingPg)
        {
            var idStr = entry.Id.ToString();
            if (!admittedRedis.TryGetValue(idStr, out var admittedScore))
            {
                continue;
            }

            if (await database.KeyExistsAsync(PurchaseQueueAdmissionRedisKeys.Pending(entry.Id)))
            {
                continue;
            }

            var expiresAtUtc = PurchaseQueueAdmissionTimeConversion.FromUnixMilliseconds(admittedScore);
            await TryTrackedReconcileOperationAsync(entry.Id, "conditional UPDATE admit (abandoned decision)",
                () => purchaseQueueRepository.AdmitIfWaitingAsync(entry.Id, now, expiresAtUtc, cancellationToken));
        }

        // 步驟 8：偵測並修復放棄的逾時標記——不需要 TTL 寬限窗口判斷，「已逾時」是純粹的時間事實
        // （design.md Decision 5 步驟 8）。
        foreach (var entry in admittedOverduePg)
        {
            var idStr = entry.Id.ToString();
            if (admittedRedis.ContainsKey(idStr))
            {
                continue;
            }

            await TryTrackedReconcileOperationAsync(entry.Id, "conditional UPDATE expire (abandoned timeout)",
                () => purchaseQueueRepository.ExpireBatchAsync([entry.Id], cancellationToken));
        }
    }

    // 步驟 3／4／6 的差集補齊/清除：單純的自我修復 gap-fill，不屬於 Decision 8 定義的三種計數來源，
    // 失敗一律只記錄 Warning，下一輪校正自然重試（design.md Decision 5「重複執行的冪等性」）。
    private async Task TryReconcileOperationAsync(Guid entryId, string operation, Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogWarning(exception,
                "Purchase queue reconciliation operation {Operation} for entry {EntryId} failed; will retry next reconciliation round.",
                operation, entryId);
        }
    }

    // 步驟 5／6 的格式損壞 member 清除：沒有可解析的 entryId，無法套用以 entryId 為 key 的 Decision 8
    // 連續失敗計數，統一走 Warning-only、下一輪重試的簡易路徑（design.md Decision 5「重複執行的
    // 冪等性」）。
    private async Task TryRemoveMalformedMemberAsync(string key, string member, string setName, IDatabase database)
    {
        try
        {
            await database.SortedSetRemoveAsync(key, member);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogWarning(exception,
                "Purchase queue reconciliation failed to remove malformed {SetName} member '{Member}'; will retry next reconciliation round.",
                setName, member);
        }
    }

    // 步驟 5／7／8：Decision 8「重試與升級策略」定義的三種同屬校正層條件式寫回的失敗來源，
    // 共用同一個以 entryId 為 key 的連續失敗計數與同一套升級門檻邏輯（design.md Decision 8／
    // tasks.md 4.8）。
    private async Task TryTrackedReconcileOperationAsync(Guid entryId, string operation, Func<Task> action)
    {
        try
        {
            await action();
            _consecutiveFailureCounts.TryRemove(entryId, out _);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            var count = _consecutiveFailureCounts.AddOrUpdate(entryId, 1, (_, existing) => existing + 1);
            if (count >= _options.ReconciliationFailureLogUpgradeThreshold)
            {
                _logger.LogError(exception,
                    "Purchase queue reconciliation operation {Operation} for entry {EntryId} has failed {ConsecutiveFailureCount} consecutive times.",
                    operation, entryId, count);
            }
            else
            {
                _logger.LogWarning(exception,
                    "Purchase queue reconciliation operation {Operation} for entry {EntryId} failed ({ConsecutiveFailureCount} consecutive failures so far).",
                    operation, entryId, count);
            }
        }
    }
}

/// <summary>Decision 5 步驟 2 原子快照讀取的結果：waiting zset 全部成員（entryId 字串)、
/// admitted zset 全部成員與各自的 score（AdmissionExpiresAtUtc 的 Unix 毫秒）。</summary>
public sealed record PurchaseQueueRedisMirrorSnapshot(
    IReadOnlyList<string> WaitingMemberIds,
    IReadOnlyDictionary<string, double> AdmittedMemberScores);
