# 對帳測試舊 helper 缺 SortedSetWhen mock（待辦）

來源：`reconcile-event-queue-complexity`（已歸檔，2026-11-10）實作期間發現，strict-reviewer 與外部審查皆列為非 blocking 建議，使用者決定另案處理。

## 現況

`tests/ProjectC.WebApi.Tests/BackgroundServices/PurchaseQueueMirrorReconciliationTests.cs` 的 `CreateFailNTimesOnSortedSetRemoveConnectionMultiplexer`（約第 1057 行，被約第 1037、1104 行的測試使用）只 mock 舊簽章 `SortedSetAddAsync(…, CommandFlags)`。服務實際呼叫的是帶 `SortedSetWhen` 的多載，Moq 對未 mock 的多載靜默回傳預設值（`false`），等於 ZADD 在該 helper 的連線上變成 no-op，卻不會報錯。

同檔新增的 `CreateSnapshotReadFailingConnectionMultiplexer`（約第 357 行）兩種多載都已 mock，可作為修法參照。

## 風險

- 使用該 helper 的測試若依賴「ZADD 有真的寫入」，會在不知情下以 no-op 通過或失敗，測試結論不可靠。
- 新 helper 只手動轉發 5 個 `IDatabase` 方法（loose mock）：日後對帳邏輯改用其他 `IDatabase` 方法時，該測試會以 null Task 失敗，而不是因目標行為失敗。

## 待辦

- [x] 確認使用舊 helper 的兩個測試（約第 1037、1104 行）是否依賴 ZADD 實際寫入；已補上 `SortedSetWhen` 多載轉發（462459b）。實測服務呼叫的是 `SortedSetWhen` 多載，舊多載未被呼叫。
- [x] 以 `mutate` 驗證：把新轉發改回 no-op 後 32 項仍通過（退出碼 1），結論是兩個測試本就不依賴 ZADD 寫入，沒有缺口需補測試；轉發僅為防止日後測試靜默失效。
- [ ] ~~評估兩個 helper 合併為共用實作~~ 決定不做（2026-11-10）：風險低、改動量大。若日後因 helper 漏轉發方法出問題，再另開待辦。
- [ ] ~~若有 change：走 propose 流程~~ 不適用：以無對應 spec 的小型改動直接處理。

## 備註

- 此為測試碼問題，不影響正式程式行為；優先度低。
- 不屬於 `reconcile-event-queue-complexity` 的範圍（該 change 為行為不變的重構，且已歸檔）。
