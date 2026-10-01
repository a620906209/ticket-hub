import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { flushPromises } from '@vue/test-utils'
import { ElMessage, ElMessageBox } from 'element-plus'
import OrderDetailPage from './OrderDetailPage.vue'
import * as ordersApi from '../../api/orders'
import { ApiError } from '../../api/httpClient'
import type { MyOrderDetail } from '../../types/apiResponses'
import {
  HELD_UNTIL_UTC,
  buildPaidOrder,
  buildPendingOrder,
  createDeferred,
  findButton,
  isButtonDisabled,
  mountOrderPageAt,
} from './orderPageTestSupport'

vi.mock('../../api/orders')

const HELD_UNTIL_TEXT = `保留至 ${new Date(HELD_UNTIL_UTC).toLocaleString()}`

function mountDetailPage(path = '/orders/order-1') {
  return mountOrderPageAt(path, '/orders/:id', OrderDetailPage)
}

// v-loading 關閉後遮罩會以 v-show 隱藏、延遲移除 DOM，只能以可見性判斷，不能以存在與否判斷。
function isPageLoadingVisible(wrapper: Awaited<ReturnType<typeof mountDetailPage>>['wrapper']): boolean {
  const mask = wrapper.find('.el-loading-mask')
  return mask.exists() && mask.isVisible()
}

function expectNoPendingActions(wrapper: Awaited<ReturnType<typeof mountDetailPage>>['wrapper']): void {
  expect(findButton(wrapper, '確認付款')).toBeUndefined()
  expect(findButton(wrapper, '取消訂單')).toBeUndefined()
}

