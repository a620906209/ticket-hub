// 必須與後端 CreateOrderHandler.HoldDuration（src/ProjectC.Application/Orders/CreateOrderHandler.cs）一致。
// 下單 API 回應不含到期時間，前端只能寫死這個值；後端常數改變時這裡要一起改（order-confirmation-page design.md 決策 5）。
export const ORDER_HOLD_MINUTES = 10
