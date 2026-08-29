# purchase-queue Specification

## Purpose
TBD - created by change rate-limiting-queue. Update Purpose after archive.

## Requirements

### Requirement: Admin 可針對個別活動開關熱門搶購模式
系統 SHALL 提供 Admin 專用端點 `PATCH /api/admin/events/{id}/queue-mode`，Body 為 `{ "enabled": bool }`，允許已登入且角色為 `Admin` 的使用者開啟或關閉指定活動的「熱門搶購模式」（`Event.IsQueueModeEnabled`）；非 `Admin` 或未登入呼叫 MUST 被拒絕（`403`），不變更任何活動狀態。`enabled` 欄位缺漏或非 boolean 時 MUST 回傳 `400` 驗證錯誤，不變更活動狀態；活動 Id 不存在時 MUST 回傳 `404`。成功時 HTTP 回應 MUST 為 `204 No Content`（比照既有 `PATCH /api/admin/tickets/{id}/redeem` 的回應慣例，不回傳 body）。活動的熱門搶購模式預設為關閉，不影響既有活動的既定下單行為。

#### Scenario: PQ-ADMIN-001 Admin 開啟熱門搶購模式
- **WHEN** 已登入 Admin 對某活動呼叫開啟熱門搶購模式
- **THEN** 系統將該活動的 `IsQueueModeEnabled` 設為 `true`

#### Scenario: PQ-ADMIN-002 Admin 關閉熱門搶購模式
- **WHEN** 已登入 Admin 對已開啟熱門搶購模式的活動呼叫關閉
- **THEN** 系統將該活動的 `IsQueueModeEnabled` 設為 `false`

#### Scenario: PQ-ADMIN-003 非 Admin 嘗試開關熱門搶購模式
- **WHEN** 非 Admin 角色或未登入的使用者呼叫開關熱門搶購模式端點
- **THEN** 系統 MUST 拒絕，不變更任何活動狀態

#### Scenario: PQ-ADMIN-004 請求 Body 完全缺漏 enabled 欄位
- **WHEN** 已登入 Admin 呼叫開關熱門搶購模式端點，請求 Body 為 `{}`（完全未包含 `enabled` 欄位）
- **THEN** 系統回傳 `400` 驗證錯誤，不變更任何活動狀態，不得將缺漏誤判為 `false` 並執行關閉

#### Scenario: PQ-ADMIN-005 對不存在的活動開關熱門搶購模式
- **WHEN** 已登入 Admin 對不存在的活動 Id 呼叫開關熱門搶購模式端點
- **THEN** 系統回傳 `404 Not Found`

#### Scenario: PQ-ADMIN-006 請求明確指定 enabled 為 false
- **WHEN** 已登入 Admin 呼叫開關熱門搶購模式端點，請求 Body 為 `{ "enabled": false }`
- **THEN** 系統成功將該活動的 `IsQueueModeEnabled` 設為 `false`，視為與 PQ-ADMIN-004（完全缺漏）不同的兩種情形，不得混淆處理

#### Scenario: PQ-ADMIN-007 請求的 enabled 型別錯誤
- **WHEN** 已登入 Admin 呼叫開關熱門搶購模式端點，請求 Body 為 `{ "enabled": "false" }`（字串而非 boolean）
- **THEN** 系統回傳 `400`，不變更任何活動狀態

### Requirement: 買家可加入活動的購票排隊
系統 SHALL 提供已登入會員加入指定活動排隊的端點 `POST /api/events/{id}/queue/entries`；僅限該活動 `IsQueueModeEnabled = true` 時可加入，`IsQueueModeEnabled = false` 的活動 MUST 拒絕加入請求（回傳 `409 Conflict`）。活動 Id 不存在時 MUST 回傳 `404 Not Found`。此端點只要求已登入（比照既有 `POST /api/orders` 的 `[Authorize]`），不限制會員角色——`MemberRole.Member` 與 `MemberRole.Admin` 皆可呼叫。排隊紀錄綁定的會員身份 MUST 完全取自呼叫者的 JWT Claims，不接受請求 Body 或任何其他輸入指定/覆寫為其他會員 Id。

