## ADDED Requirements

### Requirement: 使用者可透過介面申請建立主辦方
系統 SHALL 提供申請建立主辦方的表單頁面，供已登入使用者填寫主辦方名稱並呼叫 `organizer-management` 能力的申請端點。申請成功後，系統 SHALL 立即呼叫切換操作情境端點——呼叫時 MUST 一併帶入使用者目前持有的 Refresh Token（比照既有 `/api/auth/refresh` 流程既有的儲存機制，不新增儲存），與一般「我的主辦方」清單頁的切換操作使用同一個前端呼叫邏輯（見「使用者可透過介面查看與切換自己所屬的主辦方」需求）。但因新申請預設狀態為 `Pending`，這次自動切換 MUST 被後端依 `organizer-management` 能力的 `ORG-APPLY-005` 規則拒絕；介面 SHALL 依此預期的拒絕結果顯示「申請已送出，待平台審核」，並導向「我的主辦方」清單頁，不導向一般後台頁面，也 MUST NOT 因為自動切換呼叫失敗而顯示錯誤訊息（因為對這個流程而言，切換被拒絕是預期行為，不是異常）。若自動切換呼叫因非預期原因失敗（例如網路逾時、伺服器錯誤，而非 `ORG-APPLY-005` 的預期拒絕），介面 SHALL 仍正常顯示「申請已送出，待平台審核」並導向清單頁（申請本身已成功，自動切換只是附帶的使用者體驗優化，不影響申請結果的呈現，見 `AWU-APPLY-004`）。

#### Scenario: AWU-APPLY-001 申請建立主辦方成功
- **WHEN** 已登入使用者在申請表單填寫有效名稱並送出
- **THEN** 系統呼叫申請端點成功

#### Scenario: AWU-APPLY-002 申請成功後自動嘗試切換，因 Pending 被拒絕
- **WHEN** AWU-APPLY-001 的申請成功後，系統立即帶著使用者目前持有的 Refresh Token 呼叫切換操作情境端點
- **THEN** 後端依新申請的 `Pending` 狀態拒絕這次切換；介面 SHALL 顯示「申請已送出，待平台審核」，導向「我的主辦方」清單頁，MUST NOT 顯示錯誤訊息，也 MUST NOT 導向一般後台頁面

#### Scenario: AWU-APPLY-003 申請表單名稱留空
- **WHEN** 已登入使用者在申請表單留空名稱並嘗試送出
- **THEN** 系統顯示驗證錯誤，不呼叫申請端點

#### Scenario: AWU-APPLY-004 申請成功後自動嘗試切換，因非預期原因失敗
- **WHEN** AWU-APPLY-001 的申請成功後，系統立即呼叫切換操作情境端點，但該次呼叫因網路逾時或伺服器錯誤等非預期原因失敗（並非 `ORG-APPLY-005` 的預期拒絕）
- **THEN** 介面 SHALL 仍顯示「申請已送出，待平台審核」，導向「我的主辦方」清單頁，MUST NOT 顯示錯誤訊息

### Requirement: 使用者可透過介面查看與切換自己所屬的主辦方
系統 SHALL 提供「我的主辦方」清單頁，顯示使用者目前所屬的所有 Organizer 及其狀態（`Pending`／`Approved`／`Rejected`／`Suspended`）。狀態為 `Approved` 的項目 SHALL 提供「切換」操作，呼叫切換操作情境端點時 MUST 一併帶入使用者目前持有的 Refresh Token，成功後系統 SHALL 導向一般後台首頁；非 `Approved` 狀態的項目 SHALL 顯示對應狀態說明文字，不提供切換操作。導覽列 SHALL 顯示使用者目前切換所在的 Organizer 名稱（尚未切換時顯示提示文字），並提供快速前往「我的主辦方」清單頁的入口。

#### Scenario: AWU-LIST-001 查看所屬主辦方清單
- **WHEN** 已登入使用者開啟「我的主辦方」清單頁
- **THEN** 系統顯示該使用者目前所屬的所有 Organizer 及其狀態

#### Scenario: AWU-LIST-002 切換到已核准的主辦方
- **WHEN** 已登入使用者在清單頁對一筆狀態為 `Approved` 的 Organizer 點選「切換」
- **THEN** 系統帶著使用者目前持有的 Refresh Token 呼叫切換操作情境端點成功，導向一般後台首頁，導覽列顯示該 Organizer 名稱

#### Scenario: AWU-LIST-003 待審核、已駁回或已停權的主辦方不提供切換操作
- **WHEN** 已登入使用者在清單頁查看一筆狀態為 `Pending`、`Rejected` 或 `Suspended` 的 Organizer
- **THEN** 系統顯示對應狀態說明文字，不提供「切換」操作

#### Scenario: AWU-LIST-004 尚未加入任何主辦方
- **WHEN** 已登入使用者的「我的主辦方」清單為空
- **THEN** 系統顯示提示文字與前往申請表單的入口，不顯示錯誤

### Requirement: 平台管理員可透過介面審核主辦方申請
系統 SHALL 在 `/admin/organizers` 提供待審核主辦方清單頁，沿用既有「後台路由僅限 Admin 角色進入」規則保護（本次不修改該規則）。清單 SHALL 顯示每筆待審核申請的主辦方名稱與申請人；每筆 SHALL 提供「核准」與「駁回」操作，呼叫對應端點成功後，系統 SHALL 重新查詢清單，該筆申請自清單移除。

#### Scenario: AWU-REVIEW-001 查看待審核主辦方清單
- **WHEN** `Admin` 角色使用者開啟 `/admin/organizers` 頁面
- **THEN** 系統顯示目前所有 `Pending` 狀態的主辦方申請與其申請人

#### Scenario: AWU-REVIEW-002 核准主辦方申請
- **WHEN** `Admin` 角色使用者對清單中一筆申請點選「核准」
- **THEN** 系統呼叫核准端點成功，重新查詢清單，該筆申請自待審核清單移除

#### Scenario: AWU-REVIEW-003 駁回主辦方申請
- **WHEN** `Admin` 角色使用者對清單中一筆申請點選「駁回」
- **THEN** 系統呼叫駁回端點成功，重新查詢清單，該筆申請自待審核清單移除