describe('OrderDetailPage 訂單明細', () => {
  beforeEach(() => {
    vi.mocked(ordersApi.getMyOrderDetail).mockReset()
    vi.mocked(ordersApi.getTicketQrCodeBlob).mockReset()
    vi.mocked(ordersApi.confirmOrder).mockReset()
    vi.mocked(ordersApi.cancelOrder).mockReset()
    vi.spyOn(ElMessage, 'success').mockImplementation(() => ({ close: () => {} }) as never)
  })

  afterEach(() => {
    vi.restoreAllMocks()
    vi.unstubAllGlobals()
  })

  describe('既有顯示行為', () => {
    it('[BW-MYORDER-DETAIL-001 開啟訂單明細頁查看已出票訂單] Paid 訂單顯示已付款、已出票與查看 QR Code，不顯示保留時間', async () => {
      vi.mocked(ordersApi.getMyOrderDetail).mockResolvedValue(buildPaidOrder())
      const { wrapper } = await mountDetailPage()

      expect(wrapper.text()).toContain('已付款')
      expect(wrapper.text()).toContain('已出票')
      expect(wrapper.text()).toContain('查看 QR Code')
      expect(wrapper.text()).not.toContain('保留至')
      expect(wrapper.text()).not.toContain('Paid')
      expect(wrapper.text()).not.toContain('Issued')
      expect(wrapper.text()).toContain('活動：Spring Concert')
      expect(wrapper.text()).toContain('票種：A')
      expect(wrapper.text()).toContain('座位：A-12')
    })

    it('[BW-MYORDER-DISPLAY-001] 座位項目顯示票種與「分區-號碼」，計數項目的座位顯示「—」', async () => {
      vi.mocked(ordersApi.getMyOrderDetail).mockResolvedValue(
        buildPendingOrder({
          items: [
            {
              id: 'seat-item',
              eventSeatId: 'seat-1',
              ticketTypeId: 'ticket-type-a',
              seatZoneCode: 'A',
              seatNumber: '12',
              ticketTypeName: 'A',
              quantity: 1,
              unitPrice: 1200,
              tickets: [],
            },
            {
              id: 'count-item',
              eventSeatId: null,
              ticketTypeId: 'ticket-type-standing',
              seatZoneCode: null,
              seatNumber: null,
              ticketTypeName: '站票',
              quantity: 2,
              unitPrice: 800,
              tickets: [],
            },
          ],
        }),
      )
      const { wrapper } = await mountDetailPage()

      const items = wrapper.findAll('.order-item')
      expect(items).toHaveLength(2)
      expect(items[0].text()).toContain('票種：A')
      expect(items[0].text()).toContain('座位：A-12')
      expect(items[1].text()).toContain('票種：站票')
      expect(items[1].text()).toContain('座位：—')
    })

    it('[BW-MYORDER-DISPLAY-001] 舊訂單項目票種名稱為 null 時顯示「—」', async () => {
      vi.mocked(ordersApi.getMyOrderDetail).mockResolvedValue(
        buildPendingOrder({
          items: [
            {
              id: 'legacy-item',
              eventSeatId: 'seat-1',
              ticketTypeId: null,
              seatZoneCode: 'A',
              seatNumber: '12',
              ticketTypeName: null,
              quantity: 1,
              unitPrice: 1200,
              tickets: [],
            },
          ],
        }),
      )
      const { wrapper } = await mountDetailPage()

      const item = wrapper.find('.order-item')
      expect(item.text()).toContain('票種：—')
      expect(item.text()).toContain('座位：A-12')
    })

    // 活動名稱、票種名稱、分區皆由 Organizer 輸入，MUST 以文字插值渲染（design.md 安全確認-前端）。
    it('[BW-MYORDER-DISPLAY-001] 活動名稱、票種名稱與分區含 HTML 標籤時原樣以文字顯示，不產生元素', async () => {
      vi.mocked(ordersApi.getMyOrderDetail).mockResolvedValue(
        buildPendingOrder({
          eventTitle: '<img src=x onerror=alert(1)>',
          items: [
            {
              id: 'item-1',
              eventSeatId: 'seat-1',
              ticketTypeId: 'ticket-type-1',
              seatZoneCode: '<b>zone</b>',
              seatNumber: '12',
              ticketTypeName: '<i>vip</i>',
              quantity: 1,
              unitPrice: 1200,
              tickets: [],
            },
          ],
        }),
      )
      const { wrapper } = await mountDetailPage()

      expect(wrapper.text()).toContain('<img src=x onerror=alert(1)>')
      expect(wrapper.text()).toContain('<i>vip</i>')
      expect(wrapper.text()).toContain('<b>zone</b>-12')
      expect(wrapper.find('.order-detail-page img').exists()).toBe(false)
      expect(wrapper.find('.order-item b').exists()).toBe(false)
      expect(wrapper.find('.order-item i').exists()).toBe(false)
    })

    it('[BW-MYORDER-QR-001 點選查看 QR Code] 以票券 Id 取得 Blob、建立 Object URL，切換或卸載時釋放 URL', async () => {
      const createObjectURL = vi.fn().mockReturnValueOnce('blob:ticket-1').mockReturnValueOnce('blob:ticket-2')
      const revokeObjectURL = vi.fn()
      vi.stubGlobal('URL', { createObjectURL, revokeObjectURL })
      vi.mocked(ordersApi.getMyOrderDetail).mockResolvedValue(
        buildPaidOrder({
          items: [
            {
              id: 'item-1',
              eventSeatId: 'seat-1',
              ticketTypeId: null,
              seatZoneCode: 'A',
              seatNumber: '1',
              ticketTypeName: null,
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
      const { wrapper } = await mountDetailPage()

      const buttons = wrapper.findAll('button').filter((button) => button.text().includes('查看 QR Code'))
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

    it('[BW-MYORDER-DETAIL-002 開啟尚未出票訂單的明細頁] Pending 訂單顯示待付款、保留時間與尚未出票，不顯示 QR Code 操作', async () => {
      vi.mocked(ordersApi.getMyOrderDetail).mockResolvedValue(buildPendingOrder())
      const { wrapper } = await mountDetailPage()

      expect(wrapper.text()).toContain('待付款')
      expect(wrapper.text()).toContain(HELD_UNTIL_TEXT)
      expect(wrapper.text()).toContain('尚未出票')
      expect(wrapper.text()).not.toContain('查看 QR Code')
    })

    it('[BW-MYORDER-NOTFOUND-001 直接以網址開啟不存在的訂單明細頁] 明細 API 回傳 404 時顯示找不到提示與返回列表操作', async () => {
      vi.mocked(ordersApi.getMyOrderDetail).mockRejectedValue(new ApiError(404, { detail: 'not found' }))
      const { wrapper } = await mountDetailPage()

      expect(wrapper.text()).toContain('找不到這筆訂單')
      expect(wrapper.text()).toContain('返回我的訂單')
      expect(wrapper.text()).not.toContain('訂單 Id：')
    })

    it('[BW-MYORDER-FORBIDDEN-001 直接以網址開啟非本人的訂單明細頁] 明細 API 回傳 403 時顯示無權限提示與返回列表操作', async () => {
      vi.mocked(ordersApi.getMyOrderDetail).mockRejectedValue(new ApiError(403, { detail: 'forbidden' }))
      const { wrapper } = await mountDetailPage()

      expect(wrapper.text()).toContain('你沒有權限查看這筆訂單')
      expect(wrapper.text()).toContain('返回我的訂單')
      expect(wrapper.text()).not.toContain('訂單 Id：')
    })

    // 前端刻意不做 GUID 格式檢查，唯一的格式驗證層是後端路由約束（design.md 安全確認）。
    it('[BW-MYORDER-INVALID-ID-001] 以非 GUID 的 Id 開啟時以原值查詢，依 404 顯示找不到提示', async () => {
      vi.mocked(ordersApi.getMyOrderDetail).mockRejectedValue(new ApiError(404, { detail: 'not found' }))
      const { wrapper } = await mountDetailPage('/orders/not-a-guid')

      expect(ordersApi.getMyOrderDetail).toHaveBeenCalledWith('not-a-guid')
      expect(wrapper.text()).toContain('找不到這筆訂單')
      expect(wrapper.text()).toContain('返回我的訂單')
    })

    it('票券標籤：Issued 渲染成功色「已出票」、Redeemed 渲染中性色「已核銷」', async () => {
      vi.mocked(ordersApi.getMyOrderDetail).mockResolvedValue(
        buildPaidOrder({
          items: [
            {
              id: 'item-1',
              eventSeatId: 'seat-1',
              ticketTypeId: null,
              seatZoneCode: 'A',
              seatNumber: '1',
              ticketTypeName: null,
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
      const { wrapper } = await mountDetailPage()

      const ticketTags = wrapper.findAll('.ticket-list .el-tag')
      expect(ticketTags[0].classes()).toContain('el-tag--success')
      expect(ticketTags[0].text()).toBe('已出票')
      expect(ticketTags[1].classes()).toContain('el-tag--info')
      expect(ticketTags[1].text()).toBe('已核銷')
    })

    // 未知值原樣顯示的規定（BW-MYORDER-DISPLAY-002 的票券面）必須在頁面層成立：只測 statusLabels 純函式時，
    // 頁面若以 QR Code 條件包住整筆票券，未知狀態的票券會整筆消失而測試仍通過。
    it('[BW-MYORDER-DISPLAY-002] 未知票券狀態原樣顯示為中性色標籤，但不提供查看 QR Code', async () => {
      vi.mocked(ordersApi.getMyOrderDetail).mockResolvedValue(
        buildPaidOrder({
          items: [
            {
              id: 'item-1',
              eventSeatId: 'seat-1',
              ticketTypeId: null,
              seatZoneCode: 'A',
              seatNumber: '1',
              ticketTypeName: null,
              quantity: 2,
              unitPrice: 1200,
              tickets: [
                { id: 'ticket-1', status: 'Issued' },
                { id: 'ticket-2', status: 'Transferred' },
              ],
            },
          ],
        }),
      )
      const { wrapper } = await mountDetailPage()

      const ticketRows = wrapper.findAll('.ticket-list li')
      expect(ticketRows).toHaveLength(2)
      const unknownTag = ticketRows[1].find('.el-tag')
      expect(unknownTag.text()).toBe('Transferred')
      expect(unknownTag.classes()).toContain('el-tag--info')
      expect(ticketRows[1].text()).not.toContain('查看 QR Code')
      expect(ticketRows[0].text()).toContain('查看 QR Code')
    })
  })

  describe('Pending 訂單的確認付款與取消', () => {
    it('[BW-PENDING-001] 確認付款成功後重新查詢，顯示已付款、票券與查看 QR Code，不再顯示操作', async () => {
      vi.mocked(ordersApi.getMyOrderDetail)
        .mockResolvedValueOnce(buildPendingOrder())
        .mockResolvedValueOnce(buildPaidOrder())
      vi.mocked(ordersApi.confirmOrder).mockResolvedValue()
      const { wrapper } = await mountDetailPage()

      await findButton(wrapper, '確認付款')!.trigger('click')
      await flushPromises()

      expect(ordersApi.confirmOrder).toHaveBeenCalledExactlyOnceWith('order-1')
      expect(ElMessage.success).toHaveBeenCalledWith('付款成功，票券已出票')
      expect(ordersApi.getMyOrderDetail).toHaveBeenCalledTimes(2)
      expect(wrapper.text()).toContain('已付款')
      expect(wrapper.text()).toContain('查看 QR Code')
      expect(wrapper.text()).not.toContain('保留至')
      expectNoPendingActions(wrapper)
    })

    it('[BW-PENDING-002] 取消訂單先確認，確認後才呼叫取消 API，完成後顯示已取消', async () => {
      const dialog = createDeferred<unknown>()
      vi.spyOn(ElMessageBox, 'confirm').mockReturnValue(dialog.promise as never)
      vi.mocked(ordersApi.getMyOrderDetail)
        .mockResolvedValueOnce(buildPendingOrder())
        .mockResolvedValueOnce(buildPendingOrder({ status: 'Cancelled' }))
      vi.mocked(ordersApi.cancelOrder).mockResolvedValue()
      const { wrapper } = await mountDetailPage()

      await findButton(wrapper, '取消訂單')!.trigger('click')
      await flushPromises()
      expect(ordersApi.cancelOrder).not.toHaveBeenCalled()

      dialog.resolve('confirm')
      await flushPromises()

      expect(ordersApi.cancelOrder).toHaveBeenCalledExactlyOnceWith('order-1')
      expect(ElMessage.success).toHaveBeenCalledWith('訂單已取消')
      expect(wrapper.text()).toContain('已取消')
      expect(wrapper.text()).not.toContain('保留至')
      expectNoPendingActions(wrapper)
    })

    it.each([
      ['Expired', '已逾時'],
      ['Cancelled', '已取消'],
    ])('[BW-PENDING-003] 確認付款失敗且重新查詢為 %s：顯示「%s」，失敗訊息在重新查詢完成後仍存在', async (status, label) => {
      const refresh = createDeferred<MyOrderDetail>()
      vi.mocked(ordersApi.getMyOrderDetail)
        .mockResolvedValueOnce(buildPendingOrder())
        .mockReturnValueOnce(refresh.promise)
      vi.mocked(ordersApi.confirmOrder).mockRejectedValue(new ApiError(409, { detail: '訂單已逾時' }))
      const { wrapper } = await mountDetailPage()

      await findButton(wrapper, '確認付款')!.trigger('click')
      await flushPromises()
      expect(wrapper.text()).not.toContain('訂單已逾時')

      refresh.resolve(buildPendingOrder({ status }))
      await flushPromises()

      expect(wrapper.text()).toContain(label)
      expect(wrapper.text()).toContain('訂單已逾時')
      expect(wrapper.text()).not.toContain('保留至')
      expectNoPendingActions(wrapper)
    })

    it.each(['Paid', 'Cancelled', 'Expired'])('[BW-PENDING-004] %s 訂單不顯示確認付款與取消訂單', async (status) => {
      vi.mocked(ordersApi.getMyOrderDetail).mockResolvedValue(buildPendingOrder({ status }))
      const { wrapper } = await mountDetailPage()

      expect(wrapper.text()).toContain('訂單 Id：order-1')
      expectNoPendingActions(wrapper)
    })

    it('[BW-PENDING-005] 確認付款 API 未回應時兩按鈕皆停用', async () => {
      vi.mocked(ordersApi.getMyOrderDetail).mockResolvedValue(buildPendingOrder())
      vi.mocked(ordersApi.confirmOrder).mockReturnValue(new Promise(() => {}))
      const { wrapper } = await mountDetailPage()

      await findButton(wrapper, '確認付款')!.trigger('click')
      await flushPromises()

      expect(isButtonDisabled(wrapper, '確認付款')).toBe(true)
      expect(isButtonDisabled(wrapper, '取消訂單')).toBe(true)
    })

    it('[BW-PENDING-005] 確認取消後、取消 API 未回應時兩按鈕皆停用', async () => {
      vi.spyOn(ElMessageBox, 'confirm').mockResolvedValue('confirm' as never)
      vi.mocked(ordersApi.getMyOrderDetail).mockResolvedValue(buildPendingOrder())
      vi.mocked(ordersApi.cancelOrder).mockReturnValue(new Promise(() => {}))
      const { wrapper } = await mountDetailPage()

      await findButton(wrapper, '取消訂單')!.trigger('click')
      await flushPromises()

      expect(ordersApi.cancelOrder).toHaveBeenCalledOnce()
      expect(isButtonDisabled(wrapper, '確認付款')).toBe(true)
      expect(isButtonDisabled(wrapper, '取消訂單')).toBe(true)
    })

    it.each(['cancel', 'close'])('[BW-PENDING-006] 確認對話框以 %s 結束時不呼叫取消 API、維持待付款、無錯誤', async (reason) => {
      vi.spyOn(ElMessageBox, 'confirm').mockRejectedValue(reason)
      vi.mocked(ordersApi.getMyOrderDetail).mockResolvedValue(buildPendingOrder())
      const { wrapper } = await mountDetailPage()

      await findButton(wrapper, '取消訂單')!.trigger('click')
      await flushPromises()

      expect(ordersApi.cancelOrder).not.toHaveBeenCalled()
      expect(wrapper.text()).toContain('待付款')
      expect(wrapper.find('.el-alert--error').exists()).toBe(false)
    })

    it('[BW-PENDING-007] 操作後重新查詢期間保留原畫面內容，不顯示整頁載入狀態', async () => {
      const refresh = createDeferred<MyOrderDetail>()
      vi.mocked(ordersApi.getMyOrderDetail)
        .mockResolvedValueOnce(buildPendingOrder())
        .mockReturnValueOnce(refresh.promise)
      vi.mocked(ordersApi.confirmOrder).mockResolvedValue()
      const { wrapper } = await mountDetailPage()
      // 首次載入的遮罩在 jsdom 內要等 Element Plus 的 400ms 離場計時器才會消失，先等它消失，
      // 下面的斷言才能證明重新查詢沒有「重新」顯示整頁載入狀態。
      await vi.waitFor(() => expect(isPageLoadingVisible(wrapper)).toBe(false), { timeout: 5000 })

      await findButton(wrapper, '確認付款')!.trigger('click')
      await flushPromises()

      expect(ordersApi.getMyOrderDetail).toHaveBeenCalledTimes(2)
      expect(wrapper.text()).toContain('訂單 Id：order-1')
      expect(wrapper.text()).toContain('訂單項目')
      expect(isPageLoadingVisible(wrapper)).toBe(false)

      refresh.resolve(buildPaidOrder())
      await flushPromises()
      expect(wrapper.text()).toContain('已付款')
    })

    it('首次載入期間顯示整頁載入狀態（BW-PENDING-007 的對照組，確保上一個斷言不是空洞通過）', async () => {
      vi.mocked(ordersApi.getMyOrderDetail).mockReturnValue(new Promise(() => {}))
      const { wrapper } = await mountDetailPage()

      expect(isPageLoadingVisible(wrapper)).toBe(true)
    })

    it('[BW-PENDING-008] 取消失敗（409）且重新查詢為 Paid：顯示已付款與失敗訊息，無操作', async () => {
      vi.spyOn(ElMessageBox, 'confirm').mockResolvedValue('confirm' as never)
      vi.mocked(ordersApi.getMyOrderDetail)
        .mockResolvedValueOnce(buildPendingOrder())
        .mockResolvedValueOnce(buildPaidOrder())
      vi.mocked(ordersApi.cancelOrder).mockRejectedValue(new ApiError(409, { detail: '訂單狀態已變更' }))
      const { wrapper } = await mountDetailPage()

      await findButton(wrapper, '取消訂單')!.trigger('click')
      await flushPromises()

      expect(wrapper.text()).toContain('已付款')
      expect(wrapper.text()).toContain('訂單狀態已變更')
      expectNoPendingActions(wrapper)
    })

    it('[BW-PENDING-009] 重新查詢失敗時隱藏操作並提供重新整理，重新整理查詢中停用', async () => {
      const manualRefresh = createDeferred<MyOrderDetail>()
      vi.mocked(ordersApi.getMyOrderDetail)
        .mockResolvedValueOnce(buildPendingOrder())
        .mockRejectedValueOnce(new TypeError('Failed to fetch'))
        .mockReturnValueOnce(manualRefresh.promise)
      vi.mocked(ordersApi.confirmOrder).mockResolvedValue()
      const { wrapper } = await mountDetailPage()

      await findButton(wrapper, '確認付款')!.trigger('click')
      await flushPromises()

      expect(wrapper.text()).toContain('載入訂單明細失敗')
      expect(findButton(wrapper, '重新整理')).toBeDefined()
      expect(wrapper.text()).not.toContain('保留至')
      expectNoPendingActions(wrapper)

      await findButton(wrapper, '重新整理')!.trigger('click')
      await flushPromises()
      expect(isButtonDisabled(wrapper, '重新整理')).toBe(true)

      manualRefresh.resolve(buildPaidOrder())
      await flushPromises()
      expect(wrapper.text()).toContain('已付款')
      expect(findButton(wrapper, '重新整理')).toBeUndefined()
    })

    // 隱藏只針對「畫面可能過時」的期間：重新整理取得仍為 Pending 的最新資料後，保留時間與操作都必須回來，
    // 否則買家會卡在無法付款的狀態。
    it('[BW-PENDING-009] 重新查詢失敗後重新整理仍為 Pending：保留至與操作按鈕恢復顯示', async () => {
      vi.mocked(ordersApi.getMyOrderDetail)
        .mockResolvedValueOnce(buildPendingOrder())
        .mockRejectedValueOnce(new TypeError('Failed to fetch'))
        .mockResolvedValueOnce(buildPendingOrder())
      vi.mocked(ordersApi.confirmOrder).mockRejectedValue(new ApiError(409, { detail: '付款被拒' }))
      const { wrapper } = await mountDetailPage()

      await findButton(wrapper, '確認付款')!.trigger('click')
      await flushPromises()
      expect(wrapper.text()).not.toContain('保留至')

      await findButton(wrapper, '重新整理')!.trigger('click')
      await flushPromises()

      expect(wrapper.text()).toContain(HELD_UNTIL_TEXT)
      expect(isButtonDisabled(wrapper, '確認付款')).toBe(false)
      expect(isButtonDisabled(wrapper, '取消訂單')).toBe(false)
      expect(findButton(wrapper, '重新整理')).toBeUndefined()
    })

    it('[BW-PENDING-010] 切換訂單時較晚回來的舊訂單回應不覆蓋畫面', async () => {
      const detailA = createDeferred<MyOrderDetail>()
      const detailB = createDeferred<MyOrderDetail>()
      vi.mocked(ordersApi.getMyOrderDetail).mockImplementation((id) =>
        id === 'order-a' ? detailA.promise : detailB.promise,
      )
      const { wrapper, router } = await mountDetailPage('/orders/order-a')

      await router.push('/orders/order-b')
      await flushPromises()
      expect(ordersApi.getMyOrderDetail).toHaveBeenCalledWith('order-b')

      detailB.resolve(buildPaidOrder({ id: 'order-b' }))
      await flushPromises()
      detailA.resolve(buildPendingOrder({ id: 'order-a' }))
      await flushPromises()

      expect(wrapper.text()).toContain('訂單 Id：order-b')
      expect(wrapper.text()).not.toContain('order-a')
      expect(wrapper.text()).toContain('已付款')
      expectNoPendingActions(wrapper)
    })

    it('[BW-PENDING-011] 確認付款遭頻率限制（429）：重新查詢並顯示固定文案', async () => {
      vi.mocked(ordersApi.getMyOrderDetail).mockResolvedValue(buildPendingOrder())
      vi.mocked(ordersApi.confirmOrder).mockRejectedValue(new ApiError(429, { detail: 'Too Many Requests' }))
      const { wrapper } = await mountDetailPage()

      await findButton(wrapper, '確認付款')!.trigger('click')
      await flushPromises()

      expect(ordersApi.getMyOrderDetail).toHaveBeenCalledTimes(2)
      expect(wrapper.text()).toContain('請求過於頻繁，請稍後再試')
      expect(wrapper.text()).not.toContain('Too Many Requests')
    })

    it('[BW-PENDING-011] 取消訂單遭頻率限制（429）：重新查詢並顯示固定文案', async () => {
      vi.spyOn(ElMessageBox, 'confirm').mockResolvedValue('confirm' as never)
      vi.mocked(ordersApi.getMyOrderDetail).mockResolvedValue(buildPendingOrder())
      vi.mocked(ordersApi.cancelOrder).mockRejectedValue(new ApiError(429, { detail: 'Too Many Requests' }))
      const { wrapper } = await mountDetailPage()

      await findButton(wrapper, '取消訂單')!.trigger('click')
      await flushPromises()

      expect(ordersApi.getMyOrderDetail).toHaveBeenCalledTimes(2)
      expect(wrapper.text()).toContain('請求過於頻繁，請稍後再試')
    })

    it('[BW-PENDING-012] 付款被拒（409）且仍為 Pending：顯示失敗訊息、保留至與可用的操作，可再次嘗試', async () => {
      vi.mocked(ordersApi.getMyOrderDetail)
        .mockResolvedValueOnce(buildPendingOrder())
        .mockResolvedValueOnce(buildPendingOrder())
        .mockResolvedValueOnce(buildPaidOrder())
      vi.mocked(ordersApi.confirmOrder)
        .mockRejectedValueOnce(new ApiError(409, { detail: '付款被拒' }))
        .mockResolvedValueOnce()
      const { wrapper } = await mountDetailPage()

      await findButton(wrapper, '確認付款')!.trigger('click')
      await flushPromises()

      expect(wrapper.text()).toContain('待付款')
      expect(wrapper.text()).toContain(HELD_UNTIL_TEXT)
      expect(wrapper.text()).toContain('付款被拒')
      expect(isButtonDisabled(wrapper, '確認付款')).toBe(false)
      expect(isButtonDisabled(wrapper, '取消訂單')).toBe(false)

      await findButton(wrapper, '確認付款')!.trigger('click')
      await flushPromises()

      expect(ordersApi.confirmOrder).toHaveBeenCalledTimes(2)
      expect(wrapper.text()).toContain('已付款')
    })

    it.each([
      ['resolve', undefined],
      ['reject', new ApiError(409, { detail: '付款被拒' })],
    ])('[BW-PENDING-013] 切換訂單後舊訂單的確認回應（%s）不顯示提示、不重新查詢舊訂單', async (outcome, error) => {
      const confirmA = createDeferred<void>()
      vi.mocked(ordersApi.getMyOrderDetail).mockImplementation(async (id) =>
        id === 'order-a' ? buildPendingOrder({ id: 'order-a' }) : buildPaidOrder({ id: 'order-b' }),
      )
      vi.mocked(ordersApi.confirmOrder).mockReturnValue(confirmA.promise)
      const { wrapper, router } = await mountDetailPage('/orders/order-a')

      await findButton(wrapper, '確認付款')!.trigger('click')
      await flushPromises()
      await router.push('/orders/order-b')
      await flushPromises()
      const detailCallsForA = vi.mocked(ordersApi.getMyOrderDetail).mock.calls.filter(([id]) => id === 'order-a').length

      if (outcome === 'resolve') confirmA.resolve()
      else confirmA.reject(error)
      await flushPromises()

      expect(ElMessage.success).not.toHaveBeenCalled()
      expect(wrapper.find('.el-alert--error').exists()).toBe(false)
      expect(vi.mocked(ordersApi.getMyOrderDetail).mock.calls.filter(([id]) => id === 'order-a')).toHaveLength(
        detailCallsForA,
      )
      expect(wrapper.text()).toContain('訂單 Id：order-b')
      expect(wrapper.text()).toContain('已付款')
    })
  })

  // design.md 決策 2：卸載後才回來的回應不得寫入狀態或觸發提示，取消確認對話框不得殘留到下一頁。
  describe('卸載', () => {
    it('確認付款未回應時卸載，之後回應不拋錯也不顯示成功提示', async () => {
      const confirmCall = createDeferred<void>()
      vi.mocked(ordersApi.getMyOrderDetail).mockResolvedValue(buildPendingOrder())
      vi.mocked(ordersApi.confirmOrder).mockReturnValue(confirmCall.promise)
      const { wrapper } = await mountDetailPage()

      await findButton(wrapper, '確認付款')!.trigger('click')
      await flushPromises()
      wrapper.unmount()

      confirmCall.resolve()
      await flushPromises()

      expect(ElMessage.success).not.toHaveBeenCalled()
      expect(ordersApi.getMyOrderDetail).toHaveBeenCalledOnce()
    })

    it('取消確認對話框開啟中卸載：關閉對話框，之後以 close 結束不呼叫取消 API', async () => {
      const dialog = createDeferred<unknown>()
      vi.spyOn(ElMessageBox, 'confirm').mockReturnValue(dialog.promise as never)
      const closeSpy = vi.spyOn(ElMessageBox, 'close')
      vi.mocked(ordersApi.getMyOrderDetail).mockResolvedValue(buildPendingOrder())
      const { wrapper } = await mountDetailPage()

      await findButton(wrapper, '取消訂單')!.trigger('click')
      await flushPromises()
      wrapper.unmount()
      expect(closeSpy).toHaveBeenCalled()

      dialog.reject('close')
      await flushPromises()

      expect(ordersApi.cancelOrder).not.toHaveBeenCalled()
    })
  })
})
