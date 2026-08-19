## Context

現行訂單流程完全建構在「座位」概念上：`CreateOrderHandler`／`ConfirmOrderHandler`／`CancelOrderHandler`／`OrderService.ChangeOrderStatusAsync` 一律以 `OrderItem.EventSeatId` 為主鍵去查、鎖、改 `EventSeat`；`TicketType` 目前只在建立訂單當下用來做分區比對與價格快照，訂單建立後就不再被引用。`OrderItem` 也完全沒有儲存 `TicketTypeId`。

新增純計數模式後，訂單裡會同時存在「有座位」與「無座位、只有數量」兩種行項，且後者的鎖定/確認/取消都要操作 `TicketType.AvailableQuantity` 而非 `EventSeat`。這代表訂單生命週期的四個核心方法都需要能同時處理兩種行項，而不是只改 `TicketType` 這一個 Entity。

## Goals / Non-Goals

**Goals:**
- `TicketType` 支援 `RequiresSeat = false` 的純計數模式，建立時免綁座位圖分區
- 買家可用 `TicketTypeId + Quantity` 直接下單，不需先取得 `EventSeatId`
- 純計數模式的庫存扣減/歸還在高併發下不超賣，且不引入座位鎖定以外的新並發控制風格
- 座位模式（`RequiresSeat = true`）既有行為（悲觀鎖、Held/Sold 狀態機、確認/取消規則）逐一比對後**零回歸**

**Non-Goals:**
- 不做電子票券出票／核銷 API（`docs/project-scope.md` §8 規劃順序 ③，另開提案）
- 不做前端 UI（見 proposal.md Impact 小節）
- 不重新設計座位模式現有的鎖定/狀態機邏輯，僅新增純計數的平行路徑
- 不處理「同一票種同時開放座位與計數兩種售票方式」——`RequiresSeat` 是票種建立時一次決定、之後不可變更

## Decisions

### 決策 1：`TicketType` 新增 `RequiresSeat`（必填 bool）與 `AvailableQuantity`（int?，僅計數模式使用）

建構邏輯依 `RequiresSeat` 分流：
- `true`：沿用現行邏輯，`ZoneCode` 必須存在於座位圖分區，`AvailableQuantity` 必須為 `null`
- `false`：`ZoneCode` 仍為必填（作為票種顯示名稱，如「站票區」），但不驗證是否存在於座位圖；`AvailableQuantity` 必須為正整數

**為什麼 `ZoneCode` 計數模式仍保留**：避免新增一個平行的「票種名稱」欄位造成兩套命名概念並存；`ZoneCode` 語意收斂為「票種的顯示分類」，是否對應真實座位圖分區由 `RequiresSeat` 決定。

**替代方案考慮**：曾考慮讓 `RequiresSeat = false` 時 `ZoneCode` 可為 null，但這會讓既有「`ZoneCode` 必填」的驗證規則產生例外分支，且前端仍需要一個名稱欄位顯示，故維持必填。

### 決策 2：`OrderItem` 新增 `TicketTypeId`（新建立時必填、DB 欄位維持 nullable）與 `Quantity`，`EventSeatId` 改為可為 null

一個新建立的 `OrderItem` 只能是以下兩種形狀之一（互斥，由 domain 建構子檢查）：
- **座位行項**：`EventSeatId` 有值、`Quantity = 1`
- **計數行項**：`EventSeatId = null`、`Quantity >= 1`

`TicketTypeId` 由 domain 建構子強制要求（兩種形狀都必填），但資料庫欄位維持 nullable、**既有舊資料不回填**——見 Migration Plan 決策說明，這是本次規劃階段跟使用者確認後的決定（原本考慮回填，評估後認為非必要、且有歧義風險，詳見下方 Migration Plan）。

**為什麼計數模式一個票種只產生一筆 `OrderItem`（不逐張展開成 N 筆 `Quantity = 1`）**：座位模式天生一張座位一筆是因為每張座位是獨立可定址的 Entity；計數模式沒有這個概念，逐張展開只會製造無意義的資料列與迴圈開銷，且會讓「這筆訂單買了幾張某票種」這個查詢從加總變成數列數，沒有任何好處。

