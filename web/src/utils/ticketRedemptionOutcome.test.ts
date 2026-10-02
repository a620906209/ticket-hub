import { beforeEach, describe, expect, it, vi } from 'vitest'
import * as adminApi from '../api/admin'
import { ApiError } from '../api/httpClient'
import { lookupTicketHolder, performRedemption } from './ticketRedemptionOutcome'
import { parseTicketIdFromQrContent } from './ticketRedemptionParsing'

vi.mock('../api/admin')

const TICKET_ID = '3fa85f64-5717-4562-b3fc-2c963f66afa6'

beforeEach(() => {
  vi.mocked(adminApi.redeemTicket).mockReset()
})

describe('performRedemption', () => {
  // 對應 AC: ADMIN-REDEEM-SCAN-SUCCESS
  it('[ADMIN-REDEEM-SCAN-SUCCESS] 成功（204）回傳 success', async () => {
    vi.mocked(adminApi.redeemTicket).mockResolvedValue(undefined)

    const outcome = await performRedemption(TICKET_ID, 'sig')

    expect(outcome).toEqual({ kind: 'success' })
  })

  // 對應 AC: ADMIN-REDEEM-SCAN-CONFLICT
  it('[ADMIN-REDEEM-SCAN-CONFLICT] 409 回傳 already-redeemed', async () => {
    vi.mocked(adminApi.redeemTicket).mockRejectedValue(new ApiError(409, { status: 409, title: 'Conflict' }))

    const outcome = await performRedemption(TICKET_ID, 'sig')

    expect(outcome).toEqual({ kind: 'already-redeemed' })
  })

  // 對應 AC: ADMIN-REDEEM-SCAN-NOT-FOUND
  it('[ADMIN-REDEEM-SCAN-NOT-FOUND] 404 回傳 not-found', async () => {
    vi.mocked(adminApi.redeemTicket).mockRejectedValue(new ApiError(404, { status: 404, title: 'NotFound' }))

    const outcome = await performRedemption(TICKET_ID, 'sig')

    expect(outcome).toEqual({ kind: 'not-found' })
  })

  // 對應 AC: ADMIN-REDEEM-SCAN-INVALID-SIGNATURE
  it('[ADMIN-REDEEM-SCAN-INVALID-SIGNATURE] 400 且 title 為 InvalidTicketSignature 回傳 invalid-signature', async () => {
    vi.mocked(adminApi.redeemTicket).mockRejectedValue(
      new ApiError(400, { status: 400, title: 'InvalidTicketSignature' }),
    )

    const outcome = await performRedemption(TICKET_ID, 'sig')

    expect(outcome).toEqual({ kind: 'invalid-signature' })
  })

  // 對應 AC: ADMIN-REDEEM-SCAN-SYSTEM-ERROR（其他 400 不得歸類為簽章無效）
  it('[ADMIN-REDEEM-SCAN-INVALID-SIGNATURE] 400 但 title 不是 InvalidTicketSignature 時回傳 system-error', async () => {
    vi.mocked(adminApi.redeemTicket).mockRejectedValue(new ApiError(400, { status: 400, title: 'Validation' }))

    const outcome = await performRedemption(TICKET_ID, 'sig')

    expect(outcome).toEqual({ kind: 'system-error' })
  })

  // 對應 AC: ADMIN-REDEEM-SCAN-SYSTEM-ERROR（5xx 不得歸類為查無此票）
  it('[ADMIN-REDEEM-SCAN-SYSTEM-ERROR] 5xx 回傳 system-error，不自動重試', async () => {
    vi.mocked(adminApi.redeemTicket).mockRejectedValue(new ApiError(500, { status: 500, title: 'InternalError' }))

    const outcome = await performRedemption(TICKET_ID, 'sig')

    expect(outcome).toEqual({ kind: 'system-error' })
    expect(adminApi.redeemTicket).toHaveBeenCalledTimes(1)
  })

  // 對應 AC: ADMIN-REDEEM-SCAN-SYSTEM-ERROR（網路例外，非 ApiError）
  it('[ADMIN-REDEEM-SCAN-SYSTEM-ERROR] 網路例外（非 ApiError）回傳 system-error，不自動重試', async () => {
    vi.mocked(adminApi.redeemTicket).mockRejectedValue(new TypeError('Failed to fetch'))

    const outcome = await performRedemption(TICKET_ID, 'sig')

    expect(outcome).toEqual({ kind: 'system-error' })
    expect(adminApi.redeemTicket).toHaveBeenCalledTimes(1)
  })

  // 對應 AC: ADMIN-REDEEM-SCAN-DISPATCH（解析出的 ticketId／signature 原封不動送入呼叫，未被中途轉換或遺漏）
  it('[ADMIN-REDEEM-SCAN-DISPATCH] 掃描字串解析出的 ticketId 與 signature 恰好是 redeemTicket 收到的參數', async () => {
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

// 手動輸入路徑固定送出 signature: null（design.md Decision 4 開放給 Organizer 成員、不驗簽章的路徑）；
// 前端確實送出 null，後端跨租戶 404（RDM-AUTHZ-004 手動路徑）才會涵蓋到實際走的這條路徑。
describe('performRedemption 手動輸入路徑（signature 為 null）', () => {
  it.each([
    ['ADMIN-REDEEM-MANUAL-SUCCESS', undefined, 'success'],
    ['ADMIN-REDEEM-MANUAL-CONFLICT', new ApiError(409, { status: 409, title: 'Conflict' }), 'already-redeemed'],
    ['ADMIN-REDEEM-MANUAL-NOT-FOUND', new ApiError(404, { status: 404, title: 'NotFound' }), 'not-found'],
    ['ADMIN-REDEEM-MANUAL-SYSTEM-ERROR', new ApiError(500, { status: 500, title: 'InternalError' }), 'system-error'],
  ] as const)('[%s] redeemTicket 以 (id, null) 被呼叫一次', async (_scenario, rejection, expectedKind) => {
    if (rejection) {
      vi.mocked(adminApi.redeemTicket).mockRejectedValue(rejection)
    } else {
      vi.mocked(adminApi.redeemTicket).mockResolvedValue(undefined)
    }

    const outcome = await performRedemption(TICKET_ID, null)

    expect(outcome).toEqual({ kind: expectedKind })
    expect(adminApi.redeemTicket).toHaveBeenCalledTimes(1)
    expect(adminApi.redeemTicket).toHaveBeenCalledWith(TICKET_ID, null)
  })
})

describe('performRedemption 持票人確認（real-name-verification）', () => {
  it('409 且 title 為 HolderVerificationRequired 回傳 holder-verification-required，不得誤判為已核銷過', async () => {
    vi.mocked(adminApi.redeemTicket).mockRejectedValue(
      new ApiError(409, { status: 409, title: 'HolderVerificationRequired' }),
    )

    const outcome = await performRedemption(TICKET_ID, 'sig')

    expect(outcome).toEqual({ kind: 'holder-verification-required' })
  })

  it('確認持票人後以同一 ticketId、簽章帶 isHolderVerified = true 呼叫核銷', async () => {
    vi.mocked(adminApi.redeemTicket).mockResolvedValue(undefined)

    const outcome = await performRedemption(TICKET_ID, 'sig', true)

    expect(outcome).toEqual({ kind: 'success' })
    expect(adminApi.redeemTicket).toHaveBeenCalledWith(TICKET_ID, 'sig', true)
  })
})

describe('lookupTicketHolder', () => {
  const holder = {
    ticketId: TICKET_ID,
    ticketStatus: 'Issued',
    isRealNameRequired: true,
    holderRealName: '王小明',
    holderNationalIdLast4: '1234',
  }

  beforeEach(() => {
    vi.mocked(adminApi.getTicketHolder).mockReset()
  })

  it('查詢成功回傳 found 與持票人資料', async () => {
    vi.mocked(adminApi.getTicketHolder).mockResolvedValue(holder)

    expect(await lookupTicketHolder(TICKET_ID)).toEqual({ kind: 'found', holder })
    expect(adminApi.getTicketHolder).toHaveBeenCalledWith(TICKET_ID)
  })

  it('409 回傳 already-redeemed（例如另一台裝置剛核銷）', async () => {
    vi.mocked(adminApi.getTicketHolder).mockRejectedValue(new ApiError(409, { status: 409, title: 'Conflict' }))

    expect(await lookupTicketHolder(TICKET_ID)).toEqual({ kind: 'already-redeemed' })
  })

  it.each([
    ['網路例外', new TypeError('Failed to fetch')],
    ['500', new ApiError(500, null)],
    ['404', new ApiError(404, { status: 404 })],
    ['403', new ApiError(403, { status: 403 })],
  ])('%s 回傳 failed，交由操作人員重試或放棄', async (_label, error) => {
    vi.mocked(adminApi.getTicketHolder).mockRejectedValue(error)

    expect(await lookupTicketHolder(TICKET_ID)).toEqual({ kind: 'failed' })
  })

  it.each([
    ['姓名為 null', { holderRealName: null }],
    ['末四碼為 null', { holderNationalIdLast4: null }],
    ['活動不需實名', { isRealNameRequired: false }],
  ])('回應%s時回傳 failed，不顯示空白資料的面板', async (_label, overrides) => {
    vi.mocked(adminApi.getTicketHolder).mockResolvedValue({ ...holder, ...overrides })

    expect(await lookupTicketHolder(TICKET_ID)).toEqual({ kind: 'failed' })
  })
})
