import { getTicketHolder, redeemTicket } from '../api/admin'
import { ApiError } from '../api/httpClient'
import type { TicketHolder } from '../types/apiResponses'

// 依核銷端點回應對映出可分辨的結果（design.md 決策 2、決策 4）：
// - 409 已核銷過／404 查無此票／400 且 title 為 InvalidTicketSignature 才判定為簽章無效
//   （比照既有 QueueAdmissionRequired 的 title 判別寫法，不得只憑狀態碼判斷）
// - 409 且 title 為 HolderVerificationRequired 不是失敗，而是「需先比對持票人證件」，必須在
//   一般 409 之前判斷，否則會被誤顯示為已核銷過（real-name-verification design.md 決策 4）
// - 其餘（含其他 400、5xx、網路例外）一律視為系統錯誤，不得歸類為查無此票或簽章無效，
//   也不自動重試（呼叫端決定是否重試）
export type RedemptionOutcome =
  | { kind: 'success' }
  | { kind: 'already-redeemed' }
  | { kind: 'not-found' }
  | { kind: 'invalid-signature' }
  | { kind: 'holder-verification-required' }
  | { kind: 'system-error' }

export async function performRedemption(
  ticketId: string,
  signature: string | null,
  isHolderVerified = false,
): Promise<RedemptionOutcome> {
  try {
    // 未確認時維持與實名功能加入前完全相同的呼叫，非實名流程不受影響（AWU-REDEEM-RN-010）。
    await (isHolderVerified ? redeemTicket(ticketId, signature, true) : redeemTicket(ticketId, signature))
    return { kind: 'success' }
  } catch (error) {
    if (error instanceof ApiError) {
      if (error.status === 409 && error.problem?.title === 'HolderVerificationRequired') {
        return { kind: 'holder-verification-required' }
      }
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

// 查詢持票人只有三種處置：顯示面板、改顯示「已核銷過」（409，例如另一台裝置剛核銷），
// 其餘（網路錯誤、5xx、404、403、缺少實名資料）一律讓操作人員決定重試或放棄，不得自動核銷。
export type HolderLookupOutcome =
  | { kind: 'found'; holder: TicketHolder }
  | { kind: 'already-redeemed' }
  | { kind: 'failed' }

export async function lookupTicketHolder(ticketId: string): Promise<HolderLookupOutcome> {
  try {
    const holder = await getTicketHolder(ticketId)
    // 後端對需實名但缺實名的票回 500；這裡再防一次，避免面板顯示空白姓名讓人誤以為比對過。
    if (!holder.isRealNameRequired || !holder.holderRealName || !holder.holderNationalIdLast4) {
      return { kind: 'failed' }
    }
    return { kind: 'found', holder }
  } catch (error) {
    if (error instanceof ApiError && error.status === 409) {
      return { kind: 'already-redeemed' }
    }
    return { kind: 'failed' }
  }
}
