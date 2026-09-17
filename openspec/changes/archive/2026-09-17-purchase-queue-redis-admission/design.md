## Context

現有 `PurchaseQueueAdmissionService` 每輪輪詢，對每個 `IsQueueModeEnabled = true` 的活動，在單一 Postgres 交易內以 `PurchaseQueueRepository.GetForAdmissionAsync`（內部使用原生 SQL `FOR UPDATE`）鎖定該活動所有進行中排隊紀錄，計算有效 `Admitted` 數、推進 `Waiting → Admitted`、標記逾時紀錄為 `Expired`。`purchase-queue-leader-election` 在外層加了 Redis 分散式鎖（`SET NX PX` + Lua compare-and-delete），只負責減少多實例重複執行；即使鎖因 TTL 到期而重疊執行，正確性仍完全依賴 `GetForAdmissionAsync` 的資料庫列鎖（見既有 spec `PQLE-006a`）。同一段程式碼裡另有一個名稱相近但用途完全不同的 `IEventRepository.GetForUpdateAsync` 呼叫（鎖定 Event 以確認 Queue Mode 切換的線性化時點），本次改動不涉及、不變更此呼叫，後續 Decision 內提及「取代 `GetForAdmissionAsync`」一律專指前者。

本設計把「入場推進」的互斥/決策機制換成 Redis Lua Script 的原子操作，Postgres 仍是持久化真相來源。

## Goals / Non-Goals

**Goals：**
- 入場推進「不超額」的保證改由 Redis Lua Script 的原子性提供，取代 `GetForAdmissionAsync`
- 更新 `purchase-queue-leader-election` 的 `PQLE-006a` 正確性論證
- Redis 鏡像資料具備從 Postgres 重建的能力，Redis 重啟/資料遺失只造成一次性重建開銷與短暫延誤，不造成超額入場或永久遺漏

**Non-Goals：**
- 不改變 `PQ-JOIN`、`PQ-STATUS`、`PQ-COMPLETE` 的既有 Postgres 交易語意本身（見 proposal.md 排除範圍）——但 `PQ-COMPLETE` 的 Handler 需新增一個對 Redis 鏡像的 best-effort 同步動作（見 Decision 8），且 `PQ-COMPLETE-002`「名額立即釋放」的保證在 Redis 端衍生計算上新增一個條件式收斂例外，不是「正確性保證完全不受影響」，原因見下方修正說明
- 不追求解決現有效能瓶頸（`docs/project-scope.md` 第 5 節已排除此動機）
- 不做 Redis AOF 等級持久化——鏡像資料視為可重建的衍生資料，不是唯一真相
- **不處理 Redis 全域資源治理與正式容器部署**（第五輪審查提出，已與使用者確認排除範圍）：`maxmemory`／eviction policy／記憶體使用告警、production 等級的多階段 Dockerfile、Redis TLS／認證／網路隔離、Redis healthcheck／readiness 探測，皆不在本次範圍。理由：這些是**既有**、跨越所有 Redis 用途（cache-aside、captcha、leader-election 鎖）的基礎設施關注點，不是本次新增排隊機制才引入的風險——現有用途早已在同樣沒有 `maxmemory`、沒有 TLS、`depends_on: service_started` 不保證 ready 的環境下運作；本次改動不使風險等級變化。若要處理，應是一個獨立、宣告性的「Redis 生產就緒／資源治理」提案，涵蓋全部既有用途，而非只為這次的排隊機制局部處理，否則會造成不一致的治理水準。`waiting`／`admitted` 集合的大小上限，目前依賴 `docs/project-scope.md` 定義的既有量級（2000 座位／20 票種）與既有 captcha／rate-limiting 對加入排隊請求的間接節流，不在此次新增獨立的容量上限機制。**明確聲明（第六輪審查要求）**：本次改動只處理排隊入場推進機制本身的**並發正確性**，完成本次改動 MUST NOT 被解讀為「系統已具備 production deployment readiness」——生產就緒需要的資源治理、TLS、容器編排等，仍是待辦事項，不因本次改動而被視為已完成或已評估過風險等級

**設計修正說明（design-hardener 審查發現）**：初版設計誤以為「`PQ-COMPLETE` 完全不需要碰」，但入場推進的有效名額計算（Decision 4）改用 Redis `admitted` zset 的成員數，若訂單完成（`PQ-COMPLETE`）造成的 `Admitted → Completed` 轉出沒有同步反映到 Redis，會讓該筆已完成的紀錄持續被計入有效名額，直到原本的 `AdmissionExpiresAtUtc` 自然到期——直接違反既有 `PQ-COMPLETE-002`「名額於交易提交後立即可供下一位使用，不受原訂逾時時間影響」的 MUST 保證。修正方式見 Decision 8。

## Decisions

### Decision 1：Redis 資料結構
- `pq:admit:{eventId}:waiting`——Sorted Set，member = `entryId`（`Guid` 字串表示），score = `JoinedAtUtc` 的 Unix 毫秒（見 Decision 2）
- `pq:admit:{eventId}:admitted`——Sorted Set，member = `entryId`，score = `AdmissionExpiresAtUtc` 的 Unix 毫秒

不另存 Hash 存放完整欄位——推進邏輯只需要 `entryId`、加入順序、逾時時間，兩個 Sorted Set 的 member/score 已足夠表達；推進結果落地的完整欄位更新仍寫回 Postgres。

**Alternatives considered**：最初討論過連 `MemberId`、`Status` 都存進 Redis、完全取代 Postgres（對應先前討論的「全換式」方案），但牽涉 `PQ-COMPLETE` 跨儲存一致性、Redis 持久性要求大幅提升，決定縮小範圍只處理入場推進這一段。

### Decision 2：加入順序的 score 編碼（修正：`PurchaseQueueEntry.Id` 實際型別為 `Guid`，非自增數值）
**重要修正**：先前版本誤以為 `PurchaseQueueEntry.Id` 是 bigint 自增主鍵，經 spec-reviewer 核對原始碼（`src/ProjectC.Domain/PurchaseQueue/PurchaseQueueEntry.cs:5`、`JoinPurchaseQueueHandler.cs:100` 的 `Guid.NewGuid()`）確認為隨機產生的 `Guid`，與加入時間先後順序無單調對應關係，不能作為 Redis Sorted Set score（score 必須是 IEEE754 double）。以下為修正後的設計。

`JoinedAtUtc` 在 Postgres 的欄位設定未限制精度（`PurchaseQueueEntryConfiguration.cs:19` 僅 `IsRequired()`，Npgsql 預設對應 `timestamp` 微秒精度），但應用層 `DateTime.UtcNow` 的實際解析度依作業系統可能僅達毫秒等級，高併發加入時同毫秒仍可能有多筆紀錄，需要 tie-break（比照既有 `JoinedAtUtc ASC, Id ASC`）。

**最終決策**：score = `JoinedAtUtc` 的 Unix 毫秒（現在時間戳約 1.7×10^12，遠低於 double 53 bit 精度上限 9×10^15，無精度疑慮）。score 相同（同毫秒）時，**Redis Sorted Set 原生規則**是依 member 字串做 lexicographic 排序——member 使用 `entryId` 的 `Guid` 字串表示，因此同毫秒的 tie-break 順序取決於 Guid 字串的字典序，**與 Postgres 既有 `Id ASC`（Postgres `uuid` 型別的內部位元組比較規則）的 tie-break 順序不保證一致**。

此差異僅在「兩筆紀錄的 `JoinedAtUtc` 精確到毫秒完全相同」時才會發生，且只影響這極少數同毫秒紀錄彼此之間的相對推進順序（不影響「是否超額入場」「是否被錯誤跳過」等 MUST 級保證，也不影響與其他非同毫秒紀錄的相對順序）。

**契約決定（第四輪審查要求二選一後的最終決定）**：不採「證明兩者順序等價」這條路——雖然 RFC 4122 canonical 字串表示法與逐位元組比較在理論上可能等價，但 Postgres `uuid` 的實際比較規則、Npgsql 對 `Guid` ↔ `uuid` 的位元組轉換是否完全對齊字串顯示順序，屬於需要在目標 Postgres 版本上實際驗證的環境事實，在設計文件裡用論證取代驗證存在誤判風險。改採**正式修改既有排序契約**：`purchase-queue` 能力「排隊入場名額依先後順序推進」Requirement 的 tie-break 規則，在 `JoinedAtUtc` 完全相同時，由「`Id ASC`（Postgres `uuid` 比較）」改為「Redis `waiting` zset 的 member 字串（`entryId` 的 `Guid` 標準字串表示）lexicographic 順序」——**精確地說，只有 `waiting` zset 的排序決定推進先後**（`ZPOPMIN` 取出的順序），`admitted` zset 的 score 是 `AdmissionExpiresAtUtc`（逾時排序，供 Step 1 的逾時清除用），與加入排隊的先後順序無關，第五輪審查前的用詞誤將兩者並列，已修正——並新增自動化測試以同毫秒批次驗證實際行為具備確定性、可重現（見 spec delta 與 tasks.md）。

**Alternatives considered**：
1. 用 Redis `INCR` 產生獨立遞增序號當 score——被排除，因為序號的產生順序取決於「呼叫 `ZADD` 的時間」而非「Postgres 交易 commit 的時間」，兩者在併發下可能不一致，會讓 Redis 端順序與 Postgres 端（`PQ-STATUS` 查詢仍依據的 `JoinedAtUtc`／`Id`）系統性偏離，而非僅限同毫秒的極端情況
2. 把 `JoinedAtUtc` 與某種數值化的 tie-break 編碼進單一 score（如時間戳乘以倍數再加序號）——被排除，如前版本已發現會逼近/超過 double 精度邊界

