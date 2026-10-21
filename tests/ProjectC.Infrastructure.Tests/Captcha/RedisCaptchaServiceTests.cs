using System.Diagnostics;
using FluentAssertions;
using Moq;
using ProjectC.Application.Common;
using ProjectC.Infrastructure.Captcha;
using ProjectC.Infrastructure.Tests.TestSupport;
using StackExchange.Redis;

namespace ProjectC.Infrastructure.Tests.Captcha;

// captcha-verification design.md 決策 2／3／4／10；比照既有 RedisQueryCacheTests 手法，
// 真實 Redis（RedisFixture）驗證正常行為；mock 用於 RedisCaptchaServiceFailClosedTests 模擬 Redis
// 拋例外的情境，以及本檔案 GenerateAsync_WhenCancelledAfterImageGenerationButBeforeRedisWrite
// 需要直接斷言「StringSetAsync 從未被呼叫」而非透過真實 Redis 的間接觀察（design.md 決策 10 補充）。
[Collection(RedisCollection.Name)]
public class RedisCaptchaServiceTests
{
    private readonly RedisFixture _fixture;

    public RedisCaptchaServiceTests(RedisFixture fixture)
    {
        _fixture = fixture;
    }

    // 測試專用假圖片產生器：內容固定可控，並可在「圖片已產生」的時間點觸發回呼
    // （5.2a：控制取消發生的精確時機，見 design.md 決策 10「可測試性」段落）。
    private sealed class StubCaptchaImageGenerator : ICaptchaImageGenerator
    {
        private readonly string _content;
        private readonly byte[] _imageBytes;
        private readonly Action? _onGenerate;

        public StubCaptchaImageGenerator(string content = "TEST", byte[]? imageBytes = null, Action? onGenerate = null)
        {
            _content = content;
            _imageBytes = imageBytes ?? new byte[] { 1, 2, 3 };
            _onGenerate = onGenerate;
        }

        public CaptchaImage Generate()
        {
            var bytes = _imageBytes;
            _onGenerate?.Invoke();
            return new CaptchaImage(_content, bytes);
        }
    }

    private RedisCaptchaService CreateService(
        ICaptchaImageGenerator? imageGenerator = null,
        CaptchaOptions? options = null,
        IConnectionMultiplexer? connection = null)
        => new(
            connection ?? _fixture.CreateConnection(),
            imageGenerator ?? new StubCaptchaImageGenerator(),
            options ?? new CaptchaOptions());

    private static string RedisKeyFor(string token) => $"captcha:{token}";

    // WSL2 VM 牆上時鐘實測約每 20–30 秒回跳約 1–3 秒，Redis 以牆上時鐘計算到期，key 實際存活時間可能比 TTL 長這麼多
    // （同一問題見 QueryCacheTtlSafetyNetTests）。
    private static readonly TimeSpan ClockStepBackTolerance = TimeSpan.FromSeconds(6);

    // PEXPIRETIME（Redis 7）回傳到期的 Unix 毫秒；key 不存在回 -2、沒有 TTL 回 -1。
    private const long RedisKeyMissing = -2;
    private const long RedisNoExpiry = -1;
    private static readonly TimeSpan RedisMillisecondTruncation = TimeSpan.FromMilliseconds(1);

    // 牆上時鐘前進速度相對單調時鐘的上限倍率（WSL2 實測偏快 2–3%，取 5%）。牆上時鐘另會往前跳（實測約 60ms），往前跳會反映在
    // 窗口內牆上經過時間，故容許的前進量取「單調經過時間 × 倍率」與「牆上經過時間」的較大者。
    private const double MaxWallClockRate = 1.05;

    private readonly record struct RedisClockReading(long UnixMilliseconds, long StopwatchTimestamp);

    // Stopwatch 先於 TIME 取得，讓之後算出的單調經過時間涵蓋整段牆上經過時間。
    private static async Task<RedisClockReading> ReadClockBeforeWriteAsync(IDatabase database)
    {
        var stopwatchTimestamp = Stopwatch.GetTimestamp();
        return new RedisClockReading(await ReadRedisUnixMillisecondsAsync(database), stopwatchTimestamp);
    }

    private static async Task<long> ReadRedisUnixMillisecondsAsync(IDatabase database)
    {
        var reply = (RedisResult[])(await database.ExecuteAsync("TIME"))!;
        return (long)reply[0] * 1000 + (long)reply[1] / 1000;
    }

