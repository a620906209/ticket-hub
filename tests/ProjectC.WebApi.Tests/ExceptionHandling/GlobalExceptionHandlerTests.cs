using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using ProjectC.WebApi.ExceptionHandling;
using ProjectC.WebApi.Tests.TestSupport;

namespace ProjectC.WebApi.Tests.ExceptionHandling;

// 直接建構 GlobalExceptionHandler 與 DefaultHttpContext，不啟動主機（request-body-size-limits tasks.md 2.1）。
public class GlobalExceptionHandlerTests
{
    private const string BodySentinel = "BODY-SENTINEL-7f3a9c";
    private const string ExceptionMessageSentinel = "EXCEPTION-MESSAGE-SENTINEL-2b8e41";

    private readonly RecordingLogger<GlobalExceptionHandler> _logger = new();

    // [RBL-011] 413 回應為 ProblemDetails，title 固定為 "Request body too large."，含 traceId。
    [Fact]
    public async Task TryHandleAsync_WhenBadHttpRequest413_WritesProblemDetailsWith413()
    {
        var httpContext = CreateHttpContext();

        var isHandled = await CreateHandler().TryHandleAsync(
            httpContext,
            new BadHttpRequestException(ExceptionMessageSentinel, StatusCodes.Status413PayloadTooLarge),
            CancellationToken.None);

        isHandled.Should().BeTrue();
        httpContext.Response.StatusCode.Should().Be(StatusCodes.Status413PayloadTooLarge);
        using var problemDetails = ReadResponseJson(httpContext);
        problemDetails.RootElement.GetProperty("status").GetInt32().Should().Be(413);
        problemDetails.RootElement.GetProperty("title").GetString().Should().Be("Request body too large.");
        problemDetails.RootElement.GetProperty("traceId").GetString().Should().Be(httpContext.TraceIdentifier);
    }

    // [RBL-012] 本文讀取錯誤是請求錯誤而非伺服器故障：回例外本身的 4xx，只記 Warning，
    // 且日誌不得帶本文或例外訊息（MVC 版訊息可能含請求細節）。
    [Theory]
    [InlineData(StatusCodes.Status413PayloadTooLarge, "Request body too large.")]
    [InlineData(StatusCodes.Status400BadRequest, "Bad Request")]
    [InlineData(StatusCodes.Status408RequestTimeout, "Request Timeout")]
    public async Task TryHandleAsync_WhenBadHttpRequest_LogsWarningNotError(int statusCode, string expectedTitle)
    {
        var httpContext = CreateHttpContext();

        await CreateHandler().TryHandleAsync(
            httpContext,
            new BadHttpRequestException(ExceptionMessageSentinel, statusCode),
            CancellationToken.None);

        httpContext.Response.StatusCode.Should().Be(statusCode);
        using var problemDetails = ReadResponseJson(httpContext);
        problemDetails.RootElement.GetProperty("status").GetInt32().Should().Be(statusCode);
        problemDetails.RootElement.GetProperty("title").GetString().Should().Be(expectedTitle);

        var entry = _logger.Entries.Should().ContainSingle().Subject;
        entry.Level.Should().Be(LogLevel.Warning);
        entry.Exception.Should().BeNull();
        entry.Properties.Should().Contain(new KeyValuePair<string, object?>("TraceId", httpContext.TraceIdentifier));
        var loggedText = entry.Message + string.Join("|", entry.Properties.Select(p => $"{p.Key}={p.Value}"));
        loggedText.Should().NotContain(BodySentinel).And.NotContain(ExceptionMessageSentinel);
    }

    // [RBL-013] 非本文讀取錯誤的例外不得被新分支吞掉，維持 500 + Error。
    [Fact]
    public async Task TryHandleAsync_WhenOtherException_Returns500AndLogsError()
    {
        var httpContext = CreateHttpContext();
        var exception = new InvalidOperationException("boom");

        await CreateHandler().TryHandleAsync(httpContext, exception, CancellationToken.None);

        httpContext.Response.StatusCode.Should().Be(StatusCodes.Status500InternalServerError);
        using var problemDetails = ReadResponseJson(httpContext);
        problemDetails.RootElement.GetProperty("status").GetInt32().Should().Be(500);
        problemDetails.RootElement.GetProperty("title").GetString().Should().Be("An unexpected error occurred.");
        var entry = _logger.Entries.Should().ContainSingle().Subject;
        entry.Level.Should().Be(LogLevel.Error);
        entry.Exception.Should().BeSameAs(exception);
    }

    private GlobalExceptionHandler CreateHandler() => new(_logger);

    private static DefaultHttpContext CreateHttpContext()
    {
        var httpContext = new DefaultHttpContext { TraceIdentifier = $"trace-{Guid.NewGuid():N}" };
        httpContext.Request.Method = HttpMethods.Post;
        httpContext.Request.Path = "/api/orders";
        httpContext.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes($$"""{"note":"{{BodySentinel}}"}"""));
        httpContext.Response.Body = new MemoryStream();
        return httpContext;
    }

    private static JsonDocument ReadResponseJson(HttpContext httpContext)
    {
        httpContext.Response.Body.Position = 0;
        return JsonDocument.Parse(httpContext.Response.Body);
    }
}