### Decision 3：加入排隊時同步寫入 Redis 鏡像
`PQ-JOIN` 的 Postgres 交易 commit 後，額外執行 `ZADD pq:admit:{eventId}:waiting {joinedAtUtc的Unix毫秒} {entryId}`（best-effort，非同一交易）。此操作失敗不影響加入排隊本身的成功與否（`PQ-JOIN` 既有行為不變），只會造成該筆紀錄暫時未進入 Redis 鏡像，由 Decision 5 的校正機制補上。

失敗模式僅造成「推進延誤」，不造成「超額入場」或「永久遺漏」，比照 `purchase-queue-leader-election` 既有的 fail-open 慣例。

### Decision 4：入場推進 Lua Script
每輪推進，對每個 `IsQueueModeEnabled = true` 的活動執行單一 Lua Script（原子操作）：
1. `ZRANGEBYSCORE pq:admit:{eventId}:admitted -inf {now毫秒}` 找出已逾時的 `Admitted` entryId（`expiredIds`），`ZREM` 從 `admitted` 移除
2. `ZCARD pq:admit:{eventId}:admitted` 取得目前有效 `Admitted` 數
3. 若有剩餘名額，`ZPOPMIN pq:admit:{eventId}:waiting {剩餘名額}` 取出最早的 N 筆 entryId（`promotedIds`），`ZADD` 寫入 `admitted` zset（score = 新逾時時間），並對每個 entryId 執行 `SET pq:admit:pending:{entryId} 1 EX {TTL 秒數}`（見 Decision 9，標記「推進決策已做出、等待 Postgres 落地」，預設 30 秒——遠大於正常批次 `UPDATE` 的預期耗時，作為異常判定的寬限窗口；TTL 為正式契約值，非暫定數字，見 Decision 9 的驗收程序）
4. Script 回傳：`expiredIds`（含原本的到期時間，供失敗補償用）、`promotedIds`（含新的入場逾時時間）、`dedupedIds`（見下方 `ZPOPMIN` 跨集合防禦修正，僅供 log 使用）

Script 的 `eventId`、名額上限等參數一律透過 `EVAL` 的 `KEYS`/`ARGV` 傳遞，MUST NOT 以字串拼接組出 Script 文字（即使目前所有輸入皆為系統產生的 `Guid`／設定值，非使用者直接輸入，仍依此原則實作，避免未來維護者誤用字串拼接方式改寫）。

**`EVAL` 呼叫本身結果未知時的處理（第六輪審查提出）**：若呼叫 Redis 執行本 Lua Script 時發生逾時或連線中斷，App 端無法確定 Script 究竟有沒有在 Redis 端真正執行——MUST NOT 在同一輪內重試呼叫 `EVAL`（重試可能造成 Script 被實際執行兩次，`ZPOPMIN` 不具冪等性，會誤推進第二批不該推進的紀錄）。此情境比照 Decision 6 的 fail-closed 原則處理：記錄 Warning 結構化 log，本輪不執行任何 Postgres `UPDATE`（因為 App 根本不知道 `expiredIds`／`promotedIds` 是什麼），等待下一輪輪詢。若 Script 其實已經在 Redis 端執行成功（只是回應遺失），Redis 端資料已經正確反映推進結果（含 `pending` 標記已寫入）；**精確的收斂時間點（本輪審查修正：避免與「下一輪即收斂」混淆）**：這筆紀錄此時處於 Decision 9 定義的合法過渡態（Postgres 仍為 `Waiting`、Redis 已在 `admitted`、`pending` 標記存活中），Decision 5 步驟 7 的校正機制在 `pending` 標記**存活期間** MUST NOT 清除或代為完成落地（視為仍在正常執行中）；只有在 `pending` 標記 TTL 到期（預設 30 秒，見 Decision 9）之後，**下一次成功執行的校正**才會以條件式 `UPDATE` 代為完成該筆紀錄的 Postgres 落地。也就是說，即使輪詢間隔（`PollingIntervalSeconds`）遠小於 30 秒，這筆紀錄的收斂仍會被 `pending` TTL 保守地延後至少到 TTL 到期為止，不是「下一輪校正就會收斂」；這是 Decision 9 刻意的設計（避免校正機制與仍在正常執行中的請求打架），不是額外的延遲缺陷，不需要 App 知道當輪 Script 實際執行與否。

Redis 對單一 Lua Script 的執行是單執行緒、原子的——即使分散式鎖因 TTL 到期讓兩個實例重疊呼叫，兩次 Script 呼叫仍會依序執行、不會並行修改同一組 key，第二次執行時 zset 已反映第一次的結果，不會重複推進、不會超額。

**`ZPOPMIN` 對「跨集合已存在」member 的防禦（strict-reviewer 事後審查發現，修正下方第 84 行原本「不會被重複決策」的過強敘述）**：上述原子性只保證同一次 `EVAL` 內部不會重複處理，**不涵蓋**「`waiting` 中被彈出的 entryId，剛好已經存在於 `admitted`」這個跨集合情境。這個情境確實會發生：Decision 5 步驟 3 的 gap-fill 是「依快照做決策、之後才執行 `ZADD` 寫入」，決策與寫入之間若插入一次完整的並發推進（另一實例的 Decision 4 完整流程），可能讓同一個 entryId 短暫真的同時存在於 `waiting` 與 `admitted`（Decision 5 已承認、接受此暫態，交由下一輪校正步驟 6 收斂，見該節說明）。若 Script 對此毫無防禦，`ZPOPMIN` 會把這個已入場的 entryId 當成新候選、以新的到期時間重新 `ZADD`——等同非預期延長其 `AdmissionExpiresAtUtc`，違反 TTL 固定窗口語意，不只是「短暫不同步、下一輪即收斂」的無害延遲。**修正**：popped 的 member 寫入 `admitted` 前，Script 內先以 `ZSCORE KEYS[2] member` 檢查是否已存在；已存在者只視為「清除其 `waiting` 端殘留」（不重設 score、不計入 `promotedIds`、不佔用本輪名額），並繼續從 `waiting` 補取下一位，直到填滿可用名額或 `waiting` 耗盡（回傳新增第三份清單 `dedupedIds`，僅供可觀測性 log 使用，不驅動任何分支邏輯，理由同上方「批次執行與逐筆結果的落差」一節）。

Script 執行完後，App 在 Postgres 交易內依回傳的兩份清單分別 `UPDATE`：`expiredIds` → `Status = Expired`；`promotedIds` → `Status = Admitted`／`AdmissionExpiresAtUtc` = Script 回傳的新逾時時間——此 `UPDATE` **不需要 `GetForAdmissionAsync`**，因為互斥/決策已在 Redis Script 內原子完成，不存在「兩個交易同時決定推進誰」的競態。

**重大修正（第五輪審查發現：所有 `UPDATE` MUST 為條件式，不可無條件覆寫）**：以下兩個問題經核對後確認為同一個根因——目前設計的每一處 Postgres `UPDATE`（本節的正常路徑、Decision 5 步驟 7/8 的放棄決策補完）都是**無條件**寫入（`SET Status = X`），沒有檢查寫入當下的前提狀態是否仍然成立：

1. **延遲恢復覆寫新狀態**：若持有推進決策的程序不是當機、而是被長時間延遲（例如 GC 停頓、網路壅塞）——TTL 到期、校正機制已代為完成該筆 Postgres 落地，之後這個 entry 甚至可能已經經歷後續合法的狀態轉換（例如被 `PQ-COMPLETE` 標記為 `Completed`，或被下一輪逾時掃描標記為 `Expired`）——被延遲的程序此時才恢復執行，用它手上的舊決策做無條件 `UPDATE`，會把已經正確流轉到新狀態的紀錄**覆寫回舊狀態**
2. **`UPDATE` 結果未知時補償方向錯誤**：目前「同步例外觸發反向補償」的設計（`promotedIds` 補回 `waiting`、`expiredIds` 補回 `admitted`）假設「例外＝一定沒有寫入」，但連線逾時／中斷可能發生在「SQL 已在資料庫端提交、只是回應遺失」的情況——此時反向補償會把已經落地的正確狀態，錯誤地在 Redis 端復原成尚未處理的狀態

**最終決策**：兩個問題共用同一個修正——所有 Postgres `UPDATE`（本節與 Decision 5）一律改為**條件式 `UPDATE`**（compare-and-set，以 `Status` 欄位本身當版本錨點，因為 `PurchaseQueueEntry` 的狀態機是單向流動的 `Waiting → Admitted → Completed`／`Expired`，同一筆紀錄不會逆向流動，`Status` 天然就是足夠的版本判定依據）：
- 推進：`UPDATE ... SET Status = 'Admitted', AdmissionExpiresAtUtc = X WHERE Id = entryId AND Status = 'Waiting'`
- 逾時標記：`UPDATE ... SET Status = 'Expired' WHERE Id = entryId AND Status = 'Admitted'`
- 影響列數為 0（前提狀態已不成立）視為**正常情況**，不是錯誤——代表這筆紀錄已經被另一條合法路徑處理過（校正機制搶先完成、或已流轉到更新的狀態），MUST NOT 觸發任何補償或重試，直接跳過

