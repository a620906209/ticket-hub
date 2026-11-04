using System.Runtime.CompilerServices;
using FluentAssertions;
using ProjectC.WebApi.Logging;
using ProjectC.WebApi.Tests.TestSupport;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Serilog.Parsing;

namespace ProjectC.WebApi.Tests.Logging;

public class ExceptionMaskingSinkTests
{
    private const string Secret = "s3cret-pw";

    // 對應 OBS-EXMASK-003
    [Fact]
    public void Emit_SecretInInnerExceptionChain_IsMaskedAtEveryLevel()
    {
        var capture = new InMemoryLogEventSink();
        using var sink = CreateSink(capture);
        var exception = new InvalidOperationException("outer", new ArgumentException("middle", new FormatException("Password=" + Secret)));

        sink.Emit(CreateEvent(exception));

        ForwardedText(capture).Should().NotContain(Secret);
    }

    // 對應 OBS-EXMASK-004
    [Fact]
    public void Emit_SecretInAggregateExceptionInner_IsMasked()
    {
        var capture = new InMemoryLogEventSink();
        using var sink = CreateSink(capture);
        var exception = new AggregateException(
            new InvalidOperationException("ok"),
            new InvalidOperationException("Password=" + Secret));

        sink.Emit(CreateEvent(exception));

        ForwardedText(capture).Should().NotContain(Secret);
    }

    [Fact]
    public void Emit_WhenMasked_ReplacementHasNoPlaintextNoInnerExceptionAndNoOriginalReference()
    {
        var capture = new InMemoryLogEventSink();
        using var sink = CreateSink(capture);
        var original = ThrowAndCatch("Password=" + Secret);

        sink.Emit(CreateEvent(original));

        var forwarded = capture.Events.Single().Exception.Should().BeOfType<MaskedException>().Subject;
        forwarded.Message.Should().NotContain(Secret);
        forwarded.ToString().Should().NotContain(Secret);
        forwarded.InnerException.Should().BeNull();
        forwarded.Should().NotBeSameAs(original);
        forwarded.OriginalTypeName.Should().Be(typeof(InvalidOperationException).FullName);
    }

    [Fact]
    public void Emit_WhenMasked_DoesNotModifyOriginalEventProperties()
    {
        var capture = new InMemoryLogEventSink();
        using var sink = CreateSink(capture);
        var original = CreateEvent(new InvalidOperationException("Password=" + Secret));

        sink.Emit(original);

        capture.Events.Single().Should().NotBeSameAs(original);
        original.Exception.Should().BeOfType<InvalidOperationException>("原事件的例外不得被就地替換");
        original.Exception!.Message.Should().Contain(Secret);
    }

    // 對應 OBS-EXMASK-010
    [Fact]
    public void Emit_WhenMasked_KeepsTypeNameAndThrowingMethodInStack()
    {
        var capture = new InMemoryLogEventSink();
        using var sink = CreateSink(capture);

        sink.Emit(CreateEvent(ThrowAndCatch("Password=" + Secret)));

        ForwardedText(capture).Should().Contain("InvalidOperationException")
            .And.Contain(nameof(ThrowAndCatch))
            .And.Contain("Password=***")
            .And.NotContain(Secret);
    }

    // 對應 OBS-EXMASK-011
    [Fact]
    public void Emit_WhenMasked_OuterTypeAppearsBeforeInnerType()
    {
        var capture = new InMemoryLogEventSink();
        using var sink = CreateSink(capture);
        var exception = new InvalidOperationException("outer", new FormatException("Password=" + Secret));

        sink.Emit(CreateEvent(exception));

        var text = ForwardedText(capture);
        text.IndexOf("InvalidOperationException", StringComparison.Ordinal)
            .Should().BeLessThan(text.IndexOf("FormatException", StringComparison.Ordinal));
        text.Should().Contain("FormatException");
    }

    // 對應 OBS-EXMASK-009
    [Fact]
    public void Emit_WhenNothingMatches_ForwardsOriginalEventAndExceptionByReference()
    {
        var capture = new InMemoryLogEventSink();
        using var sink = CreateSink(capture);
        var original = CreateEvent(new InvalidOperationException("Sequence contains no elements"));

        sink.Emit(original);

        capture.Events.Single().Should().BeSameAs(original);
        capture.Events.Single().Exception.Should().BeSameAs(original.Exception);
    }

    [Fact]
    public void Emit_WhenEventHasNoException_ForwardsOriginalEvent()
    {
        var capture = new InMemoryLogEventSink();
        using var sink = CreateSink(capture);
        var original = CreateEvent(null);

        sink.Emit(original);

        capture.Events.Single().Should().BeSameAs(original);
    }

    // 對應 OBS-EXMASK-014
    [Theory]
    [InlineData("Sequence contains no elements")]
    [InlineData("Password=s3cret-pw")]
    public void Emit_PropertiesAndRenderedMessage_AreUnchangedWhetherOrNotMasked(string exceptionMessage)
    {
        var capture = new InMemoryLogEventSink();
        using var sink = CreateSink(capture);
        var original = CreateEvent(new InvalidOperationException(exceptionMessage), orderId: 42);
        var expectedMessage = original.RenderMessage();

        sink.Emit(original);

        var forwarded = capture.Events.Single();
        forwarded.RenderMessage().Should().Be(expectedMessage);
        forwarded.Properties["OrderId"].ToString().Should().Be("42");
        forwarded.Level.Should().Be(original.Level);
        forwarded.Timestamp.Should().Be(original.Timestamp);
        forwarded.MessageTemplate.Should().BeSameAs(original.MessageTemplate);
    }

