import { describe, it, expect, vi, beforeEach } from 'vitest'
import { mount, flushPromises } from '@vue/test-utils'
import ElementPlus from 'element-plus'
import AdminSalesReportPage from './AdminSalesReportPage.vue'
import * as adminApi from '../../api/admin'
import type { SalesReport } from '../../types/apiResponses'

vi.mock('../../api/admin')

vi.mock('vue-router', () => ({
  useRoute: () => ({ params: { eventId: 'event-1' } }),
}))

function buildReport(overrides: Partial<SalesReport> = {}): SalesReport {
  return {
    eventId: 'event-1',
    eventTitle: 'Concert',
    totalRevenue: 0,
    totalTicketsSold: 0,
    byTicketType: [],
    unclassifiedItemCount: 0,
    unclassifiedTicketsSold: 0,
    unclassifiedRevenue: 0,
    ...overrides,
  }
}

function mountPage() {
  return mount(AdminSalesReportPage, { global: { plugins: [ElementPlus] } })
}

describe('AdminSalesReportPage', () => {
  beforeEach(() => {
    vi.mocked(adminApi.getEventSalesReport).mockReset()
  })

  it('正確渲染總營收/總張數/依票種明細', async () => {
    vi.mocked(adminApi.getEventSalesReport).mockResolvedValue(
      buildReport({
        totalRevenue: 1400,
        totalTicketsSold: 4,
        byTicketType: [
          { ticketTypeId: 'tt-1', zoneCode: 'A', requiresSeat: true, quantitySold: 1, revenue: 500 },
          { ticketTypeId: 'tt-2', zoneCode: 'VIP', requiresSeat: false, quantitySold: 3, revenue: 900 },
        ],
      }),
    )

    const wrapper = mountPage()
    await flushPromises()

    expect(wrapper.text()).toContain('1400')
    expect(wrapper.text()).toContain('4')
    expect(wrapper.text()).toContain('A')
    expect(wrapper.text()).toContain('VIP')
    expect(wrapper.text()).toContain('座位制')
    expect(wrapper.text()).toContain('計數制')
  })

  it('總數為 0 時顯示「尚無銷售」提示，不誤判為載入失敗', async () => {
    vi.mocked(adminApi.getEventSalesReport).mockResolvedValue(buildReport())

    const wrapper = mountPage()
    await flushPromises()

    expect(wrapper.text()).toContain('尚無銷售')
    expect(wrapper.find('.el-alert--error').exists()).toBe(false)
  })

  it('unclassifiedItemCount > 0 時顯示提示，且顯示的筆數等於 API 回傳的 unclassifiedItemCount', async () => {
    vi.mocked(adminApi.getEventSalesReport).mockResolvedValue(
      buildReport({
        totalRevenue: 500,
        totalTicketsSold: 1,
        unclassifiedItemCount: 3,
        unclassifiedTicketsSold: 1,
        unclassifiedRevenue: 500,
      }),
    )

    const wrapper = mountPage()
    await flushPromises()

    expect(wrapper.text()).toContain('含 3 筆無法歸類的項目')
  })

  it('有票種但完全沒有銷售時，仍顯示票種明細表格（含 0 銷售的列），不因「尚無銷售」提示而隱藏', async () => {
    vi.mocked(adminApi.getEventSalesReport).mockResolvedValue(
      buildReport({
        byTicketType: [{ ticketTypeId: 'tt-1', zoneCode: 'A', requiresSeat: true, quantitySold: 0, revenue: 0 }],
      }),
    )

    const wrapper = mountPage()
    await flushPromises()

    expect(wrapper.text()).toContain('尚無銷售')
    expect(wrapper.find('.el-table').exists()).toBe(true)
    expect(wrapper.text()).toContain('A')
  })

  it('unclassifiedItemCount = 0 時不顯示提示', async () => {
    vi.mocked(adminApi.getEventSalesReport).mockResolvedValue(
      buildReport({ totalRevenue: 500, totalTicketsSold: 1, unclassifiedItemCount: 0 }),
    )

    const wrapper = mountPage()
    await flushPromises()

    expect(wrapper.text()).not.toContain('無法歸類')
  })
})
