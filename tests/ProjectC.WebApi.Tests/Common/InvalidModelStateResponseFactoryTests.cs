using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using ProjectC.WebApi.Extensions;

namespace ProjectC.WebApi.Tests.Common;

// validation-error-fields 決策 5 細節 (1)(2)(3)：工廠對 ModelState 的整形規則，不經 HTTP 直接守護。
public class InvalidModelStateResponseFactoryTests
{
    // 空陣列鍵對 client 毫無資訊，且違反「每個鍵至少一則訊息」。
    [Fact]
    public async Task InvalidModelStateFactory_EntryWithoutMessages_OmitsKey()
    {
        var modelState = new ModelStateDictionary();
        modelState.AddModelError("title", "bad title");
        modelState.SetModelValue("empty", "x", "x");

        var errors = await ExecuteAndReadErrorsAsync(modelState);

        errors.Keys.Should().BeEquivalentTo(["title"]);
    }

    // 參數名鍵（如 request）會隨參數改名而變、且不是 JSON 路徑，不得外洩給 client。
    [Fact]
    public async Task InvalidModelStateFactory_ParameterNameKey_IsOmitted()
    {
        var modelState = new ModelStateDictionary();
        modelState.AddModelError("request", "The request field is required.");
        modelState.AddModelError(string.Empty, "A non-empty request body is required.");

        var errors = await ExecuteAndReadErrorsAsync(modelState);

        errors.Keys.Should().BeEquivalentTo([string.Empty]);
        errors[string.Empty].Should().Equal("A non-empty request body is required.");
    }

    // 非本文來源（query）的參數，其鍵本身就是欄位名稱，不得被當成本文參數名濾掉。
    [Fact]
    public async Task InvalidModelStateFactory_QueryParameterKey_IsKept()
    {
        var modelState = new ModelStateDictionary();
        modelState.AddModelError("page", "bad page");

        var errors = await ExecuteAndReadErrorsAsync(modelState, queryParameterName: "page");

        errors.Keys.Should().BeEquivalentTo(["page"]);
    }

    // 防禦性後備：濾除後無任何鍵時訊息改列空字串鍵，確保 errors 鍵數大於 0 且不含參數名鍵。
    [Fact]
    public async Task InvalidModelStateFactory_OnlyParameterNameKey_FallsBackToEmptyKey()
    {
        var modelState = new ModelStateDictionary();
        modelState.AddModelError("request", "The request field is required.");

        var errors = await ExecuteAndReadErrorsAsync(modelState);

        errors.Keys.Should().BeEquivalentTo([string.Empty]);
        errors[string.Empty].Should().Equal("The request field is required.");
    }

    // 框架原行為：訊息為空但有例外時補通用訊息，避免輸出空字串訊息。
    [Fact]
    public async Task InvalidModelStateFactory_ErrorWithoutMessage_UsesDefaultMessage()
    {
        var modelState = new ModelStateDictionary();
        modelState.SetModelValue("maxTicketsPerOrder", "abc", "abc");
        modelState["maxTicketsPerOrder"]!.Errors.Add(new FormatException("raw"));

        var errors = await ExecuteAndReadErrorsAsync(modelState);

        errors["maxTicketsPerOrder"].Should().Equal("The input was not valid.");
    }

    private static async Task<Dictionary<string, string[]>> ExecuteAndReadErrorsAsync(
        ModelStateDictionary modelState, string? queryParameterName = null)
    {
        var parameters = new List<ParameterDescriptor>();
        var descriptor = new ActionDescriptor { Parameters = parameters };
        parameters.Add(new ParameterDescriptor
        {
            Name = "request",
            ParameterType = typeof(object),
            BindingInfo = new BindingInfo { BindingSource = BindingSource.Body },
        });
        if (queryParameterName is not null)
        {
            parameters.Add(new ParameterDescriptor
            {
                Name = queryParameterName,
                ParameterType = typeof(int),
                BindingInfo = new BindingInfo { BindingSource = BindingSource.Query },
            });
        }

        var httpContext = new DefaultHttpContext
        {
            RequestServices = new ServiceCollection().AddLogging().AddMvc().Services.BuildServiceProvider(),
            Response = { Body = new MemoryStream() },
        };
        var actionContext = new ActionContext(httpContext, new RouteData(), descriptor, modelState);

        var result = ModelStateResponseServiceCollectionExtensions.CreateInvalidModelStateResponse(actionContext);
        await result.ExecuteResultAsync(actionContext);

        httpContext.Response.Body.Position = 0;
        using var body = JsonDocument.Parse(httpContext.Response.Body);
        return body.RootElement.GetProperty("errors").EnumerateObject()
            .ToDictionary(property => property.Name, property => property.Value.EnumerateArray().Select(item => item.GetString()!).ToArray());
    }
}
