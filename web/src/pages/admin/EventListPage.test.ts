import { describe, it, expect, vi, beforeEach } from 'vitest'
import { mount, flushPromises } from '@vue/test-utils'
import ElementPlus from 'element-plus'
import EventListPage from './EventListPage.vue'
import * as adminApi from '../../api/admin'
import type { AdminEventSummary } from '../../types/apiResponses'

vi.mock('../../api/admin')

function buildEvent(overrides: Partial<AdminEventSummary> = {}): AdminEventSummary {
  return {
    id: 'event-1',
    title: 'Concert',
    startAtUtc: '2026-12-31T12:00:00Z',
    venueId: 'venue-1',
    seatMapId: 'seatmap-1',
    description: null,
    posterUrl: null,
    maxTicketsPerOrder: null,
    createdByMemberId: 'member-1',
    createdByDisplayName: 'Admin A',
    createdAtUtc: '2026-08-19T03:00:00Z',
    availableSeatCount: 0,
    heldSeatCount: 0,
    soldSeatCount: 0,
    ...overrides,
  }
}

function mountPage() {
  return mount(EventListPage, { global: { plugins: [ElementPlus], stubs: { RouterLink: true } } })
}

describe('EventListPage 活動列表：建立者/建立時間/售票狀況', () => {
  beforeEach(() => {
    vi.mocked(adminApi.getAdminEvents).mockReset()
  })

  it('建立者為 null 時顯示「—」，不顯示空白', async () => {
    vi.mocked(adminApi.getAdminEvents).mockResolvedValue([buildEvent({ createdByMemberId: null, createdByDisplayName: null })])
    const wrapper = mountPage()
    await flushPromises()

    expect(wrapper.text()).toContain('—')
  })

  it('建立時間為 null 時顯示「—」，不會被當成 1970/1/1 的假日期', async () => {
    vi.mocked(adminApi.getAdminEvents).mockResolvedValue([buildEvent({ createdAtUtc: null })])
    const wrapper = mountPage()
    await flushPromises()

    expect(wrapper.text()).not.toContain('1970')
    expect(wrapper.text()).toContain('—')
  })

  it('售票狀況橫條圖依三個數字的比例設定各區段的 flex 寬度', async () => {
    vi.mocked(adminApi.getAdminEvents).mockResolvedValue([
      buildEvent({ availableSeatCount: 3, heldSeatCount: 1, soldSeatCount: 6 }),
    ])
    const wrapper = mountPage()
    await flushPromises()

    const available = wrapper.find('.seat-status-segment.available')
    const held = wrapper.find('.seat-status-segment.held')
    const sold = wrapper.find('.seat-status-segment.sold')
    expect(available.attributes('style')).toContain('flex: 3')
    expect(held.attributes('style')).toContain('flex: 1')
    expect(sold.attributes('style')).toContain('flex: 6')
  })

  it('總座位數為 0 時顯示「尚無座位資料」，不渲染橫條、不會除以零', async () => {
    vi.mocked(adminApi.getAdminEvents).mockResolvedValue([
      buildEvent({ availableSeatCount: 0, heldSeatCount: 0, soldSeatCount: 0 }),
    ])
    const wrapper = mountPage()
    await flushPromises()

    expect(wrapper.text()).toContain('尚無座位資料')
    expect(wrapper.find('.seat-status-bar').exists()).toBe(false)
  })

  it('某個狀態的座位數為 0 時，該狀態不渲染對應的橫條區段', async () => {
    vi.mocked(adminApi.getAdminEvents).mockResolvedValue([
      buildEvent({ availableSeatCount: 5, heldSeatCount: 0, soldSeatCount: 0 }),
    ])
    const wrapper = mountPage()
    await flushPromises()

    expect(wrapper.find('.seat-status-segment.available').exists()).toBe(true)
    expect(wrapper.find('.seat-status-segment.held').exists()).toBe(false)
    expect(wrapper.find('.seat-status-segment.sold').exists()).toBe(false)
  })
})
