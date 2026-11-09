# 對帳測試舊 helper 缺 SortedSetWhen mock（待辦）

來源：`reconcile-event-queue-complexity`（已歸檔，2026-11-10）實作期間發現，strict-reviewer 與外部審查皆列為非 blocking 建議，使用者決定另案處理。

## 現況

`tests/ProjectC.WebApi.Tests/BackgroundServices/PurchaseQueueMirrorReconciliationTests.cs` 的 `CreateFailNTimesOnSortedSetRemoveConnectionMultiplexer`（約第 1057 行，被約第 1037、1104 行的測試使用）只 mock 舊簽章 `SortedSetAddAsync(…, CommandFlags)`。服務實際呼叫的是帶 `SortedSetWhen` 的多載，Moq 對未 mock 的多載靜默回傳預設值（`false`），等於 ZADD 在該 helper 的連線上變成 no-op，卻不會報錯。

同檔新增的 `CreateSnapshotReadFailingConnectionMultiplexer`（約第 357 行）兩種多載都已 mock，可作為修法參照。

## 風險

- 使用該 helper 的測試若依賴「ZADD 有真的寫入」，會在不知情下以 no-op 通過或失敗，測試結論不可靠。
- 新 helper 只手動轉發 5 個 `IDatabase` 方法（loose mock）：日後對帳邏輯改用其他 `IDatabase` 方法時，該測試會以 null Task 失敗，而不是因目標行為失敗。

## 待辦

- [ ] 確認使用舊 helper 的兩個測試（約第 1037、1104 行）是否依賴 ZADD 實際寫入；若是，補上 `SortedSetWhen` 多載的 mock 轉發。
- [ ] 以 `scripts/verify-test-evidence.sh mutate` 證明補完後測試在行為被破壞時會失敗（缺口才補測試，證據貼原文）。
- [ ] 評估兩個 helper 是否改為共用同一個「轉發至真實連線、只對特定呼叫注入失敗」的實作，避免手動轉發漏掉方法；改動量大時另開 change，須評估是否符合 CLAUDE.md「Change 顆粒度」。
- [ ] 若有 change：依 CLAUDE.md 流程走 propose → spec-reviewer → design-hardener；純測試碼改動可標 `skip_specs: true`。

## 備註

- 此為測試碼問題，不影響正式程式行為；優先度低。
- 不屬於 `reconcile-event-queue-complexity` 的範圍（該 change 為行為不變的重構，且已歸檔）。
