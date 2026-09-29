## ADDED Requirements

### Requirement: 已登入會員可以申請建立 Organizer
系統 SHALL 允許任何已通過身份驗證的 Member 申請建立一個 `Organizer`，僅需提供 `Name`（必填，去除頭尾空白後長度須為 1～100 字元，不要求全系統唯一）。申請成功後，系統 SHALL 建立狀態為 `Pending` 的 `Organizer`，並自動建立一筆對應的 `OrganizerMember`（申請人、`Role = Owner`）。`Pending` 狀態的 Organizer MUST NOT 被用於切換操作情境。至於 `Pending` 狀態是否能用於建立活動，由後續採用 `OrganizerId` claim 做授權依據的能力（見 `event-management-organizer-scoping`）自行定義與保證，本次不作保證（本次不觸碰 `event-management` 的授權規則，見 design.md Context）。本次自助申請流程建立的 Organizer 僅有申請人一位成員；系統本次不提供邀請或新增既有 Organizer 成員的功能。

#### Scenario: ORG-APPLY-001 已登入會員申請建立 Organizer 成功
- **WHEN** 已登入 Member 提供有效 `Name` 呼叫申請建立 Organizer 端點
- **THEN** 系統建立狀態為 `Pending` 的 Organizer，並將該 Member 設為 `Owner` 成員，回傳可用於後續查詢的識別碼

#### Scenario: ORG-APPLY-002 未登入呼叫申請建立 Organizer 端點
- **WHEN** 未攜帶有效 Access Token 的請求呼叫申請建立 Organizer 端點
- **THEN** 系統回傳 401 未授權錯誤，不建立任何 Organizer

#### Scenario: ORG-APPLY-003 申請時未提供名稱
- **WHEN** 已登入 Member 呼叫申請建立 Organizer 端點但未提供 `Name` 或 `Name` 為空白
- **THEN** 系統 MUST 拒絕並回報驗證錯誤，不建立任何 Organizer

#### Scenario: ORG-APPLY-004 申請時名稱超過長度上限
- **WHEN** 已登入 Member 呼叫申請建立 Organizer 端點，`Name` 去除頭尾空白後超過 100 字元
- **THEN** 系統 MUST 拒絕並回報驗證錯誤，不建立任何 Organizer

#### Scenario: ORG-APPLY-005 待審核的 Organizer 不可用於切換
- **WHEN** 該 Organizer 的 `Owner` 成員在核准前嘗試切換操作情境到這個 `Pending` 狀態的 Organizer
- **THEN** 系統 MUST 拒絕，不換發 Access Token

### Requirement: 平台管理員可以審核 Organizer 申請
系統 SHALL 允許具備 `MemberRole.Admin` 角色的使用者查詢待審核（`Pending`）的 Organizer 清單，並對指定 Organizer 執行核准（轉為 `Approved`）或駁回（轉為 `Rejected`）。只有目前狀態為 `Pending` 的 Organizer 可以被核准或駁回；已是 `Approved`／`Rejected`／`Suspended` 的 Organizer 再次呼叫核准或駁回端點 MUST 被拒絕，不改變其狀態。

#### Scenario: ORG-REVIEW-001 平台管理員查詢待審核清單
- **WHEN** `Admin` 角色使用者呼叫查詢待審核 Organizer 清單端點
- **THEN** 系統回傳目前所有 `Pending` 狀態的 Organizer 及其申請人資訊

#### Scenario: ORG-REVIEW-002 平台管理員核准申請
- **WHEN** `Admin` 角色使用者對一筆 `Pending` 狀態的 Organizer 呼叫核准端點
- **THEN** 系統將該 Organizer 狀態轉為 `Approved`，記錄審核者與審核時間

#### Scenario: ORG-REVIEW-003 平台管理員駁回申請
- **WHEN** `Admin` 角色使用者對一筆 `Pending` 狀態的 Organizer 呼叫駁回端點
- **THEN** 系統將該 Organizer 狀態轉為 `Rejected`，記錄審核者與審核時間

