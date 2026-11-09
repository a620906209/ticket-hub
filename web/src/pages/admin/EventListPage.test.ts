import { describe, it, expect, vi, beforeEach } from 'vitest'
import { mount, flushPromises, type VueWrapper } from '@vue/test-utils'
import ElementPlus from 'element-plus'
import { createPinia, setActivePinia } from 'pinia'
import EventListPage from './EventListPage.vue'
import * as adminApi from '../../api/admin'
import * as eventsApi from '../../api/events'
import type { AdminEventSummary, PagedResult, TicketType } from '../../types/apiResponses'
import { useAuthStore } from '../../stores/auth'
import { ApiError } from '../../api/httpClient'

vi.mock('../../api/admin')
vi.mock('../../api/events')

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
    isRealNameRequired: false,
    salesStartAtUtc: null,
    salesEndAtUtc: null,
    ...overrides,
  }
}

function pagedEvents(items: AdminEventSummary[], overrides: Partial<PagedResult<AdminEventSummary>> = {}): PagedResult<AdminEventSummary> {
  return { items, page: 1, pageSize: 20, totalCount: items.length, ...overrides }
}

function buildTicketType(overrides: Partial<TicketType> = {}): TicketType {
  return {
    id: 'ticket-type-1',
    zoneCode: 'A',
    price: 100,
    requiresSeat: true,
    availableQuantity: null,
    ...overrides,
  }
}

function mountPage() {
  return mount(EventListPage, { global: { plugins: [ElementPlus], stubs: { RouterLink: true } } })
}

// ElInputNumber／ElSwitch 的真實實作在 jsdom 下互動較繁瑣（格式化、blur 提交時機），
// 這裡用行為等價的原生 checkbox/number input 取代，只驗證元件邏輯本身，比照
// EventCreatePage.test.ts 對 ElSelect／ElDatePicker 的既有取捨。
const ElSwitchStub = {
  props: ['modelValue'],
  emits: ['update:modelValue', 'change'],
  template: `<input type="checkbox" role="switch" :checked="modelValue"
      @change="$emit('update:modelValue', $event.target.checked); $emit('change', $event.target.checked)" />`,
}
const ElInputNumberStub = {
  props: ['modelValue', 'disabled'],
  emits: ['update:modelValue', 'change'],
  // 用 @input（而非 @change）比照 vue-test-utils 的 setValue() 對 <input type="number"> 觸發的事件。
  template: `<input type="number" :value="modelValue" :disabled="disabled"
      @input="$emit('update:modelValue', $event.target.valueAsNumber); $emit('change', $event.target.valueAsNumber)" />`,
}
// 「活動」下拉選單同樣改用原生 select，比照 EventCreatePage.test.ts 對 ElSelect 的既有取捨。
const ElSelectStub = {
  props: ['modelValue'],
  emits: ['update:modelValue', 'change'],
  template: `<select :value="modelValue" @change="$emit('update:modelValue', $event.target.value); $emit('change', $event.target.value)">
    <option value="" />
    <slot />
  </select>`,
}
const ElOptionStub = {
  props: ['value', 'label'],
  template: `<option :value="value">{{ label }}</option>`,
}

function mountPageForTicketTypeForm() {
  return mount(EventListPage, {
    global: {
      plugins: [ElementPlus],
      stubs: {
        RouterLink: true,
        ElSwitch: ElSwitchStub,
        ElInputNumber: ElInputNumberStub,
        ElSelect: ElSelectStub,
        ElOption: ElOptionStub,
      },
    },
  })
}

