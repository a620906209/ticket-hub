## Context

`AdminVenuesController`（`api/admin/venues`）目前只有兩個 `POST` 端點（建立場館、建立座位圖），`IVenueRepository`／`ISeatMapRepository` 也只有 `GetByIdAsync` 與 `Add`，沒有任何列表查詢方法。Admin 前端因此只能用 `stores/adminVenueCache.ts` 這個 session-only 的 Pinia store 頂著場館列表，建立活動表單的場館/座位圖 Id 也只能手動輸入。這是 `ticketing-web-ui` 上線時刻意記錄、延後處理的已知落差。

`Venue`（`Id`、`Name`）與 `SeatMap`（`Id`、`VenueId`、`Seats`）都是輕量 Entity，`SeatMap.Seats` 依既有 `GetByIdAsync` 的實作約定必須完整載入（見 `ISeatMapRepository.GetByIdAsync` 的 XML 註解，`Event.CreateEventSeats` 依賴這個假設）。

## Goals / Non-Goals

**Goals:**
- 補上場館列表、場館明細（含其下座位圖摘要）、單一座位圖明細（含完整座位清單）三個查詢端點
- Admin 前端場館列表頁改為串接真實查詢 API，移除 session 暫存 store
- Admin 前端建立活動表單的場館/座位圖欄位改為下拉選單

**Non-Goals:**
- 不新增 `SeatMap.Name` 欄位——目前 `SeatMap` 沒有名稱屬性，下拉選單只能用 Id（截斷顯示）＋座位數識別。新增 Name 欄位是獨立的 schema 變更，不在本次範圍
- 不做分頁／搜尋／篩選——目前場館與座位圖數量不多，比照既有 `GetEventsHandler`／`GetOrdersHandler` 的作法一次全部撈回，之後資料量大到需要分頁再另開 change 處理
- 不改變既有建立場館／建立座位圖 API 的行為與驗證規則

## Decisions

### 決策 1：Repository 直接擴充既有介面，不建立新的查詢投影型別

`IVenueRepository` 新增 `GetAllAsync(CancellationToken)`；`ISeatMapRepository` 新增 `GetByVenueIdAsync(Guid venueId, CancellationToken)`，兩者都回傳完整 Domain Entity（`SeatMap` 含完整 `Seats`），不是輕量投影 DTO。

**理由**：
- 延續本專案既有慣例——`IOrderRepository.GetAllAsync` 供 Admin 訂單列表使用時，也是回傳完整 `Order`（含 `Items`），由 Application 層的 Handler 再映射成 Summary DTO，不是在 Repository 層做投影
- `Venue`／`SeatMap` 目前規模小（單一場館底下座位圖數量、單一座位圖座位數都是百位數等級），一次載入完整 Entity 換取程式碼單純，符合「不做推測性效能優化」原則
- **考慮過的替代方案**：在 Repository 用 EF Core `Select` 投影直接查 `SeatMap` 的座位數（不載入 `Seats` 集合），效能更好。`IOrderRepository.GetExpiredPendingOrderIdsAsync` 已有回傳非 Entity 型別（`IReadOnlyList<Guid>`）的先例，所以「Repository 只能回傳 Domain Entity」並非絕對慣例；但那個案例回傳的是單一 Id 清單，這裡若要投影還得額外定義一個「座位圖摘要」的回傳型別，複雜度不對等。目前資料量不構成真實效能問題，故先採用回傳完整 Entity 的簡單作法，不引入新的投影型別；見 Risks 章節的替代方案保留

### 決策 2：場館明細與座位圖明細分成兩個獨立端點，不合併成一次查完

`GET /api/admin/venues/{id}` 只回傳場館基本資料＋其下座位圖的**摘要**（Id、座位數）；要看某座位圖的完整座位清單，需再呼叫 `GET /api/admin/venues/{venueId}/seat-maps/{seatMapId}`。

