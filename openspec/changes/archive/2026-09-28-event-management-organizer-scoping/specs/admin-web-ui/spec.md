## RENAMED Requirements

- FROM: `### Requirement: Admin 後台路由僅限 Admin 角色進入`
- TO: `### Requirement: 後台路由僅限已切換 Organizer 或審核頁面的 Admin 進入`

## MODIFIED Requirements

### Requirement: 後台路由僅限已切換 Organizer 或審核頁面的 Admin 進入
系統 SHALL 在使用者導覽至任何 `/admin/*` 路由時檢查目前登入狀態，依頁面分三類：
- **活動、場館頁面（本次變更範圍）**：SHALL 要求 Access Token 帶有 `OrganizerId` claim（即已切換至一個 Approved Organizer），未帶者（含未登入、含尚未切換 Organizer 的一般 Member 或 Admin）SHALL 被導向登入頁或「選擇主辦方」頁面，不得進入頁面內容
- **訂單、核銷頁面（本次變更不處理，維持既有規則）**：SHALL 沿用既有「角色為 `Admin`」規則，本次不受影響；待後續變更 `order-report-redemption-organizer-scoping` 上線後才會改為與活動、場館頁面相同的 `OrganizerId` claim 規則
- **`organizer-management` 變更已新增的 Organizer 審核頁面（`/admin/organizers`）**：SHALL 額外要求角色為 `Admin`，不要求已切換 Organizer——本次為這個既有規則加上明確的 Requirement 文字，行為本身不變

#### Scenario: AWU-GUARD-001 未登入使用者直接進入後台路由
- **WHEN** 未登入的使用者直接開啟任一 `/admin/*` 網址
- **THEN** 系統導向登入頁，不顯示後台頁面內容

#### Scenario: AWU-GUARD-002 已登入但尚未切換 Organizer 的使用者進入活動或場館頁面
- **WHEN** 已登入、但 Access Token 未帶 `OrganizerId` claim 的使用者開啟活動或場館頁面
- **THEN** 系統導向「選擇主辦方」頁面，不顯示該後台頁面內容

#### Scenario: AWU-GUARD-003 已切換 Organizer 後可進入活動或場館頁面
- **WHEN** 已成功切換至一個 Approved Organizer 的使用者開啟活動或場館頁面
- **THEN** 系統顯示對應後台頁面內容

#### Scenario: AWU-GUARD-004 非 Admin 角色嘗試進入審核頁面
- **WHEN** 角色非 `Admin` 的已登入使用者（不論是否已切換 Organizer）開啟 `/admin/organizers` 審核頁面
- **THEN** 系統導向買家端首頁，不顯示審核頁面內容

#### Scenario: AWU-GUARD-005 Admin 角色可進入審核頁面，不需切換 Organizer
- **WHEN** 角色為 `Admin` 的使用者開啟 `/admin/organizers` 審核頁面，且尚未切換至任何 Organizer
- **THEN** 系統顯示審核頁面內容

#### Scenario: AWU-GUARD-006 訂單、核銷頁面本次仍維持角色為 Admin 才能進入
- **WHEN** 已切換至一個 Approved Organizer、但角色不是 `Admin` 的使用者開啟訂單或核銷頁面
- **THEN** 系統依既有規則導向買家端首頁（沿用本次變更前的行為），不因為已切換 Organizer 就放行——這兩個頁面待 `order-report-redemption-organizer-scoping` 上線後才會改用 `OrganizerId` claim 規則