**批次執行與逐筆結果的落差（本輪審查發現）**：`promotedIds`／`expiredIds` 的條件式 `UPDATE` 在實作上是**單一批次**呼叫（`WHERE Id IN (...)`），EF Core 的 `ExecuteUpdateAsync` 對這種批次呼叫只回傳**整批的總影響列數**，MUST NOT 假設能從單一總數反推「批次內個別 entryId 是否生效」。這不影響正確性——批次送出 N 筆、只影響 M（M ≤ N）列，代表其餘 N-M 筆的前置狀態（`Status = 'Waiting'` 或 `Status = 'Admitted'`）已不成立，即已被另一條合法路徑（校正機制、或其他併發推進）搶先處理，不需要也不應該再對這幾筆做任何補償——收斂完全交由下一輪校正機制基於 Postgres／Redis 當下狀態重新判斷，不依賴本次批次呼叫知道「究竟是哪幾筆」。此總影響列數僅作為**可觀測性 log** 的參考資訊（例如記錄「本輪嘗試推進 N 筆、實際生效 M 筆」），不驅動任何分支邏輯。**Alternatives considered**：改為逐筆個別 `ExecuteUpdateAsync` 呼叫以取得逐筆結果——被排除，因為會讓每輪推進從 1 次批次 SQL 退化為 N 次個別 SQL 往返，而「知道究竟是哪幾筆生效」對本設計的正確性沒有必要（不論是哪幾筆未生效，處理方式都相同：交給校正機制），不符合 CLAUDE.md Simplicity First 原則。

有了條件式 `UPDATE` 之後，**不再需要區分「確定失敗」與「結果未知」**，也**不再需要立即的反向 Redis 補償**：
- `promotedIds`／`expiredIds` 批次 `UPDATE` 遇到任何例外（不論連線失敗、逾時、或結果未知），MUST NOT 做任何反向 Redis 寫入，只記錄 Warning 結構化 log
- `promotedIds` 的收斂完全交給 Decision 9 的 `pending` 標記機制：TTL 到期後，Decision 5 步驟 7 用**同一套條件式 `UPDATE`**代為完成——不論原本的 `UPDATE` 到底有沒有生效，條件式重試都是安全的（已生效則這次是 0 列受影響的 no-op，未生效則這次補上）
- `expiredIds` 的收斂完全交給 Decision 5 步驟 8（`A_pg_overdue` 中已不在 Redis `admitted` 的殘留），同樣改用條件式 `UPDATE`，原因同上，不論原始 `UPDATE` 結果未知與否都安全

這個設計不需要引入 `decisionId`／版本號機制——`ZPOPMIN` 的原子性保證同一次 `EVAL` 內不會重複處理同一 entryId（上方 `ZSCORE` 跨集合檢查修正的是另一個情境：不同次 `EVAL` 之間、經由 `waiting` 鏡像 gap-fill 造成的跨集合重複，見上方修正說明），而 `Status` 欄位本身的單向流動特性，配合條件式 `UPDATE`，已完整提供「compare-and-set」需要的版本判定能力，不需要額外的版本欄位。

**Alternatives considered**：引入獨立的 `decisionId`／版本號，與 Redis `admitted` score／`pending` 狀態關聯——被排除，因為 `Status` 欄位本身已經是天然、單向流動、足夠的版本錨點，額外的版本號機制解決的問題在此設計下不存在，屬過度設計，不符合 CLAUDE.md 的 Simplicity First 原則

### Decision 5：Redis 鏡像校正機制（第四輪審查後再修正：新增「放棄的決策」偵測與直接補完）
**修正歷程**：第三輪審查發現「數量不一致就整個 `DEL` 重建」會誤傷 `PQLE-006a` 允許的合法重疊執行過渡態，已改為逐筆定向操作。第四輪審查進一步發現：原本「Postgres 仍顯示 `Waiting`、entryId 已在 Redis `admitted` zset 一律視為合法過渡態、不清除」的規則有漏洞——若持有該推進決策的程序在 Postgres `UPDATE` 完成前當機（非例外、無法被 catch），這筆紀錄會永久停留在這個「過渡態」，Redis 端持續計入有效名額，但 Postgres 永遠不會變成 `Admitted`，形同永久洩漏一個名額。以下為完整修正後的校正邏輯。

**最終決策**：校正機制對每個活動執行以下步驟，全部為逐筆定向操作，不做整體 `DEL` 重建：
1. 查詢 Postgres 該活動目前 `Status = Waiting` 的 entryId 集合（`W_pg`）、`Status = Admitted` 且未逾時的集合（`A_pg`）、`Status = Admitted` 但已逾時仍未標記的集合（`A_pg_overdue`，即應轉 `Expired` 但尚未落地的殘留）——**`A_pg` 與 `A_pg_overdue` MUST 由同一次查詢（同一個 `AdmissionExpiresAtUtc` 比較基準時間）切分，兩者依定義互斥**（`A_pg` 明確排除已逾時的紀錄），不存在任何 entryId 同時屬於兩者的情況；後續步驟 3～8 全部基於這次查詢取得的單一快照，MUST NOT 在同一次校正執行期間對 Postgres 重新查詢
2. 讀取 Redis `waiting` zset 成員（`W_redis`）與 `admitted` zset 成員（`A_redis`）——**MUST 透過單一 Lua Script（`EVAL`）在 Redis 端原子地一次讀取兩個 zset 的全部成員並一併回傳**（腳本內以 `redis.call('zrange', KEYS[1], 0, -1)` 依序讀取兩個 key），MUST NOT 以兩個獨立的 `ZRANGE`／`ZRANGEBYSCORE` 呼叫分兩次讀取，即使包在同一個 `MULTI`/`EXEC` 交易內也不夠（見下方第十輪審查發現的例外說明，`MULTI`/`EXEC` 排入佇列與其他做法在語意上容易與「單一原子操作」混淆，直接採用與 Decision 4 一致的 Lua Script 模式最簡單也最不易被誤用）：Redis 對單一 Script 的執行是單執行緒、不可分割的（同 Decision 4），這是唯一能保證「`W_redis`／`A_redis` 取自同一個時間點、其間不會有並發的入場推進 Lua Script（Decision 4）插入執行並改變兩者關係」的方式——若改用兩個分開的指令依序呼叫，Redis 在兩次呼叫之間可能插入執行一次完整的入場推進 Script，讓兩次讀取取得的 `W_redis`／`A_redis` 分屬不同時間點的狀態，破壞步驟 3 排除條件賴以成立的前提（見下方說明）；後續步驟不重新讀取。**部署前提**：`docker-compose.yml:39-43` 的 Redis 為單機部署（`redis:7-alpine`，非 Cluster 模式），不存在 key slot 分散於不同節點導致單一 Lua Script 無法涵蓋 `waiting`／`admitted` 兩個 key 的疑慮；若未來改為 Redis Cluster 部署，需重新檢視本設計（`pq:admit:{eventId}:waiting`／`pq:admit:{eventId}:admitted` 可透過 hash tag `{eventId}` 確保同一活動的兩個 key 落在同一個 slot，屬於獨立的後續評估項目，不在本次範圍內）

**校正與並發 Lua Script 執行的關係（第六輪審查要求，不需要嚴格線性化）**：校正執行期間，若有併發的 Lua Script 推進活動（見 `PQLE-006a` 允許的重疊執行）改變了 Redis 或 Postgres 的實際狀態，校正本次仍會依步驟 1／2 取得的快照完成判斷與寫入——這是**刻意**的設計，不強制校正與 Lua Script 互斥或線性化：因為校正的每一步寫入（`ZADD`／`ZREM`／條件式 `UPDATE`）本身都是冪等操作，即使基於的快照在執行過程中已經過時，寫入結果最差只是「這次校正沒能處理到最新的變化」，不會造成資料錯誤或超額，而下一輪校正（每輪推進前都會執行，見「觸發時機」）會用新的快照重新偵測並收斂；比起實作嚴格的互斥/線性化保證（需要額外的鎖與更高的複雜度），這是符合 CLAUDE.md Simplicity First 原則、且已經足夠正確的做法。

