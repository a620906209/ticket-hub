import { beforeEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount } from '@vue/test-utils'
import ElementPlus from 'element-plus'
import MyOrdersPage from './MyOrdersPage.vue'
import * as ordersApi from '../../api/orders'
import { ApiError } from '../../api/httpClient'

vi.mock('../../api/orders')

function mountPage() {
  return mount(MyOrdersPage, { global: { plugins: [ElementPlus], stubs: { RouterLink: true } } })
}

describe('MyOrdersPage 我的訂單列表', () => {
  beforeEach(() => {
    vi.mocked(ordersApi.getMyOrders).mockReset()
  })

  it('Pending 訂單顯示保留時間，終態訂單不顯示保留時間', async () => {
    vi.mocked(ordersApi.getMyOrders).mockResolvedValue([
      { id: 'pending-order', eventId: 'event-1', status: 'Pending', heldUntilUtc: '2026-12-31T12:00:00Z' },
      { id: 'paid-order', eventId: 'event-2', status: 'Paid', heldUntilUtc: '2026-12-31T13:00:00Z' },
      { id: 'cancelled-order', eventId: 'event-3', status: 'Cancelled', heldUntilUtc: '2026-12-31T14:00:00Z' },
      { id: 'expired-order', eventId: 'event-4', status: 'Expired', heldUntilUtc: '2026-12-31T15:00:00Z' },
    ])

    const wrapper = mountPage()
    await flushPromises()

    expect(ordersApi.getMyOrders).toHaveBeenCalledOnce()
    expect(wrapper.text()).toContain(`保留至 ${new Date('2026-12-31T12:00:00Z').toLocaleString()}`)
    expect(wrapper.text()).not.toContain(new Date('2026-12-31T13:00:00Z').toLocaleString())
    expect(wrapper.text()).not.toContain(new Date('2026-12-31T14:00:00Z').toLocaleString())
    expect(wrapper.text()).not.toContain(new Date('2026-12-31T15:00:00Z').toLocaleString())
  })

  it('沒有訂單時顯示空清單提示', async () => {
    vi.mocked(ordersApi.getMyOrders).mockResolvedValue([])

    const wrapper = mountPage()
    await flushPromises()

    expect(wrapper.text()).toContain('目前沒有訂單')
  })

  it('訂單 API 失敗時顯示錯誤提示而非空清單', async () => {
    vi.mocked(ordersApi.getMyOrders).mockRejectedValue(new ApiError(500, { detail: '載入失敗' }))

    const wrapper = mountPage()
    await flushPromises()

    expect(wrapper.text()).toContain('載入失敗')
    expect(wrapper.text()).not.toContain('目前沒有訂單')
  })
})
