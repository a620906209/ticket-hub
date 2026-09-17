## MODIFIED Requirements

### Requirement: 排隊入場名額依先後順序推進，且有名額上限
系統 SHALL 以週期性背景處理，針對每個 `IsQueueModeEnabled = true` 的活動，計算目前有效入場名額（**定義為狀態恰為 `Admitted` 且未逾時的紀錄數**——一筆紀錄只要離開 `Admitted` 狀態（轉為 `Completed` 或 `Expired`），即刻不再計入有效入場名額，不論轉出的原因或時間點）與設定的上限值；有剩餘名額時，依 `JoinedAtUtc ASC`（由舊到新）依序將 `Waiting` 紀錄推進為 `Admitted` 並設定入場逾時時間，直到補滿上限或無更多 `Waiting` 紀錄。同一活動的名額計算與推進 MUST 具備原子性，不得因併發處理而超額入場。已入場但超過入場逾時時間仍未完成訂單的紀錄，系統 SHALL 標記為 `Expired`，該名額自狀態轉換的當下起不再計入有效名額，供下一輪推進使用。

**Tie-break 契約變更（因 `purchase-queue-redis-admission` 改動而修改，取代原先的 `Id ASC`）**：`JoinedAtUtc` 完全相同的多筆紀錄之間的推進順序，由原先的「`Id ASC`（Postgres `uuid` 型別比較規則）」改為「Redis `waiting` zset member 字串（`entryId` 的 `Guid` 標準字串表示）的 lexicographic 順序」——這是正式的契約修改，不是實作細節。此變更僅影響 `JoinedAtUtc` 精確到毫秒完全相同的紀錄彼此間的相對順序，MUST 仍保持排序結果穩定、可重現（同一組輸入每次得出相同順序），不影響與其他非同毫秒紀錄的相對順序，也不影響「不超額入場」「不遺漏」等 MUST 級保證。

#### Scenario: PQ-ADMIT-001 有剩餘名額時推進等待中的排隊
- **WHEN** 某活動目前有效入場名額未達上限，且存在 `Waiting` 狀態的排隊紀錄
- **THEN** 系統依 `JoinedAtUtc ASC` 排序最前的 `Waiting` 紀錄開始，依序推進為 `Admitted`，直到補滿上限或無更多等待紀錄

#### Scenario: PQ-ADMIT-002 名額已滿時不推進
- **WHEN** 某活動目前有效入場名額已達上限
- **THEN** 系統不將任何 `Waiting` 紀錄推進為 `Admitted`，維持其等待狀態

#### Scenario: PQ-ADMIT-003 入場逾時釋放名額
- **WHEN** 某筆 `Admitted` 排隊紀錄已超過入場逾時時間，且該筆紀錄對應的訂單未完成建立
- **THEN** 系統將該紀錄標記為 `Expired`，釋放的名額於下一輪推進提供給依 `JoinedAtUtc ASC` 排序最前的 `Waiting` 紀錄

#### Scenario: PQ-ADMIT-004 併發推進不超額入場
- **WHEN** 背景處理同時間針對同一活動計算名額與推進排隊
- **THEN** 系統 MUST 確保同一活動同時只有一個推進決策真正生效並寫入排隊紀錄，最終有效入場名額不超過設定上限。**機制說明（因 `purchase-queue-redis-admission` 改動而更新，取代原先依賴 Postgres `PurchaseQueueRepository.GetForAdmissionAsync` 批次悲觀鎖的論證；此處不涉及 `IEventRepository.GetForUpdateAsync`——後者用於 Queue Mode 切換的線性化，本次改動不變更其行為）**：入場推進的互斥與決策改由 Redis Lua Script 的原子執行提供——Redis 對單一 Script 的執行為單執行緒、序列化，即使多個背景服務實例重疊呼叫推進邏輯（見 `purchase-queue-leader-election` 能力，分散式鎖租約到期時可能發生重疊呼叫），Redis 仍保證同一時刻只有一個 Script 執行在修改同一活動的排隊資料，後一次執行必定讀到前一次執行後的最新狀態，不會重複推進、不會超額。推進決策確定後才批次寫回 Postgres 持久化，此寫回動作不需要（也不依賴）`PurchaseQueueRepository.GetForAdmissionAsync` 悲觀鎖，但 MUST 為**條件式寫回**（`WHERE Id = entryId AND Status = 期望的前置狀態`，以 `Status` 欄位本身作為版本判定依據），確保被延遲或重試的寫回不會覆寫已經流轉到更新狀態的紀錄。逾時清除與新推進兩份決策清單 MUST 分開批次寫回（逾時→`Expired`、推進→`Admitted`），任一清單寫回失敗 MUST NOT 混用另一清單的補償方式（見 `purchase-queue-leader-election` 能力對應 Requirement）

