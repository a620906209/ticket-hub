## 1. Redis 基礎設施與資料結構

- [x] 1.1 定義 Redis key 命名規則（`pq:admit:{eventId}:waiting`、`pq:admit:{eventId}:admitted`、`pq:admit:pending:{entryId}`），對應 design.md Decision 1／Decision 9
- [x] 1.2 撰寫入場推進 Lua Script：逾時清除（`expiredIds`）、有效名額計算、`Waiting → Admitted` 推進（`promotedIds`，含原子寫入 `pending` 標記，初始 TTL 30 秒，見 1.4）、回傳兩份異動清單（design.md Decision 4）；`eventId`／名額上限等參數一律透過 `EVAL` 的 `KEYS`/`ARGV` 傳遞，禁止字串拼接組出 Script 文字
- [x] 1.3 依 design.md Decision 10 的既定決策落實：沿用單一 Redis DB（index 0）+ `pq:admit:` 前綴隔離，不新增 DB index、不設 `maxmemory-policy`；`waiting`／`admitted` zset 不設 TTL，`pending` 標記設 TTL（見 1.4）
- [x] 1.4 `pending` TTL 的量測與設定（design.md Decision 9 的正式契約，非暫定值）：將 TTL 值做成可設定項（`IOptions`，比照既有 `EventCapacityOptions` 模式），預設 30 秒；於整合測試環境量測批次 `UPDATE` 的實際耗時分佈（P99），記錄量測方法與數據於 PR 說明；若 P99 × 10 ≤ 30 秒，維持預設值不變；若 P99 × 10 > 30 秒，**MUST** 將預設值調高至 P99 × 10 以上，並同步修改 design.md Decision 9／Risks 段落的數字，使文件與實際配置的預設值一致——這是本任務的完成條件之一，不得只改程式碼不改文件

## 2. 加入排隊同步寫入 Redis 鏡像（design.md Decision 3）

- [x] 2.1 `PQ-JOIN` 既有 Postgres 交易 commit 後，新增 best-effort `ZADD` 寫入 `waiting` zset（score = `JoinedAtUtc` 的 Unix 毫秒，**非** `PurchaseQueueEntry.Id`——`Id` 為隨機 `Guid`，見 design.md Decision 2 修正說明）
- [x] 2.2 `ZADD` 失敗時的容錯處理：不影響加入排隊本身成功與否，記錄可觀察 log，不重試

## 2a. 訂單完成同步移除 Redis 鏡像（design.md Decision 8）

- [x] 2a.1 `PQ-COMPLETE`（`OrderService.cs` 呼叫 `PurchaseQueueEntry.Complete()`）既有 Postgres 交易 commit 後，新增 best-effort `ZREM` 移除 `admitted` zset 對應 entryId
- [x] 2a.2 `ZREM` 失敗時的容錯處理：不影響訂單建立本身成功與否，記錄可觀察 log，交由 4 節的校正機制修復

## 3. 入場推進邏輯改寫（design.md Decision 4，第五輪審查後定案：所有 Postgres 寫回一律條件式）

- [x] 3.1 `PurchaseQueueAdmissionService` 改為呼叫 Lua Script，取得 `expiredIds`（含原逾時時間）與 `promotedIds`（含新逾時時間）兩份清單
- [x] 3.2 `expiredIds` 批次**條件式** `UPDATE`（`WHERE Id IN (...) AND Status = 'Admitted'`）為 `Status = Expired`；`promotedIds` 批次**條件式** `UPDATE`（`WHERE Id IN (...) AND Status = 'Waiting'`）為 `Status = Admitted`／新 `AdmissionExpiresAtUtc`；不使用 `PurchaseQueueRepository.GetForAdmissionAsync`。**批次 `ExecuteUpdateAsync` 影響列數的用途澄清（本輪審查發現，design.md Decision 4 同步修正）**：單一 `ExecuteUpdateAsync(WHERE Id IN (...))` 呼叫只回傳整批的**總**影響列數，MUST NOT 假設能從中判斷「批次內各筆個別是否生效」——若批次送出 N 個 Id、實際只影響 M（M < N）列，代表其餘 N-M 筆的前置狀態已不成立（例如已被校正機制或其他路徑搶先處理），這是正常情況；此總數**僅作為可觀測性 log 的參考數字**（例如「本輪推進 N 筆，實際生效 M 筆」），MUST NOT 依此總數做任何補償或重試判斷，也 MUST NOT 嘗試逐筆改用個別 `ExecuteUpdateAsync` 呼叫來取得逐筆結果（會讓一輪推進從 1 次批次 SQL 退化為 N 次個別 SQL，且逐筆知道「哪幾筆生效」對正確性沒有必要——不論是哪幾筆沒生效，收斂都完全交給 4 節校正機制的下一輪重新判斷，不需要在本步驟內辨識是哪些 entryId）
- [x] 3.3 任一批次 `UPDATE` 遇到例外（連線失敗、逾時、或結果未知）：**MUST NOT** 做任何反向 Redis 補償（不 `ZADD` 回 `waiting`／`admitted`、不清除 `pending` 標記），只記錄 Warning 結構化 log，收斂完全交給 4 節的校正機制（見 design.md Decision 4 修正說明：反向補償在「結果未知」情境下會把已落地的狀態錯誤復原）
- [x] 3.4 [PQLE-REBUILD-008]（第十輪審查要求，design.md Decision 6「單一活動處理失敗不影響同一輪其餘活動」；本輪審查補上明確 Scenario／測試標記，見 spec.md `PQLE-REBUILD-008`、tasks.md 9.13a）`PurchaseQueueAdmissionService` 對每個 `IsQueueModeEnabled = true` 活動的處理（含 3.1~3.3 的推進、4 節的校正）MUST 各自包一層獨立的例外處理；單一活動處理時的**業務例外**（連線問題、逾時等）MUST 只記錄 Warning 並跳過該活動，MUST NOT 中止主迴圈、MUST NOT 影響同一輪其餘活動的處理結果。**此逐活動例外處理 MUST NOT 攔截／吞掉 `OperationCanceledException`**（例如 `catch (Exception ex) when (ex is not OperationCanceledException)`）——`OperationCanceledException` 代表呼叫端主動取消或既有 `ExecuteAsync` 外層的取消語意，MUST 讓它正常往外傳遞、中止本輪剩餘活動的處理，這是 design.md Decision 6「持鎖的實例在處理到目標活動之前整個程序中止（不可攔截）」情境在測試環境下的可控制模擬方式（見 tasks.md 9.13），與「業務例外」的逐活動隔離是兩種不同語意，不得混用同一種 catch 邏輯

## 4. Redis 鏡像校正機制（design.md Decision 5，第五輪審查後定案：全面條件式 `UPDATE`）