describe('EventListPage 活動列表：建立者/建立時間/售票狀況', () => {
  beforeEach(() => {
    setActivePinia(createPinia())
    vi.mocked(adminApi.getAdminEvents).mockReset()
  })

  it('建立者為 null 時顯示「—」，不顯示空白', async () => {
    vi.mocked(adminApi.getAdminEvents).mockResolvedValue(pagedEvents([buildEvent({ createdByMemberId: null, createdByDisplayName: null })]))
    const wrapper = mountPage()
    await flushPromises()

    expect(wrapper.text()).toContain('—')
  })

  it('建立時間為 null 時顯示「—」，不會被當成 1970/1/1 的假日期', async () => {
    vi.mocked(adminApi.getAdminEvents).mockResolvedValue(pagedEvents([buildEvent({ createdAtUtc: null })]))
    const wrapper = mountPage()
    await flushPromises()

    expect(wrapper.text()).not.toContain('1970')
    expect(wrapper.text()).toContain('—')
  })

  // 未設定者須照實顯示「未設定」，不可展開成活動開始時間，否則操作人員無法分辨是否有設定。
  it('[AWU-EVENT-SALES-008] 有設定販售期間者顯示開賣與停售時間，未設定者顯示「未設定」', async () => {
    const salesStartAtUtc = '2026-11-01T10:00:00Z'
    const salesEndAtUtc = '2026-12-30T10:00:00Z'
    vi.mocked(adminApi.getAdminEvents).mockResolvedValue(pagedEvents([
      buildEvent({ id: 'with-window', salesStartAtUtc, salesEndAtUtc }),
      buildEvent({ id: 'without-window' }),
    ]))
    const wrapper = mountPage()
    await flushPromises()

    const salesWindows = wrapper.findAll('.sales-window').map((cell) => cell.text())
    expect(salesWindows).toEqual([
      `${new Date(salesStartAtUtc).toLocaleString()} ～ ${new Date(salesEndAtUtc).toLocaleString()}`,
      '未設定 ～ 未設定（活動開始時停售）',
    ])
  })

  it('售票狀況橫條圖依三個數字的比例設定各區段的 flex 寬度', async () => {
    vi.mocked(adminApi.getAdminEvents).mockResolvedValue(pagedEvents([
      buildEvent({ availableSeatCount: 3, heldSeatCount: 1, soldSeatCount: 6 }),
    ]))
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
    vi.mocked(adminApi.getAdminEvents).mockResolvedValue(pagedEvents([
      buildEvent({ availableSeatCount: 0, heldSeatCount: 0, soldSeatCount: 0 }),
    ]))
    const wrapper = mountPage()
    await flushPromises()

    expect(wrapper.text()).toContain('尚無座位資料')
    expect(wrapper.find('.seat-status-bar').exists()).toBe(false)
  })

  it('某個狀態的座位數為 0 時，該狀態不渲染對應的橫條區段', async () => {
    vi.mocked(adminApi.getAdminEvents).mockResolvedValue(pagedEvents([
      buildEvent({ availableSeatCount: 5, heldSeatCount: 0, soldSeatCount: 0 }),
    ]))
    const wrapper = mountPage()
    await flushPromises()

    expect(wrapper.find('.seat-status-segment.available').exists()).toBe(true)
    expect(wrapper.find('.seat-status-segment.held').exists()).toBe(false)
    expect(wrapper.find('.seat-status-segment.sold').exists()).toBe(false)
  })
})

function createDeferred<T>() {
  let resolve!: (value: T) => void
  let reject!: (reason: unknown) => void
  const promise = new Promise<T>((settle, fail) => {
    resolve = settle
    reject = fail
  })
  return { promise, resolve, reject }
}

