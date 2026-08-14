---
name: branch-and-propose
description: 開始規劃新功能時觸發，自動從 master 同步並建立新分支，接著進入 OpenSpec 提案流程。當使用者說「規劃新功能」「開新的 spec」「開始做 XXX 功能」「幫我開一個新分支做 XXX」時使用。
argument-hint: [功能名稱]
---

## Instructions

1. 確認目前 git 狀態是否乾淨（`git status`）；若有未提交的變更，先詢問使用者要 stash 還是先提交，不可直接覆蓋。
2. 執行以下指令同步 master：
   ```
   git checkout master
   git pull origin master
   ```
3. 若使用者的請求中沒有明確功能名稱，先詢問功能名稱，並轉換為符合團隊命名慣例的分支名稱（英文、kebab-case，例如 `feature/dive-log-export`）。
4. 建立並切換新分支：
   ```
   git checkout -b feature/<功能名稱>
   ```
5. 分支建立完成後，向使用者確認分支已就緒，接著**必須**執行 `/opsx:propose <功能名稱>`（`<功能名稱>` 沿用步驟 3 決定的名稱）建立本次功能的 spec 提案草稿，不要自行用其他方式產生 proposal。
6. spec 草稿完成後，依照 CLAUDE.md 的「OpenSpec 工作流規則 → Task 執行前」清單，確認 Acceptance Criteria 是否已定義且對應測試任務，未對應者提醒使用者補上，不進入實作階段。

## Notes

- 這個 skill 只負責「開分支 → 交接給 OpenSpec 流程」，不涉及 spec 內容本身的產出邏輯，避免與 CLAUDE.md 裡的 OpenSpec 規則重複維護。
- 若專案的預設分支不是 `master`（例如 `main`），使用前請調整步驟 2、3 中的分支名稱。