**理由**：
- 對應前端實際使用情境：「建立活動」下拉選單只需要座位圖摘要（Id + 座位數，供辨識），不需要每個座位的細節；只有真的要看/編輯某張座位圖時才需要完整座位清單
- 避免場館明細一次把底下所有座位圖的全部座位都吐出來（一個場館可能有多張大型座位圖），維持單一端點的回應大小可預期

### 決策 3：座位圖明細端點需驗證 SeatMap 確實屬於指定的 VenueId

`GET /api/admin/venues/{venueId}/seat-maps/{seatMapId}` 查到 `SeatMap` 後，MUST 額外檢查 `seatMap.VenueId == venueId`，不符合視同找不到（404），不是回傳跨場館的座位圖資料。

**理由**：路由把 `venueId` 當作路徑的一部分語意上代表「這張座位圖屬於這個場館」，若允許用不相關的 `venueId` 查到別的場館底下的座位圖，會造成 URL 語意與實際回應不一致（雖然不是機敏資料，但屬於不必要的資訊揭露與 API 契約錯誤）

### 決策 4：新查詢端點沿用既有 `AdminOnly` 授權政策，不新增 Requirement 描述權限規則

三個新端點加在既有 `AdminVenuesController`（已標註 `[Authorize(Policy = AuthorizationPolicies.AdminOnly)]`），沿用 `event-management` 既有「後台管理 API 需要 Admin 角色」Requirement，delta spec 不重複這條規則，只在「透過管理 API 查詢場地與座位圖」這個新 Requirement 的描述中註明沿用既有權限規則。

### 決策 5：GET 端點回傳型別維持 `IActionResult`／`Ok(dto)`，前端手寫 Response 型別

比照既有 `GetEvents`／`GetOrders` 端點寫法（回傳 `IActionResult`，非強型別 `ActionResult<T>`），本次新端點不會產生 OpenAPI Response schema；三個新端點也都沒有 Request Body（純路徑參數），所以不會有 Request Body schema。查了目前 `web/src/types/api.generated.ts` 裡結構相同的既有端點（`GET /api/events/{id}/seats`、`GET /api/events/{id}/ticket-types`——同樣是純路徑參數 + `IActionResult`）的實際產出：`requestBody?: never`、`responses[200].content?: never`（沒有 Request Body、沒有 Response schema），但路徑參數本身**仍有**型別（`path: { id: string }`）。本次三個新端點結構相同，預期產出模式一致（`path` 有型別，`requestBody` 為 `never`，`responses[200].content` 為 `never`——`responses` 物件本身仍會存在，只是內容 schema 是空的，不是整個 `responses` 都不存在）；正式結論待 tasks.md 5.1 實際重新產生後對照確認，不是憑空預測。前端在 `web/src/types/apiResponses.ts` 手寫 `VenueSummary`／`VenueDetail`／`SeatMapDetail` 三個 Response DTO，延續 `ticketing-web-ui` 已建立的手寫 Response 型別慣例（見該次 design.md 決策 4），不在本次改變這個既有作法。

### 決策 6：`GetVenues` 依場館名稱＋Id 排序，其餘查詢不保證順序

`GetVenuesHandler` 對 `IVenueRepository.GetAllAsync` 的結果先依 `Name`（序數排序，`StringComparer.Ordinal`），再依 `Id` 做 tie-breaker（`.OrderBy(v => v.Name, StringComparer.Ordinal).ThenBy(v => v.Id)`）排序後才映射為 DTO；`GetVenueById` 回傳的座位圖摘要清單、`GetSeatMapById` 回傳的座位清單則不排序，也不保證任何特定順序——呼叫端（含測試）不得依賴這兩者的回傳順序。