**第十輪審查發現的例外（「冪等性足夠」這個論證有一個真正的反例，需要額外處理）**：上述「冪等寫入最差只是延誤」的論證，對「補齊 waiting」（步驟 3 原版）這一步**不成立**。反例：校正在步驟 1／2 讀取快照的當下，entryId `X` 在 Postgres 仍為 `Waiting`（尚未讀到 Lua 的推進結果），但恰好一個併發的 Lua Script 執行**已經**把 `X` 從 `waiting` 移到 `admitted`（`ZPOPMIN` 已完成，`X` 此刻已不在 Redis `waiting`，已經在 `admitted`）——此時若步驟 3 只看「`W_pg` 有、`W_redis` 沒有」就把 `X` `ZADD` 回 `waiting`，會讓 `X` 同時存在於 `waiting` 與 `admitted` 兩個集合。這**不是**單純的「延誤」，而是破壞了「`waiting`／`admitted` 互斥」這個其他步驟（尤其 Lua Script 的 `ZPOPMIN`）預設成立的前提，下一輪 Lua Script 可能把已經在 `admitted` 的 `X` 當成一筆全新的 `Waiting` 紀錄再次 `ZPOPMIN` 選中，寫入新的入場逾時時間，等同不當延長／重置 `X` 的資格窗口，且會讓當輪「剩餘名額」的計算把 `X` 誤算一次可用容量。**修正**：步驟 3 的「補齊 waiting」新增「且不在 `A_redis`」的排除條件（見上方步驟 3 內文）——由於步驟 1／2 的兩個 Redis 讀取（`W_redis`、`A_redis`）**依上方步驟 2 的修正、以單一 Lua Script 原子讀取取得**，兩者保證是同一次讀取取得的一致快照，這個排除條件完整堵住此反例：Lua 的移動若發生在快照讀取「之前」，`X` 會出現在 `A_redis`，被步驟 3 排除；若發生在快照讀取「之後」，則快照當下 `X` 仍在 `W_redis`，步驟 3 本來就不會判定為缺漏，不需要排除。**關鍵前提**：這個「非此即彼」的論證只有在 `W_redis`／`A_redis` 保證取自同一時間點時才成立——若兩者是分開呼叫、依序讀取（例如先 `ZRANGE waiting` 再 `ZRANGE admitted`），Lua Script 有可能恰好插入執行在兩次讀取之間，讓 `X` 在讀 `W_redis` 時仍在 `waiting`（尚未被 `ZPOPMIN`），但讀 `A_redis` 時卻還沒被 Lua 寫入（時序上矛盾，不會發生）；然而如果 Lua 的移動發生在「讀 `A_redis`（此時 `X` 尚未被推進，不在 `A_redis`）之後、讀 `W_redis`（此時 `X` 已被 `ZPOPMIN` 移除，也不在 `W_redis`）之前」，則 `X` 會同時滿足「不在 `W_redis`」與「不在 `A_redis`」兩個條件，被步驟 3 誤判為缺漏而補回 `waiting`——這正是分開讀取時會重現的競態，也是本設計 MUST 採用單一 Lua Script 原子讀取（而非兩個分開呼叫）的根本原因，不是風格偏好。
**可測試性要求（本輪審查要求：步驟 2 的原子快照讀取必須能被獨立驗證，不能只靠跑完整輪推進的整合測試間接推論）**：步驟 2 的原子快照讀取 MUST 實作為一個獨立、可單獨呼叫的方法（例如 `PurchaseQueueAdmissionService` 上一個 **`public`** 方法 `ReadRedisMirrorSnapshotAsync(Guid eventId, IDatabase database, CancellationToken ct)`，簽章只依賴 `IDatabase`，不依賴 `PurchaseQueueAdmissionService` 其餘欄位或 Postgres），只做「呼叫單一 `EVAL` 原子讀取 `waiting`／`admitted` 兩個 zset 並回傳」這一件事，不摻雜任何差集比對或寫入邏輯（差集比對／`ZADD`／`ZREM`／條件式 `UPDATE` 留在呼叫端）。**方法可見度決定（比照既有慣例，見 `archive/2026-08-17-ticketing-order-management/design.md`「可測試性」一節對 `CleanupOnceAsync` 的既有決定）**：本專案目前**沒有**任何專案設定 `InternalsVisibleTo`，既有慣例是「需要跨組件測試呼叫的方法直接宣告為 `public`」而非新增 `InternalsVisibleTo`——本次遵循同一慣例，MUST NOT 引入 `InternalsVisibleTo` 這個目前程式碼庫完全沒用過的新模式；`ReadRedisMirrorSnapshotAsync` 本身就是「讀取 Redis 鏡像快照」這個有意義的操作單元，公開它不算洩漏奇怪的實作細節。測試（tasks.md 9.10a）直接呼叫這個 `public` 方法，搭配 `Mock<IDatabase>` 斷言呼叫行為，不需要處理 Decision 4 推進 Lua Script 或分散式鎖等無關的 mock 設定。

3. **補齊 waiting**：`W_pg` 中不在 `W_redis`、**且不在 `A_redis`** 的 entryId → `ZADD` 補入（涵蓋 Decision 3 的 `ZADD` 失敗）。**「且不在 `A_redis`」是第十輪審查後新增的必要排除條件**：若 entryId 已存在於 `A_redis`（不論是已落地的推進、還是 Lua Script 剛完成、Postgres 尚未跟上的合法過渡態），代表 Redis 端已經對這筆紀錄做出「推進」的決策，MUST NOT 因為 Postgres 快照當下仍顯示 `Waiting`（Postgres 只是還沒追上這個決策）就把它「補齊」回 `waiting`——否則會讓同一個 entryId 同時出現在 `waiting` 與 `admitted` 兩個集合，破壞兩者互斥的前提，可能被下一輪 Lua Script 的 `ZPOPMIN` 重複選中、造成非預期的二次推進（見下方問題說明）
4. **補齊 admitted**：`A_pg` 中不在 `A_redis` 的 entryId → `ZADD` 補入（涵蓋 Decision 4 推進批次寫回失敗、或應用程式啟動/Redis 重啟後的鏡像遺失）
5. **清除 admitted 中已確認終態或孤兒的殘留（本輪審查修正：範圍從「只清 `Completed`」擴大為「不屬於目前合法 Admitted 狀態集合」；實作階段審查後再修正：明確排除第四種情況）**：`A_redis` 中**不屬於 `A_pg`、不屬於 `A_pg_overdue`、也不屬於下方步驟 7 的過渡態候選集合**的 entryId → `ZREM`。這個集合差運算涵蓋三種**應清除**的情況：(a) Postgres 狀態已為 `Completed`（涵蓋 Decision 8 的 `ZREM` 失敗）；(b) Postgres 狀態已為 `Expired`（例如該筆紀錄先前是透過本節步驟 8 的條件式 `UPDATE` 落地為 `Expired`，但當時 Redis 端因故未同步移除；正常路徑下 Decision 4 步驟 1 的 `ZRANGEBYSCORE` 會在同一個 Lua Script 呼叫內原子完成 `ZREM`，此處是額外的防禦性收斂，處理該原子路徑之外的殘留）；(c) 該 entryId 在 Postgres 查無對應紀錄（孤兒 member，例如資料被刪除或存在其他寫入路徑的錯誤）——三種情況統一用「不在目前合法 Admitted 集合內」判定，不需要分別枚舉每一種終態或建立額外的 Postgres 查詢。**MUST NOT 誤清 `A_pg_overdue` 殘留**：`A_pg_overdue`（Postgres 仍為 `Admitted` 但已逾期、尚未落地為 `Expired`）本身仍屬於合法的 Admitted 狀態集合，不在此步驟的清除範圍內——這類殘留會在下一次 Decision 4 Lua Script 呼叫的步驟 1（`ZRANGEBYSCORE -inf now`）依 score 自然清除，屬於 Decision 4 自身的收斂機制，本步驟提前清除雖然結果冪等不會造成資料錯誤，但會與 Decision 4 步驟 1 的職責重疊，不必要地增加複雜度。**MUST NOT 誤清第四種情況——「Postgres 仍為 `Waiting`、已在 `A_redis`」的過渡態候選（strict-reviewer 於實作階段審查發現，見下方修正說明）**：`A_redis` 中不屬於 `A_pg` 也不屬於 `A_pg_overdue` 的 entryId，**若**其 Postgres 狀態為 `Waiting`，代表它是步驟 7 負責處理的過渡態候選（不論其 `pending` 標記是否仍存活），**一律**排除在本步驟的清除範圍外、完全交給步驟 7 判斷與處理——**不可**只排除「`pending` 標記仍存活」的子集：若只排除子集，「`pending` 已過期、待步驟 7 代為完成落地」的另一半仍會被本步驟誤判為孤兒而 `ZREM`，其後果比放任不管更嚴重——該筆紀錄會從 `admitted` 鏡像完全消失但 Postgres 沒有任何紀錄反映這件事，下一輪步驟 3 會把它當成全新的 `Waiting` 候選重新 `ZADD` 回 `waiting`，可能被下一次 `ZPOPMIN` 二次選中、造成重複推進。此排除範圍刻意涵蓋整個過渡態候選集合、不區分 `pending` 存活與否，只在步驟 7（唯一判斷 `pending` 是否存活並代為完成落地的步驟）內部處理。**格式損壞 member 的修正（strict-reviewer 事後審查發現）**：`A_redis` 中無法解析為合法 `Guid` 的 member（格式損壞，例如資料寫入錯誤或人為誤植）先前的實作誤判為「解析失敗、略過」，導致它永久被 `ZCARD` 計入有效名額、永久卡住容量；MUST 視為孤兒（同上述情況 (c)）直接 `ZREM`，不因解析失敗而略過
6. **清除 waiting 中不屬於 `W_pg` 的殘留（本輪審查修正用詞：從「Postgres 狀態已明確不是 Waiting」改為明確的集合差運算，避免遺漏「該 entryId 在 Postgres 查無對應紀錄」的孤兒情況）**：`W_redis` 中不屬於 `W_pg` 的 entryId → `ZREM`，涵蓋該 entryId 的 Postgres 狀態已流轉至其他狀態、或該 entryId 在 Postgres 查無對應紀錄兩種情況。同上方步驟 5 的修正：無法解析為合法 `Guid` 的格式損壞 member，同樣 MUST 視為孤兒直接 `ZREM`
7. **偵測並修復放棄的推進決策**：`A_redis` 中 Postgres 狀態仍為 `Waiting` 的 entryId（過渡態候選）→ 檢查 `pq:admit:pending:{entryId}`（見 Decision 9）：標記仍存在（TTL 未到期）→ 視為合法過渡態，MUST NOT 清除；標記不存在（TTL 已到期）→ 系統 MUST 執行**條件式** `UPDATE ... SET Status = 'Admitted', AdmissionExpiresAtUtc = {Redis admitted zset 該成員的 score} WHERE Id = entryId AND Status = 'Waiting'`（見 Decision 4 的條件式 `UPDATE` 修正），影響 0 列（代表原程序其實已經成功寫入，或該紀錄已流轉到其他狀態）視為正常情況、不觸發任何後續動作；不重新競爭推進決策——Redis 端的決策本身已經確定且有效，只是原本該完成落地的程序未能確定完成，校正機制安全地代為嘗試即可
8. **偵測並修復放棄的逾時標記**：`A_pg_overdue` 中不在 `A_redis` 的 entryId → 系統 MUST 執行**條件式** `UPDATE ... SET Status = 'Expired' WHERE Id = entryId AND Status = 'Admitted'`——此步驟不需要 TTL 寬限窗口判斷（「已逾時」是純粹的時間事實，沒有競態疑慮），條件式 `UPDATE` 本身已保證任何時候偵測到都可以安全重試，不論先前是否已經有其他路徑成功寫入

