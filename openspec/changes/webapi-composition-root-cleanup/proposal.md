# Proposal

## Why

`src/ProjectC.WebApi/Program.cs` 目前 574 行，Serilog 啟動、約 25 組 Options 註冊、Redis、Repository、約 40 個 Handler、驗證授權、限流（含 5 個靜態分區函式）、健康檢查與 middleware 管線全部混在同一個頂層程式區塊。每個後續 change 都要在這個檔案的中段插入註冊，審查時 diff 脈絡難讀，註冊遺漏（例如忘記 `GetRequiredService` 觸發驗證）也不易察覺。專案已進入維護期（Phase 1–3 完成），此時拆分的風險最低。

## What Changes

- 新增 `src/ProjectC.WebApi/Extensions/`，依關注點把 `Program.cs` 的服務註冊搬成 `IServiceCollection`／`WebApplication` 擴充方法（命名與切分見 design.md）。
- `Program.cs` 只保留：bootstrap logger 與 `UseSerilog`、Kestrel 全域本文上限、對各擴充方法的呼叫、`app.UseXxx` 管線順序、`MapXxx`、`Program` 類別宣告。
- 原有的設計決策註解（`design.md 決策 N` 等引用）隨對應程式碼搬移，內容不改。
- 新增 `ServiceDescriptor` 快照特徵化測試，鎖定拆分前後的服務型別、實作型別、lifetime 完全一致。
- 更正文件中的錯誤事實：`appsettings.Development.json` 被 `.gitignore`（`**/appsettings.Development.json`）排除、從未進版控，且 `docker-compose.yml` 以環境變數覆寫其全部設定值。涉及 `docs/k8s-todo.md`（第 41 行）、`CLAUDE.md`（「強制規則」第一條中的括號說明）與記憶 `project_webapi_todo`。
- **非變更**：不改任何註冊的 lifetime、不改 middleware 順序、不改任何 `.csproj`／`docker-compose.yml`、不動 `appsettings.Development.json` 本身、不動 Application／Infrastructure 的專案結構。

## Capabilities

### New Capabilities
無。

### Modified Capabilities
無。純內部重構，對外行為（HTTP 契約、設定鍵、啟動驗證、DI 圖）完全不變，因此在 `.openspec.yaml` 設定 `skip_specs: true`。行為不變的證據由 design.md 的驗證計畫與 tasks.md 的特徵化測試承擔，不為了通過驗證而發明 Requirement。

## Impact

- 程式碼：`src/ProjectC.WebApi/Program.cs`（縮減）、`src/ProjectC.WebApi/Extensions/*.cs`（新增）、`tests/ProjectC.WebApi.Tests/Startup/` 下新增快照測試與基準檔。
- 文件：`docs/k8s-todo.md`、`CLAUDE.md`（僅一句）。
- API／設定鍵／套件相依：無變動。
- Change 顆粒度：0 個 capability、tasks 約 17 項（未達約 30 項的拆分門檻），不需拆分。
