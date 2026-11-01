# graphify 收尾待辦（2026-11-01）

狀態：**已完成**（2026-11-01）。第 1 項的確切原因查不出來，已決定不再追查。工具版本 graphify 0.9.97。

| # | 項目 | 狀態 |
| --- | --- | --- |
| 1 | 節點數從 6272 變成 12118 的原因 | 已結案：原因無法查證，保留 archive |
| 2 | 舊節點「待決：ASPNETCORE_ENVIRONMENT…」查不到 | 已結案：節點還在 |
| 3 | `post-commit` 自動更新 | 已結案：真實 commit 驗證通過 |
| 4 | `.sql` 未進圖譜 | 已處理 |
| 5 | 30 個程式檔沒有符號 | 已確認為工具限制，不處理 |
| 6 | Claude Code guard hook 是否仍注入圖譜提示 | 已結案：正常，提示有節流 |
| 7 | 查詢準確度 | 已結案：改查詢方式，不改圖譜，規則已寫入 `CLAUDE.md` |

## 1. 節點數翻倍

- 舊的 `graph.json` 已被覆蓋，也不進版控，無法比對，**確切原因查不出來**。
- 目前 12119 個節點，依 `file_type`：code 6250、document 5643、concept 211、rationale 14（在加入 SQL 解析前統計，當時總數 12118）。
- 節點最多的來源是 `openspec/changes/archive`，共 3803 個，約占 31%。
- 推測：舊圖譜涵蓋的檔案較少，這次把更多 Markdown 納入。舊 `manifest.json` 190KB、新的 287KB 符合這個方向，但不是證明。
- 決定：先保留 archive，因為設計取捨都記錄在裡面。如果查詢結果常被 archive 蓋掉，再用 `.graphifyignore` 排除。

抽樣 5 個查詢（`--budget 4000`），看輸出節點中 archive 與 `src/` 的數量：

| 查詢 | 輸出節點 | archive | `src/` |
| --- | --- | --- | --- |
| 下單流程 PlaceOrder 如何避免超賣 | 95 | 30 | 25 |
| refresh token 登出撤銷 | 91 | 33 | 25 |
| 排隊入場 leader election Redis 分散式鎖 | 66 | 12 | 13 |
| UnitOfWork 交易 rollback dispose | 74 | 9 | 7 |
| QR code 票券兌換驗簽 | 91 | 26 | 43 |

合計 417 個節點，archive 110 個（約 26%）、`src/` 113 個，archive 占比沒有超過整體的約 31%，也沒有蓋掉 `src/`。這只比較數量，沒有逐一判斷節點是否切題。

可能原因（都無法用舊圖譜驗證）：

| 可能性 | 評估 | 依據 |
| --- | --- | --- |
| 舊圖譜沒有 Markdown 標題節點，這次更新才加入 | 最可能 | 目前 5650 個 document 節點全來自 344 個 `.md`，扣掉後約 6476，接近舊的 6272；舊 `manifest.json` 也只有新的約 66%，接近非 `.md` 檔的占比（833／1177，約 71%） |
| 初次建置與 `graphify update` 的流程或工具版本不同 | 可能，常與上一項同時成立 | 版本在第一次更新前已是 0.9.97，但 10-29 建置時的版本沒有紀錄 |
| 程式碼或文件大幅新增 | 可排除 | 10-29 之後只多 5 個檔案的變更，例如 `docs/k8s-todo.md` 只有 27 個節點 |

**結案**：保留 archive，不再追查翻倍原因。之後若查詢常被 archive 蓋掉，再用 `.graphifyignore` 排除。

## 2. 舊節點名稱

- 節點仍在 `docs/k8s-todo.md` 底下，解析 JSON 後查得到原標題。
- 先前用 `grep` 搜尋原文得到 0 筆，是因為 `graph.json` 把中文跳脫編碼：實際內容為 `待決：ASPNETCORE_ENVIRONMENT`。
- 注意：`graphify explain "k8s-todo.md"` 回報找不到節點，查文件內容要用 `graphify query` 或節點標題。

## 3. post-commit 自動更新

- 已用真實 commit `460a822` 驗證：`pre-commit` 通過後，背景重建完成，`graph.json` 的 `built_at_commit` 等於 `460a822`，日誌 `~/.cache/graphify-rebuild.log` 最後一次重建顯示「Rebuilt: 12126 nodes, 34001 edges, 523 communities」。
- 注意：`pre-commit` 需要 api 容器運行，否則 commit 會被擋下，也就不會觸發 `post-commit`。

## 4. `.sql` 未進圖譜