- [x] 4.1 查詢該活動 Postgres 目前 `Waiting`（`W_pg`）、未逾時 `Admitted`（`A_pg`）、已逾時未標記（`A_pg_overdue`）三個集合；讀取 Redis `waiting`（`W_redis`）／`admitted`（`A_redis`）成員時 **MUST** 透過單一 Lua Script（`EVAL`）在 Redis 端原子地一次讀取兩個 zset 的全部成員並一併回傳，**MUST NOT** 以兩個獨立的 `ZRANGE` 呼叫分兩次讀取（design.md Decision 5 步驟 2 修正：分開讀取會讓 Decision 4 的入場推進 Lua Script 有機會插入執行在兩次讀取之間，導致 `W_redis`／`A_redis` 取自不同時間點，讓某 entryId 同時「不在 `W_redis`」又「不在 `A_redis`」，被步驟 4.2 誤判為缺漏而補回 `waiting`，造成該 entryId 同時存在於 `waiting` 與 `admitted` 兩個集合）
- [x] 4.2 補齊缺漏：`W_pg` 不在 `W_redis` **且不在 `A_redis`** 的 entryId `ZADD` 補入 `waiting`（第十輪審查發現：漏掉「且不在 `A_redis`」會讓 Lua Script 剛推進、Postgres 尚未追上的 entry 被誤補回 `waiting`，同時出現在兩個集合，見 design.md Decision 5 修正說明／`PQLE-REBUILD-005`）；`A_pg` 不在 `A_redis` 的 entryId `ZADD` 補入 `admitted`
- [x] 4.3（本輪審查修正：範圍從「只清 `Completed`」擴大為「不屬於目前合法狀態集合」，見 design.md Decision 5 步驟 5/6、`PQLE-REBUILD-007`）清除不屬於目前合法狀態集合的殘留：`A_redis` 中**不屬於 `A_pg` 也不屬於 `A_pg_overdue`** 的 entryId `ZREM`（涵蓋 Postgres 狀態為 `Completed`、`Expired`，或該 entryId 在 Postgres 查無對應紀錄三種情況；`A_pg_overdue` 本身仍屬合法 Admitted 狀態，MUST NOT 被本步驟清除，留給 Decision 4 步驟 1 的逾時清除自然收斂）；`W_redis` 中**不屬於 `W_pg`** 的 entryId `ZREM`（涵蓋已流轉至其他狀態、或查無對應紀錄兩種情況）
- [x] 4.4 放棄的推進決策偵測與修復：`A_redis` 中 Postgres 仍為 `Waiting` 的 entryId，檢查對應 `pending` 標記——存在則不動（合法過渡態）；不存在則以**條件式** `UPDATE`（`WHERE Id = entryId AND Status = 'Waiting'`）完成落地為 `Admitted`（`AdmissionExpiresAtUtc` 取 `admitted` zset 該成員 score）；影響 0 列（前提已不成立）視為正常情況，不重試、不記錄為錯誤
- [x] 4.5 放棄的逾時標記偵測與修復：`A_pg_overdue` 中不在 `A_redis` 的 entryId，以**條件式** `UPDATE`（`WHERE Id = entryId AND Status = 'Admitted'`）完成落地為 `Expired`；影響 0 列同樣視為正常情況
- [x] 4.6 應用程式啟動時對每個活動執行一次全量校正，比照既有 `IConnectionMultiplexer` 非阻塞連線設定，不阻塞啟動流程
- [x] 4.7 每輪推進前，對該活動執行一次上述校正（design.md 已定案：每輪皆執行，不留降頻的開放問題）；目前活動數量規模（`docs/project-scope.md` 定義的量級）下單輪校正成本可忽略，不實作分頁／批次上限／逾時取消語意——若活動數量未來大幅成長導致單輪校正耗時顯著增加，需另開變更處理，本次不預先設計（誠實記錄的範圍邊界，非遺漏）
- [x] 4.8（本輪審查要求，design.md Decision 8「重試與升級策略」，對應 `PQ-COMPLETE-003`／`PQLE-REBUILD-004`；**本輪再修正**：明確指定儲存結構與歸零條件，不能只寫「記錄一個欄位」帶過）新增「連續失敗次數」追蹤，具體實作方式：
  - 在 `PurchaseQueueAdmissionService` 內新增一個 `ConcurrentDictionary<Guid, int>` 欄位（key 為 entryId），**MUST NOT** 寫入 Postgres 或 Redis（純觀察性資訊，見 design.md Decision 8 說明）；此狀態的生命週期與該程序實例相同，重啟或鎖易主至其他實例時自然歸零，這是已知且可接受的限制（見 design.md）
  - 校正步驟 4.4／4.5（放棄的推進決策／逾時標記偵測修復）與 2a.2（`ZREM` 失敗補償）每次對某 entryId 執行失敗時：字典中該 entryId 的計數 +1（不存在則視為 0 再 +1）；成功執行、或判定該 entryId 已不再需要此項補償時：**MUST** 將該 entryId 從字典中移除（歸零）
  - 連續失敗次數達到設定門檻（`IOptions`，比照 Decision 9 TTL 模式，預設值 10 輪，可於實作階段依量測調整並同步更新 design.md）時，MUST 將**當次**該筆失敗的 log 等級從 Warning 升級為 Error；此升級 MUST NOT 影響重試邏輯本身（不中止、不放棄、不改變處理方式，下一輪仍照常嘗試）；不需要重試次數上限，也不需要獨立的退避邏輯（重試間隔即為既有 `PollingIntervalSeconds`）

## 5. Redis 不可用時的 fail-closed 行為（design.md Decision 6）

- [x] 5.1 偵測 Redis 連線失敗（含 Lua Script 執行失敗或結果未知）時，跳過本輪推進**與校正**，記錄 Warning 等級結構化 log；`EVAL` 呼叫逾時、結果未知時 MUST NOT 在同一輪內重試呼叫（見 design.md Decision 4 修正說明，避免 Script 被重複執行造成誤推進）
- [x] 5.2 確認此行為與既有分散式鎖取得失敗的跳過邏輯共存、不重複記錄或衝突
- [x] 5.3 更新 `docker-compose.yml` 中 `api` 對 `redis` 的 `depends_on` 註解（目前引用舊版 `purchase-queue-leader-election` fail-open 決策），反映本次改動後入場推進已改為 fail-closed

## 6. purchase-queue-leader-election 既有邏輯調整

- [x] 6.1 移除入場推進路徑中不再需要的 `PurchaseQueueRepository.GetForAdmissionAsync`（批次 FOR UPDATE，`PurchaseQueueAdmissionService.cs:158`）呼叫；**不得**移動或刪除同一段程式碼中用途不同的 `eventRepository.GetForUpdateAsync`（`:152`，用於 Queue Mode 切換的線性化，本次改動不變更此行為）
- [x] 6.2 確認分散式鎖本身的取得／釋放／TTL 邏輯不需變更（僅推進機制內部依賴改變）

## 7. 測試 — purchase-queue（`PQ-ADMIT`）

測試類型：整合測試（Testcontainers Postgres + Testcontainers Redis）為主，因為契約驗證的是 Redis Sorted Set／Lua Script 的實際排序與原子行為，mock 無法驗證。被測主體：`PurchaseQueueAdmissionService`（含呼叫的 Lua Script）。

- [x] 7.1 [PQ-ADMIT-001] 整合測試：有剩餘名額時，依 `JoinedAtUtc ASC` 依序推進 `Waiting → Admitted`，斷言 Postgres 與 Redis 鏡像狀態一致
- [x] 7.2 [PQ-ADMIT-002] 整合測試：名額已滿時不推進，斷言 Postgres `Waiting` 紀錄狀態不變
- [x] 7.3 [PQ-ADMIT-003] 整合測試：入場逾時釋放名額，斷言逾時紀錄轉為 `Expired`、下一輪推進使用釋放的名額
- [x] 7.4 [PQ-ADMIT-004]（改寫）整合測試：兩個背景服務實例（模擬 `PQLE-006a` 鎖租約到期重疊執行）對同一活動同時呼叫推進，斷言最終有效入場數不超過上限、無重複推進、無遺漏——用真實併發呼叫（非 mock 序列化）驗證 Redis 端的實際互斥效果
- [x] 7.5 [PQ-ADMIT-005]（測試類型修正：**整合測試**，非單元測試——契約驗證的是 Redis Sorted Set 實際排序行為，mock 無法驗證）建立多筆 `JoinedAtUtc` 精確相同（同毫秒）、`entryId`（`Guid`）字典序刻意交錯的 `Waiting` 紀錄，對真實 Redis 執行實際的 Lua Script，驗證完整推進順序依 `entryId` 字串 lexicographic 排列；重複執行同一組輸入，確認結果可重現（確定性）
- [x] 7.6 [PQ-ADMIT-006]（測試手法修正，第七輪審查要求：用真實 Lua Script 建立過渡態，不手動組裝 Redis 資料）整合測試：**先呼叫真實的入場推進 Lua Script**，讓它原子地把 entryId 從 `waiting` 移到 `admitted` 並寫入 `pending` 標記（重現正常推進決策的真實副作用）；接著**刻意跳過**該次决策原本該執行的 Postgres `UPDATE`（模擬持有決策的程序中止，不去手動竄改 Redis 資料本身）；將 `pending` 標記的 TTL 設為極短值或直接 `DEL` 以加速模擬過期；驗證下一次校正正確完成該筆紀錄的 Postgres 落地（條件式 `UPDATE`）；另外驗證「原程序延遲後才執行」的情境：先讓校正機制完成落地，再執行原本被延遲、但已經是校正完成後才姍姍來遲的條件式 `UPDATE`，斷言該次為 0 列受影響、不覆寫任何後續狀態

## 8. 測試 — purchase-queue-leader-election（`PQLE`）

