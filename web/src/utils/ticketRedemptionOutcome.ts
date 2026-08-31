import { redeemTicket } from '../api/admin'
import { ApiError } from '../api/httpClient'

// 依核銷端點回應對映出可分辨的結果（design.md 決策 2、決策 4）：
// - 409 已核銷過／404 查無此票／400 且 title 為 InvalidTicketSignature 才判定為簽章無效
//   （比照既有 QueueAdmissionRequired 的 title 判別寫法，不得只憑狀態碼判斷）
// - 其餘（含其他 400、5xx、網路例外）一律視為系統錯誤，不得歸類為查無此票或簽章無效，
//   也不自動重試（呼叫端決定是否重試）
export type RedemptionOutcome =
  | { kind: 'success' }
  | { kind: 'already-redeemed' }
  | { kind: 'not-found' }
  | { kind: 'invalid-signature' }
  | { kind: 'system-error' }

export async function performRedemption(ticketId: string, signature: string | null): Promise<RedemptionOutcome> {
  try {
    await redeemTicket(ticketId, signature)
    return { kind: 'success' }
  } catch (error) {
    if (error instanceof ApiError) {
      if (error.status === 409) {
        return { kind: 'already-redeemed' }
      }
      if (error.status === 404) {
        return { kind: 'not-found' }
      }
      if (error.status === 400 && error.problem?.title === 'InvalidTicketSignature') {
        return { kind: 'invalid-signature' }
      }
    }
    return { kind: 'system-error' }
  }
}
