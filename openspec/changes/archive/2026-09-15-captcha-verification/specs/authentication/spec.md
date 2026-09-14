## MODIFIED Requirements

### Requirement: 會員可以使用 Email 與密碼登入
系統 SHALL 允許已註冊且帳號狀態為啟用的會員以 Email 與密碼登入，登入成功後核發 Access Token（JWT）與 Refresh Token。**登入請求 MUST 附帶驗證碼 token 與使用者填寫的驗證碼文字，系統 SHALL 先呼叫 `captcha-verification` 能力驗證兩者，驗證失敗時 MUST 拒絕登入（不判斷帳密是否正確），回傳明確的驗證錯誤；驗證碼檢查與既有登入請求頻率限制（`api-rate-limiting`）各自獨立生效，任一項失敗皆拒絕登入，不互相取代。**

#### Scenario: 帳密正確且帳號啟用時登入成功
- **WHEN** 會員送出正確的 Email 與密碼、正確的驗證碼，且帳號狀態為啟用
- **THEN** 系統回傳 Access Token（含會員 ID 與角色 Claim）與 Refresh Token，Access Token 效期依系統設定（預設 30 分鐘）

#### Scenario: 密碼錯誤時登入失敗
- **WHEN** 會員送出存在的 Email、正確的驗證碼，但密碼錯誤
- **THEN** 系統回傳 401 未授權錯誤，不透露是 Email 不存在還是密碼錯誤

#### Scenario: Email 不存在時登入失敗
- **WHEN** 會員送出系統中不存在的 Email，且驗證碼正確
- **THEN** 系統回傳 401 未授權錯誤，訊息與密碼錯誤情境一致（避免帳號枚舉攻擊）

#### Scenario: CAPTCHA-AUTH-001 驗證碼錯誤時登入失敗，不判斷帳密
- **WHEN** 會員送出的 Email 與密碼皆正確，但驗證碼文字與對應 token 的正確答案不符
- **THEN** 系統 MUST 拒絕登入，回傳驗證碼錯誤的驗證錯誤，不核發任何 Token，不因帳密正確而略過驗證碼檢查

#### Scenario: CAPTCHA-AUTH-002 未提供驗證碼 token 或文字時登入失敗
- **WHEN** 登入請求缺漏驗證碼 token 或使用者填寫的驗證碼文字任一欄位
- **THEN** 系統回傳 400 驗證錯誤，不核發任何 Token，不呼叫驗證碼核對邏輯（缺漏本身即為請求格式錯誤）
