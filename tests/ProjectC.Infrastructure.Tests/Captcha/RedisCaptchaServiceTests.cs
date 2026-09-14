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
        var connection = _fixture.CreateConnection();
        var service = CreateService(new StubCaptchaImageGenerator("TEST"), new CaptchaOptions { TtlSeconds = 1 }, connection);
        var challenge = await service.GenerateAsync(CancellationToken.None);

        await Task.Delay(TimeSpan.FromSeconds(1.5));

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
