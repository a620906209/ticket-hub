# Design

## Context

建立訂單流程（`OrderService.PlaceOrderCoreAsync`）依序為：`PlaceOrderRequestValidator` 驗證 → 借一條連線 → 交易前 no-tracking 查詢（票種逐筆 `GetByIdAsync`、座位批次 `GetByIdsAsync`、活動、座位樣板）與快速失敗檢查（跨活動、販售期間、實名、`MaxTicketsPerOrder`、提早 409）→ 開交易、鎖活動、鎖座位／票種。

現況缺口（2026-10-16 讀程式碼確認）：

- Validator 對 `Selections` 只有 `NotEmpty`，每筆 `Quantity` 只有 `GreaterThanOrEqualTo(1)`。
- `MaxTicketsPerOrder` 是活動可選設定，只在交易前檢查；未設定時無上限。`OrderService` 已用 `long` 加總 `Quantity` 避免 `int` 溢位（外部審查曾抓到）。
- 票種存在性檢查對每個不同的 `TicketTypeId` 各查一次；`ITicketTypeRepository.GetByIdAsync` 的唯一 production 呼叫端就是這裡。
- `CreateEventRequestValidator` 對 `MaxTicketsPerOrder` 只要求 > 0。
- 驗證失敗時，`OrderService` 把所有 `ErrorMessage` 以空白串接成 `Error.Validation` 的訊息，最後成為 ProblemDetails 的 `detail`。

## Goals / Non-Goals

**Goals**

- 單一下單請求最多 50 筆選購、合計最多 50 張，在 Validator（借連線之前）擋下。
- 選購筆數超過上限時，回應只含筆數錯誤，不隨請求項目數放大。
- 建立活動的 `MaxTicketsPerOrder` 不得大於系統上限，避免前端顯示的限購值與後端不一致。
- 交易前的票種存在性檢查改為一次查詢，行為（404 與訊息）不變。

**Non-Goals**

- 前端不改：買家頁在活動未設定限購時仍允許選超過 50 張，送出後會收到後端 400（英文訊息）並依既有「其他失敗」分支清空選擇。一般購票極少超過 50 張，若要在前端提早提示，另開 change（`buyer-web-ui`）。
- 上限不做成設定檔（`IOptions`）：目前沒有依環境調整的需求，常數即可。
- 不加 Domain 守衛：既有資料若有 `MaxTicketsPerOrder` > 50，讀取時不應失敗（`event-start-time-validation` 的教訓：Domain 守衛在 EF 讀取時也會執行）。
- 本 change 不實作請求本文大小限制，改由前置 change `request-body-size-limits` 處理（見下方「前置依賴」）。系統上限在模型繫結之後才生效；前置 change 生效前，接近 Kestrel 預設 30MB 的本文仍會先被完整讀取、反序列化再擋成 400，消耗 CPU、記憶體與 GC。本 change 本身只保證「不占用資料庫連線、查詢與交易鎖」。

## 前置依賴

- **名稱**：`request-body-size-limits`（尚未建立 proposal，2026-10-16 決定拆出）。
- **為什麼拆**：本文大小是全域風險，登入、註冊等匿名端點暴露更大，不只是建立訂單；`CreateSeatMap` 的座位數沒有上限，全域限制值需要先量測合法請求的最大本文，不適合在本 change 順手決定。
- **負責人**：專案擁有者（RD-Hank）。
- **完成條件**：該 change 已 archive 並 merge 到 master，且至少包含：
  - 全域本文上限，以及建立訂單端點的個別上限；適用範圍與限制值由該 change 決定。
  - 超過上限回 413，回應格式（ProblemDetails）有明確定義。
  - WebApi 整合測試。由於 `WebApplicationFactory` 的 TestServer 不經過 Kestrel，另需在 Docker Compose 的實際容器鏈路驗證限制確實生效。
