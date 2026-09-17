## ADDED Requirements

### Requirement: 入場推進的 Redis 鏡像與 Postgres 真相一致性保證
系統 SHALL 確保入場推進使用的 Redis 鏡像資料（`waiting`／`admitted` Sorted Set）與 Postgres `PurchaseQueueEntry` 真相之間的暫時性不同步，不會造成超額入場、永久遺漏或資料損毀；不同步只造成短暫的推進延誤或名額釋放延誤，並透過校正機制自我修復。此保證涵蓋 `waiting` 鏡像（供推進來源排序）與 `admitted` 鏡像（供有效名額計算）兩者，`admitted` 鏡像的不同步來源包含入場推進本身的寫回失敗或執行者中止（含程序崩潰），以及 `purchase-queue` 能力「建立訂單成功後標記排隊紀錄為已完成」（`PQ-COMPLETE`）造成的 `Admitted → Completed` 轉出未同步反映。

校正機制 MUST 採逐筆定向操作（比對 Redis 鏡像成員與 Postgres 對應狀態的差集，只補齊缺漏、只清除不屬於目前合法狀態集合的殘留成員），MUST NOT 僅因數量不一致就整批清除重建。**`admitted` 鏡像的清除範圍 MUST 涵蓋所有「不屬於目前合法 Admitted 狀態」的殘留（`Completed`、`Expired`，以及 Postgres 查無對應紀錄的孤兒成員），不得僅限定 `Completed` 一種終態**（見 `PQLE-REBUILD-007`）；`waiting` 鏡像的清除範圍比照辦理，涵蓋已流轉至其他狀態、或查無對應紀錄的殘留。「Postgres 仍顯示 `Waiting`，但 entryId 已存在於 Redis `admitted` 鏡像」是入場推進正常執行路徑中「Redis 已完成推進決策、Postgres 尚未落地」的合法過渡態（見 `purchase-queue` 能力 `PQ-ADMIT-004`），校正機制 MUST 以「放棄判定標記」（時效性標記，隨推進決策一併原子寫入，見 `PQ-ADMIT-006` 機制）區分這是「仍在正常落地中的合法過渡態」還是「執行者已中止或延遲過久、需要代為完成的放棄決策」——標記存在時 MUST NOT 清除或打回 `waiting`；標記已過期時，MUST 以條件式 `UPDATE`（`WHERE Id = entryId AND Status = 'Waiting'`）代為完成該筆紀錄的 Postgres 落地（見 `PQ-ADMIT-006`），不重新競爭推進資格。「Postgres 顯示 `Admitted` 但已逾入場期限、entryId 已不在 Redis `admitted` 鏡像」同樣視為需要直接補完的殘留，同樣使用條件式 `UPDATE`（不需要時效性標記判斷，逾時本身是不隨時間變化的事實，見 `PQLE-REBUILD-003b`）。

補齊 `waiting` 鏡像缺漏時，MUST 排除任何已存在於 Redis `admitted` 鏡像的 entryId，即使 Postgres 當下仍顯示該 entryId 為 `Waiting`（Postgres 尚未追上 Redis 端已做出的推進決策）——MUST NOT 把這類 entryId 補回 `waiting`，否則會讓同一 entryId 同時存在於 `waiting` 與 `admitted` 兩個集合，破壞兩者互斥的前提，可能被後續推進誤判為全新的等待紀錄而重複推進（見 `PQLE-REBUILD-005`）。

**Postgres 寫回失敗或結果未知時 MUST NOT 立即反向補償 Redis 鏡像**：入場推進批次寫回 Postgres 遇到任何例外（連線失敗、逾時、或無法確認實際是否已提交），系統 MUST NOT 假設「例外＝一定未寫入」而立即對 Redis 鏡像做反向修改（例如把已推進的 entryId 打回 `waiting`），因為該次寫入實際上可能已經在資料庫端成功提交、只是回應未送達——若此時反向修改 Redis，會讓已經正確落地的狀態被錯誤地復原成未處理狀態。系統 MUST 只記錄 Warning 結構化 log，不做任何 Redis 端補償，收斂完全交給上述條件式校正機制（`PQLE-REBUILD-002a`／`003b`）——條件式 `UPDATE` 本身對重複執行是安全的（已生效則為 0 列受影響的 no-op），不需要區分「確定失敗」與「結果未知」。

