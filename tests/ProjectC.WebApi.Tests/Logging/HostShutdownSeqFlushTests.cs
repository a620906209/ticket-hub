using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ProjectC.WebApi.Tests.TestSupport;
using Xunit;

namespace ProjectC.WebApi.Tests.Logging;

// exception-log-masking tasks.md 4.5：host 關機時外層 logger 被 dispose，內層 Seq 的最後一批日誌要 flush 出去且已遮蔽。
[Collection(GlobalLoggingStateGroup.Name)]
public class HostShutdownSeqFlushTests
{
    private const string Secret = "s3cret-pw";

    [Fact]
    public async Task HostDispose_FlushesLastMaskedBatchToSeq()
    {
        using var fakeSeq = new FakeSeqIngestionServer();
        var factory = new ObservabilityWebApplicationFactory { SeqServerUrl = fakeSeq.BaseUrl };
        IAsyncLifetime lifetime = factory;
        await lifetime.InitializeAsync();
        try
        {
            var logger = factory.Services.GetRequiredService<ILoggerFactory>().CreateLogger("ProjectC.ShutdownProbe");
            logger.LogError(new InvalidOperationException("Password=" + Secret), "shutdown-probe");
        }
        finally
        {
            await lifetime.DisposeAsync();
        }

        var body = string.Concat(fakeSeq.Bodies);
        body.Should().Contain("shutdown-probe").And.Contain("Password=***").And.NotContain(Secret);
    }
}
