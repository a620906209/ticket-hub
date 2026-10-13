import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { mount, flushPromises } from '@vue/test-utils'
import { defineComponent } from 'vue'
import ElementPlus from 'element-plus'
import EventDetailPage from './EventDetailPage.vue'
import * as eventsApi from '../../api/events'
import * as ordersApi from '../../api/orders'
import * as queueApi from '../../api/queue'
import * as captchaApi from '../../api/captcha'
import * as membersApi from '../../api/members'
import QueueWaitingPanel from '../../components/QueueWaitingPanel.vue'
import { ApiError } from '../../api/httpClient'
import type { EventSeat, EventSummary, MemberProfile, QueueStatus, TicketType } from '../../types/apiResponses'

vi.mock('../../api/events')
vi.mock('../../api/orders')
vi.mock('../../api/queue')
vi.mock('../../api/captcha')
vi.mock('../../api/members')

const pushMock = vi.fn()
vi.mock('vue-router', () => ({
  useRoute: () => ({ params: { id: 'event-1' }, fullPath: '/events/event-1' }),
  useRouter: () => ({ push: pushMock }),
}))

let mockIsAuthenticated = true
vi.mock('../../stores/auth', () => ({
  useAuthStore: () => ({
    get isAuthenticated() {
      return mockIsAuthenticated
    },
  }),
}))

// ElInputNumber 的真實實作在 jsdom 下互動較繁瑣，這裡用行為等價的原生 number input 取代，
// 並自行實作限制型（限制輸入不超過 max）行為，比照 EventCreatePage.test.ts 對 ElSelect 的既有取捨。
const ElInputNumberStub = defineComponent({
  props: ['modelValue', 'min', 'max', 'disabled'],
  emits: ['update:modelValue', 'change'],
  template: `<input type="number" :value="modelValue" :min="min" :max="max" :disabled="disabled" @input="onInput" />`,
  methods: {
    onInput(event: Event) {
      let value = (event.target as HTMLInputElement).valueAsNumber
      if (Number.isNaN(value)) value = 0
      if (typeof this.max === 'number' && value > this.max) value = this.max
      if (typeof this.min === 'number' && value < this.min) value = this.min
      this.$emit('update:modelValue', value)
      this.$emit('change', value)
    },
  },
})
const ElSelectStub = {
  props: ['modelValue', 'disabled'],
  emits: ['update:modelValue', 'change'],
  template: `<select :value="modelValue" :disabled="disabled" @change="$emit('update:modelValue', $event.target.value); $emit('change', $event.target.value)">
    <slot />
  </select>`,
}
const ElOptionStub = {
  props: ['value', 'label'],
  template: `<option :value="value">{{ label }}</option>`,
}

