# Design

## Context

動機見 proposal.md「Why」。現況（`src/ProjectC.Infrastructure/Persistence/UnitOfWork.cs`）：

- `BeginTransactionAsync`：以 `GetDbConnection().State == Closed` 判定 `ownsConnection`；是則先 `Database.OpenConnectionAsync`，再 `Database.BeginTransactionAsync`，開始失敗時沒有處理。
- `OpenConnectionAsync`：有進行中交易，或 `State != Closed` 時拋出 `InvalidOperationException`。
- `UnitOfWorkTransaction` 已在提交／回滾成功後與釋放時只關一次自有連線（`unit-of-work-commit-close-failure` 決策 1、2、5），本 change 不改這部分。
- 手動開啟連線只有 `IUnitOfWork.OpenConnectionAsync` 一個入口，目前唯一的呼叫端是 `OrderService.cs:149`。`UnitOfWork` 是 scoped，與 `DbContext` 同生命週期，同一 request 內的記錄可靠。

EF Core 開啟計數的推論（依 `RelationalConnection` 的行為，未讀原始碼，tasks 1.1 以探測證實）：

- `Database.OpenConnectionAsync` 與 `BeginTransactionAsync` 內部的開啟各使計數 +1；連線已開啟時不會重新開啟，只加計數。
- `CloseConnectionAsync` 先減計數，減到 0（或原本就是 0）且連線由 EF 開啟時，才實際關閉。
- 開始交易失敗時，內部那次 +1 沒有減回，所以工作單元關一次後計數停在 1，連線仍為 `Open`（前一 change 決策 3 的觀察）。
- 關閉途中失敗（關閉 interceptor 拋出）時計數已減到 0，連線維持 `Open`，下一次關閉會再嘗試實際關閉。

## Goals / Non-Goals

**Goals:**

- 連線歸屬只依工作單元自己的記錄判斷；`BeginTransactionAsync` 與 `OpenConnectionAsync` 共用同一份記錄。
- 由工作單元負責的連線，在開始交易失敗後一定關閉（關閉本身失敗除外）。

**Non-Goals:**

- 呼叫端持有連線時，開始交易失敗所殘留的 EF 計數（會讓該連線留到 `DbContext` 釋放）。若要處理，就得在呼叫端的連線上多關一次，而呼叫端可能還在使用這條連線（下單主路徑），不值得冒這個險；spec 明載。
- 不改提交／回滾／釋放的關閉時點與失敗處理。

## Decisions

### 決策 1：`UnitOfWork` 記錄呼叫端連線，不以狀態推斷

`UnitOfWork` 新增私有欄位 `_hasCallerConnection`：

- `OpenConnectionAsync` 成功開啟後設為 true。
- `UnitOfWorkConnection` 建構時接收一個回呼，`DisposeAsync` 第一次執行時呼叫它，把欄位設回 false。回呼在關閉連線之前呼叫，因此即使關閉失敗，記錄也會重設；之後遺留的開啟連線由決策 2、3 接手。

釋放順序沿用既有規則，不新增契約：交易未完成（未提交、未回滾）時，交易須先釋放（回滾）再釋放連線，這是 `IUnitOfWork.cs:19` 既有的 `await using` 宣告順序要求；交易提交或回滾後，連線可以在交易物件釋放前先釋放，`OrderService.cs:447` 提交後即釋放連線，避免之後的 Redis I/O 占用連線池，既有測試 `DisposeAsync_AfterCommitBeforeTransactionDisposed_ClosesConnectionAndTransactionDisposeDoesNotThrow` 保護此時序。兩種順序下，回呼重設記錄都不影響該交易：交易的 `ownsConnection` 在開始時已決定（呼叫端連線存在時為 false）。`IUnitOfWorkConnection` 的 XML doc 補寫「交易提交或回滾後可先釋放」，讓這個已存在的用法成為明文。

`OpenConnectionAsync` 的檢查改為：有進行中交易時拋出（不變）；`_hasCallerConnection` 為 true 時拋出（取代 `State != Closed`）；其餘情況呼叫 `Database.OpenConnectionAsync`。連線因先前失敗仍開啟時，這一步只加計數，呼叫端釋放時減到 0 並實際關閉（UOW-TX-024）。

