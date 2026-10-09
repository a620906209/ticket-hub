## Why

UI/UX 審視報告（`docs/ui-ux-review-2026-10-08.md`，第 5.2 節 R1，P0）指出：後台導覽是 `el-menu mode="horizontal"` 搭配 `:ellipsis="false"`，一列放了 5 個選單項目、目前主辦方名稱與登出鈕，在手機寬度下必然溢出。偏偏票券核銷頁要在手機上使用（相機掃描），操作人員在現場會直接遇到這個問題。這是審視報告中最後一個尚未處理的 P0。

## What Changes

- 視窗寬度 ≤ 720px 時，後台導覽列改為「選單按鈕＋目前主辦方名稱」，水平選單不顯示；點選單按鈕會開啟側邊抽屜，抽屜內有直式選單與登出（主辦方名稱入口留在頂列，不在抽屜重複）。
- 抽屜內選單項目的顯示規則與水平選單相同（依角色顯示「主辦方審核」）；點選任一項目後導覽至目標頁面並關閉抽屜；可用 Esc 關閉，關閉後焦點回到選單按鈕。
- 視窗寬度 > 720px 時維持現有水平選單，但目前主辦方名稱過長時改為截斷顯示，避免名稱把導覽列撐寬。
- 導覽列在 320px 至 1280px 寬時都不產生橫向溢出。
- **不包含**：後台表格在手機上的收合（報告 R2，P1）、核銷頁的精簡版型、`prefers-reduced-motion` 動畫處理（全站目前都沒有，留給買家探索頁改版一併定義動畫規範）。

## Capabilities

### New Capabilities

（無）

### Modified Capabilities

- `admin-web-ui`：新增「後台導覽在窄螢幕收合為抽屜選單，且任何寬度都不溢出」需求；既有「後台導覽入口依頁面權限規則顯示」需求的行為不變，但抽屜內的選單同樣適用。

## Impact

- 程式碼：`web/src/layouts/AdminLayout.vue`（template、script、`<style scoped>`）
- 測試：`web/src/layouts/AdminLayout.test.ts` 新增抽屜行為測試；`web/build-checks/bundle-splitting.test.ts` 的元件對照表新增 `el-drawer`（BS-011 規定未列入對照表的標籤直接失敗）
- 版面溢出無法在 jsdom 驗證，沿用 mobile-layout-p0-fixes 的做法，以腳本化的瀏覽器量測取代（見 design.md「AC 測試對應例外」）
- 不影響後端、API、DB；不新增相依套件（`el-drawer` 已在 Element Plus 內；後台頁面延遲載入，不影響買家首屏）
