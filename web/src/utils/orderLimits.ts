// 必須與後端 PlaceOrderLimits.MaxTicketsPerOrder（src/ProjectC.Application/Orders/PlaceOrder/PlaceOrderLimits.cs）一致。
// 前端無法共用 C# 常數，只能寫死這個值；後端常數改變時這裡要一起改（frontend-order-limit-hints design.md 決策 2）。
export const SYSTEM_MAX_TICKETS_PER_ORDER = 50
