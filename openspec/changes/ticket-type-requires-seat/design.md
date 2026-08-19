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

**⚠️ API 相容性（外部審查抓到、原規劃遺漏）：`CreateTicketTypeRequest` 新增 `RequiresSeat` MUST 有預設值 `true`，不能是無預設值的必填 `bool`**：本提案明確排除前端（見 proposal.md Impact），代表後端上線後、前端提案完成前，既有 admin-web-ui 的「建立票種」表單會繼續送出舊格式 JSON（沒有 `requiresSeat` 欄位）。若 `CreateTicketTypeRequest` 的 `RequiresSeat` 是一般無預設值的 `bool` 參數，System.Text.Json 對缺欄位的 JSON 會用 C# 型別預設值 `false`（不會拋錯）——等於既有的「建立座位票」請求全部被誤判成「純計數票」，再因為沒帶 `AvailableQuantity` 被拒絕，直接壞掉既有功能，違反本文件 Goals「座位模式零回歸」。修正方式：在 record 的主建構子把該參數宣告為 `bool RequiresSeat = true`（C# 預設值）；System.Text.Json 用建構子反序列化時，缺欄位會套用這個宣告的預設值而非型別預設值，讓舊請求（未帶 `requiresSeat`）自動視為座位模式，行為與改動前完全一致。

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

**⚠️（修正）不是「呼叫哪份實例」的問題，是 EF Core identity resolution 會讓兩份實例根本是同一個物件**：先前這裡的說法是「`GetForUpdateAsync` 會回傳另一份、交易內鎖定的 `TicketType` 實例，`Reserve()` 只要呼叫在那份實例上就好」——這個前提是錯的，已經過外部審查抓出來並驗證過。`ApplicationDbContext` 是 Scoped（`Program.cs` `AddDbContext`），一次 HTTP 請求一個 DbContext 實例。`OrderService.PlaceOrderAsync` 現行程式碼在 `BeginTransactionAsync`（第 72 行）**之前**，就已經用 `_ticketTypeRepository.GetByIdAsync`（tracking query，`TicketTypeRepository.cs` 沒有 `AsNoTracking`）建立了 `ticketTypesById`，這時該 `TicketType` 的主鍵已經被同一個 DbContext 的 change tracker 追蹤。之後在交易內呼叫 `GetForUpdateAsync`，即使底層 SQL 真的執行了 `SELECT ... FOR UPDATE` 並取得資料庫列鎖，EF Core 的 identity resolution 規則是：**同一個 DbContext 對同一主鍵的第二次查詢，只要該主鍵已經被追蹤，就直接回傳原本追蹤的那個物件，新查到的欄位值不會覆寫進去**。也就是說 `GetForUpdateAsync` 回傳的極可能就是 `ticketTypesById` 裡那個帶著鎖前舊值的同一個物件——不存在「兩份實例」，鎖確實鎖到了 DB 列，但記憶體裡的 `AvailableQuantity` 仍是交易前讀到的舊快照，`Reserve()` 會依舊值判斷「庫存足夠」而放行，這就是超賣。

**修正方案**：`ticketTypesById` 這個查詢純粹只用於交易前的存在性/`RequiresSeat`比對，不會被拿去做任何寫入，MUST 改成 no-tracking——`ITicketTypeRepository.GetByIdAsync`（目前唯一呼叫端就是這裡）加上 `.AsNoTracking()`。這樣交易前的驗證查詢不會在 change tracker 留下追蹤紀錄，`GetForUpdateAsync` 在交易內的查詢就會是該主鍵的第一次、也是唯一一次 tracking，回傳的物件保證反映鎖定後的最新值。`Reserve()` MUST 只對 `GetForUpdateAsync` 回傳的實例呼叫（這點結論不變，但理由不再是「另一份實例天生分開」，而是「no-tracking 前查 + tracking 後鎖，保證後者是唯一被追蹤的版本」）。

