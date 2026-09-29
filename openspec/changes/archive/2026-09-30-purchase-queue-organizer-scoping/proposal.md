## Why

`event-management-organizer-scoping`、`order-report-redemption-organizer-scoping` 已經把活動／票種／場館／訂單／核銷／銷售報表的授權規則收斂為「已切換至一個 Approved Organizer」。開關活動「熱門搶購模式」（`purchase-queue` 能力既有的 `PATCH /api/admin/events/{id}/queue-mode`）仍是純「Admin 角色」授權，且核對原始碼確認：目前只檢查 `@event is null`（活動是否存在），完全沒有核對該活動的 `OrganizerId` 是否等於呼叫端目前 Organizer。

若只把授權屬性從 `AdminOnly` 改為 `RequireOrganizerContext`，卻不補上這個核對，會造成 IDOR（Insecure Direct Object Reference）缺口：任何已核准的 Organizer 成員都能透過猜測或枚舉 `eventId`，對其他 Organizer 名下的既有活動任意切換其熱門搶購模式。

## What Changes

- **BREAKING**：開關活動「熱門搶購模式」端點的授權規則，從「角色為 Admin」改為「Access Token 帶有效的 `OrganizerId` claim」；未切換 Organizer 的平台 `Admin` 將被拒絕（403）
- 新增核對：所操作的既有活動是否屬於呼叫端目前 Organizer，不一致時視同活動不存在（404，回應 body 與不存在時逐字相同；不回傳 403，避免刺探其他主辦方活動是否存在）
- 比照前兩個變更，明確記錄並測試 `RequireOrganizerContext` 不即時查表造成的停權延遲視窗（已知、有界的取捨）
- `purchase-queue` 中「Admin 關閉熱門搶購模式後…」與「買家可查詢自己的排隊狀態」兩個 Requirement 的操作者用語同步去除 `Admin`，行為不變

## Capabilities

### Modified Capabilities
- `purchase-queue`：
  - 開關熱門搶購模式端點的授權規則，從「角色為 Admin」改為「Access Token 帶有效 `OrganizerId` claim」；操作的活動不屬於呼叫端目前 Organizer 時視同找不到；補上停權延遲視窗的 Scenario
  - 「關閉熱門搶購模式後，既有排隊紀錄不主動清理」Requirement 改名並調整操作者用語，行為不變
  - 「買家可查詢自己的排隊狀態」Requirement 說明段落與 `PQ-STATUS-008` 中「Admin 關閉熱門搶購模式」的前置情境用語，改為「活動所屬 Organizer 的成員」，查詢行為不變
- `event-management`：「後台管理 API 需要已切換至一個 Approved Organizer」Requirement 中「不含維持 `AdminOnly` 的銷售報表查詢與熱門搶購模式開關」的排除敘述，改為「兩者屬於其他能力，授權規則、Scenario 與測試皆由各自能力定義，不屬於本 Requirement 與 `EVT-AUTHZ-001`～`005` 的涵蓋範圍」。本次合併後兩者都已不是 `AdminOnly`；銷售報表的部分早在 `order-report-redemption-organizer-scoping` 就已過時，當時漏改，這次一併修正。端點清單與行為不變
- `query-caching`：`QC-EVT-INV-001`／`QC-EVT-INV-002`／`QC-TT-INV-001`／`QC-FAIL-002` 的操作者從「Admin」改為「已切換至一個 Approved Organizer 的使用者」，快取失效行為不變。建立活動、建立票種的用語早在 `event-management-organizer-scoping` 就已過時，這次一併修正

### 刻意不修改
- `buyer-web-ui` 的 `BW-TOGGLE-001` 中「Admin 將該活動的熱門搶購模式關閉」只是描述前置情境的操作者，驗收的是買家端畫面行為，與誰有權關閉無關。所屬 Requirement（「買家可選位並送出訂單」）篇幅很大，為了一個用語做 MODIFIED 的風險高於收益，因此不修改；同一 Requirement 說明段落中的「（Admin 已於買家等待期間關閉熱門搶購模式）」基於相同理由一併不修改；日後該 Requirement 有實質變更時再順帶調整
- `ticket-purchase`「建立訂單」Requirement 說明段落中「若 Admin 在買家的建立訂單請求處理過程中變更了該活動的熱門搶購模式」，以及同 Requirement 下兩個 Scenario 的「Admin 已將該活動切換為 `true`／`false`」，同樣只是描述前置情境的操作者，驗收的是建立訂單當下重新讀取熱門搶購模式的行為，與誰有權切換無關。該 Requirement 篇幅同樣很大，基於與 `buyer-web-ui` 相同的理由不修改

## Impact

- **WebApi**：`AdminEventsController.SetQueueMode` 的 `[Authorize]` 規則調整，並新增 fail-closed 的 `OrganizerId` claim 取出
- **Application**：`SetEventQueueModeHandler` 新增 `organizerId` 參數，以及依呼叫端 `OrganizerId` 核對活動歸屬的邏輯
- **前端**：無。後台（`web/src`）目前沒有開關熱門搶購模式的 UI，`admin-web-ui` 不需要改動；此 BREAKING 只影響直接呼叫 API 者
- **既有測試**：`AdminEventsControllerTests` 的 PQ-ADMIN 測試改由活動所屬 Organizer 呼叫；`AuthTestHelper` 中為「維持 AdminOnly」而存在的 helper 說明需更新（見 tasks.md 3.x）

## Dependency

- 依賴已合併的 `event-management-organizer-scoping`（需要 `RequireOrganizerContext` Policy 已套用生效、`Event.OrganizerId` 欄位已存在且為 `NOT NULL`）