**Lua Script 對跨集合重複推進的下游防禦（本次事後審查修正，2026-09-18，見 `purchase-queue-leader-election` 能力 `PQLE-REBUILD-005` 的修正說明）**：一致性校正機制的 gap-fill 決策與其實際寫入之間存在時間差，理論上仍可能讓同一個 entryId 短暫同時存在於 `waiting` 與 `admitted` 兩個鏡像。入場推進 Lua Script 從 `waiting` 彈出候選 entryId、寫入 `admitted` 前，MUST 先確認該 entryId 是否已存在於 `admitted` 鏡像；已存在者 MUST 只視為清除其 `waiting` 端殘留，不得計入本輪新推進名單、不得重設其既有的入場逾時時間，並繼續從 `waiting` 補取下一位候選，直到補滿名額或無更多候選（見 `PQ-ADMIT-007`）。

**入場逾時時間的計算基準（本次事後審查修正，2026-09-18）**：Script 使用的「目前時間」與「設定入場逾時時間」的計算基準，MUST 是「實際呼叫入場推進 Lua Script 當下」取得的時間，不得沿用同一輪推進流程中、在此之前執行的一致性校正步驟開始前所取得的舊時間戳——校正本身可能耗時，若沿用舊時間戳計算入場逾時時間，會讓實際入場者拿到的有效入場視窗短於設計的入場逾時秒數。

#### Scenario: PQ-ADMIT-005 同毫秒加入的紀錄，推進順序具確定性與可重現性
- **WHEN** 兩筆以上排隊紀錄的 `JoinedAtUtc` 精確到毫秒完全相同，且都在 `Waiting` 狀態等待推進
- **THEN** 系統依這些紀錄的 `entryId` 字串 lexicographic 順序決定彼此的相對推進順序，同一組輸入的推進結果每次執行都相同（確定性、可重現），此順序不要求與 Postgres `Id ASC` 一致

#### Scenario: PQ-ADMIT-007 已入場的 entry 因鏡像暫時不一致被重複選中時，不重設其入場逾時（本次事後審查修正，2026-09-18）
- **WHEN** 入場推進 Lua Script 從 `waiting` 彈出某 entryId 作為候選，但該 entryId 因一致性校正機制的暫時性不一致（見 `purchase-queue-leader-election` 能力 `PQLE-REBUILD-005` 的修正說明）已經存在於 `admitted` 鏡像
- **THEN** 系統 MUST NOT 將此 entryId 計入本輪新推進名單、MUST NOT 重設其既有的入場逾時時間，只清除其在 `waiting` 鏡像的殘留成員，並繼續從 `waiting` 補取下一位候選，直到補滿名額或無更多候選

#### Scenario: PQ-ADMIT-006 持有推進決策的程序於寫回 Postgres 前中止或延遲，名額由校正機制安全補完
- **WHEN** 某筆 `Waiting` 紀錄已被 Redis 的原子操作決定推進為 `Admitted`，但執行該決策的程序在對應的 Postgres `UPDATE` 完成前中止（例如程序當機）或被長時間延遲，且超過放棄判定的寬限時間
- **THEN** 系統 MUST 由下一次執行的一致性校正機制（見 `purchase-queue-leader-election` 能力對應 Requirement）以條件式 `UPDATE`（`WHERE Id = entryId AND Status = 'Waiting'`）代為完成該筆紀錄的落地，不重新競爭推進資格、不因程序中止而永久遺漏這個名額；若原程序其實已經成功寫入（延遲而非真正中止），這次條件式 `UPDATE` MUST 為 0 列受影響的安全 no-op，MUST NOT 覆寫該紀錄後續已經流轉到的任何更新狀態（例如已被標記 `Completed` 或 `Expired`）