- 已執行 `uv tool install --force "graphifyy[sql]==0.9.97"`，加入 `tree-sitter-sql` 0.3.11，版本不變。這是本機工具環境的設定，換機器要重做。
- `loadtest/sql/db-waits.sql` 現在有檔案節點，缺依賴的警告也消失了。
- 這個檔案沒有定義任何符號，所以改列入第 5 項的清單。

## 5. 沒有符號的程式檔

重新更新後，graphify 回報 30 個檔案。工具輸出的清單有截斷，以下 21 個是用「節點數不超過 1」從 `graph.json` 反查的結果，**不等於完整的 30 個**：

| 類型 | 檔案 | 原因 |
| --- | --- | --- |
| Vitest 測試 | `web/src/**/*.test.ts` 共 11 個（如 `web/src/api/auth.test.ts`） | 推測內容都在 `describe` / `it` 回呼內，沒有具名符號 |
| 只有樣板的 Vue 元件 | `OrderStatusTag.vue`、`QueueWaitingPanel.vue`、`TicketStatusTag.vue` | 三個都只有 import、`defineProps` 或樣板，沒有具名函式或變數（`QueueWaitingPanel.vue` 共 33 行，沒有 `const` / `function`） |
| C# top-level statements | `tools/ProjectC.LoadTest.Seeder/Program.cs` | 26 行的進入點，沒有宣告型別 |
| 設定與其他 | `web/eslint.config.js`、`web/vitest.bundle.config.ts`、`tests/ProjectC.WebApi.Tests/AssemblyInfo.cs`、`loadtest/sql/db-waits.sql`、`openspec/changes/archive/2026-10-14-web-area-separation/measure-switch-abbreviation.js` | 沒有符號定義，或未逐一確認 |

未看到的 9 個檔案不再追查，因為工具輸出有截斷。

決定：不處理。這些檔案都還有檔案節點，查它們的內容時直接讀原始碼。

## 6. guard hook 提示

- 以合法 JSON 餵入全新 `session_id`，`hook-guard search` 和 `read` 都有輸出 `additionalContext`。先前手動測試沒有輸出，是因為測試 JSON 的 Windows 路徑反斜線未跳脫，解析失敗後依設計靜默結束。
- 工作階段後段看不到提示是設計行為（graphify/cli.py 的 `_nudge_allowed`）：每個 agent 第一次必出；之後 30 分鐘內跑過 query／explain／path 就不提示；每個 agent 最多 5 次（`GRAPHIFY_HOOK_NUDGE_CAP`）。本次工作階段的計數停在 3，與跑過 query 後停止提示相符。

## 7. 查詢準確度

同一個 UnitOfWork 問題，以 1200 token 預算比較：

| 方式 | 結果 |
| --- | --- |
| 現況 `query`（中英混合） | 有命中，但混入 `Task`、`Fact` 與不相關的 `.Dispose()` |
| 排除 archive（暫存圖譜） | 前 10 筆幾乎不變，只少一個 archive 來源 |
| 再排除無來源的外部節點（暫存圖譜） | `Task`、`Fact` 消失，其他雜訊仍在 |
| `query --seed <完整節點 id>` | 明顯較好：`IUnitOfWork`、`.BeginTransactionAsync()`、`.OpenConnectionAsync()` 進入結果 |
| `explain <路徑>::<符號>` | 最精準：列出直接關聯與行號 |

- 雜訊主因是外部型別形成的樞紐：`Task` 1838 條邊、`Fact` 1190、`Guid` 711、`CancellationToken` 475，BFS 容易經由它們擴散。
- 決定：不排除 archive、不改圖譜。已知符號用 `explain <路徑>::<符號>`；只知道主題時先 `query` 定位，再用 `--seed` 深入。這只比較了一個問題，不代表所有查詢。

## 總結報告

- **結果**：graphify 已合併回 master，CLI、Claude Code guard hook、`post-commit` 自動更新都實測可用。
- **設定調整**：加裝 `tree-sitter-sql`（版本不變）；`CLAUDE.md` 新增查詢方式規則；`hook install` 加到 `.gitattributes` 的 merge driver 設定已還原，因為 `graphify-out/` 不進版控。
- **決策**：保留 `openspec/changes/archive`；不處理沒有符號的程式檔。
- **未驗證**：節點翻倍的確切原因（舊圖譜已被覆蓋）；`post-checkout`、`post-merge`、`post-rewrite` 尚未確認實際觸發；查詢方式的比較只用了一個問題；工具回報 30 個沒有符號的檔案，只反查到 21 個。
- **限制**：git hook、`tree-sitter-sql`、`graphify-out/` 都只在本機，換機器要重新安裝與建置；`.claude/settings.json` 的 graphify 路徑寫死本機使用者目錄。
