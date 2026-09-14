using FluentAssertions;
using Moq;
using ProjectC.Application.Common;
using ProjectC.Infrastructure.Captcha;
using StackExchange.Redis;

namespace ProjectC.Infrastructure.Tests.Captcha;

// CAPTCHA-FAIL-001／002／003（Infrastructure 層級部分）：Redis 連線／逾時例外 MUST 原樣往外拋，
// 不被吞掉、不回傳驗證通過——與 RedisQueryCache 的 fail-open 慣例相反（captcha-verification
// design.md 決策 5）。比照既有 RedisQueryCacheTests 用 mock 拋出例外驗證行為的既定手法，不用真實
// 網路逾時這種難以決定性重現的時序；端到端層級的可觀察行為驗證見 CaptchaFailClosedComponentTests（6.4）。
public class RedisCaptchaServiceFailClosedTests
{
    private sealed class StubCaptchaImageGenerator : ICaptchaImageGenerator
    {
        public CaptchaImage Generate() => new("TEST", new byte[] { 1, 2, 3 });
    }

    private static IConnectionMultiplexer CreateThrowingConnection(Exception exceptionToThrow)
    {
        var mock = new Mock<IConnectionMultiplexer>();
        mock.Setup(m => m.GetDatabase(It.IsAny<int>(), It.IsAny<object?>())).Throws(exceptionToThrow);
        return mock.Object;
    }

    private static RedisConnectionException NewConnectionException()
        => new(ConnectionFailureType.UnableToConnect, CommandFlags.None, "simulated connection failure", null, CommandStatus.Unknown);

    private static RedisTimeoutException NewTimeoutException()
        => new(CommandFlags.None, "simulated timeout", CommandStatus.Unknown);

    private static RedisCaptchaService CreateService(Exception exceptionToThrow)
        => new(CreateThrowingConnection(exceptionToThrow), new StubCaptchaImageGenerator(), new CaptchaOptions());

    [Fact]
    public async Task GenerateAsync_WhenRedisConnectionFails_ThrowsWithoutFailingOpen()
    {
        var service = CreateService(NewConnectionException());

        var act = () => service.GenerateAsync(CancellationToken.None);

        await act.Should().ThrowAsync<RedisConnectionException>();
    }

    [Fact]
    public async Task VerifyAsync_WhenRedisConnectionFails_ThrowsWithoutFailingOpen()
    {
        var service = CreateService(NewConnectionException());

        var act = () => service.VerifyAsync(Guid.NewGuid().ToString(), "TEST", CancellationToken.None);

        await act.Should().ThrowAsync<RedisConnectionException>();
    }

    // CAPTCHA-FAIL-003：逾時比照無法連線處理，同樣 fail-closed。
    [Fact]
    public async Task GenerateAsync_WhenRedisTimesOut_ThrowsWithoutFailingOpen()
    {
        var service = CreateService(NewTimeoutException());

        var act = () => service.GenerateAsync(CancellationToken.None);

        await act.Should().ThrowAsync<RedisTimeoutException>();
    }

    [Fact]
    public async Task VerifyAsync_WhenRedisTimesOut_ThrowsWithoutFailingOpen()
    {
        var service = CreateService(NewTimeoutException());

        var act = () => service.VerifyAsync(Guid.NewGuid().ToString(), "TEST", CancellationToken.None);

        await act.Should().ThrowAsync<RedisTimeoutException>();
    }
}
