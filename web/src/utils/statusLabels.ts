export type StatusTagType = 'success' | 'warning' | 'info'

interface StatusDisplay {
  label: string
  tagType: StatusTagType
}

const ORDER_STATUS_DISPLAY: Record<string, StatusDisplay> = {
  Pending: { label: '待付款', tagType: 'warning' },
  Paid: { label: '已付款', tagType: 'success' },
  Cancelled: { label: '已取消', tagType: 'info' },
  Expired: { label: '已逾時', tagType: 'info' },
}

// Voided 刻意不列入：buyer-web-ui 規定不為 Voided 實作顯示邏輯，走未知值 fallback 即原字串。
const TICKET_STATUS_DISPLAY: Record<string, StatusDisplay> = {
  Issued: { label: '已出票', tagType: 'success' },
  Redeemed: { label: '已核銷', tagType: 'info' },
}

// 未知值回傳原字串而非丟例外或空白，後端新增列舉值時畫面仍可辨識（order-pending-actions design.md 決策 3）。
function resolveStatusDisplay(table: Record<string, StatusDisplay>, status: string): StatusDisplay {
  return Object.hasOwn(table, status) ? table[status] : { label: status, tagType: 'info' }
}

export function getOrderStatusLabel(status: string): string {
  return resolveStatusDisplay(ORDER_STATUS_DISPLAY, status).label
}

export function getOrderStatusTagType(status: string): StatusTagType {
  return resolveStatusDisplay(ORDER_STATUS_DISPLAY, status).tagType
}

export function getTicketStatusLabel(status: string): string {
  return resolveStatusDisplay(TICKET_STATUS_DISPLAY, status).label
}

export function getTicketStatusTagType(status: string): StatusTagType {
  return resolveStatusDisplay(TICKET_STATUS_DISPLAY, status).tagType
}
