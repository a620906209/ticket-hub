# Ticket-Hub 前端 UI/UX 審視報告

- 日期：2026-10-08
- 範圍：`web/`（Vue 3 + Element Plus 2.14，買家端與後台共 19 個頁面／版型）
- 方法：
  - 靜態閱讀版型、路由、樣式 token 與主要頁面
  - 以 WCAG 2.x 公式計算色彩對比度（腳本計算，非目測）
  - 在容器內執行 `vite build`，量測產出物大小
- **瀏覽器實測（同日補做，見第 8 節）**：用 claude-in-chrome 測 dev server 與 production build（本機靜態伺服器，gzip），RWD 以 390px 寬的同源 iframe 量測。標記「✅ 實測」的項目已驗證；仍為「推測」的項目列在第 7 節。
- **未做**：Lighthouse 分數（擴充工具無法執行；測試分頁在背景，瀏覽器不會產生 FCP／LCP 繪製事件）、實機觸控、螢幕閱讀器。

嚴重度：**P0** 影響可用性或合規，應優先處理；**P1** 明顯摩擦；**P2** 改善項。

---

## 0. 總結

| 維度 | 評分 | 一句話 |
| --- | --- | --- |
| 可用性 | ★★★☆☆ | 錯誤分流邏輯很細，但購票頁的資訊動線與錯誤訊息位置不利於操作 |
| 視覺設計 | ★★★☆☆ | 有 token 系統（莫蘭迪），但字級與間距沒有規則，inline style 散落 |
| 效能感 | ★★★☆☆ | 各頁都有 loading 回饋；但缺少骨架畫面，座位狀態只靠顏色區分 |
| 無障礙 | ★☆☆☆☆ | **色票整組不符合 WCAG AA**，`lang="en"`，座位網格缺少語意 |
| 效率指標 | ★★☆☆☆ | 單一 JS bundle 1.2 MB（gzip 394 KB），沒有路由切割；RWD 只有 1 個 breakpoint |

前三優先（建議先開一個 change 處理）：

1. **P0 色彩對比度**：主色按鈕、連結與所有狀態色都未達 AA（第 4 節）。
2. ✅ **已修正（bundle-splitting）** **P0 Bundle 切割**：路由 lazy-load，Element Plus 改為按需引入，`jsqr` 移出買家端 bundle（第 5 節）。首屏 gzip 由 390,894 B 降為 151,517 B；CSS 仍為全量（延後處理）。
3. **P0 手機版活動詳情頁會整頁橫向捲動**（✅ 實測，R3）：一行 CSS 就能修好，應最先處理。
4. **P1 購票頁動線**：錯誤訊息貼近操作位置、加上座位圖例、固定在底部的結帳列（第 1 節）。

---

## 1. 可用性

### 1.1 核心流程路徑

買家主流程：活動列表 → 活動詳情（選位／計數／排隊）→ 下單結果 → 我的訂單 → 付款／取消。

| # | 嚴重度 | 問題 | 位置 | 建議 |
| --- | --- | --- | --- | --- |
| U1 | P1 | 活動卡片只顯示標題、時間與販售狀態，**沒有海報、價格區間與場館**，買家無法在列表頁判斷要不要點進去 | `pages/buyer/EventListPage.vue:39-43` | 卡片加上海報縮圖（`posterUrl` 已有）與最低票價 |
| U2 | P1 | 主辦方或 Admin 從買家端進入後台**沒有入口**，只能透過「我的主辦方」切換後跳轉，或自行輸入 `/admin`（推測，需確認 MyOrganizersPage 的行為） | `layouts/BuyerLayout.vue:21-31` | 下拉選單加入「進入後台」（`organizerId` 有值時顯示） |
| U3 | P2 | 後台沒有品牌與回到前台的連結；未切換主辦方時只顯示灰字「尚未切換主辦方」，看不出可以點擊 | `layouts/AdminLayout.vue:44-46` | 加上品牌連結，指示器改成按鈕樣式或加上圖示 |
| U4 | P2 | 登入後若沒有 redirect，Admin 會直接導到 `/admin`；但 `/admin` 需要先切換主辦方，否則會被守衛再導到「我的主辦方」，造成兩次跳轉 | `pages/buyer/LoginPage.vue:44-46`、`router/index.ts:96` | 直接導到「我的主辦方」，或導到 `admin-organizers` |

