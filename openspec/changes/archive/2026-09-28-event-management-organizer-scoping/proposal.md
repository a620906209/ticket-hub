## Why

`organizer-management` 變更已新增 `Organizer` 實體與「切換目前操作中主辦方」機制，但刻意沒有觸碰任何既有後台端點的授權規則——`OrganizerId` claim 目前還沒有任何既有端點採用做為授權依據。

`event-management` 能力的所有後台管理端點（建立 Venue、SeatMap、Event、TicketType，以及 Admin 專用活動列表查詢）目前都是「角色為 Admin 即可呼叫」，沒有「這個活動屬於哪個主辦方」的邊界，`Event.CreatedByMemberId` 只記錄操作者，不是租戶歸屬。本次是**第一個實際把授權規則從「Admin 角色」改為「已切換至一個 Approved Organizer」的變更**，讓自助申請成為主辦方的一般 Member 真正能夠管理自己的活動——這是整個多租戶落地計畫真正產生使用者可見價值的最小切片。

## What Changes

- **BREAKING**：後台活動／票種管理端點（建立 Venue、SeatMap、Event、TicketType，以及 Admin 專用活動列表查詢）的授權方式，從「角色為 Admin」改為「Access Token 帶有效的 `OrganizerId` claim（即已切換至一個 Approved Organizer）」
- `Event` 新增 `OrganizerId`（必填）：建立活動時一律取自 Access Token 的 `OrganizerId` claim，不接受前端在請求內容中指定或覆寫；既有資料透過資料庫遷移，為既有 Event 的建立者統一建立一個「既有資料轉入」Organizer 並回填
- 建立票種時，新增核對所操作的既有活動是否屬於呼叫端目前 Organizer，不一致時視同活動不存在（IDOR 防護，見 design.md Decision 5）
- `Venue`／`SeatMap` 維持平台共用資源，不歸屬特定 Organizer
- 既有全域 `Admin` 角色在活動管理這個範圍內收斂：不再直接持有建立活動的權限，需先加入某個 Organizer 並切換過去
- 前端（`admin-web-ui`）後台路由守衛規則從「角色為 Admin」調整為「已切換至一個 Organizer，或角色為 Admin 且進入審核頁面」

## Capabilities

### Modified Capabilities
- `event-management`：後台管理端點授權規則從「角色為 Admin」改為「Access Token 帶有效 `OrganizerId` claim」；建立活動時記錄 `OrganizerId`；Admin 專用活動列表查詢的權限規則同步調整
- `admin-web-ui`：後台路由守衛規則調整（一般後台路由需要已切換 Organizer；`/admin/organizers` 審核頁面維持只需 Admin 角色）

## Impact

- **Domain**：`Event` 新增 `OrganizerId`
- **Infrastructure**：新增 EF Core migration（`Event` 新增外鍵欄位、既有資料回填腳本，新增資料轉入用 Organizer）；套用 `organizer-management` 變更已定義的 `RequireOrganizerContext` Policy
- **WebApi**：既有後台管理端點（Venue／SeatMap／Event／TicketType 建立、Admin 專用活動列表查詢）的 `[Authorize]` 規則調整
- **Application**：既有建立 Event／TicketType 的 Handler、Admin 專用活動列表查詢 Handler 新增依呼叫端 `OrganizerId` 過濾／核對的邏輯
- **前端**：`admin-web-ui` 路由守衛邏輯調整
- **既有測試**：所有假設「Admin 角色即可呼叫後台管理端點」的既有測試（`event-management`、`admin-web-ui` 相關）需要同步更新其授權前置條件

## Dependency

- 依賴已合併的 `organizer-management` 變更（需要 `Organizer`／`OrganizerMember` 資料表、`RequireOrganizerContext` Policy 定義、切換操作情境端點皆已存在）
- 後續變更 `order-report-redemption-organizer-scoping`、`purchase-queue-organizer-scoping` 依賴本次（需要 `RequireOrganizerContext` Policy 已套用生效、`Event.OrganizerId` 欄位已存在且為 `NOT NULL`）
