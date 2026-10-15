import type { SeatMapSummary } from '../types/apiResponses'

export interface SeatMapOption extends SeatMapSummary {
  label: string
}

// 座位圖沒有名稱，後端查詢也沒有 OrderBy，照 API 順序編號會讓同一張座位圖的序號在兩次查詢間改變；
// 依 Id 字串排序後編號，建立活動頁與場館管理頁才會指向同一張（frontend-minor-polish design.md 決策 2）。
// 用 < / > 而非 localeCompare，避免結果受執行環境語系影響。
export function buildSeatMapOptions(seatMaps: readonly SeatMapSummary[]): SeatMapOption[] {
  return [...seatMaps]
    .sort((a, b) => (a.id < b.id ? -1 : a.id > b.id ? 1 : 0))
    .map((seatMap, index) => ({ ...seatMap, label: `座位圖 ${index + 1}（${seatMap.seatCount} 個座位）` }))
}
