## MODIFIED Requirements

### Requirement: 買家可瀏覽活動列表與座位可售狀態
系統 SHALL 提供活動列表頁與活動詳情頁，呼叫既有 `event-catalog`／`ticket-purchase` API 顯示活動基本資訊與座位可售狀態（Available／Held／Sold），不需要登入即可瀏覽。活動詳情頁 SHALL 並排顯示活動資訊（含海報圖片與說明文字，若 Admin 建立活動時有填寫）與座位選擇區；座位依分區分組顯示。座位可售狀態（Available／Held／Sold）為頁面載入或手動重新整理當下查詢 API 取得的結果，非伺服器推播的即時更新（本輪不引入 WebSocket／SignalR），此部分不受本節下述快取異動影響（`ticket-purchase`「瀏覽活動與座位可售狀態」能力的座位查詢端點不在 `query-caching` 能力的快取範圍內）。**活動基本資訊（包含 `IsQueueModeEnabled`）呼叫的 `GET /api/events` 端點，自 `query-caching` 能力起可能回傳快取內容，落後資料庫實際值的時間不超過該能力定義的 TTL 上限（`EventListTtlSeconds`，預設 30 秒）；這不改變本頁「頁面載入或手動重新整理當下取得結果」的既有行為模式（前端仍是每次載入即發起一次查詢，只是該次查詢在後端可能命中快取），只是該次查詢結果本身可能不是資料庫當下的絕對最新值。**

#### Scenario: 瀏覽活動列表
- **WHEN** 使用者開啟活動列表頁
- **THEN** 系統以卡片格線顯示目前所有活動的基本資訊；此資訊可能是 `query-caching` 能力的快取內容，落後時間不超過該能力定義的 TTL 上限

#### Scenario: 查看活動座位狀態
- **WHEN** 使用者點選某活動進入詳情頁
- **THEN** 系統顯示該活動的資訊（含海報／說明，如有設定）與座位圖，座位依分區分組、每個座位標示目前的可售狀態

#### Scenario: 活動未設定海報或說明
- **WHEN** Admin 建立活動時沒有填寫海報網址或說明
- **THEN** 系統不顯示海報圖片區塊或說明文字區塊，不顯示空白佔位或錯誤訊息