**被測主體修正（本輪審查發現：原標題與 8.2.1～8.2.3 實際內容矛盾；上一輪文案「PQLE-004～006 目前唯一的既有測試位於 `RedisDistributedLockTests.cs`」的講法不精確，本輪修正用詞，避免造成「`PurchaseQueueAdmissionServiceLeaderElectionTests.cs` 不存在或未被考慮」的誤解）**：原標題宣稱「8.1.1～8.2.3 共用既有測試方法、斷言不需修改」，但核對既有程式碼後發現這對 PQLE-004～006 不成立。精確的現況是：
- `tests/ProjectC.WebApi.Tests/BackgroundServices/PurchaseQueueAdmissionServiceLeaderElectionTests.cs` 這個服務層測試檔案**已經存在**，且已涵蓋 PQLE-001／002／003／006a／007／008／009 共 7 個 Scenario 的服務層整合測試（第 111～339 行）——**沒有任何一個既有方法名稱或方法內容以 PQLE-004、PQLE-005 或 PQLE-006 為對象**；PQLE-001 的既有測試方法 `AdvanceQueueOnceWithLeaderElectionAsync_SingleInstance_AcquiresExecutesThenReleasesLock`（第 111～125 行）雖然標的是 PQLE-001，但其斷言（第 122～123 行：`ReleaseCallCount.Should().Be(1)`、Redis 鎖 key 已不存在）**內容上恰好與 PQLE-004「正常完成後主動釋放鎖」的 Scenario 描述重疊**，形同已有一個既有測試附帶驗證了 PQLE-004 的行為，只是未被標記為 PQLE-004 專屬測試（8.2.1 仍建議新增一個明確標記 `[PQLE-004]` 的獨立測試方法以符合 AC↔測試一對一追溯慣例，但不能宣稱 PQLE-004 完全沒有既有服務層驗證）
- `tests/ProjectC.Infrastructure.Tests/DistributedLocking/RedisDistributedLockTests.cs`（元件層，直接呼叫 `RedisDistributedLock.TryAcquireAsync`／`ReleaseAsync`，不經過 `PurchaseQueueAdmissionService`）才是 PQLE-004／005／006 這三個 Scenario **目前唯一有明確標記對應關係**的既有測試方法：`ReleaseAsync_AfterNormalAcquire_AllowsImmediateReacquisitionByAnotherCaller`（PQLE-004）／`TryAcquireAsync_AfterTtlExpiresWithoutRelease_AllowsOtherCallerToAcquire`（PQLE-005）／`ReleaseAsync_WithStaleOwnerTokenAfterAnotherCallerAcquiredNewLock_IsNoOp`（PQLE-006）

這些既有測試（不論元件層或 PQLE-001 的附帶驗證）都只驗證鎖原語本身或單一情境下的副作用，不能完整驗證 Scenario 實際要求的「背景推進服務完成一輪推進後釋放鎖」「TTL 到期後背景服務能重新取得鎖」「舊持有者遲到釋放不會刪除新持有者的鎖」這幾件事在服務層的完整整合行為（服務層是否正確呼叫鎖的取得/釋放、是否與推進邏輯正確串接，尤其 PQLE-005／006 完全沒有服務層驗證）。統一方式：**保留現有 Scenario**（PQLE-004～006 的行為契約不變），但比照 PQLE-001～003 的既有模式，**新增**以 `PurchaseQueueAdmissionService.AdvanceQueueOnceWithLeaderElectionAsync` 為測試入口的服務層整合測試（見 8.2.1～8.2.3 細項），完整驗證背景服務整合行為；既有的 `RedisDistributedLockTests.cs` 元件層測試與 `PurchaseQueueAdmissionServiceLeaderElectionTests.cs` 既有的 PQLE-001～003／006a／007～009 測試（驗證鎖原語本身或其他 Scenario 的正確性）**維持不變、不需修改**，彼此測試目標不同、並存不衝突。

被測主體（8.1.1～8.1.3）：`PurchaseQueueAdmissionServiceLeaderElectionTests.cs`；PQLE-001～003 的服務層邏輯與既有測試方法皆未變，逐條執行既有測試方法、確認既有斷言不需修改，`dotnet test` 通過即完成；若因 Decision 4/5 的介面調整導致編譯失敗，只修測試中呼叫入場推進的介面呼叫方式，不得改變測試斷言的內容。

被測主體（8.2.1～8.2.3）：**新增**至 `PurchaseQueueAdmissionServiceLeaderElectionTests.cs` 的服務層整合測試，測試入口一律為 `PurchaseQueueAdmissionService.AdvanceQueueOnceWithLeaderElectionAsync`，不直接呼叫 `RedisDistributedLock`；`RedisDistributedLockTests.cs` 既有的元件層測試同步保留、不刪除、不修改。

**既有測試與新契約衝突的清單（本輪審查發現：`PurchaseQueueAdmissionServiceLeaderElectionTests.cs` 目前仍有多個測試方法斷言舊契約——fail-open、Postgres 悲觀鎖保證不超額——這些斷言在本次改動後不成立，MUST 明確列為「取代」對象，不能只寫「(改寫)」帶過）**：
- `AdvanceQueueOnceWithLeaderElectionAsync_WhenLockTtlExpiresWhileHolderStillExecuting_OverlapDoesNotExceedAdmissionLimit`（第 271～304 行，PQLE-006a）：第 303 行斷言「由既有資料庫悲觀鎖保證」，MUST 取代為 8.3 描述的新斷言（Redis Lua Script 序列化 + `pending` 標記不被誤清除）
- `AdvanceQueueOnceWithLeaderElectionAsync_WhenRedisUnreachable_ExecutesFullAdvanceWithoutReleasingAndLogsWarning`（第 194～215 行，PQLE-007）：第 214 行斷言「Redis 不可用時仍 MUST 照常執行本輪的完整活動掃描與入場推進」（fail-open），MUST 取代為 8.4 描述的 fail-closed 斷言
- `AdvanceQueueOnceWithLeaderElectionAsync_WhenRedisUnavailable_BothInstancesExecuteButAdmissionStaysWithinLimit`（第 308～339 行，PQLE-008）：第 331 行斷言「資料庫悲觀鎖仍保證不超額入場」，MUST 取代為 8.5 描述的 fail-closed 斷言（兩實例皆跳過，Postgres 排隊紀錄維持原狀）
- `AdvanceQueueOnceWithLeaderElectionAsync_AfterRedisOutageRecovers_ResumesNormalMutualExclusionWithoutRestart`（第 220～269 行，PQLE-009）：第 244 行斷言「故障期間兩實例皆 fail-open 執行」，MUST 取代為 8.6 描述的 fail-closed 斷言（故障期間兩實例皆跳過，Postgres 排隊紀錄不變；恢復連線後才由取得鎖的一方執行推進）

`AdvanceQueueOnceWithLeaderElectionAsync_SingleInstance_AcquiresExecutesThenReleasesLock`（PQLE-001）／`_TwoInstancesConcurrently_OnlyOneAcquiresAndExecutes`（PQLE-002）／`_WhenHeldByOther_SkipsThenRetriesSuccessfullyNextRound`（PQLE-003）三個既有方法未斷言任何 fail-open 或 Postgres 悲觀鎖語意，不受本次改動影響，維持不動。