#### Scenario: ORG-REVIEW-004 非 Admin 角色嘗試審核
- **WHEN** 角色非 `Admin` 的已登入使用者呼叫核准或駁回端點
- **THEN** 系統回傳 403 禁止存取錯誤，該 Organizer 狀態不變

#### Scenario: ORG-REVIEW-005 重複審核已處理過的申請
- **WHEN** `Admin` 角色使用者對一筆狀態已是 `Approved`、`Rejected` 或 `Suspended` 的 Organizer 呼叫核准或駁回端點
- **THEN** 系統 MUST 拒絕並回報狀態衝突，不改變其狀態

#### Scenario: ORG-REVIEW-006 非 Admin 角色嘗試查詢待審核清單
- **WHEN** 角色非 `Admin` 的已登入使用者呼叫查詢待審核 Organizer 清單端點
- **THEN** 系統回傳 403 禁止存取錯誤，不回傳任何清單資料

### Requirement: 平台管理員可以停權已核准的 Organizer
系統 SHALL 允許 `Admin` 角色使用者將狀態為 `Approved` 的 Organizer 轉為 `Suspended`。只有目前狀態為 `Approved` 的 Organizer 可以被停權；非 `Approved` 狀態（`Pending`／`Rejected`／已是 `Suspended`）呼叫停權端點 MUST 被拒絕，不改變其狀態。停權 MUST NOT 刪除或變更該 Organizer 既有的 Event／Order 等資料庫資料。

停權立即阻擋兩個由本能力自己保證的時機點：(a) 該 Organizer 成員之後嘗試切換操作情境進入該 Organizer（見「會員可以切換目前操作中的 Organizer」需求）；(b) 該成員下一次以 Refresh Token 換發 Access Token 時（見「切換操作情境時同步記錄於 Refresh Token」需求）。**至於既有其他能力要如何感知並強制這個狀態變化**（例如某個已核發、帶有該 Organizer `OrganizerId` claim 的 Access Token，是否可能在過期前仍通過某個其他能力的授權檢查），屬於採用該 claim 做為授權依據的能力自己的責任與範圍，不由本能力保證即時撤銷；這個議題在首個實際採用 `OrganizerId` claim 做授權依據的變更（`event-management-organizer-scoping`）中有明確定義與測試。

平台管理員本次也沒有繞過此限制查看被停權 Organizer 資料的能力，如需這類稽核能力需另開提案。

#### Scenario: ORG-SUSPEND-001 平台管理員停權已核准的 Organizer
- **WHEN** `Admin` 角色使用者對狀態為 `Approved` 的 Organizer 呼叫停權端點
- **THEN** 系統將該 Organizer 狀態轉為 `Suspended`，該 Organizer 全體成員此後 MUST NOT 能再成功切換操作情境進入該 Organizer

#### Scenario: ORG-SUSPEND-002 停權非 Approved 狀態的 Organizer
- **WHEN** `Admin` 角色使用者對狀態非 `Approved` 的 Organizer 呼叫停權端點
- **THEN** 系統 MUST 拒絕並回報狀態衝突，不改變其狀態

#### Scenario: ORG-SUSPEND-003 非 Admin 角色嘗試停權
- **WHEN** 角色非 `Admin` 的已登入使用者呼叫停權端點
- **THEN** 系統回傳 403 禁止存取錯誤，該 Organizer 狀態不變

### Requirement: 會員可以查詢自己所屬的 Organizer 清單
系統 SHALL 允許已登入 Member 查詢自己目前所屬的所有 Organizer（不論狀態），每筆結果 SHALL 包含 Organizer 的 `Id`、`Name`、`Status`。

#### Scenario: ORG-LIST-001 查詢自己所屬的 Organizer 清單
- **WHEN** 已登入 Member 呼叫查詢自己所屬 Organizer 清單端點
- **THEN** 系統回傳該 Member 目前所屬的所有 Organizer 及其狀態，不含其他 Member 所屬的 Organizer

#### Scenario: ORG-LIST-002 尚未加入任何 Organizer
- **WHEN** 已登入 Member 尚未申請或加入任何 Organizer，呼叫查詢清單端點
- **THEN** 系統回傳空清單，不視為錯誤

