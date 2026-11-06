using System.Diagnostics;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using ProjectC.Application.Common;
using ProjectC.WebApi.Common;

namespace ProjectC.WebApi.Tests.Common;

public class ResultExtensionsTests
{
    // [AER-006][AER-007] 業務失敗回應的 traceId 必須等於執行當下 HttpContext.TraceIdentifier，
    // 使用者回報問題時才能與 log 的 TraceId 對照；status／title／detail 不得因此改變。
    [Fact]
    public async Task ToActionResult_WhenResultFailed_WritesTraceIdFromHttpContext()
    {
        var httpContext = CreateHttpContext("known-trace-id");
        var result = Result.Failure(Error.NotFound("找不到資源"));

        await result.ToActionResult().ExecuteResultAsync(CreateActionContext(httpContext));

        httpContext.Response.StatusCode.Should().Be(StatusCodes.Status404NotFound);
        httpContext.Response.ContentType.Should().StartWith("application/problem+json");
        using var body = ReadResponseJson(httpContext);
        body.RootElement.GetProperty("traceId").GetString().Should().Be("known-trace-id");
        body.RootElement.GetProperty("status").GetInt32().Should().Be(404);
        body.RootElement.GetProperty("title").GetString().Should().Be("NotFound");
        body.RootElement.GetProperty("detail").GetString().Should().Be("找不到資源");
    }

    // [AER-007] 有 Activity 時 traceId 仍須是 HttpContext.TraceIdentifier（與 log 的 TraceId 相同），
    // 而不是 ProblemDetailsFactory 預設採用的 Activity.Current.Id。
    [Fact]
    public async Task ToActionResult_WhenActivityIsCurrent_WritesTraceIdentifierNotActivityId()
    {
        using var listener = new ActivityListener
        {
            ShouldListenTo = _ => true,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
        };
        ActivitySource.AddActivityListener(listener);
        using var source = new ActivitySource(nameof(ResultExtensionsTests));
        using var activity = source.StartActivity("request");
        activity.Should().NotBeNull();
        var httpContext = CreateHttpContext("known-trace-id");

        await Result.Failure(Error.NotFound("找不到資源")).ToActionResult().ExecuteResultAsync(CreateActionContext(httpContext));

        using var body = ReadResponseJson(httpContext);
        body.RootElement.GetProperty("traceId").GetString().Should().Be("known-trace-id").And.NotBe(activity!.Id);
    }

    private static DefaultHttpContext CreateHttpContext(string traceIdentifier)
    {
        var services = new ServiceCollection().AddLogging().AddMvc().Services.BuildServiceProvider();
        return new DefaultHttpContext
        {
            TraceIdentifier = traceIdentifier,
            RequestServices = services,
            Response = { Body = new MemoryStream() },
        };
    }

    private static ActionContext CreateActionContext(HttpContext httpContext) =>
        new(httpContext, new RouteData(), new Microsoft.AspNetCore.Mvc.Abstractions.ActionDescriptor());

    private static JsonDocument ReadResponseJson(HttpContext httpContext)
    {
        httpContext.Response.Body.Position = 0;
        return JsonDocument.Parse(httpContext.Response.Body);
    }
}
