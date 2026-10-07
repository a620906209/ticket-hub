## Why

UI/UX 審視報告（`docs/ui-ux-review-2026-10-08.md`，第 5.2 節 R3、第 4.2 節）在瀏覽器實測時發現兩個成本極低、但影響買家核心流程的問題：

1. **手機寬度的活動詳情頁會整頁橫向捲動**：在 390px 寬時，頁面 `scrollWidth` 為 470px，連活動標題與票價表都被裁切。活動詳情頁是購票的唯一入口。
2. **`index.html` 宣告 `lang="en"`，但內容全是繁體中文**：螢幕閱讀器會用英文語音唸中文，瀏覽器的翻譯提示也會誤判語言。

兩者的修正量都在幾行以內，不必等之後的買家探索頁改版，先獨立交付。

## What Changes

- 活動詳情頁在窄螢幕（含 320px）下 SHALL NOT 產生頁面層級的橫向捲動；區域隨選列與計數購票列在空間不足時改為換行顯示。
- 平板與桌面寬度（> 720px）的雙欄版面（資訊欄 320px＋購票欄）維持不變。
- 文件語言宣告改為繁體中文（`zh-Hant-TW`）。
- **不包含**：色票對比度調整、Bundle 切割、座位網格無障礙、購票頁動線（sticky 結帳列等），這些留給後續 change。

## Capabilities

### New Capabilities

（無）

### Modified Capabilities

- `buyer-web-ui`：新增「活動詳情頁在窄螢幕不產生頁面橫向捲動」與「前端文件語言宣告為繁體中文」兩條需求；既有需求的行為不變。

## Impact

- 程式碼：`web/src/pages/buyer/EventDetailPage.vue`（僅 `<style scoped>`）、`web/index.html`（僅 `lang` 屬性）
- 測試：新增 1 個 Vitest 測試檔（`index.html` 的語言宣告）；版面溢出無法在 jsdom 驗證（jsdom 不做版面計算），改用腳本化的瀏覽器量測驗證（見 design.md 決策 3）
- 不影響後端、API、DB；不新增相依套件
