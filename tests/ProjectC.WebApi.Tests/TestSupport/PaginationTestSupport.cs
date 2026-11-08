using System.Net;
using System.Text.Json;
using FluentAssertions;
using Xunit;

namespace ProjectC.WebApi.Tests.TestSupport;

/// <summary>訂單兩個列表端點共用的分頁整合測試輔助：種子訂單與 400 回應格式斷言。</summary>
public static class PaginationTestSupport
{
    /// <summary>每個查詢字串對應的 400 回應 errors 鍵（FieldErrorKeyFormatter 與模型繫結皆為小寫開頭）。</summary>
    public static TheoryData<string, string[]> InvalidPagingQueries => new()
    {
        { "?page=0", ["page"] },
        { "?page=-1", ["page"] },
        { "?page=10001", ["page"] },
        { "?page=2147483647", ["page"] },
        { "?pageSize=0", ["pageSize"] },
        { "?pageSize=101", ["pageSize"] },
        { "?page=0&pageSize=101", ["page", "pageSize"] },
        { "?page=abc", ["page"] },
        { "?pageSize=abc", ["pageSize"] },
        { "?page=99999999999", ["page"] },
        { "?page=", ["page"] },
        { "?pageSize=", ["pageSize"] },
    };

    public static async Task<IReadOnlyList<Guid>> PlaceOrdersAsync(
        HttpClient buyerClient, OrderDisplayTestData.SeededEvent seededEvent, int orderCount)
    {
        var orderIds = new List<Guid>();
        for (var i = 0; i < orderCount; i++)
        {
            orderIds.Add(await OrderDisplayTestData.PlaceOrderAsync(
                buyerClient, seededEvent, seatItemCount: 1, countItemCount: 0, firstSeatIndex: i));
        }

        return orderIds;
    }

    public static async Task AssertValidationErrorAsync(HttpResponseMessage response, IReadOnlyCollection<string> expectedErrorKeys)
    {
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        document.RootElement.GetProperty("title").GetString().Should().Be("Validation");
        document.RootElement.GetProperty("errors").EnumerateObject().Select(property => property.Name)
            .Should().BeEquivalentTo(expectedErrorKeys);
    }
}