觸發時機：
1. 應用程式啟動時執行一次全量校正（比照既有 `IConnectionMultiplexer` 非阻塞連線設定，不阻塞啟動流程）
2. 每輪推進前，對該活動執行一次上述校正

**校正與入場推進 Lua Script 使用不同的 `now`（strict-reviewer 事後審查修正）**：步驟 2 觸發時機下，校正（本節步驟 1）比較基準的 `now` 與 Decision 4 入場推進 Lua Script 使用的 `now`（`ARGV[1]`、及新入場逾時時間 `admissionExpiresAtUtc` 的計算基準）MUST 分開各自即時取得，不得沿用同一個在校正執行前就先取好的舊值——校正本身可能耗時，若沿用校正前的舊 `now` 計算 `admissionExpiresAtUtc`，會讓實際入場者拿到的有效視窗短於 `AdmissionTtlSeconds`（`now` 早於推進真正發生的時間點，但 TTL 是以這個偏舊的 `now` 為基準往後推算）。

**重複執行的冪等性**：步驟 3～8 全部由「差集比對」或「條件式 `UPDATE`」構成，對同一 entryId 重複執行校正（不論是同一輪內意外重複呼叫、或連續多輪校正處理到同一筆殘留）皆為安全 no-op——`ZADD`／`ZREM` 對已符合目標狀態的成員重複執行沒有副作用，條件式 `UPDATE` 影響 0 列時不觸發任何動作。校正機制不需要額外的重入保護或去重機制。

**Alternatives considered**：
1. 曾考慮不做主動校正，讓 Redis 遺失資料後該活動的排隊靠新加入請求自然補齊——但既有 `Waiting` 紀錄會因此永久卡住，違反既有 Requirement「等待中的排隊紀錄沒有自身逾時機制」對「不因等待過久而被跳過」的保證，故排除
2. 曾考慮（第二版設計）「數量不一致即整個 `DEL` 重建」——被排除，因為會誤傷 `PQLE-006a` 允許的合法重疊執行過渡態
3. 曾考慮（第三版設計）「Postgres 仍為 Waiting、entryId 已在 admitted 一律視為合法過渡態、永不清除」——被排除，因為程序崩潰時會造成永久名額洩漏，改為步驟 7 的 TTL 寬限窗口判斷

### Decision 6：Redis 不可用時，入場推進 fail-closed（偏離既有 `purchase-queue-leader-election` 的 fail-open 慣例）
`purchase-queue-leader-election` 既有 `PQLE-007` 對「Redis 不可用」是 fail-open（照常執行，因為當時正確性仍由 Postgres 悲觀鎖保證）。本次改動後，Redis Lua Script 是入場推進唯一的互斥/決策機制，若 Redis 不可用時仍嘗試用某種降級路徑執行推進，將失去「不超額」的保證。

**決策**：入場推進在偵測到 Redis 不可用時，MUST 記錄 Warning 等級結構化 log 並跳過本輪推進（不執行任何 `Waiting → Admitted` 的變更），等待下一輪輪詢重新嘗試——這是刻意的 fail-closed，優先保護「不超賣」高於「排隊持續推進」的可用性，對齊 `docs/project-scope.md` 第 1 節商業目標優先序。此行為與分散式鎖本身取得失敗時的既有處理（跳過本輪）殊途同歸，但成因不同（前者是決策機制不可用，後者是鎖競爭失敗），需在 spec delta 中新增獨立 Scenario 說明。

**Alternatives considered**：保留 `GetForAdmissionAsync` 作為 Redis 不可用時的降級路徑（雙軌並存）——被排除，因為這違背本次改動「用 Redis 原子性取代 Postgres 鎖」的目的，且雙軌並存會讓正確性論證需要同時涵蓋兩套機制交互作用的情境，複雜度不成比例，不符合 CLAUDE.md 的 Simplicity First 原則。

**「Redis 恢復後、下一次成功校正」的精確邊界（第十輪審查要求，補齊鎖競爭與單輪部分失敗的情境）**：`PQ-COMPLETE-003`／`PQLE-REBUILD-004` 承諾的「下一次成功執行的校正」，實際邊界受以下因素影響，MUST 誠實記錄而非假設每次都恰好是「緊接著的下一輪」：
- **鎖競爭**：任一時刻只有取得分散式鎖的實例會執行該輪的推進與校正（見 `背景推進服務的多實例互斥執行` Requirement）；沒取得鎖的實例本輪完全跳過。這不構成額外的保證缺口——因為取得鎖的那個實例，其該輪校正 MUST 涵蓋**全部** `IsQueueModeEnabled = true` 的活動（見下一點的逐活動隔離設計），不因鎖由哪個實例取得而遺漏特定活動
- **單輪內個別活動處理失敗（可攔截的例外），MUST NOT 中止整輪其餘活動的處理**：入場推進與校正的主迴圈 MUST 對每個活動的處理獨立包一層例外處理（try/catch），單一活動處理時遇到的例外（連線問題、逾時等）MUST 只記錄 Warning 並跳過該活動，MUST NOT 讓例外中止迴圈、影響同一輪其餘活動的處理
- **持鎖的實例在處理到目標活動之前，整個程序中止（不可攔截，例如崩潰）**：與上一點的可攔截例外不同，這種情況下 try/catch 無法介入，該輪對目標活動的校正視同未執行——這不是保證的破口，而是「該輪沒有處理到」的情況，順延至下一次成功取得鎖、且該輪確實處理到目標活動的校正即可；MUST NOT 因為「曾經有一輪取得過鎖」就誤判為「已經檢查過、不需要再檢查」。**測試環境下的可控制模擬方式**：真正的程序崩潰無法在測試中重現，MUST 改用「逐活動迴圈中途取消 `CancellationToken`」模擬等價效果——依 tasks.md 3.4，逐活動的例外隔離 MUST NOT 攔截 `OperationCanceledException`，因此在處理完第一個（非目標）活動後、開始處理目標活動前取消 token，可讓整個方法呼叫確定性地在到達目標活動前中止，且不會被逐活動的業務例外隔離機制誤攔截、繼續處理到目標活動（見 tasks.md 9.13）
- **校正本身於執行期間再度遇到 Redis 逾時**：與 Decision 4/5 既有的「結果未知不做反向補償」原則一致，校正的個別操作若逾時，只記錄 Warning、跳過該筆，交由下一次該活動被處理到的校正嘗試補上；不影響同一輪其他 entryId 或其他活動的處理

以上兩種「部分失敗」情境（可攔截例外的單一活動隔離、不可攔截的整程序中止）合稱為本次新增的明確設計要求，避免「持鎖的實例處理到一半掛掉，導致該輪只處理了部分活動」這種部分失敗，把只影響一個活動的問題，錯誤地擴大成拖累同一輪所有活動，或反過來被誤判為「已處理」。

綜合以上，精確的保證邊界是：**「Redis 恢復連線後，下一次成功取得鎖執行的一輪，只要該輪處理到這個活動（依上述逐活動隔離設計，只要該輪正常執行、沒有在到達這個活動前就整個程序中止，就一定會處理到），該活動的殘留就會被校正修復」**——比「下一輪」的字面保證更精確，也更誠實地反映鎖競爭與例外隔離下的實際行為。**此定義為 `PQLE-REBUILD-004`（見 spec delta `purchase-queue-leader-election` 能力「Redis 不可用時的降級行為」Requirement）、`purchase-queue` 能力 `PQ-COMPLETE-003`、本文件 Decision 8 共用的唯一版本，三處 MUST 保持語意一致，對應整合測試見 tasks.md 9.7／9.12／9.13／9.14。**

**契約語意的明確選擇（本輪審查要求：「不設重試次數上限」與「MUST NOT 無限期延遲」字面上互相矛盾——若 Redis 永久不可用，任何沒有重試上限的機制都無法迴避無限期延遲，這在數學上不可能同時成立，需要明確選邊，不能兩者都宣稱）**：本設計選擇 **eventual consistency 語意**（而非「有界修復」語意），理由如下：
- **拒絕「有界修復」語意的原因**：有界修復需要定義最大重試次數、重試 backoff、Error 升級後的終止或轉人工介入路徑、以及「不構成永久延遲」的可觀察判定條件——但入場推進的正確性保證（不超額、不遺漏）完全建立在「條件式 `UPDATE`／`ZADD`／`ZREM` 永遠可以安全重試」之上（見 Decision 4／5），若設重試上限並在超過上限後放棄或轉為其他處理方式，會直接產生「這個名額被永久卡住／這個 `Completed` 紀錄永久佔用名額」的新缺陷，比目前「持續重試直到 Redis 恢復」更嚴重，不符合本次改動的核心目的
- **eventual consistency 的精確承諾（以此取代先前容易誤讀為絕對保證的「MUST NOT 無限期延遲」「MUST NOT 演變為永久性遺漏」字面用詞）**：系統 MUST 保證「**只要** Redis 最終恢復連線、且存在至少一次成功取得鎖並實際處理到目標活動的校正執行，該次執行 MUST 完成收斂」；系統 **MUST NOT** 在 Redis 已經可連線、且已經處理到目標活動的情況下，因為任何本設計自身的邏輯（例如不必要的重試上限、不必要的降頻／退避）而人為拖延收斂——這是本設計對「不無限期延遲」實際負責的範圍。若 Redis **本身**永久不可用（真正的硬體/網路永久故障，而非本設計的邏輯缺陷），名額釋放確實會跟著永久延遲，這是 Redis 這項外部依賴本身故障的直接後果，不是本設計能夠或應該迴避的情況（比照既有 `purchase-queue-leader-election` 對「Redis 不可用時入場推進整體不可用」這一更基本的既有限制，屬於同一類外部依賴失效的誠實記錄，不是本次新增的缺口）
- **「不需要人工介入」的精確範圍**：不需要人工介入才能讓系統在 Redis 恢復後自動收斂（這是機制層面的保證）；但若 Redis 故障時間遠超正常網路抖動的預期（Decision 8 的 Log 等級升級門檻正是為了讓維運人員能觀察到這個異常），排查 Redis 本身故障的根因、讓 Redis 恢復連線，本來就需要人工介入——這是維運 Redis 這項外部服務本身的正常職責，不是本設計「收斂機制」需要額外承擔的責任
- **spec delta 用詞同步修正**：`PQ-COMPLETE-003`／`PQLE-REBUILD-004` 的「MUST NOT 演變為永久性遺漏」字面用詞已改為條件式的 eventual consistency 版本（見下方 spec delta 對應段落），不再使用容易誤讀為「不論 Redis 是否恢復都保證有限時間內完成」的絕對化用詞