### Requirement: 會員可以切換目前操作中的 Organizer
系統 SHALL 允許已登入 Member 對自己所屬、且狀態為 `Approved` 的 Organizer 執行「切換操作情境」，成功後系統 SHALL 換發一組新的 Access Token，內含該 Organizer 的 `OrganizerId` claim；Refresh Token 本身的值不變。**由於同一個 Member 可能同時持有多筆有效的 Refresh Token（多裝置／多分頁登入），呼叫切換端點時 MUST 額外提供呼叫者目前持有的 Refresh Token 明文**，系統 SHALL 依此精確定位是哪一筆 Refresh Token 記錄發起本次切換（見「切換操作情境時同步記錄於 Refresh Token」需求），只更新這一筆記錄，不影響呼叫者其他裝置／分頁各自的操作情境。

#### Scenario: ORG-SWITCH-001 切換到自己所屬且已核准的 Organizer
- **WHEN** 已登入 Member 提供自己目前持有且有效的 Refresh Token，對自己所屬、狀態為 `Approved` 的 Organizer 呼叫切換端點
- **THEN** 系統換發新的 Access Token，內含該 Organizer 的 `OrganizerId` claim；回應 MUST NOT 包含新的 Refresh Token（Refresh Token 本身的值不變，呼叫端沿用原本持有的那一組）

#### Scenario: ORG-SWITCH-007 切換操作情境不觸發 Refresh Token 輪替
- **WHEN** ORG-SWITCH-001 的切換請求成功
- **THEN** 系統 MUST NOT 建立任何新的 `RefreshToken` 資料列（不像既有 Refresh Token 換發流程那樣輪替出新記錄），呼叫端原本持有的 Refresh Token 明文於切換前後完全相同；該筆（被更新 `OrganizerId` 的）`RefreshToken` 資料列的 `TokenHash`、`Status`（`Active`）、`ExpiresAt` 皆不變，之後仍可被正常用於呼叫既有 Refresh Token 換發端點

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

### Requirement: 切換操作情境時同步記錄於 Refresh Token，換發 Access Token 時重新驗證其有效性
系統 SHALL 在切換操作情境成功時，將呼叫者於本次請求提供、並經精確定位（依 Refresh Token 明文的雜湊比對，且確認屬於呼叫者本人、狀態為 `Active`）的**那一筆** Refresh Token 記錄的 `OrganizerId` 更新為切換目標；不影響呼叫者名下其他 Refresh Token 記錄；此更新為原地更新，MUST NOT 觸發 Refresh Token 輪替（不建立新的 `RefreshToken` 資料列，回應 MUST NOT 包含新的 Refresh Token 明文，見 `ORG-SWITCH-007`）。系統 SHALL 在既有換發 Access Token 端點（Refresh Token 換發）的流程中，讀取**該次換發所依據的那一筆** Refresh Token 記錄的 `OrganizerId`；若非空，MUST 重新驗證該 Member 目前是否仍是該 Organizer 的成員、且該 Organizer 狀態仍為 `Approved`——驗證通過則新換發的 Access Token 帶相同的 `OrganizerId` claim，且新產生的 Refresh Token 記錄延續同一個 `OrganizerId`；驗證未通過則新 Access Token MUST NOT 帶 `OrganizerId` claim，新產生的 Refresh Token 記錄的 `OrganizerId` 亦 MUST 清空。驗證未通過涵蓋兩種情況：(a) Organizer 狀態變為非 `Approved`（本次交付範圍內，狀態機只允許 `Approved → Suspended` 這一種離開 `Approved` 的轉換，見 `Organizer.Suspend()`，故此分支實務上等同「已被停權」，見 `ORG-REFRESH-003`）；(b) 呼叫者不再是該 Organizer 的成員（`OrganizerMember` 記錄不存在）——**本次交付範圍內沒有任何功能會移除既有 `OrganizerMember` 記錄**（見 Non-Goals：不提供邀請或移除成員功能），這個分支目前無法透過任何既有端點觸發，屬於為未來擴充（例如日後新增「移除成員」功能）預先寫好的防禦邏輯；系統仍 SHALL 對這個分支提供對應的單元測試（直接以測試手法模擬「成員關聯不存在」的資料狀態，見 `ORG-REFRESH-004`），確保這段防禦邏輯本身正確，即使目前沒有生產路徑會觸發它。

