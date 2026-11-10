using FluentAssertions;
using Moq;
using ProjectC.Application.Common;
using ProjectC.Infrastructure.Captcha;
using StackExchange.Redis;

namespace ProjectC.Infrastructure.Tests.Captcha;

// captcha-test-mode design.md 決策 2：AcceptAny 以組合持有 RedisCaptchaService（sealed，無法直接 mock），
// 故以 mock IConnectionMultiplexer／ICaptchaImageGenerator 建構真實的 RedisCaptchaService，
// 再以這兩個 mock 的呼叫紀錄判斷內部服務是否被使用。
public class AcceptAnyCaptchaServiceTests
{
    private readonly Mock<IConnectionMultiplexer> _multiplexer = new();
    private readonly Mock<IDatabase> _database = new();
    private readonly Mock<ICaptchaImageGenerator> _imageGenerator = new();
    private readonly AcceptAnyCaptchaService _service;

    public AcceptAnyCaptchaServiceTests()
    {
        _multiplexer.Setup(m => m.GetDatabase(It.IsAny<int>(), It.IsAny<object?>())).Returns(_database.Object);
        _imageGenerator.Setup(g => g.Generate()).Returns(new CaptchaImage("ABCD", new byte[] { 1, 2, 3 }));
        var inner = new RedisCaptchaService(_multiplexer.Object, _imageGenerator.Object, new CaptchaOptions());
        _service = new AcceptAnyCaptchaService(inner);
    }

    // CAPTCHA-MODE-003：任意 token 與答案皆通過，且不碰 Redis、不呼叫內部服務。
    [Fact]
    public async Task VerifyAsync_WithArbitraryTokenAndAnswer_ReturnsTrueWithoutTouchingRedis()
    {
        var result = await _service.VerifyAsync("never-generated-token", "wrong", CancellationToken.None);

        result.Should().BeTrue();
        _multiplexer.Verify(m => m.GetDatabase(It.IsAny<int>(), It.IsAny<object?>()), Times.Never);
        _imageGenerator.Verify(g => g.Generate(), Times.Never);
    }

    // CAPTCHA-MODE-004：產生端點行為不變，委派給內部服務（寫入 Redis 並回傳同一個 token 與圖片）。
    [Fact]
    public async Task GenerateAsync_DelegatesToInnerServiceOnce()
    {
        RedisKey? writtenKey = null;
        _database
            .Setup(d => d.StringSetAsync(It.IsAny<RedisKey>(), It.IsAny<RedisValue>(), It.IsAny<Expiration>(), It.IsAny<ValueCondition>(), It.IsAny<CommandFlags>()))
            .Callback<RedisKey, RedisValue, Expiration, ValueCondition, CommandFlags>((key, _, _, _, _) => writtenKey = key)
            .ReturnsAsync(true);

        var challenge = await _service.GenerateAsync(CancellationToken.None);

        _imageGenerator.Verify(g => g.Generate(), Times.Once);
        challenge.ImageBytes.Should().Equal(1, 2, 3);
        writtenKey.Should().NotBeNull();
        writtenKey!.Value.ToString().Should().Be($"captcha:{challenge.Token}");
    }
}
