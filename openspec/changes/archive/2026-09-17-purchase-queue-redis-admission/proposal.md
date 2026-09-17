## Why

現有 `purchase-queue` 能力的「入場推進」（背景服務依序將 `Waiting` 推進為 `Admitted`，且不超過名額上限）完全以 PostgreSQL 悲觀鎖（`PurchaseQueueRepository.GetForAdmissionAsync`）作為正確性基礎；`purchase-queue-leader-election` 的 Redis 分散式鎖只包在背景推進服務外層減少多實例重複執行，本身不擔保任何正確性，正確性完全仍靠 Postgres 悲觀鎖兜底（見 `PQLE-006a`）。

本次改動將「入場推進」的併發控制核心，從 Postgres 交易鎖改為 Redis Lua Script 的原子操作，對齊業界虛擬候位室（virtual waiting room）常見實作模式（如 AWS 參考架構、Queue-it 等：用記憶體資料庫的單執行緒原子操作取代關聯式資料庫交易鎖做排隊推進）。

**誠實揭露動機**：`docs/project-scope.md` 第 5 節已明確記載，現有量級（2000 座位／20 票種、100–300 同時在線）下 Postgres 複合索引即可支撐一般查詢，不存在真實效能瓶頸；本次改動的目的是技術深度展示，非解決現有效能問題，不得以效能理由包裝動機。

## What Changes

- 「入場推進」背景服務的名額計算與 `Waiting → Admitted` 推進決策，從 Postgres 悲觀鎖交易改為 Redis Lua Script 的原子操作；**推進決策**（誰被推進、推進幾筆）由 Redis 原子計算，決策結果仍寫回 Postgres 持久化
- Postgres `PurchaseQueueEntry` 資料表**維持為系統唯一真相來源**，不改變其角色——「加入排隊」（`PQ-JOIN`）、「查詢排隊狀態」（`PQ-STATUS`）、「建立訂單成功後標記完成」（`PQ-COMPLETE`）三項既有行為的 **Postgres 交易語意本身** 維持不變，本次不修改；但 `PQ-COMPLETE-002`「名額立即釋放」這項保證在 Redis 端的衍生計算上，新增一個條件式收斂例外（見下方範圍修正說明與 design.md Decision 8）——這是本提案對既有行為唯一的、需要誠實揭露的偏離
- Redis 端新增一份「入場推進專用」的即時鏡像資料（`Waiting` 排序、`Admitted` 逾時排序），供推進決策使用；鏡像資料遺失（如 Redis 重啟）時需能從 Postgres 重建，重建策略於 `design.md` 說明
- **範圍修正（design-hardener 審查發現，見 design.md Decision 8）**：`PQ-JOIN`、`PQ-COMPLETE` 的 Handler 各需新增一個 best-effort 的 Redis 鏡像同步動作（Join 寫入 `waiting` 鏡像、Complete 移除 `admitted` 鏡像），確保 Redis 端的有效名額計算不因訂單完成而失準；此同步動作在既有 Postgres 交易 commit 後執行、非同一交易，失敗不影響 `PQ-JOIN`／`PQ-COMPLETE` 本身的成功與否，僅由 Decision 5 的校正機制負責偵測與修復
- `purchase-queue-leader-election` 的分散式鎖正確性論證基礎需同步更新：`PQLE-006a` 目前論證「鎖租約重疊執行時，最終正確性由 Postgres 悲觀鎖兜底」，此保證需改為「由 Redis Lua Script 的原子性兜底」
- **`JoinedAtUtc` 同毫秒的 tie-break 排序契約正式修改**（第四輪審查要求）：由「`Id ASC`（Postgres `uuid` 比較）」改為「Redis member 字串 lexicographic 順序」，不嘗試論證兩者等價；新增自動化測試驗證同毫秒批次的確定性、可重現性（`PQ-ADMIT-005`）
- **新增「放棄的推進/逾時決策」偵測與修復機制**（第四輪審查發現的程序崩潰缺口，design.md Decision 9）：入場推進決策寫回 Postgres 前若執行者中止（例如程序當機），校正機制需能區分「仍在正常落地中的合法過渡態」與「已放棄、需代為完成」，避免名額被永久卡住；新推進與逾時標記兩種決策各自的補償/放棄修復邏輯需分開處理，不可混用（`PQ-ADMIT-006`、`PQLE-REBUILD-002a`／`003a`／`003b`）
- **所有 Postgres 寫回一律改為條件式 `UPDATE`**（第五輪審查發現：無條件覆寫會讓延遲恢復的舊決策蓋掉已流轉的新狀態，也會讓「結果未知」的失敗補償方向錯誤）：以 `Status` 欄位本身當版本錨點（`WHERE Id = entryId AND Status = 期望前置狀態`），不引入額外的 `decisionId`／版本號機制；`UPDATE` 遇到任何例外（含結果未知）一律不做反向 Redis 補償，只記錄 log，收斂交給校正機制的條件式重試（天然對重複執行安全）

