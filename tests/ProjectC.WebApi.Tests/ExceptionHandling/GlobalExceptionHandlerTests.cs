using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;
using ProjectC.WebApi.ExceptionHandling;
using ProjectC.WebApi.Tests.TestSupport;

namespace ProjectC.WebApi.Tests.ExceptionHandling;

// 直接建構 GlobalExceptionHandler 與 DefaultHttpContext，不啟動主機（request-body-size-limits tasks.md 2.1）。
public class GlobalExceptionHandlerTests
{
    private const string BodySentinel = "BODY-SENTINEL-7f3a9c";
    private const string ExceptionMessageSentinel = "EXCEPTION-MESSAGE-SENTINEL-2b8e41";
    private const string SqlSentinel = "SELECT-SQL-SENTINEL-91c4d7";
    private const string ConnectionStringSentinel = "Password=CONNSTR-SENTINEL-5e02a8";

    private readonly RecordingLogger<GlobalExceptionHandler> _logger = new();

    // [RBL-011] 413 回應為 ProblemDetails，title 固定為 "PayloadTooLarge"，含 traceId。
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
        problemDetails.RootElement.GetProperty("title").GetString().Should().Be("PayloadTooLarge");
        problemDetails.RootElement.GetProperty("traceId").GetString().Should().Be(httpContext.TraceIdentifier);
    }

    // [RBL-012] 本文讀取錯誤是請求錯誤而非伺服器故障：回例外本身的 4xx，只記 Warning，
    // 且日誌不得帶本文或例外訊息（MVC 版訊息可能含請求細節）。
    [Theory]
    [InlineData(StatusCodes.Status413PayloadTooLarge, "PayloadTooLarge")]
    [InlineData(StatusCodes.Status400BadRequest, "BadRequest")]
    [InlineData(StatusCodes.Status408RequestTimeout, "RequestTimeout")]
    [InlineData(StatusCodes.Status431RequestHeaderFieldsTooLarge, "RequestHeaderFieldsTooLarge")]
    [InlineData(StatusCodes.Status418ImATeapot, "Imateapot")]
    [InlineData(450, "BadRequest")]
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
        problemDetails.RootElement.GetProperty("traceId").GetString().Should().Be(httpContext.TraceIdentifier);

        var entry = _logger.Entries.Should().ContainSingle().Subject;
        entry.Level.Should().Be(LogLevel.Warning);
        entry.Exception.Should().BeNull();
        entry.Properties.Should().Contain(new KeyValuePair<string, object?>("TraceId", httpContext.TraceIdentifier));
        var loggedText = entry.Message + string.Join("|", entry.Properties.Select(p => $"{p.Key}={p.Value}"));
        loggedText.Should().NotContain(BodySentinel).And.NotContain(ExceptionMessageSentinel);
    }

    // [AER-036] 未預期例外的 500 回應 Content-Type 為 application/problem+json。
    [Fact]
    public async Task TryHandleAsync_WhenUnexpectedException_WritesProblemJsonContentType()
    {
        var httpContext = CreateHttpContext();

        await CreateHandler().TryHandleAsync(httpContext, new InvalidOperationException("boom"), CancellationToken.None);

        httpContext.Response.ContentType.Should().Be("application/problem+json");
    }

    // [AER-037] BadHttpRequestException 的 400／408／413 回應 Content-Type 為 application/problem+json。
    [Theory]
    [InlineData(StatusCodes.Status400BadRequest)]
    [InlineData(StatusCodes.Status408RequestTimeout)]
    [InlineData(StatusCodes.Status413PayloadTooLarge)]
    public async Task TryHandleAsync_WhenBadHttpRequest_WritesProblemJsonContentType(int statusCode)
    {
        var httpContext = CreateHttpContext();

        await CreateHandler().TryHandleAsync(
            httpContext, new BadHttpRequestException(ExceptionMessageSentinel, statusCode), CancellationToken.None);

        httpContext.Response.ContentType.Should().Be("application/problem+json");
    }

    // [AER-038] 資料庫逾時的 503 回應 Content-Type 為 application/problem+json（固定既有行為）。
    [Fact]
    public async Task TryHandleAsync_WhenDatabaseTimeout_WritesProblemJsonContentType()
    {
        var httpContext = CreateHttpContext();

        await CreateHandler().TryHandleAsync(
            httpContext, new NpgsqlException("command timeout", new TimeoutException()), CancellationToken.None);

        httpContext.Response.ContentType.Should().Be("application/problem+json");
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
        problemDetails.RootElement.GetProperty("title").GetString().Should().Be("InternalServerError");
        var entry = _logger.Entries.Should().ContainSingle().Subject;
        entry.Level.Should().Be(LogLevel.Error);
        entry.Exception.Should().BeSameAs(exception);
    }

    // [AER-DB-001] 命令逾時（NpgsqlException 內含 TimeoutException）回 503 ServiceUnavailable，含 traceId、無 detail。
    [Fact]
    public async Task TryHandleAsync_WhenCommandTimeout_Writes503ServiceUnavailableWithTraceId()
    {
        var httpContext = CreateHttpContext();

        var isHandled = await CreateHandler().TryHandleAsync(
            httpContext,
            new NpgsqlException("command timeout", new TimeoutException()),
            CancellationToken.None);

        isHandled.Should().BeTrue();
        AssertServiceUnavailable(httpContext);
    }

    // [AER-DB-002] 連線池耗盡的實測型態（db-timeout-pool-tuning tasks 3.1）：InvalidOperationException → NpgsqlException → TimeoutException。
    [Fact]
    public async Task TryHandleAsync_WhenPoolWaitTimeout_Writes503ServiceUnavailable()
    {
        var httpContext = CreateHttpContext();
        var exception = new InvalidOperationException("pool", new NpgsqlException("pool", new TimeoutException()));

        await CreateHandler().TryHandleAsync(httpContext, exception, CancellationToken.None);

        AssertServiceUnavailable(httpContext);
    }

    // [AER-DB-003] 逾時為內層例外、外層為 DbUpdateException 等包裝例外時仍判定為逾時。
    [Fact]
    public async Task TryHandleAsync_WhenTimeoutWrappedInDbUpdateException_Writes503ServiceUnavailable()
    {
        var httpContext = CreateHttpContext();
        var exception = new DbUpdateException("save", new NpgsqlException("command timeout", new TimeoutException()));

        await CreateHandler().TryHandleAsync(httpContext, exception, CancellationToken.None);

        AssertServiceUnavailable(httpContext);
    }

    // [AER-DB-004] 非逾時的例外維持 500（AER-001 不變）；裸 TimeoutException 非資料庫來源，不得誤判。
    [Theory]
    [MemberData(nameof(NonDatabaseTimeoutExceptions))]
    public async Task TryHandleAsync_WhenNotDatabaseTimeout_Writes500InternalServerError(Exception exception)
    {
        var httpContext = CreateHttpContext();

        await CreateHandler().TryHandleAsync(httpContext, exception, CancellationToken.None);

        httpContext.Response.StatusCode.Should().Be(StatusCodes.Status500InternalServerError);
        using var problemDetails = ReadResponseJson(httpContext);
        problemDetails.RootElement.GetProperty("title").GetString().Should().Be("InternalServerError");
    }

    public static TheoryData<Exception> NonDatabaseTimeoutExceptions() => new()
    {
        new NpgsqlException("connection reset", new IOException("reset")),
        new TimeoutException("http client timeout"),
        new InvalidOperationException("boom"),
    };

    // [AER-DB-005] 日誌為 Warning、帶 TraceId，不含 SQL 與連線字串（例外物件不傳入日誌）。
    [Fact]
    public async Task TryHandleAsync_WhenDatabaseTimeout_LogsWarningWithoutSqlOrConnectionString()
    {
        var httpContext = CreateHttpContext();
        var exception = new NpgsqlException($"{SqlSentinel} {ConnectionStringSentinel}", new TimeoutException(ConnectionStringSentinel));
        exception.Data["sql"] = SqlSentinel;
        exception.Data["connection"] = ConnectionStringSentinel;

        await CreateHandler().TryHandleAsync(httpContext, exception, CancellationToken.None);

        var entry = _logger.Entries.Should().ContainSingle().Subject;
        entry.Level.Should().Be(LogLevel.Warning);
        entry.Exception.Should().BeNull();
        entry.Properties.Should().Contain(new KeyValuePair<string, object?>("TraceId", httpContext.TraceIdentifier));
        var loggedText = entry.Message + string.Join("|", entry.Properties.Select(p => $"{p.Key}={p.Value}"));
        loggedText.Should().NotContain(SqlSentinel).And.NotContain(ConnectionStringSentinel);
    }

    private static void AssertServiceUnavailable(DefaultHttpContext httpContext)
    {
        httpContext.Response.StatusCode.Should().Be(StatusCodes.Status503ServiceUnavailable);
        httpContext.Response.ContentType.Should().Be("application/problem+json");
        using var problemDetails = ReadResponseJson(httpContext);
        problemDetails.RootElement.GetProperty("status").GetInt32().Should().Be(503);
        problemDetails.RootElement.GetProperty("title").GetString().Should().Be("ServiceUnavailable");
        problemDetails.RootElement.GetProperty("traceId").GetString().Should().Be(httpContext.TraceIdentifier);
        problemDetails.RootElement.TryGetProperty("detail", out _).Should().BeFalse();
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
