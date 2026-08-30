## Context

Admin 後台目前有 `event-management`（活動/票種建立、`GET /api/admin/events` 座位狀態統計）、`order-administration`（訂單列表/明細，含 Admin-only 權限規則）兩個既有能力，但沒有任何端點回答「這場活動目前賣了多少錢」。`Order`（`Status`：`Pending`/`Paid`/`Cancelled`）、`OrderItem`（`TicketTypeId`、`Quantity`、`UnitPrice`，座位制 `EventSeatId` 有值且 `Quantity=1`，計數制 `EventSeatId=null` 且 `Quantity>=1`）、`TicketType`（`ZoneCode`、`Price`、`RequiresSeat`）三者已提供本次報表所需的全部資料，不需新增欄位或 migration。

既有查詢型 Handler 有兩種模式可參考：
- `GetOrdersHandler`：透過 `IOrderRepository.GetAllAsync` 水合整個 `Order` 聚合根（含 `Items`），適合「訂單本身是回傳內容」的情境
- `GetAdminEventsHandler`：混合使用 `IEventRepository`／`IEventSeatRepository` 查領域資料，額外透過 `IApplicationDbContext.Members` 直接查會員顯示名稱（跨模組唯讀查詢，不透過 Repository）

本次報表要做的是「跨 `Order`/`OrderItem`/`TicketType` 三表、依票種分組加總」，不需要任何一個聚合根的完整狀態或行為，若沿用 `GetAllAsync` 水合整個 `Order.Items` 再在記憶體內用 LINQ 分組加總，會把不需要的座位/計數細節、跨活動的其他訂單一併載入，資料庫端 `GROUP BY` 明顯更有效率。

## Goals / Non-Goals

**Goals:**
- 單一活動的即時銷售彙總：總營收、總售出票券張數、依票種拆分的營收與售出張數
- 所有訂單銷售數字（總營收、總張數、依票種明細加總、無法歸類分組加總）SHALL 由單一資料庫聚合查詢取得，不水合不需要的 `Order`/`OrderItem` 聚合根欄位，避免 N+1；活動存在性驗證與票種目錄查詢是各自獨立的唯讀查詢，用於組裝顯示用的完整清單（含 0 銷售的票種）與判斷分組歸屬，不影響「總數＝明細加總＋無法歸類加總」這個等式本身的一致性——完整 API 呼叫並非只有一次 SQL round-trip，「單次查詢」特指銷售金額彙總這一段，不含票種目錄查詢
- 沿用既有 Admin 權限與活動存在性驗證慣例（比照 `event-management`／`order-administration`）

**Non-Goals:**
- 跨活動彙總（全平台總營收）——本次僅查單一活動，跨活動彙總留待未來若有需求再提案
- 歷史趨勢／時間序列圖表——僅回傳查詢當下的快照數字
- 核銷狀態統計（已核銷/未核銷張數）——這是 `ticket-redemption` 能力的關注點，本次報表只看「賣了多少錢」，不看「核銷了多少」

## Decisions

**1. 新增 `IOrderRepository.GetPaidItemSalesByEventIdAsync`，不新開一個 Repository 介面，且只用「一次」分組查詢取得全部彙總數字（取代原設計的兩個方法）**
報表查詢的主體仍是「Order 相關的唯讀彙總」，且目前的 Repository 數量（`IEventRepository`/`IOrderRepository`/`ITicketTypeRepository`/`IEventSeatRepository`）已經對應到清楚的聚合根切分；為這一個彙總查詢再開一個新介面（例如 `ISalesReportRepository`）沒有帶來額外的職責區隔，只會增加 DI 註冊與測試替身的數量。因此直接在 `IOrderRepository` 新增一個方法：
```
Task<IReadOnlyList<OrderItemSalesGroup>> GetPaidItemSalesByEventIdAsync(Guid eventId, CancellationToken cancellationToken);
```
`OrderItemSalesGroup`（`record`，置於 `ProjectC.Domain.Orders`，欄位：`Guid? TicketTypeId`、`int ItemCount`、`int QuantitySold`、`decimal Revenue`）是純資料投影，不是聚合根，不帶行為；`TicketTypeId` 刻意保留 `Guid?`（而非拆成分開的 classified/unclassified 型別），因為這個 record 本身就是「依 `TicketTypeId` 分組後的一列」，`TicketTypeId = null` 那一列代表無法歸類的舊資料分組——這是查詢結果的真實形狀，不應該用非 nullable 型別掩蓋。

