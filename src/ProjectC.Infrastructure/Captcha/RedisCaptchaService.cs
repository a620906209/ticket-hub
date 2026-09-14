using System.Security.Cryptography;
using System.Text;
using ProjectC.Application.Common;
using ProjectC.Application.Common.Interfaces;
using StackExchange.Redis;

namespace ProjectC.Infrastructure.Captcha;

// MUST NOT 捕捉 Redis 連線／逾時例外——與 RedisQueryCache／RedisDistributedLock 的 fail-open 慣例
// 相反，讓例外原樣往外拋，交由既有全域 IExceptionHandler 轉換為技術性錯誤回應
// （captcha-verification design.md 決策 5，CAPTCHA-FAIL-001／002／003）。
public sealed class RedisCaptchaService : ICaptchaService
{
    private const string KeyPrefix = "captcha:";

    private readonly IConnectionMultiplexer _connectionMultiplexer;
    private readonly ICaptchaImageGenerator _imageGenerator;
    private readonly CaptchaOptions _options;

    public RedisCaptchaService(
        IConnectionMultiplexer connectionMultiplexer,
        ICaptchaImageGenerator imageGenerator,
        CaptchaOptions options)
    {
        _connectionMultiplexer = connectionMultiplexer;
        _imageGenerator = imageGenerator;
        _options = options;
    }

    public async Task<CaptchaChallenge> GenerateAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var token = Guid.NewGuid().ToString();
        var challenge = _imageGenerator.Generate();

        // ICaptchaImageGenerator.Generate 是 CPU-bound 的繪圖運算，取消可能發生在這段耗時期間；
        // 開頭檢查一次不足以攔截，MUST 在 Redis 寫入前再檢查一次（CAPTCHA-CANCEL-004，design.md 決策 10）。
        cancellationToken.ThrowIfCancellationRequested();

        var database = _connectionMultiplexer.GetDatabase();
        var answerHash = Hash(challenge.Content);
        await database.StringSetAsync(KeyPrefix + token, answerHash, TimeSpan.FromSeconds(_options.TtlSeconds));

        return new CaptchaChallenge(token, challenge.ImageBytes);
    }

    public async Task<bool> VerifyAsync(string token, string answer, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var answerHash = Hash(answer.Trim());

        cancellationToken.ThrowIfCancellationRequested();

        var database = _connectionMultiplexer.GetDatabase();
        // GETDEL：讀取與刪除須為單一原子操作，MUST NOT 拆成 StringGetAsync 後再 KeyDeleteAsync 兩步
        // ——兩步寫法在並發驗證下會產生競態，讓同一 token 被兩個請求都驗證成功
        // （captcha-verification design.md 決策 4，CAPTCHA-VERIFY-006）。
        var storedHash = await database.StringGetDeleteAsync(KeyPrefix + token);

        return storedHash.HasValue && storedHash == answerHash;
    }

    private static string Hash(string value)
    {
        var normalized = value.ToUpperInvariant();
        var hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        return Convert.ToHexString(hashBytes);
    }
}
