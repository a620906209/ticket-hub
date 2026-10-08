## Context

色票集中在 `web/src/styles/morandi.css`：一組自訂 token（`--color-*`，給手刻樣式用）加上覆蓋 Element Plus 2.14.4 的 CSS 變數（`--el-*`）。`main.ts` 先 import Element Plus 的 CSS，再 import `morandi.css`，所以後者會覆蓋預設值。2026-10-08 以 `grep -rnE "#[0-9a-fA-F]{3,8}\b|rgba?\(|hsla?\(" --include=*.vue web/src` 盤點，`.vue` 檔裡寫死的顏色共四處，都不需要修改：

- `EventDetailPage.vue` 選中座位的 `color: #fff`（背景為 `--el-color-primary`）：文字色，由 WCC-001「白字對 primary base」同一組對比涵蓋。
- `RedemptionScannerPage.vue` 掃描器的 `background: #000`：相機畫面的底色，不承載文字或 UI 元件邊界。
- `RealNamePage.vue` 確認對話框遮罩的 `background: rgb(0 0 0 / 45%)`：裝飾性遮罩，對話框本體使用 `--color-bg-elevated` 與色票文字色，對比由色票 token 決定，不受遮罩影響。
- `EventListPage.vue` 活動卡片的 `box-shadow: 0 2px 8px rgb(0 0 0 / 6%)`：裝飾性陰影，不是辨識元件所需的邊界（WCAG 1.4.11 不規範）；卡片邊界見 Non-Goals 的 `--color-border` 一項。

2026-10-08 盤點專案實際使用的 Element Plus 元件與狀態：

- 按鈕：實心 primary／danger／default，以及 `text` 按鈕；沒有使用 `plain` 與 `link` 按鈕。
- `el-alert` 40 處（`type="error"` 28、`warning` 4，皆為預設的淺色樣式）、`el-tag` 4 處、`ElMessage.success／error`。
- 輸入類：`el-input`、`el-select`、`el-input-number`、`el-date-picker`、`el-checkbox`、`el-switch`。

Element Plus 2.14.4 的實心語意色按鈕（以 `.el-button--primary` 為例）：hover 背景與邊框為 `light-3`、active 背景為 `dark-2`、焦點框 `--el-button-outline-color` 為 `light-5`；預設按鈕的焦點框為 `--el-color-primary-light-5`。輸入框 hover 邊框 `--el-border-color-hover` 沒有被 `morandi.css` 覆蓋，仍是 Element Plus 預設的冷灰色。

## Goals / Non-Goals

**Goals:**
- spec 列出的所有組合達到 WCAG AA（文字 4.5、UI 元件 3.0）。
- 保留莫蘭迪的色相與低彩度。
- 以自動化測試防止之後改色時退回不合格的值。

**Non-Goals:**
- 座位網格的圖例、已售座位的樣式與 `disabled`（報告 U7／P4／A1，屬於購票頁 P1 change）。
- 按鈕按壓縮放（`scale(0.97)`）、hover 限縮在 `(hover: hover)` 裝置、調整轉場時間：屬於動效，留給之後的改版 change。觸控裝置點擊後殘留的 hover 狀態是較深的顏色，仍然達到 AA。
- `plain`／`link` 按鈕的 hover 文字色（例如 `--el-button-hover-link-text-color` 為 `light-5`）：專案沒有使用，未來使用時再處理。
- type scale、間距 token、bundle 切割、深色模式。
- 手刻可選取元件的邊界：買家端的票種與座位選擇控制項（`EventDetailPage.vue` 約 865–895 行）、活動卡片（`EventListPage.vue` 約 62–71 行）使用 `--color-border`（`#dcd5c9`，白底約 1.4:1）。這些元件的選取狀態與外觀屬於購票頁 P1 change 與買家探索頁改版，`--color-border` 在本 change 不調整。
- 輸入框 focus 邊框、checkbox／switch 開啟狀態使用 primary base：WCC-003 已要求 primary 對白底 ≥ 4.5，高於 UI 元件的 3.0，不另立 Scenario。
- `--color-*` 改為引用 `--el-*`（報告 V4）：只改數值，不改 token 結構；兩套 token 都由測試涵蓋，數值不一致時對比測試仍然會分別檢查。

## Decisions

### 決策 1：在 OKLCH 只降明度，條件是同時滿足白字與標籤文字

