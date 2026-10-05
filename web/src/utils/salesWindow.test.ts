import { describe, expect, it } from 'vitest'
import { getEffectiveSalesEndAtUtc, getSalesStatus } from './salesWindow'

// 邊界必須與後端左閉右開規則一致：等於開賣時間即可購買，等於停售時間即停售。
// 若把 < 改成 <=、或忽略 salesEndAtUtc，以下測試須失敗。
describe('getSalesStatus', () => {
  const salesStartAtUtc = '2026-11-01T10:00:00.000Z'
  const salesEndAtUtc = '2026-11-30T10:00:00.000Z'
  const startAtUtc = '2026-12-31T20:00:00.000Z'
  const ms = (iso: string) => new Date(iso).getTime()

  it('[BW-SALES-001] 早於開賣時間為 NotOpen', () => {
    expect(getSalesStatus({ startAtUtc, salesStartAtUtc, salesEndAtUtc }, ms(salesStartAtUtc) - 1)).toBe('NotOpen')
  })

  it('[BW-SALES-001] 等於開賣時間為 Open（左閉）', () => {
    expect(getSalesStatus({ startAtUtc, salesStartAtUtc, salesEndAtUtc }, ms(salesStartAtUtc))).toBe('Open')
  })

  it('[BW-SALES-001] salesEndAtUtc 為 null 時，等於 startAtUtc 為 Closed（右開）', () => {
    expect(getSalesStatus({ startAtUtc, salesStartAtUtc, salesEndAtUtc: null }, ms(startAtUtc))).toBe('Closed')
    expect(getSalesStatus({ startAtUtc, salesStartAtUtc, salesEndAtUtc: null }, ms(startAtUtc) - 1)).toBe('Open')
  })

  it('[BW-SALES-001] salesEndAtUtc 有值時，等於 salesEndAtUtc 為 Closed（不等到活動開始）', () => {
    expect(getSalesStatus({ startAtUtc, salesStartAtUtc, salesEndAtUtc }, ms(salesEndAtUtc))).toBe('Closed')
    expect(getSalesStatus({ startAtUtc, salesStartAtUtc, salesEndAtUtc }, ms(salesEndAtUtc) - 1)).toBe('Open')
  })

  it('[BW-SALES-001] 開賣為 null 且未到停售為 Open（沒有下界）', () => {
    expect(getSalesStatus({ startAtUtc, salesStartAtUtc: null, salesEndAtUtc: null }, 0)).toBe('Open')
  })

  it('[BW-SALES-001] salesStartAtUtc 為 null 與 undefined 時結果相同', () => {
    const nowMs = ms(startAtUtc) - 1
    const withNull = getSalesStatus({ startAtUtc, salesStartAtUtc: null, salesEndAtUtc: null }, nowMs)
    const withUndefined = getSalesStatus({ startAtUtc, salesStartAtUtc: undefined, salesEndAtUtc: null }, nowMs)
    expect(withNull).toBe('Open')
    expect(withUndefined).toBe(withNull)
  })

  // nowMs 刻意等於 startAtUtc：若 undefined 被當成有值而 new Date(undefined)，比較恆為 false 會回傳 Open。
  it('[BW-SALES-001] salesEndAtUtc 為 null 與 undefined 時結果相同', () => {
    const nowMs = ms(startAtUtc)
    const withNull = getSalesStatus({ startAtUtc, salesStartAtUtc: null, salesEndAtUtc: null }, nowMs)
    const withUndefined = getSalesStatus({ startAtUtc, salesStartAtUtc: null, salesEndAtUtc: undefined }, nowMs)
    expect(withNull).toBe('Closed')
    expect(withUndefined).toBe(withNull)
  })
})

describe('getEffectiveSalesEndAtUtc', () => {
  const startAtUtc = '2026-12-31T20:00:00.000Z'

  it('[BW-SALES-001] salesStartAtUtc 為 null 與 undefined 時結果相同', () => {
    const salesEndAtUtc = '2026-11-30T10:00:00.000Z'
    const withNull = getEffectiveSalesEndAtUtc({ startAtUtc, salesStartAtUtc: null, salesEndAtUtc })
    const withUndefined = getEffectiveSalesEndAtUtc({ startAtUtc, salesStartAtUtc: undefined, salesEndAtUtc })
    expect(withNull).toBe(salesEndAtUtc)
    expect(withUndefined).toBe(withNull)
  })

  it('[BW-SALES-001] salesEndAtUtc 為 null 與 undefined 時結果相同（皆為 startAtUtc）', () => {
    const withNull = getEffectiveSalesEndAtUtc({ startAtUtc, salesEndAtUtc: null })
    const withUndefined = getEffectiveSalesEndAtUtc({ startAtUtc, salesEndAtUtc: undefined })
    expect(withNull).toBe(startAtUtc)
    expect(withUndefined).toBe(withNull)
  })
})
