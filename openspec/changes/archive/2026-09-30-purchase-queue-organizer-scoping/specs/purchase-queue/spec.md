## RENAMED Requirements

- FROM: `### Requirement: Admin 可針對個別活動開關熱門搶購模式`
- TO: `### Requirement: 已切換至一個 Approved Organizer 的成員可針對自己名下活動開關熱門搶購模式`

- FROM: `### Requirement: Admin 關閉熱門搶購模式後，既有排隊紀錄不主動清理`
- TO: `### Requirement: 關閉熱門搶購模式後，既有排隊紀錄不主動清理`

## MODIFIED Requirements

### Requirement: 已切換至一個 Approved Organizer 的成員可針對自己名下活動開關熱門搶購模式
系統 SHALL 提供後台端點 `PATCH /api/admin/events/{id}/queue-mode`，Body 為 `{ "enabled": bool }`，允許 Access Token 帶有效 `OrganizerId` claim（即已切換至一個 Approved Organizer）的使用者開啟或關閉指定活動的「熱門搶購模式」（`Event.IsQueueModeEnabled`）；未帶該 claim 呼叫 MUST 被拒絕（`403`），未登入呼叫 MUST 被拒絕（`401`），皆不變更任何活動狀態。`enabled` 欄位缺漏或非 boolean 時 MUST 回傳 `400` 驗證錯誤，不變更活動狀態；活動 Id 不存在時 MUST 回傳 `404`。系統 SHALL 在確認活動存在後，額外核對該活動的 `OrganizerId` 是否等於呼叫端目前 Organizer，不一致時 MUST 視同活動不存在（`404`），不得回傳 `403`、不得變更任何活動狀態；此 404 的回應 body MUST 與活動不存在時逐字相同（含錯誤訊息），不得另寫可區分的訊息——避免已核准的 Organizer 成員對其他 Organizer 名下的活動開關熱門搶購模式，或藉由不同的錯誤結果得知「這個活動存在、只是不屬於自己」。此授權同樣適用 `RequireOrganizerContext` Policy 不即時查表的既知取捨（見 `organizer-management` 能力與 `event-management` 能力 `EVT-AUTHZ-004`）：Organizer 被停權後，其成員手上停權前已核發、尚未過期的 Access Token，在自然到期前仍可開關該 Organizer 名下活動的熱門搶購模式；延遲上限為 `AccessTokenExpirationMinutes`，停權後的換發與切換由 `organizer-management` 能力立即阻擋。成功時 HTTP 回應 MUST 為 `204 No Content`（比照既有 `PATCH /api/admin/tickets/{id}/redeem` 的回應慣例，不回傳 body）。活動的熱門搶購模式預設為關閉，不影響既有活動的既定下單行為。

#### Scenario: PQ-ADMIN-001 開啟熱門搶購模式
- **WHEN** 已切換至一個 Approved Organizer 的使用者對自己名下活動呼叫開啟熱門搶購模式
- **THEN** 系統將該活動的 `IsQueueModeEnabled` 設為 `true`

#### Scenario: PQ-ADMIN-002 關閉熱門搶購模式
- **WHEN** 已切換至一個 Approved Organizer 的使用者對自己名下、已開啟熱門搶購模式的活動呼叫關閉
- **THEN** 系統將該活動的 `IsQueueModeEnabled` 設為 `false`

#### Scenario: PQ-ADMIN-003 未帶 OrganizerId claim 嘗試開關熱門搶購模式
- **WHEN** 已登入但 Access Token 未帶 `OrganizerId` claim 的使用者（含一般 `Member`，以及角色為 `Admin` 但尚未切換 Organizer 者）呼叫開關熱門搶購模式端點
- **THEN** 系統回傳 403，不變更任何活動狀態

#### Scenario: PQ-ADMIN-003a 未登入嘗試開關熱門搶購模式
- **WHEN** 未登入的使用者呼叫開關熱門搶購模式端點
- **THEN** 系統回傳 401，不變更任何活動狀態

