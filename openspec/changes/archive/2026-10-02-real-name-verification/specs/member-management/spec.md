## MODIFIED Requirements

### Requirement: 已登入會員可以查詢自己的會員資料
系統 SHALL 允許已通過身份驗證的會員查詢自己的會員資料（不含密碼雜湊等敏感欄位）。回傳內容 SHALL 包含實名登記狀態：`hasRegisteredRealName`（是否已登記實名）、`realName`（已登記時為完整姓名，未登記時為 null）、`nationalIdLast4Masked`（已登記時為依 `real-name-verification` 能力遮蔽規則產生的遮蔽值，未登記時為 null）。回應 MUST NOT 包含完整的身分證末四碼。

#### Scenario: 查詢自己的會員資料
- **WHEN** 已登入會員呼叫查詢個人資料端點
- **THEN** 系統回傳該會員的 Email、顯示名稱、角色、帳號狀態，不包含密碼雜湊

#### Scenario: 未登入呼叫查詢端點
- **WHEN** 未攜帶有效 Access Token 的請求呼叫查詢個人資料端點
- **THEN** 系統回傳 401 未授權錯誤

#### Scenario: MM-PROFILE-RN-001 未登記實名的會員查詢個人資料
- **WHEN** 尚未登記實名的已登入會員呼叫查詢個人資料端點
- **THEN** 回應 `hasRegisteredRealName = false`，`realName` 與 `nationalIdLast4Masked` 皆為 null

#### Scenario: MM-PROFILE-RN-002 已登記實名的會員查詢個人資料
- **WHEN** 已登記實名（姓名「王小明」、末四碼「1234」）的會員呼叫查詢個人資料端點
- **THEN** 回應 `hasRegisteredRealName = true`、`realName = "王小明"`、`nationalIdLast4Masked = "**34"`，回應任何欄位都不包含「1234」

### Requirement: 已登入會員可以更新自己的會員資料
系統 SHALL 允許已通過身份驗證的會員更新自己的可編輯欄位（如顯示名稱），不得修改 Email、角色、帳號狀態。實名資料（真實姓名、身分證末四碼）MUST NOT 透過此端點修改；實名資料只能經 `real-name-verification` 能力的登記端點設定一次。

#### Scenario: 更新顯示名稱成功
- **WHEN** 已登入會員送出更新請求，僅包含合法的可編輯欄位（如顯示名稱）
- **THEN** 系統更新該會員資料並回傳最新結果

#### Scenario: 嘗試修改角色或帳號狀態遭拒
- **WHEN** 已登入會員的更新請求中包含角色（Role）或帳號狀態（IsActive）欄位
- **THEN** 系統忽略或拒絕該欄位變更，僅處理允許的欄位，不得因該請求而變更角色或帳號狀態

#### Scenario: MM-UPDATE-RN-001 透過更新個人資料端點夾帶實名欄位無效
- **WHEN** 已登記實名的會員呼叫更新個人資料端點，請求內容除合法的顯示名稱外，另夾帶 `realName` 與 `nationalIdLast4` 欄位（值與已登記不同）
- **THEN** 系統只更新顯示名稱，資料庫中的實名資料維持原值
