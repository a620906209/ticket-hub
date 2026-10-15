import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { enableAutoUnmount, flushPromises, mount } from '@vue/test-utils'
import { RouterView } from 'vue-router'
import { createPinia, setActivePinia } from 'pinia'
import type { Pinia } from 'pinia'
import ElementPlus from 'element-plus'
import router from './index'
import { useAuthStore } from '../stores/auth'
import * as ordersApi from '../api/orders'
import { ApiError } from '../api/httpClient'
import { buildPendingOrder } from '../pages/buyer/orderPageTestSupport'

vi.mock('../api/orders')

enableAutoUnmount(afterEach)

// 同 index.test.ts：假 JWT 只需可解碼，不驗簽章。
function signIn(): void {
  const authStore = useAuthStore()
  authStore.accessToken = `header.${btoa(JSON.stringify({ sub: '1' }))}.signature`
  authStore.member = { id: '1', email: 'a@example.com', displayName: 'A', role: 'Member', isActive: true, hasRegisteredRealName: false, realName: null, nationalIdLast4Masked: null }
}

let testPinia: Pinia

async function openOldResultUrl(orderId: string) {
  await router.push(`/order-result/${orderId}`)
  const wrapper = mount(RouterView, { global: { plugins: [ElementPlus, router, testPinia] } })
  await flushPromises()
  return wrapper
}

// 單看路由解析只能證明「導到 order-detail」；這裡用真實 app router 與真實明細頁，確認舊網址帶的 Id
// 一路傳到明細頁的查詢，且找不到／無權限時沿用明細頁的提示、不露出訂單內容（BW-RESULT-REDIRECT-001、003）。
describe('舊的下單結果頁網址導向訂單明細頁（order-confirmation-page）', { timeout: 30_000 }, () => {
  beforeEach(async () => {
    testPinia = createPinia()
    setActivePinia(testPinia)
    vi.mocked(ordersApi.getMyOrderDetail).mockReset()
    signIn()
    // 先回到中性路徑，避免與上一個測試的目的地相同時 vue-router 略過導覽。
    await router.push('/')
  })

  function expectRedirectedToOrderDetailWith(orderId: string) {
    expect(router.currentRoute.value.name).toBe('order-detail')
    expect(router.currentRoute.value.params.id).toBe(orderId)
    expect(ordersApi.getMyOrderDetail).toHaveBeenCalledOnce()
    expect(vi.mocked(ordersApi.getMyOrderDetail).mock.calls[0][0]).toBe(orderId)
  }

  it('[BW-RESULT-REDIRECT-001] 本人訂單：顯示該筆訂單明細', async () => {
    const orderId = '3f2504e0-4f89-11d3-9a0c-0305e82c3301'
    vi.mocked(ordersApi.getMyOrderDetail).mockResolvedValue(buildPendingOrder({ id: orderId }))

    const wrapper = await openOldResultUrl(orderId)

    expectRedirectedToOrderDetailWith(orderId)
    expect(wrapper.text()).toContain(`訂單 Id：${orderId}`)
  })

  it('[BW-RESULT-REDIRECT-003] 他人訂單：顯示無權限提示，不顯示訂單內容', async () => {
    const orderId = '3f2504e0-4f89-11d3-9a0c-0305e82c3302'
    vi.mocked(ordersApi.getMyOrderDetail).mockRejectedValue(new ApiError(403, null))

    const wrapper = await openOldResultUrl(orderId)

    expectRedirectedToOrderDetailWith(orderId)
    expect(wrapper.text()).toContain('你沒有權限查看這筆訂單')
    expect(wrapper.text()).not.toContain('訂單 Id：')
  })

  it.each(['00000000-0000-0000-0000-000000000000', 'not-a-guid'])(
    '[BW-RESULT-REDIRECT-003] 不存在或非 GUID 的 Id（%s）：顯示找不到提示，不顯示訂單內容',
    async (orderId) => {
      vi.mocked(ordersApi.getMyOrderDetail).mockRejectedValue(new ApiError(404, null))

      const wrapper = await openOldResultUrl(orderId)

      expectRedirectedToOrderDetailWith(orderId)
      expect(wrapper.text()).toContain('找不到這筆訂單')
      expect(wrapper.text()).not.toContain('訂單 Id：')
    },
  )
})
