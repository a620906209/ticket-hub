import { describe, expect, it } from 'vitest'
import { buildSeatMapOptions } from './seatMapLabel'

const first = { id: '1a000000-0000-0000-0000-000000000000', seatCount: 40 }
const second = { id: '7c000000-0000-0000-0000-000000000000', seatCount: 120 }
const third = { id: 'e3000000-0000-0000-0000-000000000000', seatCount: 8 }

describe('buildSeatMapOptions', () => {
  // [AWU-SEATMAP-LABEL-001] 後端查詢沒有 OrderBy，序號若跟著回傳順序走，兩頁的「座位圖 2」可能是不同張。
  // 座位數刻意不隨 Id 單調增減，依座位數排序（遞增或遞減）都會得到不同順序
  it('[AWU-SEATMAP-LABEL-001] buildSeatMapOptions_WithDifferentInputOrders_ReturnsSameNumbering', () => {
    const fromOneOrder = buildSeatMapOptions([third, first, second])
    const fromAnotherOrder = buildSeatMapOptions([second, third, first])

    expect(fromOneOrder).toEqual(fromAnotherOrder)
    expect(fromOneOrder.map((option) => [option.id, option.label])).toEqual([
      [first.id, '座位圖 1（40 個座位）'],
      [second.id, '座位圖 2（120 個座位）'],
      [third.id, '座位圖 3（8 個座位）'],
    ])
  })

  // [AWU-SEATMAP-LABEL-001] 排序須依字元碼（< / >），不受語系影響；localeCompare 會把小寫 a 排在大寫 B 前面。
  it('[AWU-SEATMAP-LABEL-001] buildSeatMapOptions_WithMixedCaseIds_SortsByCodeUnitNotLocale', () => {
    const lowerCase = { id: 'a0000000-0000-0000-0000-000000000000', seatCount: 10 }
    const upperCase = { id: 'B0000000-0000-0000-0000-000000000000', seatCount: 20 }

    expect(buildSeatMapOptions([lowerCase, upperCase]).map((option) => option.id)).toEqual([upperCase.id, lowerCase.id])
  })

  it('[AWU-SEATMAP-LABEL-001] buildSeatMapOptions_WithEmptyList_ReturnsEmpty', () => {
    expect(buildSeatMapOptions([])).toEqual([])
  })
})