### Decision 7：安全確認（CLAUDE.md 安全強制規則，第七輪審查後補齊完整清單）
本次改動涉及資料庫讀寫操作（Decision 4 的批次 `UPDATE`、Decision 5 的逐筆校正查詢），依 CLAUDE.md 安全強制規則逐條回答，**不因本次改動不觸及 Controller／前端就省略，逐項明確標注「不適用／沿用既有機制」**：

**輸入驗證**
- **外部輸入有沒有經過 Validation？在哪一層？**：本次改動不新增任何對外 API 端點或請求參數，不涉及外部輸入。背景服務（`PurchaseQueueAdmissionService`）的輸入來源是資料庫查詢結果與 Redis 讀取結果，皆為系統內部產生的資料（`eventId`／`entryId` 皆為既有流程產生的 `Guid`），非使用者直接輸入，不適用此問題
- **有沒有直接拼接進 SQL 或 shell 指令？**：沒有。Postgres 存取一律經 EF Core（見下方參數化查詢回答）；Redis Lua Script 的參數一律透過 `EVAL` 的 `KEYS`/`ARGV` 傳遞（見 Decision 4），不字串拼接

**資料庫**
- **是否使用 EF Core／Dapper 參數化查詢？**：Decision 4／Decision 5 的條件式 `UPDATE`（`WHERE Id = entryId AND Status = 期望前置狀態`）一律透過 EF Core 表達（`DbContext.PurchaseQueueEntries.Where(e => entryIds.Contains(e.Id) && e.Status == 期望前置狀態).ExecuteUpdateAsync(...)`），不手寫原生 SQL、不字串拼接，符合既有 `禁止繞過 EF Core 直接寫原生 SQL` 規則；`ExecuteUpdateAsync` 回傳的實際影響列數即為條件式判斷的依據，不需額外查詢確認
- **有沒有 N+1 查詢風險？**：Decision 4 的批次條件式 `UPDATE` 以單一 `Where(...Contains(entryIds) && Status == X)` 對整批 entryId 一次查詢/更新，非逐筆迴圈查詢；Decision 5 的校正查詢每個活動一次撈出該活動目前 `Waiting`／`Admitted`（含未逾時與已逾時）的 entryId 清單（有 `EventId + Status` 複合索引，既有 `PurchaseQueueEntryConfiguration.cs:28` 的 `{ EventId, Status, JoinedAtUtc, Id }` 索引已覆蓋），與 Redis 端清單在記憶體中做差集比對（`ZADD`/`ZREM`/條件式 `UPDATE` 針對差集逐筆執行，仍以批次 `Contains` 形式送出，非逐筆資料庫往返，不構成 N+1）

**權限**
- **這個操作需要什麼權限？權限檢查在哪一層執行？**：入場推進、校正機制是純背景服務（`IHostedService`），不經過任何 HTTP 端點觸發，沒有呼叫者身份、不適用權限檢查層級的概念——比照既有 `purchase-queue-leader-election` 背景服務的既定模式。本次改動涉及的兩個 Handler 異動（`PQ-JOIN`、`PQ-COMPLETE`）新增的 Redis 同步動作，掛載在既有 Handler 內部，沿用該 Handler 既有的 `[Authorize]` 授權層級，不新增、不變更權限檢查
- **有沒有可能被未授權使用者觸發？**：背景服務本身無法被外部觸發（僅依輪詢間隔自動執行）；`PQ-JOIN`／`PQ-COMPLETE` 新增的 Redis 同步動作沿用既有端點的授權檢查（`PQ-JOIN` 既有 `[Authorize]`、`PQ-COMPLETE` 走既有下單流程的授權），本次未新增任何繞過既有授權檢查的路徑

**前端**
- **有沒有直接將使用者輸入渲染進 DOM？**：不適用——本次改動純屬後端背景服務與 Redis／Postgres 互動，不涉及任何前端程式碼或 API 回應格式變更
- **API 呼叫有沒有帶正確的 Auth Header？**：不適用——本次改動未新增或修改任何 API 端點，既有 `PQ-JOIN`／`PQ-STATUS` 端點的 Auth Header 檢查機制不變

### Decision 8：訂單完成時同步移除 Redis `admitted` 鏡像（design-hardener 審查後新增，修正入場推進的名額計算缺口）
既有 `PQ-COMPLETE` 在 `OrderService.cs` 建立訂單成功的同一 Postgres 交易內呼叫 `PurchaseQueueEntry.Complete()`，將排隊紀錄標記為 `Completed`。**原始碼核對（第八輪審查要求，事實宣稱需附具體來源）**：`OrderService.PlaceOrderAsync` 於 `OrderService.cs:171` 以 `await using var transaction = await _unitOfWork.BeginTransactionAsync(...)` 開始交易，`OrderService.cs:187` 取得 `queueEntry`，`OrderService.cs:308` 呼叫 `queueEntry?.Complete()`，`OrderService.cs:311` 才 `await transaction.CommitAsync(...)`——`Complete()` 確認在 `CommitAsync` 之前、同一筆交易範圍內執行，此交易 commit 後，MUST 額外執行 best-effort `ZREM pq:admit:{eventId}:admitted {entryId}`（模式比照 Decision 3 的 Join 同步——不在同一交易內、失敗不影響訂單建立本身的成功與否）。

若不做此同步：Decision 4 的入場推進 Lua Script 以 `ZCARD pq:admit:{eventId}:admitted` 作為「目前有效名額」的計算依據，已完成訂單的紀錄若仍留在 `admitted` zset，會持續佔用名額直到原本的 `AdmissionExpiresAtUtc` 自然到期，違反既有 `PQ-COMPLETE-002`「名額於交易提交後立即可供下一位使用，不受原訂逾時時間影響」的 MUST 保證。

`ZREM` 失敗（Redis 不可用）的補救：交由 Decision 5 的校正機制偵測並修復——校正時查得該 entryId 在 Postgres 已明確為 `Completed`（非過渡態），MUST 將其從 `admitted` zset 清除；**精確的保證邊界（修正 Issue 4：「下一輪」在 Redis 持續故障期間並不成立；本輪審查再修正：需與 Decision 6 的三個邊界因素完全一致，不可只提「Redis 恢復」一個因素）**：校正本身也需要 Redis 才能執行（見 Decision 6 fail-closed，Redis 不可用時整輪推進含校正一併跳過），因此「下一輪校正」實際上受 Decision 6 列出的**三個因素**共同影響——(1) 鎖競爭：只有取得鎖的實例執行該輪校正，但該實例的校正 MUST 涵蓋全部活動，不因鎖由誰取得而遺漏本活動；(2) 取得鎖的實例在處理到本活動**之前**即整個程序中止（例如崩潰）：本輪視同未處理到本活動，順延至下一次真正處理到本活動的校正；(3) 校正本身執行期間再度遇到 Redis 逾時：只記錄 Warning、跳過該筆，不影響其他活動或下一次校正。綜合以上，精確定義是「**Redis 恢復連線後、下一次成功取得鎖且實際處理到本活動、`ZREM` 確實執行成功的校正**」，而非字面上「緊接著的下一次輪詢」；若持續無法滿足以上任一條件，名額釋放會持續延遲，這是刻意接受的條件式收斂例外，邊界是「真正處理到本活動且成功」而非固定的輪詢次數或單純的「Redis 恢復」，且不承諾有限時間內收斂——若 Redis 永久不可用，此例外會持續存在——spec delta 中 `PQ-COMPLETE-003`／`PQLE-REBUILD-004` 已同步採用此精確版本，三處（含本節）語意一致，不各自表述（見下方 Risks 與 spec delta 中 `PQ-COMPLETE` Requirement 的對應說明）。

