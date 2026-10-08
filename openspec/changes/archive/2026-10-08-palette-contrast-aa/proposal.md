## Why

UI/UX 審視報告（`docs/ui-ux-review-2026-10-08.md` 第 4.1、2.2 節）實測發現，莫蘭迪色票除了主要文字以外，所有語意色都不符合 WCAG AA：主色按鈕白字 2.90、警告標籤 2.02、連結 2.55、次要文字 3.29、placeholder 2.27、輸入框邊框 1.46。訂單狀態、販售狀態、票券狀態等標籤是買家判斷「要不要付款」「能不能買」的依據，在戶外的手機螢幕上很難閱讀。

這是報告列出的 P0，而且之後的買家探索頁改版都會沿用這組色票 token，必須先修好。

## What Changes

- 五個語意色（primary／success／warning／danger／info）保留原本的色相與彩度，只調暗明度，讓按鈕白字與狀態標籤文字都達到 4.5:1；各色的 light／dark 階層依新的 base 重新計算。
- 實心語意色按鈕的 hover 從「變淡」改為「變深」，active 再深一階，所有狀態的白字都達到 4.5:1。
- 按鈕的鍵盤焦點框改用 base 色，達到 UI 元件 3:1 的要求。
- 次要文字與 placeholder 調暗到 4.5:1。
- 輸入類元件的邊框（`--el-border-color`）與 hover 邊框調暗到 3:1；表格、分隔線用的淺色邊框（light／lighter）不變。
- `color-scheme` 由 `light dark` 改為 `light`：專案沒有深色 token，宣告 dark 會讓原生捲軸與表單元件在 OS 深色模式下變成深色，和頁面本體不協調。
- 新增 Vitest 對比度回歸測試，直接解析 `morandi.css`。
- **不包含**：座位網格的圖例與已售樣式（報告 U7，屬於購票頁 P1 change）、按鈕按壓縮放與觸控 hover 限縮、type scale、bundle 切割、深色模式。

## Capabilities

### New Capabilities

- `web-color-contrast`：前端（買家端與後台共用的色票）的文字、語意色元件與輸入類元件邊界的最低對比度要求，以及色票保留莫蘭迪色相的限制。

### Modified Capabilities

（無）既有 `buyer-web-ui`／`admin-web-ui` 的需求行為不變；色票是兩者共用的單一來源（`web/src/styles/morandi.css`），因此獨立成一個 capability，避免在兩份 spec 重複同一組要求。

## Impact

- 程式碼：`web/src/styles/morandi.css`（色票值、按鈕狀態覆寫）、`web/src/style.css`（`color-scheme`）
- 測試：新增 1 個 Vitest 測試檔（以 `?raw` 讀取 CSS，不碰 DB 與網路）
- 文件：審視報告第 4.1 節標註已修正
- 視覺：全站語意色變深，莫蘭迪的灰調保留但整體更沉穩；所有頁面都會受影響，需在瀏覽器逐頁目視確認
- 不影響後端、API、DB；不新增相依套件