**單一活動的處理失敗 MUST NOT 中止同一輪其餘活動的處理**：入場推進與校正的主迴圈對每個 `IsQueueModeEnabled = true` 的活動獨立處理，任一活動處理時遇到**業務例外**（連線問題、逾時等），MUST 只記錄 Warning 並跳過該活動，MUST NOT 中止迴圈、MUST NOT 影響同一輪其餘活動的推進與校正結果（見 `PQLE-REBUILD-006`）。**此隔離 MUST NOT 涵蓋 `OperationCanceledException`（本輪審查發現的缺口，補上明確 Scenario）**：`OperationCanceledException` 代表呼叫端主動取消或既有背景服務外層的取消語意，與「單一活動的業務例外」是不同語意，MUST 讓它正常向外傳遞，中止本輪剩餘活動的處理；已完成處理的前置活動的推進與校正結果 MUST 保留、不回滾（見 `PQLE-REBUILD-008`）。

#### Scenario: PQLE-REBUILD-001 應用程式啟動時執行 Redis 鏡像全量校正
- **WHEN** 應用程式啟動（含 Redis 先前無資料，或 Redis 剛重啟導致鏡像資料遺失的情況）
- **THEN** 系統對每個 `IsQueueModeEnabled = true` 的活動，將 Postgres 目前 `Status = Waiting` 與未逾時 `Admitted` 的紀錄補齊進 Redis 鏡像，此過程 MUST NOT 阻塞應用程式啟動流程

#### Scenario: PQLE-REBUILD-002 推進前校正，不誤判仍在寬限窗口內的合法過渡態
- **WHEN** 背景推進某活動前執行校正，Postgres 目前狀態顯示某 entryId 仍為 `Waiting`，但該 entryId 已存在於 Redis `admitted` 鏡像，且對應的放棄判定標記仍存在（代表另一實例的推進決策剛完成、寫回 Postgres 尚未落地，仍在正常執行的時間範圍內）
- **THEN** 系統 MUST NOT 將此 entryId 從 `admitted` 鏡像清除或打回 `waiting` 鏡像，維持 Redis 端已做出的推進決策不變，等待該筆紀錄的 Postgres 寫回自然完成

#### Scenario: PQLE-REBUILD-002a 放棄判定標記已過期時，校正機制以條件式 UPDATE 完成落地（對應 `PQ-ADMIT-006`）
- **WHEN** Postgres 目前狀態顯示某 entryId 仍為 `Waiting`，該 entryId 已存在於 Redis `admitted` 鏡像，但對應的放棄判定標記已過期（不存在），代表持有該推進決策的程序未能在寬限時間內完成 Postgres 落地（含程序崩潰或長時間延遲）
- **THEN** 系統 MUST 以條件式 `UPDATE`（`WHERE Id = entryId AND Status = 'Waiting'`）將該筆紀錄的 Postgres 狀態更新為 `Admitted`（`AdmissionExpiresAtUtc` 取 Redis `admitted` 鏡像該成員記錄的逾時時間），不重新競爭推進資格、不清除該筆紀錄的 Redis 鏡像；若該筆紀錄的 Postgres 狀態已不是 `Waiting`（例如原程序其實已經成功寫入，或已流轉到 `Completed`／`Expired`），此條件式 `UPDATE` MUST 為 0 列受影響的安全 no-op，MUST NOT 覆寫該紀錄實際所在的狀態

#### Scenario: PQLE-REBUILD-003 新推進批次的 Postgres 寫回失敗或結果未知時，不做反向補償
- **WHEN** 入場推進已決定推進一批排隊紀錄（`Waiting → Admitted`），但後續批次寫回 Postgres 遇到例外（連線失敗、逾時，或無法確認是否已實際提交）
- **THEN** 系統 MUST NOT 將這批紀錄的 Redis `admitted` 鏡像清除或打回 `waiting`、MUST NOT 清除對應的放棄判定標記，只記錄 Warning 結構化 log；後續收斂完全交由放棄判定標記過期後的 `PQLE-REBUILD-002a` 條件式補完，不因本次失敗而遺漏、也不因結果未知而錯誤復原已落地的狀態

