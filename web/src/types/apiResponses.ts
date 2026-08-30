// 後端 Controller 用 `IActionResult` 回傳，OpenAPI 反推不出 Response schema，
// `api.generated.ts` 只有 Request 型別。這裡手寫對應的 Response DTO，見設計文件決策 4。

export interface EventSummary {
  id: string
  title: string
  startAtUtc: string
  venueId: string
  seatMapId: string
  description: string | null
  posterUrl: string | null
  maxTicketsPerOrder: number | null
  isQueueModeEnabled: boolean
}

// 對應 GET /api/events/{id}/queue/entries/me；status："NotJoined" / "Waiting" / "Admitted" / "Expired"
// （見 rate-limiting-queue design.md 決策 6）。
export interface QueueStatus {
  status: string
  waitingCount: number | null
  queueModeEnabled: boolean
}

export interface EventSeat {
  eventSeatId: string
  zoneCode: string
  seatNumber: string
  status: string
}

export interface TicketType {
  id: string
  zoneCode: string
  price: number
  requiresSeat: boolean
  availableQuantity: number | null
}

export interface MemberProfile {
  id: string
  email: string
  displayName: string
  role: string
  isActive: boolean
}

export interface AuthTokens {
  accessToken: string
  refreshToken: string
}

export interface OrderSummary {
  id: string
  eventId: string
  buyerId: string
  status: string
  heldUntilUtc: string
}

export interface OrderItem {
  id: string
  eventSeatId: string
  unitPrice: number
}

export interface OrderDetail {
  id: string
  eventId: string
  buyerId: string
  status: string
  heldUntilUtc: string
  items: OrderItem[]
}

// 對應買家專屬訂單查詢 DTO；不共用管理端型別，以免誤以為買家端會取得 BuyerId。
export interface MyOrderSummary {
  id: string
  eventId: string
  status: string
  heldUntilUtc: string
}

export interface MyTicket {
  id: string
  status: string
}

export interface MyOrderItem {
  id: string
  eventSeatId: string | null
  ticketTypeId: string | null
  quantity: number
  unitPrice: number
  tickets: MyTicket[]
}

export interface MyOrderDetail {
  id: string
  eventId: string
  status: string
  heldUntilUtc: string
  items: MyOrderItem[]
}

export interface VenueSummary {
  id: string
  name: string
}

export interface SeatMapSummary {
  id: string
  seatCount: number
}

export interface VenueDetail {
  id: string
  name: string
  seatMaps: SeatMapSummary[]
}

export interface SeatDetail {
  id: string
  zoneCode: string
  seatNumber: string
}

export interface SeatMapDetail {
  id: string
  venueId: string
  seats: SeatDetail[]
}

// 對應 Admin 專用的 GET /api/admin/events（不是公開的 GET /api/events）。刻意跟 EventSummary
// 分開定義，不是共用一個型別加選填欄位——這兩個端點的資料來源、授權要求都不同（見
// admin-event-audit-and-sales-status design.md 決策 8），型別分開才不會讓人誤以為公開端點
// 也拿得到這些 Admin 專用欄位。
export interface AdminEventSummary {
  id: string
  title: string
  startAtUtc: string
  venueId: string
  seatMapId: string
  description: string | null
  posterUrl: string | null
  maxTicketsPerOrder: number | null
  createdByMemberId: string | null
  createdByDisplayName: string | null
  createdAtUtc: string | null
  availableSeatCount: number
  heldSeatCount: number
  soldSeatCount: number
}

// 對應 GET /api/admin/events/{eventId}/sales-report（sales-report spec.md）。unclassifiedItemCount
// 等三個欄位直接來自後端查詢結果，前端 SHALL 依 unclassifiedItemCount > 0 判斷是否顯示提示，
// 不得用 totalRevenue 減 byTicketType 加總反推筆數（見 spec.md「依票種明細排除無法歸類票種的已付款項目...」）。
export interface TicketTypeSales {
  ticketTypeId: string
  zoneCode: string
  requiresSeat: boolean
  quantitySold: number
  revenue: number
}

export interface SalesReport {
  eventId: string
  eventTitle: string
  totalRevenue: number
  totalTicketsSold: number
  byTicketType: TicketTypeSales[]
  unclassifiedItemCount: number
  unclassifiedTicketsSold: number
  unclassifiedRevenue: number
}
