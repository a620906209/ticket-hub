import { beforeEach, describe, expect, it, vi } from 'vitest'
import * as adminApi from '../api/admin'
import { ApiError } from '../api/httpClient'
import { performRedemption } from './ticketRedemptionOutcome'
import { parseTicketIdFromQrContent } from './ticketRedemptionParsing'

vi.mock('../api/admin')

const TICKET_ID = '3fa85f64-5717-4562-b3fc-2c963f66afa6'

beforeEach(() => {
  vi.mocked(adminApi.redeemTicket).mockReset()
})

describe('performRedemption', () => {
  // 對應 AC: ADMIN-REDEEM-SCAN-SUCCESS
  it('成功（204）回傳 success', async () => {
    vi.mocked(adminApi.redeemTicket).mockResolvedValue(undefined)

    const outcome = await performRedemption(TICKET_ID, 'sig')

    expect(outcome).toEqual({ kind: 'success' })
  })

  // 對應 AC: ADMIN-REDEEM-SCAN-CONFLICT
  it('409 回傳 already-redeemed', async () => {
    vi.mocked(adminApi.redeemTicket).mockRejectedValue(new ApiError(409, { status: 409, title: 'Conflict' }))

    const outcome = await performRedemption(TICKET_ID, 'sig')

    expect(outcome).toEqual({ kind: 'already-redeemed' })
  })

  // 對應 AC: ADMIN-REDEEM-SCAN-NOT-FOUND
  it('404 回傳 not-found', async () => {
    vi.mocked(adminApi.redeemTicket).mockRejectedValue(new ApiError(404, { status: 404, title: 'NotFound' }))

    const outcome = await performRedemption(TICKET_ID, 'sig')

    expect(outcome).toEqual({ kind: 'not-found' })
  })

  // 對應 AC: ADMIN-REDEEM-SCAN-INVALID-SIGNATURE
  it('400 且 title 為 InvalidTicketSignature 回傳 invalid-signature', async () => {
    vi.mocked(adminApi.redeemTicket).mockRejectedValue(
      new ApiError(400, { status: 400, title: 'InvalidTicketSignature' }),
    )

    const outcome = await performRedemption(TICKET_ID, 'sig')

    expect(outcome).toEqual({ kind: 'invalid-signature' })
  })

  // 對應 AC: ADMIN-REDEEM-SCAN-SYSTEM-ERROR（其他 400 不得歸類為簽章無效）
  it('400 但 title 不是 InvalidTicketSignature 時回傳 system-error', async () => {
    vi.mocked(adminApi.redeemTicket).mockRejectedValue(new ApiError(400, { status: 400, title: 'Validation' }))

    const outcome = await performRedemption(TICKET_ID, 'sig')

    expect(outcome).toEqual({ kind: 'system-error' })
  })

  // 對應 AC: ADMIN-REDEEM-SCAN-SYSTEM-ERROR（5xx 不得歸類為查無此票）
  it('5xx 回傳 system-error', async () => {
    vi.mocked(adminApi.redeemTicket).mockRejectedValue(new ApiError(500, { status: 500, title: 'InternalError' }))

    const outcome = await performRedemption(TICKET_ID, 'sig')

    expect(outcome).toEqual({ kind: 'system-error' })
  })

  // 對應 AC: ADMIN-REDEEM-SCAN-SYSTEM-ERROR（網路例外，非 ApiError）
  it('網路例外（非 ApiError）回傳 system-error', async () => {
    vi.mocked(adminApi.redeemTicket).mockRejectedValue(new TypeError('Failed to fetch'))

    const outcome = await performRedemption(TICKET_ID, 'sig')

    expect(outcome).toEqual({ kind: 'system-error' })
  })

  // 對應 AC: ADMIN-REDEEM-SCAN-DISPATCH（解析出的 ticketId／signature 原封不動送入呼叫，未被中途轉換或遺漏）
  it('掃描字串解析出的 ticketId 與 signature 恰好是 redeemTicket 收到的參數', async () => {
    vi.mocked(adminApi.redeemTicket).mockResolvedValue(undefined)
    const parsed = parseTicketIdFromQrContent(`${TICKET_ID}.the-signature`)
    if (!parsed.recognized) {
      throw new Error('測試前提：這個掃描字串應該要能被解析')
    }

    await performRedemption(parsed.ticketId, parsed.signature)

    expect(adminApi.redeemTicket).toHaveBeenCalledWith(TICKET_ID, 'the-signature')
    expect(adminApi.redeemTicket).toHaveBeenCalledTimes(1)
  })
})