### 1.2 資訊層級

| # | 嚴重度 | 問題 | 位置 | 建議 |
| --- | --- | --- | --- | --- |
| U5 | P1 | 活動詳情頁的 `errorMessage` 固定顯示在**頁面頂端**，但觸發錯誤的操作（點座位超過限購、區域隨選張數不足）在右欄中下方；手機單欄時，資訊欄排在購票區上方，錯誤訊息會離操作位置更遠，買家可能完全看不到 | `pages/buyer/EventDetailPage.vue:681` | 與操作相關的錯誤改用就近顯示（`quick-pick` 下方或 summary 區），或 `ElMessage` 搭配頂端 alert |
| U6 | P1 | 「送出訂單」與總金額放在**所有座位網格之後**，座位一多就必須捲到底才能結帳，也看不到已選內容 | `pages/buyer/EventDetailPage.vue:812-822` | 改為 sticky 底部結帳列（已選張數＋總額＋按鈕） |
| U7 | P1 | 座位網格**沒有圖例**；可售（`#fff`）與已售（`#f3f0ea`）背景的對比只有約 1.14:1，看起來幾乎一樣，只能從游標形狀分辨 | `EventDetailPage.vue:944-969` | 加圖例；已售座位加上斜線或 × 圖示，不能只靠顏色區分（WCAG 1.4.1） |
| U8 | P2 | 座位的 `title` 直接顯示英文 enum（`Available`／`Locked`／`Sold`），與其他頁面已中文化的狀態標籤不一致 | `EventDetailPage.vue:781` | 改用 `statusLabels` 的對照表 |
| U9 | P2 | 後台活動列表把 `venueId`、`seatMapId` GUID 當成主要欄位，佔用寬度但對營運人員沒有意義 | `pages/admin/EventListPage.vue:163-164` | 改顯示場館名稱，或收進展開列 |
| U10 | P2 | 日期一律使用 `toLocaleString()`，含秒數，格式也取決於瀏覽器語系，同一個畫面可能出現不同格式 | 多處（EventList、EventDetail、Admin） | 抽出共用的 `formatDateTime`（EventDetailPage 已有一份，可以提升到 utils） |

### 1.3 使用摩擦點

| # | 嚴重度 | 問題 | 位置 | 建議 |
| --- | --- | --- | --- | --- |
| U11 | P1 | 「自動選位並送出訂單」**一鍵直接下單**，送出前看不到抽中哪些座位；雖然待付款訂單可以取消，但這會鎖住座位並佔用限購額度 | `EventDetailPage.vue:767-769`、`handleQuickPick` | 改成「自動選位」只填入座位，由買家按送出；或送出前顯示確認視窗並列出座位（屬於產品決策，需要確認） |
| U12 | P2 | 登入頁的驗證碼圖片與輸入框拆成兩個 form-item，標籤分別是「驗證碼」與「驗證碼輸入」；驗證碼還沒載入時登入按鈕會 disabled，但沒有說明原因 | `pages/buyer/LoginPage.vue:83-95` | 合併成同一列（輸入框＋圖片＋換一張），disabled 時顯示「驗證碼載入中」 |
| U13 | P2 | 主辦方審核的「核准／駁回」沒有確認對話框（取消訂單有，做法正確） | `pages/admin/AdminOrganizersPage.vue` | 駁回這類不可逆操作補上 `ElMessageBox.confirm` |
| ✅ | — | 做得好：取消訂單的確認框刻意不用「確定／取消」按鈕文字（`usePendingOrderActions.ts:116`）；驗證碼錯誤會自動換新圖；限購用 `el-input-number` 的 `max` 直接擋住，不需要事後報錯 | | |

---

## 2. 視覺設計

### 2.1 設計系統一致性