    /// <summary>
    /// 輪詢直到 Redis 自身已淘汰該 key，且必須在 TTL + 時鐘回跳餘裕內完成。不用固定 Task.Delay：時鐘回跳時
    /// 固定等待可能不足以跨過 TTL 而誤判（flaky）。
    /// </summary>
    private static async Task WaitUntilKeyExpiredAsync(IDatabase database, string key, TimeSpan ttl)
    {
        // Stopwatch 為單調時鐘，不受牆上時鐘回跳影響。
        var stopwatch = Stopwatch.StartNew();
        // 每次觀察（含觀察到已淘汰的那一次）都檢查期限；只在 key 仍存在時檢查，會放過「超過期限後才觀察到淘汰」。
        while (true)
        {
            var isKeyPresent = await database.KeyExistsAsync(key);
            stopwatch.Elapsed.Should().BeLessThan(ttl + ClockStepBackTolerance, "驗證碼必須在 TTL（加上時鐘回跳餘裕）內由 Redis 淘汰");
            if (!isKeyPresent)
            {
                return;
            }
            await Task.Delay(100);
        }
    }

    // CAPTCHA-STORE-001：驗證碼寫入時帶有 120 秒 TTL，且儲存的值為雜湊，不是明碼。
    [Fact]
    public async Task GenerateAsync_WritesHashedAnswerWithTtl()
    {
        var connection = _fixture.CreateConnection();
        var service = CreateService(new StubCaptchaImageGenerator("TEST"), new CaptchaOptions { TtlSeconds = 30 }, connection);

        var challenge = await service.GenerateAsync(CancellationToken.None);

        var stored = await connection.GetDatabase().StringGetAsync(RedisKeyFor(challenge.Token));
        stored.HasValue.Should().BeTrue();
        stored.ToString().Should().NotBe("TEST", "MUST NOT 存明碼答案");

        var ttl = await connection.GetDatabase().KeyTimeToLiveAsync(RedisKeyFor(challenge.Token));
        ttl.Should().NotBeNull();
        ttl!.Value.Should().BeGreaterThan(TimeSpan.Zero).And.BeLessThanOrEqualTo(TimeSpan.FromSeconds(30));
    }

    // CAPTCHA-STORE-002：TTL 到期後，對應的 Redis key 已不存在，後續以該 token 驗證視為失敗。
    [Fact]
    public async Task VerifyAsync_AfterTtlExpires_ReturnsFalse()
    {
        using var connection = _fixture.CreateConnection();
        var service = CreateService(new StubCaptchaImageGenerator("TEST"), new CaptchaOptions { TtlSeconds = 1 }, connection);
        var database = connection.GetDatabase();
        var beforeWrite = await ReadClockBeforeWriteAsync(database);
        var challenge = await service.GenerateAsync(CancellationToken.None);
        var key = RedisKeyFor(challenge.Token);
        var ttl = TimeSpan.FromSeconds(1);

        // Redis 以寫入當下的牆上時鐘加 TTL 記錄到期時刻，因此「到期時刻 − 要求的 TTL」即寫入時刻，TTL 設錯時會偏離真正的寫入時刻。
        // 不能只要求它落在寫入前後兩次 Redis TIME 之間：回跳 δ 在寫入前或寫入後各會讓其中一端失準，各自放寬 δ 會放過偏差 δ 以內的錯誤 TTL。
        // 改為要求落在兩個窄區間之一——回跳不在寫入前時，寫入時刻在寫入前 TIME 之後、前進量以內；回跳不在寫入後時，在讀取後 TIME 之前、
        // 前進量以內。窗口內至多一次回跳時真正的寫入時刻必在其一，TTL 設錯則須偏差恰好約等於 δ 才會通過。
        // 補上下方輪詢放寬後抓不到的「TTL 被設得比要求長」錯誤，也抓「驗證碼提早失效」錯誤。
        var expireAt = (long)await database.ExecuteAsync("PEXPIRETIME", key);
        expireAt.Should().NotBe(RedisKeyMissing, "產生驗證碼後到讀取 TTL 之間 key 不應已過期");
        expireAt.Should().NotBe(RedisNoExpiry, "驗證碼必須設定 TTL，否則永不失效");
        var afterRead = await ReadRedisUnixMillisecondsAsync(database);
        var monotonicElapsed = Stopwatch.GetElapsedTime(beforeWrite.StopwatchTimestamp);
        var wallElapsed = TimeSpan.FromMilliseconds(afterRead - beforeWrite.UnixMilliseconds);
        var maxWallAdvance = (wallElapsed > monotonicElapsed * MaxWallClockRate ? wallElapsed : monotonicElapsed * MaxWallClockRate) + RedisMillisecondTruncation;
        var inferredWriteAt = expireAt - (long)ttl.TotalMilliseconds;
        var maxWallAdvanceMilliseconds = (long)Math.Ceiling(maxWallAdvance.TotalMilliseconds);
        var truncationMilliseconds = (long)RedisMillisecondTruncation.TotalMilliseconds;
        var isNearBeforeWrite = inferredWriteAt >= beforeWrite.UnixMilliseconds - truncationMilliseconds
            && inferredWriteAt <= beforeWrite.UnixMilliseconds + maxWallAdvanceMilliseconds;
        var isNearAfterRead = inferredWriteAt >= afterRead - maxWallAdvanceMilliseconds
            && inferredWriteAt <= afterRead + truncationMilliseconds;
        (isNearBeforeWrite || isNearAfterRead).Should().BeTrue(
            "驗證碼的 TTL 必須等於 TtlSeconds（太短會提早失效，太長則過期驗證碼仍可使用）：到期時刻 − TTL = {0}，應在寫入前 TIME {1} 之後或讀取後 TIME {2} 之前 {3}ms 內",
            inferredWriteAt, beforeWrite.UnixMilliseconds, afterRead, maxWallAdvanceMilliseconds);

        await WaitUntilKeyExpiredAsync(database, key, ttl);

        var result = await service.VerifyAsync(challenge.Token, "TEST", CancellationToken.None);

        result.Should().BeFalse();
    }