**為什麼新增 `TicketTypeId` 而非只在建立時查一次就丟棄**：確認訂單／取消訂單／逾時清理都需要知道「這個行項要對應改哪個 `TicketType` 的庫存」，而這三個方法目前完全不接觸 `TicketType`（只查 `EventSeat`）。座位模式可以靠 `EventSeat` 反查回活動，但計數模式沒有 `EventSeat` 可查，必須直接存 `TicketTypeId`。這也讓兩種行項的資料形狀更對稱、易於程式碼分流判斷（`if (item.EventSeatId is null)`）。

### 決策 3：計數模式的庫存鎖定沿用座位鎖定同一套「悲觀交易鎖」模式，不採用原子條件式 UPDATE

新增 `ITicketTypeRepository.GetForUpdateAsync(IReadOnlyList<Guid> ticketTypeIds, ct)`，比照既有 `IEventSeatRepository.GetForUpdateAsync` 的寫法（`SELECT ... FOR UPDATE`，在既有的 `IUnitOfWork.BeginTransactionAsync` 交易內執行）。`TicketType` 新增 domain 方法：
- `Reserve(int quantity)`：`AvailableQuantity` 不足時拋 `DomainException`（比照 `EventSeat.Hold` 拋例外的風格），成功則扣減
- `Release(int quantity)`：無條件歸還（取消/逾時時呼叫，比照 `EventSeat.ReleaseHold`）

下單建立訂單時呼叫 `Reserve`（代表座位模式的 Held，庫存已經被這筆 Pending 訂單佔用）；確認訂單時**不**額外扣減（建立時已扣）；取消/逾時清理時呼叫 `Release`。

**為什麼不用原子條件式 UPDATE（`UPDATE ... SET Qty = Qty - N WHERE Qty >= N`）**：這是本次規劃階段唯一認真評估過的替代方案，優點是完全不用加鎖、單一 SQL 陳述式就能防超賣。但否決理由：
1. 座位鎖定已經建立了一套「Repository 提供 `GetForUpdateAsync` → 交易內鎖定 → domain 物件在記憶體改狀態 → EF Core 隨交易提交寫回」的既定模式（`Repository/UnitOfWork/locking/lock-then-reread` pattern），原子 UPDATE 是完全不同的寫法風格，會讓同一個 Application 層裡並存兩種完全不同的並發控制心智模型，增加維護與新人理解成本（見 CLAUDE.md Rule 11：一致性優先於個人偏好）
2. `TicketType` 的鎖定範圍永遠是單一列（一次下單只會涉及該票種這一列），不像座位鎖定要「多列 + 固定順序」防死鎖，所以沿用悲觀鎖也不會引入新的死鎖風險，等於是「用同一套模式、卻不需要多付代價」
3. 若之後真的要接電子票券出票（規劃順序 ③），出票邏輯掛在確認訂單事件上，屆時如果庫存扣減與座位確認是同一套交易/鎖定風格，邏輯會更好合併；兩套並存則出票邏輯要分別處理

### 決策 4：`PlaceOrderRequest` 的選購項目擴充為可攜帶 `Quantity`，`EventSeatId` 改為可為 null

`PlaceOrderSelectionRequest` 由 `(Guid EventSeatId, Guid TicketTypeId)` 擴充為 `(Guid? EventSeatId, Guid TicketTypeId, int Quantity)`。跨欄位規則（EventSeatId 與 TicketType.RequiresSeat 是否一致）**不放在 FluentValidation**，因為 `RequiresSeat` 要查 DB 才知道，跟現行「分區比對」（`OrderService.PlaceOrderAsync` 裡座位分區 vs 票種分區）走同一個位置：`OrderService.PlaceOrderAsync` 載入 `TicketType` 後立即檢查。FluentValidation 只驗證結構層：`Quantity >= 1`、`TicketTypeId` 不可為空。

**為什麼不新增獨立的計數版下單端點**：座位模式與計數模式在同一張訂單裡本來就可能混合（例如同一場活動同時賣站票與對號座），拆成兩個端點會強迫買家分兩次下單、也不符合「一張訂單」的既有語意。

