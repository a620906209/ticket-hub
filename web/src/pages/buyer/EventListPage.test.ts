import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount } from '@vue/test-utils'
import ElementPlus from 'element-plus'
import EventListPage from './EventListPage.vue'
import * as eventsApi from '../../api/events'
import type { EventSummary } from '../../types/apiResponses'

vi.mock('../../api/events')

const NOW = new Date('2030-01-01T00:00:00Z')

function buildEvent(overrides: Partial<EventSummary> = {}): EventSummary {
  return {
    id: 'event-1',
    title: 'Concert',
    startAtUtc: '2030-06-01T12:00:00Z',
    venueId: 'venue-1',
    seatMapId: 'seatmap-1',
    description: null,
    posterUrl: null,
    maxTicketsPerOrder: null,
    isQueueModeEnabled: false,
    isRealNameRequired: false,
    salesStartAtUtc: null,
    salesEndAtUtc: null,
    ...overrides,
  }
}

// RouterLink: true 的 stub 預設不渲染 slot，卡片內容會消失；改用保留 slot 的 stub。
const RouterLinkStub = { props: ['to'], template: '<a class="event-card"><slot /></a>' }

function mountPage() {
  return mount(EventListPage, { global: { plugins: [ElementPlus], stubs: { RouterLink: RouterLinkStub } } })
}

describe('EventListPage（買家）販售狀態', () => {
  beforeEach(() => {
    vi.useFakeTimers({ toFake: ['Date'] })
    vi.setSystemTime(NOW)
    vi.mocked(eventsApi.getEvents).mockReset()
  })

  afterEach(() => {
    vi.useRealTimers()
  })

  // 列表 API 刻意不回傳推導狀態，前端必須依當下時間自行推導；若標籤改成看固定欄位或忽略
  // salesEndAtUtc，三張卡片的文字會錯位。
  it('[BW-SALES-013] 尚未開賣、販售中、已停售的活動各自顯示對應標籤', async () => {
    vi.mocked(eventsApi.getEvents).mockResolvedValue([
      buildEvent({ id: 'not-open', title: 'NotOpen Event', salesStartAtUtc: '2030-01-02T00:00:00Z' }),
      buildEvent({ id: 'open', title: 'Open Event', salesStartAtUtc: '2029-12-01T00:00:00Z' }),
      buildEvent({ id: 'closed', title: 'Closed Event', salesEndAtUtc: '2029-12-31T00:00:00Z' }),
    ])

    const wrapper = mountPage()
    await flushPromises()

    const cards = wrapper.findAll('.event-card')
    expect(cards.map((card) => card.find('h2').text())).toEqual(['NotOpen Event', 'Open Event', 'Closed Event'])
    expect(cards.map((card) => card.find('.el-tag').text())).toEqual(['尚未開賣', '販售中', '已停售'])
  })
})
