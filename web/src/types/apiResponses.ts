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
