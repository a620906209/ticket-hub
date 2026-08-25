import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount } from '@vue/test-utils'
import ElementPlus from 'element-plus'
import OrderDetailPage from './OrderDetailPage.vue'
import * as ordersApi from '../../api/orders'
import { ApiError } from '../../api/httpClient'
import type { MyOrderDetail } from '../../types/apiResponses'

vi.mock('../../api/orders')

vi.mock('vue-router', () => ({
  useRoute: () => ({ params: { id: 'order-1' } }),
}))

function buildOrder(overrides: Partial<MyOrderDetail> = {}): MyOrderDetail {
  return {
    id: 'order-1',
    eventId: 'event-1',
    status: 'Paid',
    heldUntilUtc: '2026-12-31T12:00:00Z',
    items: [
      {
        id: 'item-1',
        eventSeatId: 'seat-1',
        ticketTypeId: null,
        quantity: 1,
        unitPrice: 1200,
        tickets: [{ id: 'ticket-1', status: 'Issued' }],
      },
    ],
    ...overrides,
  }
}

function mountPage() {
  return mount(OrderDetailPage, {
    global: {
      plugins: [ElementPlus],
      stubs: { RouterLink: { props: ['to'], template: '<a :href="to"><slot /></a>' } },
    },
  })
}

function qrButtons(wrapper: ReturnType<typeof mount>) {
  return wrapper.findAll('button').filter((button) => button.text().includes('查看 QR Code'))
}

describe('OrderDetailPage 訂單明細', () => {
  beforeEach(() => {
    vi.mocked(ordersApi.getMyOrderDetail).mockReset()
    vi.mocked(ordersApi.getTicketQrCodeBlob).mockReset()
  })

  afterEach(() => {
    vi.unstubAllGlobals()
  })

  it('已出票 Paid 訂單顯示票券與查看 QR Code 操作，不顯示保留時間', async () => {
    vi.mocked(ordersApi.getMyOrderDetail).mockResolvedValue(buildOrder())

    const wrapper = mountPage()
    await flushPromises()

    expect(wrapper.text()).toContain('票券狀態：Issued')
    expect(wrapper.text()).toContain('查看 QR Code')
    expect(wrapper.text()).not.toContain('保留至')
  })

  it('查看 QR Code 時以票券 Id 取得 Blob、建立 Object URL，切換或卸載時釋放 URL', async () => {
    const createObjectURL = vi.fn().mockReturnValueOnce('blob:ticket-1').mockReturnValueOnce('blob:ticket-2')
    const revokeObjectURL = vi.fn()
    vi.stubGlobal('URL', { createObjectURL, revokeObjectURL })
    vi.mocked(ordersApi.getMyOrderDetail).mockResolvedValue(
      buildOrder({
        items: [
          {
            id: 'item-1',
            eventSeatId: 'seat-1',
            ticketTypeId: null,
            quantity: 2,
            unitPrice: 1200,
            tickets: [
              { id: 'ticket-1', status: 'Issued' },
              { id: 'ticket-2', status: 'Redeemed' },
            ],
          },
        ],
      }),
    )
    vi.mocked(ordersApi.getTicketQrCodeBlob).mockResolvedValue(new Blob(['png'], { type: 'image/png' }))

    const wrapper = mountPage()
    await flushPromises()

    const buttons = qrButtons(wrapper)
    await buttons[0].trigger('click')
    await flushPromises()
    expect(ordersApi.getTicketQrCodeBlob).toHaveBeenCalledWith('ticket-1')
    expect(createObjectURL).toHaveBeenCalledOnce()
    expect(wrapper.find('img[alt="票券 QR Code"]').attributes('src')).toBe('blob:ticket-1')

    await buttons[1].trigger('click')
    await flushPromises()
    expect(revokeObjectURL).toHaveBeenCalledWith('blob:ticket-1')
    expect(wrapper.find('img[alt="票券 QR Code"]').attributes('src')).toBe('blob:ticket-2')

    wrapper.unmount()
    expect(revokeObjectURL).toHaveBeenCalledWith('blob:ticket-2')
  })

  it('Pending 且尚未出票的項目顯示保留時間與尚未出票，不顯示 QR Code 操作', async () => {
    vi.mocked(ordersApi.getMyOrderDetail).mockResolvedValue(
      buildOrder({
        status: 'Pending',
        items: [{ id: 'item-1', eventSeatId: null, ticketTypeId: 'type-1', quantity: 2, unitPrice: 800, tickets: [] }],
      }),
    )

    const wrapper = mountPage()
    await flushPromises()

    expect(wrapper.text()).toContain(`保留至 ${new Date('2026-12-31T12:00:00Z').toLocaleString()}`)
    expect(wrapper.text()).toContain('尚未出票')
    expect(wrapper.text()).not.toContain('查看 QR Code')
  })

  it('明細 API 回傳 404 時顯示找不到提示與返回列表操作，不渲染訂單資料', async () => {
    vi.mocked(ordersApi.getMyOrderDetail).mockRejectedValue(new ApiError(404, { detail: 'not found' }))

    const wrapper = mountPage()
    await flushPromises()

    expect(wrapper.text()).toContain('找不到這筆訂單')
    expect(wrapper.text()).toContain('返回我的訂單')
    expect(wrapper.text()).not.toContain('訂單 Id：')
  })

  it('明細 API 回傳 403 時顯示無權限提示與返回列表操作，不渲染訂單資料', async () => {
    vi.mocked(ordersApi.getMyOrderDetail).mockRejectedValue(new ApiError(403, { detail: 'forbidden' }))

    const wrapper = mountPage()
    await flushPromises()

    expect(wrapper.text()).toContain('你沒有權限查看這筆訂單')
    expect(wrapper.text()).toContain('返回我的訂單')
    expect(wrapper.text()).not.toContain('訂單 Id：')
  })
})