**⚠️ 確認訂單（Confirm）純計數訂單時，即使不寫入 `AvailableQuantity`，仍然 MUST 鎖定對應 `TicketType` 列**：`ChangeOrderStatusAsync` 現行對座位模式訂單的並發安全，是靠鎖 `EventSeat` 列（`GetForUpdateAsync`）當作序列化點——兩個並發的 Confirm/Cancel 呼叫會在鎖那一步互相卡住，後到的那個等鎖釋放後透過 `ReloadAsync` 重讀訂單狀態，發現已被處理過而拒絕（見既有程式碼註解「鎖後重讀，避免兩個並發的同類操作其中一個誤報成功」）。純計數訂單沒有任何 `EventSeat`，如果 Confirm 路徑因為「反正不用寫入 `AvailableQuantity`」而省略鎖 `TicketType`，這筆訂單在資料庫層就完全沒有序列化點——兩個並發的 `ConfirmOrderAsync` 呼叫會同時讀到 `order.Status == Pending`、同時呼叫 `_paymentGateway.ChargeAsync`，造成同一筆訂單重複收款。鎖 `TicketType` 在這裡的作用是「序列化」，不是「保護即將發生的寫入」，兩者要分開理解，不能套用「沒有要改資料就不用鎖」的直覺。

**為什麼不用原子條件式 UPDATE（`UPDATE ... SET Qty = Qty - N WHERE Qty >= N`）**：這是本次規劃階段唯一認真評估過的替代方案，優點是完全不用加鎖、單一 SQL 陳述式就能防超賣。但否決理由：
1. 座位鎖定已經建立了一套「Repository 提供 `GetForUpdateAsync` → 交易內鎖定 → domain 物件在記憶體改狀態 → EF Core 隨交易提交寫回」的既定模式（`Repository/UnitOfWork/locking/lock-then-reread` pattern），原子 UPDATE 是完全不同的寫法風格，會讓同一個 Application 層裡並存兩種完全不同的並發控制心智模型，增加維護與新人理解成本（見 CLAUDE.md Rule 11：一致性優先於個人偏好）
2. ~~`TicketType` 的鎖定範圍永遠是單一列~~——**這個說法已修正為錯誤**：一筆訂單可以同時包含多個不同的純計數票種（例如站票 + 停車票，spec 並未禁止），所以 `TicketType` 鎖定跟座位鎖定一樣，同樣需要「多列 + 固定順序」防死鎖，並非可以省略這道防護。見下方鎖定順序規則的修正版本。
3. 若之後真的要接電子票券出票（規劃順序 ③），出票邏輯掛在確認訂單事件上，屆時如果庫存扣減與座位確認是同一套交易/鎖定風格，邏輯會更好合併；兩套並存則出票邏輯要分別處理

**完整鎖定順序規則（固定規則，MUST 遵守，本輪修正為兩層順序）**：一筆訂單可能同時涉及多個 `EventSeat`、多個不同的 `TicketType`（純計數票種之間，例如站票 + 停車票），死鎖風險同時存在於「跨資源類型」與「同資源類型多列」兩個層次，缺一不可：

1. **跨資源類型順序**：`PlaceOrderAsync`／`ChangeOrderStatusAsync` 在同一筆交易內若同時需要鎖座位與鎖票種庫存，一律 **先呼叫 `IEventSeatRepository.GetForUpdateAsync` 鎖座位、再呼叫 `ITicketTypeRepository.GetForUpdateAsync` 鎖票種**，兩處呼叫點（建立訂單、確認/取消/逾時清理共用的 `ChangeOrderStatusAsync`）都要遵守同一順序
2. **同資源類型內的順序**：`ITicketTypeRepository.GetForUpdateAsync` MUST 比照 `IEventSeatRepository.GetForUpdateAsync` 的既有寫法——接受一組 `TicketTypeId`（而非單一 ID）、方法內部自行 `Distinct()` 去重（不信任呼叫端已去重）、用**單一 SQL 陳述式**搭配 `ORDER BY "Id" FOR UPDATE`（資料庫端排序，不靠 .NET 端排序），一次鎖定所有列，不得逐筆迴圈個別鎖定。這樣不論請求裡選購項目的順序為何（買家可能先選站票再選停車票，或反過來），最終鎖定順序永遠由 `Id` 決定，所有交易走同一條路徑，不會因為輸入順序不同而死鎖

這是本次審查後才明確補上的規則——先前的規劃誤判「`TicketType` 永遠是單一列」，忽略了同一次下單可以涉及多個不同計數票種的情況，這個規則缺口若不補上，兩個並發的多計數票種訂單有真實死鎖風險（PostgreSQL `FOR UPDATE` 對鎖定順序不一致的交易會偵測並中止其中一個）。