- [x] 8.0 更新 `PurchaseQueueAdmissionServiceLeaderElectionTests.cs` 檔案開頭的說明註解（第 16～19 行，目前寫「Redis 故障期間 fail-open 與故障恢復後的協調行為」），反映本次改動後 Redis 不可用時已改為 fail-closed，避免文件與程式碼下方實際測試意圖不一致
- [x] 8.1.1 [PQLE-001] 單一實例取得鎖並執行推進
- [x] 8.1.2 [PQLE-002] 多實例同時輪詢，僅一個實例執行本輪推進
- [x] 8.1.3 [PQLE-003] 未取得鎖的實例不影響下一輪重新競爭
- [x] 8.2.1 [PQLE-004]（新增服務層整合測試，見上方被測主體修正說明；元件層既有測試 `ReleaseAsync_AfterNormalAcquire_AllowsImmediateReacquisitionByAnotherCaller` 保留不動）正常完成後主動釋放鎖——前置：單一 `PurchaseQueueAdmissionService` 實例，該輪有活動需要處理；觸發：呼叫該實例的輪詢方法，讓它走完整流程（取得鎖 → 執行 Decision 4/5 的推進與校正 → 釋放鎖）；斷言：輪詢完成後，Redis 對應鎖 key 已被刪除，第二個模擬 `PurchaseQueueAdmissionService` 實例的下一次輪詢可成功取得鎖並執行
- [x] 8.2.2 [PQLE-005]（新增服務層整合測試，見上方被測主體修正說明；元件層既有測試 `TryAcquireAsync_AfterTtlExpiresWithoutRelease_AllowsOtherCallerToAcquire` 保留不動；**手法修正**：一般業務例外不足以模擬「未能主動釋放」——`AdvanceQueueOnceWithLeaderElectionAsync` 的 `finally` 區塊在正常情況下一定會呼叫 `ReleaseAsync`，注入例外並不能阻止這次呼叫真正送達 Redis，無法重現「鎖未被釋放」的前提）持有鎖的實例未能主動釋放，TTL 到期後鎖自動可用——**新增測試專用元件**：在本測試檔案內新增一個私有的 `IDistributedLock` 包裝器（比照既有 `ScanGate`／`BlockingEventRepository` 的既有慣例，取名如 `ReleaseSuppressingDistributedLock`）：`TryAcquireAsync` 正常轉發至真實 `RedisDistributedLock`；`ReleaseAsync` 刻意不轉發（no-op，模擬程序在真正送出 Redis `DEL` 之前就已當機，Redis 端的鎖 key 不會被刪除）。前置：實例 A 使用此包裝鎖，以短 TTL（`pollingIntervalSeconds`／`lockTtlMultiplier` 皆設為 1，TTL = 1 秒，同既有 PQLE-006a 手法）呼叫 `AdvanceQueueOnceWithLeaderElectionAsync` 並讓其正常執行完畢（含呼叫到包裝器的 `ReleaseAsync`，但未真正送達 Redis，Redis 端鎖 key 依然存在）；觸發：等待超過 TTL 後，實例 B（使用正常的 `SpyDistributedLock`）呼叫 `AdvanceQueueOnceWithLeaderElectionAsync`；斷言：B 的 `TryAcquireAsync` 回傳 `Acquired`（TTL 到期後 Redis 自動視為可用），B 完整執行本輪推進並正常釋放，不需任何實例手動介入
- [x] 8.2.3 [PQLE-006]（新增服務層整合測試，見上方被測主體修正說明；元件層既有測試 `ReleaseAsync_WithStaleOwnerTokenAfterAnotherCallerAcquiredNewLock_IsNoOp` 保留不動；**手法修正**：需要「A 的鎖已 TTL 到期」「B 已取得新鎖且尚未釋放」「A 此時才執行遲到的釋放」三個條件同時成立，不能依賴時序巧合，需要兩個獨立可控制的同步點，且必須明確規定各實例分別注入哪一個 gate，不能只描述概念）已逾時釋放的鎖不可被原持有者誤釋放新的持有者——比照既有 PQLE-006a／PQLE-003 使用的 `ScanGate` + `BlockingScopeFactory` 機制，建立兩個**各自獨立**的 `ScanGate` 與 `BlockingScopeFactory` 執行個體：`gateA = new ScanGate()`、`blockingScopeFactoryA = new BlockingScopeFactory(realScopeFactory, gateA)`；`gateB = new ScanGate()`、`blockingScopeFactoryB = new BlockingScopeFactory(realScopeFactory, gateB)`（`realScopeFactory` 為 `_factory.Services.GetRequiredService<IServiceScopeFactory>()`，兩個 `BlockingScopeFactory` 皆包裝同一個底層 factory，但各自持有不同的 gate 執行個體，`BlockingScopeFactory` 的 gate 為建構子注入的私有欄位，不會互相干擾）。步驟：(1) `var serviceA = CreateService(lockA, maxConcurrentAdmittedBuyers: 1, pollingIntervalSeconds: 1, lockTtlMultiplier: 1, scopeFactory: blockingScopeFactoryA)`（TTL = 1 秒，同 8.2.2 手法），啟動 `var taskA = serviceA.AdvanceQueueOnceWithLeaderElectionAsync(...)`，**MUST** `await gateA.WaitUntilScanEnteredAsync()` 確認 A 已進入同步點（已取得鎖、尚未執行到 `finally` 的 `ReleaseAsync`）才繼續下一步；(2) 等待超過 A 的 TTL（`Task.Delay(1300ms)`，同 8.2.2／既有 PQLE-006a 手法），Redis 端自動視為 A 的鎖已釋放；(3) `var serviceB = CreateService(lockB, maxConcurrentAdmittedBuyers: 1, scopeFactory: blockingScopeFactoryB)`，啟動 `var taskB = serviceB.AdvanceQueueOnceWithLeaderElectionAsync(...)`，**MUST** `await gateB.WaitUntilScanEnteredAsync()` 確認 B 已進入同步點（已成功取得新鎖、尚未執行到 `ReleaseAsync`）才繼續下一步——此時鎖已歸屬 B，且 A、B 分別卡在各自獨立的 gate，互不影響；(4) 呼叫 `gateA.Release()` 讓 A 完成其（遲到的）`ReleaseAsync` 呼叫（呼叫真實 `RedisDistributedLock.ReleaseAsync`，帶 A 自己過期前的 `ownerToken`），`await taskA`；斷言：A 的這次釋放為 no-op，直接查 Redis 驗證對應鎖 key 仍存在且值仍是 B 的 `ownerToken`（不只看 A 呼叫的回傳值），此時 B 仍卡在 `gateB`、尚未完成的推進不受影響；(5) 呼叫 `gateB.Release()`，`await taskB`，讓 B 完成其餘流程並正常釋放鎖，避免影響後續測試
- [x] 8.3 [PQLE-006a]（**取代**既有方法 `AdvanceQueueOnceWithLeaderElectionAsync_WhenLockTtlExpiresWhileHolderStillExecuting_OverlapDoesNotExceedAdmissionLimit`，見上方衝突清單）整合測試：驗證重疊執行期間 Redis Lua Script 序列化執行、最終有效入場數不超額；**並新增斷言**：重疊窗口內「Redis 已推進、Postgres 尚未落地」的過渡態，在 `pending` 標記存活期間不會被另一實例同時執行的 4.4 校正誤判為不一致而清除
- [x] 8.4 [PQLE-007]（**取代**既有方法 `AdvanceQueueOnceWithLeaderElectionAsync_WhenRedisUnreachable_ExecutesFullAdvanceWithoutReleasingAndLogsWarning`，見上方衝突清單）整合測試：(a) Redis 無法連線時跳過本輪推進與校正（原驗證「照常執行」，改為驗證「跳過並記錄 Warning」），斷言 Postgres 排隊紀錄狀態不變；(b) 模擬 `EVAL` 呼叫逾時但 Script 其實已在 Redis 端執行成功，斷言 App 不重試呼叫、本輪不做任何 Postgres `UPDATE`；並明確驗證收斂時間點（本輪審查修正，避免誤導為「下一輪就收斂」）：在 `pending` 標記存活期間，即使又執行了數輪校正，該筆紀錄仍維持 Postgres `Waiting`／Redis `admitted` 的合法過渡態、MUST NOT 被提前清除或落地；只有在 `pending` TTL 到期後的下一次成功校正，才會以條件式 `UPDATE` 完成該筆紀錄落地為 `Admitted`
- [x] 8.4a [PQLE-007]（本輪審查要求，補齊 8.4(b) 未涵蓋的「過程中不重複推進、不超額」與可觀測性）整合測試：延續 8.4(b) 的情境（`EVAL` 逾時但 Script 其實已在 Redis 端執行成功，entry 已從 `waiting` 移到 `admitted` 並寫入 `pending` 標記），在 `pending` TTL 存活期間額外執行數輪完整的入場推進（含對同一活動的其他 `Waiting` 紀錄嘗試推進）；斷言：(a) 該筆過渡態 entry 不會被 `ZPOPMIN` 重複選中、不會被二次推進；(b) 該活動的有效名額計算全程不超過設定上限；(c) TTL 存活期間每一輪校正跳過此筆的行為僅記錄一般正常流程（不因此記錄 Warning／Error，因為這是合法過渡態，不是失敗）；(d) TTL 到期後由校正完成落地那一輪，若該次條件式 `UPDATE` 本身又失敗，才依 4.8 的規則記錄 Warning，連續失敗達門檻才升級 Error（與 9.15a 共用同一套斷言邏輯）
- [x] 8.5 [PQLE-008]（**取代**既有方法 `AdvanceQueueOnceWithLeaderElectionAsync_WhenRedisUnavailable_BothInstancesExecuteButAdmissionStaysWithinLimit`，見上方衝突清單；**本輪審查補充**：原描述只驗證 `Waiting` 不被錯誤推進，未驗證已逾時的 `Admitted` 紀錄同樣不受影響，見 spec.md `PQLE-008` 明確化說明）整合測試：(a) Redis 故障期間排隊維持原狀，`Waiting` 紀錄不被錯誤推進、不超額；(b) 建立一筆已超過入場逾時時間但 Postgres 仍為 `Admitted`、尚未標記的紀錄，模擬 Redis 故障後執行背景服務，斷言該筆紀錄的 Postgres 狀態仍維持 `Admitted`（MUST NOT 被標記為 `Expired`），驗證入場推進與逾時標記由同一 Lua Script 驅動、Redis 不可用時一併跳過；(c) Redis 恢復連線後，該筆逾時紀錄才由下一輪正常推進流程標記為 `Expired`，釋放的名額正常供其他 `Waiting` 紀錄使用
- [x] 8.6 [PQLE-009]（**取代**既有方法 `AdvanceQueueOnceWithLeaderElectionAsync_AfterRedisOutageRecovers_ResumesNormalMutualExclusionWithoutRestart`，見上方衝突清單）整合測試：Redis 恢復連線後回復正常推進行為（斷言改為：故障期間兩實例皆跳過、Postgres 排隊紀錄不變；恢復連線後由取得鎖的一方執行推進）
- [x] 8.7 被測主體 `ApplicationStartupWithRedisUnavailableTests.cs`：[PQLE-010] 應用程式啟動時 Redis 不可用仍可正常啟動——啟動流程本身未變（本次改動的 fail-closed 只影響輪詢期間的行為，不影響啟動階段），執行既有測試方法，斷言與既有程式碼相符，`dotnet test` 通過視為無需修改；額外確認 4.6 新增的啟動時全量校正邏輯，在 Redis 不可用時同樣不阻塞啟動（比照既有 `IConnectionMultiplexer` 非阻塞連線設定）

