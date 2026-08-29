## MODIFIED Requirements

### Requirement: 買家可透過介面註冊與登入
系統 SHALL 提供註冊頁與登入頁，呼叫既有 `member-management`／`authentication` API 完成會員註冊與登入；登入成功後 SHALL 將角色為一般會員者導向買家端首頁。登入 API 因請求頻率限制拒絕（`429 Too Many Requests`）而失敗時，系統 SHALL 顯示友善提示訊息（例如「登入嘗試過於頻繁，請稍後再試」），不得直接顯示後端回應的原始 `ProblemDetails.title` 字串（例如 `"TooManyRequests"`），不套用「登入失敗顯示錯誤訊息」情境的一般錯誤處理。

#### Scenario: 註冊成功
- **WHEN** 使用者在註冊頁填寫有效的 Email／密碼並送出
- **THEN** 系統呼叫註冊 API 成功後導向登入頁

#### Scenario: 登入成功導向買家端首頁
- **WHEN** 一般會員在登入頁輸入正確帳密送出
- **THEN** 系統登入成功並導向買家端活動列表首頁

#### Scenario: 登入失敗顯示錯誤訊息
- **WHEN** 使用者輸入錯誤密碼送出登入
- **THEN** 系統顯示登入失敗的錯誤訊息，停留在登入頁

#### Scenario: LRL-009 登入因請求頻率限制被拒絕
- **WHEN** 使用者送出登入請求，後端因該來源 IP 已超過請求頻率限制而回傳 `429 Too Many Requests`
- **THEN** 系統顯示「登入嘗試過於頻繁，請稍後再試」的友善提示訊息，不顯示原始 `title` 字串，停留在登入頁