每一個語意色保持 OKLCH 的色相與彩度，以二分搜尋找出**最淺**、且同時滿足下列兩個條件的明度（各留 0.1 的餘裕）：

- 白字對 base ≥ 4.5（實心按鈕）
- base 對自己的 `light-9`（base 與白色以 1:9 混合）≥ 4.5（標籤與提示訊息）

實際卡住的是第二個條件：標籤背景會跟著 base 一起變深，所以 base 要比「只看白字」再暗一些。候選值：

| 色 | 原值 | 新 base | 白字 | 標籤文字 | 色相差 |
| --- | --- | --- | --- | --- | --- |
| primary | `#8c9a9e` | `#616f72` | 5.22 | 4.60 | 4.69° |
| success | `#96a87f` | `#61714b` | 5.29 | 4.65 | 0.26° |
| warning | `#c9a66c` | `#87672d` | 5.24 | 4.61 | 0.47° |
| danger | `#b97c6d` | `#975d4f` | 5.26 | 4.62 | 0.10° |
| info | `#a39c93` | `#726b63` | 5.25 | 4.61 | 3.19° |

primary 與 info 的彩度很低（約 0.017），8-bit 量化造成的色相偏移就比較大，所以 spec 的色相容許值訂在 5°。

`light-3`／`5`／`7`／`8`／`9` 依 Element Plus 自己的公式（base 與白色混合 30%～90%）重新計算，`dark-2` 為 base 與黑色混合 20%。實作時以 task 2.1 的計算腳本產生確切值，不手調。

**替代方案**：
- 按鈕改用 `dark-2`、base 不動：只修好按鈕，標籤、提示訊息、連結仍然不合格。
- 改成 Element Plus 預設的高彩度藍：違反「保留莫蘭迪」的決定。

### 決策 2：實心語意色按鈕 hover 變深、active 更深

調暗後，`light-3` 的白字只有約 2.9，因此在 `morandi.css` 為五個 `.el-button--<語意色>` 覆寫：

- hover 背景與邊框：`dark-2`（混 20% 黑）
- active 背景與邊框：混 35% 黑（以字面值寫在覆寫規則中，Element Plus 沒有對應的 token）
- 焦點框 `--el-button-outline-color`：base

預設按鈕 `.el-button` 的焦點框同樣改為 `--el-color-primary`。`.el-button` 與 `.el-button--<語意色>` 權重相同，後出現的會覆蓋前者，所以 `.el-button` 區塊必須排在五個語意色區塊之前，否則語意色按鈕的焦點框會被蓋成 primary；測試斷言這個順序（WCC-008）。

text 按鈕（專案在後台與訂單明細使用，含 primary、danger）不需要覆寫：Element Plus 的 text 按鈕 hover 背景為 `--el-fill-color-light`（`morandi.css` 已覆寫為 `#f3f0ea`），active 背景為 `--el-fill-color`（未覆寫，Element Plus 預設 `#f0f2f5`）。新 primary 對兩者為 4.59／4.65，danger 為 4.62／4.69，一般文字為 6.29／6.38，由 WCC-011 把關。預設按鈕的 hover（primary 文字在 `primary-light-9` 背景上）由決策 1 的標籤條件涵蓋，不需要另外覆寫。

`morandi.css` 在 Element Plus 的 CSS 之後載入，選擇器的權重相同，所以覆寫會生效；task 3.2 在瀏覽器以 computed style 確認。

**替代方案**：hover 維持變淡、只保證靜態狀態合格。改動較少，但 hover 時白字只有 2.9，使用者已決定不採用。

### 決策 3：只調整輸入類邊框，並補上 hover 邊框

- `--el-border-color`：`#dcd5c9` → 約 `#8f897e`（白底 3.47、頁面底 3.05）。
- `--el-border-color-hover`：新增覆寫，約 `#7a746b`（頁面底 4.07），比一般邊框深。
- `--el-border-color-light`／`lighter`：不變。表格與分隔線屬於裝飾性邊界，不受 WCAG 1.4.11 規範，維持輕盈的版面。

`--el-border-color` 也會讓預設按鈕的邊框與 `el-divider` 變深，這兩者同樣可以接受。

### 決策 4：次要文字與 placeholder 調暗到 4.5

