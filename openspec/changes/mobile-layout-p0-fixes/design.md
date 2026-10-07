## Context

`EventDetailPage.vue` 的版面為 `.layout { display: grid; grid-template-columns: 320px 1fr }`，`@media (max-width: 720px)` 時改為 `1fr`。2026-10-08 瀏覽器實測（390px 同源 iframe）量到：

- `.layout` 的 computed `grid-template-columns` 為 `454px`，頁面 `scrollWidth` 為 470。
- 最寬的子元素是 `.quick-pick`（`display: flex`，沒有 `flex-wrap`），內含 110px 下拉選單、110px 數字輸入、「自動選位並送出訂單」按鈕與標籤，最小寬度約 452px。

根因是 `1fr` 等同 `minmax(auto, 1fr)`：grid 軌道的下限是內容的最小寬度（min-content），所以一個不能換行的子元素就會把整個軌道撐寬，連帶讓同一欄的資訊區（單欄時與購票區共用同一軌道）一起溢出。

`index.html` 的 `lang="en"` 是 Vite 範本的預設值，從專案建立之後就沒有改過。

## Goals / Non-Goals

**Goals:**
- 320px 至 720px 寬時，活動詳情頁沒有頁面層級的橫向捲動。
- 桌面版的雙欄版面不變。
- `lang` 宣告正確。

**Non-Goals:**
- 排隊中的驗證碼畫面（`.captcha-row`，含固定 160px 輸入框）、資料載入失敗與找不到活動的 `el-empty` 狀態：前者要進入排隊才會出現，需另行建立情境，留給買家探索頁改版 change 處理，並由 tasks 4.3 記錄到審視報告第 7 節「仍未實測」，避免遺漏；後者內容只有一行文字，溢出風險低。
- 座位按鈕的觸控尺寸、sticky 結帳列、錯誤訊息位置（報告 U5／U6／R4，留給改版 change）。
- 其他頁面的 RWD（實測活動列表與登入頁在 390px 寬時沒有溢出；後台頁面需要登入才能測，留待後續）。
- 色票與字級。

## Decisions

### 決策 1：grid 軌道改用 `minmax(0, 1fr)`，同時讓 `.quick-pick` 與 `.count-ticket-row` 換行

三處都要改，各自負責不同的事：
- `.quick-pick` 加 `flex-wrap: wrap`：消除這次溢出的來源。
- `.count-ticket-row` 加 `flex-wrap: wrap`：計數購票列同樣是不換行的 flex 列（價格與可售數量 `white-space: nowrap`，加上固定 110px 的張數輸入與三個 12px gap），在 320px 視窗（扣除頁面左右 padding 後可用 288px）可能放不下。這列只有已登入才會出現，修改前的實測沒有量到，但條件相同，一併處理。
- `.layout` 的 `1fr` 改成 `minmax(0, 1fr)`（桌面 `320px minmax(0, 1fr)`、窄螢幕 `minmax(0, 1fr)`）：讓軌道不再被任何子元素的最小寬度撐開。之後若有其他不換行的內容，溢出只會局限在該元素本身，不會把整欄連同資訊區一起撐寬。

**替代方案**：
- 只加 `flex-wrap`：可以修好這次的問題，但只要未來購票區再新增一個不換行的元件，同樣的問題就會再次出現。
- 在頁面加 `overflow-x: hidden`：只是把溢出藏起來，被裁切的內容使用者仍然看不到，等於掩蓋問題，不採用。

### 決策 2：`lang` 使用 `zh-Hant-TW`

產品介面是台灣繁體中文用語（例如「實名」「核銷」），比單用 `zh-Hant` 更精確。螢幕閱讀器（NVDA、VoiceOver）都能辨識這個 BCP 47 標記。

### 決策 3：版面驗收用腳本化的瀏覽器量測，`lang` 用 Vitest

jsdom 不做版面計算（`scrollWidth`、grid 軌道寬度恆為 0），無法用 Vitest 驗證溢出。專案目前也沒有 Playwright 等 E2E 框架，只為這一條需求引入新框架，不符合 Rule 2（最小解法）。

因此：
- **溢出**：以固定的量測腳本（同源 iframe 設定 320／390／720／721／800／1280px 寬，其中 720 與 721 是 media query 斷點兩側、等待載入、讀取 `scrollWidth` 與資訊欄寬度）在瀏覽器實測，每個 Scenario 對應一項量測，並把數值記錄在 tasks.md。修改前先量一次基準值，必須重現 470 的失敗；修改後再量，用以證明這個檢查真的能抓到問題。
- **`lang`**：Vitest 以 `import indexHtml from '../index.html?raw'` 取得檔案內容，用 jsdom 的 `DOMParser` 解析後，斷言 `documentElement.lang === 'zh-Hant-TW'`。這是對建置輸入的直接斷言，有人改回去就會失敗。不使用 `node:fs` 讀檔，因為 `tsconfig.app.json` 涵蓋 `src/**/*.ts`，而 `types` 只有 `vite/client`，沒有 node 型別，`npm run build` 的 `vue-tsc -b` 會失敗。`?raw` 的型別已經由 `vite/client` 宣告，不需要修改型別設定。

**不採用**：用 Vitest 讀取 `.vue` 的 `<style>` 字串，斷言其中含有 `minmax(0` 或 `flex-wrap`。這只能驗證寫法，不能驗證行為：換一種同樣有效的寫法會誤報失敗，加入新的不換行元件造成溢出時卻抓不到（違反 Rule 9）。

## Risks / Trade-offs

- [瀏覽器量測沒有自動化，未來的修改可能再次造成溢出] → 這次先接受這個風險。量測腳本記錄在 tasks.md 方便重跑；之後的改版 change 若引入 E2E 框架，再把這項量測自動化。
- [已登入狀態（計數票種、排隊驗證碼畫面）需要登入才能量測，而登入要過驗證碼，自動化工具不代為完成] → 該 Scenario 的量測由使用者在瀏覽器手動登入後再執行；在這之前，tasks 的該項維持未勾選，不得視為完成。
- [`minmax(0, 1fr)` 讓內容可以比軌道更寬（溢出元素本身）] → 這只說明溢出由誰承擔：溢出局限在單一元素，比整欄被撐寬更容易發現與修正。單一元素溢出時，頁面的 `scrollWidth` 仍然會變大，**不代表允許頁面層級的橫向捲動**。真正讓需求成立的是兩個 `flex-wrap`，`minmax(0, 1fr)` 只是第二道防線。
- [瀏覽器量測取代自動化測試] → 見下方「AC 測試對應例外」。

## AC 測試對應例外

| 項目 | 內容 |
| --- | --- |
| 偏離的規則 | CLAUDE.md「OpenSpec 工作流規則 → Task 執行前」第 2 點：每條 AC 至少對應一項單元測試或整合測試 |
| 適用範圍 | 僅限 BW-MOBILE-001～005（版面溢出）。BW-LANG-001 仍以 Vitest 單元測試驗證，不適用此例外 |
| 替代驗證 | tasks.md 1.2 的瀏覽器量測腳本；修改前先量基準值，並且必須重現失敗 |
| 理由 | 見決策 3：jsdom 不做版面計算；只為這一條需求引入 E2E 框架，不符合最小解法 |
| 剩餘風險 | 沒有回歸保護，之後的修改可能再次造成溢出而不被發現 |
| 解除條件 | 專案引入 E2E 框架（例如 Playwright）後，將此量測改寫為自動化測試並刪除本例外 |
| 核准狀態 | **已核准（2026-10-08）**：使用者在 spec review 對話中接受此偏離 |
