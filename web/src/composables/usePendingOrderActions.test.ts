import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { defineComponent } from 'vue'
import { ElMessageBox } from 'element-plus'
import { usePendingOrderActions } from './usePendingOrderActions'
import * as ordersApi from '../api/orders'
import { buildPendingOrder, mountOrderPageAt } from '../pages/buyer/orderPageTestSupport'

vi.mock('../api/orders')

let actions: ReturnType<typeof usePendingOrderActions>

const HostPage = defineComponent({
  setup() {
    actions = usePendingOrderActions()
    return () => null
  },
})

// 錯誤處理／防禦性測試（design.md 決策 5、CLAUDE.md「禁止靜默失敗」）：只有使用者拒絕
// （'cancel'／'close'）屬正常流程，其他 reject 值是非預期例外，吞掉會讓真正的錯誤無聲消失。
describe('usePendingOrderActions 取消確認對話框的例外邊界', () => {
  beforeEach(() => {
    vi.mocked(ordersApi.getMyOrderDetail).mockReset().mockResolvedValue(buildPendingOrder())
    vi.mocked(ordersApi.cancelOrder).mockReset()
  })

  afterEach(() => {
    vi.restoreAllMocks()
  })

  it('cancelPendingOrder_WhenConfirmRejectsWithUnexpectedError_RethrowsAndDoesNotCancel', async () => {
    vi.spyOn(ElMessageBox, 'confirm').mockRejectedValue(new Error('boom'))
    await mountOrderPageAt('/orders/order-1', '/orders/:id', HostPage)

    await expect(actions.cancelPendingOrder()).rejects.toThrow('boom')
    expect(ordersApi.cancelOrder).not.toHaveBeenCalled()
  })

  it.each(['cancel', 'close'])('cancelPendingOrder_WhenUserDismissesWith%s_ResolvesWithoutCancel', async (reason) => {
    vi.spyOn(ElMessageBox, 'confirm').mockRejectedValue(reason)
    await mountOrderPageAt('/orders/order-1', '/orders/:id', HostPage)

    await expect(actions.cancelPendingOrder()).resolves.toBeUndefined()
    expect(ordersApi.cancelOrder).not.toHaveBeenCalled()
  })
})
