import { describe, it, expect, beforeEach } from 'vitest'
import { createPinia, setActivePinia } from 'pinia'
import { usePlacedOrderNoticeStore } from './placedOrderNotice'

describe('placedOrderNotice store', () => {
  beforeEach(() => {
    setActivePinia(createPinia())
  })

  // 橫幅只能顯示一次：第二次取出若仍有值，再次進入活動列表會重複提示（BW-LOBBY-NOTICE-003）。
  it('consumePlacedOrder_CalledTwice_ReturnsIdOnlyOnce', () => {
    const store = usePlacedOrderNoticeStore()
    store.setPlacedOrder('order-123')

    expect(store.consumePlacedOrder()).toBe('order-123')
    expect(store.consumePlacedOrder()).toBeNull()
    expect(store.placedOrderId).toBeNull()
  })

  it('clearPlacedOrder_AfterSet_LeavesNothingToConsume', () => {
    const store = usePlacedOrderNoticeStore()
    store.setPlacedOrder('order-123')

    store.clearPlacedOrder()

    expect(store.consumePlacedOrder()).toBeNull()
  })

  it('consumePlacedOrder_WhenNothingPlaced_ReturnsNull', () => {
    const store = usePlacedOrderNoticeStore()

    expect(store.consumePlacedOrder()).toBeNull()
  })
})