#### Scenario: PQ-ADMIN-004 請求 Body 完全缺漏 enabled 欄位
- **WHEN** 已切換至一個 Approved Organizer 的使用者對自己名下活動呼叫開關熱門搶購模式端點，請求 Body 為 `{}`（完全未包含 `enabled` 欄位）
- **THEN** 系統回傳 `400` 驗證錯誤，不變更任何活動狀態，不得將缺漏誤判為 `false` 並執行關閉

#### Scenario: PQ-ADMIN-005 對不存在的活動開關熱門搶購模式
- **WHEN** 已切換至一個 Approved Organizer 的使用者對不存在的活動 Id 呼叫開關熱門搶購模式端點
- **THEN** 系統回傳 `404 Not Found`

#### Scenario: PQ-ADMIN-006 請求明確指定 enabled 為 false
- **WHEN** 已切換至一個 Approved Organizer 的使用者對自己名下活動呼叫開關熱門搶購模式端點，請求 Body 為 `{ "enabled": false }`
- **THEN** 系統成功將該活動的 `IsQueueModeEnabled` 設為 `false`，視為與 PQ-ADMIN-004（完全缺漏）不同的兩種情形，不得混淆處理

#### Scenario: PQ-ADMIN-007 請求的 enabled 型別錯誤
- **WHEN** 已切換至一個 Approved Organizer 的使用者對自己名下活動呼叫開關熱門搶購模式端點，請求 Body 為 `{ "enabled": "false" }`（字串而非 boolean）
- **THEN** 系統回傳 `400`，不變更任何活動狀態

#### Scenario: PQ-ADMIN-008 對其他 Organizer 名下的活動開關熱門搶購模式
- **WHEN** 已切換至 Organizer A 的使用者對存在、但 `OrganizerId` 屬於 Organizer B 的活動呼叫開關熱門搶購模式端點
- **THEN** 系統 MUST 回傳 404，回應 body 與以不存在的活動 Id 呼叫時逐字相同（含錯誤訊息），不得回傳 403，不變更該活動的 `IsQueueModeEnabled`

#### Scenario: PQ-ADMIN-009 停權前已核發、尚未過期的 Access Token 於過期前仍可開關熱門搶購模式（已知延遲視窗，非缺陷）
- **WHEN** 某 Member 持有一組停權前核發、尚未過期、帶有 Organizer 的 `OrganizerId` claim 的 Access Token，該 Organizer 隨後被停權，該 Member 在未重新換發、未重新切換的情況下，對屬於該 Organizer 的活動呼叫開關熱門搶購模式端點
- **THEN** 系統 SHALL 依 `RequireOrganizerContext` Policy 的既定行為受理該請求並變更 `IsQueueModeEnabled`；延遲上限為 `AccessTokenExpirationMinutes`，換發或切換即被拒絕的負向路徑由 `organizer-management` 能力 `ORG-REFRESH-003`／`ORG-SUSPEND-001` 負責

### Requirement: 關閉熱門搶購模式後，既有排隊紀錄不主動清理
系統 SHALL 在活動的熱門搶購模式被關閉（`IsQueueModeEnabled = false`）後，停止對該活動的 `Waiting` 紀錄執行入場推進，但 MUST NOT 主動刪除或重置既有的 `PurchaseQueueEntry` 紀錄；`ticket-purchase` 能力的排隊資格檢查僅在活動 `IsQueueModeEnabled = true` 時執行，關閉後即不再檢查排隊資格。若之後重新開啟熱門搶購模式，系統 SHALL 依既有 `JoinedAtUtc ASC` 順序（含同毫秒 tie-break 規則）繼續推進尚未處理的 `Waiting` 紀錄，不重新排序或要求會員重新加入排隊。