## 9. 測試 — Redis 鏡像一致性（`PQLE-REBUILD`，design.md Decision 4/5/8/9）

- [x] 9.1 [PQLE-REBUILD-001]（第八輪審查要求：明確驗證「不阻塞」本身，不只驗證校正結果）整合測試：建立多個 `IsQueueModeEnabled = true` 的活動、Redis 無資料（模擬剛重啟），啟動應用程式時人為讓全量校正的其中一步延遲（例如注入延遲的 Redis 呼叫或大量活動製造可觀測的校正耗時）；斷言 (a) 應用程式的啟動完成信號（例如健康檢查端點、`IHostApplicationLifetime.ApplicationStarted`）在校正邏輯仍在執行、尚未完成時就已觸發，證明啟動流程不等待校正完成；(b) 校正邏輯最終仍會完成，完成後查詢 Redis 鏡像，斷言與 Postgres 真相一致，涵蓋所有 `IsQueueModeEnabled = true` 的活動，不遺漏任何一個
- [x] 9.2 [PQLE-REBUILD-002] 整合測試：`pending` 標記存活期間，「Postgres 仍為 Waiting、entryId 已在 admitted」不被清除或打回 `waiting`
- [x] 9.3 [PQLE-REBUILD-002a]（同 7.6，用真實 Lua Script 建立過渡態）整合測試：`pending` 標記過期後，校正機制以條件式 `UPDATE` 完成落地為 `Admitted`；並驗證該筆紀錄若已不是 `Waiting`（已被其他路徑處理），條件式 `UPDATE` 為 0 列受影響、不覆寫
- [x] 9.4 [PQLE-REBUILD-003] 整合測試：`promotedIds` 批次 Postgres 寫回遇到例外時，Redis `admitted` 鏡像與 `pending` 標記維持不動（不做反向補償），交由 9.3 的條件式校正收斂
- [x] 9.5 [PQLE-REBUILD-003a] 整合測試：`expiredIds` 批次 Postgres 寫回遇到例外時，Redis 端維持不動（不做反向補償），交由 9.6 的條件式校正收斂
- [x] 9.6 [PQLE-REBUILD-003b] 整合測試：模擬逾時標記批次執行者中止或結果未知（entryId 已從 Redis `admitted` 移除但 Postgres `UPDATE` 未確定執行），驗證校正機制以條件式 `UPDATE` 完成落地為 `Expired`；並驗證該筆紀錄若已不是 `Admitted`，條件式 `UPDATE` 為 0 列受影響
- [x] 9.6a [PQLE-REBUILD-007]（本輪審查發現：步驟 5 的原版規則只清除 Postgres 狀態為 `Completed` 的殘留，遺漏 `Expired`／孤兒 member 兩種情況）整合測試：對已入場的 entry 執行正常的逾時流程——不透過本節 9.5／9.6 模擬的異常路徑，而是讓 Decision 4 的入場推進 Lua Script 正常執行、正常 `ZREM` 並回傳 `expiredIds`，接著人為讓對應的 Postgres 條件式 `UPDATE`（`Status = Expired`）成功但**刻意不同步的另一筆**（模擬混合批次中一筆生效、一筆因故被單獨補寫回 Redis `admitted`，重現「Postgres 已為 `Expired`、Redis `admitted` 仍殘留」的狀態）；執行下一次校正；斷言：該筆殘留被步驟 5 的一般化規則清除，不因「不是 `Completed`」而被略過
- [x] 9.6b [PQLE-REBUILD-007] 整合測試（模擬 Redis 重啟／資料局部遺失後的校正）：直接在 Redis `admitted` zset 中插入一筆 entryId，其對應的 Postgres 紀錄狀態為 `Expired`（模擬 Redis 重啟後從舊快照恢復、或先前某次同步異常殘留下來的髒資料）；執行校正；斷言該筆 entryId 從 `admitted` 鏡像被清除，且不影響同一活動其他合法 `Admitted`（含逾期未落地的 `A_pg_overdue`）entry 的鏡像狀態
- [x] 9.6c [PQLE-REBUILD-007] 整合測試（孤兒 entryId）：直接在 Redis `admitted` zset 中插入一個 Postgres 完全查無對應紀錄的隨機 `Guid` 字串（模擬資料被刪除或程式錯誤寫入的孤兒 member）；執行校正；斷言該 member 被清除，且不拋出例外、不影響其他合法 entry 的校正結果
- [x] 9.7 [PQLE-REBUILD-004]／[PQ-COMPLETE-003] 整合測試：訂單完成同步失敗（`ZREM` 失敗，模擬 Redis 暫時不可用）時，Postgres 端立即為 `Completed`；Redis 端名額短暫仍被佔用；Redis 恢復連線後的下一次校正正確 `ZREM` 釋放；並驗證 Redis 持續故障期間名額不會被錯誤釋放（維持保守，不超額）
- [x] 9.8 測試：`ZADD` 鏡像寫入失敗（Decision 3）不影響 `PQ-JOIN` 本身成功，且該筆紀錄能被下一輪校正機制（4.2）補上
- [x] 9.11 [PQLE-REBUILD-006]（第十輪審查要求；**本輪審查補充**：spec.md PQLE-REBUILD-006 明確要求記錄的是 Warning 等級結構化 log，測試 MUST 斷言 log 等級本身，不能只驗證「有被記錄」）整合測試：建立兩個以上 `IsQueueModeEnabled = true` 的活動，人為讓其中一個活動的處理在推進或校正階段拋出例外（例如注入該活動專屬的 Postgres 連線失敗）；執行一輪 `PurchaseQueueAdmissionService`；斷言：(a) 透過 logger sink 或可觀察的 log collector（比照既有 Serilog 整合測試手法）斷言該活動的異常產生的是 **Warning** 等級結構化 log；(b) 該活動被跳過，其餘活動仍被正常推進與校正，不因單一活動失敗而整輪中止
- [x] 9.12 [PQ-COMPLETE-003]／[PQLE-REBUILD-004]（第十輪審查要求，驗證「Redis 恢復後、下一次成功校正」邊界的完整鏈路；**本輪審查要求明確標註對應 Scenario ID，並確認斷言直接對應這兩條 Scenario 的行為承諾，不能只驗證鎖競爭本身而不回指名額釋放的保證**）整合測試：模擬某筆紀錄因 `PQ-COMPLETE` 同步失敗而短暫佔用名額（同 9.7），但額外加入鎖競爭情境——兩個 `PurchaseQueueAdmissionService` 實例都在輪詢，Redis 恢復連線後，斷言：(a) 不論哪個實例搶到鎖執行該輪，該輪的校正都會涵蓋到目標活動；(b) 目標活動的名額最終被釋放（直接查 Redis `admitted` zset 已不含該 entryId，同時查 Postgres 狀態為 `Completed`），對應 `PQ-COMPLETE-003`「Redis 端有效名額計算最終正確反映 `Completed`」與 `PQLE-REBUILD-004`「訂單完成同步失敗後、下一次成功校正完成清除」的承諾；(c) 不因鎖被哪個實例取得而遺漏
- [x] 9.13 [PQ-COMPLETE-003]／[PQLE-REBUILD-004]（本輪審查要求，統一這兩條 Scenario 與 design.md Decision 8 對「Redis 恢復後、下一次成功校正」邊界的語意，補上 9.12 未涵蓋的「取得鎖的實例中止」情境；**手法修正**：不得用可攔截的業務例外模擬「整個程序中止」——依 3.4 的規則，業務例外會被逐活動 try/catch 攔截、記錄後繼續處理下一個活動，無法證明目標活動真的沒被處理到）整合測試：建立至少兩個 `IsQueueModeEnabled = true` 的活動，其中一個是**非目標活動**（排序上先被處理）、另一個是模擬某筆紀錄因 `PQ-COMPLETE` 同步失敗而短暫佔用名額的**目標活動**（同 9.7，對應 `PQ-COMPLETE-003`）；Redis 恢復連線後，傳入一個可從測試外部控制的 `CancellationToken`，在該輪處理完非目標活動、即將開始處理目標活動之前呼叫 `Cancel()`（依 3.4，逐活動例外隔離 MUST NOT 攔截 `OperationCanceledException`，故此取消會確定性地中止整個方法呼叫，不會被误判為對目標活動的一次「已檢查」）；斷言：(a) 目標活動完全未被這一輪處理到，名額釋放尚未發生（`admitted` zset 仍含該 entryId）；(b) 下一次不帶取消、某實例成功取得鎖並且處理到目標活動時，校正正確完成 `ZREM` 並釋放名額，直接驗證此結果滿足 `PQLE-REBUILD-004`「下一次成功執行的校正」與 `PQ-COMPLETE-003`「名額最終釋放」的承諾——即使上一輪被中止，目標活動仍**持續可被下一輪重新嘗試處理**，不因曾經有一輪取得過鎖但未處理到就被視為「已檢查、不再重試」；(c) 這個過程不需要人工介入、不構成永久遺漏，呼應 design.md Decision 6「鎖競爭本身涵蓋全部活動、但單輪部分失敗不構成額外保證」的精確邊界說明
- [x] 9.13a [PQLE-REBUILD-008]（本輪審查要求：9.13 只把取消當成模擬「整個程序中止」的手法，驗證的是 `PQ-COMPLETE-003`／`PQLE-REBUILD-004` 的最終收斂承諾，沒有直接驗證取消行為本身的外部可觀察契約——3.4／`PQLE-REBUILD-008` 要求的「例外向外傳遞、後續活動不再處理、已完成結果保留」需要獨立測試）整合測試：建立至少三個 `IsQueueModeEnabled = true` 的活動 A、B、C（依處理順序排列）；傳入一個可從測試外部控制的 `CancellationToken`，在 A 處理完成、B 尚未開始處理前呼叫 `Cancel()`；斷言：(a) 呼叫 `AdvanceQueueOnceWithLeaderElectionAsync` 的 `Task` 最終以 `OperationCanceledException`（或其衍生型別）觀察到取消，不是被吞掉後正常回傳；(b) A 的推進與校正結果已正確落地（Postgres／Redis 鏡像皆已更新，不因後續取消而回滾）；(c) B、C 完全未被處理到（Postgres／Redis 鏡像狀態與取消前一致）；(d) 這不同於 9.11／`PQLE-REBUILD-006` 驗證的「業務例外只跳過單一活動」，取消是中止整輪剩餘活動，兩者的斷言與涵蓋範圍不可混用同一組測試
- [x] 9.14 [PQ-COMPLETE-003]／[PQLE-REBUILD-004]（本輪審查要求，涵蓋 design.md Decision 6 第三點「校正本身於執行期間再度遇到 Redis 逾時」與 Decision 8「重試與升級策略」，9.7／9.12／9.13 皆未覆蓋）整合測試：模擬 Redis 已恢復連線、實例成功取得鎖並開始執行校正，但校正處理到目標活動的 `ZREM`（或其他 Redis 呼叫）時再度遇到逾時（例如短暫網路抖動，非整個 Redis 再次下線）；斷言：(a) 本次校正對其餘活動／entryId 的處理不受影響，不因這次個別呼叫逾時而中止整個校正流程（呼應 `PQLE-REBUILD-006` 的單一活動隔離原則同樣適用於校正本身內部的個別操作）；(b) 目標活動**持續可被下一輪重試**，不因這次逾時就放棄或改變處理方式，且不設重試次數上限；(c) 目標活動的名額釋放延後到下一次真正成功執行完該筆 `ZREM` 的校正，滿足 `PQ-COMPLETE-003`／`PQLE-REBUILD-004` 的最終釋放承諾；不做反向補償、不記錄為需要人工介入的錯誤，連續失敗次數未達 4.8 設定的升級門檻前只記錄 Warning
- [x] 9.15 [PQ-COMPLETE-003][PQLE-REBUILD-004]（本輪審查要求，design.md Decision 8「Log 等級升級門檻」，4.8 對應測試；**單一實例範圍內驗證**，因為連續失敗次數的計數本身就明確定義為單一程序內的記憶體狀態，不涉及跨實例情境）整合測試：使用單一 `PurchaseQueueAdmissionService` 實例，將 4.8 的門檻設定為較小值（例如 3）以避免測試過慢；模擬同一筆紀錄的 `admitted` 鏡像清除連續失敗達到門檻輪數（例如注入的 Redis 呼叫在該筆 entryId 上持續逾時）；斷言：(a) 未達門檻前每一輪的失敗僅記錄 Warning；(b) 達到門檻的那一輪起，當次失敗的 log 等級升級為 Error；(c) 升級本身不影響重試邏輯——後續某輪 Redis 操作成功時，仍正常完成 `ZREM` 並釋放名額，不因曾經升級為 Error 就停止重試或需要額外的人工重設步驟；(d) 成功後該 entryId 的計數已歸零——重新製造同一 entryId 再次連續失敗時，計數重新從 0 累計（需再次達到完整門檻輪數才會再度升級為 Error），驗證 4.8 的歸零規則有被實作
- [x] 9.15a [PQ-ADMIT-006][PQLE-REBUILD-002a]（本輪審查要求：4.8 明確將「放棄的推進決策」條件式 `UPDATE` 失敗與「`ZREM` 失敗」納入同一套計數/升級機制，9.15 只測試了 `ZREM` 這一種來源，此處補齊另一來源，測試結構比照 9.15）整合測試：使用單一 `PurchaseQueueAdmissionService` 實例，將 4.8 的門檻設定為較小值（例如 3）；模擬某筆已通過 Redis 推進、`pending` 標記已過期的 entry，其 4.4 步驟的條件式 `UPDATE ... SET Status = 'Admitted' WHERE Id = entryId AND Status = 'Waiting'` 連續多輪執行失敗（例如注入該筆 Postgres 呼叫逾時）；斷言：(a) 未達門檻前每一輪的失敗僅記錄 Warning；(b) 達到門檻的那一輪起，當次失敗的 log 等級升級為 Error；(c) 升級本身不影響重試邏輯——後續某輪 Postgres 操作成功時，仍正常完成該筆 `UPDATE` 落地為 `Admitted`，不因曾經升級為 Error 就停止重試；(d) 成功後該 entryId 的計數已歸零
- [x] 9.15b [PQLE-REBUILD-003b]（本輪審查要求，理由同 9.15a，補齊「放棄的逾時標記」這一失敗來源）整合測試：使用單一 `PurchaseQueueAdmissionService` 實例，將 4.8 的門檻設定為較小值（例如 3）；模擬某筆已從 Redis `admitted` 移除但 Postgres 仍為 `Admitted` 且已逾時的 entry，其 4.5 步驟的條件式 `UPDATE ... SET Status = 'Expired' WHERE Id = entryId AND Status = 'Admitted'` 連續多輪執行失敗；斷言：(a) 未達門檻前每一輪的失敗僅記錄 Warning；(b) 達到門檻的那一輪起，當次失敗的 log 等級升級為 Error；(c) 升級本身不影響重試邏輯——後續某輪 Postgres 操作成功時，仍正常完成該筆 `UPDATE` 落地為 `Expired`，不因曾經升級為 Error 就停止重試；(d) 成功後該 entryId 的計數已歸零
- [x] 9.10（前置實作任務，design.md Decision 5「可測試性要求」）將 Decision 5 步驟 2 的原子快照讀取抽成 `PurchaseQueueAdmissionService` 上一個 **`public`** 方法 `ReadRedisMirrorSnapshotAsync(Guid eventId, IDatabase database, CancellationToken ct)`，只依賴傳入的 `IDatabase`、只做「呼叫單一 `EVAL` 原子讀取 `waiting`／`admitted` 兩個 zset 並回傳」，不摻雜差集比對或寫入邏輯；**不引入 `InternalsVisibleTo`**（本專案目前無此設定，比照既有 `archive/2026-08-17-ticketing-order-management/design.md` 對 `CleanupOnceAsync` 的既有慣例，直接用 `public` 方法）
- [x] 9.10a [PQLE-REBUILD-005]（結構性驗證，元件層；**本輪審查修正引用**：僅精確引用 `RedisCaptchaServiceTests.cs:225-245`——該處確實使用 `Mock<IDatabase>`／`Mock<IConnectionMultiplexer>` 並對 `IDatabase` 呼叫做 `Verify`，已核對行號無誤；`RedisQueryCacheTests.cs` 的既有 mock 手法僅止於讓 `IConnectionMultiplexer.GetDatabase` 拋出例外，未涉及對具體命令的 `Verify`，本次不引用它作為此手法的先例）被測主體：9.10 新增的 `PurchaseQueueAdmissionService.ReadRedisMirrorSnapshotAsync`（**不需要**建立完整的 `PurchaseQueueAdmissionService` 執行環境、不需要處理分散式鎖或 Decision 4 推進 Lua Script 的 mock 設定，直接對此方法傳入 `Mock<IDatabase>.Object` 呼叫）；斷言：(a) `mockDatabase.Invocations`／`Verify` 顯示 `ScriptEvaluateAsync`（或實際採用的 Lua 呼叫 API）恰好被呼叫一次；(b) **MUST NOT** 呼叫任何 `SortedSetRangeByScoreAsync`／`SortedSetRangeByRankAsync`（`ZRANGE`／`ZRANGEBYSCORE`）——此斷言直接、結構性地證明實作確實依 Decision 5 步驟 2 使用單一原子 `EVAL`，而非事後用「先執行再檢查結果」的間接方式推論，只要通過此斷言，`W_redis`／`A_redis` 分屬不同時間點的競態即因 Redis 對單一 Script 執行的原子性而被結構性排除
- [x] 9.10b [PQLE-REBUILD-005]（原序列情境，保留，整合層，真實 Redis）先呼叫真實入場推進 Lua Script，讓某 entryId 從 `waiting` 移到 `admitted`（模擬併發推進已完成，但刻意不執行對應的 Postgres `UPDATE`，讓 Postgres 快照仍顯示 `Waiting`）；接著執行校正；斷言該 entryId 執行校正後只存在於 `admitted` 鏡像，MUST NOT 同時出現在 `waiting` 鏡像；額外驗證：對該活動再跑一輪入場推進 Lua Script，斷言該 entryId 不會被 `ZPOPMIN` 重複選中、不會被二次推進、不會影響其他 `Waiting` 紀錄的推進結果
- [x] 9.10c [PQLE-REBUILD-005]（真實併發壓力測試，整合層，補強經驗證據，非唯一正確性依據——結構性證明見 9.10a）使用真實 Redis（Testcontainers），對同一活動建立多筆接近「即將被 `ZPOPMIN` 選中」的 `Waiting` 紀錄；以迴圈重複執行 N 次（例如 50 次）以下情境並在每次迭代後斷言：`Task.WhenAll` 同時觸發（a）一次入場推進 Lua Script 呼叫與（b）一次校正的快照讀取＋後續補齊寫入，斷言每次迭代結束後：任何 entryId MUST NOT 同時出現在 `waiting` 與 `admitted` 兩個鏡像、有效名額計算不超額、沒有任何 entryId 被 `ZPOPMIN` 選中超過一次
- [x] 9.16 [PQ-ADMIT-004]（本輪審查要求：驗證批次 `ExecuteUpdateAsync` 只回傳總影響列數、無法逐筆判定的前提下，混合批次仍能正確收斂）整合測試：對同一活動建立至少 5 筆已被 Lua Script 決定推進的 `promotedIds`（`Waiting → Admitted`），在執行本輪批次條件式 `UPDATE` **之前**，人為讓其中 3 筆的 Postgres 紀錄搶先被其他路徑處理掉（例如直接呼叫校正機制的條件式 `UPDATE` 先行落地為 `Admitted`，或直接改寫為 `Completed`／`Expired`，模擬「批次送出時前置狀態已不成立」）；執行批次條件式 `UPDATE`；斷言：(a) 批次呼叫本身正常完成、不拋例外，即使只有 2 筆實際受影響；(b) 那 3 筆搶先被處理的紀錄狀態不被覆寫（維持其搶先寫入的值，不被批次 `UPDATE` 的舊決策蓋掉）；(c) 其餘 2 筆正確推進為 `Admitted`；(d) 不觸發任何反向 Redis 補償、不記錄為錯誤等級 log；(e) Redis `admitted` 鏡像最終與 Postgres 實際狀態一致（5 筆的 Postgres 終態與 Redis 鏡像逐一核對）
- [x] 9.9 [PQ-COMPLETE-001]（新增，第七輪審查發現既有測試未涵蓋此端到端流程；第八輪審查要求明確斷言「commit 後才同步」的順序，而非僅靠「下一輪能推進」間接推論）整合測試：某活動有效名額已滿、有一筆 `Waiting` 紀錄在等待；已入場會員成功建立訂單（`OrderService.PlaceOrderAsync`）。斷言鏈需依序覆蓋：(a) Postgres 交易確實已 commit（查詢 Postgres 該排隊紀錄的 `Status` 已為 `Completed`）；(b) `admitted` 鏡像的 `ZREM` 是在交易 commit **之後**才執行（可用測試替身／攔截 `ITransaction`／`IConnectionMultiplexer` 呼叫時序，或斷言 `ZREM` 呼叫發生在 `CommitAsync` 回傳之後，確認程式碼路徑符合 2a.1 的「既有交易 commit 後」順序，而非在同一交易或交易前執行）；(c) `ZREM` 本身確實成功（直接查詢 Redis，斷言該 entryId 已不在 `admitted` zset，不只是「沒有拋出例外」）；(d) 執行下一輪 `PurchaseQueueAdmissionService`，斷言該筆 `Waiting` 紀錄被實際推進為 `Admitted`，不需等待原逾時時間

