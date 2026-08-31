using FluentAssertions;
using Microsoft.Extensions.Logging;
using ProjectC.WebApi.Tests.TestSupport;
using Serilog;
using Serilog.Events;
using Serilog.Extensions.Logging;

namespace ProjectC.WebApi.Tests.Observability;

public class StructuredLoggingTests
{
    // 對應 AC: OBS-STRUCTURED-FIELD-PRESERVED
    [Fact]
    public void NamedTemplateParameter_IsPreservedAsIndependentStructuredProperty()
    {
        var sink = new InMemoryLogEventSink();
        var serilogLogger = new LoggerConfiguration().WriteTo.Sink(sink).CreateLogger();
        using var loggerFactory = new SerilogLoggerFactory(serilogLogger, dispose: true);
        var logger = loggerFactory.CreateLogger("Test");

        var orderId = Guid.NewGuid();
        logger.LogError(new InvalidOperationException("boom"), "Failed to prepare notification for order {OrderId}.", orderId);

        var loggedEvent = sink.Events.Should().ContainSingle().Subject;
        loggedEvent.Properties.Should().ContainKey("OrderId");
        var value = ((ScalarValue)loggedEvent.Properties["OrderId"]).Value;
        value.Should().Be(orderId);
    }
}