| # | 嚴重度 | 問題 | 證據 | 建議 |
| --- | --- | --- | --- | --- |
| V1 | P1 | **字級沒有規則**：Vue 檔裡出現 12／13／14／18／22／24／32px 共 7 種，`h1` 有時 22px、有時 24px，有時沿用預設值 | `grep font-size` 統計 | 定義 type scale token（例如 12/14/16/20/24/32），寫進 `morandi.css` |
| V2 | P1 | **頁面寬度沒有規則**：`max-width` 有 360／480／520／720／800／1080 共 6 種 | 同上 | 收斂成 3 種（表單窄版 480、內容 800、列表 1080） |
| V3 | P2 | 15 個頁面共 40 處 inline `style="margin-bottom: 16px"` 這類寫法，間距散落在 template 裡 | `grep 'style="'` | 定義間距 token（4/8/12/16/24/32），改用 class |
| V4 | P2 | 後台版型用 `--el-*` 變數，買家版型用自訂的 `--color-*` 變數；兩套 token 的值雖然相同，但未來改色容易漏改一邊 | `AdminLayout.vue` vs `BuyerLayout.vue` | 自訂 token 改成引用 EP token（`--color-primary: var(--el-color-primary)`） |
| V5 | P2 | 寫死的顏色：選中座位 `color: #fff`（`EventDetailPage.vue:876` 附近）、掃描器 `#000` | grep | 改用 token |

### 2.2 色彩運用

- 品牌色系（莫蘭迪低飽和）概念一致，`morandi.css` 也有完整覆蓋 EP 的 9 階色。這點做得好。
- **問題在於低飽和＋高明度，讓所有語意色都失去對比**（見第 4 節表格）。成功、警告、資訊三色的明度接近，色覺異常者幾乎無法分辨 `el-tag` 的狀態。
- ✅ **已修正（palette-contrast-aa，改為只宣告 `light`）** `style.css` 宣告了 `color-scheme: light dark`，但**沒有 dark token**。OS 開啟深色模式時，原生捲軸與表單元件會變成深色，頁面本體卻仍是淺色，畫面不協調（推測，需實測）。建議改為只宣告 `light`，或補齊 dark token。

### 2.3 排版邏輯

- 活動詳情頁採左資訊、右購票的 320px＋1fr 雙欄，閱讀流合理。
- 右欄依序疊了 6 種 alert（販售狀態、未登入、實名、排隊、驗證碼錯誤、限購）。最壞情況下會同時出現 3–4 個 alert，把真正的選位區推到首屏以下。建議同一時間只顯示一個「主要阻擋原因」，優先順序為：停售 > 未登入 > 實名 > 排隊。

---

## 3. 效能感

| # | 嚴重度 | 問題 | 位置 | 建議 |
| --- | --- | --- | --- | --- |
| P1 | P1 | 首次載入時，`main.ts` 以 top-level `await authStore.bootstrapAsync()` 擋住 mount，期間只顯示純文字「載入中…」，沒有任何樣式（字型與背景都是瀏覽器預設）。✅ 實測：白底黑字置中，與品牌米色底不同，載入完成時會閃一下 | `index.html:11`、`main.ts:23` | 在 `index.html` 內嵌最小 CSS（背景色＋spinner），讓首屏跟品牌一致 |
| P2 | P1 | 活動列表把 `v-loading` 掛在 grid 上；初次載入時 grid 是空的、高度為 0，**遮罩幾乎看不到**。✅ 實測（dev server，載入約 1–4 秒）：畫面只有「活動列表」標題與一個淡色小點，看起來像空白頁 | `EventListPage.vue:38` | 改用 `el-skeleton` 卡片骨架，或給容器最小高度 |
| P3 | P2 | 全站沒有骨架畫面，都是遮罩加 spinner；表格類頁面可以接受，但列表與詳情頁用骨架會更順 | 12 頁使用 `v-loading` | 列表與詳情頁改用骨架 |
| P4 | P2 | 座位按鈕沒有 `:active` 樣式；已售座位也沒有設 `disabled`，hover 時邊框照樣變色，暗示可以點。（更正：✅ 實測鍵盤 Tab 時有瀏覽器預設的 `outline: auto` 焦點框，`:focus-visible` 並非缺失，只是未依品牌色客製） | `EventDetailPage.vue:956-969` | 已售座位加 `disabled` 並移除 hover；焦點框可改用品牌色（選做） |
| ✅ | — | 做得好：送出類按鈕都有 `:loading`；販售時鐘在切回分頁時立即重算；排隊輪詢使用遞迴 setTimeout，避免請求重疊；動畫極少（活動卡片 0.2s hover、掃描器 120ms 淡入），都屬於回饋型動畫，不會干擾操作 | | |

