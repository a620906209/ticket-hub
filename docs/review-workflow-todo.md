# 交叉審查流程改進待辦（2026-11-02）

## 背景

`api-health-checks` 的交叉審查有效，但 Devin 的 FAIL 都集中在證據不合規：7.1 還原驗證連續 3 次未通過（寫 N/A、用 `git show` 覆蓋而非 `git stash`、缺測試命令與 `pop` 輸出）。測試缺口由對抗式審查找出，7.4 合併 mutation 的遮蔽問題由 `strict-reviewer` 找出。

以上依現有紀錄整理，各輪細節未逐條核對。Devin 單獨審程式碼的效果，目前也無法從這次結果判斷。

## 已完成

- Blocking 須寫出具體失敗情境，寫不出來就降為 warning（`.claude/agents/` 與 `.agents/agents/` 的 `strict-reviewer`、`spec-reviewer`）。
- `CLAUDE.md` 流程：warning 不需要為了消除而再跑一輪；只改 `tasks.md` 證據紀錄時由一個 reviewer 核對；不設輪數上限。

## 待辦

依效益排序，1、2 優先。

1. **以腳本產生驗證證據**：還原驗證與 mutation 由腳本逐一套用、執行測試、還原，並輸出原文。須遵守 `CLAUDE.md`「RED 還原驗證」的 `git stash` 流程，pop 失敗或有衝突時立即停止。
2. **Devin 的 prompt 不帶主 agent 的說法**：只給 change 名稱、commit 與審查目標，不描述改了什麼或為什麼改，與對抗式審查的規則一致。
3. **明確分配模型並留下紀錄**：程式有改動時以高階模型審完整 diff，只改證據時可用較輕的模型。每輪的模型與 commit 記入 `tasks.md`。
4. **確認 Devin 已開始才等結果**：以 `herdr pane send-text` 送出後，先確認 `agent_status` 變成 `working`。這次 Enter 沒有送出，pane 上還是上一輪的 `VERDICT: PASS`，差點被當成新結果。
5. **會執行容器的審查一律排隊**：同時執行或改動容器環境曾造成 46 個測試失敗。Docker 當機也可能與記憶體壓力有關，但原因未確認。

## 下次實驗

讓 Devin 先只看程式碼審一輪，再看證據，藉此判斷它單獨審程式碼的價值。

## 待決定

第 1 項腳本放在哪裡、第 2 至 5 項是否寫入 `CLAUDE.md` 的審查迴圈，實作時再決定。