    [Fact]
    public void Emit_WhenMasked_PreservesTraceAndSpanIds()
    {
        var capture = new InMemoryLogEventSink();
        using var sink = CreateSink(capture);
        var traceId = System.Diagnostics.ActivityTraceId.CreateRandom();
        var spanId = System.Diagnostics.ActivitySpanId.CreateRandom();
        var original = new LogEvent(
            DateTimeOffset.UtcNow,
            LogEventLevel.Error,
            new InvalidOperationException("Password=" + Secret),
            new MessageTemplateParser().Parse("boom"),
            [],
            traceId,
            spanId);

        sink.Emit(original);

        var forwarded = capture.Events.Single();
        forwarded.TraceId.Should().Be(traceId);
        forwarded.SpanId.Should().Be(spanId);
    }

    // 對應 OBS-EXMASK-012
    [Theory]
    [MemberData(nameof(FailingMaskers))]
    public void Emit_WhenMaskingFails_ForwardsTypeOnlyPlaceholderAndFixedTemplateWarning(Func<string, string> failingMasker)
    {
        var capture = new InMemoryLogEventSink();
        using var sink = CreateSink(capture, failingMasker);
        var original = CreateEvent(new InvalidOperationException("Password=" + Secret));

        var act = () => sink.Emit(original);

        act.Should().NotThrow();
        var events = capture.Events.ToList();
        var forwarded = events.Single(e => e.Level == LogEventLevel.Error);
        var placeholder = forwarded.Exception.Should().BeOfType<MaskedException>().Subject;
        placeholder.ToString().Should().Be(typeof(InvalidOperationException).FullName);
        forwarded.RenderMessage().Should().Be(original.RenderMessage());

        var warning = events.Single(e => e.Level == LogEventLevel.Warning);
        warning.MessageTemplate.Text.Should().Be("Exception masking failed for {ExceptionType}");
        warning.Exception.Should().BeNull();
        var warningText = warning.RenderMessage() + string.Concat(warning.Properties.Select(p => p.Key + p.Value));
        warningText.Should().Contain(typeof(InvalidOperationException).FullName!).And.NotContain(Secret);
    }

    [Fact]
    public void Emit_WhenExceptionToStringThrows_ForwardsPlaceholderWithoutThrowing()
    {
        var capture = new InMemoryLogEventSink();
        using var sink = CreateSink(capture);

        var act = () => sink.Emit(CreateEvent(new ThrowingToStringException()));

        act.Should().NotThrow();
        capture.Events.Single(e => e.Level == LogEventLevel.Error).Exception!.ToString()
            .Should().Be(typeof(ThrowingToStringException).FullName);
        capture.Events.Should().ContainSingle(e => e.Level == LogEventLevel.Warning);
    }

    [Fact]
    public void Dispose_DisposesInnerLogger()
    {
        var disposable = new DisposableSink();
        var inner = new LoggerConfiguration().WriteTo.Sink(disposable).CreateLogger();
        var sink = new ExceptionMaskingSink(inner);

        sink.Dispose();

        disposable.IsDisposed.Should().BeTrue();
    }

    public static TheoryData<Func<string, string>> FailingMaskers() => new()
    {
        text => throw new InvalidOperationException("masker bug: " + text),
        text => throw new System.Text.RegularExpressions.RegexMatchTimeoutException(text, "pattern", TimeSpan.FromMilliseconds(100)),
    };

    private sealed class ThrowingToStringException : Exception
    {
        public override string ToString() => throw new InvalidOperationException("ToString failed");
    }

    private sealed class DisposableSink : ILogEventSink, IDisposable
    {
        public bool IsDisposed { get; private set; }

        public void Emit(LogEvent logEvent)
        {
        }

        public void Dispose() => IsDisposed = true;
    }

    internal static ExceptionMaskingSink CreateSink(ILogEventSink capture, Func<string, string>? maskText = null)
    {
        var inner = new LoggerConfiguration().MinimumLevel.Verbose().WriteTo.Sink(capture).CreateLogger();
        return new ExceptionMaskingSink(inner, maskText);
    }

    internal static LogEvent CreateEvent(Exception? exception, int orderId = 1)
    {
        var template = new MessageTemplateParser().Parse("Order {OrderId} failed");
        return new LogEvent(
            DateTimeOffset.UtcNow,
            LogEventLevel.Error,
            exception,
            template,
            [new LogEventProperty("OrderId", new ScalarValue(orderId))]);
    }

    private static string ForwardedText(InMemoryLogEventSink capture)
    {
        return capture.Events.Single().Exception!.ToString();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static InvalidOperationException ThrowAndCatch(string message)
    {
        try
        {
            throw new InvalidOperationException(message);
        }
        catch (InvalidOperationException exception)
        {
            return exception;
        }
    }
}
