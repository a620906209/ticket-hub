import { describe, expect, it } from 'vitest'
import { parseTicketIdFromManualInput, parseTicketIdFromQrContent } from './ticketRedemptionParsing'

const VALID_GUID = '3fa85f64-5717-4562-b3fc-2c963f66afa6'

describe('parseTicketIdFromQrContent', () => {
  // 對應 AC: ADMIN-REDEEM-SCAN-DISPATCH（合法格式可解析出 ticketId／signature）
  it('合法格式（GUID.signature）回傳可辨識結果並拆出 ticketId 與 signature', () => {
    const result = parseTicketIdFromQrContent(`${VALID_GUID}.someSignature`)

    expect(result).toEqual({ recognized: true, ticketId: VALID_GUID, signature: 'someSignature' })
  })

  // 對應 AC: ADMIN-REDEEM-SCAN-UNRECOGNIZED
  it('缺分隔符時回傳無法辨識', () => {
    expect(parseTicketIdFromQrContent(`${VALID_GUID}someSignature`)).toEqual({ recognized: false })
  })

  // 對應 AC: ADMIN-REDEEM-SCAN-UNRECOGNIZED（design.md 決策 5「恰好一個 . 分隔符」）
  it('多個 . 分隔符時回傳無法辨識，不誤判為可解析', () => {
    expect(parseTicketIdFromQrContent(`${VALID_GUID}.sig.extra`)).toEqual({ recognized: false })
  })

  // 對應 AC: ADMIN-REDEEM-SCAN-UNRECOGNIZED
  it('前段非合法 GUID 格式時回傳無法辨識', () => {
    expect(parseTicketIdFromQrContent('not-a-guid.someSignature')).toEqual({ recognized: false })
  })

  // 對應 AC: ADMIN-REDEEM-SCAN-UNRECOGNIZED
  it('後段（簽章）為空字串時回傳無法辨識', () => {
    expect(parseTicketIdFromQrContent(`${VALID_GUID}.`)).toEqual({ recognized: false })
  })
})

describe('parseTicketIdFromManualInput', () => {
  // 對應 AC: ADMIN-REDEEM-MANUAL-INVALID-FORMAT
  it('合法 GUID（前後可含空白）回傳可用結果並 trim', () => {
    expect(parseTicketIdFromManualInput(`  ${VALID_GUID}  `)).toEqual({ valid: true, ticketId: VALID_GUID })
  })

  // 對應 AC: ADMIN-REDEEM-MANUAL-INVALID-FORMAT
  it('帶 . 的內容視為格式不正確', () => {
    expect(parseTicketIdFromManualInput(`${VALID_GUID}.signature`)).toEqual({ valid: false })
  })

  // 對應 AC: ADMIN-REDEEM-MANUAL-INVALID-FORMAT
  it('非 GUID 格式視為格式不正確', () => {
    expect(parseTicketIdFromManualInput('not-a-guid')).toEqual({ valid: false })
  })
})
