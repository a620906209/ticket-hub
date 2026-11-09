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
herdr pane run <pane> "claude --permission-mode auto"
herdr agent list                                                         # 取名稱；等 herdr agent get <pane> 為 idle
```

`agent start` 對 Git Bash pane 會失敗，一律 `pane run`。

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

## 步驟 4：驗收並銜接審查

1. 回報使用者：完成 task 數、測試結果、偏離 spec 之處、待決事項；用 `git status`／`git diff --stat` 核對實際變更與回報一致，不一致就直說。
2. 實作 pane 的回報若有偏離 spec，先問使用者是否更新 spec，再往下。
3. 詢問使用者是否 commit 實作；**commit 由使用者同意後才做**，不自動 commit／push。
4. commit 後呼叫 `cross-review` skill（它會問用 Claude Code 或 Devin、審查對象），跑到外部 PASS，最後做一次主 session 審查。
5. 有前端變更時，提醒審查後還需真實瀏覽器驗證；審查通過後再提示歸檔（`/opsx:archive`）與合併，各階段轉換分開確認。

## 注意

- 不關閉自己沒建立的 pane；實作完成後問使用者是否關閉該 pane。
- 實作 session 若遇到需要使用者決定的分叉，應停下回報，不自行決定。
- 絕不自動 commit／push。
