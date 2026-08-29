## Why

`docs/project-scope.md` 將「防黃牛 / 防機器人搶票」列為核心商業目標第 2 位，但目前建立訂單並鎖定座位／扣減票種庫存的端點（`POST /api/orders`）完全沒有任何請求節流或排隊管制，開賣瞬間容易被腳本/機器人以高頻請求反覆搶佔座位或計數庫存，一般買家反而搶不到；同時全站也尚未有任何 API 層級的限流基礎設施。Phase 1（Must）已全數完成，依 `docs/project-scope.md` 第 7 節開發階段順序進入 Phase 2（Should），此為其中風險/技術含量最高、且與商業目標關聯最直接的一項。

## What Changes

- 新增全站可套用的 API 限流基礎設施（`Microsoft.AspNetCore.RateLimiting`），以登入會員 Id 為 partition key 做固定時間窗限流（受影響端點皆已要求登入，不需 IP fallback），超過限制回傳 `429 Too Many Requests`（`ProblemDetails` 格式）；本次先套用在下單相關端點（`POST /api/orders`、`POST /api/orders/{id}/confirm`，兩者各自獨立計數）
- 新增基礎排隊機制：當活動被標記為「熱門搶購模式」時，買家呼叫 `POST /api/orders`（建立訂單並鎖定座位/扣減庫存）前，須先加入排隊並取得入場資格、依序放行；不要求前端另外持有或傳遞 token——資格以會員 Id + 活動 Id 反查判斷；未取得入場資格或尚未輪到時 MUST 被拒絕，不進入既有的座位鎖定/庫存扣減交易
- 新增排隊狀態查詢端點，供前端顯示「目前排隊中，前方尚有 N 人」；本次不提供預估等待時間（例如剩餘秒數）——沒有實際購票速度的歷史資料可估算，貿然給一個不準的數字容易誤導買家，比顯示「不知道」更糟，留待有實際使用數據後再評估是否加入
- 「熱門搶購模式」的判定與開關方式（自動依剩餘庫存閾值 or 主辦方/Admin 手動開關）留待 design 階段決定
- 前端（買家端）選位/送出訂單流程新增排隊等待畫面，以及 429 限流錯誤的使用者提示

## Capabilities

### New Capabilities
- `api-rate-limiting`：全站 API 層級的請求限流基礎設施（依已登入會員 Id 分區的固定時間窗限流、429 標準錯誤回應），本次套用範圍為下單相關端點，其餘端點的套用留待後續視需要擴充
- `purchase-queue`：熱門活動購票排隊機制（排隊資格核發、順序放行、排隊狀態查詢），建立在 `api-rate-limiting` 之上，針對「同一活動同時大量搶購」情境做入場管制

### Modified Capabilities
- `ticket-purchase`：「透過 API 建立訂單並鎖定座位或扣減票種庫存」需求新增前置檢查——活動處於熱門搶購模式時，缺少有效排隊入場資格 MUST 被拒絕、不執行任何鎖定/扣減；一般限流拒絕（429）比照現有錯誤回應慣例
- `buyer-web-ui`：「買家可選位並送出訂單」需求新增排隊等待畫面與限流錯誤提示的呈現行為

## Impact

- 後端：新增排隊/限流相關 Middleware 或 Filter、`OrdersController` 的 `PlaceOrder`／`ConfirmOrder` 套用限流；新增 `PurchaseQueueEntry` 資料表（EF Core migration）；排隊資格檢查與「標記完成」須與既有 `OrderService.PlaceOrderAsync` 的座位/庫存鎖定交易整合，細節見 design.md
- 公開的活動列表 DTO（`GET /api/events`）新增 `isQueueModeEnabled` 欄位，供前端判斷是否需先加入排隊
- 前端：`web/src/` 新增排隊等待頁面/元件，`PlaceOrder` 呼叫前檢查排隊狀態，新增 429 與排隊資格不足（403）的錯誤提示；「區域隨選快速下單」入口比照一般選位，於排隊等待中一併停用
- 不影響既有座位鎖定、訂單確認/取消的核心邏輯與既有測試；一般（非熱門搶購模式）活動的下單流程行為不變