#### Scenario: ORG-REFRESH-001 切換操作情境後僅更新發起切換的那一筆 Refresh Token 記錄
- **WHEN** Member 同時持有兩筆有效的 Refresh Token（例如兩台裝置各自登入），提供其中一筆對自己所屬、狀態為 `Approved` 的 Organizer 呼叫切換端點成功
- **THEN** 系統除了換發新 Access Token，SHALL 只將本次請求提供的那一筆 Refresh Token 記錄的 `OrganizerId` 更新為該 Organizer，另一筆 Refresh Token 記錄的 `OrganizerId` MUST 維持不變

#### Scenario: ORG-REFRESH-002 換發時 Organizer 資格仍然有效
- **WHEN** Member 換發 Access Token，其目前 Refresh Token 記錄的 `OrganizerId` 非空，且該 Member 目前仍是該 Organizer 的成員、該 Organizer 狀態仍為 `Approved`
- **THEN** 新換發的 Access Token SHALL 帶相同的 `OrganizerId` claim，新產生的 Refresh Token 記錄延續同一個 `OrganizerId`

#### Scenario: ORG-REFRESH-003 換發時該 Organizer 已被停權
- **WHEN** Member 換發 Access Token，其目前 Refresh Token 記錄的 `OrganizerId` 非空，但該 Organizer 狀態已變為 `Suspended`
- **THEN** 新換發的 Access Token MUST NOT 帶 `OrganizerId` claim，新產生的 Refresh Token 記錄的 `OrganizerId` MUST 為空

#### Scenario: ORG-REFRESH-004 換發時呼叫者已不再是該 Organizer 成員
- **WHEN** Member 換發 Access Token，其目前 Refresh Token 記錄的 `OrganizerId` 非空，但查無對應的 `OrganizerMember` 記錄（模擬成員資格已被移除的資料狀態，即使本次交付範圍內沒有任何端點會實際產生這個狀態）
- **THEN** 新換發的 Access Token MUST NOT 帶 `OrganizerId` claim，新產生的 Refresh Token 記錄的 `OrganizerId` MUST 為空

### Requirement: 切換操作情境與 Refresh Token 換發併發競爭同一筆記錄時 MUST 有明確定義的失敗行為
系統 SHALL 依賴既有的資料庫層樂觀併發權杖（Postgres `xmin` 系統欄位，已設定為 EF Core `IsConcurrencyToken`，見既有 `RefreshTokenConfiguration`）偵測「切換操作情境」與「Refresh Token 換發」併發作用於同一筆 `RefreshToken` 資料列的情況；兩者皆對該資料列執行寫入（切換更新 `OrganizerId`；換發呼叫 `MarkAsUsed()`），任一方的 `SaveChanges` 若因併發衝突拋出 `DbUpdateConcurrencyException`，MUST 被該次操作自身的 Handler 捕捉，不得讓例外往上拋給全域 `IExceptionHandler` 變成非預期的 500。**兩種方向的併發失敗，狀態碼一致定義為 401（未授權）**，比照既有 `RefreshTokenHandler` 對「另一個併發請求已先一步消費了這個 Token」情境的既定回應（不新增 409 分類）：先完成的一方正常成功；後完成的一方的 `RefreshToken` 資料列因為已被對方修改，MUST 被視為「這個 Token 已不是你原本讀取到的那個狀態」，回傳與既有 Refresh Token 無效相同的 401 錯誤，不覆寫對方已寫入的結果，不造成資料錯亂。呼叫端只需重新呼叫一次（切換或換發皆是可安全重試的操作）：重試時會讀到對方已寫入的最新狀態，依當下狀態正常判斷。

#### Scenario: ORG-CONCURRENCY-001 切換先完成，換發後完成而失敗
- **WHEN** 切換操作情境請求與 Refresh Token 換發請求併發作用於同一筆 `RefreshToken` 資料列，切換的 `SaveChanges` 先提交成功
- **THEN** 換發請求的 `SaveChanges` MUST 因併發衝突失敗，`RefreshTokenHandler` MUST 捕捉並回傳 401，不換發任何新 Token，不建立新的 `RefreshToken` 資料列；切換已寫入的 `OrganizerId` 更新結果不被覆寫

