# Proposal

## Why

`GlobalExceptionHandler` 把完整例外傳給 `LogError`，Serilog 管線也沒有任何遮蔽機制。未預期例外的訊息若含密碼、連線字串或 token，會原樣進入 Console 與 Seq，違反 CLAUDE.md「不得記錄敏感資訊」。

缺口不限於 HTTP 路徑：全專案有二十多處帶例外的 `LogError`／`LogWarning`（背景服務、Redis、`UnitOfWork`、`OrderService` 等），框架（ASP.NET Core、Npgsql、EF Core）自己記錄的例外也走同一條管線。

`api-health-checks` 取得限期安全例外（2026-11-02），條件是本缺口須在正式部署 K8s 或 VPS 前補上（`docs/deployment-prerequisites.md` 第 2 項）。

## What Changes

- 在 Serilog 管線最外層加入遮蔽層：所有 sink（Console、Seq、測試用記憶體 sink）收到的 `LogEvent`，其例外已先遮蔽。
- 遮蔽對象為例外的完整文字（`Message`、`InnerException` 鏈、`AggregateException` 內層）；例外型別名稱與堆疊原樣保留（文字不超過 64 KB 時）；超過 64 KB 時只保證開頭 32 KB 內的型別與訊息、結尾 32 KB 內的堆疊，見 design.md。
- 遮蔽四類格式：連線字串 `Password=`／`Pwd=`、Bearer／JWT、`scheme://user:pass@host`、`ApiKey`／`Secret`／`Token` 的 `key=value`。命中後值換成 `***`，key 保留。格式清單集中在單一位置。
- 例外文字不超過 64 KB 且沒有任何命中時，`LogEvent` 原樣通過（超過 64 KB 一律依截斷政策處理，見 design.md），不替換例外物件，降低對既有行為與測試的影響。
- 收尾文件：`docs/deployment-prerequisites.md` 第 2 項與 `docs/exception-log-masking-todo.md` 標為已完成；`api-health-checks` 的限期例外因此失效。

不做：不改 500 回應本文、不遮蔽日誌訊息範本的屬性值、不修改現有各處 log 呼叫。

## Capabilities

### New Capabilities

無。

### Modified Capabilities

- `observability`：新增 Requirement「日誌中的例外內容須遮蔽敏感資訊」，涵蓋遮蔽範圍、格式、保留的排錯資訊，以及對所有日誌來源（HTTP、背景服務、框架）一致適用。既有「日誌不得輸出敏感資訊」Requirement 不變。

## Impact

- 程式碼：`src/ProjectC.WebApi/Logging/`（新增遮蔽 sink 與遮蔽器、調整 `SerilogConfigurator`）。不動 `GlobalExceptionHandler` 與其他 log 呼叫端。
- 測試：`tests/ProjectC.WebApi.Tests`（遮蔽器單元測試、端點與背景服務整合測試）；現有以記憶體 sink 斷言例外的測試可能受影響，見 design.md。
- 文件：`docs/deployment-prerequisites.md`、`docs/exception-log-masking-todo.md`。
- 無 API、資料庫、相依套件變更。
- Change 顆粒度：1 個 capability、預估 task 遠少於 30 項，不需拆分。
