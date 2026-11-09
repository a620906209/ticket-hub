---
name: cross-review
description: 透過 herdr 另開 pane，以 Claude Code (Opus) 或 Devin CLI 跑 spec-reviewer / strict-reviewer 交叉審查；主 session 只負責修正，直到外部審查 PASS 才在主 session 做最後一次審查。使用者說「交叉審查」「送審」「cross-review」時使用。
---

# Cross-Review：herdr 外部交叉審查

目的：省主 session token。外部 reviewer 審、主 session 修，迴圈跑到外部 PASS；**迴圈中主 session 不自己跑 reviewer**，最後才做一次主 session 審查。

## 前置

1. `test "${HERDR_ENV:-}" = 1`，失敗就告知不在 herdr 內並停止。
2. 送審的程式碼／文件必須已 commit（CLAUDE.md 規定；未 commit 先請使用者決定，**不自動 commit**）。記下 `git rev-parse --short HEAD`。
3. 先用 `herdr agent list` 確認沒有同名殘留 agent。

## 步驟 0：先判斷階段，再用 AskUserQuestion 問（一次問完）

呼叫端（例如 `apply-in-pane` 的主 session）若已在 prompt 明確給出工具、審查對象、change 名稱與輪次，直接採用、不再提問，也不重新推論。以下為未給答案時的預設流程。

先從 repo 狀態推出審查對象的預設值（唯讀，不修改任何檔案）：

1. `openspec list --json` 取得進行中的 change；沒有或有多個就不推論、直接問。
2. 讀該 change 的 `tasks.md` 勾選狀態，並看 `git diff --stat master...HEAD` 與 `git status --short` 變更了哪些路徑：
   - **規格階段**：`tasks.md` 沒有任何 `- [x]`，且變更只在 `openspec/`（或文件）。
   - **實作階段**：`tasks.md` 已有 `- [x]`，或變更含 `src/`、`tests/`。
   - 兩種訊號並存或都不符：視為無法判斷，不給預設。
3. 把推論結果列為第一個選項並標 (Recommended)，同時說明依據（例如「tasks.md 無勾選、變更只在 openspec/」），讓使用者一鍵確認或改選。**不得不問就直接開跑。**

問題內容：

- 工具：Claude Code (Opus) ／ Devin CLI
- 審查對象：spec-reviewer（規格階段，PASS 後同一輪接著跑 design-hardener）／ strict-reviewer（程式碼階段）
- change 名稱（能從對話推得就帶預設，不另問）
- 若為重審：是否附上前輪 blocking 編號與位置

## 步驟 1：開 pane 並啟動

```bash
herdr pane split --current --direction right --cwd "$PWD" --no-focus   # 讀 .result.pane.pane_id；窄／高 pane 改 down
herdr pane run <pane> "claude --model opus --permission-mode auto"     # Claude Code
herdr pane run <pane> "devin --permission-mode dangerous"                  # Devin（bypass：smart 會卡權限對話框，2026-11-10 使用者決定改用）
herdr agent list                                                         # 取 agent 名稱與 pane id
```

- `herdr agent start` 對 Git Bash pane 會失敗（agent_pane_busy／not an available shell），一律用 `pane run`。
- 啟動後等 `herdr agent get <pane>` 為 `idle` 再送 prompt。
- 同一輪迴圈沿用同一個 pane；重審時用全新的 prompt，不要求它沿用上輪結論。
- 從 `apply-in-pane` 的實作 pane 執行時：一律 `--direction down`（reviewer 在實作 pane 正下方，右欄上下排列，不往右切），報告送回自己的 pane（`herdr pane current --current` 取得 id）；送出以 `/` 開頭的指令時加 `MSYS_NO_PATHCONV=1`。

## 步驟 2：送審 prompt

只給：change 名稱、commit、審查目標。**不描述改了什麼或為什麼改。** 重審可附前輪 blocking 編號與參考位置供回歸核對。

必須包含：
- 使用 subagent：Claude Code 用 `spec-reviewer` / `strict-reviewer`（`.claude/agents`）；Devin 用 `.agents/agents` 下同名 agent。Devin 的 prompt 要**明確**寫「請用 subagent 執行」。
- 角色聲明：「你是 reviewer，不要使用 cross-review／apply-in-pane skill，也不要詢問要用哪個工具，直接審查。」（Devin 會自動載入專案 skill，曾自行觸發 cross-review 並反問工具選擇，2026-11-10）
- 唯讀，不修改任何檔案、不 commit。
- blocking 必須附具體失敗情境；沒有就列為建議。
- 第一次送審的結尾：審完後用 herdr 把**完整報告**送回主 session pane（pane id 以 `herdr agent list` 查到的主 session 為準），再回 idle。

```bash
herdr agent prompt <name-or-pane> "<prompt>" --wait --timeout 1800000
```

## 步驟 3：確認真的在工作、取結果

`agent prompt --wait` 會把權限對話框誤判為 idle／done，所以每次送出後：

1. `herdr agent get <name>`：要看到 `working`；沒有就別等結果（可能讀到上一輪 verdict）。
2. `herdr agent read <name> --source visible`：確認沒有待批准的對話框。有就**回報使用者**，不要代為核准危險動作。
3. 完成後 `herdr agent read <name> --source recent-unwrapped --lines 300` 讀報告（或使用外部送回的訊息）。

## 步驟 4：修正迴圈

- 外部 reviewer 的每個 blocking 先**自己驗證**（回頭看程式／規格與失敗情境），不照單全收；不成立的在紀錄裡寫理由。
- 修正後跑相關測試（容器內，遵守 docker compose 規則）；程式碼有改動才需要跑，只改 tasks.md 證據紀錄則不必。
- 修完 commit 前**先問使用者**；commit 後重審（回步驟 2，附前輪 blocking 編號）。
- 程式有改動：高階模型審完整 diff；只改證據紀錄：可用較輕模型。
- 每輪的工具／模型與 commit 記入 `tasks.md`（無 tasks.md 則記在回報中）。
- 不設輪數上限：仍有具體失敗情境的 blocking 就繼續；warning 不需為了消除再跑一輪。

## 步驟 5：外部 PASS 後的最後一次主 session 審查

外部 reviewer 沒有 blocking 後，才在主 session 做一次：

- 程式碼：`strict-reviewer`，**依序**再跑一個全新的對抗式 general-purpose subagent（只給 change 名稱與審查目標，不給前輪結論；不要同時並跑，會執行容器的審查同時進行曾造成大量假失敗）。
- 規格：`spec-reviewer`，PASS 後接著 `design-hardener`。
- 若這次又修改了程式，回步驟 2 讓外部再審；只改證據則核對即可。

最後回報：輪數、每輪工具／模型／commit、blocking 處理結果、最後一次主 session 審查結果。

## 注意

- 規格 PASS 後停下，等使用者交叉審查並明確指示才進入實作；階段轉換各自確認。
- 不關閉自己沒建立的 pane；審查結束問使用者是否關閉該 reviewer pane。
- 絕不自動 commit／push。
