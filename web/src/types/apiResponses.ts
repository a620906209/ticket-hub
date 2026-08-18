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