#### Scenario: PQ-TOGGLE-001 關閉熱門搶購模式後既有 Waiting 紀錄停止推進
- **WHEN** 活動所屬 Organizer 的成員將已有多筆 `Waiting` 排隊紀錄的活動關閉熱門搶購模式
- **THEN** 背景推進機制不再處理該活動，既有排隊紀錄維持原狀態不被刪除，該活動的建立訂單請求不再檢查排隊資格

#### Scenario: PQ-TOGGLE-002 重新開啟熱門搶購模式後沿用既有排隊順序
- **WHEN** 活動所屬 Organizer 的成員將先前關閉、仍存有 `Waiting` 紀錄的活動重新開啟熱門搶購模式
- **THEN** 背景推進機制依既有 `JoinedAtUtc ASC` 順序（含同毫秒 tie-break 規則）繼續推進這些 `Waiting` 紀錄，不要求會員重新加入排隊、不重置加入時間

### Requirement: 買家可查詢自己的排隊狀態
系統 SHALL 提供已登入會員查詢自己在指定活動排隊狀態的端點 `GET /api/events/{id}/queue/entries/me`；活動 Id 不存在時回傳 `404 Not Found`。比照加入排隊端點，此端點只要求已登入、不限制角色，且只回傳呼叫者本人（依 JWT Claims 判斷）的排隊紀錄，不支援查詢或代入其他會員 Id（端點路徑 `/me` 即代表僅限本人）。查詢時，系統只在該會員對該活動狀態為 `Waiting`／`Admitted`／`Expired` 的紀錄中取加入時間最新的一筆作為代表；查無此範圍內的紀錄時（含從未加入，或僅有的歷史紀錄皆為 `Completed`）回傳「尚未加入排隊」狀態，即使該會員過去對此活動曾有 `Completed` 的歷史紀錄，也視為可重新加入排隊。狀態為 `Waiting` 時，回應 SHALL 包含目前排在自己之前的等待人數（依 `JoinedAtUtc ASC, Id ASC` 排序後，早於自己的 `Waiting` 紀錄數，此計算 **完全透過 Postgres 查詢完成，不經過 Redis**，見下方精確化說明）；狀態為 `Admitted` 且未逾時時，回應 SHALL 標示已可送出訂單；狀態為 `Expired` 時，回應 SHALL 標示入場名額已逾時。此端點為查詢操作，MUST 於查詢當下依 `AdmissionExpiresAtUtc` 與目前時間比對即時推導是否已逾時（比照既有訂單逾時「查詢時推導」的既定慣例），不得只依賴背景服務尚未執行完成的 `Expired` 標記——資料庫紀錄狀態仍為 `Admitted` 但已超過 `AdmissionExpiresAtUtc` 時，查詢回應 SHALL 視為已逾時，不落地寫回 `Expired`（落地寫回由背景服務或下一次加入排隊時的自我修復流程處理，維持單一寫入來源）。

回應 SHALL 額外附帶 `queueModeEnabled` 欄位，反映該活動當下的 `Event.IsQueueModeEnabled`，讓前端在每次輪詢排隊狀態時，能一併得知活動是否仍處於熱門搶購模式，不需另外呼叫活動列表 API 確認——買家在排隊等待畫面（`Waiting`）停留期間，若活動所屬 Organizer 的成員將該活動的熱門搶購模式關閉，前端的下一次輪詢即可從 `queueModeEnabled = false` 得知，據以停止排隊流程、開放正常購票操作（見 `buyer-web-ui` 能力）；若已在 `Waiting` 或 `Admitted` 但 `IsQueueModeEnabled` 已被關閉，回應的排隊狀態欄位（`status`／`waitingCount` 等）SHALL 仍依實際紀錄內容如實回傳，由前端依 `queueModeEnabled` 決定是否據以停止排隊流程，後端本身不因 `IsQueueModeEnabled = false` 而改變這筆排隊紀錄的狀態或提前清理。

