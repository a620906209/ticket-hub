using System.Reflection;
using FluentAssertions;
using Npgsql;
using ProjectC.WebApi.ExceptionHandling;

namespace ProjectC.WebApi.Tests.ExceptionHandling;

// db-timeout-pool-tuning hardener：偵測器遇到 null、AggregateException 與循環例外鏈時的行為。
public class DatabaseTimeoutDetectorTests
{
    [Fact]
    public void IsDatabaseTimeout_WhenExceptionIsNull_ThrowsArgumentNullException()
    {
        var act = () => DatabaseTimeoutDetector.IsDatabaseTimeout(null!);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void IsDatabaseTimeout_WhenTimeoutIsInSecondInnerOfAggregate_ReturnsTrue()
    {
        var timeoutBranch = new NpgsqlException("command timeout", new TimeoutException());
        var aggregate = new AggregateException(new InvalidOperationException("other"), timeoutBranch);

        DatabaseTimeoutDetector.IsDatabaseTimeout(aggregate).Should().BeTrue();
    }

    [Fact]
    public void IsDatabaseTimeout_WhenExceptionChainIsCyclic_TerminatesAndReturnsFalse()
    {
        var first = new InvalidOperationException("first");
        var second = new InvalidOperationException("second", first);
        typeof(Exception).GetField("_innerException", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(first, second);

        // 保護失效時偵測器會無窮迴圈；放到背景工作並設逾時，讓失效時以逾時失敗而不是讓測試行程掛住。
        var detection = Task.Run(() => DatabaseTimeoutDetector.IsDatabaseTimeout(first));

        detection.Wait(TimeSpan.FromSeconds(5)).Should().BeTrue("循環例外鏈必須在時限內走完");
        detection.Result.Should().BeFalse();
    }

    [Fact]
    public void IsDatabaseTimeout_WhenBareTimeoutWithoutNpgsqlException_ReturnsFalse()
    {
        DatabaseTimeoutDetector.IsDatabaseTimeout(new TimeoutException()).Should().BeFalse();
    }
}
