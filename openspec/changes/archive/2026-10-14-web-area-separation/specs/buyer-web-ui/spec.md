## RENAMED Requirements

- FROM: `### Requirement: 角色為 Admin 的使用者可從買家端版面進入主辦方審核頁`
- TO: `### Requirement: 角色為 Admin 的使用者可從買家端版面經區域切換選單進入平台管理`

## MODIFIED Requirements

### Requirement: 角色為 Admin 的使用者可從買家端版面經區域切換選單進入平台管理
買家端版面 SHALL 在已登入時顯示區域切換選單（規則見 admin-web-ui「三個區域的版面可由區域名稱與背景色辨識，並可由區域切換選單互相前往」Requirement）；登入者角色為 `Admin` 時，選單 SHALL 有「平台管理」項目，點選後導向 `/platform`（最終為 `/platform/organizers` 審核頁）；此入口不要求已切換 Organizer（與審核頁路由守衛規則一致）。角色非 `Admin` 的使用者 MUST NOT 看到「平台管理」。會員下拉選單 MUST NOT 再包含「主辦方審核」項目；「我的主辦方」項目保留。

#### Scenario: BW-ADMIN-ENTRY-001 尚未切換 Organizer 的 Admin 從買家端進入平台管理
- **WHEN** 角色為 `Admin`、尚未切換至任何 Organizer 的使用者在買家端展開區域切換選單並點選「平台管理」
- **THEN** 系統導向 `/platform/organizers` 並顯示審核頁內容

#### Scenario: BW-ADMIN-ENTRY-002 非 Admin 使用者看不到平台管理入口
- **WHEN** 角色非 `Admin` 的已登入使用者展開買家端區域切換選單
- **THEN** 選單中不顯示「平台管理」

#### Scenario: BW-ADMIN-ENTRY-003 會員下拉選單不再有主辦方審核
- **WHEN** 角色為 `Admin` 與非 `Admin` 的已登入使用者分別開啟買家端會員下拉選單
- **THEN** 兩者的選單中都不顯示「主辦方審核」，都顯示「我的主辦方」
