> 所有指令都在容器內執行（例如 `docker compose exec web npm run test`），不在本機執行 node／npm。
>
> **測試種類與 AC 對應**（理由見 design.md 決策 5）。所有 AC 都以 Vitest 單元測試驗證（`?raw` 讀取 CSS，不碰 DB 與網路），不需要 AC 測試對應例外。
>
> | ID | Requirement／Scenario | 驗證方式 | Task |
> | --- | --- | --- | --- |
> | WCC-001 | 實心語意色按鈕三種狀態的白字 | Vitest | 1.2（基準失敗）、2.1、2.4 |
> | WCC-002 | 狀態標籤與提示訊息文字 | Vitest | 1.2、2.1、2.4 |
> | WCC-003 | 連結與主色文字 | Vitest | 1.2、2.1、2.4 |
> | WCC-004 | 一般文字與次要文字 | Vitest | 1.2、2.1、2.4 |
> | WCC-005 | Placeholder | Vitest | 1.2、2.1、2.4 |
> | WCC-006 | error 系列維持為 danger 的別名 | Vitest | 2.1、2.4 |
> | WCC-007 | 輸入框邊框與 hover 邊框 | Vitest | 1.2、2.1、2.4 |
> | WCC-008 | 按鈕焦點框 | Vitest | 1.2、2.1、2.4 |
> | WCC-009 | 調整後的語意色色相與彩度 | Vitest | 2.1、2.4 |
> | WCC-010 | 根元素配色宣告 | Vitest | 1.2、2.3 |
> | WCC-011 | text 按鈕文字對 hover 與 active 背景 | Vitest | 1.2、2.1、2.4 |
> | WCC-012 | 同義 token 同值、裝飾性邊框不變 | Vitest | 1.2、2.2、2.4 |
>
> **不新增整合測試**：本 change 不改後端與 API。

## 1. 測試先行與基準

- [ ] 1.1 新增 `web/src/styles/morandi.contrast.test.ts`。測試內的輔助函式：解析 CSS 區塊中的自訂屬性（`:root`、`.el-button`、`.el-button--<語意色>`）、遞迴展開 `var()`（指向不存在的 token 時拋出錯誤，不得回傳空值而讓比較假通過）、WCAG 相對亮度與對比度、sRGB→OKLCH。輔助函式先以參考值驗證：`#000000` 對 `#ffffff` 為 21.00、`#767676` 對 `#ffffff` 為 4.54（±0.01）、同色為 1.00；`#ff0000` 的 OKLCH 色相約 29.2°（±0.5）；`var()` 循環引用或缺少 token 時拋出錯誤
- [ ] 1.2 依 spec 為 WCC-001～012 撰寫斷言，每個 `it` 名稱以對應的 WCC 編號開頭（例如 `WCC-001 實心語意色按鈕…`），讓測試結果可回指 AC：
  - WCC-001：從 `.el-button--<語意色>` 區塊讀取 `--el-button-hover-bg-color`、`--el-button-hover-border-color`、`--el-button-active-bg-color`、`--el-button-active-border-color`，該區塊不存在或缺少變數時測試必須失敗；除白字對比外，斷言 hover 相對亮度 < base、active 相對亮度 < hover，以及邊框等於對應背景
  - WCC-008：從 `.el-button` 與 `.el-button--<語意色>` 讀取 `--el-button-outline-color`；另斷言 `morandi.css` 中 `.el-button {` 區塊出現在所有 `.el-button--<語意色>` 區塊之前（兩者權重相同，順序在後者會覆蓋前者；`.el-button` 若排在後面，會把語意色按鈕的焦點框蓋回預設值）
  - WCC-009：原色寫死在測試中，不從 CSS 讀取
  - WCC-011：`--el-fill-color` 在 `morandi.css` 未定義時，以 Element Plus 2.14.4 預設值 `#f0f2f5` 計算（測試內註明來源）
  - WCC-012：`--el-border-color-light`／`lighter` 的原值寫死在測試中

  以**修改前**的 `morandi.css`／`style.css` 執行 `docker compose exec web npm run test -- morandi.contrast`，逐項記錄結果與**失敗原因**（對比不足，或區塊／變數不存在，分開記錄）：WCC-001～005、007、008、010、011 必須失敗（審視報告第 4.1 節已實測不合格；WCC-001、008 另因覆寫區塊不存在而失敗），WCC-006、012 必須通過（現有別名與同值關係正確）；WCC-009 修改前預期通過（新舊值相同，色相差為 0、彩度相等），照實記錄。若預期失敗的項目通過，代表測試有誤，先停下排查