    // CAPTCHA-VERIFY-001：答案正確時驗證成功，且 key 已被原子刪除。
    [Fact]
    public async Task VerifyAsync_WithCorrectAnswer_ReturnsTrueAndDeletesKey()
    {
        var connection = _fixture.CreateConnection();
        var service = CreateService(new StubCaptchaImageGenerator("TEST"), connection: connection);
        var challenge = await service.GenerateAsync(CancellationToken.None);

        var result = await service.VerifyAsync(challenge.Token, "TEST", CancellationToken.None);

        result.Should().BeTrue();
        (await connection.GetDatabase().KeyExistsAsync(RedisKeyFor(challenge.Token))).Should().BeFalse();
    }

    // CAPTCHA-VERIFY-002：答案錯誤時驗證失敗，且 key 已被原子刪除。
    [Fact]
    public async Task VerifyAsync_WithWrongAnswer_ReturnsFalseAndDeletesKey()
    {
        var connection = _fixture.CreateConnection();
        var service = CreateService(new StubCaptchaImageGenerator("TEST"), connection: connection);
        var challenge = await service.GenerateAsync(CancellationToken.None);

        var result = await service.VerifyAsync(challenge.Token, "WRONG", CancellationToken.None);

        result.Should().BeFalse();
        (await connection.GetDatabase().KeyExistsAsync(RedisKeyFor(challenge.Token))).Should().BeFalse();
    }

    // CAPTCHA-VERIFY-003：不存在的 token 驗證失敗，不拋出未預期例外。
    [Fact]
    public async Task VerifyAsync_WithNonExistentToken_ReturnsFalse()
    {
        var service = CreateService();

        var result = await service.VerifyAsync(Guid.NewGuid().ToString(), "TEST", CancellationToken.None);

        result.Should().BeFalse();
    }

    // CAPTCHA-VERIFY-004：同一 token 驗證兩次，第二次視為 token 不存在而失敗。
    [Fact]
    public async Task VerifyAsync_SameTokenTwice_SecondAttemptFails()
    {
        var service = CreateService(new StubCaptchaImageGenerator("TEST"));
        var challenge = await service.GenerateAsync(CancellationToken.None);

        var first = await service.VerifyAsync(challenge.Token, "TEST", CancellationToken.None);
        var second = await service.VerifyAsync(challenge.Token, "TEST", CancellationToken.None);

        first.Should().BeTrue();
        second.Should().BeFalse();
    }

    // CAPTCHA-VERIFY-005：比對時忽略大小寫。
    [Fact]
    public async Task VerifyAsync_WithLowercaseInput_MatchesUppercaseAnswer()
    {
        var service = CreateService(new StubCaptchaImageGenerator("TEST"));
        var challenge = await service.GenerateAsync(CancellationToken.None);

        var result = await service.VerifyAsync(challenge.Token, "test", CancellationToken.None);

        result.Should().BeTrue();
    }

    // CAPTCHA-INPUT-003：輸入前後夾帶空白仍比對成功（VerifyAsync 內部 Trim()）。
    [Fact]
    public async Task VerifyAsync_WithSurroundingWhitespace_StillMatches()
    {
        var service = CreateService(new StubCaptchaImageGenerator("TEST"));
        var challenge = await service.GenerateAsync(CancellationToken.None);

        var result = await service.VerifyAsync(challenge.Token, "  TEST  ", CancellationToken.None);

        result.Should().BeTrue();
    }