### 9a. 補充故障邊界測試（第六輪審查要求，涵蓋 admission 正確性的核心故障面，非僅可用性）

測試手法：Testcontainers Redis + Testcontainers Postgres，每項測試在動作後逐一斷言：最終 Postgres 狀態、Redis `waiting`／`admitted` 鏡像內容、當下有效名額數、每個受影響 entryId 只發生過合法的狀態轉換、不超額、不永久遺漏、不重複推進。

- [x] 9a.1 驗證 `A_pg` 與 `A_pg_overdue` 的互斥性：建立一筆 Postgres `Admitted` 且已逾時、Redis `admitted` 已不含該 entryId 的紀錄（模擬 Decision 4 逾時批次寫回中止），執行校正，斷言該紀錄只被步驟 8（4.5）處理為 `Expired`，不會被步驟 4（4.2）誤判為需要補回 `admitted`（design.md Decision 5 第六輪審查澄清）
- [x] 9a.2 單一 key 遺失：只刪除某活動的 `pending:{entryId}` key（其餘 `waiting`／`admitted` 資料正常），執行校正，斷言僅該筆放棄判定被觸發，其餘紀錄不受影響
- [x] 9a.3 單一 key 遺失：只刪除某活動的 `waiting` zset（`admitted` 正常），執行校正，斷言 `waiting` 被正確從 Postgres 重建，`admitted` 不受影響
- [x] 9a.4 單一 key 遺失：只刪除某活動的 `admitted` zset（`waiting` 正常），執行校正，斷言 `admitted` 被正確從 Postgres 重建（含逾時判斷），`waiting` 不受影響
- [x] 9a.5 部分 `FLUSH`（模擬同時清空多個活動中的其中一個活動的所有 key，其餘活動不受影響），執行校正，斷言只有受影響活動被重建，其餘活動的鏡像與有效名額不受干擾
- [x] 9a.6 Redis 命令結果未知：模擬 `ZADD`（Decision 3／Decision 5 補齊）或 `ZREM`（Decision 8）呼叫逾時但伺服器端其實已執行成功，斷言重複執行同一命令為安全 no-op，不產生重複或矛盾狀態
- [x] 9a.7 複合情境：校正執行期間，另一背景服務實例同時對同一活動執行 Lua Script 推進（`PQLE-006a` 重疊執行）且過程中 Redis 短暫斷線一次，斷言最終收斂正確（不超額、不遺漏、不重複推進），且不需要校正與 Lua Script 互相等待或線性化（呼應 design.md Decision 5「不需要嚴格線性化」的設計說明）
- [x] 9a.8 重複校正的冪等性：對同一活動連續執行兩次校正（中間沒有任何狀態變化），斷言第二次執行對 Redis／Postgres 完全無副作用（no-op）

