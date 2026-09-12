## Why

一般查詢 API（活動列表、票種查詢）目前每次請求都直接打 PostgreSQL，在專案設定的量級（100–300 同時在線）下不是效能瓶頸，但這兩個端點是全站讀取頻率最高、且改動頻率相對低的查詢，適合示範 cache-aside 快取模式與明確的失效（invalidation）策略。專案的 Redis 基礎設施已因 `purchase-queue-leader-election` 存在（`docker-compose.yml` 的 `redis` 服務、`IConnectionMultiplexer` Singleton 註冊），可直接複用，不需新增服務。

## What Changes

- 為 `GET /api/events`（活動列表）與 `GET /api/events/{id}/ticket-types`（指定活動的票種列表）導入 Redis 查詢快取，採 cache-aside 模式：查詢時先讀快取，未命中才查資料庫並寫回快取。
- 快取項目 MUST 訂定 TTL 作為過期安全網；同時在對應資料異動的操作（活動建立、熱門搶購模式開關、票種建立、訂單成功建立或取消／逾時導致的 `TicketType.AvailableQuantity` 變動）上明確觸發快取失效（主動刪除對應 key），不完全依賴 TTL 被動過期。**確認付款（`ConfirmOrderAsync`）不變更 `AvailableQuantity`（純計數票種庫存於訂單建立時即已扣減），因此不需要、也不會觸發票種列表快取失效**——庫存異動只發生在「建立訂單」（扣減）與「取消／逾時」（歸還）兩個時間點，見 design.md 決策 4。
- 快取失敗（Redis 無法連線）MUST 不影響查詢功能本身——比照既有 `purchase-queue-leader-election` 的 fail-open 慣例，快取讀寫失敗時 fallback 為直接查詢資料庫，並記錄 Warning 等級 log。
- 不快取 `GET /api/events/{id}/seats`（座位查詢）——座位鎖定狀態變動頻繁且需要即時性，不屬於「一般查詢」範疇。

## Capabilities

### New Capabilities
- `query-caching`：一般查詢 API 的 Redis cache-aside 快取層，涵蓋快取讀寫、TTL、明確失效觸發點、Redis 故障時的 fail-open 降級行為。

### Modified Capabilities
- `ticket-purchase`：「瀏覽活動與座位可售狀態」Requirement 原文對 `AvailableQuantity`／`IsQueueModeEnabled` 使用「當下」的措辭，暗示查詢即時反映資料庫真值；本次改動為這兩個查詢端點導入快取後，需明確窄化「當下」為「不超過 `query-caching` 能力定義的 TTL 上限」，並重申下單時的即時重新驗證不受此影響（原文的 `AvailableQuantity`/`IsQueueModeEnabled` 定義本身不變，只補充查詢時的新鮮度邊界）
- `buyer-web-ui`：「買家可瀏覽活動列表與座位可售狀態」Requirement 對活動基本資訊呼叫的查詢 API 做了「當下查詢 API 取得的結果」的措辭；本次改動需明確排除座位狀態（未快取）、並為活動基本資訊（含 `IsQueueModeEnabled`）補充快取新鮮度邊界的說明。`event-catalog`／`event-management` 既有 Requirement 描述的是資料建立與驗證規則，不涉及查詢 API 的快取/新鮮度行為，不受影響，不列入 Modified Capabilities

## Impact

- **新增元件**：Application 層新增查詢快取抽象介面；Infrastructure 層新增以 `IConnectionMultiplexer`（既有 Singleton）為後端的實作
- **受影響端點**：`EventsController.GetEvents`、`EventsController.GetTicketTypes`（讀取路徑套用快取）
- **受影響的既有寫入路徑**（需加上快取失效觸發）：
  - `CreateEventHandler`（`POST /api/admin/events`）、`SetEventQueueModeHandler`（`PATCH /api/admin/events/{id}/queue-mode`）——目前 Admin 端無「更新活動」端點，僅建立與熱門搶購模式開關兩處會改變 `GetEvents` 回應內容
  - `CreateTicketTypeHandler`（`POST /api/admin/events/{eventId}/ticket-types`）——目前無「更新票種」端點
  - `OrderService` 內訂單成功建立／取消導致 `TicketType.AvailableQuantity` 異動的既有邏輯（純計數票種）
- **DI 註冊**：`Program.cs` 新增快取抽象的 Singleton 註冊
- **設定**：`appsettings.json` 新增 TTL 等快取設定值