// 活動列表只帶 venueId／seatMapId，操作者看不懂 GUID；場館名稱改由場地查詢 API 對照（frontend-minor-polish design.md 決策 1）。
describe('EventListPage 活動列表：場館名稱', () => {
  const venueId = '5d3f0c2a-9b1e-4f6a-8c7d-2e4b6a8c0d1f'
  const seatMapId = 'a1b2c3d4-e5f6-4a7b-8c9d-0e1f2a3b4c5d'
  const venueName = '城市音樂廳'

  beforeEach(() => {
    setActivePinia(createPinia())
    vi.mocked(adminApi.getAdminEvents).mockReset()
    vi.mocked(adminApi.getVenues).mockReset()
  })

  function venueCells(wrapper: ReturnType<typeof mountPage>): string[] {
    return wrapper.findAll('.venue-name').map((cell) => cell.text())
  }

  function expectNoIds(text: string, ids: string[]): void {
    for (const id of ids) {
      expect(text).not.toContain(id)
      expect(text).not.toContain(id.slice(0, 8))
    }
  }

  it('[AWU-EVENT-VENUE-001] mount_WhenVenuesLoaded_ShowsVenueNameWithoutIds', async () => {
    vi.mocked(adminApi.getAdminEvents).mockResolvedValue(pagedEvents([buildEvent({ venueId, seatMapId })]))
    vi.mocked(adminApi.getVenues).mockResolvedValue([{ id: venueId, name: venueName }])
    const wrapper = mountPage()
    await flushPromises()

    expect(venueCells(wrapper)).toEqual([venueName])
    const headers = wrapper.findAll('th').map((th) => th.text())
    expect(headers).toContain('場館')
    expect(headers.some((header) => header.includes('座位圖') || header.includes('Id'))).toBe(false)
    expectNoIds(wrapper.find('.el-table').text(), [venueId, seatMapId])
    // 舊提示「顯示原始 Id」與新行為矛盾，須一併移除
    expect(wrapper.text()).not.toContain('原始 Id')
  })

  it('[AWU-EVENT-VENUE-001] mount_WhenVenuesRespondAfterEvents_ShowsLoadingThenVenueName', async () => {
    const venues = createDeferred<{ id: string; name: string }[]>()
    vi.mocked(adminApi.getAdminEvents).mockResolvedValue(pagedEvents([buildEvent({ venueId })]))
    vi.mocked(adminApi.getVenues).mockReturnValue(venues.promise)
    const wrapper = mountPage()
    await flushPromises()

    // 活動列表不等場地查詢就先顯示
    expect(wrapper.text()).toContain('Concert')
    expect(venueCells(wrapper)).toEqual(['載入中…'])

    venues.resolve([{ id: venueId, name: venueName }])
    await flushPromises()

    expect(venueCells(wrapper)).toEqual([venueName])
  })

  it('[AWU-EVENT-VENUE-001] mount_WhenVenuesRespondBeforeEvents_ShowsVenueName', async () => {
    const events = createDeferred<PagedResult<AdminEventSummary>>()
    vi.mocked(adminApi.getAdminEvents).mockReturnValue(events.promise)
    vi.mocked(adminApi.getVenues).mockResolvedValue([{ id: venueId, name: venueName }])
    const wrapper = mountPage()
    await flushPromises()
    // 兩個查詢須並行發出；場地查詢若排在活動列表之後，名稱要多等一整個列表請求
    expect(adminApi.getVenues).toHaveBeenCalledTimes(1)

    events.resolve(pagedEvents([buildEvent({ venueId })]))
    await flushPromises()

    expect(venueCells(wrapper)).toEqual([venueName])
  })

  it('[AWU-EVENT-VENUE-002] mount_WhenVenueLookupFails_StillShowsEventsWithFallbackText', async () => {
    vi.mocked(adminApi.getAdminEvents).mockResolvedValue(pagedEvents([buildEvent({ venueId, seatMapId })]))
    vi.mocked(adminApi.getVenues).mockRejectedValue(new Error('network'))
    const wrapper = mountPage()
    await flushPromises()

    expect(wrapper.text()).toContain('Concert')
    expect(venueCells(wrapper)).toEqual(['無法載入場館名稱'])
    expectNoIds(wrapper.text(), [venueId, seatMapId])
    expect(wrapper.find('.el-alert').exists()).toBe(false)
  })

  // 兩個查詢的錯誤互不混用：活動列表失敗只顯示列表錯誤，不會冒出場館的後備文字。
  it('[AWU-EVENT-VENUE-002] mount_WhenEventListFailsButVenuesLoad_ShowsListErrorOnly', async () => {
    vi.mocked(adminApi.getAdminEvents).mockRejectedValue(new Error('network'))
    vi.mocked(adminApi.getVenues).mockResolvedValue([{ id: venueId, name: venueName }])
    const wrapper = mountPage()
    await flushPromises()

    expect(wrapper.find('.el-alert').text()).toContain('載入活動列表失敗')
    expect(wrapper.findAll('.el-table__body tr')).toHaveLength(0)
    expect(wrapper.text()).not.toContain('無法載入場館名稱')
  })

  it('[AWU-EVENT-VENUE-003] mount_WhenVenueNotInList_ShowsUnknownVenue', async () => {
    vi.mocked(adminApi.getAdminEvents).mockResolvedValue(pagedEvents([buildEvent({ venueId })]))
    vi.mocked(adminApi.getVenues).mockResolvedValue([{ id: 'other-venue', name: '別的場館' }])
    const wrapper = mountPage()
    await flushPromises()

    expect(venueCells(wrapper)).toEqual(['未知場館'])
    expectNoIds(wrapper.text(), [venueId])
  })
})

