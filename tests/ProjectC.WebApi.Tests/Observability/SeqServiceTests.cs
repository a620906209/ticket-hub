using FluentAssertions;
using ProjectC.WebApi.Tests.TestSupport;

namespace ProjectC.WebApi.Tests.Observability;

// 用 Testcontainers 啟動的真實 Seq 容器（與 docker-compose.yml 定義的 seq 服務完全獨立），
// 自動化驗證 OBS-SEQ-SERVICE-STARTS／OBS-API-LOG-QUERYABLE-IN-SEQ 這兩條 AC——不再只靠人工驗證
// （observability design.md 決策 8）。
public class SeqServiceTests : IClassFixture<SeqTestcontainersFixture>, IClassFixture<ObservabilityWebApplicationFactory>
{
    private readonly SeqTestcontainersFixture _seq;
    private readonly ObservabilityWebApplicationFactory _factory;

    public SeqServiceTests(SeqTestcontainersFixture seq, ObservabilityWebApplicationFactory factory)
    {
        _seq = seq;
        _factory = factory;
        _factory.SeqServerUrl = _seq.BaseUrl;
    }

    // 對應 AC: OBS-SEQ-SERVICE-STARTS
    [Fact]
    public async Task SeqContainer_WebUi_RespondsSuccessfully()
    {
        using var httpClient = new HttpClient();

        var response = await httpClient.GetAsync(_seq.BaseUrl);

        response.IsSuccessStatusCode.Should().BeTrue();
    }

    // 對應 AC: OBS-API-LOG-QUERYABLE-IN-SEQ
    [Fact]
    public async Task ApiRequest_LogIsQueryableInSeq()
    {
        // 原本用自訂 header 當 marker 是假陽性：(1) header 從不出現在日誌裡（本專案的請求摘要日誌
        // 刻意不記錄 headers，見 spec.md「日誌不得輸出敏感資訊」），(2) 查詢時只驗證「訊息含
        // "HTTP"」——任何殘留在 Seq 裡的舊日誌都會讓斷言通過，不代表這次請求真的寫進去了
        // （實測發現）。改用一個會被 UseSerilogRequestLogging 記進 RequestPath 屬性的唯一路徑片段
        // （不存在的 Guid，命中既有 GetEventSeats 端點，安全回傳 404、不會拋例外），查詢時比對
        // 這個具體、不可能是殘留資料的值。
        var uniqueMarker = Guid.NewGuid();
        var client = _factory.CreateClient();

        await client.GetAsync($"/api/events/{uniqueMarker}/seats");

        // Seq ingestion 是非同步批次寫入，輪詢等待而非假設立即可查得（見 tasks.md 6.2）。
        using var httpClient = new HttpClient();
        var found = false;
        for (var attempt = 0; attempt < 20 && !found; attempt++)
        {
            await Task.Delay(500);
            var response = await httpClient.GetAsync($"{_seq.BaseUrl}/api/events?count=50");
            if (!response.IsSuccessStatusCode)
            {
                continue;
            }

            var body = await response.Content.ReadAsStringAsync();
            found = body.Contains(uniqueMarker.ToString(), StringComparison.OrdinalIgnoreCase);
        }

        found.Should().BeTrue("api 服務處理這次特定請求後產生的摘要日誌（含這次獨有的路徑片段）應該最終能在 Seq 查詢到");
    }
}
