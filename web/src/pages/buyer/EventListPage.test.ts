import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount } from '@vue/test-utils'
import ElementPlus from 'element-plus'
import { createPinia, setActivePinia } from 'pinia'
import EventListPage from './EventListPage.vue'
import * as eventsApi from '../../api/events'
import type { EventSummary, PagedResult } from '../../types/apiResponses'
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

function buildPage(items: EventSummary[], overrides: Partial<PagedResult<EventSummary>> = {}): PagedResult<EventSummary> {
  return { items, page: 1, pageSize: 20, totalCount: items.length, ...overrides }
}

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
    vi.mocked(eventsApi.getEvents).mockResolvedValue(buildPage([
      buildEvent({ id: 'not-open', title: 'NotOpen Event', salesStartAtUtc: '2030-01-02T00:00:00Z' }),
      buildEvent({ id: 'open', title: 'Open Event', salesStartAtUtc: '2029-12-01T00:00:00Z' }),
      buildEvent({ id: 'closed', title: 'Closed Event', salesEndAtUtc: '2029-12-31T00:00:00Z' }),
    ]))

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
    vi.mocked(eventsApi.getEvents).mockResolvedValue(buildPage([]))
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

describe('EventListPage（買家）分頁（public-event-list-pagination）', () => {
  const pageOf = (page: number, count: number, totalCount: number) =>
    buildPage(
      Array.from({ length: count }, (_, i) => buildEvent({ id: `p${page}-${i}`, title: `Event p${page}-${i}` })),
      { page, totalCount },
    )

  function createDeferred<T>() {
    let resolve!: (value: T) => void
    let reject!: (reason: unknown) => void
    const promise = new Promise<T>((res, rej) => {
      resolve = res
      reject = rej
    })
    return { promise, resolve, reject }
  }

  async function changePage(wrapper: ReturnType<typeof mountPage>, page: number) {
    wrapper.findComponent({ name: 'ElPagination' }).vm.$emit('current-change', page)
    await flushPromises()
  }

  // v-loading 關閉後遮罩以 v-show 隱藏、延遲移除 DOM，只能以「存在且可見」判斷。
  function isLoadingMaskVisible(wrapper: ReturnType<typeof mountPage>): boolean {
    const mask = wrapper.find('.event-grid .el-loading-mask')
    return mask.exists() && mask.isVisible()
  }

  const titles = (wrapper: ReturnType<typeof mountPage>) => wrapper.findAll('.event-card h2').map((card) => card.text())

  beforeEach(() => {
    vi.mocked(eventsApi.getEvents).mockReset()
  })

  // 換頁若沒帶頁碼或沒有載入中提示，買家會看到舊內容卻以為已切換。
  it('[BW-LIST-PAGE-001] changePage_WhenTotalExceedsOnePage_RequestsThatPageWithLoadingAndShowsTotal', async () => {
    const second = createDeferred<PagedResult<EventSummary>>()
    vi.mocked(eventsApi.getEvents).mockResolvedValueOnce(pageOf(1, 20, 45)).mockReturnValueOnce(second.promise)
    const wrapper = mountPage()
    await flushPromises()
    expect(wrapper.find('.el-pagination__total').text()).toContain('45')

    await changePage(wrapper, 2)

    expect(eventsApi.getEvents).toHaveBeenLastCalledWith(2)
    await vi.waitFor(() => expect(isLoadingMaskVisible(wrapper)).toBe(true))
    second.resolve(pageOf(2, 20, 45))
    await flushPromises()
    expect(titles(wrapper)[0]).toBe('Event p2-0')
    await vi.waitFor(() => expect(isLoadingMaskVisible(wrapper)).toBe(false))
  })

  it('[BW-LIST-PAGE-002] mount_WhenTotalFitsOnePage_DoesNotShowPagination', async () => {
    vi.mocked(eventsApi.getEvents).mockResolvedValue(pageOf(1, 20, 20))

    const wrapper = mountPage()
    await flushPromises()

    expect(wrapper.findAll('.event-card')).toHaveLength(20)
    expect(wrapper.find('.el-pagination').exists()).toBe(false)
  })

  it('[BW-LIST-PAGE-003] mount_WhenTotalCountIsZero_ShowsEmptyStateWithoutPagination', async () => {
    vi.mocked(eventsApi.getEvents).mockResolvedValue(pageOf(1, 0, 0))

    const wrapper = mountPage()
    await flushPromises()

    expect(wrapper.text()).toContain('目前沒有活動')
    expect(wrapper.find('.el-pagination').exists()).toBe(false)
  })

  // 活動被刪除或下架使頁數變少時，買家停在超出的頁會看到空白；只重試一次，避免總數持續變動時無限重試。
  it('[BW-LIST-PAGE-004] changePage_WhenPageIsEmptyButTotalPositive_RetriesLastPageOnlyOnce', async () => {
    vi.mocked(eventsApi.getEvents)
      .mockResolvedValueOnce(pageOf(1, 20, 45))
      .mockResolvedValueOnce(pageOf(3, 0, 25))
      .mockResolvedValueOnce(pageOf(2, 5, 25))
    const wrapper = mountPage()
    await flushPromises()

    await changePage(wrapper, 3)

    expect(vi.mocked(eventsApi.getEvents).mock.calls.map((call) => call[0])).toEqual([1, 3, 2])
    expect(titles(wrapper)).toHaveLength(5)
    expect(wrapper.findComponent({ name: 'ElPagination' }).props('currentPage')).toBe(2)
  })

  it('[BW-LIST-PAGE-004b] changePage_WhenRetryPageIsAlsoEmpty_DoesNotRetryAgain', async () => {
    vi.mocked(eventsApi.getEvents)
      .mockResolvedValueOnce(pageOf(1, 20, 45))
      .mockResolvedValueOnce(pageOf(3, 0, 25))
      .mockResolvedValueOnce(pageOf(2, 0, 25))
    const wrapper = mountPage()
    await flushPromises()

    await changePage(wrapper, 3)

    expect(eventsApi.getEvents).toHaveBeenCalledTimes(3)
  })

  // 重試最後一頁的請求也是非同步的；若它在使用者已改選其他頁後才回來，不可覆蓋新結果。
  it('[BW-LIST-PAGE-007b] changePage_WhenStaleResponseArrivesDuringLastPageRetry_DiscardsIt', async () => {
    const slowRetry = createDeferred<PagedResult<EventSummary>>()
    vi.mocked(eventsApi.getEvents)
      .mockResolvedValueOnce(pageOf(1, 20, 45))
      .mockResolvedValueOnce(pageOf(3, 0, 25))
      .mockReturnValueOnce(slowRetry.promise)
      .mockResolvedValueOnce(pageOf(1, 20, 25))
    const wrapper = mountPage()
    await flushPromises()

    await changePage(wrapper, 3)
    await changePage(wrapper, 1)
    slowRetry.resolve(pageOf(2, 5, 25))
    await flushPromises()

    expect(titles(wrapper)[0]).toBe('Event p1-0')
    expect(wrapper.findComponent({ name: 'ElPagination' }).props('currentPage')).toBe(1)
  })

  // 失敗後若頁碼停在新頁而內容仍是舊頁，分頁元件與卡片會不一致。
  it('[BW-LIST-PAGE-005] changePage_WhenRequestFails_RevertsPageKeepsListAndShowsError', async () => {
    vi.mocked(eventsApi.getEvents).mockResolvedValueOnce(pageOf(1, 20, 45)).mockRejectedValueOnce(new Error('network down'))
    const wrapper = mountPage()
    await flushPromises()

    await changePage(wrapper, 2)

    expect(wrapper.text()).toContain('載入活動列表失敗')
    expect(titles(wrapper)[0]).toBe('Event p1-0')
    expect(wrapper.findAll('.event-card')).toHaveLength(20)
    expect(wrapper.findComponent({ name: 'ElPagination' }).props('currentPage')).toBe(1)
  })

  it('[BW-LIST-PAGE-006] mount_WhenFirstLoadFails_ShowsErrorWithoutListPaginationOrEmptyState', async () => {
    vi.mocked(eventsApi.getEvents).mockRejectedValue(new Error('network down'))

    const wrapper = mountPage()
    await flushPromises()

    expect(wrapper.text()).toContain('載入活動列表失敗')
    expect(wrapper.find('.event-grid').exists()).toBe(false)
    expect(wrapper.find('.el-pagination').exists()).toBe(false)
    expect(wrapper.text()).not.toContain('目前沒有活動')
  })

  // 快速連點兩頁時，較慢回來的舊請求若覆蓋新結果，買家會看到與頁碼不符的內容。
  it('[BW-LIST-PAGE-007] changePage_WhenStaleResponseArrivesLate_DiscardsIt', async () => {
    const slowSecond = createDeferred<PagedResult<EventSummary>>()
    vi.mocked(eventsApi.getEvents)
      .mockResolvedValueOnce(pageOf(1, 20, 90))
      .mockReturnValueOnce(slowSecond.promise)
      .mockResolvedValueOnce(pageOf(3, 20, 90))
    const wrapper = mountPage()
    await flushPromises()

    await changePage(wrapper, 2)
    await changePage(wrapper, 3)
    slowSecond.resolve(pageOf(2, 20, 90))
    await flushPromises()

    expect(titles(wrapper)[0]).toBe('Event p3-0')
    expect(wrapper.findComponent({ name: 'ElPagination' }).props('currentPage')).toBe(3)
    await vi.waitFor(() => expect(isLoadingMaskVisible(wrapper)).toBe(false))
  })

  // 頁碼只存在元件內，不進 URL；從詳情頁回來重新掛載後回到第 1 頁。
  it('[BW-LIST-PAGE-008] mount_AfterLeavingFromLaterPage_StartsAtFirstPage', async () => {
    vi.mocked(eventsApi.getEvents).mockResolvedValueOnce(pageOf(1, 20, 45)).mockResolvedValueOnce(pageOf(2, 20, 45))
    const first = mountPage()
    await flushPromises()
    await changePage(first, 2)
    first.unmount()
    vi.mocked(eventsApi.getEvents).mockResolvedValueOnce(pageOf(1, 20, 45))

    mountPage()
    await flushPromises()

    expect(eventsApi.getEvents).toHaveBeenLastCalledWith(1)
  })
})
