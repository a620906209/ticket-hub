# 例外日誌遮蔽待辦（2026-11-02；已由 `exception-log-masking` change 完成）

## 完成狀態

已由 `exception-log-masking` change 完成：`ExceptionMaskingSink` 在 Serilog 管線最外層遮蔽所有日誌來源的例外文字（含 `InnerException` 與堆疊），保留例外型別名稱與堆疊；bootstrap logger 一併納入。遮蔽範圍與格式清單見該 change 的 `design.md` 決策 3。

已知限制：只認指定格式（`password`／`passwd`／`pwd`、`api-key`／`secret`／`token` 的 key=value、`Bearer`、JWT、URL 內嵌密碼），不處理沒有 key 的裸密碼、日誌屬性值與歷史日誌。

以下為開 change 前的原始記錄。

## 缺口

`src/ProjectC.WebApi/ExceptionHandling/GlobalExceptionHandler.cs` 的 `CreateUnexpectedErrorProblemDetails` 把完整例外傳給 `LogError`，`src/ProjectC.WebApi/Logging/` 也沒有遮蔽機制。未預期例外的訊息本身若含密碼、連線字串或 token，會原樣進入日誌與 Seq，不符合專案「不得記錄敏感資訊」的規則。

此缺口影響所有端點，不是 `api-health-checks` 引入的。回應本文不受影響，500 回應只有 `status`、`title`、`traceId`。

## 目前已知

- 尚未確認實際有哪條路徑會產生含敏感資訊的例外；目前只確認沒有防護。
- `api-health-checks` 只驗證探測流程不帶入設定的密碼，以及真實 DB 失敗時日誌不含密碼，不涵蓋例外訊息本身含密碼的情況（見該 change 的 `design.md` 決策 9 與 Risks）。

## 待辦

開獨立的 change，不併入 `api-health-checks`，因為 handler 全站共用，修改會影響所有端點的日誌與排錯。使用者於 2026-11-02 核准 `api-health-checks` 的限期例外，此 change 須在正式部署 K8s 前完成（見該 change 的 `design.md`「已核准的安全規則例外」）；VPS 部署同樣須先完成（見 `docs/deployment-prerequisites.md`）。需決定：

- 遮蔽放在哪一層：`GlobalExceptionHandler`，或 Serilog 管線（可涵蓋其他直接記錄例外的程式）。
- 遮蔽範圍：連線字串的 `Password=`、token 等格式，以及內部例外（`InnerException`）與堆疊是否一併處理。
- 如何保留排錯需要的例外型別與堆疊。