`GetPaidItemSalesByEventIdAsync` 的介面契約（實作時 MUST 寫進 `IOrderRepository` 的 XML Doc，避免實作者或未來的測試替身語意漂移）：
- 只包含 `Order.EventId == eventId` **且** `Order.Status == Paid` 的 `OrderItem`
- 依 `TicketTypeId` 分組，每個相異的 `TicketTypeId` 值最多出現一組
- `TicketTypeId = null` 的項目自成一組，最多一組（若有任何一筆對應項目才會出現，沒有則不出現）
- 沒有符合條件的項目時回傳空集合（`IReadOnlyList<OrderItemSalesGroup>` 的空清單），MUST NOT 回傳 `null`
- `ItemCount` 是該分組內 `OrderItem` 的**筆數**（`Count()`），不是售出張數；`QuantitySold` 才是依 `Quantity` 加總的售出張數——兩者語意不同，呼叫端不可混用
- 這個方法**不**判斷 `TicketTypeId` 是否真的屬於 `eventId` 對應的活動（只依 `TicketTypeId` 本身分組），「是否屬於本活動」是 Application 層依決策 2 的票種目錄清單另外判斷的責任，不是這個方法的職責

**重要修正（取代先前版本）**：`OrderItem` 沒有指向 `Order` 的 navigation property（`Order` 端用 `HasMany(o => o.Items).WithOne().HasForeignKey("OrderId")` 建立 shadow FK，`OrderItem` 只有純量欄位，見 `OrderItemConfiguration.cs`/`OrderConfiguration.cs`），因此 `oi.Order.EventId` 這種寫法無法編譯／無法被 EF Core 正確翻譯。查詢 MUST 從 `Order` 出發：
```csharp
await _dbContext.Orders
    .Where(o => o.EventId == eventId && o.Status == OrderStatus.Paid)
    .SelectMany(o => o.Items)
    .GroupBy(item => item.TicketTypeId)
    .Select(g => new OrderItemSalesGroup(
        g.Key,
        g.Count(),
        g.Sum(i => i.Quantity),
        g.Sum(i => i.Quantity * i.UnitPrice)))
    .ToListAsync(cancellationToken);
```
`GroupBy(item => item.TicketTypeId)` 直接對 nullable 欄位分組，SQL 端 `NULL` 會自然落在同一組，等同「一次查詢同時取得依票種明細＋無法歸類分組＋（加總所有組即可得到）總營收／總張數」——不需要、也不應該再拆成兩個方法分別查「依票種」與「總數」（見決策 3、風險段落）。
**替代方案**：沿用 `GetAllAsync` 在記憶體分組——放棄，效能隨訂單總數線性變差且無上限（會查出跟這個活動無關的其他活動訂單）。

**2. Handler 用 `ITicketTypeRepository.GetByEventIdAsync` 補齊「0 銷售」的票種，同時作為「這個分組是否真的屬於本活動」的判斷依據**
`GetPaidItemSalesByEventIdAsync` 只回傳有銷售記錄的分組（`GROUP BY` 自然排除無資料的分組），但報表應讓 Admin 看到「這個活動有哪些票種、每種各賣多少」，尚未賣出任何張數的票種也要顯示為 0，而不是從清單裡消失。`GetEventSalesReportHandler` 因此另外呼叫既有的 `ITicketTypeRepository.GetByEventIdAsync(eventId)` 取得該活動全部票種，用左外連接的方式把銷售彙總併回去（查無銷售記錄的票種 `QuantitySold`/`Revenue` 補 0）。這個查詢只影響「票種目錄」的完整性（顯示用），不影響金額類數字，因此獨立查詢不會造成決策 1 所述的一致性風險。

這份票種清單同時決定「一個非 null 的 `TicketTypeId` 分組算不算屬於本活動」（見決策 3——`OrderItem.TicketTypeId → TicketType` 只有 FK 約束，資料庫層級沒有任何機制保證 `TicketType.EventId == Order.EventId`，只靠 `OrderService.PlaceOrderAsync` 在應用層驗證，見該檔案「跨活動驗證」段落）：`TicketTypeId` 不在這份清單裡的分組，一律併入「無法歸類」，不論該 `TicketTypeId` 是 null，或是存在但屬於別的活動。

