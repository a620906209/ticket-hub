namespace ProjectC.Application.Orders.PlaceOrder;

public sealed record PlaceOrderRequest(IReadOnlyList<PlaceOrderSelectionRequest> Selections);

// EventSeatId 可為 null：純計數票種不指定座位。Quantity 預設 1——既有買家端下單流程從未帶過這個欄位，
// 缺欄位時套用這裡宣告的預設值，讓舊請求（純座位選購）行為與本次變更前完全一致
// （design.md 決策 4 API 相容性段落）。
public sealed record PlaceOrderSelectionRequest(Guid? EventSeatId, Guid TicketTypeId, int Quantity = 1);
