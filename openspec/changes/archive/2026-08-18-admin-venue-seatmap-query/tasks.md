## 1. 後端：Repository 查詢方法

- [x] 1.1 `IVenueRepository` 新增 `GetAllAsync(CancellationToken)`，回傳 `IReadOnlyList<Venue>`；`VenueRepository`（EF Core 實作）補上對應查詢
- [x] 1.2 `ISeatMapRepository` 新增 `GetByVenueIdAsync(Guid venueId, CancellationToken)`，回傳 `IReadOnlyList<SeatMap>`（比照既有 `GetByIdAsync` 的 XML 註解約定，MUST 一併載入 `Seats`）；`SeatMapRepository`（EF Core 實作）補上對應查詢。**新方法本身也要補上 XML 註解**（`/// <summary>實作 MUST 一併載入 <see cref="SeatMap.Seats"/>。</summary>`），不要只把這個約定寫在 design.md／tasks.md，避免未來只看介面、沒看規劃文件的人漏掉 `Include(m => m.Seats)`
- [x] 1.3 更新 `tests/ProjectC.Application.Tests/TestSupport/FakeVenueRepository.cs`、`FakeSeatMapRepository.cs`，補上這兩個新方法的 in-memory 實作，供後續 Handler 測試使用

## 2. 後端：查詢場地列表／明細

- [x] 2.1 新增 `Application/Venues/GetVenues/VenueSummaryDto.cs`（`Id`、`Name`）與 `GetVenuesHandler.cs`（呼叫 `IVenueRepository.GetAllAsync`，依 `.OrderBy(v => v.Name, StringComparer.Ordinal).ThenBy(v => v.Id)` 排序後映射為 `VenueSummaryDto` 清單——`Name` 沒有唯一性約束，MUST 補 `ThenBy(Id)` 當 tie-breaker，否則同名場館的相對順序在只依名稱排序時是未定義行為，見 design.md 決策 6）
- [x] 2.2 新增 `Application/Venues/GetVenueById/SeatMapSummaryDto.cs`（`Id`、`SeatCount`）、`VenueDetailDto.cs`（`Id`、`Name`、`IReadOnlyList<SeatMapSummaryDto> SeatMaps`）與 `GetVenueByIdHandler.cs`（查無場地時回傳 `Result.Failure(Error.NotFound(...))`；查到場地後呼叫 `ISeatMapRepository.GetByVenueIdAsync` 組出座位圖摘要，**MUST 把 `cancellationToken` 一併傳入這次呼叫**，不要漏帶；座位圖摘要清單不排序，不保證順序，見 design.md 決策 6）
- [x] 2.3 `AdminVenuesController` 新增 `[HttpGet]` `GetVenues`、`[HttpGet("{id:guid}")]` `GetVenueById` 兩個 action
- [x] 2.4 補測試：`GetVenuesHandlerTests`（有場地時回傳依名稱排序的清單／無場地時回傳空清單／有兩個以上同名場地時，依 `Id` 做 tie-breaker，順序穩定可重現）、`GetVenueByIdHandlerTests`（場地存在且有多張座位圖、且座位數各不相同時，正確回傳每張座位圖各自對應的座位數，不混淆／場地存在但沒有任何座位圖時 `SeatMaps` 為空清單／場地不存在時回傳 NotFound／場地底下有一張座位圖但目前沒有任何座位時，該座位圖摘要的 `SeatCount` 為 0——用 `FakeSeatMapRepository` 直接塞一個沒有座位的 `SeatMap` 測試，因為透過真正的建立座位圖 API 沒辦法產生這個狀態，見 design.md 決策 7），對應 spec `透過管理 API 查詢場地與座位圖` 的「查詢場地列表」「場地列表中有多個同名場地」「查詢場地明細」「場地下有多張座位圖」「場地明細中某張座位圖目前沒有任何座位」「查詢不存在的場地明細」六個 Scenario

## 3. 後端：查詢座位圖明細