// 銷售報表後端已改為 RequireOrganizerContext（order-report-redemption-organizer-scoping design.md Decision 3），
// 入口不再依角色隱藏；活動列表本身已只列出目前 Organizer 名下的活動。
describe('EventListPage 銷售報表入口', () => {
  beforeEach(() => {
    setActivePinia(createPinia())
    vi.mocked(adminApi.getAdminEvents).mockReset()
    vi.mocked(adminApi.getAdminEvents).mockResolvedValue(pagedEvents([buildEvent({ id: 'event-1' }), buildEvent({ id: 'event-2', title: 'Festival' })]))
  })

  // 預設的 RouterLink stub 不保留 to 物件，改用會把路由名稱與參數輸出成屬性的 stub 以便辨識是哪個連結。
  const RouterLinkStub = {
    props: ['to'],
    template: `<a :data-route-name="to.name" :data-event-id="to.params?.eventId"><slot /></a>`,
  }

  it('[AWU-NAV-003] 非 Admin 使用者看得到每筆活動的銷售報表入口，且連結指向對應活動的銷售報表路由', async () => {
    useAuthStore().member = { id: '2', email: 'member@example.com', displayName: 'Member', role: 'Member', isActive: true, hasRegisteredRealName: false, realName: null, nationalIdLast4Masked: null }
    const wrapper = mount(EventListPage, { global: { plugins: [ElementPlus], stubs: { RouterLink: RouterLinkStub } } })
    await flushPromises()

    const links = wrapper.findAll('[data-route-name="admin-sales-report"]')
    expect(links.map((link) => link.attributes('data-event-id'))).toEqual(['event-1', 'event-2'])
    expect(links.every((link) => link.text() === '銷售報表')).toBe(true)
  })
})