---

## 4. 無障礙

### 4.1 色彩對比度（WCAG AA：一般文字 ≥ 4.5，大字／UI 元件 ≥ 3.0）

| 組合 | 前景 / 背景 | 比值 | AA 一般文字 | AA 大字/UI |
| --- | --- | --- | --- | --- |
| 主要文字 | `#4a4643` / `#f3f0ea` | 8.21 | ✅ | ✅ |
| 一般文字 | `#5c5750` / `#fff` | 7.16 | ✅ | ✅ |
| 次要文字 | `#8b8378` / `#fff` | 3.74 | ❌ | ✅ |
| 次要文字（頁面背景） | `#8b8378` / `#f3f0ea` | 3.29 | ❌ | ✅ |
| Placeholder | `#b3ab9e` / `#fff` | 2.27 | ❌ | ❌ |
| 連結／主色文字 | `#8c9a9e` / `#f3f0ea` | **2.55** | ❌ | ❌ |
| **主色按鈕白字** | `#fff` / `#8c9a9e` | **2.90** | ❌ | ❌ |
| 成功按鈕白字 | `#fff` / `#96a87f` | 2.56 | ❌ | ❌ |
| 警告按鈕白字 | `#fff` / `#c9a66c` | 2.29 | ❌ | ❌ |
| 危險按鈕白字（取消訂單） | `#fff` / `#b97c6d` | 3.41 | ❌ | ✅ |
| 資訊按鈕白字 | `#fff` / `#a39c93` | 2.72 | ❌ | ❌ |
| el-tag／el-alert 危險色 | `#b97c6d` / `#f3e7e4` | 2.82 | ❌ | ❌ |
| el-tag／el-alert 警告色 | `#c9a66c` / `#f6f0e5` | **2.02** | ❌ | ❌ |
| el-tag／el-alert 成功色 | `#96a87f` / `#eef1e8` | 2.24 | ❌ | ❌ |
| el-tag／el-alert 資訊色 | `#a39c93` / `#eeece9` | 2.30 | ❌ | ❌ |
| 輸入框邊框 | `#dcd5c9` / `#fff` | 1.46 | — | ❌（UI 元件需 ≥ 3） |

**結論（P0）**：除了主要文字以外，所有語意色都不符合 AA。狀態標籤（訂單狀態、販售狀態、票券狀態）是買家判斷「要不要付款」「能不能買」的關鍵資訊，目前在戶外手機螢幕上很難閱讀。

✅ **已修正（palette-contrast-aa）**：五個語意色保留 OKLCH 色相與彩度、只調暗明度；標籤文字維持 base 色（未採下方「改用 dark-2 再暗一階」的建議，改為讓 base 本身對 `light-9` 達到 4.5）。實心按鈕 hover 改為變深（`dark-2`）、active 再深一階；回歸測試見 `web/src/styles/morandi.contrast.test.ts`。修正後的值：

| 組合 | 前景 / 背景 | 比值 |
| --- | --- | --- |
| 次要文字 | `#736b61` / `#fff`、`#f3f0ea` | 5.24、4.61 |
| Placeholder | `#7c7468` / `#fff` | 4.61 |
| 連結／主色文字 | `#616f72` / `#f3f0ea` | 4.59 |
| 按鈕白字 primary／success／warning／danger／info | `#fff` / `#616f72`、`#61714b`、`#87672d`、`#975d4f`、`#726b63` | 5.22、5.29、5.24、5.26、5.25（hover ≥ 7.23、active ≥ 9.37） |
| el-tag／el-alert（base / `light-9`）同上五色 | 例：警告 `#87672d` / `#f3f0ea` | 4.60、4.65、4.61、4.62、4.61 |
| 輸入框邊框／hover 邊框 | `#8e877c`、`#797369` / `#fff`、`#f3f0ea` | 3.56／3.13、4.70／4.13 |

**修法建議**（保留莫蘭迪色調，只調整明度）：
- 主色與語意色的 `base` 往暗調整，讓白字的對比至少達到 4.5（例如主色 `#8c9a9e` → 約 `#5f6e72`）；按鈕改用 `dark-2` 階也是一種做法。
- `el-tag` 與 `el-alert` 的文字改用 `dark-2` 再往暗一階，而不是用 base 色。
- 次要文字 `#8b8378` → 約 `#6f685e`（在 `#f3f0ea` 上約 4.6）。
- 調整後重新執行對比度腳本驗證（腳本可直接沿用：WCAG 相對亮度公式，數十行 Node 程式即可）。

