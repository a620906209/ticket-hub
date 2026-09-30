import { describe, expect, it } from 'vitest'
import {
  getOrderStatusLabel,
  getOrderStatusTagType,
  getTicketStatusLabel,
  getTicketStatusTagType,
} from './statusLabels'

// 訂單／票券狀態對照集中在一處（order-pending-actions design.md 決策 3、5）：
// 對照表改錯會讓買家與後台同時看到錯誤的狀態文字或顏色，所以逐一釘住。
describe('statusLabels 狀態中文標籤與 tag type 對照', () => {
  it.each([
    ['Pending', '待付款', 'warning'],
    ['Paid', '已付款', 'success'],
    ['Cancelled', '已取消', 'info'],
    ['Expired', '已逾時', 'info'],
  ])('訂單狀態 %s 顯示「%s」、tag type 為 %s', (status, label, tagType) => {
    expect(getOrderStatusLabel(status)).toBe(label)
    expect(getOrderStatusTagType(status)).toBe(tagType)
  })

  it.each([
    ['Issued', '已出票', 'success'],
    ['Redeemed', '已核銷', 'info'],
  ])('票券狀態 %s 顯示「%s」、tag type 為 %s', (status, label, tagType) => {
    expect(getTicketStatusLabel(status)).toBe(label)
    expect(getTicketStatusTagType(status)).toBe(tagType)
  })

  // BW-MYORDER-DISPLAY-002 的單元層：後端新增列舉值時畫面仍可辨識，不顯示空白或丟例外。
  // Voided 刻意不列入對照（buyer-web-ui 規定不為 Voided 實作顯示邏輯），應走未知值 fallback。
  it.each(['Refunded', 'Voided', 'toString'])('未知值 %s 原樣回傳且 tag type 為 info', (status) => {
    expect(getOrderStatusLabel(status)).toBe(status)
    expect(getOrderStatusTagType(status)).toBe('info')
    expect(getTicketStatusLabel(status)).toBe(status)
    expect(getTicketStatusTagType(status)).toBe('info')
  })
})
