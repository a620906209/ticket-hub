## Why

Admin 後台目前只有建立場館（`Venue`）與座位圖（`SeatMap`）的 `POST` API，沒有對應的查詢 API。這個缺口是 `ticketing-web-ui` 上線時刻意標記、延後處理的已知落差（見 `openspec/changes/archive/2026-08-18-ticketing-web-ui/design.md` Non-Goals）：Admin 前端「場館列表」只能顯示當前瀏覽器分頁 session 內建立過的資料（`stores/adminVenueCache.ts`，重新整理即消失），「建立活動」表單的場館 Id、座位圖 Id 也只能靠 Admin 手動複製貼上 GUID，沒有下拉選單可選。這個工作流程對日常操作（尤其是種測試資料、示範環境操作）造成持續的摩擦，且無法反映資料庫的真實狀態，需要補上查詢端點解決。

## What Changes

- `event-management` 新增場館與座位圖的查詢端點：場館列表（`GET /api/admin/venues`）、單一場館明細（`GET /api/admin/venues/{id}`，含其下座位圖摘要列表）、單一座位圖明細（`GET /api/admin/venues/{venueId}/seat-maps/{seatMapId}`，含完整座位清單）
- `IVenueRepository`／`ISeatMapRepository` 新增對應的查詢方法（`GetAllAsync`／依 VenueId 查詢座位圖列表等，實際簽章依 design.md 決定）
- Admin 前端「建立活動」表單的場館 Id、座位圖 Id 改為下拉選單，資料來源改為查詢 API，移除 GUID 格式驗證這個暫時性替代方案
- `VenueListPage.vue` 改為呼叫查詢 API 顯示真實資料，移除 `stores/adminVenueCache.ts` 這個 session-only 暫存 store
- 移除前端「複製新建立場館/座位圖 Id」這個因應查詢 API 缺失而存在的 UI 提示（查詢 API 到位後，新建立的資料本來就查得到，不需要特別複製 Id 手動貼）
- 順帶修正 `CreateEventHandler` 缺少的驗證：目前只分別確認場地、座位圖各自存在，從未檢查座位圖是否真的屬於指定的場地——這跟既有 `event-catalog` spec「指定使用場地下的一份座位圖」的語意不符，查證時發現的既有落差，範圍小且與本次改動的「座位圖歸屬場地」規則直接相關，一併修正（見 design.md 決策 8）

## Capabilities

### New Capabilities
（無——本次為補齊既有能力的查詢端點，不引入新的業務能力）

### Modified Capabilities
- `event-management`：新增「透過管理 API 查詢場地與座位圖」的 Requirement（場館列表、場館明細含座位圖摘要、座位圖明細含完整座位清單），查詢端點沿用既有「後台管理 API 需要 Admin 角色」的權限規則；既有「透過管理 API 建立活動與票種」Requirement 內容變更——建立活動時新增座位圖須屬於指定場地的檢查（決策 8）
- `admin-web-ui`：既有「Admin 可透過介面管理場館與座位圖」Requirement 內容變更——場館列表 SHALL 改為查詢 API 取得的真實資料，不再是 session 暫存清單，重新整理頁面後清單 SHALL 保留（不再清空）；既有「Admin 可透過介面管理活動與票種」Requirement 內容變更——建立活動表單的場館 Id、座位圖 Id SHALL 改為下拉選單，不再是手動輸入 GUID 欄位

## Impact

- 新增後端查詢端點：`AdminVenuesController` 新增 `GetVenues`／`GetVenueById`／`GetSeatMapById` action；`IVenueRepository`／`ISeatMapRepository` 新增對應查詢方法與 EF Core 實作
- 修改既有 `CreateEventHandler`：新增座位圖須屬於指定場地的檢查（決策 8），這是本次唯一觸及既有建立流程行為的地方
- 前端：`web/src/api/admin.ts` 新增對應查詢 API 呼叫、`VenueListPage.vue` 改為串接真實查詢、`EventListPage.vue`（Admin）的建立活動表單改用下拉選單、移除 `stores/adminVenueCache.ts`
- 需重新產生 `web/src/types/api.generated.ts`；三個新 GET 端點沒有 Request Body、且回傳型別維持既有的 `IActionResult`（見 design.md 決策 5），不會產生 Response schema，`web/src/types/apiResponses.ts` 仍需手寫對應的 Response DTO，延續 `ticketing-web-ui` 已建立的作法
- 不影響既有建立場館／座位圖／票種的行為，也不影響建立活動既有的合法成功流程與既有測試；本次唯一改變的既有行為，是拒絕場館與座位圖不對應的活動建立請求（原本這種不合法輸入可能誤放行，決策 8 修正後改為回傳 NotFound）