**這兩個方法目前對「零座位」的呼叫沒有防呆，必須補上**：`IEventSeatRepository.GetForUpdateAsync` 的既有 XML doc 明載「`eventSeatIds` 為空清單時拋出 `ArgumentException`」。純計數訂單（沒有任何座位項目）會讓 `eventSeatIds` 過濾 null 後變成空清單，若照舊無條件呼叫這個方法會直接拋例外——這正是本次審查抓到的第一個落差。`PlaceOrderAsync`（目前第 75 行）與 `ChangeOrderStatusAsync`（目前第 201 行）兩處呼叫都要改成「僅在 `eventSeatIds` 非空時才呼叫 `GetForUpdateAsync`」。

### 決策 4：`PlaceOrderRequest` 的選購項目擴充為可攜帶 `Quantity`，`EventSeatId` 改為可為 null

`PlaceOrderSelectionRequest` 由 `(Guid EventSeatId, Guid TicketTypeId)` 擴充為 `(Guid? EventSeatId, Guid TicketTypeId, int Quantity)`。跨欄位規則（EventSeatId 與 TicketType.RequiresSeat 是否一致）**不放在 FluentValidation**，因為 `RequiresSeat` 要查 DB 才知道，跟現行「分區比對」（`OrderService.PlaceOrderAsync` 裡座位分區 vs 票種分區）走同一個位置：`OrderService.PlaceOrderAsync` 載入 `TicketType` 後立即檢查。FluentValidation 只驗證結構層：`Quantity >= 1`、`TicketTypeId` 不可為空。

**座位項目的 `Quantity` 也需要交叉驗證，不能只檢查 `EventSeatId` 有沒有給**：座位模式下 `Quantity` 語意上只能是 1（一個 `EventSeatId` 對應一張票），但既有 `SeatSelection` record（`src/ProjectC.Application/Orders/SeatSelection.cs`）完全沒有 `Quantity` 欄位，`CreateOrderHandler` 建構 `OrderItem` 時本來就固定寫 1。如果只檢查「座位項目未指定 `EventSeatId` MUST 拒絕」，卻不檢查「座位項目指定了非 1 的 `Quantity`」，這種請求會被靜默接受、`Quantity` 欄位的值直接被忽略，買家或前端可能誤以為訂了多張，回應卻只鎖一個座位、金額對不上，且沒有任何驗證錯誤提示。`OrderService.PlaceOrderAsync` 載入 `TicketType` 後的交叉驗證，MUST 一併檢查：座位項目（`EventSeatId` 有值）的 `Quantity` 必須恰好為 1，不為 1 MUST 拒絕並回報驗證錯誤。

**為什麼不新增獨立的計數版下單端點**：座位模式與計數模式在同一張訂單裡本來就可能混合（例如同一場活動同時賣站票與對號座），拆成兩個端點會強迫買家分兩次下單、也不符合「一張訂單」的既有語意。

**⚠️ API 相容性（外部審查抓到、原規劃遺漏）：`PlaceOrderSelectionRequest` 新增 `Quantity` MUST 有預設值 `1`**：跟決策 1 的 `RequiresSeat` 是同一類問題——既有買家端下單流程從未帶過 `Quantity`，前端這次不改動。若 `Quantity` 是無預設值的 `int`，缺欄位時 System.Text.Json 會給 `0`，接著被新增的 `Quantity >= 1` 驗證規則拒絕，既有的座位選購流程直接壞掉。修正方式：`PlaceOrderSelectionRequest` 的 `Quantity` 參數宣告為 `int Quantity = 1`，讓既有只帶 `EventSeatId`／`TicketTypeId` 的舊請求視為購買 1 張（座位模式的既定語意本來就是 1），行為不變。

**既有 `PlaceOrderRequestValidator` 的重複座位檢查需要同步修正**：現行 `.Must(selections => selections.Select(s => s.EventSeatId).Distinct().Count() == selections.Count)` 假設 `EventSeatId` 一定有值。`EventSeatId` 改為 `Guid?` 後，兩筆不同計數項目的 `EventSeatId` 都是 `null`，`Distinct()` 會把兩個 `null` 收斂成 1 個，導致誤判「同一座位選兩次」而拒絕合法的多計數項目請求。修正方式：先過濾掉 null 再做 `Distinct().Count()` 比對，只針對有 `EventSeatId` 的座位項目檢查重複。