系統資料庫層 MUST 保證同一會員對同一活動同時最多只有一筆進行中（`Waiting` 或未逾時 `Admitted`）的排隊紀錄。加入排隊的處理 MUST 在單一交易內完成以下判斷，確保「逾時後可重新加入」與「唯一性約束」兩者不互相矛盾：
1. 鎖定並查詢該會員在該活動目前「進行中」（`Status IN (Waiting, Admitted)`）的紀錄（依唯一性約束，最多一筆）
2. 若查得一筆且狀態為 `Admitted` 但已逾時（`AdmissionExpiresAtUtc <= 目前時間`）：系統 MUST 先將該筆紀錄轉為 `Expired` 並與後續操作一起提交，視同「查無進行中紀錄」繼續下一步——這一步是必要的，因為「已逾時」只是查詢時的推導結果，資料庫的唯一性約束只認實際落地的 `Status` 欄位，若不先把它寫成 `Expired`，該筆逾時紀錄仍會佔用約束名額，導致同一會員的重新加入請求被自己過去的逾時紀錄擋下
3. 若查得一筆且仍為 `Waiting` 或未逾時的 `Admitted`：回傳該筆既有紀錄，不建立新紀錄（Idempotent）
4. 若查無進行中紀錄（原本就沒有，或第 2 步剛轉為 `Expired`）：建立一筆新的 `Waiting` 紀錄

已逾時（`Expired`）或已完成（`Completed`）的歷史紀錄不影響重新加入，會員可在資格逾時或完成後對同一活動再次加入排隊，產生新的一筆紀錄。新加入的排隊紀錄初始狀態為 `Waiting`，依加入時間先後排序。

即使上述交易內已先做了鎖定查詢，仍可能有另一個完全獨立的加入排隊請求，在本次交易查詢「查無進行中紀錄」之後、提交之前，也各自判斷「查無進行中紀錄」並嘗試建立新紀錄——此時資料庫的唯一性約束會讓其中一個提交失敗。系統 MUST 明確處理這種提交衝突：偵測到違反唯一性約束時，MUST NOT 讓該次請求以未預期錯誤（如 500）結束；系統 SHALL 改為重新查詢該會員目前進行中的紀錄並回傳，讓兩個並發請求最終都成功、且回傳同一筆紀錄的 Id。若重新查詢仍查無進行中紀錄（極端情況），系統 SHALL 至多重試一次完整的加入流程，仍失敗則回傳明確、可觀察的錯誤，不得無限重試、不得靜默失敗。

#### Scenario: PQ-JOIN-001 首次加入排隊
- **WHEN** 已登入會員對已開啟熱門搶購模式的活動首次呼叫加入排隊
- **THEN** 系統建立一筆 `Waiting` 狀態的排隊紀錄，記錄加入時間

#### Scenario: PQ-JOIN-002 重複加入排隊
- **WHEN** 已登入會員對同一活動已有 `Waiting` 或未逾時的 `Admitted` 排隊紀錄，再次呼叫加入排隊
- **THEN** 系統回傳既有的排隊紀錄，不建立新紀錄

#### Scenario: PQ-JOIN-003 資格逾時後重新加入排隊
- **WHEN** 已登入會員先前在該活動的排隊紀錄狀態為 `Admitted` 但已超過入場逾時時間（尚未被背景服務標記為 `Expired`），會員再次對同一活動呼叫加入排隊
- **THEN** 系統將原本的紀錄轉為 `Expired`，並在同一次處理中建立一筆新的 `Waiting` 狀態排隊紀錄，加入時間為本次呼叫的時間；不因該筆逾時紀錄仍在資料庫中而拒絕本次加入

#### Scenario: PQ-JOIN-004 兩個請求同時首次加入排隊
- **WHEN** 已登入會員對同一活動、尚無任何排隊紀錄，幾乎同時送出兩次加入排隊請求
- **THEN** 系統最終只留存一筆進行中的排隊紀錄，兩次請求皆成功回應且回傳同一筆紀錄的 Id，不產生重複紀錄、不回傳非預期錯誤

#### Scenario: PQ-JOIN-005 對未開啟熱門搶購模式的活動加入排隊
- **WHEN** 已登入會員對 `IsQueueModeEnabled = false` 的活動呼叫加入排隊
- **THEN** 系統 MUST 拒絕，回傳 `409 Conflict`，不建立排隊紀錄