- 替代方案「保留狀態判斷，另加一個『遺留連線』旗標」：歸屬就有兩個判斷來源，每個關閉失敗點都要維護旗標；否決。
- `OpenConnectionAsync_WhenAlreadyOpen_ThrowsInvalidOperationException`（同一工作單元重複開啟，UOW-TX-027）改由 `_hasCallerConnection` 判定，仍拋出；`OpenConnectionAsync_WhenTransactionInProgress_ThrowsInvalidOperationException`（UOW-TX-028）不變。兩者為既有測試，作為行為保護。

### 決策 2：沒有呼叫端連線時，開始交易一律由工作單元開啟並負責連線

`ownsConnection = !_hasCallerConnection`。為 true 時一律呼叫 `Database.OpenConnectionAsync`：連線已因先前失敗而開啟時，這一步只加計數，提交後由工作單元關閉，計數歸 0 並實際關閉。EF 交易的提交不再觸發關閉，關閉失敗依既有規則記 Warning（UOW-TX-022、023）。修正前此時判定為非自有，由 EF 在提交時關閉，關閉失敗會從 `CommitAsync` 傳出，即 §8 ⑥。

手動開啟在呼叫 EF 前先檢查取消權杖：EF 對已開啟的連線只加計數、不檢查 token，接手遺留連線時取消會被靜默吞掉（修正前此時拋 `InvalidOperationException`，第 4 輪對抗式審查探測）。開始交易不另加檢查：取消會在 EF 開始交易時拋出，再依決策 3 關閉（探測確認遺留連線上亦成立）。（UOW-TX-031）

### 決策 3：開始交易失敗時重複關閉至 `Closed`，限定自有連線、上限 3 次

`ownsConnection` 為 true 且 `Database.BeginTransactionAsync` 拋出任何例外（含 `OperationCanceledException`）時：

- 在 catch 內呼叫 `CloseConnectionAsync`，直到 `State == Closed`。
- 單次關閉拋出例外時記 Warning（「Failed to close the unit of work connection after the transaction failed to start.」），並停止重試，因為狀態已無法判斷。
- 這裡讀 `State` 只是判斷自有連線是否已關閉（關閉進度），不是判斷歸屬；歸屬在進入此段前已由 `_hasCallerConnection` 決定。
- 嘗試 3 次仍未關閉時，記 Warning（「Connection remained open after the unit of work transaction failed to start.」）。
- 最後以 `throw;` 重拋開始交易的原例外，保留堆疊。

為何現在可以這樣做：前一 change 決策 3 否決「重複關閉直到關閉」，理由是如果 EF 日後在開始失敗時自行減回計數，多關一次會關掉呼叫端開啟的連線。本 change 只在 `_hasCallerConnection` 為 false 時重試，此時不存在呼叫端連線，沒有誤關的對象；每次關閉前都先檢查狀態，EF 行為改變時迴圈第一次就停下，不會多關。

`Database.OpenConnectionAsync` 本身失敗或被取消（在 try 之前）時不處理：依 EF 行為，實體開啟本身失敗時不增加計數、沒有遺留（推論，未另行探測；連線已開啟時的接手不做實體開啟，只加計數，不會在此失敗）。實作後對抗式審查探測：實體連線已開啟、之後才在 `ConnectionOpenedAsync` interceptor 拋出時，連線會遺留為 `Open` 直到 `DbContext` 釋放；`src/` 未註冊任何連線或交易 interceptor，正式環境碰不到，不處理。

