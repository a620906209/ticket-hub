import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { flushPromises } from '@vue/test-utils'
import { ElMessage, ElMessageBox } from 'element-plus'
import OrderResultPage from './OrderResultPage.vue'
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

function mountResultPage(path = '/order-result/order-1') {
  return mountOrderPageAt(path, '/order-result/:id', OrderResultPage)
}

describe('OrderResultPage 訂單結果頁', () => {
  beforeEach(() => {
    vi.mocked(ordersApi.getMyOrderDetail).mockReset()
    vi.mocked(ordersApi.confirmOrder).mockReset()
    vi.mocked(ordersApi.cancelOrder).mockReset()
    vi.spyOn(ElMessage, 'success').mockImplementation(() => ({ close: () => {} }) as never)
  })

  afterEach(() => {
    vi.restoreAllMocks()
  })

  it('[BW-RESULT-004] 確認付款成功後重新查詢並顯示已付款，不再顯示操作', async () => {
    vi.mocked(ordersApi.getMyOrderDetail)
      .mockResolvedValueOnce(buildPendingOrder())
      .mockResolvedValueOnce(buildPaidOrder())
    vi.mocked(ordersApi.confirmOrder).mockResolvedValue()
    const { wrapper } = await mountResultPage()

    await findButton(wrapper, '確認付款')!.trigger('click')
    await flushPromises()

    expect(ordersApi.confirmOrder).toHaveBeenCalledExactlyOnceWith('order-1')
    expect(ElMessage.success).toHaveBeenCalledWith('付款成功，票券已出票')
    expect(ordersApi.getMyOrderDetail).toHaveBeenCalledTimes(2)
    expect(wrapper.text()).toContain('已付款')
    expect(findButton(wrapper, '確認付款')).toBeUndefined()
    expect(findButton(wrapper, '取消訂單')).toBeUndefined()
  })

  it('[BW-RESULT-005] 取消訂單先顯示確認對話框，確認後才呼叫取消 API', async () => {
    const dialog = createDeferred<unknown>()
    const confirmSpy = vi.spyOn(ElMessageBox, 'confirm').mockReturnValue(dialog.promise as never)
    vi.mocked(ordersApi.getMyOrderDetail)
      .mockResolvedValueOnce(buildPendingOrder())
      .mockResolvedValueOnce(buildPendingOrder({ status: 'Cancelled' }))
    vi.mocked(ordersApi.cancelOrder).mockResolvedValue()
    const { wrapper } = await mountResultPage()

    await findButton(wrapper, '取消訂單')!.trigger('click')
    await flushPromises()
    expect(confirmSpy).toHaveBeenCalledWith(
      expect.any(String),
      expect.any(String),
      expect.objectContaining({ confirmButtonText: '取消訂單', cancelButtonText: '保留訂單' }),
    )
    expect(ordersApi.cancelOrder).not.toHaveBeenCalled()

    dialog.resolve('confirm')
    await flushPromises()

    expect(ordersApi.cancelOrder).toHaveBeenCalledExactlyOnceWith('order-1')
    expect(wrapper.text()).toContain('已取消')
    expect(ElMessage.success).toHaveBeenCalledWith('訂單已取消')
  })

  it('[BW-RESULT-005] 確認對話框選「保留訂單」時不呼叫取消 API、不顯示錯誤', async () => {
    vi.spyOn(ElMessageBox, 'confirm').mockRejectedValue('cancel')
    vi.mocked(ordersApi.getMyOrderDetail).mockResolvedValue(buildPendingOrder())
    const { wrapper } = await mountResultPage()

    await findButton(wrapper, '取消訂單')!.trigger('click')
    await flushPromises()

    expect(ordersApi.cancelOrder).not.toHaveBeenCalled()
    expect(wrapper.find('.el-alert--error').exists()).toBe(false)
    expect(wrapper.text()).toContain('待付款')
  })

  it('確認付款 API 未回應時兩個按鈕皆停用；重新查詢未回應時訂單 Id 仍顯示', async () => {
    const confirmCall = createDeferred<void>()
    const refresh = createDeferred<MyOrderDetail>()
    vi.mocked(ordersApi.getMyOrderDetail)
      .mockResolvedValueOnce(buildPendingOrder())
      .mockReturnValueOnce(refresh.promise)
    vi.mocked(ordersApi.confirmOrder).mockReturnValue(confirmCall.promise)
    const { wrapper } = await mountResultPage()

    await findButton(wrapper, '確認付款')!.trigger('click')
    await flushPromises()
    expect(isButtonDisabled(wrapper, '確認付款')).toBe(true)
    expect(isButtonDisabled(wrapper, '取消訂單')).toBe(true)

    confirmCall.resolve()
    await flushPromises()
    expect(wrapper.text()).toContain('訂單 Id：order-1')

    refresh.resolve(buildPaidOrder())
    await flushPromises()
    expect(wrapper.text()).toContain('已付款')
  })

  it('[BW-RESULT-001] 重新整理已付款訂單的結果頁：顯示已付款、無操作、無保留至', async () => {
    vi.mocked(ordersApi.getMyOrderDetail).mockResolvedValue(buildPaidOrder())
    const { wrapper } = await mountResultPage()

    expect(wrapper.text()).toContain('已付款')
    expect(wrapper.text()).not.toContain('保留至')
    expect(findButton(wrapper, '確認付款')).toBeUndefined()
    expect(findButton(wrapper, '取消訂單')).toBeUndefined()
  })

  it('[BW-RESULT-002] 保留至時間取自明細 API，不讀取路由 query 的 heldUntilUtc', async () => {
    vi.mocked(ordersApi.getMyOrderDetail).mockResolvedValue(buildPendingOrder())
    const staleQueryValue = '2000-01-01T00:00:00Z'
    const { wrapper } = await mountResultPage(`/order-result/order-1?heldUntilUtc=${staleQueryValue}`)

    expect(ordersApi.getMyOrderDetail).toHaveBeenCalledWith('order-1')
    expect(wrapper.text()).toContain('訂單 Id：order-1')
    expect(wrapper.text()).toContain('待付款')
    expect(wrapper.text()).toContain(HELD_UNTIL_TEXT)
    expect(wrapper.text()).not.toContain(new Date(staleQueryValue).toLocaleString())
  })

  it.each([
    ['Expired', '已逾時'],
    ['Cancelled', '已取消'],
  ])('[BW-RESULT-003] 確認付款失敗且重新查詢為 %s：顯示「%s」與失敗訊息，失敗訊息在重新查詢後才設定', async (status, label) => {
    const refresh = createDeferred<MyOrderDetail>()
    vi.mocked(ordersApi.getMyOrderDetail)
      .mockResolvedValueOnce(buildPendingOrder())
      .mockReturnValueOnce(refresh.promise)
    vi.mocked(ordersApi.confirmOrder).mockRejectedValue(new ApiError(409, { detail: '訂單已逾時' }))
    const { wrapper } = await mountResultPage()

    await findButton(wrapper, '確認付款')!.trigger('click')
    await flushPromises()
    expect(wrapper.text()).not.toContain('訂單已逾時')

    refresh.resolve(buildPendingOrder({ status }))
    await flushPromises()

    expect(wrapper.text()).toContain(label)
    expect(wrapper.text()).toContain('訂單已逾時')
    expect(wrapper.text()).not.toContain('保留至')
    expect(findButton(wrapper, '確認付款')).toBeUndefined()
    expect(findButton(wrapper, '取消訂單')).toBeUndefined()
  })

  it('[BW-RESULT-006] 付款被拒（409）且仍為 Pending：顯示失敗訊息並允許再次嘗試', async () => {
    vi.mocked(ordersApi.getMyOrderDetail)
      .mockResolvedValueOnce(buildPendingOrder())
      .mockResolvedValueOnce(buildPendingOrder())
      .mockResolvedValueOnce(buildPaidOrder())
    vi.mocked(ordersApi.confirmOrder)
      .mockRejectedValueOnce(new ApiError(409, { detail: '付款被拒' }))
      .mockResolvedValueOnce()
    const { wrapper } = await mountResultPage()

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

  // 前端刻意不做 GUID 格式檢查，唯一的格式驗證層是後端路由約束（design.md 安全確認）。
  it.each(['00000000-0000-0000-0000-000000000000', 'not-a-guid'])(
    '[BW-RESULT-007] 以 %s 開啟結果頁、明細 API 回 404：顯示找不到提示與返回連結，無訂單資料與操作',
    async (orderId) => {
      vi.mocked(ordersApi.getMyOrderDetail).mockRejectedValue(new ApiError(404, { detail: 'not found' }))
      const { wrapper } = await mountResultPage(`/order-result/${orderId}`)

      expect(ordersApi.getMyOrderDetail).toHaveBeenCalledWith(orderId)
      expect(wrapper.text()).toContain('找不到這筆訂單')
      expect(wrapper.text()).toContain('返回我的訂單')
      expect(wrapper.text()).not.toContain('訂單 Id：')
      expect(findButton(wrapper, '確認付款')).toBeUndefined()
      expect(findButton(wrapper, '取消訂單')).toBeUndefined()
    },
  )

  it('[BW-RESULT-008] 明細 API 回 403：顯示無權限提示與返回連結，無訂單資料與操作', async () => {
    vi.mocked(ordersApi.getMyOrderDetail).mockRejectedValue(new ApiError(403, { detail: 'forbidden' }))
    const { wrapper } = await mountResultPage()

    expect(wrapper.text()).toContain('你沒有權限查看這筆訂單')
    expect(wrapper.text()).toContain('返回我的訂單')
    expect(wrapper.text()).not.toContain('訂單 Id：')
    expect(findButton(wrapper, '確認付款')).toBeUndefined()
    expect(findButton(wrapper, '取消訂單')).toBeUndefined()
  })

  it('[BW-RESULT-009] 確認付款成功但重新查詢失敗：顯示錯誤與重新整理，隱藏操作', async () => {
    vi.mocked(ordersApi.getMyOrderDetail)
      .mockResolvedValueOnce(buildPendingOrder())
      .mockRejectedValueOnce(new TypeError('Failed to fetch'))
    vi.mocked(ordersApi.confirmOrder).mockResolvedValue()
    const { wrapper } = await mountResultPage()

    await findButton(wrapper, '確認付款')!.trigger('click')
    await flushPromises()

    expect(wrapper.text()).toContain('載入訂單明細失敗')
    expect(findButton(wrapper, '重新整理')).toBeDefined()
    expect(wrapper.text()).not.toContain('保留至')
    expect(findButton(wrapper, '確認付款')).toBeUndefined()
    expect(findButton(wrapper, '取消訂單')).toBeUndefined()
  })

  it('[BW-RESULT-010] 切換訂單時較晚回來的舊訂單回應不覆蓋畫面', async () => {
    const detailA = createDeferred<MyOrderDetail>()
    const detailB = createDeferred<MyOrderDetail>()
    vi.mocked(ordersApi.getMyOrderDetail).mockImplementation((id) => (id === 'order-a' ? detailA.promise : detailB.promise))
    const { wrapper, router } = await mountResultPage('/order-result/order-a')

    await router.push('/order-result/order-b')
    await flushPromises()
    expect(ordersApi.getMyOrderDetail).toHaveBeenCalledWith('order-b')

    detailB.resolve(buildPaidOrder({ id: 'order-b' }))
    await flushPromises()
    detailA.resolve(buildPendingOrder({ id: 'order-a' }))
    await flushPromises()

    expect(wrapper.text()).toContain('訂單 Id：order-b')
    expect(wrapper.text()).not.toContain('order-a')
    expect(wrapper.text()).toContain('已付款')
    expect(findButton(wrapper, '確認付款')).toBeUndefined()
    expect(findButton(wrapper, '取消訂單')).toBeUndefined()
  })
})
