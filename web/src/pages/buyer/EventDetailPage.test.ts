import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { mount, flushPromises } from '@vue/test-utils'
import { defineComponent } from 'vue'
import ElementPlus from 'element-plus'
import EventDetailPage from './EventDetailPage.vue'
import * as eventsApi from '../../api/events'
import * as ordersApi from '../../api/orders'
import { ApiError } from '../../api/httpClient'
import type { EventSeat, EventSummary, TicketType } from '../../types/apiResponses'

vi.mock('../../api/events')
vi.mock('../../api/orders')

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
  props: ['modelValue'],
  emits: ['update:modelValue', 'change'],
  template: `<select :value="modelValue" @change="$emit('update:modelValue', $event.target.value); $emit('change', $event.target.value)">
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
    startAtUtc: '2026-12-31T20:00:00Z',
    venueId: 'venue-1',
    seatMapId: 'seatmap-1',
    description: null,
    posterUrl: null,
    maxTicketsPerOrder: null,
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

function mountPage() {
  return mount(EventDetailPage, {
    global: {
      plugins: [ElementPlus],
      stubs: { ElInputNumber: ElInputNumberStub, ElSelect: ElSelectStub, ElOption: ElOptionStub },
    },
  })
}

function submitButton(wrapper: ReturnType<typeof mount>) {
  const button = wrapper.findAll('button').find((b) => b.text() === '送出訂單')
  if (!button) throw new Error('找不到「送出訂單」按鈕')
  return button
}

function quickPickButton(wrapper: ReturnType<typeof mount>) {
  const button = wrapper.findAll('button').find((b) => b.text() === '自動選位並送出訂單')
  if (!button) throw new Error('找不到「自動選位並送出訂單」按鈕')
  return button
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
    expect(pushMock).toHaveBeenCalledWith(expect.objectContaining({ path: '/order-result/order-1' }))
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

  it('區域隨選：維持預設「全部區域」，能隨機抽出對應數量並直接送出訂單成功', async () => {
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

    expect(ordersApi.placeOrder).toHaveBeenCalledOnce()
    const selections = vi.mocked(ordersApi.placeOrder).mock.calls[0][0]
    expect(selections).toHaveLength(1)
    expect(pushMock).toHaveBeenCalledWith(expect.objectContaining({ path: '/order-result/order-1' }))
  })

  it('區域隨選：指定單一分區時，只從該分區抽出座位，不會選到其他分區', async () => {
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

    expect(ordersApi.placeOrder).toHaveBeenCalledOnce()
    const selections = vi.mocked(ordersApi.placeOrder).mock.calls[0][0]
    expect(selections).toHaveLength(2)
    expect(selections.every((s) => s.eventSeatId === 'seat-a1' || s.eventSeatId === 'seat-a2')).toBe(true)
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

  it('使用區域隨選時一併送出已輸入的計數購買', async () => {
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

    expect(ordersApi.placeOrder).toHaveBeenCalledWith([
      { eventSeatId: 'seat-1', ticketTypeId: 'tt-seat-a' },
      { eventSeatId: null, ticketTypeId: 'tt-count-1', quantity: 2 },
    ])
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

afterEach(() => {
  vi.unstubAllGlobals()
})