#### Scenario: PQLE-REBUILD-003a 逾時標記批次的 Postgres 寫回失敗或結果未知時，不做反向補償
- **WHEN** 入場推進已決定將一批逾時的 `Admitted` 紀錄標記為 `Expired`（已從 Redis `admitted` 鏡像移除），但後續批次寫回 Postgres 遇到例外（連線失敗、逾時，或無法確認是否已實際提交）
- **THEN** 系統 MUST NOT 將這批紀錄補回 Redis `admitted` 或 `waiting` 鏡像，只記錄 Warning 結構化 log；後續收斂完全交由 `PQLE-REBUILD-003b` 的條件式補完，不因結果未知而錯誤復原已落地的狀態

#### Scenario: PQLE-REBUILD-003b 逾時標記批次的執行者中止或結果未知時，校正機制以條件式 UPDATE 完成落地
- **WHEN** 入場推進已將一批逾時的 `Admitted` 紀錄從 Redis `admitted` 鏡像移除，但執行者在對應的 Postgres `UPDATE` 完成前中止（例如程序當機），使得 Postgres 該筆紀錄仍顯示 `Admitted` 但入場逾時時間已過去，且該紀錄已不在 Redis `admitted` 鏡像
- **THEN** 系統 MUST 以條件式 `UPDATE`（`WHERE Id = entryId AND Status = 'Admitted'`）將該筆紀錄的 Postgres 狀態更新為 `Expired`，不需要時效性標記或寬限時間判斷（逾時本身是不隨時間變化、無競態疑慮的事實，任何時候偵測到都可安全重試）；若該筆紀錄已不是 `Admitted`（例如已被標記 `Completed`），此條件式 `UPDATE` MUST 為 0 列受影響的安全 no-op

#### Scenario: PQLE-REBUILD-005 校正快照期間 Lua Script 已推進的 entry，不被誤判為缺漏而補回 waiting
- **WHEN** 校正讀取快照時，某 entryId 在 Postgres 仍為 `Waiting`，但該次快照顯示 Redis 端已經完成推進（entryId 已不在 `waiting` 鏡像、已存在於 `admitted` 鏡像）——代表一個併發的 Lua Script 推進發生在快照讀取之前
- **THEN** 系統 MUST NOT 因為「Postgres 顯示 Waiting 且不在 waiting 鏡像」就把該 entryId 補回 `waiting` 鏡像；該 entryId MUST 只存在於 `admitted` 鏡像，不得同時出現在兩個鏡像中，避免後續推進誤判為全新等待紀錄而重複推進、或誤算剩餘名額

#### Scenario: PQLE-REBUILD-006 單一活動處理失敗不影響同一輪其餘活動
- **WHEN** 持有分散式鎖的實例在同一輪內依序處理多個 `IsQueueModeEnabled = true` 的活動，其中一個活動的推進或校正處理時遇到例外（例如該活動對應的 Postgres 查詢逾時）
- **THEN** 系統記錄該活動的 Warning 結構化 log 並跳過，MUST 繼續處理同一輪其餘活動，其餘活動的推進與校正結果不受影響、不因單一活動的例外而整輪中止

#### Scenario: PQLE-REBUILD-007 admitted 鏡像的終態殘留清除不限於 Completed（本輪審查發現的清除範圍缺口）
- **WHEN** 校正讀取快照時，某 entryId 存在於 Redis `admitted` 鏡像，但該 entryId 在 Postgres 的目前狀態為 `Expired`（例如先前由本能力的條件式 `UPDATE` 落地為 `Expired`，但當時對應的 Redis 移除未同步成功），或該 entryId 在 Postgres 查無任何對應紀錄（孤兒 member）
- **THEN** 系統 MUST 將該 entryId 從 `admitted` 鏡像清除，不得因為它不符合「Postgres 狀態為 `Completed`」這個單一條件就略過；此清除範圍 MUST NOT 誤及 Postgres 仍為 `Admitted`（不論是否已逾期）的 entryId——逾期但尚未落地為 `Expired` 的殘留由入場推進機制自身的逾時清除步驟自然收斂，不由本 Scenario 的校正步驟搶先處理