describe('EventListPage 建立票種：座位制／計數制（RequiresSeat 開關）', () => {
  beforeEach(() => {
    setActivePinia(createPinia())
    vi.mocked(adminApi.getAdminEvents).mockReset()
    vi.mocked(adminApi.createTicketType).mockReset()
    vi.mocked(eventsApi.getTicketTypes).mockReset()
    vi.mocked(adminApi.getAdminEvents).mockResolvedValue(pagedEvents([buildEvent()]))
    vi.mocked(eventsApi.getTicketTypes).mockResolvedValue([])
  })

  it('維持「是否綁座位」開關為開啟，建立票種送出 RequiresSeat = true', async () => {
    vi.mocked(adminApi.createTicketType).mockResolvedValue({ id: 'tt-1' })
    const wrapper = mountPageForTicketTypeForm()
    await flushPromises()

    await wrapper.find('select').setValue('event-1')
    await wrapper.find('input[maxlength="50"]').setValue('A')
    await wrapper.find('input[type="number"]').setValue(100)
    await wrapper.find('form').trigger('submit')
    await flushPromises()

    // el-form 的 validate 走 async-validator，在高負載下單次 flushPromises 不保證驗證與送出已完成，改為等待條件成立。
    await vi.waitFor(() => expect(adminApi.createTicketType).toHaveBeenCalledWith('event-1', 'A', 100, true, undefined))
  })

  it('關閉開關並填寫票種名稱、票價、可售總量，建立票種送出 RequiresSeat = false 與 AvailableQuantity', async () => {
    vi.mocked(adminApi.createTicketType).mockResolvedValue({ id: 'tt-1' })
    const wrapper = mountPageForTicketTypeForm()
    await flushPromises()

    await wrapper.find('select').setValue('event-1')
    await wrapper.find('input[type="checkbox"]').setValue(false)
    await wrapper.find('input[maxlength="50"]').setValue('站立區')
    const numberInputs = wrapper.findAll('input[type="number"]')
    await numberInputs[0].setValue(500)
    await numberInputs[1].setValue(200)
    await wrapper.find('form').trigger('submit')
    await flushPromises()

    await vi.waitFor(() => expect(adminApi.createTicketType).toHaveBeenCalledWith('event-1', '站立區', 500, false, 200))
  })

  it('關閉開關但可售總量留空，顯示驗證錯誤、不呼叫 createTicketType', async () => {
    const wrapper = mountPageForTicketTypeForm()
    await flushPromises()

    await wrapper.find('select').setValue('event-1')
    await wrapper.find('input[type="checkbox"]').setValue(false)
    await wrapper.find('input[maxlength="50"]').setValue('站立區')
    const priceInput = wrapper.findAll('input[type="number"]')[0]
    await priceInput.setValue(500)
    // 可售總量刻意不填（維持 undefined），直接送出。
    await wrapper.find('form').trigger('submit')
    await flushPromises()

    expect(adminApi.createTicketType).not.toHaveBeenCalled()
    // Element Plus 欄位錯誤訊息延遲 100ms 才顯示（validateStateDebounced）
    await vi.waitFor(() => expect(wrapper.text()).toContain('可售總量須為大於 0 的整數'))
  })

  it('關閉開關但可售總量填 0，顯示驗證錯誤、不呼叫 createTicketType', async () => {
    const wrapper = mountPageForTicketTypeForm()
    await flushPromises()

    await wrapper.find('select').setValue('event-1')
    await wrapper.find('input[type="checkbox"]').setValue(false)
    await wrapper.find('input[maxlength="50"]').setValue('站立區')
    const numberInputs = wrapper.findAll('input[type="number"]')
    await numberInputs[0].setValue(500)
    await numberInputs[1].setValue(0)
    await wrapper.find('form').trigger('submit')
    await flushPromises()

    expect(adminApi.createTicketType).not.toHaveBeenCalled()
    // Element Plus 欄位錯誤訊息延遲 100ms 才顯示（validateStateDebounced）
    await vi.waitFor(() => expect(wrapper.text()).toContain('可售總量須為大於 0 的整數'))
  })

  it('關閉開關但可售總量填負數，顯示驗證錯誤、不呼叫 createTicketType', async () => {
    const wrapper = mountPageForTicketTypeForm()
    await flushPromises()

    await wrapper.find('select').setValue('event-1')
    await wrapper.find('input[type="checkbox"]').setValue(false)
    await wrapper.find('input[maxlength="50"]').setValue('站立區')
    const numberInputs = wrapper.findAll('input[type="number"]')
    await numberInputs[0].setValue(500)
    await numberInputs[1].setValue(-1)
    await wrapper.find('form').trigger('submit')
    await flushPromises()

    expect(adminApi.createTicketType).not.toHaveBeenCalled()
    // Element Plus 欄位錯誤訊息延遲 100ms 才顯示（validateStateDebounced）
    await vi.waitFor(() => expect(wrapper.text()).toContain('可售總量須為大於 0 的整數'))
  })

  it('關閉開關輸入可售總量後重新開啟開關，可售總量欄位值被清空', async () => {
    const wrapper = mountPageForTicketTypeForm()
    await flushPromises()

    const checkbox = wrapper.find('input[type="checkbox"]')
    await checkbox.setValue(false)
    await wrapper.findAll('input[type="number"]')[1].setValue(200)
    await checkbox.setValue(true)
    await checkbox.setValue(false) // 再次關閉，讓可售總量欄位重新出現以便檢查目前值

    const availableQuantityInput = wrapper.findAll('input[type="number"]')[1]
    expect((availableQuantityInput.element as HTMLInputElement).value).toBe('')
  })

  it('開啟活動列表頁展開票種清單，正確顯示既有票種的模式與可售總量', async () => {
    vi.mocked(eventsApi.getTicketTypes).mockResolvedValue([
      buildTicketType({ id: 'tt-seat', zoneCode: 'A', requiresSeat: true, availableQuantity: null }),
      buildTicketType({ id: 'tt-count', zoneCode: '站立區', requiresSeat: false, availableQuantity: 300 }),
    ])
    const wrapper = mountPage()
    await flushPromises()

    await wrapper.find('.el-table__expand-icon').trigger('click')
    await flushPromises()

    expect(eventsApi.getTicketTypes).toHaveBeenCalledWith('event-1')
    expect(wrapper.text()).toContain('座位制')
    expect(wrapper.text()).toContain('計數制')
    expect(wrapper.text()).toContain('300')
  })
})