### Requirement: 建立訂單成功後標記排隊紀錄為已完成，名額即時釋放
系統 SHALL 在買家透過已入場（`Admitted` 且未逾時）的排隊資格成功建立訂單後，於建立訂單的同一次資料庫交易內，將對應的排隊紀錄標記為 `Completed`；依「排隊入場名額依先後順序推進」需求對有效名額的定義，該筆紀錄的狀態一離開 `Admitted`，即刻不再計入有效名額——名額在交易提交的當下即視為釋放，供下一輪背景推進使用，不需等待該紀錄原本的 `AdmissionExpiresAtUtc` 到期。「標記完成」的判斷（該會員當下是否仍持有有效的 `Admitted` 資格）MUST 在與座位/庫存鎖定相同的交易內重新確認，避免發生「檢查時資格有效、實際扣減時資格已被背景服務標記逾時」的競態。

**條件式收斂例外（因 `purchase-queue-redis-admission` 改動而新增，取代「立即」的無條件保證）**：Postgres 交易本身仍在 commit 當下就把紀錄標記為 `Completed`，這部分不變。但「名額隨即可供下一位使用」這件事，在入場推進機制改為依賴 Redis `admitted` 鏡像（見 `PQ-ADMIT-004` 機制說明）後，實際上依賴交易 commit 後對 Redis 鏡像的 best-effort 同步動作（見 `purchase-queue-leader-election` 能力 `PQLE-REBUILD-004`）。該同步動作正常情況下與交易 commit 幾乎同時完成，名額依然即時釋放；僅在該同步動作失敗（例如 Redis 當下不可用）時，名額釋放會延遲到 **Redis 恢復連線後、下一次成功取得鎖且處理到該活動的一致性校正**偵測並修復為止（精確邊界見 `purchase-queue-leader-election` 能力對應 Requirement 的說明：鎖競爭不構成額外延遲，因為持鎖實例的該輪校正涵蓋全部活動；但單輪內個別活動的處理失敗會被隔離，不影響其他活動——若 Redis 持續故障，此延遲會持續到 Redis 恢復為止，不是固定的輪詢次數）。**契約語意（本輪審查要求，明確選邊，見 `purchase-queue-leader-election` 能力 design.md Decision 6「契約語意的明確選擇」）**：本保證採 **eventual consistency** 語意，不是「有界修復」語意——系統 MUST 保證「只要 Redis 最終恢復連線、且存在至少一次成功處理到本活動的校正執行，該次執行 MUST 完成收斂」，且 MUST NOT 因本設計自身的邏輯（例如不必要的重試上限或退避）額外拖延收斂；但若 Redis 本身永久不可用，名額釋放確實會跟著永久延遲——這是外部依賴故障的直接後果，不是本設計能夠迴避、也不是本 Requirement 承諾要迴避的情況。

#### Scenario: PQ-COMPLETE-001 成功建立訂單後標記排隊紀錄完成
- **WHEN** 已入場（`Admitted` 且未逾時）的會員成功建立訂單
- **THEN** 系統在同一交易內將該筆排隊紀錄標記為 `Completed`；Redis 鏡像同步動作正常成功時，於交易提交後的下一輪背景推進即可將名額提供給下一位等待者，不需等待原本的入場逾時時間（同步失敗時的條件式收斂例外見 `PQ-COMPLETE-003`）

#### Scenario: PQ-COMPLETE-002 名額於交易提交後立即可供下一位使用
- **WHEN** 某活動的有效入場名額已達上限，其中一筆 `Admitted` 紀錄因成功建立訂單而在交易內轉為 `Completed`，且交易提交後對 Redis 鏡像的同步動作正常成功
- **THEN** 該筆紀錄轉為 `Completed` 後，有效入場名額隨即減少一筆，下一輪背景推進得以將名額提供給最早的 `Waiting` 紀錄，不受該筆紀錄原訂的 `AdmissionExpiresAtUtc` 影響