## 10. 回歸驗證

被測主體（10.1.1～10.1.8）：`GetMyQueueStatusHandlerTests.cs`；`PQ-STATUS` 的「前方等待人數」查詢邏輯完全未變，仍是純 Postgres `JoinedAtUtc ASC, Id ASC`。**明確澄清（第八輪審查 Warning）**：`PQ-ADMIT-005` 的 Redis member 字串 tie-break **只適用於入場推進（`PQ-ADMIT`）本身**，不適用於 `PQ-STATUS` 顯示的排名計算，兩者在極少數同毫秒情況下可能有微小數字差異（design.md Decision 2／spec delta 已記錄為已知限制），但這不代表 `PQ-STATUS` 的既有測試斷言需要改成比對 Redis 順序——現有斷言（比對 Postgres 查詢結果）維持不變即可。

端點：`GET /api/events/{id}/queue/entries/me`（已登入會員呼叫，身份取自 JWT）。以下每項的「前置」皆先有一筆屬於呼叫者本人的 `PurchaseQueueEntry` 紀錄（除非另有說明），「斷言」皆包含 HTTP 200（除非另有說明）與 response body 的對應欄位。

- [x] 10.1.1 [PQ-STATUS-001]（即時推導已逾時但未落地）前置：紀錄 `Status = Admitted`，Postgres `AdmissionExpiresAtUtc <= 目前時間`（尚未被背景服務改寫為 `Expired`）；觸發：呼叫端點；斷言：回應 `status` 為已逾時，不是「已可送出訂單」；資料庫紀錄本身查詢後仍是 `Admitted`（不落地寫回）
- [x] 10.1.2 [PQ-STATUS-002]（查詢等待中）前置：紀錄 `Status = Waiting`；觸發：呼叫端點；斷言：回應 `status = Waiting`，`waitingCount` 為 Postgres 依 `JoinedAtUtc ASC, Id ASC` 計算的正確前方人數
- [x] 10.1.3 [PQ-STATUS-003]（查詢已入場未逾時）前置：紀錄 `Status = Admitted`，`AdmissionExpiresAtUtc >` 目前時間；觸發：呼叫端點；斷言：回應標示已入場、可送出訂單
- [x] 10.1.4 [PQ-STATUS-004]（查詢已逾時）前置：紀錄 `Status = Admitted`，`AdmissionExpiresAtUtc <=` 目前時間；觸發：呼叫端點；斷言：回應標示已逾時
- [x] 10.1.5 [PQ-STATUS-005]（從未加入）前置：呼叫者對該活動完全沒有任何 `PurchaseQueueEntry` 紀錄；觸發：呼叫端點；斷言：回應標示「尚未加入排隊」
- [x] 10.1.6 [PQ-STATUS-006]（僅有已完成歷史）前置：呼叫者對該活動僅有一筆 `Status = Completed` 的舊紀錄，此後未再加入；觸發：呼叫端點；斷言：回應標示「尚未加入排隊」，不是回報已完成或錯誤
- [x] 10.1.7 [PQ-STATUS-007]（活動不存在）前置：呼叫路徑的活動 Id 不存在於 Postgres；觸發：呼叫端點；斷言：HTTP 404
- [x] 10.1.8 [PQ-STATUS-008]（附帶 queueModeEnabled）前置：紀錄任意狀態（如仍為 `Waiting`），該活動 `Event.IsQueueModeEnabled` 於查詢當下為 `false`（Admin 已關閉）；觸發：呼叫端點；斷言：回應 `queueModeEnabled = false`，排隊紀錄本身狀態如實回傳（不因活動已關閉而被清理或竄改）