function buildEvent(overrides: Partial<EventSummary> = {}): EventSummary {
  return {
    id: 'event-1',
    title: 'Concert',
    // 固定遠未來：販售狀態依本機時鐘推導，日期一過既有測試會全部變成「已停售」（event-sales-window-web-ui task 3.1）。
    startAtUtc: '2099-12-31T20:00:00Z',
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

function buildSeat(overrides: Partial<EventSeat> = {}): EventSeat {
  return { eventSeatId: 'seat-1', zoneCode: 'A', seatNumber: '1', status: 'Available', ...overrides }
}

function buildSeatTicketType(overrides: Partial<TicketType> = {}): TicketType {
  return { id: 'tt-seat-a', zoneCode: 'A', price: 1000, requiresSeat: true, availableQuantity: null, ...overrides }
}

function buildCountTicketType(overrides: Partial<TicketType> = {}): TicketType {
  return { id: 'tt-count-1', zoneCode: '站立區', price: 500, requiresSeat: false, availableQuantity: 10, ...overrides }
}

function buildQueueStatus(overrides: Partial<QueueStatus> = {}): QueueStatus {
  return { status: 'NotJoined', waitingCount: null, queueModeEnabled: true, ...overrides }
}

// 需要斷言焦點的測試才傳 attachTo：jsdom 只有掛在 document 上的元素能取得焦點。
function mountPage(options: { attachTo?: HTMLElement } = {}) {
  return mount(EventDetailPage, {
    attachTo: options.attachTo,
    global: {
      plugins: [ElementPlus],
      stubs: {
        ElInputNumber: ElInputNumberStub,
        ElSelect: ElSelectStub,
        ElOption: ElOptionStub,
        // vue-router 已整個 mock 掉，以 <a> 呈現 router-link 的目標，供斷言連結位置。
        RouterLink: { props: ['to'], template: '<a :href="to"><slot /></a>' },
      },
    },
  })
}

function submitButton(wrapper: ReturnType<typeof mount>) {
  const button = wrapper.findAll('button').find((b) => b.text() === '送出訂單')
  if (!button) throw new Error('找不到「送出訂單」按鈕')
  return button
}

function quickPickButton(wrapper: ReturnType<typeof mount>) {
  const button = wrapper.findAll('button').find((b) => b.text() === '自動選位')
  if (!button) throw new Error('找不到「自動選位」按鈕')
  return button
}

// 錯誤位置一律同時斷言「出現在應有的位置」與「不出現在另一個位置」（purchase-flow-ux design.md 決策 6）。
function actionError(wrapper: ReturnType<typeof mount>) {
  return wrapper.find('.checkout-bar .action-error')
}

function pageError(wrapper: ReturnType<typeof mount>) {
  return wrapper.find('.page-error')
}

function checkoutSeatList(wrapper: ReturnType<typeof mount>) {
  return wrapper.find('.checkout-seat-list')
}

function selectedSeatCount(wrapper: ReturnType<typeof mount>) {
  return wrapper.findAll('.seat-btn.selected').length
}

function countInputFor(wrapper: ReturnType<typeof mount>, ticketTypeName: string) {
  const row = wrapper.findAll('.count-ticket-row').find((r) => r.text().includes(ticketTypeName))
  if (!row) throw new Error(`找不到計數票種列：${ticketTypeName}`)
  return row.find('input[type="number"]')
}

describe('EventDetailPage 座位選購（既有行為的基礎測試覆蓋）', () => {
  beforeEach(() => {
    mockIsAuthenticated = true
    pushMock.mockReset()
    vi.mocked(eventsApi.getEvents).mockReset()
    vi.mocked(eventsApi.getEventSeats).mockReset()
    vi.mocked(eventsApi.getTicketTypes).mockReset()
    vi.mocked(ordersApi.placeOrder).mockReset()
  })

  it('選擇可售座位並成功下單', async () => {
    vi.mocked(eventsApi.getEvents).mockResolvedValue([buildEvent()])
    vi.mocked(eventsApi.getEventSeats).mockResolvedValue([buildSeat()])
    vi.mocked(eventsApi.getTicketTypes).mockResolvedValue([buildSeatTicketType()])
    vi.mocked(ordersApi.placeOrder).mockResolvedValue({ id: 'order-1' })
    const wrapper = mountPage()
    await flushPromises()

    await wrapper.find('.seat-btn').trigger('click')
    await submitButton(wrapper).trigger('click')
    await flushPromises()

    expect(ordersApi.placeOrder).toHaveBeenCalledWith([{ eventSeatId: 'seat-1', ticketTypeId: 'tt-seat-a' }])
    // 結果頁改由明細 API 取得伺服器端持有到期時間（order-pending-actions），導頁不得再帶前端推算的值。
    expect(pushMock).toHaveBeenCalledWith({ path: '/order-result/order-1' })
  })

  it('下單時座位已被搶先鎖定：顯示錯誤、清空已選座位、重新整理資料', async () => {
    vi.mocked(eventsApi.getEvents).mockResolvedValue([buildEvent()])
    vi.mocked(eventsApi.getEventSeats).mockResolvedValue([buildSeat()])
    vi.mocked(eventsApi.getTicketTypes).mockResolvedValue([buildSeatTicketType()])
    vi.mocked(ordersApi.placeOrder).mockRejectedValue(new Error('座位已被搶'))
    const wrapper = mountPage()
    await flushPromises()

    await wrapper.find('.seat-btn').trigger('click')
    await submitButton(wrapper).trigger('click')
    await flushPromises()

    expect(wrapper.text()).toContain('下單失敗')
    expect(wrapper.text()).toContain('已選 0 個座位')
    expect(eventsApi.getEvents).toHaveBeenCalledTimes(2)
  })

  it('下單時因換發失敗回傳 401：導向登入頁，保留已選座位/計數輸入，不重新呼叫查詢 API', async () => {
    // 活動詳情頁是公開頁（路由沒有 requiresAuth），App.vue 的全域 401 watcher 只在
    // requiresAuth/requiresAdmin 的路由才會導頁，不會處理這裡的登入失效，必須由元件自己
    // 攔截 401 並導向登入頁；且 401 分支不應執行 loadData()／clearSelections()——
    // 只驗證「有導頁、沒顯示下單失敗文字」不足以防止未來有人誤把這兩個呼叫放回 401 分支，
    // 必須直接斷言選購狀態還在、查詢 API 沒有被重新呼叫。
    vi.mocked(eventsApi.getEvents).mockResolvedValue([buildEvent()])
    vi.mocked(eventsApi.getEventSeats).mockResolvedValue([buildSeat()])
    vi.mocked(eventsApi.getTicketTypes).mockResolvedValue([buildSeatTicketType(), buildCountTicketType()])
    vi.mocked(ordersApi.placeOrder).mockRejectedValue(new ApiError(401, { detail: 'unauthorized' }))
    const wrapper = mountPage()
    await flushPromises()

    await wrapper.find('.seat-btn').trigger('click')
    await countInputFor(wrapper, '站立區').setValue(2)
    await submitButton(wrapper).trigger('click')
    await flushPromises()

    expect(pushMock).toHaveBeenCalledWith({ path: '/login', query: { redirect: '/events/event-1' } })
    expect(wrapper.text()).not.toContain('下單失敗')
    expect(wrapper.text()).toContain('已選 1 個座位、2 張計數票券')
    expect(eventsApi.getEvents).toHaveBeenCalledTimes(1)
    expect(eventsApi.getEventSeats).toHaveBeenCalledTimes(1)
    expect(eventsApi.getTicketTypes).toHaveBeenCalledTimes(1)
  })

  it('已選座位數達到每筆訂單限購張數，無法再選新座位', async () => {
    vi.mocked(eventsApi.getEvents).mockResolvedValue([buildEvent({ maxTicketsPerOrder: 1 })])
    vi.mocked(eventsApi.getEventSeats).mockResolvedValue([
      buildSeat({ eventSeatId: 'seat-1', seatNumber: '1' }),
      buildSeat({ eventSeatId: 'seat-2', seatNumber: '2' }),
    ])
    vi.mocked(eventsApi.getTicketTypes).mockResolvedValue([buildSeatTicketType()])
    const wrapper = mountPage()
    await flushPromises()

    const seatButtons = wrapper.findAll('.seat-btn')
    await seatButtons[0].trigger('click')
    await seatButtons[1].trigger('click')

    expect(wrapper.text()).toContain('這個活動每筆訂單最多購買 1 張')
    expect(wrapper.text()).toContain('已選 1 個座位')
  })

  it('活動未設定限購張數，可選任意數量座位', async () => {
    vi.mocked(eventsApi.getEvents).mockResolvedValue([buildEvent({ maxTicketsPerOrder: null })])
    vi.mocked(eventsApi.getEventSeats).mockResolvedValue([
      buildSeat({ eventSeatId: 'seat-1', seatNumber: '1' }),
      buildSeat({ eventSeatId: 'seat-2', seatNumber: '2' }),
    ])
    vi.mocked(eventsApi.getTicketTypes).mockResolvedValue([buildSeatTicketType()])
    const wrapper = mountPage()
    await flushPromises()

    const seatButtons = wrapper.findAll('.seat-btn')
    await seatButtons[0].trigger('click')
    await seatButtons[1].trigger('click')

    expect(wrapper.text()).toContain('已選 2 個座位')
  })

  // 自動選位只填入已選清單、不直接下單（purchase-flow-ux 決策 3）：買家要先看到抽到哪些座位，
  // 由買家自己按「送出訂單」才建立訂單，避免抽到不想要的座位就被持有。
  it('[BW-QUICK-001] 區域隨選：維持預設「全部區域」，抽出座位填入已選清單，由買家送出訂單', async () => {
    vi.mocked(eventsApi.getEvents).mockResolvedValue([buildEvent()])
    vi.mocked(eventsApi.getEventSeats).mockResolvedValue([
      buildSeat({ eventSeatId: 'seat-1', seatNumber: '1', zoneCode: 'A' }),
      buildSeat({ eventSeatId: 'seat-2', seatNumber: '2', zoneCode: 'B' }),
    ])
    vi.mocked(eventsApi.getTicketTypes).mockResolvedValue([
      buildSeatTicketType({ id: 'tt-a', zoneCode: 'A' }),
      buildSeatTicketType({ id: 'tt-b', zoneCode: 'B' }),
    ])
    vi.mocked(ordersApi.placeOrder).mockResolvedValue({ id: 'order-1' })
    const wrapper = mountPage()
    await flushPromises()

    await quickPickButton(wrapper).trigger('click')
    await flushPromises()

    expect(ordersApi.placeOrder).not.toHaveBeenCalled()
    expect(selectedSeatCount(wrapper)).toBe(1)
    expect(wrapper.text()).toContain('已選 1 個座位')
    // 抽選是隨機的，只能確認抽到兩區之一，且送出的就是已選清單上顯示的那一席。
    const seatIdByLabel: Record<string, string> = { 'A-1': 'seat-1', 'B-2': 'seat-2' }
    const pickedLabel = checkoutSeatList(wrapper).text()
    expect(Object.keys(seatIdByLabel)).toContain(pickedLabel)

    await submitButton(wrapper).trigger('click')
    await flushPromises()

    expect(ordersApi.placeOrder).toHaveBeenCalledOnce()
    const selections = vi.mocked(ordersApi.placeOrder).mock.calls[0][0]
    expect(selections.map((s) => s.eventSeatId)).toEqual([seatIdByLabel[pickedLabel]])
    expect(pushMock).toHaveBeenCalledWith(expect.objectContaining({ path: '/order-result/order-1' }))
  })

  it('[BW-QUICK-003] 自動選位後手動取消其中一個座位，送出的請求不包含該座位', async () => {
    vi.mocked(eventsApi.getEvents).mockResolvedValue([buildEvent()])
    vi.mocked(eventsApi.getEventSeats).mockResolvedValue([
      buildSeat({ eventSeatId: 'seat-1', seatNumber: '1' }),
      buildSeat({ eventSeatId: 'seat-2', seatNumber: '2' }),
      buildSeat({ eventSeatId: 'seat-3', seatNumber: '3' }),
    ])
    vi.mocked(eventsApi.getTicketTypes).mockResolvedValue([buildSeatTicketType()])
    vi.mocked(ordersApi.placeOrder).mockResolvedValue({ id: 'order-1' })
    const wrapper = mountPage()
    await flushPromises()

    await wrapper.find('.quick-pick input[type="number"]').setValue(2)
    await quickPickButton(wrapper).trigger('click')
    await flushPromises()
    expect(ordersApi.placeOrder).not.toHaveBeenCalled()
    expect(selectedSeatCount(wrapper)).toBe(2)

    const cancelledSeat = wrapper.findAll('.seat-btn.selected')[0]
    const cancelledSeatId = ['seat-1', 'seat-2', 'seat-3'][
      wrapper.findAll('.seat-btn').findIndex((b) => b.element === cancelledSeat.element)
    ]
    await cancelledSeat.trigger('click')
    expect(selectedSeatCount(wrapper)).toBe(1)

    await submitButton(wrapper).trigger('click')
    await flushPromises()

    expect(ordersApi.placeOrder).toHaveBeenCalledOnce()
    const selections = vi.mocked(ordersApi.placeOrder).mock.calls[0][0]
    expect(selections).toHaveLength(1)
    expect(selections.map((s) => s.eventSeatId)).not.toContain(cancelledSeatId)
  })

  it('[BW-QUICK-002] 區域隨選：指定單一分區時，只從該分區抽出座位，不會選到其他分區', async () => {
    vi.mocked(eventsApi.getEvents).mockResolvedValue([buildEvent()])
    vi.mocked(eventsApi.getEventSeats).mockResolvedValue([
      buildSeat({ eventSeatId: 'seat-a1', seatNumber: '1', zoneCode: 'A' }),
      buildSeat({ eventSeatId: 'seat-a2', seatNumber: '2', zoneCode: 'A' }),
      buildSeat({ eventSeatId: 'seat-b1', seatNumber: '1', zoneCode: 'B' }),
    ])
    vi.mocked(eventsApi.getTicketTypes).mockResolvedValue([
      buildSeatTicketType({ id: 'tt-a', zoneCode: 'A' }),
      buildSeatTicketType({ id: 'tt-b', zoneCode: 'B' }),
    ])
    vi.mocked(ordersApi.placeOrder).mockResolvedValue({ id: 'order-1' })
    const wrapper = mountPage()
    await flushPromises()

    await wrapper.find('.quick-pick select').setValue('A')
    await wrapper.find('.quick-pick input[type="number"]').setValue(2)
    await quickPickButton(wrapper).trigger('click')
    await flushPromises()

    expect(ordersApi.placeOrder).not.toHaveBeenCalled()
    expect(selectedSeatCount(wrapper)).toBe(2)
    expect(checkoutSeatList(wrapper).text()).toContain('A-1')
    expect(checkoutSeatList(wrapper).text()).toContain('A-2')
    expect(checkoutSeatList(wrapper).text()).not.toContain('B-')

    await submitButton(wrapper).trigger('click')
    await flushPromises()

    expect(ordersApi.placeOrder).toHaveBeenCalledOnce()
    const selections = vi.mocked(ordersApi.placeOrder).mock.calls[0][0]
    expect(selections).toHaveLength(2)
    expect(selections.every((s) => s.eventSeatId === 'seat-a1' || s.eventSeatId === 'seat-a2')).toBe(true)
    expect(pushMock).toHaveBeenCalledWith(expect.objectContaining({ path: '/order-result/order-1' }))
  })

  it('區域隨選：分區有可售座位但沒有對應的座位制票種時，不出現在分區選單與抽選池中，也不會縮減數量後下單', async () => {
    // B 區有座位，但故意不建立對應的票種——buildSelection() 對這種座位會回傳 null。
    // 若 zoneOptions／candidates 沒有排除這種分區，要求 2 張時仍可能只抽到 1 張有效座位就直接送出。
    vi.mocked(eventsApi.getEvents).mockResolvedValue([buildEvent()])
    vi.mocked(eventsApi.getEventSeats).mockResolvedValue([
      buildSeat({ eventSeatId: 'seat-a1', seatNumber: '1', zoneCode: 'A' }),
      buildSeat({ eventSeatId: 'seat-b1', seatNumber: '1', zoneCode: 'B' }),
    ])
    vi.mocked(eventsApi.getTicketTypes).mockResolvedValue([buildSeatTicketType({ id: 'tt-a', zoneCode: 'A' })])
    const wrapper = mountPage()
    await flushPromises()

    const zoneSelect = wrapper.find('.quick-pick select')
    const optionValues = zoneSelect.findAll('option').map((o) => o.attributes('value'))
    expect(optionValues).not.toContain('B')

    await wrapper.find('.quick-pick input[type="number"]').setValue(2)
    await quickPickButton(wrapper).trigger('click')
    await flushPromises()

    // 全部區域下候選只有 A 區這 1 個有效座位，要求 2 張應該擋下、不呼叫下單 API，
    // 不能只抽到 1 張（因為誤把 B 區座位也算進候選池）就直接送出。
    expect(ordersApi.placeOrder).not.toHaveBeenCalled()
    expect(wrapper.text()).toContain('目前沒有足夠的可售座位')
    expect(wrapper.text()).toContain('已選 0 個座位')
  })

  it('區域隨選張數超過可售座位或限購剩餘額度時，顯示錯誤、不呼叫下單 API', async () => {
    // 限購 1 張，先手動選滿額度，讓區域隨選當下的剩餘額度為 0，觸發「張數超過剩餘額度」分支。
    vi.mocked(eventsApi.getEvents).mockResolvedValue([buildEvent({ maxTicketsPerOrder: 1 })])
    vi.mocked(eventsApi.getEventSeats).mockResolvedValue([
      buildSeat({ eventSeatId: 'seat-1', seatNumber: '1' }),
      buildSeat({ eventSeatId: 'seat-2', seatNumber: '2' }),
    ])
    vi.mocked(eventsApi.getTicketTypes).mockResolvedValue([buildSeatTicketType()])
    const wrapper = mountPage()
    await flushPromises()

    await wrapper.findAll('.seat-btn')[0].trigger('click')
    await quickPickButton(wrapper).trigger('click')
    await flushPromises()

    expect(ordersApi.placeOrder).not.toHaveBeenCalled()
    expect(wrapper.text()).toContain('這個活動每筆訂單最多購買 1 張')
  })

  it('區域隨選要求數量超過可售座位數時（未設限購），顯示錯誤、不靜默縮減張數送出', async () => {
    // 沒有限購張數，只有 2 個可售座位，但要求 5 張——不能靜默縮成 2 張直接下單，
    // 買家要求的數量跟實際送出的訂單張數不一致是錯的，必須擋下並提示，完全不呼叫下單 API。
    vi.mocked(eventsApi.getEvents).mockResolvedValue([buildEvent({ maxTicketsPerOrder: null })])
    vi.mocked(eventsApi.getEventSeats).mockResolvedValue([
      buildSeat({ eventSeatId: 'seat-1', seatNumber: '1' }),
      buildSeat({ eventSeatId: 'seat-2', seatNumber: '2' }),
    ])
    vi.mocked(eventsApi.getTicketTypes).mockResolvedValue([buildSeatTicketType()])
    const wrapper = mountPage()
    await flushPromises()

    const quickPickCountInput = wrapper.find('.quick-pick input[type="number"]')
    await quickPickCountInput.setValue(5)
    await quickPickButton(wrapper).trigger('click')
    await flushPromises()

    expect(ordersApi.placeOrder).not.toHaveBeenCalled()
    expect(wrapper.text()).toContain('目前沒有足夠的可售座位')
    expect(wrapper.text()).toContain('已選 0 個座位')
  })

  it('[BW-QUICK-001] 連續使用自動選位：只從尚未選取的座位抽出，不會重複加入同一個座位', async () => {
    // 若候選集合沒有排除已選座位，第二次抽選可能抽到已選的座位，實際新增的張數就會少於買家要求的張數。
    vi.mocked(eventsApi.getEvents).mockResolvedValue([buildEvent({ maxTicketsPerOrder: null })])
    vi.mocked(eventsApi.getEventSeats).mockResolvedValue([
      buildSeat({ eventSeatId: 'seat-1', seatNumber: '1' }),
      buildSeat({ eventSeatId: 'seat-2', seatNumber: '2' }),
      buildSeat({ eventSeatId: 'seat-3', seatNumber: '3' }),
    ])
    vi.mocked(eventsApi.getTicketTypes).mockResolvedValue([buildSeatTicketType()])
    const wrapper = mountPage()
    await flushPromises()

    const quickPickCountInput = wrapper.find('.quick-pick input[type="number"]')
    await quickPickCountInput.setValue(2)
    await quickPickButton(wrapper).trigger('click')
    await flushPromises()
    expect(selectedSeatCount(wrapper)).toBe(2)

    await quickPickCountInput.setValue(1)
    await quickPickButton(wrapper).trigger('click')
    await flushPromises()

    expect(selectedSeatCount(wrapper)).toBe(3)
    expect(wrapper.text()).toContain('已選 3 個座位')
    expect(checkoutSeatList(wrapper).text().split('、').sort()).toEqual(['A-1', 'A-2', 'A-3'])

    await quickPickButton(wrapper).trigger('click')
    await flushPromises()

    expect(selectedSeatCount(wrapper)).toBe(3)
    expect(actionError(wrapper).text()).toContain('目前沒有足夠的可售座位')
    expect(ordersApi.placeOrder).not.toHaveBeenCalled()
  })

  it('[BW-FLOW-007] 手動點選座位成功不清除結帳列的既有錯誤（只有「自動選位」與「送出訂單」會清除）', async () => {
    vi.mocked(eventsApi.getEvents).mockResolvedValue([buildEvent({ maxTicketsPerOrder: null })])
    vi.mocked(eventsApi.getEventSeats).mockResolvedValue([
      buildSeat({ eventSeatId: 'seat-1', seatNumber: '1' }),
      buildSeat({ eventSeatId: 'seat-2', seatNumber: '2' }),
    ])
    vi.mocked(eventsApi.getTicketTypes).mockResolvedValue([buildSeatTicketType()])
    const wrapper = mountPage()
    await flushPromises()

    await wrapper.find('.quick-pick input[type="number"]').setValue(5)
    await quickPickButton(wrapper).trigger('click')
    await flushPromises()
    expect(actionError(wrapper).text()).toContain('目前沒有足夠的可售座位')

    await wrapper.findAll('.seat-btn')[0].trigger('click')
    await flushPromises()

    expect(selectedSeatCount(wrapper)).toBe(1)
    expect(actionError(wrapper).text()).toContain('目前沒有足夠的可售座位')
    expect(pageError(wrapper).exists()).toBe(false)
  })

  it('未登入使用區域隨選：導向登入頁，不進行任何選位或下單動作', async () => {
    mockIsAuthenticated = false
    vi.mocked(eventsApi.getEvents).mockResolvedValue([buildEvent()])
    vi.mocked(eventsApi.getEventSeats).mockResolvedValue([buildSeat()])
    vi.mocked(eventsApi.getTicketTypes).mockResolvedValue([buildSeatTicketType()])
    const wrapper = mountPage()
    await flushPromises()

    await quickPickButton(wrapper).trigger('click')
    await flushPromises()

    expect(pushMock).toHaveBeenCalledWith({ path: '/login', query: { redirect: '/events/event-1' } })
    expect(ordersApi.placeOrder).not.toHaveBeenCalled()
  })
})

describe('EventDetailPage 純計數票種購買（本次新增）', () => {
  beforeEach(() => {
    mockIsAuthenticated = true
    pushMock.mockReset()
    vi.mocked(eventsApi.getEvents).mockReset()
    vi.mocked(eventsApi.getEventSeats).mockReset()
    vi.mocked(eventsApi.getTicketTypes).mockReset()
    vi.mocked(ordersApi.placeOrder).mockReset()
    vi.mocked(eventsApi.getEventSeats).mockResolvedValue([])
  })

  it('未登入嘗試調整計數購買數量：立即導向登入頁，不套用該次變更', async () => {
    mockIsAuthenticated = false
    vi.mocked(eventsApi.getEvents).mockResolvedValue([buildEvent()])
    vi.mocked(eventsApi.getTicketTypes).mockResolvedValue([buildCountTicketType()])
    const wrapper = mountPage()
    await flushPromises()

    const input = countInputFor(wrapper, '站立區')
    await input.setValue(3)

    expect(pushMock).toHaveBeenCalledWith({ path: '/login', query: { redirect: '/events/event-1' } })
    // 用「已選 X 個座位、Y 張計數票券」摘要文字驗證 countQuantities 真的沒被寫入，
    // 不直接讀 stub 的 DOM value——Vue 對「值沒變」的 prop 不會強制重新 patch input.value，
    // 讀取被攔截元件自己的 DOM 屬性驗證不出真正有沒有寫入 state。
    expect(wrapper.text()).toContain('已選 0 個座位、0 張計數票券')
    expect(ordersApi.placeOrder).not.toHaveBeenCalled()
  })

  it('純計數票種輸入購買數量並成功下單', async () => {
    vi.mocked(eventsApi.getEvents).mockResolvedValue([buildEvent()])
    vi.mocked(eventsApi.getTicketTypes).mockResolvedValue([buildCountTicketType()])
    vi.mocked(ordersApi.placeOrder).mockResolvedValue({ id: 'order-1' })
    const wrapper = mountPage()
    await flushPromises()

    await countInputFor(wrapper, '站立區').setValue(3)
    await submitButton(wrapper).trigger('click')
    await flushPromises()

    expect(ordersApi.placeOrder).toHaveBeenCalledWith([{ eventSeatId: null, ticketTypeId: 'tt-count-1', quantity: 3 }])
    expect(pushMock).toHaveBeenCalledWith(expect.objectContaining({ path: '/order-result/order-1' }))
  })

  it('混合座位選購與純計數購買並成功下單', async () => {
    vi.mocked(eventsApi.getEvents).mockResolvedValue([buildEvent()])
    vi.mocked(eventsApi.getEventSeats).mockResolvedValue([buildSeat()])
    vi.mocked(eventsApi.getTicketTypes).mockResolvedValue([buildSeatTicketType(), buildCountTicketType()])
    vi.mocked(ordersApi.placeOrder).mockResolvedValue({ id: 'order-1' })
    const wrapper = mountPage()
    await flushPromises()

    await wrapper.find('.seat-btn').trigger('click')
    await countInputFor(wrapper, '站立區').setValue(2)
    await submitButton(wrapper).trigger('click')
    await flushPromises()

    expect(ordersApi.placeOrder).toHaveBeenCalledWith([
      { eventSeatId: 'seat-1', ticketTypeId: 'tt-seat-a' },
      { eventSeatId: null, ticketTypeId: 'tt-count-1', quantity: 2 },
    ])
  })

  it('純計數購買數量達到每筆訂單限購張數：輸入元件限制上限，不顯示提示訊息（限制型，與可售總量的互動模式一致）', async () => {
    vi.mocked(eventsApi.getEvents).mockResolvedValue([buildEvent({ maxTicketsPerOrder: 2 })])
    vi.mocked(eventsApi.getEventSeats).mockResolvedValue([buildSeat()])
    vi.mocked(eventsApi.getTicketTypes).mockResolvedValue([buildSeatTicketType(), buildCountTicketType()])
    const wrapper = mountPage()
    await flushPromises()

    await wrapper.find('.seat-btn').trigger('click') // 已選 1 張，剩餘額度 1
    const input = countInputFor(wrapper, '站立區')
    await input.setValue(5) // 嘗試超過剩餘額度

    expect((input.element as HTMLInputElement).value).toBe('1')
    expect(wrapper.text()).not.toContain('已達每筆訂單限購張數')
  })

  it('計數輸入元件限制數量不得超過可售總量（限制型，不呈現為錯誤狀態）', async () => {
    vi.mocked(eventsApi.getEvents).mockResolvedValue([buildEvent()])
    vi.mocked(eventsApi.getTicketTypes).mockResolvedValue([buildCountTicketType({ availableQuantity: 3 })])
    const wrapper = mountPage()
    await flushPromises()

    const input = countInputFor(wrapper, '站立區')
    await input.setValue(10)

    expect((input.element as HTMLInputElement).value).toBe('3')
    expect(wrapper.find('.el-form-item__error').exists()).toBe(false)
  })

  it('送出時因庫存已變動被後端拒絕：清空已選座位與計數輸入、重新整理資料', async () => {
    vi.mocked(eventsApi.getEvents).mockResolvedValue([buildEvent()])
    vi.mocked(eventsApi.getTicketTypes).mockResolvedValue([buildCountTicketType()])
    vi.mocked(ordersApi.placeOrder).mockRejectedValue(new Error('庫存已變動'))
    const wrapper = mountPage()
    await flushPromises()

    const input = countInputFor(wrapper, '站立區')
    await input.setValue(3)
    await submitButton(wrapper).trigger('click')
    await flushPromises()

    expect(wrapper.text()).toContain('下單失敗')
    expect((countInputFor(wrapper, '站立區').element as HTMLInputElement).value).toBe('0')
    expect(eventsApi.getTicketTypes).toHaveBeenCalledTimes(2)
  })

  it('純計數票種可售總量為 0：輸入框上限為 0，顯示「已售完」', async () => {
    vi.mocked(eventsApi.getEvents).mockResolvedValue([buildEvent()])
    vi.mocked(eventsApi.getTicketTypes).mockResolvedValue([buildCountTicketType({ availableQuantity: 0 })])
    const wrapper = mountPage()
    await flushPromises()

    const input = countInputFor(wrapper, '站立區')
    await input.setValue(5)

    expect(wrapper.text()).toContain('已選 0 個座位、0 張計數票券')
    expect(wrapper.text()).toContain('已售完')
  })

  it('活動未設定限購張數時，計數票種的輸入上限僅受可售總量限制', async () => {
    vi.mocked(eventsApi.getEvents).mockResolvedValue([buildEvent({ maxTicketsPerOrder: null })])
    vi.mocked(eventsApi.getTicketTypes).mockResolvedValue([buildCountTicketType({ availableQuantity: 5 })])
    const wrapper = mountPage()
    await flushPromises()

    const input = countInputFor(wrapper, '站立區')
    await input.setValue(100)

    expect((input.element as HTMLInputElement).value).toBe('5')
  })

  it('計數購買數量為 0 時不送出對應項目', async () => {
    vi.mocked(eventsApi.getEvents).mockResolvedValue([buildEvent()])
    vi.mocked(eventsApi.getTicketTypes).mockResolvedValue([
      buildCountTicketType({ id: 'tt-count-a', zoneCode: '站立區A' }),
      buildCountTicketType({ id: 'tt-count-b', zoneCode: '站立區B' }),
    ])
    vi.mocked(ordersApi.placeOrder).mockResolvedValue({ id: 'order-1' })
    const wrapper = mountPage()
    await flushPromises()

    await countInputFor(wrapper, '站立區A').setValue(2)
    // 站立區B 維持 0，不輸入
    await submitButton(wrapper).trigger('click')
    await flushPromises()

    expect(ordersApi.placeOrder).toHaveBeenCalledWith([{ eventSeatId: null, ticketTypeId: 'tt-count-a', quantity: 2 }])
  })

  it('送出訂單前偵測到合併總數超過限購張數：擋下送出、不呼叫下單 API', async () => {
    // 兩個計數票種在同一個同步批次內（未等待中間 re-render）分別輸入，
    // 模擬 design.md 決策 3 風險緩解描述的情境：個別輸入框當下的 max 還沒反映另一邊剛寫入的數量。
    vi.mocked(eventsApi.getEvents).mockResolvedValue([buildEvent({ maxTicketsPerOrder: 3 })])
    vi.mocked(eventsApi.getTicketTypes).mockResolvedValue([
      buildCountTicketType({ id: 'tt-count-a', zoneCode: '站立區A', availableQuantity: 10 }),
      buildCountTicketType({ id: 'tt-count-b', zoneCode: '站立區B', availableQuantity: 10 }),
    ])
    const wrapper = mountPage()
    await flushPromises()

    const inputA = countInputFor(wrapper, '站立區A').element as HTMLInputElement
    const inputB = countInputFor(wrapper, '站立區B').element as HTMLInputElement
    inputA.value = '2'
    inputA.dispatchEvent(new Event('input'))
    inputB.value = '2'
    inputB.dispatchEvent(new Event('input'))
    await flushPromises()

    await submitButton(wrapper).trigger('click')
    await flushPromises()

    expect(ordersApi.placeOrder).not.toHaveBeenCalled()
    expect(wrapper.text()).toContain('每筆訂單最多購買 3 張')
  })

  it('已輸入純計數購買數量時，區域隨選的剩餘額度隨之減少', async () => {
    vi.mocked(eventsApi.getEvents).mockResolvedValue([buildEvent({ maxTicketsPerOrder: 2 })])
    vi.mocked(eventsApi.getEventSeats).mockResolvedValue([
      buildSeat({ eventSeatId: 'seat-1', seatNumber: '1' }),
      buildSeat({ eventSeatId: 'seat-2', seatNumber: '2' }),
    ])
    vi.mocked(eventsApi.getTicketTypes).mockResolvedValue([buildSeatTicketType(), buildCountTicketType()])
    const wrapper = mountPage()
    await flushPromises()

    await countInputFor(wrapper, '站立區').setValue(2) // 用滿限購額度

    const quickPickCountInput = wrapper.find('.quick-pick input[type="number"]')
    await quickPickCountInput.setValue(1)
    await quickPickButton(wrapper).trigger('click')
    await flushPromises()

    expect(ordersApi.placeOrder).not.toHaveBeenCalled()
    expect(wrapper.text()).toContain('每筆訂單最多購買 2 張')
  })

  it('純計數票種不會出現在區域隨選的分區選單與抽選池中', async () => {
    vi.mocked(eventsApi.getEvents).mockResolvedValue([buildEvent()])
    vi.mocked(eventsApi.getEventSeats).mockResolvedValue([buildSeat({ zoneCode: 'A' })])
    vi.mocked(eventsApi.getTicketTypes).mockResolvedValue([
      buildSeatTicketType({ zoneCode: 'A' }),
      buildCountTicketType({ zoneCode: '站立區' }),
    ])
    const wrapper = mountPage()
    await flushPromises()

    const zoneSelect = wrapper.find('.quick-pick select')
    const optionValues = zoneSelect.findAll('option').map((o) => o.text())
    expect(optionValues.some((label) => label.includes('站立區'))).toBe(false)
  })

  it('純計數票種的名稱跟座位分區同名時，不會覆寫座位分區的票種對照，座位下單仍組出正確的座位制票種 Id', async () => {
    // 計數票種的 zoneCode 只是自由顯示名稱，這裡刻意跟座位分區同名（都是 'A'），
    // 且刻意排在陣列後面模擬「後蓋前」的順序，驗證座位選購不會被純計數票種的 id 覆寫掉。
    vi.mocked(eventsApi.getEvents).mockResolvedValue([buildEvent()])
    vi.mocked(eventsApi.getEventSeats).mockResolvedValue([buildSeat({ zoneCode: 'A' })])
    vi.mocked(eventsApi.getTicketTypes).mockResolvedValue([
      buildSeatTicketType({ id: 'tt-seat-a', zoneCode: 'A' }),
      buildCountTicketType({ id: 'tt-count-a', zoneCode: 'A' }),
    ])
    vi.mocked(ordersApi.placeOrder).mockResolvedValue({ id: 'order-1' })
    const wrapper = mountPage()
    await flushPromises()

    await wrapper.find('.seat-btn').trigger('click')
    await submitButton(wrapper).trigger('click')
    await flushPromises()

    expect(ordersApi.placeOrder).toHaveBeenCalledWith([{ eventSeatId: 'seat-1', ticketTypeId: 'tt-seat-a' }])
  })

  it('[BW-QUICK-008] 自動選位後與已輸入的計數購買合併送出', async () => {
    vi.mocked(eventsApi.getEvents).mockResolvedValue([buildEvent()])
    vi.mocked(eventsApi.getEventSeats).mockResolvedValue([buildSeat()])
    vi.mocked(eventsApi.getTicketTypes).mockResolvedValue([buildSeatTicketType(), buildCountTicketType()])
    vi.mocked(ordersApi.placeOrder).mockResolvedValue({ id: 'order-1' })
    const wrapper = mountPage()
    await flushPromises()

    await countInputFor(wrapper, '站立區').setValue(2)
    const quickPickCountInput = wrapper.find('.quick-pick input[type="number"]')
    await quickPickCountInput.setValue(1)
    await quickPickButton(wrapper).trigger('click')
    await flushPromises()

    expect(ordersApi.placeOrder).not.toHaveBeenCalled()
    expect(selectedSeatCount(wrapper)).toBe(1)
    expect(checkoutSeatList(wrapper).text()).toBe('A-1')
    expect(wrapper.text()).toContain('已選 1 個座位、2 張計數票券')

    await submitButton(wrapper).trigger('click')
    await flushPromises()

    expect(ordersApi.placeOrder).toHaveBeenCalledOnce()
    expect(ordersApi.placeOrder).toHaveBeenCalledWith([
      { eventSeatId: 'seat-1', ticketTypeId: 'tt-seat-a' },
      { eventSeatId: null, ticketTypeId: 'tt-count-1', quantity: 2 },
    ])
    expect(pushMock).toHaveBeenCalledWith(expect.objectContaining({ path: '/order-result/order-1' }))
  })

  it('已手動選取座位佔用額度後，計數購買輸入上限隨之減少', async () => {
    vi.mocked(eventsApi.getEvents).mockResolvedValue([buildEvent({ maxTicketsPerOrder: 2 })])
    vi.mocked(eventsApi.getEventSeats).mockResolvedValue([buildSeat()])
    vi.mocked(eventsApi.getTicketTypes).mockResolvedValue([
      buildSeatTicketType(),
      buildCountTicketType({ availableQuantity: 10 }),
    ])
    const wrapper = mountPage()
    await flushPromises()

    await wrapper.find('.seat-btn').trigger('click')
    const input = countInputFor(wrapper, '站立區')
    await input.setValue(5)

    expect((input.element as HTMLInputElement).value).toBe('1')
  })
})

describe('EventDetailPage 熱門搶購模式排隊（本次新增）', () => {
  function joinQueueButton(wrapper: ReturnType<typeof mount>) {
    const button = wrapper.findAll('button').find((b) => b.text() === '加入排隊')
    if (!button) throw new Error('找不到「加入排隊」按鈕')
    return button
  }

  // 加入排隊區塊裡唯一的純文字輸入欄位（驗證碼輸入），比照 LoginPage/RegisterPage 測試的既有選法。
  function captchaAnswerInput(wrapper: ReturnType<typeof mount>) {
    return wrapper.find('input[type="text"]')
  }

  beforeEach(() => {
    mockIsAuthenticated = true
    pushMock.mockReset()
    vi.mocked(eventsApi.getEvents).mockReset()
    vi.mocked(eventsApi.getEventSeats).mockReset()
    vi.mocked(eventsApi.getTicketTypes).mockReset()
    vi.mocked(ordersApi.placeOrder).mockReset()
    vi.mocked(queueApi.getMyQueueStatus).mockReset()
    vi.mocked(queueApi.joinQueue).mockReset()
    vi.mocked(captchaApi.getCaptcha).mockReset()
    vi.mocked(eventsApi.getEventSeats).mockResolvedValue([buildSeat()])
    vi.mocked(eventsApi.getTicketTypes).mockResolvedValue([buildSeatTicketType()])
    vi.mocked(captchaApi.getCaptcha).mockResolvedValue({ token: 'captcha-token-1', imageBase64: 'base64-image-1' })
  })

  afterEach(() => {
    vi.useRealTimers()
  })

  it('BW-QUEUE-001：進入熱門搶購模式活動且尚未加入排隊，顯示加入排隊操作、不開放選位', async () => {
    vi.mocked(eventsApi.getEvents).mockResolvedValue([buildEvent({ isQueueModeEnabled: true })])
    vi.mocked(queueApi.getMyQueueStatus)
      .mockResolvedValueOnce(buildQueueStatus({ status: 'NotJoined' }))
      .mockResolvedValueOnce(buildQueueStatus({ status: 'Waiting', waitingCount: 0 }))
    vi.mocked(queueApi.joinQueue).mockResolvedValue({ id: 'entry-1' })
    const wrapper = mountPage()
    await flushPromises()

    expect(wrapper.text()).toContain('加入排隊')
    expect(wrapper.find('.seat-btn').exists()).toBe(false)

    // 驗證碼為必填欄位，先填寫再點擊「加入排隊」，否則若前端做必填驗證會卡在原地送不出去（7.3a）。
    await captchaAnswerInput(wrapper).setValue('TEST')
    await joinQueueButton(wrapper).trigger('click')
    await flushPromises()

    expect(queueApi.joinQueue).toHaveBeenCalledWith('event-1', 'captcha-token-1', 'TEST')
    expect(wrapper.text()).toContain('排隊中')
  })

  // CAPTCHA-BW-QUEUE-001：尚未加入排隊時顯示驗證碼圖片與輸入欄位。
  it('CAPTCHA-BW-QUEUE-001：尚未加入排隊時顯示驗證碼圖片與輸入欄位', async () => {
    vi.mocked(eventsApi.getEvents).mockResolvedValue([buildEvent({ isQueueModeEnabled: true })])
    vi.mocked(queueApi.getMyQueueStatus).mockResolvedValue(buildQueueStatus({ status: 'NotJoined' }))
    const wrapper = mountPage()
    await flushPromises()

    expect(wrapper.find('img[alt="驗證碼圖片"]').exists()).toBe(true)
    expect(captchaAnswerInput(wrapper).exists()).toBe(true)
  })

  // CAPTCHA-BW-QUEUE-002：驗證碼錯誤時，MUST 同時滿足以下四項，缺一不可。
  it('CAPTCHA-BW-QUEUE-002：驗證碼錯誤時顯示提示、清空輸入並自動換發、停留原畫面、不開放選位', async () => {
    vi.mocked(eventsApi.getEvents).mockResolvedValue([buildEvent({ isQueueModeEnabled: true })])
    vi.mocked(queueApi.getMyQueueStatus).mockResolvedValue(buildQueueStatus({ status: 'NotJoined' }))
    vi.mocked(queueApi.joinQueue).mockRejectedValue(
      new ApiError(400, { status: 400, title: 'CaptchaInvalid', detail: '驗證碼錯誤或已逾時，請重新取得驗證碼。' }),
    )
    const wrapper = mountPage()
    await flushPromises()
    vi.mocked(captchaApi.getCaptcha).mockResolvedValue({ token: 'captcha-token-2', imageBase64: 'base64-image-2' })

    await captchaAnswerInput(wrapper).setValue('WRONG')
    await joinQueueButton(wrapper).trigger('click')
    await flushPromises()

    // ①顯示驗證碼錯誤提示
    expect(wrapper.text()).toContain('驗證碼錯誤或已逾時，請重新取得驗證碼。')
    // ②自動呼叫 GET /api/captcha 換取新驗證碼並清空舊有輸入欄位
    expect(captchaApi.getCaptcha).toHaveBeenCalledTimes(2)
    expect((captchaAnswerInput(wrapper).element as HTMLInputElement).value).toBe('')
    // ③停留在尚未加入排隊的畫面
    expect(wrapper.text()).toContain('加入排隊')
    // ④選位與計數購票操作入口仍不開放
    expect(wrapper.find('.seat-btn').exists()).toBe(false)
  })

  // CAPTCHA-BW-005／006：GET /api/captcha 載入或刷新失敗時顯示提示、停用加入排隊按鈕，
  // 不清空/重新整理座位與票種資料。
  it('GET /api/captcha 初次載入失敗時顯示提示、停用加入排隊按鈕，不重新整理座位票種資料', async () => {
    vi.mocked(eventsApi.getEvents).mockResolvedValue([buildEvent({ isQueueModeEnabled: true })])
    vi.mocked(queueApi.getMyQueueStatus).mockResolvedValue(buildQueueStatus({ status: 'NotJoined' }))
    vi.mocked(captchaApi.getCaptcha).mockReset()
    vi.mocked(captchaApi.getCaptcha).mockRejectedValue(new Error('network error'))
    const wrapper = mountPage()

    await flushPromises()

    expect(wrapper.text()).toContain('系統暫時無法取得驗證碼，請稍後再試')
    expect(joinQueueButton(wrapper).attributes('disabled')).toBeDefined()
    expect(eventsApi.getEventSeats).toHaveBeenCalledTimes(1)
    expect(eventsApi.getTicketTypes).toHaveBeenCalledTimes(1)
  })

  it('BW-QUEUE-002：排隊中顯示等待畫面與前方等待人數，停用選位/計數/區域隨選，並定期輪詢狀態', async () => {
    vi.useFakeTimers()
    vi.mocked(eventsApi.getEvents).mockResolvedValue([buildEvent({ isQueueModeEnabled: true })])
    vi.mocked(queueApi.getMyQueueStatus).mockResolvedValue(buildQueueStatus({ status: 'Waiting', waitingCount: 3 }))
    const wrapper = mountPage()
    await flushPromises()

    expect(wrapper.text()).toContain('排隊中')
    expect(wrapper.text()).toContain('目前前方還有 3 人等待')
    expect(wrapper.find('.seat-btn').exists()).toBe(false)
    expect(wrapper.findAll('button').some((b) => b.text() === '自動選位')).toBe(false)

    expect(queueApi.getMyQueueStatus).toHaveBeenCalledTimes(1)
    await vi.advanceTimersByTimeAsync(5000)
    expect(queueApi.getMyQueueStatus).toHaveBeenCalledTimes(2)
  })

  it('BW-QUEUE-003：排隊狀態由 Waiting 轉為 Admitted 後，停止輪詢並開放選位與下單', async () => {
    vi.useFakeTimers()
    vi.mocked(eventsApi.getEvents).mockResolvedValue([buildEvent({ isQueueModeEnabled: true })])
    vi.mocked(queueApi.getMyQueueStatus)
      .mockResolvedValueOnce(buildQueueStatus({ status: 'Waiting', waitingCount: 1 }))
      .mockResolvedValue(buildQueueStatus({ status: 'Admitted' }))
    const wrapper = mountPage()
    await flushPromises()
    expect(wrapper.text()).toContain('排隊中')

    await vi.advanceTimersByTimeAsync(5000)

    expect(wrapper.text()).not.toContain('排隊中')
    expect(wrapper.find('.seat-btn').exists()).toBe(true)

    const callCountAfterAdmitted = vi.mocked(queueApi.getMyQueueStatus).mock.calls.length
    await vi.advanceTimersByTimeAsync(10000)
    expect(queueApi.getMyQueueStatus).toHaveBeenCalledTimes(callCountAfterAdmitted)
  })

  it('BW-QUEUE-004：下單時排隊資格已逾時（403 + QueueAdmissionRequired），導回排隊等待畫面，不清空已選座位、不重新整理座位票種資料', async () => {
    vi.mocked(eventsApi.getEvents).mockResolvedValue([buildEvent({ isQueueModeEnabled: true })])
    vi.mocked(queueApi.getMyQueueStatus)
      .mockResolvedValueOnce(buildQueueStatus({ status: 'Admitted' }))
      .mockResolvedValueOnce(buildQueueStatus({ status: 'Waiting', waitingCount: 2 }))
    vi.mocked(ordersApi.placeOrder).mockRejectedValue(new ApiError(403, { status: 403, title: 'QueueAdmissionRequired' }))
    const wrapper = mountPage()
    await flushPromises()

    await wrapper.find('.seat-btn').trigger('click')
    await submitButton(wrapper).trigger('click')
    await flushPromises()

    expect(wrapper.text()).not.toContain('下單失敗')
    expect(wrapper.text()).toContain('排隊中')
    expect(eventsApi.getEventSeats).toHaveBeenCalledTimes(1)
    expect(eventsApi.getTicketTypes).toHaveBeenCalledTimes(1)
  })

  // 驗證碼為一次性：加入成功後若沿用舊 token，排隊資格逾時後重新加入必定被後端以 CaptchaInvalid 拒絕，
  // 使用者等於被迫多失敗一次。此測試確保重新出現加入畫面時一定換發新驗證碼、且不殘留上次輸入的答案。
  it('加入排隊成功後資格逾時再次出現加入畫面：換發新驗證碼、清空舊答案，重新加入使用新 token', async () => {
    vi.mocked(eventsApi.getEvents).mockResolvedValue([buildEvent({ isQueueModeEnabled: true })])
    vi.mocked(queueApi.getMyQueueStatus)
      .mockResolvedValueOnce(buildQueueStatus({ status: 'NotJoined' }))
      .mockResolvedValueOnce(buildQueueStatus({ status: 'Admitted' }))
      .mockResolvedValueOnce(buildQueueStatus({ status: 'Expired' }))
      .mockResolvedValueOnce(buildQueueStatus({ status: 'Waiting', waitingCount: 0 }))
    vi.mocked(queueApi.joinQueue).mockResolvedValue({ id: 'entry-1' })
    vi.mocked(ordersApi.placeOrder).mockRejectedValue(new ApiError(403, { status: 403, title: 'QueueAdmissionRequired' }))
    const wrapper = mountPage()
    await flushPromises()

    await captchaAnswerInput(wrapper).setValue('FIRST')
    await joinQueueButton(wrapper).trigger('click')
    await flushPromises()

    vi.mocked(captchaApi.getCaptcha).mockResolvedValue({ token: 'captcha-token-2', imageBase64: 'base64-image-2' })
    await wrapper.find('.seat-btn').trigger('click')
    await submitButton(wrapper).trigger('click')
    await flushPromises()

    expect(wrapper.find('img[alt="驗證碼圖片"]').attributes('src')).toContain('base64-image-2')
    expect((captchaAnswerInput(wrapper).element as HTMLInputElement).value).toBe('')

    await captchaAnswerInput(wrapper).setValue('SECOND')
    await joinQueueButton(wrapper).trigger('click')
    await flushPromises()

    expect(queueApi.joinQueue).toHaveBeenLastCalledWith('event-1', 'captcha-token-2', 'SECOND')
  })

  it('加入排隊成功但狀態仍為 NotJoined（加入畫面持續顯示）：立即換發新驗證碼並清空答案', async () => {
    vi.mocked(eventsApi.getEvents).mockResolvedValue([buildEvent({ isQueueModeEnabled: true })])
    vi.mocked(queueApi.getMyQueueStatus).mockResolvedValue(buildQueueStatus({ status: 'NotJoined' }))
    vi.mocked(queueApi.joinQueue).mockResolvedValue({ id: 'entry-1' })
    const wrapper = mountPage()
    await flushPromises()
    vi.mocked(captchaApi.getCaptcha).mockResolvedValue({ token: 'captcha-token-2', imageBase64: 'base64-image-2' })

    await captchaAnswerInput(wrapper).setValue('FIRST')
    await joinQueueButton(wrapper).trigger('click')
    await flushPromises()

    expect(captchaApi.getCaptcha).toHaveBeenCalledTimes(2)
    expect(wrapper.find('img[alt="驗證碼圖片"]').attributes('src')).toContain('base64-image-2')
    expect((captchaAnswerInput(wrapper).element as HTMLInputElement).value).toBe('')
  })

  // 後端可能在驗證碼已消耗後才因其他原因失敗，前端無從分辨，必須一律換發，否則下一次送出必定 CaptchaInvalid。
  it('加入排隊因非驗證碼錯誤失敗：顯示錯誤、換發新驗證碼並清空答案', async () => {
    vi.mocked(eventsApi.getEvents).mockResolvedValue([buildEvent({ isQueueModeEnabled: true })])
    vi.mocked(queueApi.getMyQueueStatus).mockResolvedValue(buildQueueStatus({ status: 'NotJoined' }))
    vi.mocked(queueApi.joinQueue).mockRejectedValue(
      new ApiError(409, { status: 409, title: 'Conflict', detail: '活動目前未開啟熱門搶購模式' }),
    )
    const wrapper = mountPage()
    await flushPromises()
    vi.mocked(captchaApi.getCaptcha).mockResolvedValue({ token: 'captcha-token-2', imageBase64: 'base64-image-2' })

    await captchaAnswerInput(wrapper).setValue('RIGHT')
    await joinQueueButton(wrapper).trigger('click')
    await flushPromises()

    expect(wrapper.text()).toContain('活動目前未開啟熱門搶購模式')
    expect(captchaApi.getCaptcha).toHaveBeenCalledTimes(2)
    expect((captchaAnswerInput(wrapper).element as HTMLInputElement).value).toBe('')
  })

  it('BW-QUEUE-005：下單因請求頻率限制被拒絕（429），顯示提示、不清空已選內容、不重新整理資料', async () => {
    vi.mocked(eventsApi.getEvents).mockResolvedValue([buildEvent({ isQueueModeEnabled: true })])
    vi.mocked(queueApi.getMyQueueStatus).mockResolvedValue(buildQueueStatus({ status: 'Admitted' }))
    vi.mocked(ordersApi.placeOrder).mockRejectedValue(new ApiError(429, { status: 429, title: 'TooManyRequests' }))
    const wrapper = mountPage()
    await flushPromises()

    await wrapper.find('.seat-btn').trigger('click')
    await submitButton(wrapper).trigger('click')
    await flushPromises()

    expect(wrapper.text()).toContain('請求過於頻繁')
    expect(wrapper.text()).toContain('已選 1 個座位')
    expect(eventsApi.getEventSeats).toHaveBeenCalledTimes(1)
  })

  it('BW-QUEUE-006：其他語意的 403（title 不是 QueueAdmissionRequired）沿用一般失敗處理，不導向排隊等待畫面', async () => {
    vi.mocked(eventsApi.getEvents).mockResolvedValue([buildEvent({ isQueueModeEnabled: true })])
    vi.mocked(queueApi.getMyQueueStatus).mockResolvedValue(buildQueueStatus({ status: 'Admitted' }))
    vi.mocked(ordersApi.placeOrder).mockRejectedValue(new ApiError(403, { status: 403, title: 'Forbidden', detail: '非本人操作' }))
    const wrapper = mountPage()
    await flushPromises()

    await wrapper.find('.seat-btn').trigger('click')
    await submitButton(wrapper).trigger('click')
    await flushPromises()

    // 沿用一般失敗處理：顯示錯誤（訊息內容取自 ApiError 本身，不是排隊專屬文案）、清空已選座位、重新整理資料。
    expect(wrapper.text()).toContain('非本人操作')
    expect(wrapper.text()).toContain('已選 0 個座位')
    expect(eventsApi.getEventSeats).toHaveBeenCalledTimes(2)
  })

  it('BW-TOGGLE-001：排隊等待期間 Queue Mode 被關閉，下一次輪詢的 queueModeEnabled 為 false 時，停止輪詢並開放正常購票', async () => {
    vi.useFakeTimers()
    vi.mocked(eventsApi.getEvents).mockResolvedValue([buildEvent({ isQueueModeEnabled: true })])
    vi.mocked(queueApi.getMyQueueStatus)
      .mockResolvedValueOnce(buildQueueStatus({ status: 'Waiting', waitingCount: 1, queueModeEnabled: true }))
      .mockResolvedValue(buildQueueStatus({ status: 'Waiting', waitingCount: 1, queueModeEnabled: false }))
    const wrapper = mountPage()
    await flushPromises()
    expect(wrapper.text()).toContain('排隊中')

    await vi.advanceTimersByTimeAsync(5000)

    expect(wrapper.text()).not.toContain('排隊中')
    expect(wrapper.find('.seat-btn').exists()).toBe(true)

    const callCountAfterDisabled = vi.mocked(queueApi.getMyQueueStatus).mock.calls.length
    await vi.advanceTimersByTimeAsync(10000)
    expect(queueApi.getMyQueueStatus).toHaveBeenCalledTimes(callCountAfterDisabled)
  })
})

afterEach(() => {
  vi.unstubAllGlobals()
})

describe('EventDetailPage 需實名活動的標示與登記引導（real-name-verification）', () => {
  const unregisteredProfile: MemberProfile = {
    id: 'member-1',
    email: 'buyer@example.com',
    displayName: '買家',
    role: 'Member',
    isActive: true,
    hasRegisteredRealName: false,
    realName: null,
    nationalIdLast4Masked: null,
  }
  const registeredProfile: MemberProfile = {
    ...unregisteredProfile,
    hasRegisteredRealName: true,
    realName: '王小明',
    nationalIdLast4Masked: '**34',
  }

  function joinQueueButton(wrapper: ReturnType<typeof mount>) {
    const button = wrapper.findAll('button').find((b) => b.text() === '加入排隊')
    if (!button) throw new Error('找不到「加入排隊」按鈕')
    return button
  }

  function registerLinkTarget(wrapper: ReturnType<typeof mount>): string | undefined {
    return wrapper.findAll('a').find((a) => a.text() === '前往登記')?.attributes('href')
  }

  beforeEach(() => {
    mockIsAuthenticated = true
    pushMock.mockReset()
    vi.mocked(eventsApi.getEvents).mockReset()
    vi.mocked(eventsApi.getEventSeats).mockReset().mockResolvedValue([buildSeat()])
    vi.mocked(eventsApi.getTicketTypes).mockReset().mockResolvedValue([buildSeatTicketType()])
    vi.mocked(ordersApi.placeOrder).mockReset()
    vi.mocked(queueApi.getMyQueueStatus).mockReset()
    vi.mocked(queueApi.joinQueue).mockReset()
    vi.mocked(captchaApi.getCaptcha).mockReset()
    vi.mocked(captchaApi.getCaptcha).mockResolvedValue({ token: 'captcha-token-1', imageBase64: 'base64-image-1' })
    vi.mocked(membersApi.getMyProfile).mockReset().mockResolvedValue(registeredProfile)
  })

  it('[BW-RN-EVENT-001] 需實名活動顯示「本活動需實名」文字標示', async () => {
    vi.mocked(eventsApi.getEvents).mockResolvedValue([buildEvent({ isRealNameRequired: true })])
    const wrapper = mountPage()
    await flushPromises()

    expect(wrapper.text()).toContain('本活動需實名')
  })

  it('[BW-RN-EVENT-001] 未登入者也看得到標示，但不查詢個人資料、不顯示登記提示', async () => {
    mockIsAuthenticated = false
    vi.mocked(eventsApi.getEvents).mockResolvedValue([buildEvent({ isRealNameRequired: true })])
    const wrapper = mountPage()
    await flushPromises()

    expect(wrapper.text()).toContain('本活動需實名')
    expect(wrapper.text()).not.toContain('購票前需先登記實名')
    expect(membersApi.getMyProfile).not.toHaveBeenCalled()
  })

  it('[BW-RN-EVENT-002] 不需實名活動不顯示標示與登記提示', async () => {
    vi.mocked(membersApi.getMyProfile).mockResolvedValue(unregisteredProfile)
    vi.mocked(eventsApi.getEvents).mockResolvedValue([buildEvent({ isRealNameRequired: false })])
    const wrapper = mountPage()
    await flushPromises()

    expect(wrapper.text()).not.toContain('本活動需實名')
    expect(wrapper.text()).not.toContain('購票前需先登記實名')
  })

  it('[BW-RN-EVENT-003] 未登記者看到登記引導，前往登記連結帶 redirect 指回此活動頁', async () => {
    vi.mocked(membersApi.getMyProfile).mockResolvedValue(unregisteredProfile)
    vi.mocked(eventsApi.getEvents).mockResolvedValue([buildEvent({ isRealNameRequired: true })])
    const wrapper = mountPage()
    await flushPromises()

    expect(wrapper.text()).toContain('購票前需先登記實名')
    expect(registerLinkTarget(wrapper)).toBe('/me/real-name?redirect=/events/event-1')
  })

  it('[BW-RN-EVENT-004] 已登記者看到標示、看不到登記提示', async () => {
    vi.mocked(eventsApi.getEvents).mockResolvedValue([buildEvent({ isRealNameRequired: true })])
    const wrapper = mountPage()
    await flushPromises()

    expect(membersApi.getMyProfile).toHaveBeenCalledTimes(1)
    expect(wrapper.text()).toContain('本活動需實名')
    expect(wrapper.text()).not.toContain('購票前需先登記實名')
  })

  it('[BW-RN-EVENT-005] 下單回 403 RealNameRequired 時顯示引導，不導向登入頁、不顯示泛用錯誤、保留已選座位', async () => {
    vi.mocked(eventsApi.getEvents).mockResolvedValue([buildEvent({ isRealNameRequired: true })])
    vi.mocked(ordersApi.placeOrder).mockRejectedValue(
      new ApiError(403, { status: 403, title: 'RealNameRequired', detail: '此活動需實名登記後才能購票。' }),
    )
    const wrapper = mountPage()
    await flushPromises()
    expect(wrapper.text()).not.toContain('購票前需先登記實名')

    await wrapper.find('.seat-btn').trigger('click')
    await submitButton(wrapper).trigger('click')
    await flushPromises()

    expect(wrapper.text()).toContain('購票前需先登記實名')
    expect(registerLinkTarget(wrapper)).toBe('/me/real-name?redirect=/events/event-1')
    expect(pushMock).not.toHaveBeenCalled()
    expect(wrapper.find('.el-alert--error').exists()).toBe(false)
    expect(wrapper.find('.seat-btn.selected').exists()).toBe(true)
  })

  it('[BW-RN-EVENT-006] 加入排隊回 403 RealNameRequired 時顯示引導，未進入排隊等待畫面並換發驗證碼', async () => {
    vi.mocked(eventsApi.getEvents).mockResolvedValue([buildEvent({ isRealNameRequired: true, isQueueModeEnabled: true })])
    vi.mocked(queueApi.getMyQueueStatus).mockResolvedValue(buildQueueStatus({ status: 'NotJoined' }))
    vi.mocked(queueApi.joinQueue).mockRejectedValue(
      new ApiError(403, { status: 403, title: 'RealNameRequired', detail: '此活動需實名登記後才能購票。' }),
    )
    const wrapper = mountPage()
    await flushPromises()

    await wrapper.find('input[placeholder="請輸入驗證碼"]').setValue('TEST')
    await joinQueueButton(wrapper).trigger('click')
    await flushPromises()

    expect(queueApi.joinQueue).toHaveBeenCalledTimes(1)
    expect(wrapper.text()).toContain('購票前需先登記實名')
    expect(registerLinkTarget(wrapper)).toBe('/me/real-name?redirect=/events/event-1')
    expect(wrapper.findComponent(QueueWaitingPanel).exists()).toBe(false)
    expect(queueApi.getMyQueueStatus).toHaveBeenCalledTimes(1)
    expect(wrapper.find('.el-alert--error').exists()).toBe(false)
    expect(pushMock).not.toHaveBeenCalled()
    expect(captchaApi.getCaptcha).toHaveBeenCalledTimes(2)
  })

  it('[BW-RN-EVENT-007] 個人資料查詢失敗時不顯示提示，送出訂單按鈕可用且會呼叫建立訂單 API', async () => {
    vi.mocked(membersApi.getMyProfile).mockRejectedValue(new ApiError(500, null))
    vi.mocked(eventsApi.getEvents).mockResolvedValue([buildEvent({ isRealNameRequired: true })])
    vi.mocked(ordersApi.placeOrder).mockResolvedValue({ id: 'order-1' })
    const wrapper = mountPage()
    await flushPromises()

    expect(wrapper.text()).not.toContain('購票前需先登記實名')
    expect(wrapper.find('.el-alert--error').exists()).toBe(false)
    await wrapper.find('.seat-btn').trigger('click')
    expect(submitButton(wrapper).attributes('disabled')).toBeUndefined()
    await submitButton(wrapper).trigger('click')
    await flushPromises()

    expect(ordersApi.placeOrder).toHaveBeenCalledTimes(1)
    expect(pushMock).toHaveBeenCalledWith({ path: '/order-result/order-1' })
  })

  it('未登記者仍可送出訂單，前端不自行擋下（以後端判斷為準）', async () => {
    vi.mocked(membersApi.getMyProfile).mockResolvedValue(unregisteredProfile)
    vi.mocked(eventsApi.getEvents).mockResolvedValue([buildEvent({ isRealNameRequired: true })])
    vi.mocked(ordersApi.placeOrder).mockResolvedValue({ id: 'order-1' })
    const wrapper = mountPage()
    await flushPromises()

    await wrapper.find('.seat-btn').trigger('click')
    await submitButton(wrapper).trigger('click')
    await flushPromises()

    expect(ordersApi.placeOrder).toHaveBeenCalledTimes(1)
  })

  it('403 但 title 不是 RealNameRequired 時不顯示實名引導', async () => {
    vi.mocked(eventsApi.getEvents).mockResolvedValue([buildEvent({ isRealNameRequired: true })])
    vi.mocked(ordersApi.placeOrder).mockRejectedValue(new ApiError(403, { status: 403, title: 'Forbidden' }))
    const wrapper = mountPage()
    await flushPromises()

    await wrapper.find('.seat-btn').trigger('click')
    await submitButton(wrapper).trigger('click')
    await flushPromises()

    expect(wrapper.text()).not.toContain('購票前需先登記實名')
    expect(wrapper.find('.el-alert--error').exists()).toBe(true)
  })
})

describe('EventDetailPage 販售期間（event-sales-window-web-ui）', () => {
  const NOW = new Date('2030-01-01T00:00:00.000Z').getTime()
  const iso = (ms: number) => new Date(ms).toISOString()
  const localized = (ms: number) => new Date(ms).toLocaleString()
  const SALES_START = NOW + 60_000
  const SALES_END = NOW + 120_000

  function notOpenEvent(overrides: Partial<EventSummary> = {}) {
    return buildEvent({ salesStartAtUtc: iso(SALES_START), ...overrides })
  }
  function closedEvent(overrides: Partial<EventSummary> = {}) {
    return buildEvent({ salesEndAtUtc: iso(NOW - 1000), ...overrides })
  }

  function salesTagText(wrapper: ReturnType<typeof mount>) {
    return wrapper.find('.sales-status').text()
  }
  function salesPrompt(wrapper: ReturnType<typeof mount>) {
    return wrapper.find('.purchase-column .sales-prompt')
  }
  function errorAlert(wrapper: ReturnType<typeof mount>) {
    return wrapper.find('.el-alert--error')
  }
  function firstElementAfterHeading(wrapper: ReturnType<typeof mount>) {
    const column = wrapper.find('.purchase-column').element
    expect(column.children[0].tagName).toBe('H2')
    return column.children[1]
  }
  function quickPickSelect(wrapper: ReturnType<typeof mount>) {
    return wrapper.find('.quick-pick select')
  }
  function quickPickCountInput(wrapper: ReturnType<typeof mount>) {
    return wrapper.find('.quick-pick input[type="number"]')
  }
  function joinQueueButton(wrapper: ReturnType<typeof mount>) {
    return wrapper.findAll('button').find((b) => b.text() === '加入排隊')
  }
  function captchaAnswerInput(wrapper: ReturnType<typeof mount>) {
    return wrapper.find('input[type="text"]')
  }
  function isDisabled(element: { attributes: (name: string) => string | undefined }) {
    return element.attributes('disabled') !== undefined
  }

  // 計數輸入、區域下拉、數量輸入、快速選位按鈕四者一併檢查，避免只鎖住其中幾個。
  function expectPurchaseControlsDisabled(wrapper: ReturnType<typeof mount>, expected: boolean) {
    expect(isDisabled(countInputFor(wrapper, '站立區'))).toBe(expected)
    expect(isDisabled(quickPickSelect(wrapper))).toBe(expected)
    expect(isDisabled(quickPickCountInput(wrapper))).toBe(expected)
    expect(isDisabled(quickPickButton(wrapper))).toBe(expected)
  }

  async function crossTo(ms: number) {
    vi.setSystemTime(ms)
    await vi.advanceTimersByTimeAsync(1000)
  }

  function salesWindowError(title: 'SalesNotOpen' | 'SalesClosed') {
    return new ApiError(409, { status: 409, title, detail: `Event 'event-1' english detail for ${title}.` })
  }

  function isFollowing(reference: Element, other: Element) {
    return (reference.compareDocumentPosition(other) & Node.DOCUMENT_POSITION_FOLLOWING) !== 0
  }

  beforeEach(() => {
    vi.useFakeTimers()
    vi.setSystemTime(NOW)
    mockIsAuthenticated = true
    pushMock.mockReset()
    vi.mocked(eventsApi.getEvents).mockReset()
    vi.mocked(eventsApi.getEventSeats).mockReset()
    vi.mocked(eventsApi.getTicketTypes).mockReset()
    vi.mocked(ordersApi.placeOrder).mockReset()
    vi.mocked(queueApi.getMyQueueStatus).mockReset()
    vi.mocked(queueApi.joinQueue).mockReset()
    vi.mocked(captchaApi.getCaptcha).mockReset()
    vi.mocked(eventsApi.getEventSeats).mockResolvedValue([
      buildSeat({ eventSeatId: 'seat-1', seatNumber: '1' }),
      buildSeat({ eventSeatId: 'seat-2', seatNumber: '2' }),
    ])
    vi.mocked(eventsApi.getTicketTypes).mockResolvedValue([buildSeatTicketType(), buildCountTicketType()])
    vi.mocked(ordersApi.placeOrder).mockResolvedValue({ id: 'order-1' } as Awaited<ReturnType<typeof ordersApi.placeOrder>>)
    vi.mocked(captchaApi.getCaptcha).mockResolvedValue({ token: 'captcha-token-1', imageBase64: 'base64-image-1' })
  })

  afterEach(() => {
    vi.useRealTimers()
    // 只在測試本體結尾還原的話，斷言失敗時會污染同檔其他測試（task 3.11）。
    delete (document as unknown as Record<string, unknown>).visibilityState
  })

  it('[BW-SALES-002] 詳情頁顯示開賣、停售時間與「販售中」', async () => {
    const salesStart = NOW - 3_600_000
    const salesEnd = NOW + 3_600_000
    vi.mocked(eventsApi.getEvents).mockResolvedValue([
      buildEvent({ salesStartAtUtc: iso(salesStart), salesEndAtUtc: iso(salesEnd) }),
    ])
    const wrapper = mountPage()
    await flushPromises()

    const salesWindow = wrapper.find('.sales-window').text()
    expect(salesWindow).toContain(localized(salesStart))
    expect(salesWindow).toContain(localized(salesEnd))
    expect(salesTagText(wrapper)).toBe('販售中')
  })

  it('[BW-SALES-003] 未設定販售期間顯示「即日起」至活動開始時間，可選位並送出', async () => {
    const event = buildEvent()
    vi.mocked(eventsApi.getEvents).mockResolvedValue([event])
    const wrapper = mountPage()
    await flushPromises()

    const salesWindow = wrapper.find('.sales-window').text()
    expect(salesWindow).toContain('即日起')
    expect(salesWindow).toContain(new Date(event.startAtUtc).toLocaleString())
    expect(salesTagText(wrapper)).toBe('販售中')

    await wrapper.findAll('.seat-btn')[0].trigger('click')
    await submitButton(wrapper).trigger('click')
    await flushPromises()
    expect(ordersApi.placeOrder).toHaveBeenCalledTimes(1)
  })

  it.each([
    ['BW-SALES-004', '尚未開賣', () => notOpenEvent(), `尚未開賣，將於 ${localized(SALES_START)} 開放購票`],
    ['BW-SALES-005', '已停售', () => closedEvent(), '本活動已停售'],
  ])('[%s] %s時座位圖仍顯示但停用全部購票操作', async (_id, tagText, buildFixture, promptText) => {
    vi.mocked(eventsApi.getEvents).mockResolvedValue([buildFixture()])
    const wrapper = mountPage()
    await flushPromises()

    expect(salesTagText(wrapper)).toBe(tagText)
    expect(wrapper.findAll('.seat-btn')).toHaveLength(2)
    expect(wrapper.find('.seat-grid').classes()).toContain('seat-grid--locked')

    await wrapper.findAll('.seat-btn')[0].trigger('click')
    expect(wrapper.find('.seat-btn.selected').exists()).toBe(false)

    expectPurchaseControlsDisabled(wrapper, true)
    await quickPickButton(wrapper).trigger('click')
    await flushPromises()
    expect(ordersApi.placeOrder).not.toHaveBeenCalled()
    expect(isDisabled(submitButton(wrapper))).toBe(true)

    expect(salesPrompt(wrapper).text()).toContain(promptText)
  })

  it('[BW-SALES-006] 尚未開賣的熱門搶購活動不顯示加入排隊畫面、不呼叫驗證碼 API', async () => {
    vi.mocked(eventsApi.getEvents).mockResolvedValue([notOpenEvent({ isQueueModeEnabled: true })])
    vi.mocked(queueApi.getMyQueueStatus).mockResolvedValue(buildQueueStatus({ status: 'NotJoined' }))
    const wrapper = mountPage()
    await flushPromises()

    expect(joinQueueButton(wrapper)).toBeUndefined()
    expect(captchaApi.getCaptcha).not.toHaveBeenCalled()
    expect(salesPrompt(wrapper).text()).toContain(`尚未開賣，將於 ${localized(SALES_START)} 開放購票`)
  })

  it('[BW-SALES-007] 停留到開賣時間自動解鎖，手動選位後可送出訂單', async () => {
    vi.mocked(eventsApi.getEvents).mockResolvedValue([notOpenEvent()])
    const wrapper = mountPage()
    await flushPromises()
    expect(salesTagText(wrapper)).toBe('尚未開賣')

    await crossTo(SALES_START)

    expect(salesTagText(wrapper)).toBe('販售中')
    expectPurchaseControlsDisabled(wrapper, false)
    await wrapper.findAll('.seat-btn')[0].trigger('click')
    expect(wrapper.findAll('.seat-btn')[0].classes()).toContain('selected')
    await submitButton(wrapper).trigger('click')
    await flushPromises()
    expect(ordersApi.placeOrder).toHaveBeenCalledTimes(1)
    expect(ordersApi.placeOrder).toHaveBeenCalledWith([{ eventSeatId: 'seat-1', ticketTypeId: 'tt-seat-a' }])
  })

  it('[BW-SALES-007] 熱門搶購活動跨過開賣才載入驗證碼並顯示加入排隊畫面', async () => {
    vi.mocked(eventsApi.getEvents).mockResolvedValue([notOpenEvent({ isQueueModeEnabled: true })])
    vi.mocked(queueApi.getMyQueueStatus).mockResolvedValue(buildQueueStatus({ status: 'NotJoined' }))
    const wrapper = mountPage()
    await flushPromises()
    expect(captchaApi.getCaptcha).toHaveBeenCalledTimes(0)

    await crossTo(SALES_START)
    await flushPromises()

    expect(captchaApi.getCaptcha).toHaveBeenCalledTimes(1)
    expect(joinQueueButton(wrapper)).toBeDefined()
    expect(salesTagText(wrapper)).toBe('販售中')
  })

  it('[BW-SALES-017] 跨過開賣時「尚未開賣」提示原位改為「現在開放購票」', async () => {
    vi.mocked(eventsApi.getEvents).mockResolvedValue([notOpenEvent()])
    const wrapper = mountPage()
    await flushPromises()
    const promptBefore = firstElementAfterHeading(wrapper)
    expect(promptBefore.textContent).toContain('尚未開賣')

    await crossTo(SALES_START)

    const promptAfter = firstElementAfterHeading(wrapper)
    expect(promptAfter).toBe(promptBefore)
    expect(promptAfter.textContent).toContain('現在開放購票')
    expect(salesTagText(wrapper)).toBe('販售中')
    expectPurchaseControlsDisabled(wrapper, false)
  })

  it('[BW-SALES-017] 未登入者跨過開賣同樣顯示「現在開放購票」，且排在「請先登入」之前', async () => {
    mockIsAuthenticated = false
    vi.mocked(eventsApi.getEvents).mockResolvedValue([notOpenEvent()])
    const wrapper = mountPage()
    await flushPromises()

    await crossTo(SALES_START)

    const prompt = firstElementAfterHeading(wrapper)
    expect(prompt.textContent).toContain('現在開放購票')
    const loginAlert = wrapper.findAll('.purchase-column .el-alert').find((a) => a.text().includes('請先登入'))
    expect(loginAlert).toBeDefined()
    expect(isFollowing(prompt, loginAlert!.element)).toBe(true)
  })

  it('[BW-SALES-017] 熱門搶購活動跨過開賣後「現在開放購票」位於加入排隊畫面上方', async () => {
    vi.mocked(eventsApi.getEvents).mockResolvedValue([notOpenEvent({ isQueueModeEnabled: true })])
    vi.mocked(queueApi.getMyQueueStatus).mockResolvedValue(buildQueueStatus({ status: 'NotJoined' }))
    const wrapper = mountPage()
    await flushPromises()

    await crossTo(SALES_START)
    await flushPromises()

    const prompt = firstElementAfterHeading(wrapper)
    expect(prompt.textContent).toContain('現在開放購票')
    const queueWarning = wrapper.findAll('.purchase-column .el-alert').find((a) => a.text().includes('請先加入排隊'))
    expect(queueWarning).toBeDefined()
    expect(isFollowing(prompt, queueWarning!.element)).toBe(true)
  })

  it('[BW-SALES-017] 載入時即為販售中的活動不顯示「現在開放購票」', async () => {
    vi.mocked(eventsApi.getEvents).mockResolvedValue([buildEvent()])
    const wrapper = mountPage()
    await flushPromises()
    await vi.advanceTimersByTimeAsync(3000)

    expect(salesPrompt(wrapper).exists()).toBe(false)
    expect(wrapper.text()).not.toContain('現在開放購票')
  })

  it('[BW-SALES-018] 停留期間依序跨過開賣與停售，提示與標籤同步變化', async () => {
    vi.mocked(eventsApi.getEvents).mockResolvedValue([notOpenEvent({ salesEndAtUtc: iso(SALES_END) })])
    const wrapper = mountPage()
    await flushPromises()
    expect(salesPrompt(wrapper).text()).toContain('尚未開賣')
    expect(salesTagText(wrapper)).toBe('尚未開賣')

    await crossTo(SALES_START)
    expect(salesPrompt(wrapper).text()).toContain('現在開放購票')
    expect(salesTagText(wrapper)).toBe('販售中')

    await wrapper.findAll('.seat-btn')[0].trigger('click')
    await crossTo(SALES_END)
    expect(salesPrompt(wrapper).text()).toContain('本活動已停售')
    expect(salesTagText(wrapper)).toBe('已停售')
    expect(isDisabled(submitButton(wrapper))).toBe(true)
  })

  it('[BW-SALES-019] 熱門搶購開賣後排隊等待時隱藏「現在開放購票」，放行後重新顯示並可下單', async () => {
    vi.mocked(eventsApi.getEvents).mockResolvedValue([notOpenEvent({ isQueueModeEnabled: true })])
    vi.mocked(queueApi.getMyQueueStatus).mockResolvedValue(buildQueueStatus({ status: 'NotJoined' }))
    vi.mocked(queueApi.joinQueue).mockResolvedValue({ id: 'entry-1' })
    const wrapper = mountPage()
    await flushPromises()

    await crossTo(SALES_START)
    await flushPromises()
    expect(salesPrompt(wrapper).text()).toContain('現在開放購票')

    vi.mocked(queueApi.getMyQueueStatus).mockResolvedValue(buildQueueStatus({ status: 'Waiting', waitingCount: 2 }))
    await captchaAnswerInput(wrapper).setValue('TEST')
    await joinQueueButton(wrapper)!.trigger('click')
    await flushPromises()
    expect(wrapper.findComponent(QueueWaitingPanel).exists()).toBe(true)
    expect(wrapper.text()).not.toContain('現在開放購票')

    vi.mocked(queueApi.getMyQueueStatus).mockResolvedValue(buildQueueStatus({ status: 'Admitted' }))
    await vi.advanceTimersByTimeAsync(5000)
    await flushPromises()

    const prompt = firstElementAfterHeading(wrapper)
    expect(prompt.textContent).toContain('現在開放購票')
    await wrapper.findAll('.seat-btn')[0].trigger('click')
    await submitButton(wrapper).trigger('click')
    await flushPromises()
    expect(ordersApi.placeOrder).toHaveBeenCalledTimes(1)
  })

  it('[BW-SALES-008] 停留到停售時間自動停用，跨越前已選的座位不會送出', async () => {
    vi.mocked(eventsApi.getEvents).mockResolvedValue([buildEvent({ salesEndAtUtc: iso(SALES_END) })])
    const wrapper = mountPage()
    await flushPromises()
    expect(salesTagText(wrapper)).toBe('販售中')
    await wrapper.findAll('.seat-btn')[0].trigger('click')

    await crossTo(SALES_END)

    expect(salesTagText(wrapper)).toBe('已停售')
    expect(salesPrompt(wrapper).text()).toContain('本活動已停售')
    expect(isDisabled(submitButton(wrapper))).toBe(true)
    expectPurchaseControlsDisabled(wrapper, true)
    await quickPickButton(wrapper).trigger('click')
    await submitButton(wrapper).trigger('click')
    await flushPromises()
    expect(ordersApi.placeOrder).toHaveBeenCalledTimes(0)
  })

  it('[BW-SALES-009] 分頁切回前景時立即重算，不等下一次定時更新', async () => {
    vi.mocked(eventsApi.getEvents).mockResolvedValue([notOpenEvent()])
    const wrapper = mountPage()
    await flushPromises()
    expect(salesTagText(wrapper)).toBe('尚未開賣')

    vi.setSystemTime(SALES_START)
    Object.defineProperty(document, 'visibilityState', { configurable: true, value: 'visible' })
    document.dispatchEvent(new Event('visibilitychange'))
    await wrapper.vm.$nextTick()

    expect(salesTagText(wrapper)).toBe('販售中')
    expectPurchaseControlsDisabled(wrapper, false)
    await wrapper.findAll('.seat-btn')[0].trigger('click')
    expect(wrapper.findAll('.seat-btn')[0].classes()).toContain('selected')
  })

  it('[BW-SALES-009] 分頁仍在背景（hidden）時不提前重算', async () => {
    vi.mocked(eventsApi.getEvents).mockResolvedValue([notOpenEvent()])
    const wrapper = mountPage()
    await flushPromises()

    vi.setSystemTime(SALES_START)
    Object.defineProperty(document, 'visibilityState', { configurable: true, value: 'hidden' })
    document.dispatchEvent(new Event('visibilitychange'))
    await wrapper.vm.$nextTick()

    expect(salesTagText(wrapper)).toBe('尚未開賣')
  })

  async function mountWithSeatAndCountSelected() {
    vi.mocked(eventsApi.getEvents).mockResolvedValue([buildEvent()])
    const wrapper = mountPage()
    await flushPromises()
    await wrapper.findAll('.seat-btn')[0].trigger('click')
    await countInputFor(wrapper, '站立區').setValue(2)
    return wrapper
  }

  function expectNoReload() {
    expect(eventsApi.getEvents).toHaveBeenCalledTimes(1)
    expect(eventsApi.getEventSeats).toHaveBeenCalledTimes(1)
    expect(eventsApi.getTicketTypes).toHaveBeenCalledTimes(1)
  }

  it('[BW-SALES-010] 下單收到 SalesNotOpen：中文訊息、保留選擇、不重新載入、仍可重試', async () => {
    const wrapper = await mountWithSeatAndCountSelected()
    vi.mocked(ordersApi.placeOrder).mockRejectedValue(salesWindowError('SalesNotOpen'))

    await submitButton(wrapper).trigger('click')
    await flushPromises()

    expect(errorAlert(wrapper).text()).toContain('尚未開賣，請於開賣時間後再試')
    expect(wrapper.text()).not.toContain('english detail')
    expect(wrapper.findAll('.seat-btn')[0].classes()).toContain('selected')
    expect((countInputFor(wrapper, '站立區').element as HTMLInputElement).value).toBe('2')
    expectNoReload()
    expect(salesTagText(wrapper)).toBe('販售中')
    expect(isDisabled(submitButton(wrapper))).toBe(false)

    await submitButton(wrapper).trigger('click')
    await flushPromises()
    expect(ordersApi.placeOrder).toHaveBeenCalledTimes(2)
  })

  it('[BW-SALES-010] 快速選位路徑收到 SalesNotOpen 時保留抽出的座位', async () => {
    vi.mocked(eventsApi.getEvents).mockResolvedValue([buildEvent()])
    vi.mocked(ordersApi.placeOrder).mockRejectedValue(salesWindowError('SalesNotOpen'))
    const wrapper = mountPage()
    await flushPromises()

    await quickPickButton(wrapper).trigger('click')
    await flushPromises()
    expect(ordersApi.placeOrder).not.toHaveBeenCalled()

    await submitButton(wrapper).trigger('click')
    await flushPromises()

    expect(ordersApi.placeOrder).toHaveBeenCalledTimes(1)
    expect(wrapper.findAll('.seat-btn.selected')).toHaveLength(1)
    expect(actionError(wrapper).text()).toContain('尚未開賣，請於開賣時間後再試')
    expect(pageError(wrapper).exists()).toBe(false)
    expectNoReload()
  })

  it('[BW-SALES-011] 下單收到 SalesClosed：中文訊息、保留選擇、不重新載入、頁面轉為已停售', async () => {
    const wrapper = await mountWithSeatAndCountSelected()
    vi.mocked(ordersApi.placeOrder).mockRejectedValue(salesWindowError('SalesClosed'))

    await submitButton(wrapper).trigger('click')
    await flushPromises()

    // 已停售只在販售提示顯示一次；若又設 errorMessage，畫面會重複出現同一句。
    expect(errorAlert(wrapper).exists()).toBe(false)
    expect(salesPrompt(wrapper).text()).toContain('本活動已停售')
    expect(wrapper.text()).not.toContain('english detail')
    expect(wrapper.findAll('.seat-btn')[0].classes()).toContain('selected')
    expect((countInputFor(wrapper, '站立區').element as HTMLInputElement).value).toBe('2')
    expectNoReload()
    expect(salesTagText(wrapper)).toBe('已停售')
    expect(isDisabled(submitButton(wrapper))).toBe(true)
    expectPurchaseControlsDisabled(wrapper, true)
  })

  it('[BW-SALES-011] 快速選位路徑收到 SalesClosed 時保留抽出的座位', async () => {
    vi.mocked(eventsApi.getEvents).mockResolvedValue([buildEvent()])
    vi.mocked(ordersApi.placeOrder).mockRejectedValue(salesWindowError('SalesClosed'))
    const wrapper = mountPage()
    await flushPromises()

    await quickPickButton(wrapper).trigger('click')
    await flushPromises()
    expect(ordersApi.placeOrder).not.toHaveBeenCalled()

    await submitButton(wrapper).trigger('click')
    await flushPromises()

    expect(ordersApi.placeOrder).toHaveBeenCalledTimes(1)
    expect(wrapper.findAll('.seat-btn.selected')).toHaveLength(1)
    expect(salesTagText(wrapper)).toBe('已停售')
    expect(errorAlert(wrapper).exists()).toBe(false)
    expectNoReload()
  })

  async function mountJoinPromptAndSubmit(title: 'SalesNotOpen' | 'SalesClosed') {
    vi.mocked(eventsApi.getEvents).mockResolvedValue([buildEvent({ isQueueModeEnabled: true })])
    vi.mocked(queueApi.getMyQueueStatus).mockResolvedValue(buildQueueStatus({ status: 'NotJoined' }))
    vi.mocked(queueApi.joinQueue).mockRejectedValue(salesWindowError(title))
    const wrapper = mountPage()
    await flushPromises()
    expect(captchaApi.getCaptcha).toHaveBeenCalledTimes(1)

    await captchaAnswerInput(wrapper).setValue('TEST')
    await joinQueueButton(wrapper)!.trigger('click')
    await flushPromises()
    return wrapper
  }

  it('[BW-SALES-012] 加入排隊收到 SalesNotOpen：中文訊息、清空並換發驗證碼、停留加入畫面', async () => {
    const wrapper = await mountJoinPromptAndSubmit('SalesNotOpen')

    expect(errorAlert(wrapper).text()).toContain('尚未開賣，請於開賣時間後再試')
    expect(wrapper.text()).not.toContain('english detail')
    expect((captchaAnswerInput(wrapper).element as HTMLInputElement).value).toBe('')
    expect(captchaApi.getCaptcha).toHaveBeenCalledTimes(2)
    expect(joinQueueButton(wrapper)).toBeDefined()
    expect(wrapper.findComponent(QueueWaitingPanel).exists()).toBe(false)
  })

  it('[BW-SALES-014] 加入排隊收到 SalesClosed：轉為已停售、隱藏加入畫面、不再換發驗證碼', async () => {
    const wrapper = await mountJoinPromptAndSubmit('SalesClosed')
    await vi.advanceTimersByTimeAsync(3000)

    // 已停售只在販售提示顯示一次；若又設 errorMessage，畫面會重複出現同一句。
    expect(errorAlert(wrapper).exists()).toBe(false)
    expect(salesPrompt(wrapper).text()).toContain('本活動已停售')
    expect(salesTagText(wrapper)).toBe('已停售')
    expect(joinQueueButton(wrapper)).toBeUndefined()
    expect(captchaApi.getCaptcha).toHaveBeenCalledTimes(1)
    expect(wrapper.findComponent(QueueWaitingPanel).exists()).toBe(false)
  })

  it.each([
    ['尚未開賣', () => notOpenEvent(), '尚未開賣，將於'],
    ['已停售', () => closedEvent(), '本活動已停售'],
  ])('[BW-SALES-015] 未登入者在%s時控制項不停用，操作皆導向登入頁', async (_label, buildFixture, promptText) => {
    mockIsAuthenticated = false
    vi.mocked(eventsApi.getEvents).mockResolvedValue([buildFixture()])
    const wrapper = mountPage()
    await flushPromises()

    const prompt = firstElementAfterHeading(wrapper)
    expect(prompt.textContent).toContain(promptText)
    const loginAlert = wrapper.findAll('.purchase-column .el-alert').find((a) => a.text().includes('請先登入'))
    expect(loginAlert).toBeDefined()
    expect(isFollowing(prompt, loginAlert!.element)).toBe(true)

    expectPurchaseControlsDisabled(wrapper, false)
    expect(wrapper.find('.seat-grid').classes()).not.toContain('seat-grid--locked')

    const loginRedirect = { path: '/login', query: { redirect: '/events/event-1' } }
    await wrapper.findAll('.seat-btn')[0].trigger('click')
    expect(pushMock).toHaveBeenLastCalledWith(loginRedirect)
    pushMock.mockClear()
    await countInputFor(wrapper, '站立區').setValue(1)
    expect(pushMock).toHaveBeenLastCalledWith(loginRedirect)
    pushMock.mockClear()
    await quickPickButton(wrapper).trigger('click')
    expect(pushMock).toHaveBeenLastCalledWith(loginRedirect)
    await flushPromises()
    expect(ordersApi.placeOrder).toHaveBeenCalledTimes(0)
  })

  it('[BW-SALES-016] 排隊等待中跨越停售仍顯示等待畫面，放行後顯示已停售並停用送出', async () => {
    vi.mocked(eventsApi.getEvents).mockResolvedValue([
      buildEvent({ isQueueModeEnabled: true, salesEndAtUtc: iso(SALES_END) }),
    ])
    vi.mocked(queueApi.getMyQueueStatus).mockResolvedValue(buildQueueStatus({ status: 'Waiting', waitingCount: 5 }))
    const wrapper = mountPage()
    await flushPromises()

    await crossTo(SALES_END)
    expect(wrapper.findComponent(QueueWaitingPanel).exists()).toBe(true)
    expect(wrapper.text()).not.toContain('本活動已停售')

    vi.mocked(queueApi.getMyQueueStatus).mockResolvedValue(buildQueueStatus({ status: 'Admitted' }))
    await vi.advanceTimersByTimeAsync(5000)
    await flushPromises()

    expect(wrapper.findComponent(QueueWaitingPanel).exists()).toBe(false)
    expect(salesPrompt(wrapper).text()).toContain('本活動已停售')
    expect(isDisabled(submitButton(wrapper))).toBe(true)
  })

  it('卸載時清除販售時鐘 interval 並移除 visibilitychange 監聽', async () => {
    const setIntervalSpy = vi.spyOn(window, 'setInterval')
    const clearIntervalSpy = vi.spyOn(window, 'clearInterval')
    const addListenerSpy = vi.spyOn(document, 'addEventListener')
    const removeListenerSpy = vi.spyOn(document, 'removeEventListener')
    vi.mocked(eventsApi.getEvents).mockResolvedValue([buildEvent()])
    const wrapper = mountPage()
    await flushPromises()

    const intervalCallIndex = setIntervalSpy.mock.calls.findIndex(([, delay]) => delay === 1000)
    expect(intervalCallIndex).toBeGreaterThanOrEqual(0)
    const intervalId = setIntervalSpy.mock.results[intervalCallIndex].value
    const visibilityHandler = addListenerSpy.mock.calls.find(([type]) => type === 'visibilitychange')?.[1]
    expect(visibilityHandler).toBeDefined()

    wrapper.unmount()

    expect(clearIntervalSpy).toHaveBeenCalledWith(intervalId)
    expect(removeListenerSpy).toHaveBeenCalledWith('visibilitychange', visibilityHandler)
    setIntervalSpy.mockRestore()
    clearIntervalSpy.mockRestore()
    addListenerSpy.mockRestore()
    removeListenerSpy.mockRestore()
  })
})

describe('EventDetailPage 結帳列與座位無障礙（purchase-flow-ux）', () => {
  const NOW = new Date('2030-01-01T00:00:00.000Z').getTime()
  const scrollIntoViewMock = vi.fn()
  let mountedWrapper: ReturnType<typeof mount> | null = null

  function mountAttached() {
    mountedWrapper = mountPage({ attachTo: document.body })
    return mountedWrapper
  }

  function seatsOf(count: number, zoneCode = 'A') {
    return Array.from({ length: count }, (_, i) =>
      buildSeat({ eventSeatId: `seat-${zoneCode}${i + 1}`, zoneCode, seatNumber: String(i + 1) }),
    )
  }

  function summaryRegion(wrapper: ReturnType<typeof mount>) {
    return wrapper.find('.checkout-bar [aria-live="polite"]')
  }

  function expectActionErrorOnly(wrapper: ReturnType<typeof mount>, message: string) {
    expect(actionError(wrapper).exists()).toBe(true)
    expect(actionError(wrapper).text()).toContain(message)
    expect(actionError(wrapper).attributes('role')).toBe('alert')
    expect(pageError(wrapper).exists()).toBe(false)
  }

  function salesNotOpenError() {
    return new ApiError(409, { status: 409, title: 'SalesNotOpen', detail: 'english detail' })
  }

  // BW-FLOW-007 的前置狀態：一般下單失敗且重新載入也失敗，頁面層級與操作層級錯誤並存。
  // 重新載入失敗時 event 仍保留上一次的值（loadData 只在全部成功後才寫入），頁面不會變成「找不到活動」。
  async function mountWithPageAndActionErrors(event: EventSummary = buildEvent()) {
    vi.mocked(eventsApi.getEvents).mockResolvedValueOnce([event]).mockRejectedValueOnce(new Error('network'))
    vi.mocked(ordersApi.placeOrder).mockRejectedValueOnce(new Error('座位已被搶'))
    const wrapper = mountPage()
    await flushPromises()

    await wrapper.find('.seat-btn').trigger('click')
    await submitButton(wrapper).trigger('click')
    await flushPromises()

    expect(pageError(wrapper).text()).toContain('載入活動資訊失敗')
    expect(actionError(wrapper).text()).toContain('下單失敗')
    return wrapper
  }

  function expectBothErrorsRemain(wrapper: ReturnType<typeof mount>) {
    expect(pageError(wrapper).text()).toContain('載入活動資訊失敗')
    expect(actionError(wrapper).text()).toContain('下單失敗')
  }

  async function fillTwoCountTicketsInOneBatch(wrapper: ReturnType<typeof mount>) {
    // 同一個同步批次內輸入兩個計數票種，讓個別輸入框的 max 來不及反映另一邊（沿用既有超量測試的做法）。
    const inputA = countInputFor(wrapper, '站立區A').element as HTMLInputElement
    const inputB = countInputFor(wrapper, '站立區B').element as HTMLInputElement
    inputA.value = '2'
    inputA.dispatchEvent(new Event('input'))
    inputB.value = '2'
    inputB.dispatchEvent(new Event('input'))
    await flushPromises()
  }

  beforeEach(() => {
    mockIsAuthenticated = true
    pushMock.mockReset()
    vi.mocked(eventsApi.getEvents).mockReset().mockResolvedValue([buildEvent()])
    vi.mocked(eventsApi.getEventSeats).mockReset().mockResolvedValue([buildSeat()])
    vi.mocked(eventsApi.getTicketTypes).mockReset().mockResolvedValue([buildSeatTicketType()])
    vi.mocked(ordersApi.placeOrder).mockReset()
    vi.mocked(queueApi.getMyQueueStatus).mockReset()
    vi.mocked(queueApi.joinQueue).mockReset()
    vi.mocked(captchaApi.getCaptcha).mockReset().mockResolvedValue({ token: 'captcha-token-1', imageBase64: 'base64-image-1' })
    vi.mocked(membersApi.getMyProfile).mockReset()
    scrollIntoViewMock.mockReset()
    // jsdom 沒有實作 scrollIntoView，鍵盤移動焦點時會呼叫。
    Element.prototype.scrollIntoView = scrollIntoViewMock
  })

  afterEach(() => {
    mountedWrapper?.unmount()
    mountedWrapper = null
    delete (Element.prototype as Partial<Element>).scrollIntoView
    vi.useRealTimers()
  })

  it('[BW-FLOW-002、005] 結帳列依選取順序列出前 6 個座位，超過時顯示「等 N 個座位」，摘要為 polite live region', async () => {
    vi.mocked(eventsApi.getEventSeats).mockResolvedValue(seatsOf(7))
    const wrapper = mountPage()
    await flushPromises()

    expect(checkoutSeatList(wrapper).exists()).toBe(false)
    expect(summaryRegion(wrapper).exists()).toBe(true)

    const seatButtons = wrapper.findAll('.seat-btn')
    await seatButtons[0].trigger('click')
    expect(summaryRegion(wrapper).text()).toContain('已選 1 個座位')

    // 點選順序刻意不是座位編號順序，確認清單依加入先後排列而非依編號排序。
    for (const index of [6, 1, 2, 3, 4, 5]) await seatButtons[index].trigger('click')

    const listText = checkoutSeatList(wrapper).text()
    expect(listText).toContain('A-1、A-7、A-2、A-3、A-4、A-5')
    expect(listText).not.toContain('A-6')
    expect(listText).toContain('等 7 個座位')

    await seatButtons[0].trigger('click')

    const afterCancelText = checkoutSeatList(wrapper).text()
    expect(afterCancelText).toContain('A-7、A-2、A-3、A-4、A-5、A-6')
    expect(afterCancelText).not.toContain('等')
  })

  async function mountAndSubmitOneSeat() {
    const wrapper = mountPage()
    await flushPromises()
    await wrapper.find('.seat-btn').trigger('click')
    await submitButton(wrapper).trigger('click')
    return wrapper
  }

  it.each([
    [
      '(a) 限購 1 張時點選第 2 個座位',
      '這個活動每筆訂單最多購買 1 張',
      async () => {
        vi.mocked(eventsApi.getEvents).mockResolvedValue([buildEvent({ maxTicketsPerOrder: 1 })])
        vi.mocked(eventsApi.getEventSeats).mockResolvedValue(seatsOf(2))
        const wrapper = mountPage()
        await flushPromises()
        await wrapper.findAll('.seat-btn')[0].trigger('click')
        await wrapper.findAll('.seat-btn')[1].trigger('click')
        return wrapper
      },
    ],
    [
      '(b) 分區沒有座位制票種時點選該分區的座位',
      '分區 B 尚未設定票種',
      async () => {
        vi.mocked(eventsApi.getEventSeats).mockResolvedValue(seatsOf(1, 'B'))
        const wrapper = mountPage()
        await flushPromises()
        await wrapper.find('.seat-btn').trigger('click')
        return wrapper
      },
    ],
    [
      '(c) 區域隨選張數超過可售座位數',
      '目前沒有足夠的可售座位',
      async () => {
        vi.mocked(eventsApi.getEventSeats).mockResolvedValue(seatsOf(2))
        const wrapper = mountPage()
        await flushPromises()
        await wrapper.find('.quick-pick input[type="number"]').setValue(5)
        await quickPickButton(wrapper).trigger('click')
        return wrapper
      },
    ],
    [
      '(d) 區域隨選張數超過限購剩餘額度',
      '這個活動每筆訂單最多購買 1 張',
      async () => {
        vi.mocked(eventsApi.getEvents).mockResolvedValue([buildEvent({ maxTicketsPerOrder: 1 })])
        vi.mocked(eventsApi.getEventSeats).mockResolvedValue(seatsOf(2))
        const wrapper = mountPage()
        await flushPromises()
        await wrapper.findAll('.seat-btn')[0].trigger('click')
        await quickPickButton(wrapper).trigger('click')
        return wrapper
      },
    ],
    [
      '(e) 送出前合併總數超過限購',
      '每筆訂單最多購買 3 張',
      async () => {
        vi.mocked(eventsApi.getEvents).mockResolvedValue([buildEvent({ maxTicketsPerOrder: 3 })])
        vi.mocked(eventsApi.getEventSeats).mockResolvedValue([])
        vi.mocked(eventsApi.getTicketTypes).mockResolvedValue([
          buildCountTicketType({ id: 'tt-count-a', zoneCode: '站立區A' }),
          buildCountTicketType({ id: 'tt-count-b', zoneCode: '站立區B' }),
        ])
        const wrapper = mountPage()
        await flushPromises()
        await fillTwoCountTicketsInOneBatch(wrapper)
        await submitButton(wrapper).trigger('click')
        return wrapper
      },
    ],
    [
      '(f) 送出時回傳 429',
      '請求過於頻繁',
      async () => {
        vi.mocked(ordersApi.placeOrder).mockRejectedValue(new ApiError(429, { status: 429, title: 'TooManyRequests' }))
        return mountAndSubmitOneSeat()
      },
    ],
    [
      '(g) 送出時回傳 409 SalesNotOpen',
      '尚未開賣，請於開賣時間後再試',
      async () => {
        vi.mocked(ordersApi.placeOrder).mockRejectedValue(salesNotOpenError())
        return mountAndSubmitOneSeat()
      },
    ],
    [
      '(h) 送出時一般失敗且重新載入成功',
      '下單失敗',
      async () => {
        vi.mocked(ordersApi.placeOrder).mockRejectedValue(new Error('座位已被搶'))
        return mountAndSubmitOneSeat()
      },
    ],
  ])('[BW-FLOW-003] %s：錯誤顯示在結帳列，不顯示在頁面頂端', async (_label, message, arrange) => {
    const wrapper = await arrange()
    await flushPromises()

    expectActionErrorOnly(wrapper, message)
  })

  it('[BW-FLOW-003] 頁面層級錯誤存在時送出前檢查超量：頁面錯誤保留，結帳列顯示限購訊息', async () => {
    vi.mocked(eventsApi.getTicketTypes).mockResolvedValue([
      buildSeatTicketType(),
      buildCountTicketType({ id: 'tt-count-a', zoneCode: '站立區A' }),
      buildCountTicketType({ id: 'tt-count-b', zoneCode: '站立區B' }),
    ])
    const wrapper = await mountWithPageAndActionErrors(buildEvent({ maxTicketsPerOrder: 3 }))

    await fillTwoCountTicketsInOneBatch(wrapper)
    await submitButton(wrapper).trigger('click')
    await flushPromises()

    expect(ordersApi.placeOrder).toHaveBeenCalledTimes(1)
    expect(pageError(wrapper).text()).toContain('載入活動資訊失敗')
    expect(actionError(wrapper).text()).toContain('每筆訂單最多購買 3 張')
    expect(actionError(wrapper).text()).not.toContain('下單失敗')
  })

  it('[BW-FLOW-003] (i) 未登入按「自動選位」：導向登入頁，兩處錯誤都不清除', async () => {
    const wrapper = await mountWithPageAndActionErrors()
    mockIsAuthenticated = false

    await quickPickButton(wrapper).trigger('click')
    await flushPromises()

    expect(pushMock).toHaveBeenCalledWith({ path: '/login', query: { redirect: '/events/event-1' } })
    expectBothErrorsRemain(wrapper)
    expect(ordersApi.placeOrder).toHaveBeenCalledTimes(1)
  })

  it('[BW-FLOW-003] (j) 已登入且非販售中：「自動選位」停用，兩處錯誤都不清除', async () => {
    vi.useFakeTimers()
    vi.setSystemTime(NOW)
    const wrapper = await mountWithPageAndActionErrors(
      buildEvent({ salesEndAtUtc: new Date(NOW + 60_000).toISOString() }),
    )

    vi.setSystemTime(NOW + 60_000)
    await vi.advanceTimersByTimeAsync(1000)

    expect(quickPickButton(wrapper).attributes('disabled')).toBeDefined()
    await quickPickButton(wrapper).trigger('click')
    await flushPromises()
    expectBothErrorsRemain(wrapper)
    expect(ordersApi.placeOrder).toHaveBeenCalledTimes(1)
  })

  it('[BW-FLOW-003] (k) 沒有已選內容：「送出訂單」停用，兩處錯誤都不清除', async () => {
    const wrapper = await mountWithPageAndActionErrors()

    expect(wrapper.text()).toContain('已選 0 個座位')
    expect(submitButton(wrapper).attributes('disabled')).toBeDefined()
    await submitButton(wrapper).trigger('click')
    await flushPromises()
    expectBothErrorsRemain(wrapper)
    expect(ordersApi.placeOrder).toHaveBeenCalledTimes(1)
  })

  it('[BW-FLOW-007] 一般失敗且重新載入也失敗時兩處錯誤並存，重新送出時兩處舊錯誤都清除', async () => {
    const wrapper = await mountWithPageAndActionErrors()
    expect(wrapper.text()).toContain('已選 0 個座位')

    await wrapper.find('.seat-btn').trigger('click')
    expectBothErrorsRemain(wrapper)

    vi.mocked(ordersApi.placeOrder).mockRejectedValueOnce(new ApiError(429, { status: 429, title: 'TooManyRequests' }))
    await submitButton(wrapper).trigger('click')
    await flushPromises()

    expect(ordersApi.placeOrder).toHaveBeenCalledTimes(2)
    expect(pageError(wrapper).exists()).toBe(false)
    expect(actionError(wrapper).text()).toContain('請求過於頻繁')
    expect(actionError(wrapper).text()).not.toContain('下單失敗')
  })

  it('[BW-FLOW-004] 活動載入失敗：錯誤顯示在頁面頂端並帶 role="alert"，不出現在結帳列', async () => {
    vi.mocked(eventsApi.getEvents).mockRejectedValue(new Error('network'))
    const wrapper = mountPage()
    await flushPromises()

    expect(pageError(wrapper).text()).toContain('載入活動資訊失敗')
    expect(pageError(wrapper).attributes('role')).toBe('alert')
    expect(actionError(wrapper).exists()).toBe(false)
  })

  it('[BW-FLOW-004] 熱門搶購模式查詢排隊狀態失敗：錯誤顯示在頁面頂端', async () => {
    vi.mocked(eventsApi.getEvents).mockResolvedValue([buildEvent({ isQueueModeEnabled: true })])
    vi.mocked(queueApi.getMyQueueStatus).mockRejectedValue(new Error('network'))
    const wrapper = mountPage()
    await flushPromises()

    expect(pageError(wrapper).text()).toContain('查詢排隊狀態失敗')
    expect(actionError(wrapper).exists()).toBe(false)
  })

  it('[BW-FLOW-004] 熱門搶購模式加入排隊一般失敗：錯誤顯示在頁面頂端', async () => {
    vi.mocked(eventsApi.getEvents).mockResolvedValue([buildEvent({ isQueueModeEnabled: true })])
    vi.mocked(queueApi.getMyQueueStatus).mockResolvedValue(buildQueueStatus({ status: 'NotJoined' }))
    vi.mocked(queueApi.joinQueue).mockRejectedValue(
      new ApiError(409, { status: 409, title: 'Conflict', detail: '活動目前未開啟熱門搶購模式' }),
    )
    const wrapper = mountPage()
    await flushPromises()

    await wrapper.find('input[placeholder="請輸入驗證碼"]').setValue('TEST')
    const joinButton = wrapper.findAll('button').find((b) => b.text() === '加入排隊')
    if (!joinButton) throw new Error('找不到「加入排隊」按鈕')
    await joinButton.trigger('click')
    await flushPromises()

    expect(pageError(wrapper).text()).toContain('活動目前未開啟熱門搶購模式')
    expect(actionError(wrapper).exists()).toBe(false)
  })

  it('[BW-FLOW-004、006] 送出時排隊資格逾時並轉為 Waiting：錯誤顯示在頁面頂端，結帳列不存在', async () => {
    vi.mocked(eventsApi.getEvents).mockResolvedValue([buildEvent({ isQueueModeEnabled: true })])
    vi.mocked(queueApi.getMyQueueStatus)
      .mockResolvedValueOnce(buildQueueStatus({ status: 'Admitted' }))
      .mockResolvedValue(buildQueueStatus({ status: 'Waiting', waitingCount: 2 }))
    vi.mocked(ordersApi.placeOrder).mockRejectedValue(new ApiError(403, { status: 403, title: 'QueueAdmissionRequired' }))
    const wrapper = mountPage()
    await flushPromises()

    await wrapper.find('.seat-btn').trigger('click')
    await submitButton(wrapper).trigger('click')
    await flushPromises()

    expect(pageError(wrapper).text()).toContain('排隊資格已逾時或尚未入場')
    expect(wrapper.find('.checkout-bar').exists()).toBe(false)
    // 排隊輪詢計時器跟著元件，不卸載會在之後的測試觸發
    wrapper.unmount()
  })

  it.each(['NotJoined', 'Waiting'])('[BW-FLOW-006] 排隊狀態為 %s 時不顯示結帳列與「送出訂單」', async (status) => {
    vi.mocked(eventsApi.getEvents).mockResolvedValue([buildEvent({ isQueueModeEnabled: true })])
    vi.mocked(queueApi.getMyQueueStatus).mockResolvedValue(buildQueueStatus({ status, waitingCount: 1 }))
    const wrapper = mountPage()
    await flushPromises()

    expect(wrapper.find('.checkout-bar').exists()).toBe(false)
    expect(wrapper.findAll('button').some((b) => b.text() === '送出訂單')).toBe(false)
    wrapper.unmount()
  })

  it('[BW-SEAT-A11Y-001、002] 第一個分區的座位網格前顯示一次圖例，Sold／Held 座位帶不可選樣式', async () => {
    vi.mocked(eventsApi.getEventSeats).mockResolvedValue([
      buildSeat({ eventSeatId: 'a1', seatNumber: '1', status: 'Available' }),
      buildSeat({ eventSeatId: 'a2', seatNumber: '2', status: 'Sold' }),
      buildSeat({ eventSeatId: 'a3', seatNumber: '3', status: 'Held' }),
      buildSeat({ eventSeatId: 'b1', zoneCode: 'B', seatNumber: '1' }),
    ])
    const wrapper = mountPage()
    await flushPromises()

    expect(wrapper.findAll('.seat-legend')).toHaveLength(1)
    const firstZone = wrapper.findAll('.zone-block')[0]
    const legend = firstZone.find('.seat-legend')
    expect(legend.exists()).toBe(true)
    const grid = firstZone.find('.seat-grid').element
    expect(legend.element.compareDocumentPosition(grid) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy()

    const swatches = legend.findAll('.seat-swatch')
    expect(swatches).toHaveLength(3)
    expect(swatches.map((s) => s.element.parentElement?.textContent?.trim())).toEqual(['可選', '已選', '不可選'])
    for (const swatch of swatches) {
      expect(swatch.attributes('aria-hidden')).toBe('true')
      expect(swatch.classes()).not.toContain('seat-btn')
    }
    expect(legend.findAll('.seat-swatch--unavailable')).toHaveLength(1)
    expect(swatches[2].classes()).toContain('seat-swatch--unavailable')

    const zoneASeats = firstZone.findAll('.seat-btn')
    expect(zoneASeats[0].classes()).not.toContain('sold')
    expect(zoneASeats[1].classes()).toContain('sold')
    expect(zoneASeats[2].classes()).toContain('sold')
  })

  it('[BW-SEAT-A11Y-003] 已登入時座位的 aria-label、title 與 aria-pressed 反映分區、號碼與狀態', async () => {
    vi.mocked(eventsApi.getEventSeats).mockResolvedValue([
      buildSeat({ eventSeatId: 'a12', seatNumber: '12', status: 'Available' }),
      buildSeat({ eventSeatId: 'a13', seatNumber: '13', status: 'Sold' }),
      buildSeat({ eventSeatId: 'a14', seatNumber: '14', status: 'Held' }),
      buildSeat({ eventSeatId: 'a15', seatNumber: '15', status: 'Unknown' }),
    ])
    const wrapper = mountPage()
    await flushPromises()

    const seatButtons = wrapper.findAll('.seat-btn')
    const expectedLabels = ['A 區 12 號，可選', 'A 區 13 號，已售出', 'A 區 14 號，保留中', 'A 區 15 號，不可選']
    seatButtons.forEach((button, index) => {
      expect(button.attributes('aria-label')).toBe(expectedLabels[index])
      expect(button.attributes('title')).toBe(expectedLabels[index])
    })
    expect(seatButtons[0].attributes('aria-pressed')).toBe('false')

    await seatButtons[0].trigger('click')

    expect(seatButtons[0].attributes('aria-label')).toBe('A 區 12 號，已選')
    expect(seatButtons[0].attributes('title')).toBe('A 區 12 號，已選')
    expect(seatButtons[0].attributes('aria-pressed')).toBe('true')
  })

  it('[BW-SEAT-A11Y-004] 未登入點選已售出座位：aria-disabled 為 true、沒有原生 disabled，仍導向登入頁', async () => {
    mockIsAuthenticated = false
    vi.mocked(eventsApi.getEventSeats).mockResolvedValue([buildSeat({ status: 'Sold' })])
    const wrapper = mountPage()
    await flushPromises()

    const soldSeat = wrapper.find('.seat-btn')
    expect(soldSeat.attributes('aria-disabled')).toBe('true')
    expect(soldSeat.attributes('disabled')).toBeUndefined()

    await soldSeat.trigger('click')

    expect(pushMock).toHaveBeenCalledWith({ path: '/login', query: { redirect: '/events/event-1' } })
  })

  it('[BW-SEAT-A11Y-004] 保留中（Held）座位同樣標示 aria-disabled、沒有原生 disabled，已登入點選不加入已選清單', async () => {
    vi.mocked(eventsApi.getEventSeats).mockResolvedValue([buildSeat({ status: 'Held' })])
    const wrapper = mountPage()
    await flushPromises()

    const heldSeat = wrapper.find('.seat-btn')
    expect(heldSeat.attributes('aria-disabled')).toBe('true')
    expect(heldSeat.attributes('disabled')).toBeUndefined()

    await heldSeat.trigger('click')

    expect(selectedSeatCount(wrapper)).toBe(0)
    expect(wrapper.text()).toContain('已選 0 個座位')
  })

  it('[BW-SEAT-A11Y-004] 已登入點選已售出座位：不加入已選清單', async () => {
    vi.mocked(eventsApi.getEventSeats).mockResolvedValue([buildSeat({ status: 'Sold' })])
    const wrapper = mountPage()
    await flushPromises()

    await wrapper.find('.seat-btn').trigger('click')

    expect(selectedSeatCount(wrapper)).toBe(0)
    expect(wrapper.text()).toContain('已選 0 個座位')
    expect(checkoutSeatList(wrapper).exists()).toBe(false)
  })

  describe('[BW-SEAT-A11Y-005、006] 座位網格 roving tabindex 與方向鍵', () => {
    // 第 4 席用 Held：Held 與 Sold 一樣不可選，必須被 Tab 停留點與方向鍵跳過。
    const zoneAStatuses = ['Sold', 'Available', 'Available', 'Held', 'Available']

    function buildRovingSeats(statusOverrides: Record<number, string> = {}) {
      return [
        ...zoneAStatuses.map((status, i) =>
          buildSeat({ eventSeatId: `a${i + 1}`, seatNumber: String(i + 1), status: statusOverrides[i + 1] ?? status }),
        ),
        buildSeat({ eventSeatId: 'b1', zoneCode: 'B', seatNumber: '1', status: 'Sold' }),
        buildSeat({ eventSeatId: 'b2', zoneCode: 'B', seatNumber: '2', status: 'Sold' }),
      ]
    }

    function zoneSeats(wrapper: ReturnType<typeof mount>, zoneIndex: number) {
      return wrapper.findAll('.zone-block')[zoneIndex].findAll('.seat-btn')
    }

    function tabStopNumbers(wrapper: ReturnType<typeof mount>, zoneIndex: number) {
      return zoneSeats(wrapper, zoneIndex)
        .filter((b) => b.attributes('tabindex') === '0')
        .map((b) => b.text())
    }

    // 在目前焦點的座位上觸發，事件冒泡到 .seat-grid，與真實鍵盤操作一致。
    function pressKey(key: string, modifiers: { altKey?: boolean; ctrlKey?: boolean } = {}) {
      const event = new KeyboardEvent('keydown', { key, bubbles: true, cancelable: true, ...modifiers })
      ;(document.activeElement as HTMLElement).dispatchEvent(event)
      return event
    }

    function focusedSeatNumber() {
      return (document.activeElement as HTMLElement).textContent?.trim()
    }

    beforeEach(() => {
      vi.mocked(eventsApi.getEventSeats).mockResolvedValue(buildRovingSeats())
      vi.mocked(eventsApi.getTicketTypes).mockResolvedValue([
        buildSeatTicketType({ id: 'tt-a', zoneCode: 'A' }),
        buildSeatTicketType({ id: 'tt-b', zoneCode: 'B' }),
      ])
    })

    it('(a) 每個分區只有第一個可選座位是 Tab 停留點，全部不可選的分區沒有停留點', async () => {
      const wrapper = mountPage()
      await flushPromises()

      expect(zoneSeats(wrapper, 0).map((b) => b.attributes('tabindex'))).toEqual(['-1', '0', '-1', '-1', '-1'])
      expect(zoneSeats(wrapper, 1).map((b) => b.attributes('tabindex'))).toEqual(['-1', '-1'])
      expect(wrapper.findAll('.seat-grid').map((g) => g.attributes('role'))).toEqual(['group', 'group'])
      expect(wrapper.findAll('.seat-grid').map((g) => g.attributes('aria-label'))).toEqual(['A 區座位', 'B 區座位'])
    })

    it('(b) 方向鍵、Home、End 只在可選座位間移動、不循環，其他按鍵與修飾鍵組合不攔截', async () => {
      const wrapper = mountAttached()
      await flushPromises()
      ;(zoneSeats(wrapper, 0)[1].element as HTMLElement).focus()

      const steps: Array<[string, string]> = [
        ['ArrowRight', '3'],
        ['ArrowRight', '5'],
        ['ArrowLeft', '3'],
        ['End', '5'],
        ['ArrowRight', '5'],
        ['Home', '2'],
        ['ArrowLeft', '2'],
        ['ArrowDown', '3'],
        ['ArrowUp', '2'],
      ]
      const focusedNumbers: Array<string | undefined> = []
      for (const [key, expectedNumber] of steps) {
        const event = pressKey(key)
        await flushPromises()
        focusedNumbers.push(focusedSeatNumber())
        expect(focusedSeatNumber(), `按下 ${key} 後的焦點`).toBe(expectedNumber)
        expect(event.defaultPrevented, `${key} 的 defaultPrevented`).toBe(true)
        expect(tabStopNumbers(wrapper, 0)).toEqual([expectedNumber])
        expect(scrollIntoViewMock).toHaveBeenLastCalledWith({ block: 'nearest' })
        expect(scrollIntoViewMock.mock.contexts.at(-1)).toBe(document.activeElement)
      }
      expect(focusedNumbers).toEqual(['3', '5', '3', '5', '5', '2', '2', '3', '2'])
      expect(focusedNumbers).not.toContain('1')
      expect(focusedNumbers).not.toContain('4')

      const passThroughKeys: Array<[string, { altKey?: boolean; ctrlKey?: boolean }]> = [
        ['Tab', {}],
        ['Enter', {}],
        [' ', {}],
        ['ArrowLeft', { altKey: true }],
        ['End', { ctrlKey: true }],
      ]
      for (const [key, modifiers] of passThroughKeys) {
        const event = pressKey(key, modifiers)
        await flushPromises()
        expect(event.defaultPrevented, `${key} ${JSON.stringify(modifiers)} 的 defaultPrevented`).toBe(false)
        expect(focusedSeatNumber()).toBe('2')
      }
    })

    it('(b2) 焦點停在不可選座位（滑鼠點選 Held 座位）時，方向鍵與 Home／End 不攔截、焦點不移動', async () => {
      const wrapper = mountAttached()
      await flushPromises()
      ;(zoneSeats(wrapper, 0)[3].element as HTMLElement).focus()

      for (const key of ['ArrowRight', 'ArrowLeft', 'Home', 'End']) {
        const event = pressKey(key)
        await flushPromises()
        expect(event.defaultPrevented, `${key} 的 defaultPrevented`).toBe(false)
        expect(focusedSeatNumber(), `按下 ${key} 後的焦點`).toBe('4')
      }
      expect(tabStopNumbers(wrapper, 0)).toEqual(['2'])
    })

    it('(c) 滑鼠點選座位後，Tab 停留點移到該座位', async () => {
      const wrapper = mountPage()
      await flushPromises()

      await zoneSeats(wrapper, 0)[4].trigger('click')

      expect(tabStopNumbers(wrapper, 0)).toEqual(['5'])
    })

    it('(d) 記錄的座位變成不可選並重新載入後，Tab 停留點回到第一個可選座位', async () => {
      vi.mocked(ordersApi.placeOrder).mockRejectedValue(new Error('座位已被搶'))
      const wrapper = mountPage()
      await flushPromises()
      await zoneSeats(wrapper, 0)[4].trigger('click')
      expect(tabStopNumbers(wrapper, 0)).toEqual(['5'])

      vi.mocked(eventsApi.getEventSeats).mockResolvedValue(buildRovingSeats({ 5: 'Sold' }))
      await submitButton(wrapper).trigger('click')
      await flushPromises()

      expect(eventsApi.getEventSeats).toHaveBeenCalledTimes(2)
      expect(tabStopNumbers(wrapper, 0)).toEqual(['2'])
    })
  })
})