為何上限是 3：依推論，自有連線開始失敗後計數最多為 2（工作單元開啟一次、EF 開始交易時一次），第 2 次關閉即可關掉；多留 1 次為純餘裕（第 5 輪對抗式審查實測：遺留連線接手後開始失敗也只需關 2 次，上限改為 2 時現有測試全數通過，第 3 次沒有測試能區分）。上限防止無窮迴圈。原先推論這條 Warning 沒有注入點可觸發；第 2 輪對抗式審查探測推翻：呼叫端連線上開始交易失敗兩次（Non-Goal，每次殘留計數 1），釋放呼叫端連線後計數為 2、連線仍開啟，再於自有交易開始失敗時計數為 4，關 3 次後仍為 `Open`，記這筆 Warning，連線留到 `DbContext` 釋放。觸發前提是 Non-Goal 的計數殘留，影響與該 Non-Goal 相同；spec 不保證此 Warning，目前無測試。tasks 1.1 (d) 探測「遺留連線接手後開始失敗」所需的關閉次數，確認上限足夠。

catch 內的 `CloseConnectionAsync` 不收 `CancellationToken`，已取消的 token 不影響關閉。

上述計數行為屬 EF Core 內部實作（目前 10.0.11），不是公開契約；實作註解寫明此依賴，升級 EF 時以 UOW-TX-018～026 作為回歸偵測。

- 替代方案「改用 ADO.NET 自行開始交易，再以 `UseTransactionAsync` 交給 EF」：EF 不再擁有交易，提交、回滾、釋放的路徑與既有 15 條 Scenario 都要重新驗證，影響面遠大於本問題；否決（Grill Q3）。
- 取消的模擬：連線開啟前就已取消的 token 會讓 `OpenConnectionAsync` 在開啟前拋出，不會遺留連線，所以不是要驗證的情境。真正會遺留的是「開啟後、開始交易途中」被取消，無法用真實 token 穩定打中這個時間點，因此 UOW-TX-019 以交易 interceptor 在 `TransactionStartingAsync` 拋出 `OperationCanceledException` 模擬。

### 決策 4：測試注入方式

- `UnitOfWorkTests` 內的 private `ThrowingTransactionInterceptor` 新增 `startFailure` 參數（覆寫 `TransactionStartingAsync`），用於 UOW-TX-018～021。
- `TestSupport` 新增 `SwitchableCloseInterceptor`：`IsFailing` 為 true 時在 `ConnectionClosingAsync` 拋出指定例外。測試要先讓一次提交後的關閉失敗、留下開啟的連線，再恢復正常或維持失敗（UOW-TX-022～025）；`ThrowingCloseInterceptor` 會一律失敗，做不到這種切換，所以既有的維持不動。
- UOW-TX-026 以 `ThrowingCloseInterceptor`（一律失敗）讓呼叫端連線釋放時關閉失敗，之後的提交關閉也失敗：回呼若放在關閉之後或只在關閉成功時呼叫，記錄會卡在 true，交易判為非自有、由 EF 在提交時關閉並拋出，測試失敗；修正前同樣因判為非自有而失敗（RED）。
- UOW-TX-021、023、025、027、028 在修正前就會通過，作為行為保護（021、025 另於 3.2 以變異檢查證明能失敗）：021 防止重複關閉誤關呼叫端連線；023 確認接手後成功路徑仍會關閉；025 確認 `UnitOfWorkConnection` 釋放後記錄會重設。少了回呼的話，修正後會判定為非自有，由 EF 在提交時關閉，關閉失敗便會從 `CommitAsync` 傳出。

### 決策 5：主 spec Purpose 於歸檔時手動更新

delta 無法修改既有 capability 的 Purpose，歸檔同步也會原樣保留。主 spec 現行 Purpose 只描述「釋放時」的行為，已不符合前一 change 加入的提交／回滾關閉，也不符合本 change 的開始交易與歸屬判斷。歸檔同步後，手動把 `openspec/specs/unit-of-work-transaction/spec.md` 的 Purpose 改為：

> 定義工作單元交易與連線的生命週期：開始交易時依工作單元的記錄判斷連線歸屬，開始失敗、提交或回滾完成、釋放時關閉由工作單元負責的連線；未完成的交易於釋放時自動回滾，各階段的關閉或回滾失敗不蓋掉呼叫端的結果或原例外。

### 決策 6：交易未完成時釋放呼叫端連線記錄 Warning

