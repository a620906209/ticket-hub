# member-management Specification

## Purpose
TBD - created by archiving change membership-system. Update Purpose after archive.
## Requirements
### Requirement: 使用者可以註冊會員帳號
系統 SHALL 允許未登入的使用者以 Email 與密碼註冊會員帳號，Email 須為系統中唯一值。**註冊請求 MUST 附帶驗證碼 token 與使用者填寫的驗證碼文字，系統 SHALL 先呼叫 `captcha-verification` 能力驗證兩者，驗證失敗時 MUST 拒絕註冊（不判斷 Email 是否重複、密碼是否符合強度），回傳明確的驗證錯誤。**

#### Scenario: 使用未重複的 Email 註冊成功
- **WHEN** 使用者送出註冊請求，Email 尚未被其他會員使用，密碼符合強度規則（至少 8 碼，含英數），且驗證碼正確
- **THEN** 系統建立新會員（狀態為啟用、角色為一般會員），密碼以雜湊方式儲存，並回傳成功結果

#### Scenario: 使用已存在的 Email 註冊失敗
- **WHEN** 使用者送出註冊請求，Email 已被其他會員使用，且驗證碼正確
- **THEN** 系統拒絕註冊，回傳 409 衝突錯誤，不建立新會員

#### Scenario: 密碼不符強度規則
- **WHEN** 使用者送出註冊請求，密碼長度小於 8 碼或不含英數混合，且驗證碼正確
- **THEN** 系統拒絕註冊，回傳 400 驗證錯誤並說明密碼規則

#### Scenario: CAPTCHA-REG-001 驗證碼錯誤時註冊失敗，不判斷 Email 或密碼
- **WHEN** 使用者送出的 Email 未重複、密碼符合強度規則，但驗證碼文字與對應 token 的正確答案不符
- **THEN** 系統 MUST 拒絕註冊，回傳驗證碼錯誤的驗證錯誤，不建立新會員，不因其餘欄位皆合法而略過驗證碼檢查

#### Scenario: CAPTCHA-REG-002 未提供驗證碼 token 或文字時註冊失敗
- **WHEN** 註冊請求缺漏驗證碼 token 或使用者填寫的驗證碼文字任一欄位
- **THEN** 系統回傳 400 驗證錯誤，不建立新會員，不呼叫驗證碼核對邏輯

### Requirement: 已登入會員可以查詢自己的會員資料
系統 SHALL 允許已通過身份驗證的會員查詢自己的會員資料（不含密碼雜湊等敏感欄位）。

#### Scenario: 查詢自己的會員資料
- **WHEN** 已登入會員呼叫查詢個人資料端點
- **THEN** 系統回傳該會員的 Email、顯示名稱、角色、帳號狀態，不包含密碼雜湊

#### Scenario: 未登入呼叫查詢端點
- **WHEN** 未攜帶有效 Access Token 的請求呼叫查詢個人資料端點
- **THEN** 系統回傳 401 未授權錯誤

### Requirement: 已登入會員可以更新自己的會員資料
系統 SHALL 允許已通過身份驗證的會員更新自己的可編輯欄位（如顯示名稱），不得修改 Email、角色、帳號狀態。

#### Scenario: 更新顯示名稱成功
- **WHEN** 已登入會員送出更新請求，僅包含合法的可編輯欄位（如顯示名稱）
- **THEN** 系統更新該會員資料並回傳最新結果

#### Scenario: 嘗試修改角色或帳號狀態遭拒
- **WHEN** 已登入會員的更新請求中包含角色（Role）或帳號狀態（IsActive）欄位
- **THEN** 系統忽略或拒絕該欄位變更，僅處理允許的欄位，不得因該請求而變更角色或帳號狀態

### Requirement: 管理員可以啟用或停用會員帳號
系統 SHALL 允許具備管理員角色的使用者停用或重新啟用指定會員帳號；被停用的會員無法登入或換發 Token，且既有 Token 立即失效。

#### Scenario: 管理員停用會員帳號
- **WHEN** 管理員呼叫停用端點並指定目標會員
- **THEN** 系統將該會員帳號狀態設為停用，並同步撤銷該會員名下所有現有 Refresh Token，該會員後續登入與換發 Token 嘗試均被拒絕

#### Scenario: 非管理員嘗試停用會員帳號
- **WHEN** 非管理員角色的已登入使用者呼叫停用端點
- **THEN** 系統回傳 403 禁止存取錯誤，帳號狀態不變

#### Scenario: 已停用帳號嘗試登入
- **WHEN** 帳號狀態為停用的會員嘗試登入
- **THEN** 系統拒絕登入並回傳 403 錯誤，說明帳號已被停用

