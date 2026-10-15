import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount } from '@vue/test-utils'
import ElementPlus from 'element-plus'
import { createPinia, setActivePinia } from 'pinia'
import EventListPage from './EventListPage.vue'
import * as eventsApi from '../../api/events'
import type { EventSummary } from '../../types/apiResponses'
import { usePlacedOrderNoticeStore } from '../../stores/placedOrderNotice'

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
// 輸出 href 才能斷言連結目標；class 由使用端的 attribute fallthrough 帶入。本頁的 to 都是字串。
const RouterLinkStub = { props: ['to'], template: '<a :href="to"><slot /></a>' }

function mountPage() {
  return mount(EventListPage, { global: { plugins: [ElementPlus], stubs: { RouterLink: RouterLinkStub } } })
}

beforeEach(() => {
  setActivePinia(createPinia())
})

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

describe('EventListPage（買家）下單成功付款提示（order-confirmation-page）', () => {
  const NOTICE_TEXT = '訂單已建立，已為你保留 10 分鐘，請在期限內完成付款'

  beforeEach(() => {
    vi.mocked(eventsApi.getEvents).mockReset()
    vi.mocked(eventsApi.getEvents).mockResolvedValue([])
  })

  function findNotice(wrapper: ReturnType<typeof mountPage>) {
    return wrapper.find('.placed-order-notice')
  }

  // 買家剛建立的 Pending 訂單只保留 10 分鐘；連結若指向錯的 Id 或不顯示，買家就找不到要付款的訂單。
  it('[BW-LOBBY-NOTICE-001] mount_AfterOrderPlaced_ShowsPaymentNoticeLinkingToOrderDetail', async () => {
    const store = usePlacedOrderNoticeStore()
    store.setPlacedOrder('order-456')

    const wrapper = mountPage()
    await flushPromises()

    const notice = findNotice(wrapper)
    expect(notice.exists()).toBe(true)
    expect(notice.attributes('role')).toBe('status')
    expect(notice.text()).toContain(NOTICE_TEXT)
    const payLink = notice.findAll('a').find((link) => link.text() === '前往付款')
    expect(payLink?.attributes('href')).toBe('/orders/order-456')
    expect(store.placedOrderId).toBeNull()
  })

  it('[BW-LOBBY-NOTICE-002] closeNotice_WhenClicked_HidesNotice', async () => {
    usePlacedOrderNoticeStore().setPlacedOrder('order-456')
    const wrapper = mountPage()
    await flushPromises()

    await findNotice(wrapper).find('.el-alert__close-btn').trigger('click')

    expect(findNotice(wrapper).exists()).toBe(false)
  })

  // 提示若重複出現，買家會在保留期過後仍看到「已為你保留 10 分鐘」而誤以為還能付款。
  it('[BW-LOBBY-NOTICE-003] mount_SecondTimeAfterNoticeShown_DoesNotShowNotice', async () => {
    usePlacedOrderNoticeStore().setPlacedOrder('order-456')
    const first = mountPage()
    await flushPromises()
    expect(findNotice(first).exists()).toBe(true)
    first.unmount()

    const second = mountPage()
    await flushPromises()

    expect(findNotice(second).exists()).toBe(false)
    expect(second.text()).not.toContain(NOTICE_TEXT)
  })

  it('[BW-LOBBY-NOTICE-004] mount_WithoutPlacedOrder_DoesNotShowNotice', async () => {
    const wrapper = mountPage()
    await flushPromises()

    expect(findNotice(wrapper).exists()).toBe(false)
    expect(wrapper.text()).not.toContain(NOTICE_TEXT)
  })

  // 列表載入失敗與訂單無關；若錯誤分支蓋掉提示，買家會失去唯一的付款入口提示。
  it('[BW-LOBBY-NOTICE-005] mount_AfterOrderPlacedAndListFails_ShowsErrorAndNotice', async () => {
    vi.mocked(eventsApi.getEvents).mockRejectedValue(new Error('network down'))
    usePlacedOrderNoticeStore().setPlacedOrder('order-456')

    const wrapper = mountPage()
    await flushPromises()

    expect(wrapper.text()).toContain('載入活動列表失敗')
    const notice = findNotice(wrapper)
    expect(notice.exists()).toBe(true)
    const payLink = notice.findAll('a').find((link) => link.text() === '前往付款')
    expect(payLink?.attributes('href')).toBe('/orders/order-456')
  })
})