#### Scenario: ORG-CONCURRENCY-002 換發先完成，切換後完成而失敗
- **WHEN** 切換操作情境請求與 Refresh Token 換發請求併發作用於同一筆 `RefreshToken` 資料列，換發的 `SaveChanges` 先提交成功（該筆記錄已被標記為 `Used` 並產生新的輪替記錄）
- **THEN** 切換請求的 `SaveChanges` MUST 因併發衝突失敗，`SwitchOrganizerContextHandler` MUST 捕捉並回傳 401，不換發任何新 Access Token，不更新任何 `RefreshToken` 記錄的 `OrganizerId`；換發已產生的新 `RefreshToken` 記錄不受影響

### Requirement: `RequireOrganizerContext` Authorization Policy MUST 驗證 claim 格式並 fail-closed
系統 SHALL 提供 `RequireOrganizerContext` Authorization Policy，供依賴本能力的變更（例如 `event-management-organizer-scoping`）套用至需要「已切換至一個 Approved Organizer」才能呼叫的端點。此 Policy MUST NOT 僅檢查 `OrganizerId` claim 是否存在，還 MUST 驗證該 claim 的值可被解析為合法、非 `Guid.Empty` 的 `Guid`；claim 不存在、值為空字串、值無法解析為 `Guid`、或值恰為 `Guid.Empty`，皆 MUST 視同未通過本 Policy，一律回傳 403（fail-closed），不得因解析例外而變成非預期的 500。系統 SHALL 提供一個共用的 `ClaimsPrincipal` 擴充方法（例如 `TryGetOrganizerId`），封裝這段解析與驗證邏輯，供本 Policy 的 Handler 與任何需要在 Controller／Application 層讀取呼叫端目前 `OrganizerId`（例如建立活動時取得歸屬、後台列表查詢的過濾條件）的程式碼共用，不得由各處各自呼叫 `Guid.Parse` 各自實作一套。

此 claim 由 `JwtTokenService.GenerateAccessToken` 依型別為 `Guid` 的參數簽發（見「會員可以切換目前操作中的 Organizer」「切換操作情境時同步記錄於 Refresh Token」兩個需求），正常流程下必為合法、非空的 `Guid`；本 Requirement 的驗證是防禦本 Policy 的執行環境被未來程式碼誤用（例如意外傳入空字串）時仍能 fail-closed，而非假設外部呼叫端能偽造已簽章 JWT 內的 claim 值。

#### Scenario: ORG-POLICY-001 claim 為合法非空 Guid 時通過
- **WHEN** 呼叫端的 Access Token 帶有可解析為合法、非 `Guid.Empty` 的 `Guid` 的 `OrganizerId` claim
- **THEN** `RequireOrganizerContext` Policy 判定通過，受理該請求

#### Scenario: ORG-POLICY-002 claim 不存在時拒絕
- **WHEN** 呼叫端的 Access Token 不帶 `OrganizerId` claim
- **THEN** `RequireOrganizerContext` Policy MUST 判定不通過，回傳 403，不執行任何查詢或寫入

#### Scenario: ORG-POLICY-003 claim 值無法解析為 Guid 時拒絕
- **WHEN** 呼叫端的 Access Token 帶有 `OrganizerId` claim，但其值為空字串或無法解析為合法 `Guid`（例如非 Guid 格式的任意文字）
- **THEN** `RequireOrganizerContext` Policy MUST 判定不通過，回傳 403，不得因解析例外造成 500，不執行任何查詢或寫入

#### Scenario: ORG-POLICY-004 claim 值為 Guid.Empty 時拒絕
- **WHEN** 呼叫端的 Access Token 帶有 `OrganizerId` claim，其值可解析為 `Guid`，但恰為 `Guid.Empty`
- **THEN** `RequireOrganizerContext` Policy MUST 判定不通過，回傳 403，不執行任何查詢或寫入