- [x] 3.1 新增 `Application/Venues/GetSeatMapById/SeatDto.cs`（`Id`、`ZoneCode`、`SeatNumber`）、`SeatMapDetailDto.cs`（`Id`、`VenueId`、`IReadOnlyList<SeatDto> Seats`）與 `GetSeatMapByIdHandler.cs`：呼叫 `ISeatMapRepository.GetByIdAsync`，查無座位圖、或查到但 `seatMap.VenueId != venueId` 皆回傳 `Result.Failure(Error.NotFound(...))`（design.md 決策 3）；座位圖存在且屬於指定場地時，**不論 `Seats` 是否為空集合都 MUST 視為成功**（design.md 決策 7），不要誤寫成「集合空就當找不到」；`Seats` 清單不排序，不保證順序
- [x] 3.2 `AdminVenuesController` 新增 `[HttpGet("{venueId:guid}/seat-maps/{seatMapId:guid}")]` `GetSeatMapById` action
- [x] 3.3 `Program.cs` 註冊 `GetVenuesHandler`／`GetVenueByIdHandler`／`GetSeatMapByIdHandler` 三個新 Handler（`builder.Services.AddScoped<XHandler>()`），比照既有每個 Handler 都要手動註冊的慣例（見 `GetOrdersHandler`／`GetOrderByIdHandler` 的既有註冊）——遺漏會導致 `AdminVenuesController` 建構子注入時 DI 解析失敗，服務無法啟動
- [x] 3.4 補單元測試：`GetSeatMapByIdHandlerTests`（座位圖存在且屬於指定場地時正確回傳完整座位清單／座位圖不存在時回傳 NotFound／座位圖存在但屬於另一個場地時回傳 NotFound／座位圖存在且屬於指定場地、但目前沒有任何座位時，回傳成功且 `Seats` 為空陣列——用 `FakeSeatMapRepository` 直接塞一個沒有座位的 `SeatMap` 測試，理由同 2.4），對應 spec 「查詢座位圖明細」「查詢不屬於指定場地的座位圖明細」「座位圖目前沒有任何座位」三個 Scenario
- [x] 3.5 `AdminVenuesControllerTests` 補上三個新 GET 端點各自至少一個成功案例的整合測試（`GetVenues_AsAdmin_ReturnsOk`、`GetVenueById_AsAdmin_ReturnsVenueWithSeatMaps`、`GetSeatMapById_AsAdmin_ReturnsSeats`，透過 `CustomWebApplicationFactory` 實際打真的路由，驗證 route mapping 與回應內容正確，不只是 Handler 單元測試涵蓋的業務邏輯）；授權測試（401／403）比照既有慣例（`[Authorize]` 掛在 Controller 層級，既有測試只在 `CreateVenue` 測過一次，`CreateSeatMap` 未重複測）只挑其中一個新 GET 端點（例如 `GetVenues`）補一組即可共用，不必三個端點各測一輪，對應既有 `event-management` spec 「後台管理 API 需要 Admin 角色」Requirement

## 4. 後端：CreateEventHandler 補上座位圖歸屬場地驗證（既有落差修正）

- [x] 4.1 `CreateEventHandler.HandleAsync` 在確認 `Venue`／`SeatMap` 各自存在後，新增檢查 `seatMap.VenueId != request.VenueId`，不符合時回傳 `Result.Failure(Error.NotFound($"Seat map '{request.SeatMapId}' was not found."))`（沿用既有「座位圖找不到」的錯誤語意，不新增錯誤分類，見 design.md 決策 8）
- [x] 4.2 `CreateEventHandlerTests` 新增測試：場地與座位圖都存在、但座位圖實際屬於另一個場地時，回傳 NotFound、不建立 Event 也不建立 EventSeat（比照既有 `HandleAsync_WithNonExistentSeatMap_ReturnsNotFound` 的寫法，改成種兩個不同場館各自的座位圖），對應 spec `透過管理 API 建立活動與票種`（MODIFIED）的「建立活動時場地與座位圖不對應」Scenario
- [x] 4.3 確認既有 `HandleAsync_WithValidVenueAndSeatMap_CreatesEventAndEventSeats` 等既有測試都是用互相對應的場地/座位圖組合（已於規劃階段查證過 `SeedVenueAndSeatMap` 的寫法確實如此），跑一次既有測試確認新增的檢查沒有破壞任何既有案例

## 5. 前端：API service 層與型別

- [x] 5.1 重新產生 `web/src/types/api.generated.ts`：三個新 GET 端點都沒有 Request Body、也沒有回應內容的 schema（回傳型別是 `IActionResult`），預期不會有 `requestBody` schema 或 `response content` schema；但 `responses` 物件本身、以及路徑參數（例如 `path: { id: string }`）預期仍會存在——比照結構相同的既有端點 `GET /api/events/{id}/seats`／`GET /api/events/{id}/ticket-types` 的既有產出（`requestBody?: never`、`responses[200].content?: never`，但 `path` 有型別），見 design.md 決策 5。實際產生後對照確認是否與預期一致
- [x] 5.2 `web/src/types/apiResponses.ts` 手寫新增 `VenueSummary`、`VenueDetail`（含 `seatMaps: SeatMapSummary[]`）、`SeatMapDetail`（含 `seats: SeatDetail[]`）三個 Response 型別（design.md 決策 5）
- [x] 5.3 `web/src/api/admin.ts` 新增 `getVenues()`、`getVenueById(id)`、`getSeatMapById(venueId, seatMapId)` 三個 API 呼叫函式，**MUST 呼叫既有的 `authorizedRequest`**（比照檔案內其他函式的既有寫法，不要直接用 `fetch` 或 `request`），Auth Header 會透過 `authorizedRequest` 統一帶入，不需要為此另外寫測試——`httpClient.test.ts` 已經在通用層級覆蓋這個機制

## 6. 前端：場館列表頁改真實查詢