#### Scenario: PQ-JOIN-006 對不存在的活動加入排隊
- **WHEN** 已登入會員對不存在的活動 Id 呼叫加入排隊
- **THEN** 系統回傳 `404 Not Found`

#### Scenario: PQ-JOIN-007 Admin 角色帳號也可加入排隊
- **WHEN** 已登入且角色為 `Admin` 的帳號，對已開啟熱門搶購模式的活動呼叫加入排隊
- **THEN** 系統依一般會員的既定規則處理，成功建立排隊紀錄，不因呼叫者角色為 `Admin` 而拒絕

#### Scenario: PQ-JOIN-008 未登入呼叫加入排隊
- **WHEN** 未登入的使用者呼叫加入排隊端點
- **THEN** 系統 MUST 拒絕，不建立排隊紀錄

#### Scenario: PQ-JOIN-009 排隊紀錄的會員身份一律取自 JWT
- **WHEN** 已登入會員呼叫加入排隊端點
- **THEN** 系統建立的排隊紀錄，其 `MemberId` 為呼叫者 JWT Claims 中的會員 Id，不受請求中任何其他輸入影響（端點本身也不接受帶入會員 Id 的參數）

### Requirement: 買家可查詢自己的排隊狀態
系統 SHALL 提供已登入會員查詢自己在指定活動排隊狀態的端點 `GET /api/events/{id}/queue/entries/me`；活動 Id 不存在時回傳 `404 Not Found`。比照加入排隊端點，此端點只要求已登入、不限制角色，且只回傳呼叫者本人（依 JWT Claims 判斷）的排隊紀錄，不支援查詢或代入其他會員 Id（端點路徑 `/me` 即代表僅限本人）。查詢時，系統只在該會員對該活動狀態為 `Waiting`／`Admitted`／`Expired` 的紀錄中取加入時間最新的一筆作為代表；查無此範圍內的紀錄時（含從未加入，或僅有的歷史紀錄皆為 `Completed`）回傳「尚未加入排隊」狀態，即使該會員過去對此活動曾有 `Completed` 的歷史紀錄，也視為可重新加入排隊。狀態為 `Waiting` 時，回應 SHALL 包含目前排在自己之前的等待人數（依 `JoinedAtUtc ASC, Id ASC` 排序後，早於自己的 `Waiting` 紀錄數，排序規則須與入場推進機制一致）；狀態為 `Admitted` 且未逾時時，回應 SHALL 標示已可送出訂單；狀態為 `Expired` 時，回應 SHALL 標示入場名額已逾時。此端點為查詢操作，MUST 於查詢當下依 `AdmissionExpiresAtUtc` 與目前時間比對即時推導是否已逾時（比照既有訂單逾時「查詢時推導」的既定慣例），不得只依賴背景服務尚未執行完成的 `Expired` 標記——資料庫紀錄狀態仍為 `Admitted` 但已超過 `AdmissionExpiresAtUtc` 時，查詢回應 SHALL 視為已逾時，不落地寫回 `Expired`（落地寫回由背景服務或下一次加入排隊時的自我修復流程處理，維持單一寫入來源）。

回應 SHALL 額外附帶 `queueModeEnabled` 欄位，反映該活動當下的 `Event.IsQueueModeEnabled`，讓前端在每次輪詢排隊狀態時，能一併得知活動是否仍處於熱門搶購模式，不需另外呼叫活動列表 API 確認——買家在排隊等待畫面（`Waiting`）停留期間，若 Admin 將該活動的熱門搶購模式關閉，前端的下一次輪詢即可從 `queueModeEnabled = false` 得知，據以停止排隊流程、開放正常購票操作（見 `buyer-web-ui` 能力）；若已在 `Waiting` 或 `Admitted` 但 `IsQueueModeEnabled` 已被關閉，回應的排隊狀態欄位（`status`／`waitingCount` 等）SHALL 仍依實際紀錄內容如實回傳，由前端依 `queueModeEnabled` 決定是否據以停止排隊流程，後端本身不因 `IsQueueModeEnabled = false` 而改變這筆排隊紀錄的狀態或提前清理。

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
- **WHEN** 已登入會員查詢自己在某活動的排隊狀態，該活動的 `IsQueueModeEnabled` 於查詢當下為 `false`（例如 Admin 已在該會員排隊等待期間關閉熱門搶購模式）
- **THEN** 系統回應的 `queueModeEnabled` 欄位為 `false`，排隊紀錄本身的狀態（如仍為 `Waiting`）如實回傳、不因活動已關閉熱門搶購模式而被清理或竄改