    // CAPTCHA-VERIFY-006：兩個並發請求以同一 token、同一正確答案同時驗證，恰好一次成功、
    // 另一次失敗，驗證 GETDEL 的原子性（design.md 決策 4）。
    [Fact]
    public async Task VerifyAsync_ConcurrentCallsWithSameTokenAndCorrectAnswer_ExactlyOneSucceeds()
    {
        var service = CreateService(new StubCaptchaImageGenerator("TEST"));
        var challenge = await service.GenerateAsync(CancellationToken.None);

        var results = await Task.WhenAll(
            service.VerifyAsync(challenge.Token, "TEST", CancellationToken.None),
            service.VerifyAsync(challenge.Token, "TEST", CancellationToken.None));

        results.Count(r => r).Should().Be(1);
        results.Count(r => !r).Should().Be(1);
    }

    // CAPTCHA-CANCEL-001：GenerateAsync 呼叫當下已取消，MUST 在 Redis 寫入前拋出，不回傳任何 token。
    [Fact]
    public async Task GenerateAsync_WhenCancellationAlreadyRequested_ThrowsOperationCanceledException()
    {
        var service = CreateService();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var act = () => service.GenerateAsync(cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    // CAPTCHA-CANCEL-002：VerifyAsync 呼叫當下已取消，MUST 在 Redis 讀取／刪除前拋出。
    [Fact]
    public async Task VerifyAsync_WhenCancellationAlreadyRequested_ThrowsOperationCanceledException()
    {
        var service = CreateService();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var act = () => service.VerifyAsync(Guid.NewGuid().ToString(), "TEST", cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    // CAPTCHA-CANCEL-004（5.2a）：GenerateAsync 在圖片產生完成後、Redis 寫入前才被取消——開頭檢查
    // 已通過，MUST 在 StringSetAsync 呼叫前再次偵測到取消，且該 token 對應的 key 確實不存在，
    // 證明取消發生在 Redis 寫入之前（design.md 決策 10「可測試性」段落）。
    [Fact]
    public async Task GenerateAsync_WhenCancelledAfterImageGenerationButBeforeRedisWrite_ThrowsAndDoesNotWriteToRedis()
    {
        // 比對「呼叫前後 captcha:* key 集合有沒有差異」不足以可靠證明 StringSetAsync 從未被呼叫：
        // server.Keys() 底層走 SCAN，不是一致性快照，也無法排除其他行程或測試同時增刪 key 的可能
        // （即使本專案的 xUnit collection 設定讓同一 collection 內測試不會並行執行，這個推論本身仍是
        // 間接證據，不是對「這次呼叫有沒有呼叫 StringSetAsync」的直接觀察）。改用 mock
        // IConnectionMultiplexer／IDatabase，直接斷言 StringSetAsync 從未被呼叫——這是對本測試唯一
        // 要驗證的宣稱（取消發生在 Redis 寫入之前）最直接、不依賴時序推論的證明方式。正常寫入與 TTL
        // 行為已由 GenerateAsync_WritesHashedAnswerWithTtl 用真實 Redis 驗證，不在此重複。
        using var cts = new CancellationTokenSource();
        var imageGenerator = new StubCaptchaImageGenerator("TEST", onGenerate: () => cts.Cancel());
        var mockDatabase = new Mock<IDatabase>();
        var mockMultiplexer = new Mock<IConnectionMultiplexer>();
        mockMultiplexer.Setup(m => m.GetDatabase(It.IsAny<int>(), It.IsAny<object?>())).Returns(mockDatabase.Object);
        var service = CreateService(imageGenerator, connection: mockMultiplexer.Object);

        var act = () => service.GenerateAsync(cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();

        mockMultiplexer.Verify(m => m.GetDatabase(It.IsAny<int>(), It.IsAny<object?>()), Times.Never,
            "取消確實發生在 GetDatabase／StringSetAsync 呼叫之前，這次呼叫完全不應該嘗試存取 Redis");
        // IDatabaseAsync.StringSetAsync 有多個 overload；(RedisKey, RedisValue, TimeSpan?, bool keepTtl,
        // When, CommandFlags) 這個 overload 的 keepTtl 參數沒有預設值，3 個 positional 參數的呼叫方式
        // 不可能解析到它。已用 expression tree 實際核對過 C# 編譯器對 RedisCaptchaService.cs 那行呼叫
        // （database.StringSetAsync(key, value, TimeSpan)）真正解析到的 overload 是
        // (RedisKey, RedisValue, Expiration, ValueCondition, CommandFlags)——TimeSpan 透過隱式轉換
        // 進入 Expiration。MUST 對這個實際會被呼叫的 overload 斷言，否則 Verify 對到一個永遠不會被
        // 呼叫的簽章，不論程式碼對不對都會恆真通過，形同沒有驗證。
        mockDatabase.Verify(
            d => d.StringSetAsync(It.IsAny<RedisKey>(), It.IsAny<RedisValue>(), It.IsAny<Expiration>(), It.IsAny<ValueCondition>(), It.IsAny<CommandFlags>()),
            Times.Never);
    }
}
