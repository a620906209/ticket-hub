import { beforeEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount, type VueWrapper } from '@vue/test-utils'
import ElementPlus from 'element-plus'
import MyOrdersPage from './MyOrdersPage.vue'
import * as ordersApi from '../../api/orders'
import { ApiError } from '../../api/httpClient'
import type { PagedResult } from '../../types/apiResponses'

function pagedResult<T>(items: T[], overrides: Partial<PagedResult<T>> = {}): PagedResult<T> {
  return { items, page: 1, pageSize: 20, totalCount: items.length, ...overrides }
}

type Deferred<T> = { promise: Promise<T>; resolve: (value: T) => void; reject: (reason: unknown) => void }

function createDeferred<T>(): Deferred<T> {
  let resolve!: (value: T) => void
  let reject!: (reason: unknown) => void
  const promise = new Promise<T>((onResolve, onReject) => {
    resolve = onResolve
    reject = onReject
  })
  return { promise, resolve, reject }
}


vi.mock('../../api/orders')

function mountPage() {
  return mount(MyOrdersPage, { global: { plugins: [ElementPlus], stubs: { RouterLink: true } } })
}

describe('MyOrdersPage 我的訂單列表', () => {
  beforeEach(() => {
    vi.mocked(ordersApi.getMyOrders).mockReset()
  })

  it('[BW-MYORDER-LIST-001 開啟我的訂單列表頁] 顯示中文狀態；Pending 訂單顯示保留時間，終態訂單不顯示保留時間', async () => {
    vi.mocked(ordersApi.getMyOrders).mockResolvedValue(pagedResult([
      { id: 'pending-order', eventId: 'event-1', eventTitle: 'Event 1', status: 'Pending', heldUntilUtc: '2026-12-31T12:00:00Z' },
      { id: 'paid-order', eventId: 'event-2', eventTitle: 'Event 2', status: 'Paid', heldUntilUtc: '2026-12-31T13:00:00Z' },
      { id: 'cancelled-order', eventId: 'event-3', eventTitle: 'Event 3', status: 'Cancelled', heldUntilUtc: '2026-12-31T14:00:00Z' },
      { id: 'expired-order', eventId: 'event-4', eventTitle: 'Event 4', status: 'Expired', heldUntilUtc: '2026-12-31T15:00:00Z' },
    ]))

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
    vi.mocked(ordersApi.getMyOrders).mockResolvedValue(pagedResult([
      { id: 'pending-order', eventId: 'event-1', eventTitle: 'Event 1', status: 'Pending', heldUntilUtc: '2026-12-31T12:00:00Z' },
      { id: 'paid-order', eventId: 'event-2', eventTitle: 'Event 2', status: 'Paid', heldUntilUtc: '2026-12-31T13:00:00Z' },
      { id: 'cancelled-order', eventId: 'event-3', eventTitle: 'Event 3', status: 'Cancelled', heldUntilUtc: '2026-12-31T14:00:00Z' },
    ]))

    const wrapper = mountPage()
    await flushPromises()

    const tags = wrapper.findAll('.el-table__body .el-tag')
    expect(tags.map((tag) => tag.text())).toEqual(['待付款', '已付款', '已取消'])
    expect(tags[0].classes()).toContain('el-tag--warning')
    expect(tags[1].classes()).toContain('el-tag--success')
    expect(tags[2].classes()).toContain('el-tag--info')
  })

  it('[BW-MYORDER-DISPLAY-002] 未知狀態原樣顯示，其餘訂單正常顯示', async () => {
    vi.mocked(ordersApi.getMyOrders).mockResolvedValue(pagedResult([
      { id: 'unknown-order', eventId: 'event-1', eventTitle: 'Event 1', status: 'Refunded', heldUntilUtc: '2026-12-31T12:00:00Z' },
      { id: 'paid-order', eventId: 'event-2', eventTitle: 'Event 2', status: 'Paid', heldUntilUtc: '2026-12-31T13:00:00Z' },
    ]))

    const wrapper = mountPage()
    await flushPromises()

    expect(wrapper.text()).toContain('Refunded')
    expect(wrapper.text()).toContain('已付款')
  })

  // design.md 決策 1：列表頁不承擔操作，買家由「查看明細」進入明細頁操作。
  it('列表頁不提供確認付款或取消訂單操作', async () => {
    vi.mocked(ordersApi.getMyOrders).mockResolvedValue(pagedResult([
      { id: 'pending-order', eventId: 'event-1', eventTitle: 'Event 1', status: 'Pending', heldUntilUtc: '2026-12-31T12:00:00Z' },
    ]))

    const wrapper = mountPage()
    await flushPromises()

    expect(wrapper.text()).toContain('待付款')
    expect(wrapper.text()).not.toContain('確認付款')
    expect(wrapper.text()).not.toContain('取消訂單')
  })

  // 活動名稱由 Organizer 輸入，MUST 以表格 prop 或文字插值渲染（design.md 安全確認-前端）。
  it('[BW-MYORDER-LIST-001] 活動名稱含 HTML 標籤時原樣以文字顯示，表格內不產生元素', async () => {
    vi.mocked(ordersApi.getMyOrders).mockResolvedValue(pagedResult([
      { id: 'order-1', eventId: 'event-1', eventTitle: '<img src=x onerror=alert(1)>', status: 'Paid', heldUntilUtc: '2026-12-31T12:00:00Z' },
    ]))

    const wrapper = mountPage()
    await flushPromises()

    expect(wrapper.text()).toContain('<img src=x onerror=alert(1)>')
    expect(wrapper.find('.el-table__body img').exists()).toBe(false)
  })

  it('沒有訂單時顯示空清單提示', async () => {
    vi.mocked(ordersApi.getMyOrders).mockResolvedValue(pagedResult([]))

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

describe('MyOrdersPage 分頁（order-list-pagination）', () => {
  const getList = () => vi.mocked(ordersApi.getMyOrders)

  beforeEach(() => {
    getList().mockReset()
  })

  function pageOf(page: number, totalCount = 45, count = 2) {
    return pagedResult(
      Array.from({ length: count }, (_, index) => ({ id: `order-${page}-${index + 1}`, eventId: 'event-1', eventTitle: `Event ${page}-${index + 1}`, status: 'Paid', heldUntilUtc: '2026-12-31T12:00:00Z' })),
      { page, totalCount },
    )
  }

  // v-loading 關閉後遮罩以 v-show 隱藏、延遲移除 DOM，只能以「存在且可見」判斷。
  function isLoadingMaskVisible(wrapper: VueWrapper): boolean {
    const mask = wrapper.find('.el-loading-mask')
    return mask.exists() && mask.isVisible()
  }

  function rowTexts(wrapper: VueWrapper): string {
    return wrapper.findAll('.el-table__body tr').map((row) => row.text()).join('|')
  }

  function activePage(wrapper: VueWrapper): string {
    return wrapper.find('.el-pager li.is-active').text()
  }

  async function clickPage(wrapper: VueWrapper, pageNumber: number): Promise<void> {
    const button = wrapper.findAll('.el-pager li').find((item) => item.text() === String(pageNumber))
    expect(button, `應有頁碼 ${pageNumber}`).toBeDefined()
    await button!.trigger('click')
  }

  async function clickRefresh(wrapper: VueWrapper): Promise<void> {
    const button = wrapper.findAll('button').find((item) => item.text().includes('重新整理'))
    expect(button, '應有重新整理按鈕').toBeDefined()
    await button!.trigger('click')
  }

  it('[BW-MYORDER-PAGE-001] 超過一頁時顯示總筆數與頁碼（無每頁筆數選擇器），點第 2 頁以 page=2 取代列表並在載入期間顯示載入中', async () => {
    getList().mockResolvedValueOnce(pageOf(1))
    const wrapper = mountPage()
    await flushPromises()

    expect(getList()).toHaveBeenLastCalledWith(1)
    expect(wrapper.find('.el-pagination').exists()).toBe(true)
    expect(wrapper.find('.el-pagination__total').text()).toContain('45')
    expect(wrapper.find('.el-pagination__sizes').exists()).toBe(false)
    expect(wrapper.find('.el-pagination__jump').exists()).toBe(false)
    expect(rowTexts(wrapper)).toContain('Event 1-1')

    const second = createDeferred<ReturnType<typeof pageOf>>()
    getList().mockReturnValueOnce(second.promise)
    await clickPage(wrapper, 2)
    await flushPromises()
    expect(getList()).toHaveBeenLastCalledWith(2)
    await vi.waitFor(() => expect(isLoadingMaskVisible(wrapper)).toBe(true))

    second.resolve(pageOf(2))
    await flushPromises()
    expect(rowTexts(wrapper)).toContain('Event 2-1')
    expect(rowTexts(wrapper)).not.toContain('Event 1-1')
    expect(activePage(wrapper)).toBe('2')
    await vi.waitFor(() => expect(isLoadingMaskVisible(wrapper)).toBe(false))
  })

  it('[BW-MYORDER-PAGE-002] 筆數不超過一頁或沒有訂單時不顯示分頁元件', async () => {
    getList().mockResolvedValueOnce(pagedResult([], { totalCount: 0 }))
    const emptyWrapper = mountPage()
    await flushPromises()
    expect(emptyWrapper.find('.el-pagination').exists()).toBe(false)

    getList().mockResolvedValueOnce(pageOf(1, 20, 2))
    const singlePageWrapper = mountPage()
    await flushPromises()
    expect(rowTexts(singlePageWrapper)).toContain('Event 1-1')
    expect(singlePageWrapper.find('.el-pagination').exists()).toBe(false)
    expect(singlePageWrapper.text()).not.toContain('目前沒有訂單')
  })

  it('[BW-MYORDER-PAGE-003] 重新整理停留在目前頁，不回到第 1 頁', async () => {
    getList().mockResolvedValueOnce(pageOf(1)).mockResolvedValueOnce(pageOf(2)).mockResolvedValueOnce(pageOf(2))
    const wrapper = mountPage()
    await flushPromises()
    await clickPage(wrapper, 2)
    await flushPromises()

    await clickRefresh(wrapper)
    await flushPromises()

    expect(getList().mock.calls.map((call) => call[0])).toEqual([1, 2, 2])
    expect(activePage(wrapper)).toBe('2')
  })

  it('[BW-MYORDER-PAGE-003] 目前頁回傳空 items 但 totalCount > 0 時只重試一次改請求最後一頁，並同步分頁元件頁碼', async () => {
    getList()
      .mockResolvedValueOnce(pageOf(1))
      .mockResolvedValueOnce(pageOf(2))
      .mockResolvedValueOnce(pagedResult([], { page: 2, totalCount: 45 }))
      .mockResolvedValueOnce(pageOf(3, 45, 1))
    const wrapper = mountPage()
    await flushPromises()
    await clickPage(wrapper, 2)
    await flushPromises()

    await clickRefresh(wrapper)
    await flushPromises()

    expect(getList().mock.calls.map((call) => call[0])).toEqual([1, 2, 2, 3])
    expect(rowTexts(wrapper)).toContain('Event 3-1')
    expect(activePage(wrapper)).toBe('3')
  })

  it('[BW-MYORDER-PAGE-003] 最後一頁仍為空時不再重試（只重試一次）', async () => {
    getList()
      .mockResolvedValueOnce(pagedResult([], { page: 1, totalCount: 45 }))
      .mockResolvedValueOnce(pagedResult([], { page: 3, totalCount: 45 }))
    mountPage()
    await flushPromises()

    expect(getList().mock.calls.map((call) => call[0])).toEqual([1, 3])
  })

  it('[BW-MYORDER-PAGE-004] 換頁請求失敗時頁碼退回前一頁，列表保留前一頁資料並顯示錯誤提示', async () => {
    getList().mockResolvedValueOnce(pageOf(1)).mockRejectedValueOnce(new ApiError(500, { detail: '換頁失敗' }))
    const wrapper = mountPage()
    await flushPromises()

    await clickPage(wrapper, 2)
    await flushPromises()

    expect(activePage(wrapper)).toBe('1')
    expect(rowTexts(wrapper)).toContain('Event 1-1')
    expect(wrapper.text()).toContain('換頁失敗')
  })

  it('[BW-MYORDER-PAGE-005] 快速連續換頁時丟棄過期回應：第 3 頁先到、第 2 頁後到，列表顯示第 3 頁', async () => {
    getList().mockResolvedValueOnce(pageOf(1))
    const wrapper = mountPage()
    await flushPromises()

    const second = createDeferred<ReturnType<typeof pageOf>>()
    const third = createDeferred<ReturnType<typeof pageOf>>()
    getList().mockReturnValueOnce(second.promise).mockReturnValueOnce(third.promise)
    await clickPage(wrapper, 2)
    await clickPage(wrapper, 3)
    third.resolve(pageOf(3, 45, 1))
    await flushPromises()
    second.resolve(pageOf(2))
    await flushPromises()

    expect(rowTexts(wrapper)).toContain('Event 3-1')
    expect(rowTexts(wrapper)).not.toContain('Event 2-1')
    expect(activePage(wrapper)).toBe('3')
  })

  it('[BW-MYORDER-PAGE-005] 過期請求的失敗回應被丟棄：第 2 頁較晚失敗時，不顯示錯誤、不改動第 3 頁的頁碼與列表', async () => {
    getList().mockResolvedValueOnce(pageOf(1))
    const wrapper = mountPage()
    await flushPromises()

    const second = createDeferred<ReturnType<typeof pageOf>>()
    const third = createDeferred<ReturnType<typeof pageOf>>()
    getList().mockReturnValueOnce(second.promise).mockReturnValueOnce(third.promise)
    await clickPage(wrapper, 2)
    await clickPage(wrapper, 3)
    third.resolve(pageOf(3, 45, 1))
    await flushPromises()
    second.reject(new ApiError(500, { detail: '過期的失敗' }))
    await flushPromises()

    expect(wrapper.text()).not.toContain('過期的失敗')
    expect(rowTexts(wrapper)).toContain('Event 3-1')
    expect(activePage(wrapper)).toBe('3')
  })
})
