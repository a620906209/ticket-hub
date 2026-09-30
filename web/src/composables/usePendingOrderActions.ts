import { computed, onBeforeUnmount, ref, watch } from 'vue'
import { useRoute } from 'vue-router'
import { ElMessage, ElMessageBox } from 'element-plus'
import { cancelOrder, confirmOrder, getMyOrderDetail } from '../api/orders'
import { ApiError } from '../api/httpClient'
import type { MyOrderDetail } from '../types/apiResponses'
import { toErrorMessage } from '../utils/errors'

type PendingOrderAction = 'confirm' | 'cancel'

const RATE_LIMITED_MESSAGE = '請求過於頻繁，請稍後再試'

function toDetailErrorMessage(error: unknown): string {
  if (error instanceof ApiError && error.status === 404) return '找不到這筆訂單'
  if (error instanceof ApiError && error.status === 403) return '你沒有權限查看這筆訂單'
  return toErrorMessage(error, '載入訂單明細失敗')
}

function toActionErrorMessage(error: unknown, fallback: string): string {
  if (error instanceof ApiError && error.status === 429) return RATE_LIMITED_MESSAGE
  return toErrorMessage(error, fallback)
}

function isUserDismissal(reason: unknown): boolean {
  return reason === 'cancel' || reason === 'close'
}

// OrderResultPage 與 OrderDetailPage 共用「載入明細＋確認＋取消＋重新查詢」流程
// （order-pending-actions design.md 決策 2）。操作後一律重新查詢、以伺服器狀態為準，
// 前端不依錯誤類型推測訂單新狀態。
export function usePendingOrderActions() {
  const route = useRoute()
  // 只在同一條路由內切換 id 時才視為換訂單：離開到其他路由時 watcher 可能在卸載前先觸發，
  // 若不比對路由名稱，會以下一頁的 params.id 多送一次無用的明細請求。
  const routeName = route.name
  const orderId = ref(String(route.params.id))

  const order = ref<MyOrderDetail | null>(null)
  const isInitialLoading = ref(false)
  const isRefreshing = ref(false)
  const hasRefreshFailed = ref(false)
  const loadErrorMessage = ref('')
  const actionErrorMessage = ref('')
  const activeAction = ref<PendingOrderAction | null>(null)

  // contextVersion：切換訂單或卸載時遞增，丟棄舊訂單進行中的操作回應；
  // detailRequestVersion：每次明細請求遞增，只採用最新一次回應（沿用 OrderDetailPage 的 qrRequestVersion 模式）。
  let contextVersion = 0
  let detailRequestVersion = 0

  // 重新查詢失敗時畫面可能已過時：不讓使用者依過時狀態再次送出，也不顯示可能已失效的保留時間，
  // 避免訂單其實已付款或已取消時仍讓買家以為在保留中。
  const isCurrentPending = computed(() => order.value?.status === 'Pending' && !hasRefreshFailed.value)
  const canAct = isCurrentPending
  const canShowHeldUntil = isCurrentPending

  async function fetchDetail(mode: 'initial' | 'refresh'): Promise<void> {
    const requestVersion = ++detailRequestVersion
    const requestedOrderId = orderId.value
    if (mode === 'initial') {
      order.value = null
      isInitialLoading.value = true
    } else {
      isRefreshing.value = true
    }
    try {
      const detail = await getMyOrderDetail(requestedOrderId)
      if (requestVersion !== detailRequestVersion) return
      order.value = detail
      hasRefreshFailed.value = false
      loadErrorMessage.value = ''
    } catch (error) {
      if (requestVersion !== detailRequestVersion) return
      loadErrorMessage.value = toDetailErrorMessage(error)
      if (mode === 'refresh') hasRefreshFailed.value = true
    } finally {
      if (requestVersion === detailRequestVersion) {
        isInitialLoading.value = false
        isRefreshing.value = false
      }
    }
  }

  async function runAction(
    action: PendingOrderAction,
    callApi: (id: string) => Promise<void>,
    successMessage: string,
    failureFallback: string,
  ): Promise<void> {
    const actionContextVersion = contextVersion
    activeAction.value = action
    actionErrorMessage.value = ''
    let failureMessage = ''
    try {
      await callApi(orderId.value)
      if (actionContextVersion !== contextVersion) return
      ElMessage.success(successMessage)
    } catch (error) {
      if (actionContextVersion !== contextVersion) return
      failureMessage = toActionErrorMessage(error, failureFallback)
    }
    await fetchDetail('refresh')
    if (actionContextVersion !== contextVersion) return
    // 失敗訊息必須在重新查詢之後才設定，否則會被載入流程清掉（EventDetailPage 曾踩過同一個順序問題）。
    actionErrorMessage.value = failureMessage
    activeAction.value = null
  }

  function confirmPayment(): Promise<void> {
    return runAction('confirm', confirmOrder, '付款成功，票券已出票', '確認付款失敗')
  }

  async function cancelPendingOrder(): Promise<void> {
    const actionContextVersion = contextVersion
    try {
      // 按鈕文字刻意不用「確定／取消」：在「取消訂單？」語境下「取消」按鈕語意含糊（design.md 決策 5）。
      await ElMessageBox.confirm('取消後座位將釋出，且無法復原。', '取消訂單？', {
        confirmButtonText: '取消訂單',
        cancelButtonText: '保留訂單',
        confirmButtonType: 'danger',
        type: 'warning',
      })
    } catch (reason) {
      if (isUserDismissal(reason)) return
      throw reason
    }
    if (actionContextVersion !== contextVersion) return
    await runAction('cancel', cancelOrder, '訂單已取消', '取消訂單失敗')
  }

  function refreshOrder(): Promise<void> {
    actionErrorMessage.value = ''
    return fetchDetail('refresh')
  }

  function discardInFlightWork(): void {
    contextVersion += 1
    detailRequestVersion += 1
    ElMessageBox.close()
  }

  watch(
    () => route.params.id,
    (id) => {
      if (route.name !== routeName || typeof id !== 'string' || id === orderId.value) return
      discardInFlightWork()
      orderId.value = id
      activeAction.value = null
      hasRefreshFailed.value = false
      isRefreshing.value = false
      loadErrorMessage.value = ''
      actionErrorMessage.value = ''
      void fetchDetail('initial')
    },
  )

  onBeforeUnmount(discardInFlightWork)

  void fetchDetail('initial')

  return {
    orderId,
    order,
    isInitialLoading,
    isRefreshing,
    hasRefreshFailed,
    loadErrorMessage,
    actionErrorMessage,
    activeAction,
    canAct,
    canShowHeldUntil,
    confirmPayment,
    cancelPendingOrder,
    refreshOrder,
  }
}