### 4.2 文字大小可讀性

- 根字級 16px，正常。但座位編號 12px、提示文字 13px，再搭配不到 AA 的次要文字色，在小螢幕上可讀性差（V1、4.1）。
- ✅ **已修正（mobile-layout-p0-fixes）** **P1**：`index.html` 設為 `lang="en"`，但內容全是繁體中文。螢幕閱讀器會用英文語音唸中文，瀏覽器的自動翻譯也會誤判。改成 `lang="zh-Hant-TW"`，一行即可修正。

### 4.3 鍵盤導航

| # | 嚴重度 | 問題 | 建議 |
| --- | --- | --- | --- |
| A1 | P1 | 座位網格每個座位都是 `<button>`，數百個座位都會進入 Tab 順序，鍵盤使用者要按幾百次 Tab 才能到「送出訂單」；已售座位沒有 `disabled`，同樣會被 Tab 到。✅ 實測：測試活動頁共 507 個可聚焦元素，其中 500 個是座位；座位沒有 `aria-pressed`／`aria-label`，`title` 為「A-56（Available）」 | 已售座位設 `disabled`；網格改成 roving tabindex（方向鍵移動），或在網格前加「跳到結帳」連結 |
| A2 | P1 | 座位沒有 `aria-pressed` 或 `aria-label`，螢幕閱讀器只會唸出「A-12」，不知道是否已選或已售 | 加上 `:aria-pressed="isSelected(seat)"` 與 `aria-label="A 區 12 號，可售"` |
| A3 | P2 | 買家導覽的會員下拉選單觸發元素是 `<span>`，Element Plus 的 dropdown 對 span 觸發元素的鍵盤支援有限（推測，需實測 Tab＋Enter） | 觸發元素改用 `<button>` |
| A4 | P2 | 全站只有 8 處 `aria-*`／`role`；動態錯誤 alert 沒有 `aria-live`，螢幕閱讀器不會唸出下單失敗 | 頂端錯誤區加上 `role="alert"`（`el-alert` 本身不帶 live region） |
| ✅ | — | 做得好：實名頁與核銷頁會在結果出現時主動移動 focus（`RealNamePage.vue:101`、`RedemptionScannerPage.vue:50`）；實名引導使用 `role="status"` | |

---

## 5. 效率指標

### 5.1 首屏載入與資源順序（`vite build` 實測）

> ✅ **已修正（bundle-splitting，2026-10-09）**：18 個頁面與 `AdminLayout` 改為 `() => import()`，Element Plus 元件 JS 改由 `unplugin-vue-components` 按需引入，`jsqr` 只隨驗票頁 chunk 載入。實測首屏（entry 靜態閉包 ∪ 首頁 chunk 閉包）gzip **151,517 B**（JS 102,346＋CSS 49,171），修正前同口徑為 390,894 B（約 −61%），build 共 61 個 chunk，由 `npm run test:bundle` 持續檢查（≤ 200 kB）。**CSS 仍為 `element-plus/dist/index.css` 全量引入**：按需樣式會在延遲載入的 chunk 裡較晚注入，蓋掉 morandi.css 的色票覆寫，因此使用者決定延後處理。下表與成因為修正前的紀錄。

| 產出物 | 原始 | gzip |
| --- | --- | --- |
| `index-*.js`（**唯一一支 JS**） | 1,212.80 kB | **394.23 kB** |
| `index-*.css` | 369.67 kB | 50.37 kB |
| `index.html` | 0.59 kB | 0.40 kB |

Vite 本身已對超過 500 kB 的 chunk 發出警告。成因：

1. **`router/index.ts` 靜態 import 全部 19 個頁面**，沒有任何 `() => import()`。買家第一次打開活動列表，就會一併下載後台、報表與相機掃描的程式碼。
2. **`app.use(ElementPlus)` 全量引入**，元件 JS 與 `element-plus/dist/index.css`（大部分是用不到的元件樣式）全部打包。
3. **`jsqr`**（QR 解碼，僅核銷頁使用）經由 `RedemptionScannerPage` 的靜態 import 進入主 bundle。
4. 首屏是串行的：下載 JS → 執行 → `await bootstrapAsync()`（refresh token 請求）→ mount → 活動列表 API。LCP 至少要等兩次 RTT 加上 394 KB 的解析時間。

