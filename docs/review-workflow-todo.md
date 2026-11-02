# 交叉審查流程改進待辦（2026-11-02）

## 背景

`api-health-checks` 的交叉審查有效，但 Devin 的 FAIL 都集中在證據不合規：7.1 還原驗證連續 3 次未通過（寫 N/A、用 `git show` 覆蓋而非 `git stash`、缺測試命令與 `pop` 輸出）。測試缺口由對抗式審查找出，7.4 合併 mutation 的遮蔽問題由 `strict-reviewer` 找出。

以上依現有紀錄整理，各輪細節未逐條核對。Devin 單獨審程式碼的效果，目前也無法從這次結果判斷。

## 已完成

- Blocking 須寫出具體失敗情境，寫不出來就降為 warning（`.claude/agents/` 與 `.agents/agents/` 的 `strict-reviewer`、`spec-reviewer`）。
- `CLAUDE.md` 流程：warning 不需要為了消除而再跑一輪；只改 `tasks.md` 證據紀錄時由一個 reviewer 核對；不設輪數上限。
- `scripts/verify-test-evidence.sh`：逐一執行還原驗證與 mutation 並輸出原文，測試在 `scripts/tests/verify-test-evidence.test.sh`。
- `CLAUDE.md` 流程：審查依序執行；Devin 的 prompt 不描述改動內容，重審只附前輪 blocking 的編號與參考位置；依變更分配模型並記入 `tasks.md`；確認 `agent_status` 為 `working` 才等結果。

## 待觀察

- 已 commit 的修改仍須手動走 `git reset --mixed <fix>^` 流程，腳本只處理尚未 commit 的修改。若這類情況再次出錯，再考慮納入腳本。
- 下次交叉審查時，讓 Devin 先只看程式碼審一輪，再看證據，藉此判斷它單獨審程式碼的價值。
