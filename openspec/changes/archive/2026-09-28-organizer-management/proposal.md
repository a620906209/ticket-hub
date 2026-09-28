## Why

現有系統只有 `Member`／`Admin` 兩種全域角色：任何 `Admin` 都能建立與管理所有活動，`Event.CreatedByMemberId` 只記錄「誰按下建立」，並沒有「這個活動屬於哪個主辦方」的概念。`docs/project-scope.md` 對外定位是「Organizer 為獨立 Entity 的多租戶架構」，但這個定位從未真正落地。

要落實這個定位、並讓一般會員能自助申請成為主辦方，需要先導入 `Organizer` 實體本身，以及對應的申請、平台審核、成員關聯、切換目前操作中主辦方的機制。**本次變更是整個多租戶落地計畫的第一步**：只新增 `Organizer` 能力本身，刻意不觸碰任何既有 capability（`event-management`／`order-administration`／`ticket-redemption`／`sales-report`／`purchase-queue`／既有後台路由守衛）的授權規則。`OrganizerId` claim 在本次交付範圍內尚未被任何既有端點採用做為授權依據，純粹是新增能力，對既有功能零風險，可獨立上線與展示。

真正把既有後台端點的授權規則從「Admin 角色」改為「Organizer 邊界」，由後續變更 `event-management-organizer-scoping`（及其之後的 `order-report-redemption-organizer-scoping`、`purchase-queue-organizer-scoping`）處理，三者依序依賴本次變更。

## What Changes

- 新增 `Organizer` entity：`Id`、`Name`、`Status`（`Pending` / `Approved` / `Rejected` / `Suspended`）、`CreatedByMemberId`、`CreatedAtUtc`、`ReviewedByMemberId`（可為 null）、`ReviewedAtUtc`（可為 null）
- 新增 `OrganizerMember` 關聯 entity：`OrganizerId`、`MemberId`、`Role`（目前僅 `Owner`），支援一個 Member 屬於多個 Organizer
- 新增「申請建立 Organizer」端點：已登入 Member 可自助申請，初始狀態為 `Pending`，申請人自動成為該 Organizer 的 `Owner`
- 新增「平台管理員審核 Organizer 申請」端點：僅限既有 `MemberRole.Admin` 呼叫，可 Approve／Reject／Suspend；**Suspend 本次僅提供後端端點，不提供前端操作介面**（見 design.md Non-Goals）
- 新增「切換目前操作中的 Organizer」端點：驗證呼叫者屬於該 Organizer 且該 Organizer 狀態為 `Approved` 後，換發帶有 `OrganizerId` claim 的新 Access Token；`RefreshToken` 新增 `OrganizerId` 欄位，換發 Access Token 時重新驗證其有效性
- 前端（`admin-web-ui`）新增：申請主辦方頁面、「我的主辦方」清單與切換器（導覽列）、平台管理員專用的審核清單頁——**本次不修改既有後台路由守衛規則**，這些新畫面各自呼叫本次新增的端點，不需要任何其他 capability 配合即可完整運作

## Capabilities

### New Capabilities
- `organizer-management`：`Organizer` 實體、申請／審核／停權工作流、`Member`-`Organizer` 多對多關聯、切換目前操作 Organizer 的授權機制（含 Access Token 換發）

### Modified Capabilities
- `admin-web-ui`：新增主辦方申請／切換／審核相關畫面與導覽邏輯；**不修改**既有「後台路由僅限 Admin 角色進入」規則
- `authentication`：既有「使用者可以使用 Refresh Token 換發新的 Access Token」Requirement 新增 `OrganizerId` 重新驗證/清空邏輯與併發失敗情境，見對應 delta spec

## Impact

- **Domain**：新增 `Organizer`、`OrganizerMember`；`RefreshToken` 新增 `OrganizerId`
- **Infrastructure**：新增 EF Core migration（`Organizers`、`OrganizerMembers` 資料表；`RefreshTokens` 新增 `OrganizerId` 欄位）——**不觸碰 `Events` 資料表**
- **WebApi**：新增 `OrganizerController`（申請／查詢／切換）、Admin 審核端點
- **Application**：新增 `ApplyForOrganizerHandler`／`GetMyOrganizersHandler`／`ReviewOrganizerHandler`／`SuspendOrganizerHandler`／`SwitchOrganizerContextHandler`；既有 `RefreshTokenHandler` 新增 `OrganizerId` 重新驗證邏輯
- **Infrastructure/Security**：`JwtTokenService` 新增 `OrganizerId` claim 支援與換發邏輯
- **前端**：`admin-web-ui` 新增 3 個畫面
- **既有測試**：無需更動任何既有測試的前置條件——本次沒有任何既有端點「誰可以呼叫」的授權規則改變；既有 `/api/auth/refresh` 端點新增的是 `OrganizerId` 重新驗證/清空這段**額外**行為（見 `authentication` 能力 delta spec），既有 Refresh Token 輪替、重複使用偵測、帳號停用拒絕等既有行為與既有測試前置條件不受影響

## Dependency

- 後續變更 `event-management-organizer-scoping` 依賴本次（需要 `Organizer`／`OrganizerMember` 已存在才能執行既有 Admin 帳號的回填）
- 建議本次與 `event-management-organizer-scoping` 接續部署，間隔不宜過長：本次上線後、`event-management-organizer-scoping` 上線前這段期間，一般會員自助申請並切換成功後，導向的一般後台頁面仍受舊版「Admin 角色」路由守衛保護，非 Admin 的自助主辦方會被導向買家端首頁（因為尚未有任何頁面認得 `OrganizerId` claim）——這是預期的過渡狀態，僅平台管理員審核工作流在此期間即可獨立驗證與使用