- **順序**：前置 change 先 apply、merge，本 change 才開始實作（tasks 0.1）。部署順序相同。
- **宣稱範圍**：前置 change 生效前，本 change 不得宣稱已完整防護建立訂單請求的資源消耗。
- 不處理 §8 其他待辦（Redis 預扣閘門、`UnitOfWorkTransaction.DisposeAsync`、入場鏡像同步取消測試）。

## Decisions

### 決策 1：系統上限為常數 50，放在 Application

新增 `ProjectC.Application.Orders.PlaceOrder.PlaceOrderLimits`，內含 `public const int MaxTicketsPerOrder = 50;`。`PlaceOrderRequestValidator` 與 `CreateEventRequestValidator` 共用這個常數，避免兩處數字分歧。

- 為什麼是 50：遠高於一般購票數量（現有活動限購最大 6），同時讓單一請求最多鎖 50 個座位或扣 50 張庫存。
- 為什麼放 Application 不放 Domain：這是 API 輸入的防濫用上限，不是 `Event` 自身的不變量；Domain 不加守衛（見 Non-Goals）。

### 決策 2：Validator 規則與錯誤訊息

`PlaceOrderRequestValidator`：

1. `Selections.Count` ≤ 50，訊息 `An order cannot contain more than 50 selections.`
2. `Selections` 不得含 null 元素，訊息 `Selections must not contain null items.`；只在筆數未超過上限時檢查。
3. 所有 `Quantity` 以 `long` 加總 ≤ 50（未提供 `Quantity` 的項目由 `PlaceOrderSelectionRequest` 的參數預設值 `Quantity = 1` 補上，依 TP-ORDER-010 既有行為以 1 計入；Validator 拿到的已是補上後的值，不需另外判斷缺省），訊息 `An order cannot contain more than 50 tickets in total.`；只在筆數未超過上限且沒有 null 元素時檢查。
4. 既有的逐項規則（`RuleForEach`）與兩條重複檢查，只在 `Selections` 非 null、筆數 ≤ 50 且沒有 null 元素時執行。

規則 1～4 的 `When` 條件 MUST 本身就能處理 `Selections` 為 null（例如 `x.Selections is { Count: > 0 and <= 50 }`），不得寫成 `x.Selections.Count > 0`：FluentValidation 會評估所有規則的 `When`，`NotEmpty` 回報錯誤不會阻止後續規則的 `When` 存取 `Selections.Count` 而拋 `NullReferenceException`（現況兩條重複檢查就是這樣）。

null 元素的理由：`{"selections":[null]}` 預期可以通過模型繫結（System.Text.Json 預設不依 nullable 註記拒絕 null，tasks 1.2 實測確認）；FluentValidation 的 `SetValidator` 對 null 元素不驗證，現況的重複檢查會直接存取元素屬性而拋例外，新的加總規則同樣會存取 `Quantity`。這是外部可觸發的 500，本 change 一併修正（tasks 1.2 先重現）。

規則 1～4 全部在 `PlaceOrderRequestValidator` 內，而 `OrderService.PlaceOrderCoreAsync` 在 `OpenConnectionAsync` 之前執行 Validator 並在失敗時直接回傳，因此任何一條規則失敗都不會借連線、不會查詢 Repository、不會開交易（spec「在開啟資料庫連線之前拒絕」）。這個保證由 `OrderServiceTests` 對每一類違規逐一斷言 `OpenConnectionCallCount` 為 0（tasks AC 表），不只靠 TP-LIMIT-003。

座位項目購買數量必須為 1 的既有檢查留在 `OrderService`（需要查票種的 `RequiresSeat`），在 Validator 之後執行，所以座位項目 `Quantity = 2` 且總張數未超過 50 時，回報的是既有座位數量訊息（TP-LIMIT-010）。

筆數超過上限時，回應只有規則 1 一則訊息。理由：否則每個不合格項目都會產生一則訊息，`detail` 隨請求放大（Context 最後一點）。

