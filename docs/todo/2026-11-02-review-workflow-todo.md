# 交叉審查流程改進待辦（2026-11-02）

## 背景

`api-health-checks` 的交叉審查有效，但 Devin 的 FAIL 都集中在證據不合規：7.1 還原驗證連續 3 次未通過（寫 N/A、用 `git show` 覆蓋而非 `git stash`、缺測試命令與 `pop` 輸出）。測試缺口由對抗式審查找出，7.4 合併 mutation 的遮蔽問題由 `strict-reviewer` 找出。

以上依現有紀錄整理，各輪細節未逐條核對。Devin 單獨審程式碼的效果，目前也無法從這次結果判斷。

## 待觀察

- 已 commit 的修改仍須手動走 `git reset --mixed <fix>^` 流程，腳本只處理尚未 commit 的修改。若這類情況再次出錯，再考慮納入腳本。
- 下次交叉審查時，讓 Devin 先只看程式碼審一輪，再看證據，藉此判斷它單獨審程式碼的價值。
- herdr 的 `agent_status` 在權限對話框等待中仍可能顯示 `done`／`idle`（2026-11-04 exception-log-masking 規格審查觀察）。送審後除了確認 `working`，還要用 `herdr agent read <name> --source visible` 確認沒有待批准的對話框，再讀 `recent-unwrapped` 取結果。若多次再現，再考慮寫成腳本。
- 規格審查輪數偏多、規格逐輪變複雜（exception-log-masking 的截斷政策改寫多次，最後 24 個 Scenario）。每輪發現的問題都成立，但越來越窄。下次規格審查時留意：邊界案例是否值得用規格逐條鎖定，還是改成設計中記錄為已知取捨。若再出現，再考慮限制輪數或在 CLAUDE.md 加收斂條件。
- Devin 送審 prompt 須明確指示使用 subagent（2026-11-04 起；使用者已確認之後的審查都跑 subagent）。實際用過幾次、確認穩定後，再決定是否升級為 `CLAUDE.md` 正式規則。
- `cross-review`、`apply-in-pane` skill 已建立（2026-11-10）並於 `reconcile-event-queue-complexity` 實跑一輪（三 pane 流程，外部審查用 Claude Code Opus）。已確認：`/opsx:apply` 經 `agent prompt` 可觸發，但 Git Bash 會把 `/` 開頭轉成路徑，須加 `MSYS_NO_PATHCONV=1`；預設模型在此環境不可用，實作 pane 須指定 Sonnet 5.5；`--permission-mode auto` 下沒有遇到待批准的對話框。**尚未驗證**：Devin 作為外部審查時 subagent 是否穩定（這次沒用 Devin）。再多跑幾次、確認穩定後，再評估是否把送審規則升級為 `CLAUDE.md` 正式規則。
