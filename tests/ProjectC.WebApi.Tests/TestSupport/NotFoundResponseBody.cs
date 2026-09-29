using System.Text.Json.Nodes;

namespace ProjectC.WebApi.Tests.TestSupport;

/// <summary>
/// 「視同找不到」MUST 與真正不存在的回應逐字相同（order-report-redemption-organizer-scoping design.md Decision 1）。
/// 只斷言狀態碼會漏掉「訊息不同仍洩漏存在性」，因此比對整個 body：移除每次請求都不同的 <c>traceId</c>，
/// 並把資源 ID 換成固定佔位字串，其餘欄位（title／detail／status／type）須完全相同。
/// </summary>
public static class NotFoundResponseBody
{
    public static async Task<string> ReadNormalizedAsync(HttpResponseMessage response, Guid resourceId)
    {
        var body = await response.Content.ReadAsStringAsync();
        var json = JsonNode.Parse(body)!.AsObject();
        json.Remove("traceId");
        return json.ToJsonString().Replace(resourceId.ToString(), "{id}", StringComparison.OrdinalIgnoreCase);
    }
}
