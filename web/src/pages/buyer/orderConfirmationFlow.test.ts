import { beforeEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount } from '@vue/test-utils'
import { defineComponent, reactive } from 'vue'
import { createPinia, setActivePinia } from 'pinia'
import type { Pinia } from 'pinia'
import ElementPlus from 'element-plus'
import EventDetailPage from './EventDetailPage.vue'
import EventListPage from './EventListPage.vue'
import { usePlacedOrderNoticeStore } from '../../stores/placedOrderNotice'
import * as eventsApi from '../../api/events'
import * as ordersApi from '../../api/orders'
import type { PlaceOrderSelection } from '../../api/orders'
import type { EventSeat, EventSummary, TicketType } from '../../types/apiResponses'

vi.mock('../../api/events')
vi.mock('../../api/orders')
vi.mock('../../api/queue')
vi.mock('../../api/captcha')
vi.mock('../../api/members')

vi.mock('../../stores/auth', () => ({
  useAuthStore: () => ({ isAuthenticated: true }),
}))

// 只有「同頁、只帶 query」的導覽會改 route（進出確認步驟）；回到活動列表等跨頁導覽只記錄呼叫，
// 由測試自己卸載詳情頁、掛載列表頁來模擬換頁。
const mockRoute = reactive({
  params: { id: 'event-1' },
  query: {} as Record<string, string>,
  path: '/events/event-1',
  fullPath: '/events/event-1',
})

function applyInPageQuery(location: unknown) {
  if (typeof location !== 'object' || location === null) return
  const { path, name, query } = location as { path?: string; name?: string; query?: Record<string, string> }
  if (path === undefined && name === undefined && query) mockRoute.query = { ...query }
}

const pushMock = vi.fn(async (location: unknown) => applyInPageQuery(location))
const replaceMock = vi.fn(async (location: unknown) => applyInPageQuery(location))

vi.mock('vue-router', () => ({
  useRoute: () => mockRoute,
  useRouter: () => ({
    push: pushMock,
    replace: replaceMock,
    back: vi.fn(),
    afterEach: () => () => {},
    onError: () => () => {},
  }),
  onBeforeRouteLeave: vi.fn(),
}))

// 與 EventDetailPage.test.ts 相同的取捨：以原生 number input 取代 ElInputNumber。
const ElInputNumberStub = defineComponent({
  props: ['modelValue', 'min', 'max', 'disabled'],
  emits: ['update:modelValue', 'change'],
  template: `<input type="number" :value="modelValue" :min="min" :max="max" :disabled="disabled" @input="onInput" />`,
  methods: {
    onInput(event: Event) {
      const value = (event.target as HTMLInputElement).valueAsNumber
      this.$emit('update:modelValue', Number.isNaN(value) ? 0 : value)
      this.$emit('change', Number.isNaN(value) ? 0 : value)
    },
  },
})

// 輸出真實 href，才能斷言「前往付款」指向的訂單 Id。
const RouterLinkStub = { props: ['to'], template: '<a :href="to"><slot /></a>' }

const EVENT: EventSummary = {
  id: 'event-1',
  title: 'Concert',
  startAtUtc: '2099-12-31T20:00:00Z',
  venueId: 'venue-1',
  seatMapId: 'seatmap-1',
  description: null,
  posterUrl: null,
  // 不設限購：混合案例合計 3 張，避免與 BW-ORDER-015 的限購攔截混淆。
  maxTicketsPerOrder: null,
  isQueueModeEnabled: false,
  isRealNameRequired: false,
  salesStartAtUtc: null,
  salesEndAtUtc: null,
}
const SEAT: EventSeat = { eventSeatId: 'seat-1', zoneCode: 'A', seatNumber: '1', status: 'Available' }
const SEAT_TICKET_TYPE: TicketType = { id: 'tt-seat-a', zoneCode: 'A', price: 1000, requiresSeat: true, availableQuantity: null }
const COUNT_TICKET_TYPE: TicketType = {
  id: 'tt-count-1',
  zoneCode: '站立區',
  price: 500,
  requiresSeat: false,
  availableQuantity: 10,
}

interface FlowCase {
  name: string
  seats: EventSeat[]
  ticketTypes: TicketType[]
  selectsSeat: boolean
  countQuantity: number
  expectedSelections: PlaceOrderSelection[]
  orderId: string
}

const flowCases: FlowCase[] = [
  {
    name: '座位（BW-ORDER-001）',
    seats: [SEAT],
    ticketTypes: [SEAT_TICKET_TYPE],
    selectsSeat: true,
    countQuantity: 0,
    expectedSelections: [{ eventSeatId: 'seat-1', ticketTypeId: 'tt-seat-a' }],
    orderId: 'order-123',
  },
  {
    name: '純計數（BW-ORDER-007）',
    seats: [],
    ticketTypes: [COUNT_TICKET_TYPE],
    selectsSeat: false,
    countQuantity: 3,
    expectedSelections: [{ eventSeatId: null, ticketTypeId: 'tt-count-1', quantity: 3 }],
    orderId: 'order-count-123',
  },
  {
    name: '混合（BW-ORDER-008）',
    seats: [SEAT],
    ticketTypes: [SEAT_TICKET_TYPE, COUNT_TICKET_TYPE],
    selectsSeat: true,
    countQuantity: 2,
    expectedSelections: [
      { eventSeatId: 'seat-1', ticketTypeId: 'tt-seat-a' },
      { eventSeatId: null, ticketTypeId: 'tt-count-1', quantity: 2 },
    ],
    orderId: 'order-mixed-1',
  },
]