**Core Web Vitals 預估**（推測，未用 Lighthouse 實測）：
- **LCP**：在慢速 4G 的手機上，394 KB gzip 的 JS 解析加上 bootstrap 請求，很可能超過 2.5s 的「良好」門檻。粗估（Lighthouse Slow 4G 約 1.6 Mbps、RTT 150ms）：JS＋CSS 約 431 KB 傳輸時間約 2.2s，加上 `/api/events` 45 KB 約 0.4s 與數次 RTT，**活動卡片出現約在 3s 以上**，尚未計入低階手機解析 1.2 MB JS 的時間。
- **CLS**：活動詳情頁的海報 `<img>` 沒有設 `width`／`height` 或 `aspect-ratio`，圖片載入後會把下方內容往下推（`EventDetailPage.vue:686`）。開賣提示原位替換，這個設計已經考慮到 CLS，值得肯定。
- **INP**：座位選取已改用 `Set` 做 O(1) 查找，風險低；數百顆座位按鈕的 DOM 數量仍值得在低階手機上實測。

**建議（依效益排序）**：
1. 路由全面 lazy-load：`component: () => import('../pages/...')`。後台與核銷頁獨立成 chunk，預期買家首屏 JS 會大幅下降（實際數字待重建後量測）。
2. Element Plus 改用 `unplugin-vue-components`＋`unplugin-auto-import` 按需引入（JS 與 CSS 都按需）。
3. 海報圖片加上 `aspect-ratio` 與 `loading="lazy"`（列表頁加上海報後尤其需要）。
4. `index.html` 內嵌品牌底色與 spinner（P1），讓等待 bootstrap 期間有一致的視覺。

### 5.2 響應式適配

| 斷點 | 狀態 |
| --- | --- |
| Desktop ≥ 1080 | ✅ 主要設計目標 |
| Tablet 720–1080 | ⚠️ 版面可用，但 1080px 容器在 768 的寬度下只剩 16px 左右邊距，表格欄位擁擠 |
| Mobile < 720 | ❌ **全站只有 1 個 `@media`**（EventDetailPage 雙欄改單欄，但實測仍溢出，見 R3）。✅ 實測 390px：活動列表、登入頁無溢出 |

| # | 嚴重度 | 問題 | 建議 |
| --- | --- | --- | --- |
| R1 | **P0** | 後台導覽是 `el-menu mode="horizontal"` 搭配 `:ellipsis="false"`，有 5 個選單項目、主辦方名稱與登出鈕，在手機寬度下**必然溢出**。偏偏**票券核銷頁是在手機上使用的**（相機掃描） | 手機寬度改用漢堡選單或開啟 `ellipsis`；核銷頁可以考慮做成精簡版型 |
| R2 | P1 | 後台表格（活動列表 9 欄、訂單列表）在手機上沒有任何收合策略 | 窄螢幕改用卡片列表，或固定前兩欄並允許橫向捲動 |
| R3 | **P0**（✅ 已修正：mobile-layout-p0-fixes） | ✅ 實測：390px 寬時，**活動詳情頁整頁寬 470px，會橫向捲動**，連資訊欄的標題、票價表都被裁切。根因：`.quick-pick` 不換行，最小寬度約 452px；`.layout` 在 `@media (max-width:720px)` 下是 `grid-template-columns: 1fr`，而 `1fr` 等於 `minmax(auto, 1fr)`，不會縮到比內容最小寬度更窄，於是整個 grid 欄被撐到 454px | `.layout` 改為 `minmax(0, 1fr)`（桌面版的 `320px 1fr` 也一併改），`.quick-pick` 加 `flex-wrap: wrap` |
| R4 | P2 | 座位按鈕 36×28px，**低於行動裝置建議的 44×44 觸控目標**（WCAG 2.5.8 AA 最低是 24×24，目前合格，但容易誤觸） | 手機寬度下放大到 40×40 |
| R5 | P2 | 登入／註冊表單 `label-width="80px"`，「驗證碼輸入」標籤在 360px 寬度下擠壓輸入框 | 手機寬度改用 `label-position="top"` |