### Requirement: 排隊入場名額依先後順序推進，且有名額上限
系統 SHALL 以週期性背景處理，針對每個 `IsQueueModeEnabled = true` 的活動，計算目前有效入場名額（**定義為狀態恰為 `Admitted` 且未逾時的紀錄數**——一筆紀錄只要離開 `Admitted` 狀態（轉為 `Completed` 或 `Expired`），即刻不再計入有效入場名額，不論轉出的原因或時間點）與設定的上限值；有剩餘名額時，依 `JoinedAtUtc ASC, Id ASC`（`Id` 作為加入時間相同時的排序依據，確保排序結果穩定、可重現）由舊到新，依序將 `Waiting` 紀錄推進為 `Admitted` 並設定入場逾時時間，直到補滿上限或無更多 `Waiting` 紀錄。同一活動的名額計算與推進 MUST 具備交易一致性，不得因併發處理而超額入場。已入場但超過入場逾時時間仍未完成訂單的紀錄，系統 SHALL 標記為 `Expired`，該名額自狀態轉換的當下起不再計入有效名額，供下一輪推進使用。

#### Scenario: PQ-ADMIT-001 有剩餘名額時推進等待中的排隊
- **WHEN** 某活動目前有效入場名額未達上限，且存在 `Waiting` 狀態的排隊紀錄
- **THEN** 系統依 `JoinedAtUtc ASC, Id ASC` 排序最前的 `Waiting` 紀錄開始，依序推進為 `Admitted`，直到補滿上限或無更多等待紀錄

#### Scenario: PQ-ADMIT-002 名額已滿時不推進
- **WHEN** 某活動目前有效入場名額已達上限
- **THEN** 系統不將任何 `Waiting` 紀錄推進為 `Admitted`，維持其等待狀態

#### Scenario: PQ-ADMIT-003 入場逾時釋放名額
- **WHEN** 某筆 `Admitted` 排隊紀錄已超過入場逾時時間，且該筆紀錄對應的訂單未完成建立
- **THEN** 系統將該紀錄標記為 `Expired`，釋放的名額於下一輪推進提供給依 `JoinedAtUtc ASC, Id ASC` 排序最前的 `Waiting` 紀錄

#### Scenario: PQ-ADMIT-004 併發推進不超額入場
- **WHEN** 背景處理同時間針對同一活動計算名額與推進排隊
- **THEN** 系統 MUST 確保單一活動同時只有一次推進在進行，最終有效入場名額不超過設定上限

### Requirement: 等待中的排隊紀錄沒有自身逾時機制
`Waiting` 狀態的排隊紀錄 SHALL NOT 因等待時間長短而自動失效或被清理；只有在被背景推進機制依序推進為 `Admitted` 後才會開始計算入場逾時。系統 MUST 保證同一活動的 `Waiting` 紀錄之間的推進順序恆依 `JoinedAtUtc ASC, Id ASC` 由舊到新，不因等待過久而被跳過或重新排序。

#### Scenario: PQ-WAIT-001 長時間等待不會被自動清理
- **WHEN** 某筆 `Waiting` 排隊紀錄已等待相當長的時間，但活動仍持續有其他 `Admitted` 名額被 `Completed`／`Expired` 釋放
- **THEN** 系統依然依 `JoinedAtUtc ASC, Id ASC` 順序將其推進為 `Admitted`，不因等待時間過長而跳過或標記為 `Expired`

