# graphify 收尾待辦（2026-11-01）

狀態：**剩 2 項未結案**（第 1、3 項）。工具版本 graphify 0.9.97，圖譜建於 master `6b5e336`。

| # | 項目 | 狀態 |
| --- | --- | --- |
| 1 | 節點數從 6272 變成 12118 的原因 | 無法查證，組成已確認 |
| 2 | 舊節點「待決：ASPNETCORE_ENVIRONMENT…」查不到 | 已結案：節點還在 |
| 3 | `post-commit` 自動更新 | 腳本已驗證，真實 commit 待驗證 |
| 4 | `.sql` 未進圖譜 | 已處理 |
| 5 | 30 個程式檔沒有符號 | 已確認為工具限制，不處理 |

## 1. 節點數翻倍

- 舊的 `graph.json` 已被覆蓋，也不進版控，無法比對，**確切原因查不出來**。
- 目前 12119 個節點，依 `file_type`：code 6250、document 5643、concept 211、rationale 14（在加入 SQL 解析前統計，當時總數 12118）。
- 節點最多的來源是 `openspec/changes/archive`，共 3803 個，約占 31%。
- 推測：舊圖譜涵蓋的檔案較少，這次把更多 Markdown 納入。舊 `manifest.json` 190KB、新的 287KB 符合這個方向，但不是證明。
- 決定：先保留 archive，因為設計取捨都記錄在裡面。如果查詢結果常被 archive 蓋掉，再用 `.graphifyignore` 排除。

**結案條件**：實際使用一段時間後，確認查詢品質可接受，或決定排除 archive。

## 2. 舊節點名稱

- 節點仍在 `docs/k8s-todo.md` 底下，解析 JSON 後查得到原標題。
- 先前用 `grep` 搜尋原文得到 0 筆，推測是 `graph.json` 把中文跳脫編碼；這點未另外驗證。
- 注意：`graphify explain "k8s-todo.md"` 回報找不到節點，查文件內容要用 `graphify query` 或節點標題。

## 3. post-commit 自動更新

- 已手動執行 `.git/hooks/post-commit`，背景重建正常，日誌在 `~/.cache/graphify-rebuild.log`。日誌顯示「5 file(s) changed - rebuilding graph...」，最後是「No code-graph topology changes detected; outputs left untouched.」。
- 還沒透過真實的 `git commit` 觸發過。

**結案條件**：下一次 commit 後，`graph.json` 的 `built_at_commit` 等於新的 HEAD，且日誌沒有錯誤。如果該 commit 沒有改變圖譜結構，輸出不會更新，這時只看日誌。

## 4. `.sql` 未進圖譜

- 已執行 `uv tool install --force "graphifyy[sql]==0.9.97"`，加入 `tree-sitter-sql` 0.3.11，版本不變。這是本機工具環境的設定，換機器要重做。
- `loadtest/sql/db-waits.sql` 現在有檔案節點，缺依賴的警告也消失了。
- 這個檔案沒有定義任何符號，所以改列入第 5 項的清單。

## 5. 沒有符號的程式檔

重新更新後，graphify 回報 30 個檔案。工具輸出的清單有截斷，以下 21 個是用「節點數不超過 1」從 `graph.json` 反查的結果，**不等於完整的 30 個**：

| 類型 | 檔案 | 原因 |
| --- | --- | --- |
| Vitest 測試 | `web/src/**/*.test.ts` 共 11 個（如 `web/src/api/auth.test.ts`） | 推測內容都在 `describe` / `it` 回呼內，沒有具名符號 |
| 只有樣板的 Vue 元件 | `OrderStatusTag.vue`、`QueueWaitingPanel.vue`、`TicketStatusTag.vue` | 已看過 `OrderStatusTag.vue`：共 9 行，只有 import；其餘兩個未看 |
| C# top-level statements | `tools/ProjectC.LoadTest.Seeder/Program.cs` | 26 行的進入點，沒有宣告型別 |
| 設定與其他 | `web/eslint.config.js`、`web/vitest.bundle.config.ts`、`tests/ProjectC.WebApi.Tests/AssemblyInfo.cs`、`loadtest/sql/db-waits.sql`、`openspec/changes/archive/2026-10-14-web-area-separation/measure-switch-abbreviation.js` | 沒有符號定義，或未逐一確認 |

決定：不處理。這些檔案都還有檔案節點，查它們的內容時直接讀原始碼。
