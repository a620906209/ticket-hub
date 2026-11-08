# Spec Delta

## MODIFIED Requirements

### Requirement: 會員可以切換目前操作中的 Organizer
系統 SHALL 允許已登入 Member 對自己所屬、且狀態為 `Approved` 的 Organizer 執行「切換操作情境」，成功後系統 SHALL 換發一組新的 Access Token，內含該 Organizer 的 `OrganizerId` claim；Refresh Token 本身的值不變。**由於同一個 Member 可能同時持有多筆有效的 Refresh Token（多裝置／多分頁登入），呼叫切換端點時 MUST 額外提供呼叫者目前持有的 Refresh Token 明文**，系統 SHALL 依此精確定位是哪一筆 Refresh Token 記錄發起本次切換（見「切換操作情境時同步記錄於 Refresh Token」需求），只更新這一筆記錄，不影響呼叫者其他裝置／分頁各自的操作情境。

#### Scenario: ORG-SWITCH-001 切換到自己所屬且已核准的 Organizer
- **WHEN** 已登入 Member 提供自己目前持有且有效的 Refresh Token，對自己所屬、狀態為 `Approved` 的 Organizer 呼叫切換端點
- **THEN** 系統換發新的 Access Token，內含該 Organizer 的 `OrganizerId` claim；回應 MUST NOT 包含新的 Refresh Token（Refresh Token 本身的值不變，呼叫端沿用原本持有的那一組）

#### Scenario: ORG-SWITCH-007 切換操作情境不觸發 Refresh Token 輪替
- **WHEN** ORG-SWITCH-001 的切換請求成功
- **THEN** 系統 MUST NOT 建立任何新的 `RefreshToken` 資料列（不像既有 Refresh Token 換發流程那樣輪替出新記錄），呼叫端原本持有的 Refresh Token 明文於切換前後完全相同；該筆（被更新 `OrganizerId` 的）`RefreshToken` 資料列的 `TokenHash`、`Status`（`Active`）、`ExpiresAtUtc` 皆不變，之後仍可被正常用於呼叫既有 Refresh Token 換發端點

#### Scenario: ORG-SWITCH-002 切換到非自己所屬的 Organizer
- **WHEN** 已登入 Member 對自己並非成員的 Organizer 呼叫切換端點
- **THEN** 系統回傳 403 禁止存取錯誤，不換發任何 Token

#### Scenario: ORG-SWITCH-003 切換到尚未核准或已駁回的 Organizer
- **WHEN** 已登入 Member 對自己所屬、但狀態非 `Approved`（`Pending`／`Rejected`／`Suspended`）的 Organizer 呼叫切換端點
- **THEN** 系統 MUST 拒絕並回報狀態不允許切換，不換發任何 Token

#### Scenario: ORG-SWITCH-004 切換到不存在的 Organizer
- **WHEN** 已登入 Member 對不存在的 Organizer Id 呼叫切換端點
- **THEN** 系統 MUST 回報找不到，不換發任何 Token

#### Scenario: ORG-SWITCH-005 切換時未提供 Refresh Token
- **WHEN** 已登入 Member 呼叫切換端點時未提供 Refresh Token 欄位，或提供空白字串
- **THEN** 系統 MUST 拒絕並回報驗證錯誤，不換發任何 Token，不更新任何 Refresh Token 記錄

#### Scenario: ORG-SWITCH-006 切換時提供的 Refresh Token 無效或不屬於呼叫者
- **WHEN** 已登入 Member 呼叫切換端點時提供的 Refresh Token 不存在、已非 `Active`、已過期，或屬於另一個 Member
- **THEN** 系統 MUST 拒絕並回報 401 未授權，不換發任何 Token，不更新任何 Refresh Token 記錄