#### Scenario: PQLE-REBUILD-008 處理中途遭取消時，例外正常傳遞、後續活動不再處理、已完成結果保留（本輪審查發現的追溯缺口）
- **WHEN** 持有分散式鎖的實例在同一輪內依序處理多個 `IsQueueModeEnabled = true` 的活動，已完成部分活動的推進與校正後，本次呼叫收到外部發出的取消（`CancellationToken` 被觸發），尚未開始處理後續活動
- **THEN** 系統 MUST 讓 `OperationCanceledException` 正常向外傳遞（不得被單一活動的業務例外隔離邏輯攔截或吞掉），本輪剩餘、尚未處理到的活動 MUST NOT 被處理；已完成處理的前置活動，其推進與校正結果 MUST 保留（不回滾、不因取消而復原）

#### Scenario: PQLE-REBUILD-004 訂單完成同步失敗時，名額於 Redis 恢復後的校正完成後釋放
- **WHEN** 已入場會員成功建立訂單，`purchase-queue` 能力在同一 Postgres 交易內將排隊紀錄標記為 `Completed`，但交易 commit 後同步移除 Redis `admitted` 鏡像的動作失敗（例如 Redis 當下不可用）
- **THEN** 該筆紀錄短暫仍計入 Redis 端的有效名額，直到 **Redis 恢復連線後、下一次成功執行的校正**（精確邊界見上方「Redis 不可用時的降級行為」Requirement 的說明，涵蓋鎖競爭、持鎖實例中止、校正本身逾時三個因素）查得該 entryId 在 Postgres 已明確為 `Completed`（非過渡態）並將其從 `admitted` 鏡像清除，該筆紀錄才不再計入有效名額；若 Redis 持續故障，或取得鎖的實例在處理到本活動前即中止、或該輪只處理了部分活動，此延遲會持續到「真正處理到本活動且該次 `ZREM` 實際執行成功」的校正為止，不是固定的輪詢次數。**契約語意（eventual consistency，非「有界修復」，本輪審查要求明確選邊，見上方「Redis 不可用時的降級行為」Requirement 的「契約語意的明確選擇」說明）**：系統 MUST 保證「只要 Redis 最終恢復連線、且存在至少一次成功處理到本活動的校正執行，該次執行 MUST 完成收斂」，且 MUST NOT 因本設計自身邏輯額外拖延收斂；若 Redis 本身永久不可用，名額釋放確實會跟著永久延遲，這是外部依賴故障的直接後果，不是本 Scenario 或 `purchase-queue` 能力 `PQ-COMPLETE-003`（與本 Scenario 共用同一套邊界定義）承諾要迴避的情況。**重試次數與升級門檻**（design.md Decision 8 的重試與升級策略）：系統 MUST NOT 設重試次數上限、MUST NOT 因重試多輪未成功就放棄或改變處理方式；不需要人工介入即可讓機制本身持續收斂嘗試；連續多輪仍無法完成清除時，MUST 將對應 log 等級由 Warning 升級為 Error 以利可觀測性，此升級不影響、不中止重試本身

## MODIFIED Requirements

### Requirement: 背景推進服務的多實例互斥執行
系統 SHALL 在 `PurchaseQueueAdmissionService` 每一輪輪詢執行實際推進邏輯（掃描活動並推進入場名額，推進本身的原子性機制見 `purchase-queue` 能力 `PQ-ADMIT-004`）之前，先嘗試取得以 Redis 為後端的分散式鎖（單一固定 key，涵蓋整輪推進，不分活動）；取得成功時 MUST 執行本輪推進，執行完畢後 MUST 釋放該鎖；取得失敗（鎖已被其他實例持有）時 MUST 直接跳過本輪，不執行任何推進邏輯，等待下一輪輪詢再嘗試。分散式鎖的取得與釋放 MUST NOT 改變既有推進邏輯本身的入場順序、逾時判斷、或任何對外可觀察行為（見 `purchase-queue` 能力既有 Requirement）。

#### Scenario: PQLE-001 單一實例取得鎖並執行推進
- **WHEN** 只有一個實例在跑 `PurchaseQueueAdmissionService`，該輪輪詢嘗試取得分散式鎖
- **THEN** 系統成功取得鎖，執行本輪的活動掃描與入場推進，執行完畢後釋放鎖