## 明確排除範圍（留待後續提案）

- 「加入排隊」的唯一性約束、「查詢排隊狀態」的排名查詢、「建立訂單成功後標記完成」三項既有行為的 Postgres 交易機制本身完全不動；`ticket-purchase` 的 API 介面與既有 Postgres 交易內訂單／資格檢查語意不變，但 `OrderService` 必須新增 commit 後的 `PQ-COMPLETE` Redis `admitted` 鏡像同步（`ZREM`），這不是不可觀察的內部實作變更——`PQ-COMPLETE-001`～`003` 的名額釋放時機與故障延遲契約皆依賴此同步動作，且該同步動作失敗不影響其 Postgres 交易本身；`PQ-COMPLETE-002` 在 Redis 端衍生的「有效名額」計算上有一個新增的條件式收斂例外（見上方範圍修正說明），這條 Requirement 需要 spec delta 明確標注此例外，不能算完全不受影響
- 若之後要把「加入排隊」也改為 Redis 原生機制（涉及與 `PQ-COMPLETE` 的跨儲存一致性問題），風險與複雜度較高，留待本提案完成、Redis 端機制穩定後另開提案處理
- **Redis 全域資源治理與正式容器部署明確排除**（第五輪審查提出，已與使用者確認）：`maxmemory`／eviction／告警、production Dockerfile、Redis TLS／認證／readiness 探測，皆是跨越所有既有 Redis 用途（cache-aside、captcha、leader-election）的基礎設施關注點，非本次新增風險，留待獨立的「Redis 生產就緒」提案處理（見 design.md Non-Goals）

## Capabilities

### New Capabilities
（無）

### Modified Capabilities
- `purchase-queue`：「排隊入場名額依先後順序推進，且有名額上限」一條 Requirement 的併發控制機制改變（`PQ-ADMIT-004` 對正確性論證的描述需重寫，`PQ-ADMIT-001`／`003` 的 tie-break 用詞同步調整），新增 `PQ-ADMIT-005`（同毫秒 tie-break 契約）、`PQ-ADMIT-006`（放棄決策的校正補完）兩條 Scenario；「建立訂單成功後標記排隊紀錄為已完成，名額即時釋放」需新增一個條件式收斂例外的限定說明（`PQ-COMPLETE-003`，`PQ-COMPLETE-002` 明確限定為同步成功情境，見 design.md Decision 8）；**「買家可查詢自己的排隊狀態」「等待中的排隊紀錄沒有自身逾時機制」「Admin 關閉熱門搶購模式後既有排隊紀錄不主動清理」三條 Requirement 需新增文字修改**（第七輪審查發現：三者皆引用 `JoinedAtUtc ASC, Id ASC` 的完整 tie-break 字面，與 `PQ-ADMIT` 新契約的措辭不一致，需同步調整用詞並在「買家可查詢自己的排隊狀態」補上排序一致性範圍的精確化說明；三者的 Handler 實作行為本身不變，僅文字對齊）；`PQ-JOIN` 的 Requirement 文字不需修改，其 Handler 實作內部新增一個 best-effort Redis 鏡像同步步驟（不影響該 Requirement 本身的行為保證，純屬支援 `PQ-ADMIT` 新機制的內部依賴）
- `purchase-queue-leader-election`：分散式鎖重疊執行期間的正確性兜底機制，從 Postgres 悲觀鎖改為 Redis Lua Script 原子性，`PQLE-006a` 需重寫論證；Redis 不可用時的降級行為從 fail-open 改為 fail-closed（`PQLE-007`／`PQLE-008` 需重寫）；新增「入場推進的 Redis 鏡像與 Postgres 真相一致性保證」Requirement，涵蓋逐筆校正（`PQLE-REBUILD-001`／`002`）、放棄決策的偵測與補完（`PQLE-REBUILD-002a`）、新推進與逾時標記兩種批次分開的失敗補償（`PQLE-REBUILD-003`／`003a`／`003b`），以及訂單完成同步失敗的條件式收斂例外、精確邊界為「Redis 恢復後」而非固定輪詢次數（`PQLE-REBUILD-004`）