**一致性範圍的精確界定**：決策 1 的聚合查詢保證「總營收＝依票種明細加總＋無法歸類加總」這個等式恆成立，因為三者都來自同一次查詢結果的不同切分方式，不是各自獨立查詢再相加。但這份票種目錄查詢（決定某個分組該歸類到哪個票種，或該歸類為「無法歸類」）是**另一次獨立、非交易級快照**的查詢——若票種目錄在兩次查詢之間變動（目前系統沒有刪除票種的功能，理論風險極低，但設計上不假設），可能影響「這個分組該不該算某個票種的 0 銷售項目、或某個非 null 分組該分到哪一類」的顯示歸類，但**不影響**總營收／總張數這兩個金額數字本身——因為無論分組最終被歸類到 `ByTicketType` 或 `Unclassified`，都仍計入同一個總數。換句話說：金額類數字有交易級快照等級的一致性保證；票種目錄相關的顯示歸類則沒有，也不需要（票種目錄在本次範圍內是唯讀且無刪除功能，風險是理論性的）。

**3. 「無法歸類」的完整定義：`TicketTypeId` 為 null，或不在本活動票種目錄清單中——兩者統一處理，排除出依票種明細，計入總數，且以明確欄位（而非前端推算）回報筆數**
`OrderItem` 的私有物化建構子相容既有舊資料 `TicketTypeId` 為 null 的情況（見 `OrderItem.cs` 註解，`order-payment-gateway-alignment` 之前建立的訂單）——這是「無法歸類」的其中一種成因。另一種成因是資料一致性異常：`OrderItem.TicketTypeId` 指向一個確實存在、但屬於別的活動的 `TicketType`（正常流程下 `OrderService.PlaceOrderAsync` 已在建立訂單前用「票種活動」與「座位活動」的聯集驗證所有項目屬於同一場活動，見該檔案「跨活動驗證」註解段落與其後的 `distinctEventIds` 檢查，這條路徑目前不應該產生跨活動的髒資料；但報表是金額查詢，資料庫層級沒有 FK 保證這件事恆成立，選擇不假設它永遠不會發生）。兩種成因處理方式統一：
- 依票種明細（`ByTicketType`）：只收「`TicketTypeId` 非 null，且存在於決策 2 取得的本活動票種清單中」的分組
- 總營收／總售出張數：**兩種成因的項目仍計入**——它們是真實已付款的銷售金額，若因為缺分類就從總營收拿掉，Admin 看到的總數字會比實際收到的錢少，比明細少一行更嚴重；靜默丟棄（既不進明細也不進總數，也不進無法歸類統計）是本設計明確排除的選項——那樣會讓 `TotalRevenue` 跟「依票種明細加總＋無法歸類」兜不起來，且沒有任何訊號能讓人發現資料異常
- `SalesReportDto` 額外回傳 `UnclassifiedItemCount`／`UnclassifiedTicketsSold`／`UnclassifiedRevenue` 三個欄位（涵蓋以上兩種成因的加總），前端 SHALL 以 `UnclassifiedItemCount > 0` 判斷是否顯示提示，不得用「總數減明細加總」反推——金額差只能反推金額、張數差只能反推張數，無法反推筆數，且反推邏輯會把不同成因的落差混為一談
- 因為決策 1 已改為單一查詢回傳所有分組，`TotalRevenue = ByTicketType 加總 + UnclassifiedRevenue`、`TotalTicketsSold = ByTicketType 加總 + UnclassifiedTicketsSold` 這兩個等式在同一次查詢的結果下必然成立，不存在「兩次查詢查到不同資料庫狀態」的問題（取代先前版本「總數獨立查詢」的設計，該設計在查詢 1、查詢 2 之間若剛好有訂單變成 Paid，會出現總數與明細真正對不上的競態）

**4. 不依 `Event.StartAtUtc` 限制查詢時機**
`docs/project-scope.md` 提到報表「僅支援活動進行中/結束後查詢」，但 `Event` Entity 目前沒有活動結束時間欄位，也沒有「活動狀態」的概念。若額外用 `StartAtUtc <= now` 當作查詢門檻，會擋掉「活動開賣中、尚未到 `StartAtUtc`（多數售票情境下訂票發生在活動開始前）」這個最需要看報表的期間，語意上不合理。決策：報表對任何存在的活動即時可查，不做時間門檻；`docs/project-scope.md` 原文措辭已於 2026-08-30 同步修正為「活動建立後即可查詢，不限制活動開始時間」，與本決策一致，不再有文件與實際規劃衝突。