#### Scenario: PQLE-002 多實例同時輪詢，僅一個實例執行本輪推進
- **WHEN** 兩個以上的實例幾乎同時進入同一輪輪詢週期，各自嘗試取得同一把分散式鎖
- **THEN** 系統保證只有一個實例成功取得鎖並執行本輪的活動掃描與入場推進，其餘實例本輪皆跳過、不執行推進邏輯

#### Scenario: PQLE-003 未取得鎖的實例不影響下一輪重新競爭
- **WHEN** 某實例在某一輪因鎖已被其他實例持有而跳過
- **THEN** 該實例於下一輪輪詢仍會重新嘗試取得鎖，不因上一輪失敗而永久停止嘗試

### Requirement: 分散式鎖的租約與逾時自動釋放
分散式鎖 SHALL 具備 TTL（租約時間）；持有鎖的實例正常完成本輪推進後 MUST 主動釋放鎖。若持有鎖的實例在租約到期前未能主動釋放（例如處理中途當掉或逾時），系統 SHALL 在 TTL 到期後自動視為鎖已釋放，允許其他實例於後續輪詢重新取得，不需要任何實例手動介入即可恢復正常推進。鎖的釋放 MUST 只由目前實際持有該鎖的實例完成——系統 MUST NOT 讓一個實例釋放另一個實例目前持有的鎖（例如自己的租約已到期後、其他實例已取得新鎖時，前者仍嘗試釋放）。

本機制 MUST NOT 實作鎖的自動續租（lease renewal）——這是刻意的設計選擇，非遺漏：若原持有鎖的實例因推進耗時超過 TTL 而尚未完成（未當掉、仍在執行中），TTL 到期後另一實例 MAY 取得新鎖並開始執行，形成兩個實例的推進邏輯同時執行的重疊窗口。系統 MUST NOT 阻止這種重疊發生（不做鎖延長、不拒絕重疊），重疊執行期間的正確性完全依賴 `purchase-queue` 能力入場推進機制本身的原子性（Redis Lua Script 的單執行緒序列化，見 `PQ-ADMIT-004`）保證不超額入場，本分散式鎖不重複提供、也不需要提供這層正確性保證，只負責在正常情況下減少多實例重複執行的效率浪費。

#### Scenario: PQLE-006a 原持有者仍在執行中、TTL 到期，另一實例取得鎖並重疊執行
- **WHEN** 實例 A 持有鎖並正在執行推進邏輯（尚未完成、未當掉），但其鎖的 TTL 已到期；實例 B 於此時嘗試取得同一把鎖
- **THEN** 系統允許實例 B 成功取得鎖並開始執行推進邏輯，即使實例 A 的前一輪推進邏輯仍在執行中；兩者呼叫的推進邏輯（各自對應一次 Redis Lua Script 執行）MUST 依 Redis 單執行緒特性依序執行、不並行修改同一活動的排隊資料，最終有效入場人數 MUST NOT 超過該活動設定的上限（`MaxConcurrentAdmittedBuyers`），不因本次改動而破壞既有 `purchase-queue` 能力「排隊入場名額依先後順序推進，且有名額上限」Requirement 的既定保證

#### Scenario: PQLE-004 正常完成後主動釋放鎖
- **WHEN** 某實例成功取得鎖並完成本輪推進邏輯
- **THEN** 系統在推進完成後主動釋放該鎖，供其他實例於下一輪競爭

#### Scenario: PQLE-005 持有鎖的實例未能主動釋放，TTL 到期後鎖自動可用
- **WHEN** 持有鎖的實例在租約到期前未執行釋放操作（模擬處理中途中斷）
- **THEN** 系統在該鎖的 TTL 到期後，允許其他實例於後續輪詢成功取得鎖並執行推進，不需人工介入

#### Scenario: PQLE-006 已逾時釋放的鎖不可被原持有者誤釋放新的持有者
- **WHEN** 實例 A 持有的鎖已因 TTL 到期而被系統視為釋放，實例 B 隨後取得同一個鎖並開始執行；此時實例 A 才執行（遲到的）釋放操作
- **THEN** 系統的釋放操作 MUST 判斷該鎖目前的持有者是否仍為實例 A，發現已不是時 MUST 拒絕釋放（無操作），不得誤刪實例 B 持有的鎖