**同一個計數票種在同一次請求中出現兩次以上 MUST 被拒絕**：決策 2 的既定不變量是「一個票種只產生一筆 `OrderItem`」，但這只規範了輸出端的資料形狀，沒規範輸入端——如果請求裡對同一個 `TicketTypeId`（`EventSeatId = null` 的計數項目）送出兩筆以上選購項目（例如分兩筆各買 2 張、3 張），既有的重複檢查只比對 `EventSeatId`，不會攔到這種情況。這裡採用跟座位重複選取一致的策略：**拒絕、不自動合併**——比對 `PlaceOrderRequestValidator` 既有「座位不可重複選取」的處理方式，新增一條規則：`EventSeatId = null` 的選購項目之間，`TicketTypeId` MUST 互不重複；買家想買多張同一計數票種，MUST 把數量加總成單一選購項目的 `Quantity` 送出，不接受拆成多筆。這樣可以讓「一個票種一筆 `OrderItem`」在輸入端就自然成立，`CreateOrderHandler.Handle` 不需要額外寫合併邏輯。

## Risks / Trade-offs

- **[Risk][阻斷，外部審查抓到] EF Core identity resolution 導致 `Reserve()` 可能作用在鎖前舊快照上** → 詳見決策 3 修正段落；Mitigation：`ITicketTypeRepository.GetByIdAsync`（`PlaceOrderAsync` 交易前存在性檢查用）改為 no-tracking，確保 `GetForUpdateAsync` 是該主鍵在這個 DbContext 內唯一一次 tracking，回傳值保證反映鎖定後最新值
- **[Risk][阻斷，外部審查抓到] `TicketType` 鎖定並非「永遠單列」，多計數票種訂單有死鎖風險** → 詳見決策 3 修正段落；Mitigation：`ITicketTypeRepository.GetForUpdateAsync` 比照 `EventSeatRepository` 用單一 SQL + `ORDER BY "Id" FOR UPDATE` 鎖定，不逐筆迴圈
- **[Risk][阻斷，外部審查抓到] 新增的 `RequiresSeat`／`Quantity` 若無預設值，既有前端（本提案不改動）送出的舊格式 JSON 會被 System.Text.Json 填入型別預設值（`false`/`0`），直接壞掉既有座位模式功能** → 詳見決策 1／決策 4；Mitigation：兩個欄位在 record 主建構子分別宣告預設值 `true`／`1`
- **[Risk] `OrderItem` 形狀從單一（永遠是座位）變成雙形狀（座位/計數互斥），既有讀取 `item.EventSeatId` 的呼叫端（`ConfirmOrderHandler`、`CancelOrderHandler`、`OrderService.ChangeOrderStatusAsync` 的 `eventSeatIds = order.Items.Select(i => i.EventSeatId)...`）若忘記過濾 null 會直接壞掉** → Mitigation：這幾個方法本次全部要重構為「先依 `EventSeatId is null` 分流」，且新增的測試須涵蓋「訂單同時含座位行項與計數行項」的混合情境，不能只測純座位或純計數
- **[Risk] `MaxTicketsPerOrder` 限購邏輯目前用 `request.Selections.Count`（座位模式下一列等於一張票），計數模式一列可能代表多張，會低估張數** → Mitigation：限購檢查改為對 `Quantity` 加總（座位模式 `Quantity` 固定 1，語意自然相容，不需要為座位模式另外分流）
- **[Trade-off] `ZoneCode` 在計數模式下語意从「座位分區代碼」變成「票種顯示名稱」，兩種語意共用一個欄位** → 已在決策 1 說明取捨；若未來語意分歧到需要不同驗證規則（例如長度、格式），屆時再評估是否拆欄位，本次先不過度設計
- **[Risk] `AvailableQuantity` 只在建立訂單時扣減（Reserve），若該訂單之後被確認，庫存不會再變動；若逾時或取消，`Release` 歸還——這與座位 Held→Sold（狀態轉換，不是計數）不同，需要額外測試「訂單逾時清理」路徑也正確呼叫到 `TicketType.Release`，不能只測座位的清理路徑** → Mitigation：`tasks.md` 需明確列出「逾時清理同時涵蓋計數行項」的測試任務
- **[Risk] `MaxTicketsPerOrder` 限購檢查目前用 `eventSeats[0].EventId` 反查活動（`OrderService.PlaceOrderAsync` 目前第 86 行），純計數訂單的 `eventSeats` 是空清單，索引會直接 `IndexOutOfRangeException`** → Mitigation：改用選購項目清單本身（例如第一筆選購項目對應的 `TicketType.EventId`，`ticketTypesById` 已經先載入）反查活動，不依賴 `eventSeats` 是否非空
- **[Risk] 確認訂單付款金額計算漏算 `Quantity`——`ConfirmOrderHandler.cs:49` 現行是 `order.Items.Sum(i => i.UnitPrice)`，未乘以張數，計數項目一筆 `OrderItem` 可能代表多張** → Mitigation：改為 `order.Items.Sum(i => i.UnitPrice * i.Quantity)`；座位項目 `Quantity` 固定 1，語意自然相容，需新增測試驗證「一筆計數項目、`Quantity > 1`」的付款金額正確
- **[Risk] `OrderDetailDto`／`OrderItemDto`（`GetOrderById` 查詢）未涵蓋在原本的任務規劃內：`EventSeatId` 改為 `Guid?` 後，`GetOrderByIdHandler` 目前建構 `OrderItemDto` 的呼叫會編譯失敗；即使修正型別，買家也無法在訂單明細看到計數項目對應的票種與購買數量** → Mitigation：`OrderItemDto` 新增 `TicketTypeId`、`Quantity`，`EventSeatId` 改為 `Guid?`，並新增對應測試/查詢驗證
- **[Risk] 買家端查詢票種列表的 `TicketTypeDto`（`GetTicketTypesHandler`，`GET /api/events/{id}/ticket-types`，`ticket-purchase` spec 既有的「查詢活動票種與價格」Requirement）目前只有 `Id`／`ZoneCode`／`Price`，沒有 `RequiresSeat`——呼叫端完全無從得知某個 `TicketTypeId` 是否要指定座位，只能用嘗試錯誤法（送出去被拒絕才知道），這跟 `OrderDetailDto` 是同一類「DTO 沒同步更新」問題，但影響的是建立訂單前的查詢流程** → Mitigation：`TicketTypeDto` 新增 `RequiresSeat`（bool），計數模式額外回傳 `AvailableQuantity`（讓買家或呼叫端知道剩餘庫存，比照座位可售狀態查詢的精神），`GetTicketTypesHandler` 同步調整