**5. Response 沿用既有 DTO 慣例，不新增 Repository 之外的 Application 抽象；`GetEventSalesReportHandler` 比照既有 Handler 在 `Program.cs` 註冊為 Scoped**
比照 `AdminEventSummaryDto` 的作法，`SalesReportDto`／`TicketTypeSalesDto` 直接定義在 `ProjectC.Application.Orders.GetEventSalesReport` 底下，`GetEventSalesReportHandler` 直接組裝，不另外引入 Mapper 類別或 AutoMapper——欄位少、轉換邏輯簡單，額外抽象不會提高可讀性。這個專案的 Handler 一律在 `Program.cs` 手動 `builder.Services.AddScoped<XxxHandler>()`（例如 `GetAdminEventsHandler`、`GetOrdersHandler` 等既有登錄），不是靠組件掃描自動註冊，`GetEventSalesReportHandler` 沿用同一慣例、同一 lifetime（Scoped，理由同表：注入的 Repository 綁定同一 `DbContext`）——這一步是既有慣例的直接套用，不是新的技術決策，但遺漏會在啟動或第一次呼叫時造成 DI resolution 失敗，故明確列出以確保 tasks.md 涵蓋。

**6. 權限沿用 Admin-only，不做「主辦方僅能查看自己建立的活動」的擁有權檢查**
`docs/project-scope.md` 對主辦方角色的敘述是「主辦方查看自己活動的銷售報表」，但目前系統的權限模型是單一角色 `Admin`（沒有 `Organizer` 實體或每個 Admin 對應一個主辦方的概念），`event-management`／`order-administration` 既有能力也都是「任一 Admin 可操作全部資源」，未依 `Event.CreatedByMemberId` 做擁有權限縮。本次報表沿用相同模式：**目前階段**任一 Admin 皆可查詢任一活動的報表，不比對 `Event.CreatedByMemberId` 與呼叫者身份；**若未來系統擴充為真正多租戶**（`docs/project-scope.md` 第 1 節「系統定位」列為架構擴充性保留項），才需要在授權層新增「Admin 僅能查詢自己建立（或所屬主辦方）活動」的檢查，屆時應是跨既有 `event-management`／`order-administration`／本能力一併調整的獨立提案，不在本次範圍內單獨處理。

**7. 金額計算以 `OrderItem.UnitPrice`（下單當下寫入的快照）為準，不重新讀取 `TicketType.Price`**
`OrderItem.UnitPrice` 是建立訂單當下寫入的欄位（見 `OrderItem.cs` 建構子），代表買家實際成交的單價；`TicketType.Price` 是票種目前設定的售價，兩者在票價調整後會不一致（例如活動中途調價，先前已售出的訂單金額不應該跟著變動）。本次報表的所有金額（`ByTicketType[].Revenue`、`UnclassifiedRevenue`、`TotalRevenue`）SHALL 一律以 `OrderItem.UnitPrice × Quantity` 計算，不查詢或使用 `TicketType.Price`——這與訂單本身「已付款金額不因後續調價而變動」的既有語意一致，也是唯一在財務上正確的做法。金額欄位維持資料庫既有的 `decimal(18,2)` 精度，加總過程不做任何額外的四捨五入或型別轉換。

