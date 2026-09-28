## Why

`event-management-organizer-scoping` 已經把活動／票種／場館的後台管理端點改為「已切換至一個 Approved Organizer」，但訂單查詢（`order-administration`）、票券核銷（`ticket-redemption`）、銷售報表（`sales-report`）這三個能力仍維持純「Admin 角色」授權，沒有租戶邊界。

若不處理，會有兩個問題：(1) 多租戶隔離不完整——既有 Admin 帳號仍能看穿所有租戶的訂單／核銷／銷售資料，牴觸整個計畫「收斂 Admin 為僅平台治理」的目的；(2) 更嚴重的是，自助申請成為主辦方的一般 Member（非 `Admin` 角色）在切換至自己的 Organizer 後，呼叫這三個能力的 API 時會因為不具備 `Admin` 角色被 `AdminOnly` Policy 拒絕，導致「查看訂單、核銷、看自己的銷售報表」這三個核心後台功能，對自助 Organizer Owner 完全不可用——這是本次通盤盤點既有 Admin-only 端點時發現的缺口。

## What Changes

- **BREAKING**：訂單查詢（`order-administration`）與票券核銷（`ticket-redemption`）的既有「Admin 角色」授權規則，改為「Access Token 帶有效的 `OrganizerId` claim」；訂單列表／明細與核銷端點依訂單／票券所屬活動的 `OrganizerId` 過濾，只能操作呼叫端目前 Organizer 名下的資料，不屬於目前 Organizer 的訂單／票券視同找不到（比照既有跨租戶查詢一律回報「找不到」而非揭露 403 的慣例）
- **BREAKING**：銷售報表查詢（`sales-report`）同樣從「Admin 角色」改為「Access Token 帶有效的 `OrganizerId` claim」，查詢的活動不屬於呼叫端目前 Organizer 時視同找不到
- 核銷的檢查順序固定為「簽章 → 存在 → 歸屬 → 狀態」，屬於其他 Organizer 的票券不論狀態一律視同查無此票，避免以 409 洩漏其他主辦方票券已核銷
- 三個能力比照 event-management，明確記錄並測試 `RequireOrganizerContext` 不即時查表造成的停權延遲視窗（已知、有界的取捨）
- **BREAKING**：平台 `Admin` 角色不再能不切換 Organizer 就跨租戶查看訂單、核銷、銷售報表，本次不提供平台層級替代查詢（見 design.md Non-Goals）
- 核銷頁「手動輸入 Ticket ID（不驗簽章）」的信任對象從 Admin 擴大為已切換 Organizer 的成員，受歸屬核對限制；介面標示「Admin 信任操作」改為「操作人員信任操作」（見 design.md Decision 4）
- 前端後台路由守衛：訂單、核銷、銷售報表頁面從「角色為 Admin」改為與活動、場館頁面相同的「已切換至一個 Organizer」規則；導覽選單的「訂單管理」「票券核銷」與活動列表上的「銷售報表」入口不再只對 Admin 顯示（「主辦方審核」維持只對 Admin 顯示）

## Capabilities

### Modified Capabilities
- `order-administration`：訂單查詢授權規則從「角色為 Admin」改為「Access Token 帶有效 `OrganizerId` claim」；訂單列表／明細依所屬活動的 `OrganizerId` 過濾
- `ticket-redemption`：核銷 API 授權規則從「角色為 Admin」改為「Access Token 帶有效 `OrganizerId` claim」；核銷前驗證票券所屬活動的 `OrganizerId` 與呼叫端一致
- `sales-report`：查詢授權規則從「角色為 Admin」改為「Access Token 帶有效 `OrganizerId` claim」；查詢的活動不屬於呼叫端目前 Organizer 時視同找不到
- `admin-web-ui`：後台路由守衛規則擴大套用至訂單、核銷、銷售報表頁面；導覽選單與銷售報表入口依新規則顯示；訂單列表頁的描述從「所有訂單」改為「目前 Organizer 名下的訂單」

## Impact

- **WebApi**：既有訂單查詢、票券核銷、銷售報表查詢端點的 `[Authorize]` 規則調整
- **Domain／Infrastructure**：`IOrderRepository` 新增 `GetByOrganizerIdAsync`（資料庫端過濾，取代並移除 `GetAllAsync`）與 `GetOrganizerIdByOrderItemIdAsync`（核銷歸屬的單一投影查詢）
- **Application**：`GetOrdersHandler`／`GetOrderByIdHandler`／`RedeemTicketHandler`／`GetEventSalesReportHandler` 新增依呼叫端 `OrganizerId` 過濾／核對訂單、票券或活動所屬歸屬的邏輯
- **前端**：訂單、核銷、銷售報表頁面路由守衛調整；`AdminLayout.vue` 導覽選單與 `EventListPage.vue` 銷售報表入口的顯示條件調整；`RedemptionScannerPage.vue` 手動輸入信任標示文字調整
- **既有測試**：所有假設「Admin 角色即可呼叫這三個能力端點」的既有測試需要同步更新其授權前置條件

## Dependency

- 依賴已合併的 `event-management-organizer-scoping`（需要 `RequireOrganizerContext` Policy 已套用生效、`Event.OrganizerId` 欄位已存在且為 `NOT NULL`）
