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

  it('[BW-MYORDER-LIST-001 開啟我的訂單列表頁] 顯示中文狀態；Pending 訂單顯示保留時間，終態訂單不顯示保留時間', async () => {
    vi.mocked(ordersApi.getMyOrders).mockResolvedValue([
      { id: 'pending-order', eventId: 'event-1', eventTitle: 'Event 1', status: 'Pending', heldUntilUtc: '2026-12-31T12:00:00Z' },
      { id: 'paid-order', eventId: 'event-2', eventTitle: 'Event 2', status: 'Paid', heldUntilUtc: '2026-12-31T13:00:00Z' },
      { id: 'cancelled-order', eventId: 'event-3', eventTitle: 'Event 3', status: 'Cancelled', heldUntilUtc: '2026-12-31T14:00:00Z' },
      { id: 'expired-order', eventId: 'event-4', eventTitle: 'Event 4', status: 'Expired', heldUntilUtc: '2026-12-31T15:00:00Z' },
    ])

    const wrapper = mountPage()
    await flushPromises()

    expect(ordersApi.getMyOrders).toHaveBeenCalledOnce()
    const rows = wrapper.findAll('.el-table__body tr')
    expect(rows.map((row) => row.find('td').text())).toEqual(['Event 1', 'Event 2', 'Event 3', 'Event 4'])
    expect(wrapper.text()).toContain('已逾時')
    expect(wrapper.text()).not.toMatch(/Pending|Paid|Cancelled|Expired/)
    expect(wrapper.text()).toContain(`保留至 ${new Date('2026-12-31T12:00:00Z').toLocaleString()}`)
    expect(wrapper.text()).not.toContain(new Date('2026-12-31T13:00:00Z').toLocaleString())
    expect(wrapper.text()).not.toContain(new Date('2026-12-31T14:00:00Z').toLocaleString())
    expect(wrapper.text()).not.toContain(new Date('2026-12-31T15:00:00Z').toLocaleString())
  })

  it('[BW-STATUS-TAG-001] Pending／Paid／Cancelled 分別以警示色、成功色、中性色標籤顯示中文文字', async () => {
    vi.mocked(ordersApi.getMyOrders).mockResolvedValue([
      { id: 'pending-order', eventId: 'event-1', eventTitle: 'Event 1', status: 'Pending', heldUntilUtc: '2026-12-31T12:00:00Z' },
      { id: 'paid-order', eventId: 'event-2', eventTitle: 'Event 2', status: 'Paid', heldUntilUtc: '2026-12-31T13:00:00Z' },
      { id: 'cancelled-order', eventId: 'event-3', eventTitle: 'Event 3', status: 'Cancelled', heldUntilUtc: '2026-12-31T14:00:00Z' },
    ])

    const wrapper = mountPage()
    await flushPromises()

    const tags = wrapper.findAll('.el-table__body .el-tag')
    expect(tags.map((tag) => tag.text())).toEqual(['待付款', '已付款', '已取消'])
    expect(tags[0].classes()).toContain('el-tag--warning')
    expect(tags[1].classes()).toContain('el-tag--success')
    expect(tags[2].classes()).toContain('el-tag--info')
  })

  it('[BW-MYORDER-DISPLAY-002] 未知狀態原樣顯示，其餘訂單正常顯示', async () => {
    vi.mocked(ordersApi.getMyOrders).mockResolvedValue([
      { id: 'unknown-order', eventId: 'event-1', eventTitle: 'Event 1', status: 'Refunded', heldUntilUtc: '2026-12-31T12:00:00Z' },
      { id: 'paid-order', eventId: 'event-2', eventTitle: 'Event 2', status: 'Paid', heldUntilUtc: '2026-12-31T13:00:00Z' },
    ])

    const wrapper = mountPage()
    await flushPromises()

    expect(wrapper.text()).toContain('Refunded')
    expect(wrapper.text()).toContain('已付款')
  })

  // design.md 決策 1：列表頁不承擔操作，買家由「查看明細」進入明細頁操作。
  it('列表頁不提供確認付款或取消訂單操作', async () => {
    vi.mocked(ordersApi.getMyOrders).mockResolvedValue([
      { id: 'pending-order', eventId: 'event-1', eventTitle: 'Event 1', status: 'Pending', heldUntilUtc: '2026-12-31T12:00:00Z' },
    ])

    const wrapper = mountPage()
    await flushPromises()

    expect(wrapper.text()).toContain('待付款')
    expect(wrapper.text()).not.toContain('確認付款')
    expect(wrapper.text()).not.toContain('取消訂單')
  })

  // 活動名稱由 Organizer 輸入，MUST 以表格 prop 或文字插值渲染（design.md 安全確認-前端）。
  it('[BW-MYORDER-LIST-001] 活動名稱含 HTML 標籤時原樣以文字顯示，表格內不產生元素', async () => {
    vi.mocked(ordersApi.getMyOrders).mockResolvedValue([
      { id: 'order-1', eventId: 'event-1', eventTitle: '<img src=x onerror=alert(1)>', status: 'Paid', heldUntilUtc: '2026-12-31T12:00:00Z' },
    ])

    const wrapper = mountPage()
    await flushPromises()

    expect(wrapper.text()).toContain('<img src=x onerror=alert(1)>')
    expect(wrapper.find('.el-table__body img').exists()).toBe(false)
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