**8. `FakeOrderRepository` 的新方法不透過 `Data`（既有 `Order` 聚合根清單）推導，改為直接可設定的回傳值**
`OrderItem` 的公開建構子要求 `ticketTypeId` 為非 null `Guid`（只有 EF Core 專用的 private 物化建構子接受 null，見 `OrderItem.cs`），所以無法透過正常 Domain API 建出一筆 `TicketTypeId = null` 的 `OrderItem` 放進 `FakeOrderRepository.Data` 讓既有方法反推。`GetPaidItemSalesByEventIdAsync` 這個新方法的職責本來就是「回傳一組跟資料庫實際分組結果同形狀的投影」，不是「反映 `Data` 目前的內容」，因此 `FakeOrderRepository` 為這個方法新增一個可直接設定的欄位（例如 `PaidItemSalesGroups`，Application 單元測試在 Arrange 階段直接指定要回傳的 `IReadOnlyList<OrderItemSalesGroup>`，含 `TicketTypeId = null` 或指向其他活動票種的分組），不嘗試從 `Data` 推導——這樣可以在不繞過 Domain 封裝的前提下，測試 Handler 對「查詢結果包含無法歸類分組」的處理邏輯；資料庫如何真正物化舊資料、如何真正對映到 `OrderItemSalesGroup`，交給決策 1 的 Infrastructure Testcontainers 整合測試驗證（見 tasks.md 2.2.3）。這是刻意的職責分工：Application 測試驗證「Handler 拿到某個查詢結果形狀後做出正確的分類決策」，Infrastructure 測試驗證「查詢本身真的產生這個形狀」，兩者合起來才涵蓋完整路徑，任何一邊單獨都不足夠。

## Risks / Trade-offs

- **[風險] 決策 6 目前允許任一 Admin 查詢任一活動的營收，若未來真的接了多個主辦方帳號，會有資訊外洩疑慮** → Mitigation：這是延續既有 `event-management`／`order-administration` 就已存在的權限模型限制，非本次新增的風險；`docs/project-scope.md` 明確定義目前只需支援單一主辦方，多租戶留待未來架構擴充時一併處理（決策 6 已記錄）
- **[風險] 決策 4 不設時間門檻，Admin 在活動建立當下（尚未有任何訂單）就能查報表，回傳全 0** → Mitigation：這是預期行為（空報表不是錯誤），Requirement 需明確定義「活動存在但無任何票種/銷售」的回傳格式，避免前端把「總數 0」誤判為載入失敗
- **[風險] `GetByEventIdAsync`（Admin 活動列表）已經在做類似的「查詢後在記憶體組裝統計」模式，本次報表卻改用資料庫端 `GROUP BY`，兩種寫法在同一個 Controller 底下不一致** → Mitigation：這是刻意的取捨而非疏漏——`GetAdminEventsHandler` 的座位統計資料量小（單一活動座位數上限約 2000，見 project-scope 第 3 節）且已經批次查詢，記憶體分組可接受；本次報表若比照座位統計「先撈全部 Order 再記憶體分組」，會連帶撈出座位制/計數制的所有 `OrderItem` 明細，資料量級不對稱，值得用不同寫法換取效能，於 tasks.md 完成後在 PR 描述中說明這個不一致是刻意決策，不是待清理的重複邏輯
- **[風險] `GetEventSalesReportHandler` 若忘記在 `Program.cs` 註冊為 Scoped，會在 Controller 建立階段丟出 DI resolution error** → Mitigation：tasks.md 明確列出這個註冊步驟為獨立任務項，不隱含在「新增 Handler」裡；WebApi 整合測試（4.3.1）呼叫真實端點會自然驗證 DI 圖是否完整，遺漏會直接讓測試失敗，不會被漏測
- **[風險] 擴充 `IOrderRepository` 介面簽章後，任何既有實作（`OrderRepository`、`FakeOrderRepository`，未來若有其他測試替身）沒有同步補上新方法就無法編譯** → Mitigation：tasks.md 明確要求搜尋 `IOrderRepository` 的全部實作並逐一確認（見 tasks.md 第 5 節），編譯器本身也會在遺漏時直接報錯，不會是靜默問題
- **[風險] 跨活動 `TicketTypeId` 的無法歸類情境（決策 3 第二種成因）若只靠 Application 單元測試（用 `FakeOrderRepository.PaidItemSalesGroups` 直接餵資料）驗證，資料庫端這個分組真的查得出來這件事本身沒有被測到** → Mitigation：tasks.md 2.2 新增對應的 Infrastructure Testcontainers 測試，實際建立「`Order.EventId = Event-A`、`OrderItem.TicketTypeId` 指向屬於 `Event-B` 的 `TicketType`、`Order.Status = Paid`」的資料，驗證 `GetPaidItemSalesByEventIdAsync` 確實回傳這個分組（`ItemCount`/`QuantitySold`/`Revenue` 正確）——完整路徑（資料庫查詢真的撈得出異常分組 + Handler 正確分類）需要 Infrastructure 與 Application 兩層測試合起來才算涵蓋，任何一層單獨都不足夠
