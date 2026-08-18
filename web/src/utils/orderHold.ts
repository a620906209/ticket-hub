// 下單 API 成功回應只有訂單 Id，沒有持有到期時間欄位；10 分鐘對照後端
// CreateOrderHandler.HoldDuration 常數寫死在這裡（查證後發現的落差，見設計文件）。
export const ORDER_HOLD_MINUTES = 10

export function computeHeldUntilUtc(fromNow: Date = new Date()): string {
  return new Date(fromNow.getTime() + ORDER_HOLD_MINUTES * 60_000).toISOString()
}