let testPinia: Pinia

function mountWithSharedPinia<T>(component: T) {
  return mount(component as never, {
    global: {
      plugins: [ElementPlus, testPinia],
      stubs: { ElInputNumber: ElInputNumberStub, RouterLink: RouterLinkStub },
    },
  })
}

function findButtonByText(wrapper: ReturnType<typeof mount>, text: string) {
  const button = wrapper.findAll('button').find((b) => b.text() === text)
  if (!button) throw new Error(`找不到「${text}」按鈕`)
  return button
}

function findPayLink(wrapper: ReturnType<typeof mount>) {
  return wrapper.find('.placed-order-notice').findAll('a').find((link) => link.text() === '前往付款')
}

describe('下單成功通知鏈：活動詳情頁確認訂單 → 活動列表付款提示（order-confirmation-page）', () => {
  beforeEach(() => {
    mockRoute.query = {}
    pushMock.mockClear()
    replaceMock.mockClear()
    vi.mocked(eventsApi.getEvents).mockReset()
    vi.mocked(eventsApi.getEventSeats).mockReset()
    vi.mocked(eventsApi.getTicketTypes).mockReset()
    vi.mocked(ordersApi.placeOrder).mockReset()
    // 兩頁共用同一個 Pinia：詳情頁寫入、列表頁消費的必須是同一份 store，不 mock store。
    testPinia = createPinia()
    setActivePinia(testPinia)
  })

  // 單頁測試各自只驗一半：詳情頁寫進 store、列表頁讀 store。這裡串起來，確保買家看到的付款連結
  // 正是本次下單 API 回應的那筆訂單，而不是空值、別筆或沒被傳到。
  it.each(flowCases)(
    'confirmOrder_WhenSucceeded_EventListShowsNoticeLinkingToSameOrderId：$name',
    async ({ seats, ticketTypes, selectsSeat, countQuantity, expectedSelections, orderId }) => {
      vi.mocked(eventsApi.getEvents).mockResolvedValue([EVENT])
      vi.mocked(eventsApi.getEventSeats).mockResolvedValue(seats)
      vi.mocked(eventsApi.getTicketTypes).mockResolvedValue(ticketTypes)
      vi.mocked(ordersApi.placeOrder).mockResolvedValue({ id: orderId })

      const detailPage = mountWithSharedPinia(EventDetailPage)
      await flushPromises()
      if (selectsSeat) await detailPage.find('.seat-btn').trigger('click')
      if (countQuantity > 0) {
        const row = detailPage.findAll('.count-ticket-row').find((r) => r.text().includes('站立區'))
        if (!row) throw new Error('找不到計數票種列')
        await row.find('input[type="number"]').setValue(countQuantity)
      }
      await findButtonByText(detailPage, '下一步').trigger('click')
      await flushPromises()
      if (!detailPage.find('.confirm-step').exists()) throw new Error('沒有進入確認步驟')
      await detailPage.find('.confirm-step input[type="checkbox"]').setValue(true)
      await findButtonByText(detailPage, '確認訂單').trigger('click')
      await flushPromises()

      expect(ordersApi.placeOrder).toHaveBeenCalledOnce()
      expect(vi.mocked(ordersApi.placeOrder).mock.calls[0]).toStrictEqual([expectedSelections])
      const store = usePlacedOrderNoticeStore()
      expect(store.placedOrderId).toBe(orderId)
      expect(replaceMock).toHaveBeenCalledOnce()
      expect(replaceMock).toHaveBeenCalledWith({ name: 'events' })
      // 唯一的 push 是進入確認步驟的同頁 query 導覽；成功後不得再 push 任何頁面。
      expect(pushMock.mock.calls).toEqual([[{ query: { step: 'confirm' } }]])

      detailPage.unmount()
      vi.mocked(eventsApi.getEvents).mockResolvedValue([])
      const listPage = mountWithSharedPinia(EventListPage)
      await flushPromises()

      expect(listPage.find('.placed-order-notice').exists()).toBe(true)
      expect(findPayLink(listPage)?.attributes('href')).toBe(`/orders/${orderId}`)
      expect(store.placedOrderId).toBeNull()

      listPage.unmount()
      const listPageAgain = mountWithSharedPinia(EventListPage)
      await flushPromises()
      expect(listPageAgain.find('.placed-order-notice').exists()).toBe(false)
      listPageAgain.unmount()
    },
  )
})