**重試與升級策略（本輪審查要求：「Redis 恢復後、下一次成功校正即修復」的保證過強，需明確定義重試次數上限、退避策略、log 升級門檻、是否需要人工介入，否則只能保證「某次成功執行的 ZREM 會收斂」，不能保證不會永久延遲）**：**適用範圍明確聲明（本輪審查澄清）**：以下重試/升級策略（含連續失敗計數與 Log 等級升級門檻）MUST 統一適用於三種同屬「校正層條件式寫回」的失敗來源，不只限於本節標題所述的 `ZREM`——(a) 本節的 `ZREM` 失敗（`PQ-COMPLETE-003`／`PQLE-REBUILD-004`）；(b) Decision 5 步驟 7「偵測並修復放棄的推進決策」條件式 `UPDATE` 失敗（`PQ-ADMIT-006`／`PQLE-REBUILD-002a`）；(c) Decision 5 步驟 8「偵測並修復放棄的逾時標記」條件式 `UPDATE` 失敗（`PQLE-REBUILD-003b`）——三者共用同一個以 entryId 為 key 的連續失敗計數與同一套升級門檻邏輯（見 tasks.md 4.8），理由是三者在失效模式與收斂機制上完全對稱（皆為冪等的條件式寫回、皆無需反向補償、皆交由下一輪校正自然重試），拆成三套獨立邏輯不成比例增加複雜度，違反 CLAUDE.md Simplicity First 原則：
- **重試機制與次數上限**：MUST NOT 另外實作獨立的重試/退避邏輯——校正本身是「每輪對每個活動全量重新執行」的冪等操作（見 Decision 5「重複執行的冪等性」），故每一輪推進前的校正呼叫，本質上就是對上一輪未成功項目的一次自動重試，重試間隔即為既有的 `PollingIntervalSeconds`；**不設重試次數上限**——只要 Redis 最終恢復連線，理論上下一次成功處理到本活動的校正就會完成該筆 `ZREM`；MUST NOT 因為「已經重試超過 N 次」就放棄或改變處理方式（放棄會直接導致名額永久卡住，違反「不遺漏」的核心保證，比持續佔用名額更嚴重）
- **退避策略**：不需要指數退避——單次 `ZREM` 呼叫成本低、無副作用風險（冪等），過度退避只會拉長真正修復所需的時間，退避的價值（降低下游負載）在此情境不成立
- **Log 等級升級門檻（新增，補齊本輪審查要求的可觀察性缺口；本輪再修正：明確定義計數的儲存範圍、歸零條件與跨實例／重啟行為，否則「達到門檻」無法成為可驗證的系統保證）**：
  - **儲存範圍與資料結構**：連續失敗次數 MUST 儲存為 `PurchaseQueueAdmissionService` 所在**單一程序內的記憶體狀態**（例如 `ConcurrentDictionary<Guid, int>`，key 為 entryId），**MUST NOT** 嘗試寫入 Postgres 或 Redis 做為權威來源——理由：這個計數只是「連續失敗次數達到門檻時把 log 等級從 Warning 升級為 Error」的**純觀察性**輔助資訊，不是任何正確性判斷的依據（正確性完全由「條件式 `UPDATE`／`ZREM` 冪等重試」保證，見上方「重試機制與次數上限」），若此時 Redis 正好故障，也無法把計數寫入本來就不可用的 Redis；寫入 Postgres 則需要額外交易與欄位，對一個純觀察性功能是不成比例的複雜度（不符合 CLAUDE.md Simplicity First 原則）
  - **作用範圍的明確限制（誠實記錄，不假裝跨程序/跨重啟精確）**：此計數的作用範圍僅限「目前持有分散式鎖、實際執行本輪校正的那個程序實例」——若因鎖競爭（Decision 6 邊界因素之一）在不同輪次由不同實例取得鎖，或該實例重啟，計數會回到 0 重新累計，**不會**跨實例／跨重啟精確加總。這是刻意接受的已知限制：後果僅止於「Error 等級告警可能比實際故障持續時間更晚觸發」（低估連續失敗次數），MUST NOT 影響任何正確性保證（重試本身不依賴此計數，見上方），也 MUST NOT 因為計數被重置就誤判為「问题已解決」——重試邏輯與計數完全獨立運作，即使計數歸零，只要該筆 `entryId` 仍在 Redis `admitted` 鏡像中且 Postgres 已為 `Completed`，校正仍會持續嘗試 `ZREM`
  - **歸零條件**：同一 entryId 的計數在以下任一情況發生時 MUST 歸零並從字典中移除（避免無界成長）：(1) 該筆 entryId 的 `ZREM`／條件式 `UPDATE` 補償操作成功執行；(2) 校正判定該筆 entryId 已不再需要此項補償（例如已不在 Redis `admitted` 鏡像中，或 Postgres 狀態已流轉為其他終態）——這兩種情況都代表該筆殘留已解決，不需要繼續追蹤
  - **升級門檻與觸發時機**：連續失敗次數達到設定門檻（`IOptions`，比照 Decision 9 TTL 的驗收程序模式：預設值於實作階段依 Redis 故障平均恢復時間與 `PollingIntervalSeconds` 的比例量測後定案，例如「連續 10 輪」為初始預設值，若量測顯示 Redis 平均故障恢復時間對應的輪數遠低於此值可維持，否則需調整並同步更新本文件）時，MUST 將**當次**該筆記錄的 log 等級從 Warning 升級為 Error（僅該次失敗的 log 使用 Error，不是持續每次都用 Error 洗版——後續每次仍失敗才會再次以 Error 記錄，因為每次記錄的當下連續次數都已超過門檻）——此升級純粹為了可觀測性／告警，MUST NOT 影響重試邏輯本身（仍然繼續逐輪重試、不中止、不放棄）
- **是否需要人工介入**：收斂機制本身不需要人工介入——只要 Redis 最終恢復連線，系統會自動完成收斂；Error 等級 log 的目的是讓維運人員在「Redis 故障時間異常拉長」（超出一般網路抖動的正常範圍，可能代表 Redis 本身有更深層的問題）時能主動介入排查根因，而非收斂機制的正確性依賴人工觸發
- **「成功校正」的判定基準**（沿用第十一輪已定義的版本，本輪重申以消除歧義）：以「該次校正確實對目標活動完成了對應的 Redis 讀寫操作（例如某筆 `ZREM` 實際執行且回應成功）」為準，不以「輪詢次數」或「經過時間」判斷

`PQ-COMPLETE-003`／`PQLE-REBUILD-004` 的 spec delta 與對應測試（tasks.md 9.7／9.12～9.14）已同步納入上述重試/升級語意，四處保持一致。

**Alternatives considered**：讓 Lua Script 執行時即時查詢 Postgres 確認 `admitted` zset 內每個 entryId 的真實狀態——被排除，因為 Lua Script 無法在 Redis 原子執行環境內呼叫外部資料庫，會破壞 Decision 4 整段操作的原子性，等同重新引入本次改動想取代的跨系統競態問題。

### Decision 9：推進決策的放棄偵測機制（`pending` 標記，第四輪審查後新增，修正程序崩潰造成永久名額洩漏的缺口）
**問題**：Decision 5 第三版邏輯規定「Postgres 仍為 `Waiting`、entryId 已在 Redis `admitted` zset」一律視為合法過渡態、永不清除，理由是這是 Decision 4 正常執行路徑的短暫時序落差。但若持有該推進決策的程序（Lua Script 執行完畢後、Postgres `UPDATE` 完成前）當機，這筆紀錄會永久卡在這個「過渡態」——Redis 持續計入有效名額，Postgres 永遠不會變成 `Admitted`，且因為規則禁止清除，沒有任何機制會發現並修復，形同永久洩漏一個名額，且無法被任何既有 Scenario 偵測。

**決策**：Decision 4 Step 3 對每個新推進的 entryId，在同一個 Lua Script 內原子地額外執行 `SET pq:admit:pending:{entryId} 1 EX 30`。Decision 5 的校正機制（步驟 7）在遇到「Postgres 仍為 `Waiting`、entryId 已在 `admitted`」時，改為檢查此標記是否仍存在：存在則視為合法過渡態不動；已過期（不存在）則視為放棄的決策，直接由校正機制代為完成 Postgres `UPDATE`，不重新競爭推進資格。

**TTL 值的契約定位（第七輪審查要求：不能完全留給實作階段決定）**：`30` 秒是本次改動**正式定案的預設契約值**，不是留待實作階段自由決定的暫定數字。來源：透過 `IOptions`（比照既有 `EventCapacityOptions` 模式，可經 `appsettings`／環境變數覆寫，預設值固定為 30 秒）提供，非寫死常數。驗收程序：
1. 實作階段 MUST 在整合測試環境實際量測批次 `UPDATE` 的耗時分佈（P99）
2. 若量測結果顯示 30 秒 ≥ P99 × 10（寬裕假設成立），30 秒維持為最終契約值，不需修改本文件
3. 若量測結果顯示 30 秒 < P99 × 10（寬裕假設不成立，例如環境效能明顯低於預期），MUST 將預設值調高至 P99 × 10 以上，且 MUST 同步更新本節與 Risks 段落的數值，並在該次任務的 PR 說明中記錄量測數據與調整後的值——調整後的值才是最終驗收契約，不得讓程式碼與本文件的數字不一致
4. 驗收（`strict-reviewer`／`spec-reviewer` 審查）時，以「本文件當下記載的數值」與「程式碼／設定檔實際配置的預設值」是否一致為判準，兩者不一致即為 blocking

此設計不需要更複雜的「decision id / version」方案——因為 `ZPOPMIN` 本身是原子操作，同一個 entryId 不可能被兩個不同的 Script 執行重複取出並各自產生衝突的推進決策，只會有「決策已產生、落地中」或「決策已產生、落地失敗需要補完」兩種狀態，一個布林式的 TTL 標記已足以區分，不需要版本號或租約物件。

**Alternatives considered**：
1. 不設寬限時間，任何「Postgres 仍 Waiting、Redis 已 admitted」的狀態都立即視為需要校正機制介入完成——被排除，因為 Decision 4 的批次 `UPDATE` 本身需要一點時間執行，過早介入會與正常執行中的請求打架（例如校正機制搶先寫入、原程序稍後也寫入，雖然結果冪等不會造成資料錯誤，但徒增不必要的競爭與 log 雜訊）
2. 用 `decision id`／版本號取代簡單的存在性標記——被排除，因為 `ZPOPMIN` 的原子性已經排除了「同一 entryId 被重複決策」的可能，版本號解決的問題在此設計下不存在，屬過度設計

