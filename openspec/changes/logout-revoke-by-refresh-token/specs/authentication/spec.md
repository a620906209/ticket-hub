## RENAMED Requirements

- FROM: `### Requirement: 已登入使用者可以登出`
- TO: `### Requirement: 持有 Refresh Token 者可以登出並撤銷該 Token`

## MODIFIED Requirements

### Requirement: 持有 Refresh Token 者可以登出並撤銷該 Token
系統 SHALL 允許呼叫者只憑 Refresh Token 明文登出，登出端點 MUST NOT 要求 Access Token。登出後該 Refresh Token 立即失效，無法再用於換發新 Access Token。撤銷 MUST 只依 Refresh Token 雜湊比對，MUST NOT 以 Access Token 的會員身分作為條件。Token 不存在、已使用或已撤銷時，系統 SHALL 回傳與成功相同的回應，MUST NOT 揭露 Token 是否存在，也 MUST NOT 修改任何 Token 狀態。

#### Scenario: AUTH-LOGOUT-001 攜帶有效 Access Token 登出成功
- **WHEN** 已登入使用者攜帶有效 Access Token 與其 Active Refresh Token 呼叫登出端點
- **THEN** 系統回傳 204，該 Refresh Token 狀態變為已撤銷，之後用它呼叫換發端點回傳 401

#### Scenario: AUTH-LOGOUT-002 未攜帶或攜帶無效 Access Token 仍可登出
- **WHEN** 呼叫者未攜帶 Authorization header，或攜帶已過期／簽章無效的 Access Token，並送出 Active Refresh Token 呼叫登出端點
- **THEN** 系統回傳 204，該 Refresh Token 狀態變為已撤銷，之後用它呼叫換發端點回傳 401

#### Scenario: AUTH-LOGOUT-003 Token 不存在時回傳相同結果
- **WHEN** 呼叫者送出不存在於系統中的 Refresh Token 呼叫登出端點
- **THEN** 系統回傳 204，不修改任何 Refresh Token 狀態

#### Scenario: AUTH-LOGOUT-004 Token 已使用或已撤銷時不修改狀態
- **WHEN** 呼叫者送出狀態為已使用（已輪替）或已撤銷的 Refresh Token 呼叫登出端點
- **THEN** 系統回傳 204，該 Token 狀態維持不變，同一會員名下其他 Refresh Token 狀態也不變（登出不觸發重複使用偵測的全面撤銷）

#### Scenario: AUTH-LOGOUT-005 登出與另一操作併發競爭同一筆 Refresh Token 記錄
- **WHEN** 登出請求與換發或切換操作情境請求併發作用於同一筆 Active `RefreshToken` 資料列，且對方的寫入先提交成功
- **THEN** 登出請求因樂觀併發衝突失敗，MUST 被捕捉並回傳 409，MUST NOT 回傳 500；記錄恰好一筆 Warning log，log 的訊息與結構化屬性 MUST NOT 包含 Refresh Token 明文或雜湊

#### Scenario: AUTH-LOGOUT-006 未提供 Refresh Token 欄位
- **WHEN** 呼叫者送出不含 `refreshToken` 欄位（或值為 null）的登出請求
- **THEN** 系統回傳 400，不修改任何 Refresh Token 狀態

#### Scenario: AUTH-LOGOUT-007 Active 但已過有效期限的 Token 仍被撤銷
- **WHEN** 呼叫者送出狀態為 Active、但有效期限已過的 Refresh Token 呼叫登出端點
- **THEN** 系統回傳 204，該 Token 狀態變為已撤銷