---

## 6. 建議處理順序

| 批次 | 內容 | 預估影響 |
| --- | --- | --- |
| 1（快速修正） | `lang="zh-Hant-TW"`、已售座位 `disabled`＋`aria-*`、`.layout` 改 `minmax(0, 1fr)`＋`.quick-pick` 加 wrap（修 R3 P0）、海報 `aspect-ratio`、`color-scheme: light` | 小改動，幾乎沒有風險 |
| 2（P0） | 色票明度調整＋對比度回歸腳本；路由 lazy-load＋Element Plus 按需引入 | 合規與首屏效能 |
| 3（P1 UX） | 購票頁 sticky 結帳列、座位圖例、錯誤就近顯示、alert 優先順序；後台手機版導覽 | 核心轉換流程 |
| 4（P2） | type scale／spacing token、清除 inline style、日期格式共用化、骨架畫面 | 長期維護 |

批次 2、3 會改變既有 spec 描述的畫面行為（例如 buyer-web-ui 的購票區結構），依 CLAUDE.md 規定須先建立 OpenSpec change。批次 1 屬於「無對應 spec 的小型改動」，可以先實作，但 commit 須註明。

## 7. 待瀏覽器驗證清單

仍未實測：
- **A3**（會員下拉選單的鍵盤操作）、**R1**（後台導覽在手機上溢出）：都需要登入，而登入需要通過驗證碼，自動化工具不代為完成驗證碼，須由使用者登入後再測。
- **2.2 深色模式**：擴充工具無法模擬 `prefers-color-scheme`，需要手動切換 OS 深色模式後檢查。
- **排隊驗證碼畫面（`.captcha-row`，含固定 160px 輸入框）的窄螢幕量測**：要進入排隊才會出現，mobile-layout-p0-fixes 未涵蓋，留給買家探索頁改版 change 處理。
- **買家頁面即使內容很短也會出現垂直捲動**（mobile-layout-p0-fixes 量測時發現）：`BuyerLayout.vue` 的 `.buyer-content { min-height: calc(100svh - 56px) }` 沒有算到 header 的 1px 下框線（header 實高 57px）；頁面根元素的 `margin-top` 又摺疊到 `main` 外。EventList／EventDetail 的 margin 是 32px，所以多出約 33px；Login／Register／MyOrders／OrderDetail／OrderResult 是 64px，多出約 65px。只改 calc 只能消掉 1px。另案處理。
- **Core Web Vitals 實際數值**：建議用 Chrome DevTools 的 Lighthouse（Mobile、Slow 4G），在前景分頁跑活動列表與活動詳情兩頁，作為基準值。

## 8. 瀏覽器實測紀錄（2026-10-08）

**Production build（本機 gzip 靜態伺服器，無網路節流）**

| 資源 | 開始 | 結束 | 傳輸 |
| --- | --- | --- | --- |
| `index-*.js` | 85ms | 301ms | 382 KB |
| `index-*.css` | 85ms | 576ms | 49 KB |
| `/api/events` | 672ms | 1022ms | 45 KB |

DOMContentLoaded 681ms。**新發現**：`GET /api/events` 不分頁，一次回傳全部 111 筆活動（45 KB），買家首頁一次渲染 111 張卡片；活動詳情頁也為了找一筆活動而抓整份列表（`EventDetailPage.vue` `loadData`）。活動數量成長後，這是首屏與詳情頁共同的瓶頸，建議做分頁並提供單筆查詢 API（需動後端，須開 change）。

**Dev server**：84 個未打包的模組請求，DOMContentLoaded 約 7s（Windows bind mount 加 polling），不代表正式環境效能，只用來觀察 loading 狀態。

**RWD（390×844 同源 iframe）**

| 頁面 | scrollWidth | 結果 |
| --- | --- | --- |
| `/`（活動列表） | 375 | ✅ |
| `/login` | 380 | ✅ |
| `/events/:id` | **470** | ❌ 橫向捲動（R3） |

**鍵盤（活動詳情頁）**：Tab 可聚焦 507 個元素，其中 500 個是座位；焦點框為瀏覽器預設 `outline: auto`（可見）。座位按鈕 36×28px。