#### Scenario: PQ-COMPLETE-003 Redis 鏡像同步失敗時，名額釋放延遲至 Redis 恢復後的校正完成
- **WHEN** 某筆 `Admitted` 紀錄因成功建立訂單而在交易內轉為 `Completed`，但交易提交後對 Redis `admitted` 鏡像的同步動作失敗（例如 Redis 當下不可用）
- **THEN** 該筆紀錄在 Postgres 的狀態立即為 `Completed`（不受影響），但 Redis 端的有效名額計算短暫仍計入這筆紀錄，直到 **Redis 恢復連線後、下一次成功執行的一致性校正**（`purchase-queue-leader-election` 能力 `PQLE-REBUILD-004`，精確邊界見該能力「Redis 不可用時的降級行為」Requirement 的說明，涵蓋鎖競爭、持鎖實例中止、校正本身逾時三個因素——本 Scenario 與 `PQLE-REBUILD-004` 共用同一套邊界定義，不各自表述）偵測到該筆紀錄已為 `Completed` 並從 `admitted` 鏡像移除，名額才實際釋放；若 Redis 持續故障，或取得鎖的實例在處理到本活動前即中止、或該輪只處理了部分活動，此延遲會持續到「真正處理到本活動且該次 `ZREM` 實際執行成功」的校正為止，不是固定的輪詢次數。**契約語意（eventual consistency，非「有界修復」，見 design.md Decision 6「契約語意的明確選擇」）**：系統 MUST 保證「只要 Redis 最終恢復連線、且存在至少一次成功處理到本活動的校正執行，該次執行 MUST 完成收斂」，且 MUST NOT 因本設計自身邏輯額外拖延收斂；若 Redis 本身永久不可用，名額釋放確實會跟著永久延遲，這是外部依賴故障的直接後果，不是本 Scenario 承諾要迴避的情況。**重試與觀測性（見 design.md Decision 8 的重試與升級策略，本 Scenario 沿用同一套定義）**：系統不設重試次數上限、不需要人工介入即可自動收斂（每一輪校正本身即是一次自動重試）；同一筆紀錄連續多輪仍無法完成清除時，MUST 將對應 log 從 Warning 升級為 Error 以利可觀測性，但升級本身不影響、不中止重試

### Requirement: 買家可查詢自己的排隊狀態
系統 SHALL 提供已登入會員查詢自己在指定活動排隊狀態的端點 `GET /api/events/{id}/queue/entries/me`；活動 Id 不存在時回傳 `404 Not Found`。比照加入排隊端點，此端點只要求已登入、不限制角色，且只回傳呼叫者本人（依 JWT Claims 判斷）的排隊紀錄，不支援查詢或代入其他會員 Id（端點路徑 `/me` 即代表僅限本人）。查詢時，系統只在該會員對該活動狀態為 `Waiting`／`Admitted`／`Expired` 的紀錄中取加入時間最新的一筆作為代表；查無此範圍內的紀錄時（含從未加入，或僅有的歷史紀錄皆為 `Completed`）回傳「尚未加入排隊」狀態，即使該會員過去對此活動曾有 `Completed` 的歷史紀錄，也視為可重新加入排隊。狀態為 `Waiting` 時，回應 SHALL 包含目前排在自己之前的等待人數（依 `JoinedAtUtc ASC, Id ASC` 排序後，早於自己的 `Waiting` 紀錄數，此計算 **完全透過 Postgres 查詢完成，不經過 Redis**，見下方精確化說明）；狀態為 `Admitted` 且未逾時時，回應 SHALL 標示已可送出訂單；狀態為 `Expired` 時，回應 SHALL 標示入場名額已逾時。此端點為查詢操作，MUST 於查詢當下依 `AdmissionExpiresAtUtc` 與目前時間比對即時推導是否已逾時（比照既有訂單逾時「查詢時推導」的既定慣例），不得只依賴背景服務尚未執行完成的 `Expired` 標記——資料庫紀錄狀態仍為 `Admitted` 但已超過 `AdmissionExpiresAtUtc` 時，查詢回應 SHALL 視為已逾時，不落地寫回 `Expired`（落地寫回由背景服務或下一次加入排隊時的自我修復流程處理，維持單一寫入來源）。

