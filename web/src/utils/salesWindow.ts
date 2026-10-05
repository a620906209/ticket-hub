export type SalesStatus = 'NotOpen' | 'Open' | 'Closed'

export interface SalesWindowSource {
  startAtUtc: string
  salesStartAtUtc?: string | null
  salesEndAtUtc?: string | null
}

// 與後端販售期間規則一致：左閉右開 [salesStartAtUtc, salesEndAtUtc ?? startAtUtc)
// （event-sales-window-web-ui design.md 決策 1）。以 == null 同時涵蓋 null 與欄位缺漏，
// 避免 new Date(undefined) 變成 NaN 使比較恆為 false。
export function getEffectiveSalesEndAtUtc(event: SalesWindowSource): string {
  return event.salesEndAtUtc == null ? event.startAtUtc : event.salesEndAtUtc
}

export function getSalesStatus(event: SalesWindowSource, nowMs: number): SalesStatus {
  if (event.salesStartAtUtc != null && nowMs < new Date(event.salesStartAtUtc).getTime()) {
    return 'NotOpen'
  }
  if (nowMs >= new Date(getEffectiveSalesEndAtUtc(event)).getTime()) {
    return 'Closed'
  }
  return 'Open'
}
