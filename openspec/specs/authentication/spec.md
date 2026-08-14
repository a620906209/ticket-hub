# authentication Specification

## Purpose
TBD - created by archiving change membership-system. Update Purpose after archive.
## Requirements
### Requirement: 會員可以使用 Email 與密碼登入
系統 SHALL 允許已註冊且帳號狀態為啟用的會員以 Email 與密碼登入，登入成功後核發 Access Token（JWT）與 Refresh Token。

#### Scenario: 帳密正確且帳號啟用時登入成功
- **WHEN** 會員送出正確的 Email 與密碼，且帳號狀態為啟用
- **THEN** 系統回傳 Access Token（含會員 ID 與角色 Claim）與 Refresh Token，Access Token 效期依系統設定（預設 30 分鐘）

#### Scenario: 密碼錯誤時登入失敗
- **WHEN** 會員送出存在的 Email 但密碼錯誤
- **THEN** 系統回傳 401 未授權錯誤，不透露是 Email 不存在還是密碼錯誤

#### Scenario: Email 不存在時登入失敗
- **WHEN** 會員送出系統中不存在的 Email
- **THEN** 系統回傳 401 未授權錯誤，訊息與密碼錯誤情境一致（避免帳號枚舉攻擊）

### Requirement: 使用者可以使用 Refresh Token 換發新的 Access Token
系統 SHALL 允許持有效 Refresh Token 的使用者換發新的 Access Token，並輪替（Rotation）Refresh Token。

#### Scenario: 使用有效且未使用過的 Refresh Token 換發成功
- **WHEN** 使用者送出尚未過期且尚未被使用過的 Refresh Token
- **THEN** 系統核發新的 Access Token 與新的 Refresh Token，並使舊 Refresh Token 立即失效

#### Scenario: 使用已過期的 Refresh Token
- **WHEN** 使用者送出已過期的 Refresh Token
- **THEN** 系統拒絕換發，回傳 401 錯誤，要求重新登入

#### Scenario: 偵測 Refresh Token 重複使用（疑似遭竊）
- **WHEN** 使用者送出已被使用過（已輪替失效）的 Refresh Token
- **THEN** 系統判定為疑似 Token 遭竊，撤銷該會員名下所有 Refresh Token，並回傳 401 錯誤要求重新登入

#### Scenario: 帳號已停用時使用 Refresh Token 換發失敗
- **WHEN** 帳號狀態為停用的會員，使用其（停用前核發、理論上已被同步撤銷的）Refresh Token 呼叫換發端點
- **THEN** 系統拒絕換發，回傳 401 錯誤，不核發新的 Access Token

### Requirement: 已登入使用者可以登出
系統 SHALL 允許已登入使用者登出，登出後其 Refresh Token 立即失效，無法再用於換發新 Access Token。

#### Scenario: 登出成功
- **WHEN** 已登入使用者攜帶有效 Access Token 呼叫登出端點
- **THEN** 系統撤銷該次登入對應的 Refresh Token，該 Token 之後無法再用於換發

### Requirement: 使用者可以重設忘記的密碼
系統 SHALL 允許使用者透過 Email 觸發密碼重設流程，取得一次性且具時效性的重設 Token，並用該 Token 設定新密碼。

#### Scenario: 申請密碼重設
- **WHEN** 使用者以已註冊的 Email 申請密碼重設
- **THEN** 系統產生一次性、具時效性（預設 15 分鐘）的重設 Token 並記錄於系統中（實際寄送方式不在本次範圍內）

#### Scenario: 使用有效重設 Token 設定新密碼成功
- **WHEN** 使用者攜帶尚未過期且尚未使用過的重設 Token，並提供符合強度規則的新密碼
- **THEN** 系統更新該會員密碼雜湊，重設 Token 立即失效，且撤銷該會員所有既有 Refresh Token（強制重新登入）

#### Scenario: 使用過期或已使用過的重設 Token
- **WHEN** 使用者攜帶已過期或已使用過的重設 Token
- **THEN** 系統拒絕重設，回傳 400 錯誤，密碼維持不變

### Requirement: 系統依角色限制 API 存取權限
系統 SHALL 依會員角色（Member / Admin）限制特定 API 端點的存取，未具備所需角色者不得呼叫該端點。

#### Scenario: 具備所需角色時允許存取
- **WHEN** 已登入使用者的 Access Token 角色 Claim 符合端點所需角色（如 Admin）
- **THEN** 系統允許該請求執行

#### Scenario: 不具備所需角色時拒絕存取
- **WHEN** 已登入使用者的 Access Token 角色 Claim 不符合端點所需角色
- **THEN** 系統回傳 403 禁止存取錯誤，不執行該端點邏輯

#### Scenario: 未攜帶或攜帶無效 Access Token
- **WHEN** 請求未攜帶 Access Token，或 Token 已過期／簽章驗證失敗
- **THEN** 系統回傳 401 未授權錯誤