被測主體（10.2.1～10.2.3）：涵蓋 `PQ-WAIT`／`PQ-TOGGLE` 的既有測試（`PurchaseQueueAdmissionServiceTests.cs` 或對應測試檔，依實際程式碼結構為準）；三者的推進順序描述已從 `JoinedAtUtc ASC, Id ASC` 對齊為 `JoinedAtUtc ASC`（含 `PQ-ADMIT` 的 tie-break 契約），若既有測試斷言字面比對到 `Id ASC` 排序邏輯本身（而非僅比對推進結果集合），需同步調整比對方式為呼叫真實 Lua Script 後的實際順序，不得改變測試想驗證的行為本身。

- [x] 10.2.1 [PQ-WAIT-001]（長時間等待不自動清理）前置：某活動有一筆 `Waiting` 紀錄已存在相當長時間，該活動持續有其他 `Admitted` 名額因 `Completed`／`Expired` 被釋放（用真實 Lua Script 執行多輪推進模擬）；觸發：執行入場推進背景服務；斷言：該筆紀錄最終依 `JoinedAtUtc ASC`（含同毫秒 tie-break）順序被推進為 `Admitted`，過程中不曾被跳過或標記為 `Expired`
- [x] 10.2.2 [PQ-TOGGLE-001]（關閉後既有 Waiting 停止推進）前置：某活動已有多筆 `Waiting` 排隊紀錄，`IsQueueModeEnabled = true`；觸發：Admin 呼叫 `PATCH /api/admin/events/{id}/queue-mode` 關閉（`{ "enabled": false }`），接著執行入場推進背景服務；斷言：背景推進不處理該活動（Postgres 排隊紀錄狀態不變、不被刪除），對該活動呼叫建立訂單不再檢查排隊資格
- [x] 10.2.3 [PQ-TOGGLE-002]（重新開啟後沿用既有順序）前置：延續 10.2.2 的關閉狀態，活動仍有既有 `Waiting` 紀錄；觸發：Admin 重新開啟（`{ "enabled": true }`），接著執行入場推進背景服務；斷言：背景推進依既有 `JoinedAtUtc ASC` 順序（含同毫秒 tie-break）繼續推進這些紀錄，不要求會員重新加入排隊、不重置 `JoinedAtUtc`
- [x] 10.3 確認 `PQ-JOIN` 既有測試（`PQ-JOIN-001`～`PQ-JOIN-009`）全數通過、不需修改（`PQ-JOIN` 的 Requirement 文字與 Handler 行為保證皆未變，僅內部新增 best-effort Redis 同步副作用，不影響既有測試斷言）
- [x] 10.4 [PQ-COMPLETE-001][PQ-COMPLETE-002] `PQ-COMPLETE` 測試調整（修正第七輪審查發現的不實宣稱）：**`PQ-COMPLETE-001` 並非「既有測試已驗證、不受影響」**——核對 `OrderService.cs` 現況，交易 commit 後目前只有票種快取失效，尚未有任何 Redis admission 鏡像同步；`PQ-COMPLETE-001` 的 Scenario 文字（Redis 鏡像同步成功後、下一輪背景推進實際將名額提供給下一位等待者）是本次改動新增的行為，**必須新增**端到端整合測試：建立訂單完成 → 確認 2a.1 的 `ZREM` 成功 → 執行下一輪 `PurchaseQueueAdmissionService` → 斷言下一筆 `Waiting` 紀錄被實際推進為 `Admitted`；`PQ-COMPLETE-002`（`tests/ProjectC.WebApi.Tests/BackgroundServices/PurchaseQueueAdmissionServiceTests.cs` 既有案例）**必須修改**斷言方式為驗證 Redis `admitted` zset 已移除該 entryId（而非僅檢查 Postgres `Status` 欄位）；新增 9.7 對應的 `PQ-COMPLETE-003` 測試涵蓋同步失敗情境
- [x] 10.5 全專案測試套件（4 個測試專案）全數通過

## 11. 防禦性檢查與審查

- [x] 11.1 套用 `.claude/skills/hardener/SKILL.md` 檢查清單（Application/Infrastructure 層變更全部完成後套用一次）
- [x] 11.2 呼叫 `strict-reviewer` 審查
