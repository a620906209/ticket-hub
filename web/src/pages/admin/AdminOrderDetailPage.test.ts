import { describe, it, expect, vi, beforeEach } from 'vitest'
import { mount, flushPromises } from '@vue/test-utils'
import ElementPlus from 'element-plus'
import AdminOrderDetailPage from './AdminOrderDetailPage.vue'
import * as adminApi from '../../api/admin'

vi.mock('../../api/admin')

vi.mock('vue-router', () => ({
  useRoute: () => ({ params: { id: 'order-1' } }),
}))

function mountPage() {
  return mount(AdminOrderDetailPage, { global: { plugins: [ElementPlus] } })
}

describe('AdminOrderDetailPage', () => {
  beforeEach(() => {
    vi.mocked(adminApi.getAdminOrderById).mockReset()
  })

  // 後端 OrderItemDto 的 EventSeatId 可為 null（純計數票種），且一個項目可含多張（Quantity）。
  // 若只顯示座位 Id 與單價，Admin 會把一筆 5 張的計數項目誤讀成 1 張、座位欄空白像資料遺失。
  it('混合座位項目與計數項目：計數項目標示為計數票並顯示數量，單價以貨幣格式呈現', async () => {
    vi.mocked(adminApi.getAdminOrderById).mockResolvedValue({
      id: 'order-1',
      eventId: 'event-1',
      buyerId: 'buyer-1',
      status: 'Paid',
      heldUntilUtc: '2026-09-30T10:00:00Z',
      items: [
        { id: 'item-1', eventSeatId: 'seat-1', ticketTypeId: 'tt-seat', quantity: 1, unitPrice: 1500 },
        { id: 'item-2', eventSeatId: null, ticketTypeId: 'tt-count', quantity: 5, unitPrice: 800 },
      ],
    })

    const wrapper = mountPage()
    await flushPromises()

    const rows = wrapper.findAll('.el-table__body tr')
    expect(rows).toHaveLength(2)
    expect(rows[0].text()).toContain('seat-1')
    expect(rows[0].text()).toContain('NT$1,500')
    expect(rows[1].text()).toContain('計數票')
    expect(rows[1].text()).toContain('5')
    expect(rows[1].text()).toContain('NT$800')
  })
})
