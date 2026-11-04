using Serilog.Core;
using Serilog.Events;
using Serilog.Parsing;

namespace ProjectC.WebApi.Logging;

/// <summary>
/// 在管線最外層遮蔽 <see cref="LogEvent.Exception"/> 的文字，再轉給持有所有輸出 sink 的內層 logger
/// （exception-log-masking design.md 決策 1、2）。
/// </summary>
public sealed class ExceptionMaskingSink : ILogEventSink, IDisposable
{
    private readonly Logger _innerLogger;
    private readonly Func<string, string> _maskText;

    public ExceptionMaskingSink(Logger innerLogger, Func<string, string>? maskText = null)
    {
        ArgumentNullException.ThrowIfNull(innerLogger);

        _innerLogger = innerLogger;
        _maskText = maskText ?? SensitiveTextMasker.Mask;
    }

    public void Emit(LogEvent logEvent)
    {
        ArgumentNullException.ThrowIfNull(logEvent);

        var exception = logEvent.Exception;
        if (exception is null)
        {
            _innerLogger.Write(logEvent);
            return;
        }

        string? failedTypeName = null;
        LogEvent forwarded;
        try
        {
            forwarded = MaskException(logEvent, exception);
        }
        catch (Exception maskingFailure)
        {
            // 遮蔽失敗不重擲：Serilog 會吞 sink 例外，重擲只會讓該筆日誌消失。降級為只含型別名稱的占位文字，
            // 並另送固定範本的 Warning 留下可查的訊號（design.md 決策 4）；maskingFailure 本身可能含原文，刻意不記錄。
            _ = maskingFailure;
            failedTypeName = exception.GetType().FullName ?? exception.GetType().Name;
            forwarded = CopyWithException(logEvent, new MaskedException(failedTypeName, failedTypeName));
        }

        _innerLogger.Write(forwarded);
        if (failedTypeName is not null)
        {
            _innerLogger.Write(CreateMaskingFailedWarning(logEvent.Timestamp, failedTypeName));
        }
    }

    public void Dispose()
    {
        _innerLogger.Dispose();
    }

    private LogEvent MaskException(LogEvent logEvent, Exception exception)
    {
        var originalText = exception.ToString();
        var maskedText = _maskText(originalText);
        if (string.Equals(originalText, maskedText, StringComparison.Ordinal))
        {
            return logEvent;
        }

        return CopyWithException(logEvent, new MaskedException(exception.GetType().FullName ?? exception.GetType().Name, maskedText));
    }

    private static LogEvent CreateMaskingFailedWarning(DateTimeOffset timestamp, string exceptionTypeName)
    {
        var template = new MessageTemplateParser().Parse("Exception masking failed for {ExceptionType}");
        return new LogEvent(
            timestamp,
            LogEventLevel.Warning,
            null,
            template,
            [
                new LogEventProperty("ExceptionType", new ScalarValue(exceptionTypeName)),
                new LogEventProperty(Constants.SourceContextPropertyName, new ScalarValue(typeof(ExceptionMaskingSink).FullName)),
            ]);
    }

    // 建構新事件而不是就地修改：原事件的 Properties 可能與其他 sink 共用。
    private static LogEvent CopyWithException(LogEvent source, Exception exception)
    {
        var properties = source.Properties.Select(pair => new LogEventProperty(pair.Key, pair.Value));
        return new LogEvent(source.Timestamp, source.Level, exception, source.MessageTemplate, properties, source.TraceId ?? default, source.SpanId ?? default);
    }
}
