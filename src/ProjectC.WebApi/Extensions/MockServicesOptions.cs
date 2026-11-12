namespace ProjectC.WebApi.Extensions;

// 缺漏時視為 false：Production 綁定 Mock 付款／Email 預設拒絕啟動，須明示放行
// （mock-service-production-guard design.md 決策 3、4）。
public sealed class MockServicesOptions
{
    public const string SectionName = "Mocks";

    public bool AllowInProduction { get; set; }
}
