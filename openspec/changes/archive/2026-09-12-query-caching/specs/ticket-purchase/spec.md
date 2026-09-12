## MODIFIED Requirements

### Requirement: 瀏覽活動與座位可售狀態
系統 SHALL 提供不需登入即可查詢的端點，讓使用者查詢活動列表、活動的座位可售狀態（含分區代碼，供對應票種價格）、活動的票種與價格。查詢票種列表時，每筆票種 SHALL 附帶 `RequiresSeat`，供呼叫端判斷該票種是否需要另外指定座位；`RequiresSeat = false` 的票種 SHALL 額外附帶可售總量（`AvailableQuantity`）——**此數值可能因 `query-caching` 能力為此查詢端點導入的 Redis cache-aside 快取而落後資料庫實際值，最大陳舊時間依 `query-caching` 能力「快取項目具備 TTL 安全網」Requirement 定義（`TicketTypesTtlSeconds`，預設 10 秒）；這個陳舊時間不影響下單正確性——買家實際送出訂單時，系統仍依本能力「透過 API 建立訂單並鎖定座位或扣減票種庫存」Requirement，以資料庫當下真實庫存重新驗證並扣減，此處查詢到的數值僅供瀏覽時的參考顯示，不是下單時的授權依據**。查詢活動列表時，每筆活動 SHALL 額外附帶 `IsQueueModeEnabled`（是否處於熱門搶購模式），供前端判斷買家進入該活動詳情頁時是否需先加入排隊；此欄位為活動的公開屬性，不需登入即可取得，與需要登入的排隊加入/查詢端點不同——**此欄位同樣可能因查詢快取而落後資料庫實際設定值，最大陳舊時間依 `query-caching` 能力對應 Requirement 定義（`EventListTtlSeconds`，預設 30 秒）；買家實際建立訂單時，系統仍依本能力「透過 API 建立訂單並鎖定座位或扣減票種庫存」Requirement 的既有規則，以「系統實際執行建立邏輯當下重新讀取到的值」為準決定是否套用排隊資格檢查，不受瀏覽時查到的快取值影響——瀏覽時看到的 `IsQueueModeEnabled` 僅供前端決定是否顯示排隊入口的參考，不是下單時的最終授權判斷依據**。

#### Scenario: TP-BROWSE-001 查詢活動列表
- **WHEN** 使用者查詢活動列表
- **THEN** 系統回傳目前已建立的活動基本資訊，每筆活動附帶 `IsQueueModeEnabled`；此回應可能是 `query-caching` 能力的快取內容，`IsQueueModeEnabled` 可能落後資料庫實際值，落後時間不超過該能力定義的 TTL 上限

#### Scenario: TP-BROWSE-002 查詢活動座位可售狀態
- **WHEN** 使用者查詢某活動的座位狀態
- **THEN** 系統回傳該活動每個座位當下的可售狀態與所屬分區代碼

#### Scenario: TP-BROWSE-003 查詢活動票種與價格
- **WHEN** 使用者查詢某活動的票種列表
- **THEN** 系統回傳該活動已建立的票種、對應價格，以及每個票種的 `RequiresSeat`；`RequiresSeat = false` 的票種另附帶可售總量；此回應可能是 `query-caching` 能力的快取內容，可售總量可能落後資料庫實際值，落後時間不超過該能力定義的 TTL 上限，且不影響下單時系統對即時庫存的重新驗證

#### Scenario: TP-BROWSE-004 查詢不存在的活動
- **WHEN** 使用者以不存在的活動 ID 查詢座位可售狀態或票種列表
- **THEN** 系統回傳 404 找不到資源，不得擲出未預期例外
