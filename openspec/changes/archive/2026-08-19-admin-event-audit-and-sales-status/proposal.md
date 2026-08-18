## Why

Admin 後台目前的「活動管理」頁面（`EventListPage.vue`）資訊量偏薄，也缺乏維運時常用的兩種資訊：活動是誰、什麼時候建立的（目前完全沒有稽核紀錄，出問題時查不到是哪個 Admin 帳號建立的活動）；活動目前的售票狀況（Available／Held／Sold 各多少，要另外點進活動詳情頁才看得到）。建立活動的表單塞在列表頁下方一起顯示，欄位版面擠、體驗不好。票價相關欄位也沒有標示幣別，容易誤讀。這次先做這四項風險較低、屬於顯示/導頁層級的改動；「票種建立要依座位圖分區自動帶入」這個真正動到 `CreateEvent`／`CreateTicketType` 資料流程的改動範圍較大，留到下一個 change，且會疊在這次新增的「建立活動」獨立頁面上做。

## What Changes

- `Event` 新增 `CreatedByMemberId`（建立者，Admin 的 MemberId）與 `CreatedAtUtc`（建立時間）兩個稽核欄位；`CreateEventHandler` 建立活動時記錄呼叫者身份（沿用既有 `User.GetMemberId()` 慣例，比照 `MembersController` 把 memberId 當作 Handler 的獨立參數傳入，不塞進 Request DTO）與當下時間（`IDateTimeProvider`）
- 既有資料庫裡的活動沒有這兩筆資訊可回溯，兩個新欄位在 schema 上是可為 null（nullable），不對舊資料造假填值；本次之後新建立的活動一律會填入這兩個值
- 新增 Admin 專用的活動列表查詢端點（`GET /api/admin/events`，`GetAdminEventsHandler`／`AdminEventSummaryDto`），回傳建立者顯示名稱（`CreatedByDisplayName`，查無對應 Member 或欄位為 null 時顯示為 null）、建立時間，以及該活動目前的售票狀況統計（Available／Held／Sold 各自座位數）——後者透過既有 `EventSeat.GetStatus(now)` 邏輯，一次查詢該活動底下所有座位並依狀態分組計數，不新增一個要前端逐一呼叫每個活動的獨立端點。**既有公開的 `GET /api/events`（`GetEventsHandler`／`EventDto`）完全不動**——查證後發現這個端點沒有 `[Authorize]`、買家端活動列表也在用，若把建立者身份/建立時間/即時售票數字加進去會透過公開端點外洩給未登入的任何人，故改成新增獨立的 Admin 專用端點（詳見 design.md 決策 8）
- Admin 活動列表新增「建立者」「建立時間」欄位，以及依 Available／Held／Sold 比例呈現的售票狀況橫條圖（顏色區分，簡單呈現，不做即時推播更新，跟現有活動列表一樣是頁面載入/手動刷新當下的快照）
- 「建立活動」表單從活動列表頁下方的內嵌表單，改成獨立頁面／路由（`/admin/events/new`，比照既有 Admin 路由命名慣例），列表頁新增「建立活動」按鈕導向該頁，建立成功後導回列表頁
- 「建立票種」表單本次維持在活動列表頁（不搬移），範圍留給下一個 change 隨「票種依座位圖分區」的改造一併處理
- 票價相關的輸入與顯示欄位（Admin 建立票種表單的價格輸入、買家端活動詳情頁的票價欄位與已選座位總金額）統一補上「NT$」貨幣單位標示，純顯示格式調整，不改變任何金額的儲存或計算邏輯

## Capabilities

### New Capabilities
（無——本次為既有能力的欄位擴充與顯示調整，不引入新的業務能力）

### Modified Capabilities
- `event-management`：既有「透過管理 API 建立活動與票種」Requirement 內容變更——建立活動時 SHALL 記錄建立者（呼叫端的 MemberId）與建立時間；新增「透過管理 API 查詢活動列表時取得建立者與售票狀況統計」Requirement——新增的 Admin 專用活動列表查詢端點，每筆活動 SHALL 附帶建立者、建立時間，以及座位依 Available／Held／Sold 分類的數量統計；既有公開的活動列表查詢端點不受影響，不回傳這些欄位
- `admin-web-ui`：既有「Admin 可透過介面管理活動與票種」Requirement 內容變更——活動列表新增建立者／建立時間欄位與售票狀況橫條圖；「建立活動」表單改為獨立頁面，不再是列表頁內嵌表單；票價相關欄位 SHALL 顯示「NT$」貨幣單位

## Impact

- 新增 EF Core migration：`Event` 新增 `CreatedByMemberId`（`Guid?`）、`CreatedAtUtc`（`DateTime?`）兩個 nullable 欄位
- 後端：`Event` Domain Entity、`EventConfiguration`、`CreateEventHandler`（新增建立者/建立時間記錄）、`CreateEventRequest` 不變（建立者身份不透過前端傳入，由後端從 JWT 取得）、`AdminEventsController` 新增 `GetEvents` action（`GET /api/admin/events`）與 `User.GetMemberId()` 呼叫並傳入 `CreateEventHandler`、新增 `GetAdminEventsHandler`／`AdminEventSummaryDto`（建立者顯示名稱／建立時間／售票狀況統計三組欄位）；既有 `EventsController`／`GetEventsHandler`／`EventDto`（公開端點）不變
- `IEventSeatRepository` 新增批次查詢方法（依多個 EventId 一次查詢所有座位，供售票狀況統計使用，避免逐一活動查詢造成前端 N+1 呼叫）
- 前端：`web/src/router/index.ts` 新增 `admin-event-create` 路由；`EventListPage.vue` 拆出「建立活動」表單到新頁面、改呼叫新的 `getAdminEvents()`（`web/src/api/admin.ts`）顯示建立者/建立時間欄位與售票狀況橫條圖；買家端 `EventDetailPage.vue` 維持呼叫既有 `getEvents()`，票價顯示補上貨幣單位
- 不影響既有建立票種、查詢座位、下單相關的行為與既有測試；新增回歸測試確保既有公開的 `GET /api/events` 不會意外開始回傳建立者/售票統計等 Admin 專用欄位