describe('EventListPage 需實名標示（real-name-verification）', () => {
  beforeEach(() => {
    setActivePinia(createPinia())
    vi.mocked(adminApi.getAdminEvents).mockReset()
  })

  it('[AWU-EVENT-RN-004] 需實名活動該列顯示「需實名」文字，不需實名活動該列不顯示', async () => {
    vi.mocked(adminApi.getAdminEvents).mockResolvedValue(pagedEvents([
      buildEvent({ id: 'event-rn', title: '實名演唱會', isRealNameRequired: true }),
      buildEvent({ id: 'event-plain', title: '一般演唱會', isRealNameRequired: false }),
    ]))
    const wrapper = mountPage()
    await flushPromises()

    const rowFor = (title: string) => wrapper.findAll('.el-table__row').find((row) => row.text().includes(title))
    expect(rowFor('實名演唱會')?.text()).toContain('需實名')
    expect(rowFor('一般演唱會')).toBeDefined()
    expect(rowFor('一般演唱會')?.text()).not.toContain('需實名')
  })
})

describe('EventListPage 分頁（admin-event-list-pagination）', () => {
  const getList = () => vi.mocked(adminApi.getAdminEvents)

  beforeEach(() => {
    setActivePinia(createPinia())
    getList().mockReset()
    vi.mocked(adminApi.getVenues).mockReset()
    vi.mocked(adminApi.getVenues).mockResolvedValue([])
    vi.mocked(adminApi.createTicketType).mockReset()
  })

  function pageOf(page: number, totalCount = 45, count = 2) {
    return pagedEvents(
      Array.from({ length: count }, (_, index) => buildEvent({ id: `event-${page}-${index + 1}`, title: `Event ${page}-${index + 1}` })),
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

  function dropdownValues(wrapper: VueWrapper): string[] {
    return wrapper.findAll('select option').map((option) => (option.element as HTMLOptionElement).value).filter((value) => value !== '')
  }

  it('[AWU-EVENT-PAGE-001] 超過一頁時顯示總筆數與頁碼（無每頁筆數選擇器），點第 2 頁以 page=2 取代列表並在載入期間顯示載入中', async () => {
    getList().mockResolvedValueOnce(pageOf(1))
    const wrapper = mountPage()
    await flushPromises()

    expect(getList()).toHaveBeenLastCalledWith(1)
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

  it('[AWU-EVENT-PAGE-002] 筆數不超過一頁或沒有活動時不顯示分頁元件', async () => {
    getList().mockResolvedValueOnce(pagedEvents([], { totalCount: 0 }))
    const emptyWrapper = mountPage()
    await flushPromises()
    expect(emptyWrapper.find('.el-pagination').exists()).toBe(false)
    expect(emptyWrapper.text()).toContain('目前沒有活動')

    getList().mockResolvedValueOnce(pageOf(1, 20, 2))
    const singlePageWrapper = mountPage()
    await flushPromises()
    expect(rowTexts(singlePageWrapper)).toContain('Event 1-1')
    expect(singlePageWrapper.find('.el-pagination').exists()).toBe(false)
  })

  it('[AWU-EVENT-PAGE-003] 目前頁回傳空 items 但 totalCount > 0 時只重試一次改請求最後一頁，並同步分頁元件頁碼', async () => {
    getList()
      .mockResolvedValueOnce(pageOf(1))
      .mockResolvedValueOnce(pagedEvents([], { page: 2, totalCount: 45 }))
      .mockResolvedValueOnce(pageOf(3, 45, 1))
    const wrapper = mountPage()
    await flushPromises()

    await clickPage(wrapper, 2)
    await flushPromises()

    expect(getList().mock.calls.map((call) => call[0])).toEqual([1, 2, 3])
    expect(rowTexts(wrapper)).toContain('Event 3-1')
    expect(activePage(wrapper)).toBe('3')
  })

  it('[AWU-EVENT-PAGE-003] 最後一頁仍為空時不再重試（只重試一次）', async () => {
    getList()
      .mockResolvedValueOnce(pagedEvents([], { page: 1, totalCount: 45 }))
      .mockResolvedValueOnce(pagedEvents([], { page: 3, totalCount: 45 }))
    mountPage()
    await flushPromises()

    expect(getList().mock.calls.map((call) => call[0])).toEqual([1, 3])
  })

  it('[AWU-EVENT-PAGE-004] 換頁請求失敗時頁碼退回前一頁，列表保留前一頁資料並顯示錯誤提示', async () => {
    getList().mockResolvedValueOnce(pageOf(1)).mockRejectedValueOnce(new ApiError(500, { detail: '換頁失敗' }))
    const wrapper = mountPage()
    await flushPromises()

    await clickPage(wrapper, 2)
    await flushPromises()

    expect(activePage(wrapper)).toBe('1')
    expect(rowTexts(wrapper)).toContain('Event 1-1')
    expect(wrapper.text()).toContain('換頁失敗')
  })

  it('[AWU-EVENT-PAGE-005] 快速連續換頁時丟棄過期回應：第 3 頁先到、第 2 頁後到，列表顯示第 3 頁', async () => {
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

  it('[AWU-EVENT-PAGE-005] 過期請求的失敗回應被丟棄：第 2 頁較晚失敗時，不顯示錯誤、不改動第 3 頁的頁碼與列表', async () => {
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

  it('[AWU-EVENT-PAGE-006] 票種表單的活動下拉選單只列當頁活動，換頁後改列新頁活動', async () => {
    getList().mockResolvedValueOnce(pageOf(1)).mockResolvedValueOnce(pageOf(2))
    const wrapper = mountPageForTicketTypeForm()
    await flushPromises()
    expect(dropdownValues(wrapper)).toEqual(['event-1-1', 'event-1-2'])

    await clickPage(wrapper, 2)
    await flushPromises()

    expect(dropdownValues(wrapper)).toEqual(['event-2-1', 'event-2-2'])
  })

  it('[AWU-EVENT-PAGE-007] 換頁失敗後下拉選單仍列前一頁活動，已選活動維持', async () => {
    getList().mockResolvedValueOnce(pageOf(1)).mockRejectedValueOnce(new ApiError(500, { detail: '換頁失敗' }))
    const wrapper = mountPageForTicketTypeForm()
    await flushPromises()
    await wrapper.find('select').setValue('event-1-2')

    await clickPage(wrapper, 2)
    await flushPromises()

    expect(dropdownValues(wrapper)).toEqual(['event-1-1', 'event-1-2'])
    expect((wrapper.find('select').element as HTMLSelectElement).value).toBe('event-1-2')
  })

  it('[AWU-EVENT-PAGE-008] 換頁成功且已選活動不在新一頁時清除選取，且不會以該活動建立票種', async () => {
    getList().mockResolvedValueOnce(pageOf(1)).mockResolvedValueOnce(pageOf(2))
    const wrapper = mountPageForTicketTypeForm()
    await flushPromises()
    await wrapper.find('select').setValue('event-1-2')

    await clickPage(wrapper, 2)
    await flushPromises()

    expect(wrapper.findComponent(ElSelectStub).props('modelValue')).toBe('')
    expect((wrapper.find('select').element as HTMLSelectElement).value).toBe('')
    await wrapper.find('input[maxlength="50"]').setValue('VIP')
    await wrapper.find('form').trigger('submit')
    await flushPromises()
    expect(adminApi.createTicketType).not.toHaveBeenCalled()
  })

  it('[AWU-EVENT-PAGE-009] 換頁成功且已選活動仍在新一頁時保留選取', async () => {
    getList()
      .mockResolvedValueOnce(pageOf(1))
      .mockResolvedValueOnce(pagedEvents([buildEvent({ id: 'event-1-2', title: 'Event 1-2' }), buildEvent({ id: 'event-2-1', title: 'Event 2-1' })], { page: 2, totalCount: 45 }))
    const wrapper = mountPageForTicketTypeForm()
    await flushPromises()
    await wrapper.find('select').setValue('event-1-2')

    await clickPage(wrapper, 2)
    await flushPromises()

    expect((wrapper.find('select').element as HTMLSelectElement).value).toBe('event-1-2')
  })
})