- 次要文字：`#8b8378` → 約 `#736b61`（頁面底 4.61、白底 5.24）。
- Placeholder：`#b3ab9e` → 約 `#7c7468`（白底 4.61）。調暗後與次要文字接近，也更接近輸入值的顏色（`--el-text-color-regular` `#5c5750`），使用者可能把 placeholder 誤認為已填寫的內容。

  2026-10-08 盤點 14 個帶 `placeholder` 的欄位，其中 11 個在自己的 `el-form-item label` 內；另外 3 處只靠 placeholder 辨識欄位：
  - `EventDetailPage.vue` 排隊驗證碼輸入框（約 657 行）：沒有 label，靠旁邊的驗證碼圖片（`alt="驗證碼圖片"`）、「看不清楚？換一張」按鈕與 placeholder「請輸入驗證碼」辨識。
  - `VenueListPage.vue` 批次產生與手動新增的「分區代碼」「座位號碼」輸入框（約 260、280–281 行）：只有群組 label（「批次產生」「手動新增」），個別欄位靠 placeholder 辨識。

  對這 3 處而言，placeholder 從 2.27 提高到 4.61，會比修改前**更**容易辨識，誤認風險則和其他欄位相同。補上可見 label 或 `aria-label`（WCAG 3.3.2／1.3.1）屬於表單結構調整，不是色票問題，不在本 change 範圍：驗證碼欄位留給購票頁 P1 change，後台場館頁留給之後的後台表單改善。

### 決策 5：Vitest 直接解析 `morandi.css` 計算對比度

測試以 `import morandiCss from './morandi.css?raw'` 取得原始字串（沿用 `mobile-layout-p0-fixes` 的做法：`?raw` 的型別由 `vite/client` 宣告，不需要 node 型別），解析 `:root` 與 `.el-button`、`.el-button--<語意色>` 區塊的自訂屬性，遞迴展開 `var()`，再以 WCAG 相對亮度公式計算 spec 列出的每一組對比。`color-scheme` 同樣以 `?raw` 讀取 `style.css` 驗證。

這是對建置輸入的直接斷言：任何人把顏色改回不合格的值、刪掉 hover 覆寫，或讓 `var()` 指向不存在的 token，測試就會失敗。task 1.2 要求先用**修改前**的 `morandi.css` 跑一次，必須失敗，證明測試抓得到問題（Rule 9）。

測試輔助函式（解析 CSS、展開 `var()`、相對亮度、OKLCH 轉換）需要自己的單元測試，用已知的參考值核對（例如黑對白 21:1、`#767676` 對白約 4.54），避免公式寫錯導致測試假通過。

**不採用**：
- 在 jsdom 讀 computed style：jsdom 對 CSS 自訂屬性的 cascade 支援有限，結果不可靠。
- 引入 E2E 框架或 axe-core：spec 的所有組合都能從色票原始碼算出來，不需要渲染；只為這個需求引入新框架不符合 Rule 2。

cascade 是否真的生效（覆寫有沒有被 Element Plus 更高權重的規則蓋掉），由 task 3.2 在瀏覽器確認。這一步是實作驗證，不取代任何 AC 的測試，因此不需要 AC 測試對應例外。

## Risks / Trade-offs

- [全站變深，莫蘭迪的「霧感」減弱] → 這是使用者接受的取捨（保留色相、調暗到 AA）；之後的改版以字級與留白營造編輯風格，不依賴淺色。task 3.3 逐頁目視確認，並截圖修改前後的畫面供使用者確認。
- [success、warning、info 調暗後明度接近，色覺異常者較難只靠顏色分辨] → 狀態標籤都帶有中文文字，符合 WCAG 1.4.1（不只靠顏色傳達資訊）；不在本 change 處理。
- [primary 色相差 4.69°，接近 5° 上限] → 若微調時超過上限，測試會失敗；屆時改調整明度搜尋的餘裕，而不是放寬 spec。
- [測試只驗證色票原始碼，不驗證 cascade] → 由 task 3.2 的瀏覽器 computed style 檢查補上，修改前先記錄 hover 變數仍解析為 `light-3` 的值作為基準。
- [Element Plus 升版後變數名稱改變，覆寫可能悄悄失效] → 測試只能保證 `morandi.css` 本身；升版時須重跑 task 3.2 的瀏覽器檢查（記錄在 design 的這一條，供升版 change 參考）。
