import { defineStore } from 'pinia'
import { ref } from 'vue'

// 只在記憶體中保存：重新整理後清空是預期行為，避免舊訂單的「保留 10 分鐘」提示在過期後才出現（design.md 決策 4）。
export const usePlacedOrderNoticeStore = defineStore('placedOrderNotice', () => {
  const placedOrderId = ref<string | null>(null)

  function setPlacedOrder(orderId: string): void {
    placedOrderId.value = orderId
  }

  /** 取出剛建立的訂單 Id 並清空，確保活動列表的付款提示只顯示一次。 */
  function consumePlacedOrder(): string | null {
    const orderId = placedOrderId.value
    placedOrderId.value = null
    return orderId
  }

  function clearPlacedOrder(): void {
    placedOrderId.value = null
  }

  return { placedOrderId, setPlacedOrder, consumePlacedOrder, clearPlacedOrder }
})