回應 SHALL 額外附帶 `queueModeEnabled` 欄位，反映該活動當下的 `Event.IsQueueModeEnabled`，讓前端在每次輪詢排隊狀態時，能一併得知活動是否仍處於熱門搶購模式，不需另外呼叫活動列表 API 確認——買家在排隊等待畫面（`Waiting`）停留期間，若 Admin 將該活動的熱門搶購模式關閉，前端的下一次輪詢即可從 `queueModeEnabled = false` 得知，據以停止排隊流程、開放正常購票操作（見 `buyer-web-ui` 能力）；若已在 `Waiting` 或 `Admitted` 但 `IsQueueModeEnabled` 已被關閉，回應的排隊狀態欄位（`status`／`waitingCount` 等）SHALL 仍依實際紀錄內容如實回傳，由前端依 `queueModeEnabled` 決定是否據以停止排隊流程，後端本身不因 `IsQueueModeEnabled = false` 而改變這筆排隊紀錄的狀態或提前清理。

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
- **WHEN** 已登入會員查詢自己在某活動的排隊狀態，該活動的 `IsQueueModeEnabled` 於查詢當下為 `false`（例如 Admin 已在該會員排隊等待期間關閉熱門搶購模式）
- **THEN** 系統回應的 `queueModeEnabled` 欄位為 `false`，排隊紀錄本身的狀態（如仍為 `Waiting`）如實回傳、不因活動已關閉熱門搶購模式而被清理或竄改

### Requirement: 等待中的排隊紀錄沒有自身逾時機制
`Waiting` 狀態的排隊紀錄 SHALL NOT 因等待時間長短而自動失效或被清理；只有在被背景推進機制依序推進為 `Admitted` 後才會開始計算入場逾時。系統 MUST 保證同一活動的 `Waiting` 紀錄之間的推進順序恆依 `JoinedAtUtc ASC` 由舊到新（同毫秒 tie-break 見「排隊入場名額依先後順序推進」Requirement 的契約變更），不因等待過久而被跳過或重新排序。

#### Scenario: PQ-WAIT-001 長時間等待不會被自動清理
- **WHEN** 某筆 `Waiting` 排隊紀錄已等待相當長的時間，但活動仍持續有其他 `Admitted` 名額被 `Completed`／`Expired` 釋放
- **THEN** 系統依然依 `JoinedAtUtc ASC` 順序（含同毫秒 tie-break 規則）將其推進為 `Admitted`，不因等待時間過長而跳過或標記為 `Expired`

### Requirement: Admin 關閉熱門搶購模式後，既有排隊紀錄不主動清理
系統 SHALL 在 Admin 關閉活動的熱門搶購模式（`IsQueueModeEnabled = false`）後，停止對該活動的 `Waiting` 紀錄執行入場推進，但 MUST NOT 主動刪除或重置既有的 `PurchaseQueueEntry` 紀錄；`ticket-purchase` 能力的排隊資格檢查僅在活動 `IsQueueModeEnabled = true` 時執行，關閉後即不再檢查排隊資格。若之後重新開啟熱門搶購模式，系統 SHALL 依既有 `JoinedAtUtc ASC` 順序（含同毫秒 tie-break 規則）繼續推進尚未處理的 `Waiting` 紀錄，不重新排序或要求會員重新加入排隊。

#### Scenario: PQ-TOGGLE-001 關閉熱門搶購模式後既有 Waiting 紀錄停止推進
- **WHEN** Admin 將已有多筆 `Waiting` 排隊紀錄的活動關閉熱門搶購模式
- **THEN** 背景推進機制不再處理該活動，既有排隊紀錄維持原狀態不被刪除，該活動的建立訂單請求不再檢查排隊資格

#### Scenario: PQ-TOGGLE-002 重新開啟熱門搶購模式後沿用既有排隊順序
- **WHEN** Admin 將先前關閉、仍存有 `Waiting` 紀錄的活動重新開啟熱門搶購模式
- **THEN** 背景推進機制依既有 `JoinedAtUtc ASC` 順序（含同毫秒 tie-break 規則）繼續推進這些 `Waiting` 紀錄，不要求會員重新加入排隊、不重置加入時間
