namespace ProjectC.WebApi.Tests.TestSupport;

/// <summary>對應後台管理 API 建立成功時回傳的 `{ id }` Body（見 ticketing-event-management design.md 決策 4）。</summary>
public sealed record CreatedResponse(Guid Id);