- [x] 6.1 `VenueListPage.vue` 改為在 `onMounted` 呼叫 `getVenues()` 顯示場館列表，並提供手動重新整理按鈕（比照 `AdminOrderListPage.vue`／`EventListPage.vue` 既有的「載入／手動刷新」模式）
- [x] 6.2 點選某場館時呼叫 `getVenueById(id)` 顯示其下座位圖摘要清單（Id、座位數）。**MUST 防止過期回應覆蓋較新的選擇**（review 時發現遺漏，比照 7.2 的防護：快速連續點選不同場館時，記錄目前選定的場館，回應抵達時比對是否仍是目前選定場館，不是就捨棄）；座位圖摘要清單 SHALL 可展開查看完整座位清單，展開時才呼叫 `getSeatMapById(venueId, seatMapId)`（review 時發現這個既有 API 函式從未被呼叫，是死代碼，補上這個互動讓 `GetSeatMapById` 端點實際被使用）
- [x] 6.3 建立場館／建立座位圖成功後，改為重新呼叫查詢 API 刷新畫面（不再手動把新建立的資料塞進 store），移除「複製新建立場館/座位圖 Id」這個因應查詢缺失而存在的 UI 提示
- [x] 6.4 刪除 `web/src/stores/adminVenueCache.ts`；查證後確認目前只有 `VenueListPage.vue` 引用這個 store（建立活動表單／`EventListPage.vue` 從一開始就是手動輸入 GUID 文字欄位，未使用這個 store），故只需移除 `VenueListPage.vue` 裡的 import 與所有 `cache.venues`／`cache.addVenue`／`cache.addSeatMap` 呼叫，確認沒有殘留引用

## 7. 前端：建立活動表單改下拉選單

- [x] 7.1 `EventListPage.vue`（Admin）建立活動表單的場館欄位改為 `<el-select>` 下拉選單，選項來源為 `getVenues()`
- [x] 7.2 選定場館後呼叫 `getVenueById(venueId)`，座位圖欄位改為 `<el-select>` 下拉選單，選項顯示座位圖 Id（截斷）＋座位總數；場館未選或該場館底下沒有座位圖時，座位圖下拉選單顯示「尚無可選項目」且無法送出表單；**改選另一個場館時 MUST 清除已選的座位圖值**，重新呼叫 `getVenueById` 取得新場館的座位圖選項（對應新增的「切換場館後清除已選座位圖」Scenario）。**MUST 防止過期回應覆蓋較新的選擇**（見 design.md Risks）：快速連續切換場館時，只有「回應對應目前選定場館」的那次 `getVenueById` 結果可以套用到座位圖下拉選單，較晚抵達但對應較舊場館選擇的回應 MUST 被捨棄——實作上可以記錄每次呼叫當下的 `venueId`，回應抵達時比對是否仍等於目前選定的 `venueId`，不等於就不套用結果
- [x] 7.3 移除原本手動輸入場館/座位圖 Id 的文字欄位與 GUID 格式前端驗證邏輯
- [x] 7.4 補前端單元測試（Vitest），涵蓋：
  - 場館下拉選單有選項時可選取／座位圖下拉選單隨場館選擇更新（對應「選擇場館後座位圖下拉選單隨之更新」Scenario）
  - 場館或座位圖無可選項目時表單無法送出（對應「尚未有任何場館或座位圖時無法選擇」Scenario）
  - 已選座位圖後改選另一個場館，座位圖選擇值被清除、下拉選單改顯示新場館的選項（對應「切換場館後清除已選座位圖」Scenario）
  - 快速連續切換兩次場館（例如先選 A 再選 B），若 B 的 `getVenueById` 回應先抵達、A 的回應後抵達，最終座位圖下拉選單 MUST 顯示 B 場館的選項，不得被較晚抵達的 A 回應覆蓋（對應 7.2 的過期回應防護）
  - `getVenues()` 或 `getVenueById()` 呼叫失敗時顯示錯誤訊息，不讓下拉選單卡在載入中或顯示過期資料
  - 建立活動成功後表單完整 reset，包含場館／座位圖下拉選單的選擇值也一併清空（不只是原本的文字欄位）

## 8. 收尾

- [x] 8.1 `npm run lint`／`vue-tsc --noEmit`／`npm run test`／`npm run build` 皆通過
- [x] 8.2 後端 `dotnet test` 全數通過（含本次新增測試）
- [x] 8.3 用 claude-in-chrome 實際於瀏覽器驗證：開啟場館列表頁顯示既有場館真實資料（非空）→ 重新整理頁面資料仍在 → 建立新場館後列表即時出現該場館 → 點選場館看到座位圖摘要與座位數 → 開啟建立活動表單，場館/座位圖皆為下拉選單且可正確選取、切換場館後座位圖選擇被清除、送出後活動成功建立
- [x] 8.4 同步確認 `admin-web-ui`／`event-management` 兩份主 spec 的既有 Requirement 已依 delta 正確更新（歸檔時同步），檢查是否有其他文件（例如 `web/README.md`）提到「手動輸入 GUID」或 `adminVenueCache` 需要一併更新