實作後對抗式審查探測到：呼叫端在交易未完成時先釋放呼叫端連線（違反 `IUnitOfWorkConnection` doc 的順序），之後提交時連線由 EF 關閉，關閉失敗會從 `CommitAsync` 傳出，重現 §8 ⑥。使用者決定加防呆。

- `UnitOfWorkConnection.DisposeAsync` 在 `CurrentTransaction` 不為 null 時記一筆不帶例外的 Warning，之後照常關閉（計數減 1，實體連線留給交易，提交或回滾時由 EF 關閉）。
- 只記錄、不拋出也不改變關閉行為：dispose 常在例外展開途中執行，拋出會蓋掉原例外；跳過關閉則計數殘留，連線留到 `DbContext` 釋放。
- 不在此處改為由工作單元接手交易的連線：會讓交易物件的歸屬在交易進行中改變，超出本 change 範圍；契約違反仍以 Warning 讓問題在 log 可見。
- 正常順序（交易提交／回滾後、或交易先釋放後才釋放連線）時 EF 已清除 `CurrentTransaction`，不記錄（UOW-TX-030）。
- 029 斷言提交後連線已關閉：只記 Warning 而跳過關閉的錯誤實作會讓計數殘留（第 4 輪對抗式審查以變異證實原測試抓不到）。

## Risks / Trade-offs

- [探測結果與推論不符，例如開始失敗後第 2 次關閉仍為 `Open`，或關閉失敗後計數不為 0] → tasks 1.1 的停止條件：停止並回報，回到 design 重新決定，不改用其他手段硬造。
- [呼叫端持有連線時，開始交易失敗後該連線留到 `DbContext` 釋放] → Non-Goal，spec 明載；呼叫端都以 `await using` 包住，例外發生時 request 隨即結束，`DbContext` 也一併釋放。計數殘留後，同一工作單元之後的自有交易與呼叫端連線也不再實際關閉（「提交後立即歸還連線」一併失效），影響同樣止於 `DbContext` 釋放。
- [開始交易途中實體連線被斷開（狀態已是 `Closed`）後拋出] → 決策 3 的迴圈第一次判斷即停下，EF 計數殘留，之後同一工作單元的交易提交後連線不再關閉、也不記 Warning，直到 `DbContext` 釋放。Npgsql 延遲送出 BEGIN，斷線通常不發生在這一步，不處理。
- [呼叫端違反契約，在交易未完成時先釋放呼叫端連線] → 提交時關閉失敗仍會從 `CommitAsync` 傳出（§8 ⑥ 情境）。目前沒有呼叫端這樣用；決策 6 在釋放時記 Warning，讓違反可在 log 看見，但不阻止例外傳出；此時 `CommitAsync` 拋出後交易未標記完成，釋放交易會再嘗試回滾已提交的交易，多記一筆回滾失敗的 Warning（第 2 輪對抗式審查探測確認）。
- [交易物件釋放本身失敗後，EF 可能未清除 `CurrentTransaction`] → 依 EF 原始碼推論（第 3 輪對抗式審查，未實測，找不到注入點），之後即使依正確順序釋放呼叫端連線，也會多記一筆決策 6 的 Warning。只影響 log 判讀，不影響資料或連線歸還，不處理。
- [接手的遺留連線實體已損毀] → 後續查詢失敗會走一般例外路徑；連線損毀時 Npgsql 直接丟棄、不回連線池，與既有關閉失敗的處理一致。
- [`OpenConnectionAsync` 遇到遺留連線時不再拋出] → 行為契約改變（proposal 已標明）。修正前這種情況是誤判，沒有呼叫端依賴這個拋出。
- [`UnitOfWorkConnection` 漏呼叫回呼或只在關閉成功時呼叫，記錄卡在 true，之後的交易都判為非自有] → UOW-TX-025（未呼叫）與 UOW-TX-026（關閉失敗時未呼叫）抓得到；回呼放在 `_disposed` 防重入之後，重複釋放不會重複呼叫。

## Migration Plan

純程式修正，無 schema、設定或 API 變更；直接部署，回退即還原該 commit。