### Requirement: Redis 不可用時的降級行為
系統 SHALL 在嘗試取得分散式鎖時明確區分「鎖已被其他實例持有」與「無法連線至 Redis（基礎設施故障）」兩種失敗情境。**行為變更（因 `purchase-queue-redis-admission` 改動而更新，取代原先的 fail-open 行為）**：當偵測到無法連線 Redis 時，系統 MUST 記錄可觀察的警告等級（Warning）結構化 log，且 MUST 直接跳過本輪推進，不執行任何 `Waiting → Admitted` 的變更，等待下一輪輪詢重新嘗試——這是刻意的 fail-closed，原因是入場推進的「不超額」保證現在完全依賴 Redis Lua Script 的原子執行（見 `purchase-queue` 能力 `PQ-ADMIT-004`），Redis 不可用時已無其他機制可保證正確性，MUST NOT 嘗試任何降級路徑繼續執行推進邏輯。此決策優先保護「不超賣」高於「排隊持續推進」的可用性。

此 fail-closed 原則不適用於**應用程式啟動階段**：Redis 尚未就緒或無法連線時，系統 MUST NOT 阻塞或中斷應用程式啟動流程；`IConnectionMultiplexer` 的建立 MUST 採非阻塞連線設定，讓應用程式正常完成啟動，背景服務啟動後於執行期再依上述 fail-closed 行為處理 Redis 連線狀態（即：啟動不受影響，但啟動後的每一輪推進仍會因 Redis 不可用而跳過）。

**「Redis 恢復連線後、下一次成功執行的校正」的精確邊界（本 Requirement 為 `PQ-COMPLETE-003`／`PQLE-REBUILD-004` 共同依賴的邊界定義來源，三處 MUST 使用同一套語意，不得各自表述）**：本能力／`purchase-queue` 能力凡承諾「Redis 恢復後、下一次成功校正會完成修復」之處，其邊界受以下三個因素影響，系統設計與對應測試 MUST 誠實反映，而非假設每次都恰好是「緊接著的下一輪」：
- **鎖競爭**：任一時刻只有取得分散式鎖的實例會執行該輪的推進與校正（見「背景推進服務的多實例互斥執行」Requirement）；沒取得鎖的實例本輪完全跳過。這不構成額外的保證缺口——因為取得鎖的那個實例，其該輪校正 MUST 涵蓋**全部** `IsQueueModeEnabled = true` 的活動，不因鎖由哪個實例取得而遺漏特定活動
- **單輪內個別活動處理失敗或持鎖實例中止，MUST NOT 讓其他活動或後續輪次的保證失效**：入場推進與校正的主迴圈 MUST 對每個活動的處理獨立包一層例外處理，單一活動處理時的例外 MUST 只記錄 Warning 並跳過該活動、不中止迴圈（見 `PQLE-REBUILD-006`）；若持鎖的實例在處理到目標活動**之前**就整個程序中止（例如崩潰，非例外可攔截的情況），本輪對該活動的校正视同未執行，該活動的殘留修復順延至下一次成功取得鎖、且該輪處理到這個活動的校正，不因這次未處理到而被視為「已檢查過、不需要再檢查」
- **校正本身於執行期間再度遇到 Redis 逾時**：與批次寫回 Postgres 一致的「結果未知不做反向補償」原則，校正內部個別操作（例如某筆 `ZREM`）若逾時，只記錄 Warning、跳過該筆，交由下一次校正嘗試補上；不影響同一輪其他 entryId 或其他活動的處理

綜合以上，精確的保證邊界是：**「Redis 恢復連線後，下一次成功取得鎖執行、且該輪確實處理到目標活動的校正（依上述逐活動隔離設計，只要該輪正常執行、沒有在到達這個活動前就整個程序中止，就一定會處理到），該活動的殘留就會被校正修復」**——比字面上的「下一輪」更精確，也更誠實地反映鎖競爭、持鎖實例中止與例外隔離下的實際行為；「下一次成功執行的校正」在觀測與測試上，一律以「該次校正確實對目標活動完成了對應的 Redis 讀寫操作（例如 `ZREM` 實際執行且回應成功）」為判準，不以「輪詢次數」或「時間經過」判斷。

