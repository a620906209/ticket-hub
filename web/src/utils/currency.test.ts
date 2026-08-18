import { describe, expect, it } from 'vitest'
import { formatCurrency } from './currency'

describe('formatCurrency', () => {
  it('整數金額加上 NT$ 字首，不補 .00', () => {
    expect(formatCurrency(1000)).toBe('NT$1,000')
  })

  it('一位小數不強制補成兩位', () => {
    expect(formatCurrency(1000.5)).toBe('NT$1,000.5')
  })

  it('兩位小數完整顯示', () => {
    expect(formatCurrency(1000.55)).toBe('NT$1,000.55')
  })

  it('金額為 0 時顯示 NT$0', () => {
    expect(formatCurrency(0)).toBe('NT$0')
  })

  it('金額跨多組千分位時逐組加上逗號', () => {
    expect(formatCurrency(1000000)).toBe('NT$1,000,000')
  })
})
