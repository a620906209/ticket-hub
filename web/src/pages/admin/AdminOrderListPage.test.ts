import { beforeEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount } from '@vue/test-utils'
import ElementPlus from 'element-plus'
import AdminOrderListPage from './AdminOrderListPage.vue'
import * as adminApi from '../../api/admin'

vi.mock('../../api/admin')

function mountPage() {
  return mount(AdminOrderListPage, { global: { plugins: [ElementPlus], stubs: { RouterLink: true } } })
}

describe('AdminOrderListPage 後台訂單列表', () => {
  beforeEach(() => {
    vi.mocked(adminApi.getAdminOrders).mockReset()
    vi.mocked(adminApi.getAdminOrders).mockResolvedValue([
      { id: 'pending-order', eventId: 'event-1', buyerId: 'buyer-1', status: 'Pending', heldUntilUtc: '2026-09-30T10:00:00Z' },
      { id: 'paid-order', eventId: 'event-2', buyerId: 'buyer-2', status: 'Paid', heldUntilUtc: '2026-09-30T11:00:00Z' },
    ])
  })

  it('[AWU-ORDER-LIST-001] 訂單狀態以中文標籤顯示，Pending／Paid 分別為警示色／成功色', async () => {
    const wrapper = mountPage()
    await flushPromises()

    const rows = wrapper.findAll('.el-table__body tr')
    expect(rows).toHaveLength(2)
    const pendingTag = rows[0].find('.el-tag')
    const paidTag = rows[1].find('.el-tag')
    expect(pendingTag.text()).toBe('待付款')
    expect(pendingTag.classes()).toContain('el-tag--warning')
    expect(paidTag.text()).toBe('已付款')
    expect(paidTag.classes()).toContain('el-tag--success')
  })

  // HeldUntilUtc 是建立訂單當下的原始值、不因終態改寫，對 Paid 訂單顯示會誤導成仍在保留中。
  it('[AWU-ORDER-HOLD-001] 持有到期時間只對 Pending 訂單顯示', async () => {
    const wrapper = mountPage()
    await flushPromises()

    const rows = wrapper.findAll('.el-table__body tr')
    expect(rows[0].text()).toContain(new Date('2026-09-30T10:00:00Z').toLocaleString())
    expect(rows[1].text()).not.toContain(new Date('2026-09-30T11:00:00Z').toLocaleString())
  })
})
