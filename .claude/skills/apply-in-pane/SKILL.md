---
name: apply-in-pane
description: 使用者說「幫我實作目前的 change」「開始實作」「實作這個 change」「apply」「/apply-in-pane」時使用，優先於在主 session 直接執行 /opsx:apply。透過 herdr 另開 pane 啟動新的 Claude Code session 執行 /opsx:apply，完成後銜接 cross-review。
---

# Apply-in-Pane：另開 pane 實作 OpenSpec change

目的：實作在新 pane 的乾淨 session 跑，主 session 只負責開 pane、等待、銜接審查，節省 token。

## 前置檢查（任一不符就停下回報）

1. `test "${HERDR_ENV:-}" = 1`，失敗就告知不在 herdr 內並停止。
2. 決定 change：只有一個進行中的 change 就直接用（`openspec list --json`）；多個就用 AskUserQuestion 問；沒有就告知需先 `/opsx:propose`。
3. spec 已通過審查（spec-reviewer PASS）且已 commit：`git status --short` 乾淨、目前在該 change 的 feature 分支（不在 master）。使用者說「幫我實作」即視為已明確下達實作指示，不重複確認；但若無法確認 spec 曾 PASS，先問。
4. 沒有殘留的同名 reviewer／implementer agent：`herdr agent list`。

## 步驟 1：開 pane 並啟動實作 session

```bash
herdr pane split --current --direction right --cwd "$PWD" --no-focus   # 讀 .result.pane.pane_id；窄／高 pane 改 down
herdr pane run <pane> "claude --model claude-sonnet-5-5 --permission-mode auto"
herdr agent list                                                         # 取名稱；等 herdr agent get <pane> 為 idle
```

`agent start` 對 Git Bash pane 會失敗，一律 `pane run`。實作 pane 固定用 Sonnet 5.5；不帶 `--model` 時預設模型在此環境可能不可用（回 "issue with the selected model"，0 秒結束、沒有改任何檔案）。送出以 `/` 開頭的指令（`/opsx:apply`、`/cross-review`、`/model`）時，Git Bash 會把它當路徑轉換，命令前加 `MSYS_NO_PATHCONV=1`。

## 步驟 2：送實作 prompt

```bash
herdr agent prompt <pane> "/opsx:apply <change>。遵守 CLAUDE.md：依 TDD 逐 task 執行並把 RED／GREEN 原文證據寫進 tasks.md；程式碼全部完成後套用 hardener 並記錄；不要 commit／push；需要使用者決定的分叉事項不要自行決定，寫在最後回報。完成後用 herdr 把完整回報（完成 task、測試輸出摘要、偏離 spec 之處、待決事項）送回主 session pane <主 pane id>，再回 idle。" --wait --timeout 3600000
```

主 pane id 用 `herdr pane current --current` 取得。

## 步驟 3：等待與確認

`agent prompt --wait` 會把權限對話框誤判為 idle，所以：

1. `herdr agent get <pane>` 要看到 `working`，否則別當作已開始。
2. `herdr agent read <pane> --source visible` 確認沒有待批准的對話框；有就回報使用者，不代為核准危險動作。
3. 完成後讀送回的訊息，或 `herdr agent read <pane> --source recent-unwrapped --lines 300`。

實作期間主 session **不改檔、不跑會 build 的容器指令**（兩個 pane 共用同一工作目錄）。

## 步驟 4：驗收並銜接審查（三 pane 標準流程）

主 session 只負責下指令、轉達使用者決定與核對，**不審查、不改檔**。實作 pane 實作完成後保留不關，由它自己接著審查、修正與送外部審查。

1. 回報使用者：完成 task 數、測試結果、偏離 spec 之處、待決事項；用 `git status`／`git diff --stat` 核對實際變更與回報一致，不一致就直說。
2. 實作 pane 的回報若有偏離 spec，先問使用者是否更新 spec，再往下。
3. 請實作 pane 用 subagent 跑 `strict-reviewer`：有 blocking 就先自行驗證，成立才修，修到沒有 blocking；結果回報主 pane。證據不是原文（被過濾、截斷、以「其餘原文」帶過）也算 blocking。
4. strict-reviewer PASS 後，詢問使用者是否 commit；**commit 由使用者同意後才做**，由實作 pane 執行（訊息繁體中文、結尾帶 Co-Authored-By），不 push。
5. commit 後請實作 pane 執行 `/cross-review`。步驟 0 的答案由主 session 先寫進 prompt（工具、審查對象、change 名稱、第幾輪），不讓它用 AskUserQuestion 詢問；並指定：reviewer pane 從**實作 pane 自己**往下切（`herdr pane split --current --direction down --cwd "$PWD" --no-focus`，不往右、不改方向），報告送回實作 pane 而非主 pane。版面：主 session 在左欄，實作 pane 在右欄上方，reviewer pane 在右欄下方。
6. 迴圈中實作 pane 修正後要 commit 前，先回報主 pane 並等使用者指示；需使用者決定的事一律回報、不自行決定。外部 PASS 後，實作 pane 不做 cross-review 步驟 5 的最後主 session 審查，直接把每輪結果（工具／模型、commit、blocking 處理、測試摘要行）回報主 pane；是否另跑主 session 最終審查由使用者決定。
7. 有前端變更時，提醒審查後還需真實瀏覽器驗證；審查通過後再提示歸檔（`/opsx:archive`）與合併，各階段轉換分開確認。

## 注意

- 不關閉自己沒建立的 pane；實作與審查結束後問使用者是否關閉實作 pane 與 reviewer pane。
- 實作 session 若遇到需要使用者決定的分叉，應停下回報，不自行決定。
- 絕不自動 commit／push。