### Requirement: Admin 關閉熱門搶購模式後，既有排隊紀錄不主動清理
系統 SHALL 在 Admin 關閉活動的熱門搶購模式（`IsQueueModeEnabled = false`）後，停止對該活動的 `Waiting` 紀錄執行入場推進，但 MUST NOT 主動刪除或重置既有的 `PurchaseQueueEntry` 紀錄；`ticket-purchase` 能力的排隊資格檢查僅在活動 `IsQueueModeEnabled = true` 時執行，關閉後即不再檢查排隊資格。若之後重新開啟熱門搶購模式，系統 SHALL 依既有 `JoinedAtUtc ASC, Id ASC` 順序繼續推進尚未處理的 `Waiting` 紀錄，不重新排序或要求會員重新加入排隊。

#### Scenario: PQ-TOGGLE-001 關閉熱門搶購模式後既有 Waiting 紀錄停止推進
- **WHEN** Admin 將已有多筆 `Waiting` 排隊紀錄的活動關閉熱門搶購模式
- **THEN** 背景推進機制不再處理該活動，既有排隊紀錄維持原狀態不被刪除，該活動的建立訂單請求不再檢查排隊資格

#### Scenario: PQ-TOGGLE-002 重新開啟熱門搶購模式後沿用既有排隊順序
- **WHEN** Admin 將先前關閉、仍存有 `Waiting` 紀錄的活動重新開啟熱門搶購模式
- **THEN** 背景推進機制依既有 `JoinedAtUtc ASC, Id ASC` 順序繼續推進這些 `Waiting` 紀錄，不要求會員重新加入排隊、不重置加入時間

### Requirement: 入場名額上限、逾時時間與推進間隔須為正數設定
系統 SHALL 在啟動時驗證排隊機制的設定值（`MaxConcurrentAdmittedBuyers`、`AdmissionTtl`、`PollingInterval`）皆為正數；任一值設定為 0 或負數，會導致開啟熱門搶購模式的活動永遠無人能入場、或背景推進機制無法有效運作（買家全數卡在 `Waiting`，形同下單功能完全失效），MUST 在應用程式啟動時 fail-fast 阻止啟動，不允許以無效設定值靜默運作。

#### Scenario: PQ-CONFIG-001 設定值為正數時正常啟動
- **WHEN** `MaxConcurrentAdmittedBuyers`、`AdmissionTtl`、`PollingInterval` 皆設定為正數
- **THEN** 應用程式正常啟動

#### Scenario: PQ-CONFIG-002 設定值為 0 或負數時啟動失敗
- **WHEN** `MaxConcurrentAdmittedBuyers`、`AdmissionTtl` 或 `PollingInterval` 任一被設定為 0 或負數
- **THEN** 應用程式啟動時 MUST 失敗，不得以此設定值繼續運作

### Requirement: 建立訂單成功後標記排隊紀錄為已完成，名額即時釋放
系統 SHALL 在買家透過已入場（`Admitted` 且未逾時）的排隊資格成功建立訂單後，於建立訂單的同一次資料庫交易內，將對應的排隊紀錄標記為 `Completed`；依「排隊入場名額依先後順序推進」需求對有效名額的定義，該筆紀錄的狀態一離開 `Admitted`，即刻不再計入有效名額——名額在交易提交的當下即視為釋放，供下一輪背景推進使用，不需等待該紀錄原本的 `AdmissionExpiresAtUtc` 到期。「標記完成」的判斷（該會員當下是否仍持有有效的 `Admitted` 資格）MUST 在與座位/庫存鎖定相同的交易內重新確認，避免發生「檢查時資格有效、實際扣減時資格已被背景服務標記逾時」的競態。

#### Scenario: PQ-COMPLETE-001 成功建立訂單後標記排隊紀錄完成
- **WHEN** 已入場（`Admitted` 且未逾時）的會員成功建立訂單
- **THEN** 系統在同一交易內將該筆排隊紀錄標記為 `Completed`，並於交易提交後的下一輪背景推進即可將名額提供給下一位等待者，不需等待原本的入場逾時時間

#### Scenario: PQ-COMPLETE-002 名額於交易提交後立即可供下一位使用
- **WHEN** 某活動的有效入場名額已達上限，其中一筆 `Admitted` 紀錄因成功建立訂單而在交易內轉為 `Completed`
- **THEN** 該筆紀錄轉為 `Completed` 後，有效入場名額隨即減少一筆，下一輪背景推進得以將名額提供給最早的 `Waiting` 紀錄，不受該筆紀錄原訂的 `AdmissionExpiresAtUtc` 影響