### Decision 10：Redis key 生命週期、eviction 與隔離策略（第四輪審查後新增，補齊原本「檢視、必要時調整」的空白決策）
核對現況（第八輪審查要求補齊具體來源，逐一列出）：
- `docker-compose.yml:39-43`：`redis` 服務沒有任何 `command`／設定檔掛載，未設定 `maxmemory`——代表在正常記憶體可用的情況下不會因記憶體上限觸發 eviction（`maxmemory-policy` 只在設有 `maxmemory` 上限且觸及時才會作用）；只有主機層級 OOM 才可能造成資料遺失，這是既有 lock／cache／captcha 用途已經承受的同一種基礎設施風險，本次不新增額外風險
- `src/ProjectC.WebApi/Program.cs:208-220`：`IConnectionMultiplexer` 以 `ConnectionMultiplexer.Connect(redisConfigurationOptions)` 建立，連線字串來自 `ConnectionStrings__Redis`（`docker-compose.yml` 設為 `"redis:6379"`），未指定 `defaultDatabase`／`Db=` 參數
- `src/ProjectC.Infrastructure/DistributedLocking/RedisDistributedLock.cs:31,51`、`src/ProjectC.Infrastructure/Captcha/RedisCaptchaService.cs:41,56`、`src/ProjectC.Infrastructure/Caching/RedisQueryCache.cs:26,59,76`：三者皆呼叫 `_connectionMultiplexer.GetDatabase()`（無參數），StackExchange.Redis 在未指定資料庫編號時預設為 index 0——三者實際上都在同一個 DB（index 0）操作
- Key 前綴證據（**第九輪審查要求縮小宣稱範圍，不誇大驗證程度**）：`RedisCaptchaService.cs:14` 定義 `KeyPrefix = "captcha:"`；`PurchaseQueueAdmissionService.cs:17` 定義 `LeaderElectionLockKey = "purchase-queue-admission:lock"`；`RedisQueryCache` 本身是通用快取介面，`IQueryCache` 實際有多個呼叫端各自組出 key（`GetEventsHandler.CacheKey`、`GetTicketTypesHandler.BuildCacheKey(...)`、`OrderService.cs:317,460` 等），本次**僅核對到 `OrderService.cs` 呼叫的 `GetTicketTypesHandler.BuildCacheKey(...)` 這一個呼叫端**，未逐一核對 `IQueryCache` 的每一個呼叫端。**因此本文件的隔離宣稱精確範圍為**：現有隔離策略確認是「單一 DB（index 0）+ 各服務自訂 key 前綴」的模式（非多 DB index）；本次改動新增的 `pq:admit:` 前綴與已核對過的 `captcha:`、`purchase-queue-admission:lock`、`GetTicketTypesHandler.BuildCacheKey(...)` 三者不重疊；**不宣稱**已窮舉驗證 `IQueryCache` 全部呼叫端（含 `GetEventsHandler` 等）的 key 格式都與 `pq:admit:` 前綴不衝突——`pq:admit:` 是足夠具體、業界慣例不會意外撞名的命名空間（比對既有 `captcha:`／`purchase-queue-admission:` 等既有前綴的具體程度），實務風險評估為極低，但誠實記錄為未窮舉驗證

**決策**：入場推進的 Redis 鍵（`pq:admit:{eventId}:waiting`、`pq:admit:{eventId}:admitted`、`pq:admit:pending:{entryId}`）沿用既有慣例，走同一個 DB（index 0）+ 專屬 `pq:admit:` 前綴隔離，不新增 DB index、不修改 `maxmemory-policy`——與既有用途的風險等級一致，不需要額外的持久化或隔離機制。

- `waiting`／`admitted` zset **不設 TTL**——生命週期由應用邏輯（Lua Script 的 `ZADD`/`ZREM`/`ZPOPMIN`）與 Decision 5 的校正機制主動管理，比照 Decision 5 的「鏡像資料視為可重建的衍生資料」原則，不需要被動過期
- `pq:admit:pending:{entryId}` **設 TTL（30 秒，見 Decision 9）**——這是唯一需要被動過期的 key，因為它的存在意義就是「時間窗口內的標記」
- **單一 key 遺失／部分資料遺失／`FLUSHALL` 的偵測與復原**：不需要獨立的偵測機制——Decision 5 的校正機制本身就是通用的偵測與復原手段，不論鏡像資料是完全遺失（`FLUSHALL`）、部分 key 遺失、或單一活動的 zset 被意外清除，校正機制在下一輪推進前都會依 Postgres 真相補齊缺漏，行為上與「應用程式啟動時的全量校正」（Decision 5 觸發時機 1）完全一致，不需要另外設計「偵測遺失」的邏輯——校正機制的補齊/清除本身就是冪等的自我修復，不論觸發原因為何

**Alternatives considered**：為入場推進獨立開一個 Redis DB index 或獨立 Redis 服務——被排除，現有專案對 Redis 的多用途隔離都是靠 key 前綴，沒有先例使用多 DB index，獨立引入會打破既有慣例且無實質風險緩解（見上方 eviction 風險評估），不符合 CLAUDE.md 的 Simplicity First 原則

## Risks / Trade-offs

- [風險] Redis 鏡像與 Postgres 真相之間短暫不同步（Decision 3/4/8 的 best-effort 操作失敗）→ 緩解：Decision 5 的校正機制（已擴充涵蓋 `admitted` zset，且改為定向操作、不誤傷合法過渡態）；`waiting` zset 不同步只造成推進延誤，不造成超額入場
- [風險] Decision 8 的 `ZREM` 失敗時，已完成訂單的紀錄短暫繼續佔用 Redis `admitted` 名額，直到 **Redis 恢復連線後、下一次成功執行的校正**（非固定輪詢次數，見 Decision 5 精確邊界說明）才釋放，與既有 `PQ-COMPLETE-002`「名額立即釋放」的保證有短暫偏離 → 緩解：僅限 Redis 故障期間發生，且 Redis 恢復後即修復，非永久性違反；此為誠實記錄的已知限制，非遺漏（若 Redis 永久不可用，此延遲確實會永久化——此邊界情況已在 Decision 6／Decision 8／spec.md `PQ-COMPLETE-003`／`PQLE-REBUILD-004` 明確記載，此處僅為摘要，不表示絕對保證）
- [風險] 同毫秒（`JoinedAtUtc` 完全相同）的多筆紀錄，tie-break 契約已正式改為 Redis member 字串 lexicographic 順序（見 Decision 2 契約決定），不再嘗試論證與 Postgres `Id ASC` 等價 → 緩解：契約已正式修改並將以自動化測試驗證實際行為的確定性、可重現性（見 tasks.md），僅影響極少數同毫秒紀錄彼此的相對推進順序，不影響「不超額入場」「不遺漏」等 MUST 級保證
- [風險] Decision 9 的 `pending` 標記 TTL（預設 30 秒）若設定過短，可能在批次 `UPDATE` 正常執行但耗時較長時被誤判為放棄的決策，導致校正機制與正常執行中的請求同時嘗試完成同一筆 Postgres 寫入（結果冪等、不會造成資料錯誤，但增加不必要的競爭與 log 雜訊）→ 緩解：30 秒為契約定案值，實作階段 MUST 量測 P99 驗證是否足夠寬裕，不足時 MUST 依 Decision 9 的驗收程序調高並同步更新本文件，不是留給實作自由心證的暫定數字
- [風險] Decision 5 每輪校正查詢增加 Postgres 查詢負載 → 緩解：現有規模下為有索引的清單查詢，成本可忽略；活動數量大幅成長時可降低校正頻率，不影響正確性、僅影響不同步的偵測即時性
- [Trade-off] Redis 不可用時 fail-closed（Decision 6），排隊推進會短暫停滯，換取不超賣的正確性優先——相較於既有 `purchase-queue-leader-election` 的 fail-open 慣例是行為上的偏離，需要在 spec delta 中明確標注、且不可與既有 `PQLE-007` 的 fail-open 語意混淆
- [Trade-off] 本設計中 Redis 只負責「入場推進」的互斥決策，Postgres 仍是最終真相——相較於「Redis 完全取代 Postgres」的技術純粹度較低，但換取 `PQ-COMPLETE`／`PQ-JOIN` 的 Postgres 交易語意與正確性保證零風險（僅新增 best-effort 的 Redis 鏡像同步動作，非交易本身），審查成本大幅降低（design-hardener 審查後修正：初版誤稱「零風險」涵蓋 `PQ-COMPLETE` 完全不受影響，實際上 `PQ-COMPLETE` 需新增 Decision 8 的同步動作，見上方風險項）

## Migration Plan

- `docker-compose.yml` 的 `redis` 服務沿用現有設定（見 Decision 10：不新增 DB index、不設 `maxmemory-policy`、不新增 AOF 持久化）
- 部署新版本、應用程式啟動時執行 Decision 5 的全量校正；校正過程本身若因 Redis 不可用而無法執行，比照 Decision 6 fail-closed，等待 Redis 恢復後執行下一次校正並繼續（非固定輪詢次數）
- 無回滾特殊步驟——若需回滾，還原程式碼版本即可，Postgres 資料結構本身不變（未新增/移除欄位或資料表），Redis 鏡像資料屬衍生性質可直接捨棄

## Open Questions

（無——原「校正檢查頻率」問題已定案：每輪推進前皆執行校正，不設降頻，見 Decision 5「觸發時機」；現有規模下查詢成本可忽略，見 Risks，且降頻會直接放大 `PQ-COMPLETE` 名額釋放延遲與 Redis 資料遺失後的收斂時間，維持每輪校正是本次設計的必要前提，不留作實作階段的開放決策）
