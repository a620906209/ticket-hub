namespace ProjectC.Application.Tickets.CreateTicketType;

// RequiresSeat 預設 true：既有客戶端（本次未改動的 admin-web-ui）送出的舊格式 JSON 沒有這個欄位，
// System.Text.Json 用建構子反序列化時，缺欄位會套用這裡宣告的預設值，讓舊請求維持原本的座位模式行為
// 不受影響（design.md 決策 1 API 相容性段落）。
public sealed record CreateTicketTypeRequest(string ZoneCode, decimal Price, bool RequiresSeat = true, int? AvailableQuantity = null);