## Risks / Trade-offs

- **[Risk] `OrderItem` 形狀從單一（永遠是座位）變成雙形狀（座位/計數互斥），既有讀取 `item.EventSeatId` 的呼叫端（`ConfirmOrderHandler`、`CancelOrderHandler`、`OrderService.ChangeOrderStatusAsync` 的 `eventSeatIds = order.Items.Select(i => i.EventSeatId)...`）若忘記過濾 null 會直接壞掉** → Mitigation：這幾個方法本次全部要重構為「先依 `EventSeatId is null` 分流」，且新增的測試須涵蓋「訂單同時含座位行項與計數行項」的混合情境，不能只測純座位或純計數
- **[Risk] `MaxTicketsPerOrder` 限購邏輯目前用 `request.Selections.Count`（座位模式下一列等於一張票），計數模式一列可能代表多張，會低估張數** → Mitigation：限購檢查改為對 `Quantity` 加總（座位模式 `Quantity` 固定 1，語意自然相容，不需要為座位模式另外分流）
- **[Trade-off] `ZoneCode` 在計數模式下語意从「座位分區代碼」變成「票種顯示名稱」，兩種語意共用一個欄位** → 已在決策 1 說明取捨；若未來語意分歧到需要不同驗證規則（例如長度、格式），屆時再評估是否拆欄位，本次先不過度設計
- **[Risk] `AvailableQuantity` 只在建立訂單時扣減（Reserve），若該訂單之後被確認，庫存不會再變動；若逾時或取消，`Release` 歸還——這與座位 Held→Sold（狀態轉換，不是計數）不同，需要額外測試「訂單逾時清理」路徑也正確呼叫到 `TicketType.Release`，不能只測座位的清理路徑** → Mitigation：`tasks.md` 需明確列出「逾時清理同時涵蓋計數行項」的測試任務

## Migration Plan

1. EF Core migration：`TicketType` 新增 `RequiresSeat`（`NOT NULL DEFAULT true`，回填既有資料全部為 `true`，符合現況）、`AvailableQuantity`（nullable）；`OrderItem` 新增 `TicketTypeId`（**nullable，不回填既有資料**）、`Quantity`（`NOT NULL DEFAULT 1`，既有座位行項全部符合）、`EventSeatId` 改 nullable
2. **`OrderItem.TicketTypeId` 不回填既有資料**（規劃階段已與使用者確認，見下方決策說明）——`TicketTypeId` 只在 domain 建構子層對「新建立」的 `OrderItem` 強制要求，資料庫欄位維持 nullable。既有座位行項的 Confirm/Cancel 邏輯本來就只依賴 `EventSeatId`（不讀 `TicketTypeId`），所以舊資料留空不影響任何既有功能運作，純粹只是「無法反查歷史訂單買的是哪個票種」這個目前系統也還沒有的需求。若之後真的需要追溯歷史訂單票種，屆時再評估回填腳本（那時候才需要處理下面這個歧義問題）
3. 無回滾（rollback）特別設計：本次都是新增欄位／改寬鬆（nullable），不刪除既有欄位，理論上可安全 down-migration

**為什麼不回填**：原本規劃考慮透過 `EventSeat → Seat → ZoneCode → TicketType`（同活動同分區）反查回填，但現行 `TicketTypeConfiguration` 沒有 `(EventId, ZoneCode)` 唯一性約束，理論上可能存在「同分區多個 `TicketType`」的歧義資料，回填腳本會遇到猜不出正確答案的情況。評估後認為：回填只是「錦上添花」（方便查歷史票種），不是這次功能上線的必要條件，為了這個非必要需求去處理歧義資料、寫回填腳本、承擔猜錯的風險，不符合效益。因此規劃階段與使用者確認後，決定舊資料不回填，`TicketTypeId` 只保證新資料一定有值。

## 已確認事項

- **計數模式的 `MaxTicketsPerOrder` 限購與座位模式共用同一個上限**，不拆成獨立欄位（沿用既有 `Event.MaxTicketsPerOrder`，見 Risks 小節）。若之後有主辦方要求座位票／計數票分開設限購，再另開提案處理，本次不預先設計
