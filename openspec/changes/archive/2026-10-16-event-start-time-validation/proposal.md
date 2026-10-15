# Proposal

## Why

建立活動時，`StartAtUtc` 不帶 `Z`（例如 `2026-12-01T10:00:00`）或帶偏移（例如 `+08:00`）會讓 API 回 **500**：EF Core 寫入 `timestamptz` 時丟 `DbUpdateException`。這是 2026-10-03 實測確認的既有缺口（`docs/project-scope.md` §8 R4）。

另一個問題是 `StartAtUtc` 為 `DateTime.MaxValue` 時，Npgsql 會存成 `infinity`。讀回的值 Kind 是 Unspecified，JSON 輸出不帶 `Z`，前端會當成本地時間解讀（同節「`StartAtUtc` 極端值」）。

兩者都是錯誤輸入，應該跟 `event-sales-window` 對 `SalesStartAtUtc`／`SalesEndAtUtc` 的處理一致，回 400。

## What Changes

- 建立活動 API：`StartAtUtc` 反序列化後 MUST 為 UTC（`Kind=Utc`；本系統只有結尾為 `Z` 的字串符合，已實測），不帶時區或帶任何偏移（含 `+00:00`）時回 400，不再 500。
- 建立活動 API：`StartAtUtc` MUST NOT 為 `9999-12-31T23:59:59.9999999Z`（`DateTime.MaxValue`），否則回 400。
- Domain `Event` 建構子補上 `StartAtUtc` 必須為 UTC 的守衛，與既有販售時間欄位一致。
- **BREAKING（僅限直接呼叫 API 的客戶端）**：原本會 500 的請求改回 400；原本以 `DateTime.MaxValue` 建立成功（201）的請求改回 400。前端一律送 `toISOString()`（帶 `Z`），不受影響。

## Capabilities

### New Capabilities

（無）

### Modified Capabilities

- `event-management`：「透過管理 API 建立活動與票種」新增 `StartAtUtc` 的 UTC 與最大值規則，以及對應 Scenario。

## Impact

- 後端：`CreateEventRequestValidator`、`Event` 建構子，以及 Application／Domain 單元測試與 API 整合測試。
- 既有資料：EF 透過建構子建立 `Event`，Domain 守衛在讀取時也會執行。資料庫若已有 `StartAtUtc` 為 `infinity` 的活動，讀回 Kind 不是 UTC，活動列表會變 500。因此 tasks 要先查既有資料，並加讀回已儲存 UTC 活動的整合測試。
- **部署前提（deployment prerequisite）**：部署到任何資料庫環境前，由執行部署的人（目前是專案唯一維護者）執行 design 決策 3 的資料閘門。每個環境各自執行，任一環境檢查不為 0 就禁止部署該環境，修復並在同一環境驗證後才可部署（tasks 5.1）。目前唯一的環境是本機開發 DB，2026-10-15 已在實作前查過，結果為 0；部署當下仍要重新執行。
- 測試 fixture：直接 `new Event(...)` 的測試若用了非 UTC 時間會開始失敗，須一併修正。
- 前端、API 回應格式、資料庫 schema：不變。