**理由**：查了 `GetEventsHandler`／`GetOrdersHandler`，兩者都沒有對查詢結果做 `OrderBy`，本專案目前沒有既有排序慣例可以直接延用。但場館列表這次的主要用途是「建立活動表單的下拉選單」（決策見 Goals），沒有穩定排序的話，Admin 每次開表單看到的選項順序都可能不同，對挑選造成不必要的困擾；場館數量通常不多，依名稱排序的成本可忽略。只依 `Name` 排序還不夠——`Venue.Name` 目前沒有唯一性約束（`VenueConfiguration`／`CreateVenueRequestValidator` 都沒有要求不重複），兩個同名場館之間的相對順序在只依 `Name` 排序時仍是未定義行為，資料庫每次查詢可能給出不同順序；補上 `Id` 當 tie-breaker（`Id` 是 GUID，天生唯一）就能讓整體排序完全確定。座位圖摘要／座位清單目前沒有類似的挑選情境（座位圖摘要用座位數輔助辨識、座位清單只是明細呈現），暫不特別排序；但明確聲明「不保證順序」比含糊地說「維持自然順序」更準確——後者容易被誤讀成一種隱含的順序保證，實際上 EF Core 沒有 `OrderBy` 時的回傳順序不是 API 契約的一部分。

### 決策 7：座位圖沒有任何座位時，明細查詢仍視為成功（回傳空陣列），不是 NotFound

`GetSeatMapById` 只有「查無此座位圖」或「座位圖存在但不屬於指定場館」（決策 3）才回傳 NotFound；座位圖存在但 `Seats.Count == 0`（空集合）MUST 視為成功，回傳 `Seats` 為空陣列的 `SeatMapDetailDto`，不得跟「找不到」混為一談。

**理由**：`CreateSeatMapRequestValidator` 目前要求 `Seats` `NotEmpty()`，正常建立流程不會產生零座位的座位圖，這個狀態在目前的真實資料中不會出現；但 `SeatMap` Domain Entity 本身允許座位集合為空（建構子只設定 `Id`／`VenueId`，`Seats` 從空集合開始），Handler 邏輯若沒有明確處理，容易被誤寫成「集合空就當作找不到」這種常見的邊界錯誤。這個決策只是把 Handler 該有的防呆行為說清楚，供 2.2/3.1 的單元測試（用 Fake Repository 直接塞一個沒有座位的 `SeatMap`，不透過真正的建立 API）覆蓋，不是新增一個使用者可觸發的業務情境。

### 決策 8：順帶修正 `CreateEventHandler` 缺少的座位圖歸屬場地驗證

查證 `CreateEventHandler.cs` 後發現：目前的實作只分別確認 `Venue` 存在、`SeatMap` 存在，**從未檢查兩者是否互相對應**（`seatMap.VenueId == request.VenueId`）。但既有 `event-catalog` spec 的「建立活動」Requirement 寫的是「指定使用場地下的一份座位圖（`SeatMap`）」——語意上已經隱含座位圖必須屬於指定場地，實作卻沒有真的檢查，等於 spec 語意跟實作行為存在落差。這個落差跟本次新增的 `GetSeatMapById`（決策 3）是同一組「座位圖是否屬於指定場地」的規則，只是一個在查詢端、一個在建立端。

**決定**：在 `CreateEventHandler` 補上同樣的檢查——`seatMap.VenueId != request.VenueId` 時回傳 `Result.Failure(Error.NotFound(...))`，錯誤語意比照既有「場地或座位圖不存在」的 NotFound 分類，不引入新的錯誤型別。

