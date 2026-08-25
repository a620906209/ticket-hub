## Why

買家下單、確認付款、系統出票之後，目前完全沒有管道查詢自己的訂單或票券——`OrdersController` 只有下單/確認/取消三個 `POST` 端點，沒有任何查詢端點；訂單查詢 API 目前只存在於 `order-administration`（Admin-only，一般會員呼叫會被拒絕）。`buyer-web-ui` 的「我的訂單」列表頁與明細頁也僅顯示固定空狀態，等待後端支援（見 `buyer-web-ui` spec 現有需求）。這使得 Phase 1 Must 的核心流程「建立活動 → 買家下單 → 核銷」在買家端實質斷點：買家拿不到自己的 Ticket ID 或 QR Code，核銷這一步只能靠 Admin 端工具或直接查資料庫才能示範，不是買家能自己走完的流程。

## What Changes

- 新增買家專屬的訂單查詢 API：查詢自己的訂單列表、查詢自己單筆訂單明細（含每個項目對應的票券狀態）
- 新增依票券 ID 取得 QR Code 圖檔的買家端點，直接回傳 PNG 圖檔（重用既有 `ticket-issuance` 能力已實作但目前無呼叫端的 `ITicketSigningService`／`TicketQrCodeGenerator`）
- 買家只能查詢/存取自己身份為買家的訂單與其票券；查詢他人訂單 MUST 被拒絕（回應語意需與現有 `ticket-purchase` 能力的「非本人」情境一致）
- `buyer-web-ui` 的「我的訂單」列表頁與明細頁改為串接新查詢 API，移除現行的固定空狀態，明細頁需顯示票券狀態並提供 QR Code 顯示（`Issued` 狀態的票券）

## Capabilities

### New Capabilities
- `buyer-order-query`：買家專屬的訂單/票券查詢能力——列出自己的訂單、查詢單筆訂單明細（含票券狀態）、依票券 ID 取得 QR Code 圖檔；所有查詢皆限定呼叫者自己的買家身份，非本人查詢 MUST 被拒絕

### Modified Capabilities
- `buyer-web-ui`：「我的訂單」列表與明細頁的既有需求（目前規格為「本輪僅顯示空狀態，待後端補齊查詢 API 後再串接」）改為串接 `buyer-order-query` 新增的 API，顯示真實訂單資料、票券狀態，並在明細頁顯示已出票票券的 QR Code

## Impact

- **後端**：新增買家端 Controller（或擴充 `OrdersController`）與對應的 Application handler；`GetOrdersHandler`/`GetOrderByIdHandler`（現屬 `order-administration`，無買家身份過濾）不可直接沿用於買家端，需要新增依 `BuyerId` 過濾的查詢邏輯，避免任一買家查到他人訂單；QR Code 端點直接重用既有 `ITicketSigningService`／`TicketQrCodeGenerator`（`ticket-issuance` 能力已實作，目前無呼叫端）
- **前端**：`buyer-web-ui` 我的訂單列表頁、訂單明細頁改為呼叫新 API，明細頁新增票券狀態與 QR Code 顯示區塊
- **不受影響**：下單、確認、取消訂單既有 API 與流程；`ticket-issuance`／`ticket-redemption` 能力本身的規則不變，僅新增一個消費既有 QR 產生能力的呼叫端