## Migration Plan

1. EF Core migration：`TicketType` 新增 `RequiresSeat`（`NOT NULL DEFAULT true`，回填既有資料全部為 `true`，符合現況）、`AvailableQuantity`（nullable）；`OrderItem` 新增 `TicketTypeId`（**nullable，不回填既有資料**）、`Quantity`（`NOT NULL DEFAULT 1`，既有座位行項全部符合）、`EventSeatId` 改 nullable
2. **`OrderItem.TicketTypeId` 不回填既有資料**（規劃階段已與使用者確認，見下方決策說明）——`TicketTypeId` 只在 domain 建構子層對「新建立」的 `OrderItem` 強制要求，資料庫欄位維持 nullable。既有座位行項的 Confirm/Cancel 邏輯本來就只依賴 `EventSeatId`（不讀 `TicketTypeId`），所以舊資料留空不影響任何既有功能運作，純粹只是「無法反查歷史訂單買的是哪個票種」這個目前系統也還沒有的需求。若之後真的需要追溯歷史訂單票種，屆時再評估回填腳本（那時候才需要處理下面這個歧義問題）
3. 無回滾（rollback）特別設計：本次都是新增欄位／改寬鬆（nullable），不刪除既有欄位，理論上可安全 down-migration

**為什麼不回填**：原本規劃考慮透過 `EventSeat → Seat → ZoneCode → TicketType`（同活動同分區）反查回填，但現行 `TicketTypeConfiguration` 沒有 `(EventId, ZoneCode)` 唯一性約束，理論上可能存在「同分區多個 `TicketType`」的歧義資料，回填腳本會遇到猜不出正確答案的情況。評估後認為：回填只是「錦上添花」（方便查歷史票種），不是這次功能上線的必要條件，為了這個非必要需求去處理歧義資料、寫回填腳本、承擔猜錯的風險，不符合效益。因此規劃階段與使用者確認後，決定舊資料不回填，`TicketTypeId` 只保證新資料一定有值。

## 已確認事項

- **計數模式的 `MaxTicketsPerOrder` 限購與座位模式共用同一個上限**，不拆成獨立欄位（沿用既有 `Event.MaxTicketsPerOrder`，見 Risks 小節）。若之後有主辦方要求座位票／計數票分開設限購，再另開提案處理，本次不預先設計