`long` 加總的理由與 `OrderService` 既有限購檢查相同：兩筆接近 `int.MaxValue` 的 `Quantity` 用 `int` 加總會拋 `OverflowException`，變成 500。

負數或 0 的 `Quantity` 由既有逐項規則擋下，不會被加總規則漏放：例如 `[100, -60]` 加總為 40，但 `-60` 違反 `≥ 1`，整個請求仍是 400。

`MaxTicketsPerOrder`（活動限購）檢查維持在 `OrderService` 交易前，不搬動。系統上限與活動限購同時適用，實際上限取較小值。

### 決策 3：建立活動 `MaxTicketsPerOrder` ≤ 50

`CreateEventRequestValidator` 在既有 `GreaterThan(0)` 之外加 `LessThanOrEqualTo(PlaceOrderLimits.MaxTicketsPerOrder)`，訊息 `MaxTicketsPerOrder must not exceed 50.`，只在有提供值時檢查。未提供時維持「不限購」，實際受系統上限 50 約束。

### 決策 4：票種存在性檢查改為批次查詢

`ITicketTypeRepository.GetByIdAsync(Guid)` 改為 `GetByIdsAsync(IReadOnlyList<Guid>)`：

- MUST 維持 no-tracking：理由見既有註解（交易內 `GetForUpdateAsync` 會被 EF identity resolution 回傳鎖前快照，ticket-type-requires-seat design.md 決策 3）。
- 空清單回傳空集合、不查資料庫；找不到的 Id 不補、不拋例外，比照 `IEventSeatRepository.GetByIdsAsync`。
- 舊的 `GetByIdAsync` 移除，不保留：唯一 production 呼叫端是 `OrderService`，保留會讓之後有人再寫回逐筆查詢。

`OrderService` 對請求中不同的 `TicketTypeId`（保留請求順序）一次查詢；有缺漏時回傳請求順序中第一個找不到的 Id，訊息格式維持 `Ticket type '{id}' was not found.`，與現況相同。

## 安全確認

- **輸入驗證**：全部在 Application 層 FluentValidation，於借連線、查資料庫之前執行。沒有拼接 SQL 或 shell。
- **資料庫**：`GetByIdsAsync` 用 EF Core LINQ（`Contains` 轉成參數化 `IN`／`= ANY`），不寫 raw SQL。筆數上限讓 `IN` 清單最多 50 個值。改為批次查詢後 N+1 消失。
- **權限**：不變。建立訂單需登入會員，建立活動需已切換至 Approved Organizer，檢查位置不變（Authorization Policy）。
- **前端**：不改，無新的 DOM 渲染。
- **Log**：驗證訊息不含個資或 token。
- **資源保護**：本 change 保護資料庫連線、查詢與交易鎖；模型繫結前的本文讀取與反序列化由前置 change `request-body-size-limits` 保護（見「前置依賴」）。會員分區 rate limit 擋不住多帳號或遭竊 Token 的聚合請求，不能取代本文上限。

## Risks / Trade-offs

- 直接呼叫 API 的客戶端若曾下 > 50 張的單，會改收到 400（proposal BREAKING）。目前唯一客戶端是本專案前端。
- 其他環境若有 `MaxTicketsPerOrder` > 50 的既有活動，買家頁會顯示大於 50 的限購值，但實際上限是 50。本機 DB 查過沒有；部署前可用 `SELECT count(*) FROM "Events" WHERE "MaxTicketsPerOrder" > 50;` 確認，結果不為 0 時再決定是否修正資料（不阻擋部署，因為不會造成錯誤，只是顯示不一致）。這是部署備忘，不列為 task：本 change 只在本機驗證，目前沒有其他環境可查；EVT-LIMIT-003 已確保這類資料不會讓列表讀取失敗。
- 買家頁在活動未設限購時選超過 50 張，送出才被擋，且選擇會被清空（Non-Goals）。

## Migration Plan

無 schema 變更、無資料遷移。直接部署；回滾即恢復舊行為。
