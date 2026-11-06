# Proposal

## Why

`OrderService.PlaceOrderCoreAsync`（`src/ProjectC.Application/Orders/OrderService.cs:138-464`，約 330 行）循環複雜度 48，超過 `CA1502` 預設門檻 25（`.editorconfig` 只將其嚴重度設為 warning），CI 每次建置都出現此警告，也不符 CLAUDE.md「多層巢狀分支應拆分」。同一方法混合了交易外驗證、販售期間／實名／限購閘門、鎖內資格與庫存鎖定、commit 與 commit 後的 Redis 同步 8 個階段，日後修改限購、庫存或交易邏輯時不易閱讀、容易改壞。專案處於維護期且下單相關測試密集（`OrderServiceTests` 共 100 個測試方法（97 Fact + 3 Theory，執行案例數更多），其中 76 個為 `PlaceOrderAsync_*`，另有 Infrastructure.Tests 的 `OrderService*Tests`），是拆分風險最低的時機。

## What Changes

- 把 `PlaceOrderCoreAsync` 拆成 `OrderService` 內的 private 方法（階段切分見 design.md），方法本身退為依序呼叫各階段的協調者；不新增類別、不改建構子、不動 DI。
- 階段間的中間資料（票種字典、驗證用座位字典、座位配置、交易外讀到的活動等）以 private record 傳遞；階段提前失敗沿用檔內 `GetSeatPlacementErrorOrNull` 的 `Error?` 回傳慣例（需要回傳資料的階段用既有 `Result<T>`）。
- 拆分前先對照既有測試補齊被搬動分支的特徵化測試（在現有程式碼上就會通過）。
- 完成後更新 `docs/project-scope.md`「已知技術債：下單核心方法複雜度」條目。
- **非變更**：不改任何對外行為（HTTP 契約、錯誤類型與訊息、鎖定順序、交易邊界、日誌欄位）、不新增類別、不調整 `.editorconfig` 門檻、不處理 `PurchaseQueueAdmissionService.ReconcileEventQueueAsync` 的 `S3776`（另案）。

## Capabilities

### New Capabilities
無。

### Modified Capabilities
無。純內部重構，下單的對外行為不變，因此在 `.openspec.yaml` 設定 `skip_specs: true`；行為不變的證據由 design.md 的不變量清單與 tasks.md 的特徵化測試、突變驗證承擔，不為通過驗證而發明 Requirement。

## Impact

- 程式碼：`src/ProjectC.Application/Orders/OrderService.cs`（僅 `PlaceOrderCoreAsync` 內部拆分；`PlaceOrderAsync`、`ConfirmOrderAsync`、`CancelOrderAsync` 等其餘成員不動）；測試：`tests/ProjectC.Application.Tests/Orders/OrderServiceTests.cs` 視缺口補特徵化測試，以及 `tests/**/TestSupport/Fake*.cs`（Fake 皆 sealed，鎖定順序測試需加呼叫順序紀錄掛點，只加紀錄與可選的、預設關閉的極短延遲，不改既有預設行為）。
- 文件：`docs/project-scope.md` 技術債條目。
- API／設定鍵／套件相依／DI 圖：無變動。
- Change 顆粒度：0 個 capability、tasks 約 20 項，未達拆分門檻，不需拆分。