**理由**：
- 範圍小、風險低：純新增一個檢查，不改變既有欄位、不改變既有**合法**成功路徑的行為；查過 `CreateEventHandlerTests.cs` 既有測試都用 `SeedVenueAndSeatMap` 建立**互相對應**的場地/座位圖組合，沒有任何既有測試依賴「場地與座位圖不對應」仍能成功建立這個（未定義的）行為，補上檢查不會破壞任何既有測試。但要明確講清楚：這確實**會改變不合法的跨場館請求結果**——原本場館與座位圖不對應的請求可能被誤放行、成功建立出一個資料不一致的活動，補上檢查後改為回傳 NotFound、拒絕建立，這是本次刻意要修正的行為變更，不是純粹的「零行為影響」
- 前端這次雖然改成下拉選單（場館→座位圖連動選擇），UI 上已經不會讓 Admin 選出不對應的組合，但這只是前端的體驗防呆，不是後端的邊界防線；直接呼叫 API（略過前端）仍能建立場地與座位圖不對應的活動，這跟本專案一貫「前端提示、後端才是最終把關」的原則（例如每筆訂單限購張數、選位分區比對）不一致
- 不在本次修的替代方案：把這個修正另開一個 change——但這是個位數行數的修正、且是本次改動已經在處理的同一組規則（座位圖歸屬場地），另開 change 的溝通與追蹤成本比直接做完更高，故不採用

**不在本次範圍**：不回頭處理其他可能存在的類似落差（例如票種與分區的對應是否有其他未覆蓋的邊界情況）——只修這次查證時具體發現、且與本次改動直接相關的這一項，避免範圍蔓延。

## Risks / Trade-offs

- **[Risk]** 場館底下座位圖數量與座位數成長後，`GetVenueById` 為了算每張座位圖的座位數而載入完整 `Seats` 集合，會有不必要的資料庫負載 → **Mitigation**：目前資料量小，先用簡單作法；若之後場館/座位圖規模明顯變大導致明細頁載入變慢，再改成決策 1 提到的 SQL 投影查詢（`COUNT` 不載入 Seats），屬於獨立的效能優化 change，不在本次預先處理
- **[Risk]** `SeatMap` 沒有 `Name` 欄位，下拉選單只能顯示 Id（截斷）＋座位數，Admin 選錯座位圖的風險比有名稱時高 → **Mitigation**：本次範圍不新增欄位，`SeatMapSummaryDto` 也只有 `Id`／`SeatCount` 兩個欄位（不引入時間戳等新屬性）；下拉選單就用這兩個既有欄位輔助辨識，若後續體感不佳，再另開 change 補 `Name` 欄位
- **[Risk]** 前端拿掉 `stores/adminVenueCache.ts` 後，若「建立場館/座位圖成功」與「查詢 API 看得到新資料」之間出現時間差（理論上 EF Core 寫入後立即可查，不應該有延遲），可能讓 Admin 誤以為建立失敗 → **Mitigation**：建立成功後前端直接重新呼叫對應查詢 API 刷新列表/下拉選單，不依賴任何快取層，跟 `AdminOrderListPage`／`EventListPage`（Admin）現有的「建立後重新整理列表」作法一致
- **[Risk]** 建立活動表單中，Admin 快速切換場館下拉選單時會連續觸發多次 `getVenueById` 查詢；如果沒有防護，較早送出、但較晚回來的回應可能在較新的回應之後才抵達，把座位圖選項覆蓋回舊場館的內容，讓畫面顯示的座位圖選項跟目前選定的場館對不上 → **Mitigation**：每次呼叫 `getVenueById` 時記錄當下選定的 `venueId`（或用遞增序號／`AbortController` 皆可），回應抵達時先確認「這個回應對應的場館，是否仍是目前選定的場館」，不是的話捨棄這次回應，不更新座位圖選項；task 7.2／7.4 明確列了這個實作要求與對應測試（快速切換場館兩次，確認最終顯示的座位圖選項對應最後一次選擇，不被較晚抵達的舊回應覆蓋）

## Migration Plan

不需要 EF Core migration（沒有新增或修改任何資料表欄位，純新增查詢端點與查詢方法）。前後端可視為一次性替換：後端新增端點上線後，前端同一輪改用查詢 API 取代 `stores/adminVenueCache.ts` 與手動輸入 GUID，兩者在同一個 change 內完成，不需要分階段 rollout 或 feature flag。