**排序一致性範圍精確化（因 `purchase-queue-redis-admission` 改動而新增，第七輪審查發現的跨 Requirement 一致性缺口）**：本 Requirement 的 Handler 不受本次改動影響，「目前排在自己之前的等待人數」持續透過純 Postgres 查詢（`JoinedAtUtc ASC, Id ASC`）計算，未改用 Redis。但 `PQ-ADMIT-004`／`PQ-ADMIT-005` 已將入場推進機制的同毫秒 tie-break 改為 Redis member 字串順序，與本 Requirement 使用的 `Id ASC` 不保證一致——這代表在極少數「多筆紀錄 `JoinedAtUtc` 完全相同」的情況下，本查詢回報的「前方等待人數排名」與入場推進機制實際採用的順序可能有微小偏差（僅限同毫秒紀錄彼此之間，不影響與非同毫秒紀錄的相對順序，也不影響「不超額入場」等 MUST 級保證）。此為 `purchase-queue-redis-admission` 改動範圍內、誠實記錄的已知限制，不修正本 Requirement 的計算方式（修正需要讓此查詢也改用 Redis，超出本次改動範圍）。

#### Scenario: PQ-STATUS-001 查詢時即時推導已逾時但尚未被背景服務標記的紀錄
- **WHEN** 已登入會員查詢自己的排隊狀態，該筆紀錄的資料庫狀態仍為 `Admitted`，但 `AdmissionExpiresAtUtc <=` 目前時間（背景服務尚未執行下一輪推進）
- **THEN** 系統回應視為已逾時狀態，不因資料庫紀錄尚未被背景服務改寫為 `Expired` 而回傳「已可送出訂單」

#### Scenario: PQ-STATUS-002 查詢等待中的排隊狀態
- **WHEN** 已登入會員查詢自己狀態為 `Waiting` 的排隊紀錄
- **THEN** 系統回傳 `Waiting` 狀態與目前前方等待人數

#### Scenario: PQ-STATUS-003 查詢已入場的排隊狀態
- **WHEN** 已登入會員查詢自己狀態為 `Admitted` 且尚未逾時的排隊紀錄
- **THEN** 系統回傳已入場狀態，標示可送出訂單

#### Scenario: PQ-STATUS-004 查詢已逾時的排隊狀態
- **WHEN** 已登入會員查詢自己狀態為 `Admitted` 但已超過入場逾時時間的排隊紀錄
- **THEN** 系統回傳已逾時狀態

#### Scenario: PQ-STATUS-005 查詢尚未加入排隊的活動
- **WHEN** 已登入會員查詢自己在某活動的排隊狀態，但從未加入過排隊
- **THEN** 系統回傳「尚未加入排隊」狀態

#### Scenario: PQ-STATUS-006 查詢時僅有已完成的歷史紀錄
- **WHEN** 已登入會員對某活動僅有的排隊紀錄狀態為 `Completed`（過去已成功透過排隊建立訂單），此後未再加入排隊
- **THEN** 系統回傳「尚未加入排隊」狀態，而非回報已完成或錯誤

#### Scenario: PQ-STATUS-007 查詢不存在的活動的排隊狀態
- **WHEN** 已登入會員以不存在的活動 Id 查詢排隊狀態
- **THEN** 系統回傳 `404 Not Found`

#### Scenario: PQ-STATUS-008 查詢回應附帶當下的 queueModeEnabled
- **WHEN** 已登入會員查詢自己在某活動的排隊狀態，該活動的 `IsQueueModeEnabled` 於查詢當下為 `false`（例如活動所屬 Organizer 的成員已在該會員排隊等待期間關閉熱門搶購模式）
- **THEN** 系統回應的 `queueModeEnabled` 欄位為 `false`，排隊紀錄本身的狀態（如仍為 `Waiting`）如實回傳、不因活動已關閉熱門搶購模式而被清理或竄改