## Impact

- **Infrastructure 層**：新增 Redis Lua Script（入場推進的原子計算，含 `pending` 標記寫入），新增「Postgres 與 Redis 鏡像逐筆校正」邏輯（含放棄決策偵測，見 design.md Decision 5/9）；`docker-compose.yml` 的 `redis` 服務設定沿用現有慣例（單一 DB index + key 前綴隔離，不新增 DB index／`maxmemory-policy`，見 design.md Decision 10 的具體決策，非待確認事項）
- **Application 層**：背景入場推進 Handler（`PurchaseQueueAdmissionService`）的核心邏輯改寫，含新推進／逾時標記兩種批次分開的失敗補償；加入排隊（`PQ-JOIN`）的 Handler 需在既有 Postgres 交易 commit 後，新增「同步寫入 Redis `waiting` 鏡像」的動作，既有 Postgres 寫入路徑與交易保證不變；訂單完成（`PQ-COMPLETE`，位於 `OrderService.cs`）的 Handler 需在既有 Postgres 交易 commit 後，新增「同步移除 Redis `admitted` 鏡像」的動作（design.md Decision 8），既有 Postgres 交易語意不變；查詢狀態（`PQ-STATUS`）的 Handler **不需要**任何異動，維持純查詢 Postgres
- **測試**：`purchase-queue` 的 `PQ-ADMIT-*`、`PQ-WAIT-001` 與 `purchase-queue-leader-election` 全部既有測試，需改為針對 Redis 的整合測試（Testcontainers Redis）；黑箱行為（Scenario）不變，但測試手法需重寫；`PQ-JOIN-*`、`PQ-STATUS-*` 測試不受影響；**`PQ-COMPLETE-001` 目前沒有對應的既有測試能涵蓋本次新增的行為**（核對 `OrderService.cs` 現況，交易 commit 後尚無任何 Redis 同步動作），MUST 新增端到端整合測試（下單完成 → Redis 同步 → 下一輪推進實際推進下一位）；`PQ-COMPLETE-002`（`PurchaseQueueAdmissionServiceTests.cs` 既有案例）**必須修改**斷言方式為驗證 Redis 鏡像狀態，非僅檢查 Postgres 欄位；新增多項測試覆蓋放棄決策偵測、批次補償、同毫秒排序（詳見 tasks.md 第 7～9 節，每條新增 Scenario 皆有對應測試任務）
- **範圍限定**：`ticket-purchase` 的 API 介面與 Postgres 交易語意（座位/庫存鎖定、訂單建立本身）不變，但其核心 Handler 所在的 `OrderService.cs` 內新增交易 commit 後的 best-effort Redis 衍生鏡像同步（`PQ-COMPLETE` 的 `ZREM`，見 Decision 8），不能算「整合方式完全不變」；`buyer-web-ui`、`captcha-verification` 等既有能力則確實不受影響，既有行為與整合方式完全不變