## 2. 色票修改

- [ ] 2.1 依 design.md 決策 1 的方法（OKLCH 只降明度、二分搜尋、兩個條件各留 0.1 餘裕）以容器內的 node 計算確切色票：五色 base、`light-3`／`5`／`7`／`8`／`9`（base 與白色混合）、`dark-2`（混 20% 黑）、active 色（混 35% 黑）；次要文字、placeholder、`--el-border-color`、`--el-border-color-hover` 依決策 3、4 計算。將腳本與輸出記錄在本 task 下方，之後若需重算可重現
- [ ] 2.2 更新 `web/src/styles/morandi.css`：`:root` 的語意色各階、文字色（`--color-text-secondary` 與 `--el-text-color-secondary` 同值）、placeholder、`--color-primary`（與新的 `--el-color-primary` 同值）、`--el-border-color`，新增 `--el-border-color-hover`；新增 `.el-button` 焦點框覆寫與五個 `.el-button--<語意色>` 的 hover 背景與邊框、active 背景與邊框、焦點框覆寫（決策 2），`.el-button` 區塊須排在五個語意色區塊之前，各附一行說明「為什麼」的註解。error 別名區塊、`--el-border-color-light`／`lighter` 不動
- [ ] 2.3 `web/src/style.css` 的 `color-scheme: light dark` 改為 `color-scheme: light`
- [ ] 2.4 執行 `docker compose exec web npm run test -- morandi.contrast`，WCC-001～012 全部通過；再執行完整前端測試 `docker compose exec web npm run test` 與 `docker compose exec web npm run build`（含 `vue-tsc -b`），皆須通過。若有既有測試失敗，先在 HEAD 上重跑確認是否為既有 flake，並照實記錄

## 3. 瀏覽器驗證

- [ ] 3.1 修改前（2.2 之前）在瀏覽器以 javascript 讀取一個實心 primary 按鈕的 `getComputedStyle(button).getPropertyValue('--el-button-hover-bg-color')` 與 `--el-button-outline-color`、一個 `el-input__wrapper` 所在元素的 `--el-border-color-hover`，記錄修改前的值（預期為舊的 `light-3`、`light-5` 與 Element Plus 預設冷灰），並截圖買家活動列表、活動詳情頁、登入頁、後台活動列表作為基準
- [ ] 3.2 修改後重讀 3.1 的同一組 computed style，值必須等於 2.1 算出的 `dark-2`、base 與 hover 邊框色，證明 `morandi.css` 的覆寫沒有被 Element Plus 的規則蓋掉；另讀一個 `type="error"` 的 `el-alert` 與一個 `el-tag` 的 computed `color` 與 `background-color`，必須等於新的 base 與 `light-9`。任一不符先停下排查
- [ ] 3.3 修改後截圖 3.1 的同一組頁面，逐頁目視確認：沒有文字消失或對比反轉、hover 時按鈕變深、Tab 焦點框清楚可見、輸入框邊框可辨識。將修改前後的截圖交給使用者確認視覺方向；後台頁面需要登入，若使用者尚未登入，該頁的截圖延到使用者登入後進行，在此之前本 task 不得勾選

## 4. 文件與收尾

- [ ] 4.1 更新 `docs/ui-ux-review-2026-10-08.md` 第 4.1 節：標註已由 `palette-contrast-aa` 修正，附上新值的對比度；第 2.2 節的 `color-scheme` 項目標註已修正
- [ ] 4.2 呼叫 `strict-reviewer` 審查（本 change 只改前端樣式與測試，不涉及 Application／Repository 層，不適用 hardener）；依結果修正後重跑 2.4