**契約語意的明確選擇（本輪審查要求：「不設重試次數上限」與「不得無限期延遲」字面上互相矛盾——若 Redis 永久不可用，任何沒有重試上限的機制都無法迴避無限期延遲，需要明確選邊，不能兩者都宣稱）**：本能力與 `purchase-queue` 能力 `PQ-COMPLETE-003` 對「Redis 恢復後、下一次成功校正」的保證，一律採 **eventual consistency 語意**，不是「有界修復」語意（有界修復需要定義最大重試次數、退避、Error 升級後的終止或人工介入路徑，但這會與「條件式 `UPDATE`／`ZADD`／`ZREM` 永遠可安全重試」這個入場推進正確性保證的基礎互相衝突，見 `purchase-queue` 能力 `PQ-ADMIT-004`）。精確承諾為：系統 MUST 保證「只要 Redis 最終恢復連線、且存在至少一次成功處理到目標活動的校正執行，該次執行 MUST 完成收斂」，且 MUST NOT 因本設計自身邏輯（例如不必要的重試上限或降頻）額外拖延收斂；若 Redis 本身永久不可用，名額釋放確實會跟著永久延遲——這是 Redis 這項外部依賴本身故障的直接後果，不是本能力或 `PQ-COMPLETE-003` 承諾要迴避的情況。不需要人工介入才能讓機制本身持續收斂嘗試；Decision 8 的 log 等級升級（Warning 升為 Error）純粹是讓維運人員能觀察到「Redis 故障時間異常拉長」，排查 Redis 本身故障根因所需的人工介入是維運 Redis 這項外部服務的正常職責，不是收斂機制本身的責任。

#### Scenario: PQLE-007 Redis 無法連線或執行結果未知時跳過本輪推進，不重試
- **WHEN** 背景服務嘗試取得分散式鎖或執行入場推進 Lua Script 時，Redis 連線失敗（例如服務未啟動或網路中斷），或呼叫逾時導致無法確認 Lua Script 是否已在 Redis 端實際執行（結果未知）
- **THEN** 系統記錄 Warning 等級的結構化 log，並直接跳過本輪的活動掃描與入場推進，MUST NOT 在同一輪內重試呼叫 Lua Script（避免結果未知時的重試造成 Script 被重複執行、誤推進不該推進的紀錄），也不執行任何 `Waiting → Admitted` 或逾時標記的 Postgres 變更，背景服務本身不停止運作，等待下一輪輪詢重新嘗試；若 Script 其實已在 Redis 端執行成功，其結果由下一次校正（`PQLE-REBUILD-002a`／`003b`）自然收斂

#### Scenario: PQLE-008 Redis 故障期間排隊推進暫停，逾時後自動恢復
- **WHEN** Redis 持續無法連線，多個實例的每一輪輪詢皆因無法連線 Redis 而跳過本輪推進
- **THEN** 系統在 Redis 故障期間不執行任何入場推進（排隊中的 `Waiting` 紀錄維持原狀，不會超額入場，也不會被錯誤推進）；**已入場但已超過入場逾時時間的 `Admitted` 紀錄，同樣 MUST NOT 在此期間被標記為 `Expired`**（本輪審查發現的明確化：入場推進與逾時標記由同一個 Decision 4 Lua Script 呼叫驅動，Redis 無法連線時兩者一併跳過，不是只暫停推進而逾時標記照常執行）；直到 Redis 恢復連線後的下一輪輪詢，才由正常推進與校正流程一併處理這些已逾時的殘留

#### Scenario: PQLE-009 Redis 恢復連線後回復正常推進行為
- **WHEN** Redis 從無法連線恢復為可連線，背景服務下一輪輪詢再次嘗試取得鎖並執行推進
- **THEN** 系統恢復正常的分散式鎖互斥與入場推進行為，不需重新啟動應用程式或任何手動介入

#### Scenario: PQLE-010 應用程式啟動時 Redis 不可用，API 仍可正常啟動
- **WHEN** 應用程式啟動時，Redis 服務尚未就緒或無法連線
- **THEN** 系統 MUST 正常完成應用程式啟動流程（DI 容器建置與 `IConnectionMultiplexer` 連線建立不拋出例外中止啟動）。本情境僅涵蓋「啟動流程本身不被 Redis 阻塞」；背景服務實際執行輪詢時對 Redis 不可用的 fail-closed 降級行為，由本 Requirement 前段與 PQLE-007 定義並驗證，不在本情境重複驗證
