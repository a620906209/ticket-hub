## MODIFIED Requirements

### Requirement: 使用者可以使用 Refresh Token 換發新的 Access Token
系統 SHALL 允許持有效 Refresh Token 的使用者換發新的 Access Token，並輪替（Rotation）Refresh Token。**若該筆 Refresh Token 記錄帶有非空的 `OrganizerId`（由 `organizer-management` 能力的「切換操作情境」機制寫入），系統 SHALL 依 `organizer-management` 能力定義的規則重新驗證其資格（呼叫者是否仍是該 Organizer 的成員、該 Organizer 是否仍為 `Approved`），決定新換發的 Access Token 是否延續該 `OrganizerId` claim；驗證通過則延續、新 Refresh Token 記錄延續同一個 `OrganizerId`，驗證未通過則新 Access Token 不帶該 claim、新 Refresh Token 記錄的 `OrganizerId` 清空。完整規則、驗證未通過的判斷條件與對應測試情境，見 `organizer-management` 能力「切換操作情境時同步記錄於 Refresh Token，換發 Access Token 時重新驗證其有效性」Requirement（`ORG-REFRESH-001`～`004`）。此重新驗證與既有 Refresh Token 輪替（本 Requirement 既有行為）各自獨立執行，不互相取代。**

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

#### Scenario: 換發時與另一操作併發競爭同一筆 Refresh Token 記錄
- **WHEN** 換發請求與 `organizer-management` 能力的「切換操作情境」請求併發作用於同一筆 `RefreshToken` 資料列，且對方的寫入先提交成功
- **THEN** 換發請求因樂觀併發衝突失敗，MUST 被捕捉並回傳 401，不核發任何新 Token；完整規則見 `organizer-management` 能力「切換操作情境與 Refresh Token 換發併發競爭同一筆記錄時 MUST 有明確定義的失敗行為」Requirement（`ORG-CONCURRENCY-001`）
