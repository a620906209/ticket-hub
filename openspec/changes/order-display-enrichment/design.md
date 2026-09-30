## Context

- 本 change 從原 `order-ui-gaps` 拆出，**依賴 `order-pending-actions` 先歸檔**：本 change 的 `buyer-web-ui`／`admin-web-ui` delta 以其歸檔後的主 spec 為基準，前端頁面也已套用中文狀態標籤與 Pending 訂單操作。
- 買家查詢 DTO（`MyOrderSummaryDto`、`MyOrderDetailDto`）只有 `EventId`／`EventSeatId`／`TicketTypeId`，前端無法顯示人看得懂的資訊；後台列表 `OrderSummaryDto` 只有 `BuyerId`。
- 票務相關 entity 走 Domain 定義的 repository（`IEventRepository`、`IEventSeatRepository`、`ISeatMapRepository`、`ITicketTypeRepository`），`IApplicationDbContext` 只暴露會員／認證／主辦方相關 DbSet（含 `Members`）。座位的分區與號碼存在 `SeatMap.Seats`（`Seat` 範本），`EventSeat` 只有 `SeatId`。既有 `GetEventSeatsHandler` 以「載入座位圖 → 以 `SeatId` 對照」組出分區與號碼，但 `ISeatMapRepository.GetByIdAsync` 會 `Include` 整張座位圖的全部座位（且未 `AsNoTracking`）——該端點本就要列出全部座位，訂單明細卻只需要其中少數幾個。

## Goals / Non-Goals

**Goals:**
- 買家訂單列表／明細、訂單結果頁顯示活動名稱；明細顯示座位標示、票種名稱
- 後台訂單列表顯示買家顯示名稱取代 GUID
- 查詢筆數不隨訂單數或項目數成長（無 N+1）

**Non-Goals:**
- 後台訂單明細的座位／票種顯示名稱（明細維持既有 hotfix 後的座位 Id 與「計數票」標示）
- 買家 Email 或其他個資出現在後台列表
- 移除 `OrderSummaryDto` 既有的 `BuyerId` 欄位（前端不再顯示，但 API 保留以免破壞相容性；是否移除另行決策）
- 分頁、搜尋、排序

## Decisions

### 決策 1：顯示用關聯資料在 Application 層以批次查詢組裝，不改 repository 回傳的 Domain 物件

- **買家明細**（單筆訂單、單一活動）：以 `IEventRepository.GetByIdAsync`（活動名稱）、`ITicketTypeRepository.GetByEventIdAsync`（票種）組裝；訂單含座位項目時，另以既有 `IEventSeatRepository.GetByIdsAsync`（訂單內座位項目的 `EventSeat`）與新增的 `ISeatMapRepository.GetSeatsByIdsAsync(IReadOnlyList<Guid> seatIds)`（只取這些 `EventSeat.SeatId` 對應的座位範本，單次 `Where(Contains)`、`AsNoTracking`）取得分區與號碼；只有計數項目時不發出這兩個查詢。查詢次數固定、與項目數無關，讀取的資料量只與訂單大小有關，不隨場館座位數成長。不沿用 `ISeatMapRepository.GetByIdAsync`：它會載入整張座位圖的全部座位，萬席場館每次開啟明細、結果頁或操作後重新查詢都要讀取上萬筆，而查詢次數測試抓不到這種資料量問題。
- **買家列表**（多筆訂單、可能多個活動）：只需活動名稱。新增 `IEventRepository.GetByIdsAsync(IReadOnlyList<Guid>)`，對列表中不重複的 `EventId` 一次查詢。不用既有 `GetAllAsync` 後在記憶體過濾，因其會載入全平台所有活動。
- **後台列表**：買家顯示名稱透過 `IApplicationDbContext.Members` 以不重複 `BuyerId` 做單次 `Where(Contains)` 查詢並只投影 `Id`、`DisplayName`（寫法比照 `GetAdminEventsHandler` 以 `Where(Contains)` + `ToDictionaryAsync` 批次取 `DisplayName` 的既有模式）。注意只比照查詢寫法、不比照其「查不到回 null」：該處的 `CreatedByMemberId` 本身可為 null，查不到屬正常情況；本處的 `BuyerId` 為非 null FK，查不到即資料損毀，依決策 2 處理。

替代方案：在 `OrderRepository` 內以 join 直接投影成 DTO——會讓 Infrastructure 知道 Application 的 DTO 形狀，違反分層方向，否決。

### 決策 2：關聯資料查不到時一律視為資料不一致並大聲失敗

比照 `order-administration` 既有 `ORD-DETAIL-004`：訂單的 `EventId`、非 null 的 `EventSeatId`／`TicketTypeId`、`EventSeat.SeatId` 對應的座位範本（`Seat`）、後台列表的 `BuyerId` 對應資料查不到時，丟出例外由全域例外處理轉為 500，不回傳部分資料、不以空字串或 GUID 代替。例外訊息 MUST 帶上訂單 Id 與查不到的關聯 Id（例如 `EventId`、`BuyerId`、`SeatId`）：`GlobalExceptionHandler` 只記錄 TraceId 與例外本身，訊息是維運定位損毀資料的唯一線索；訊息只進 log，`ProblemDetails` 回應不含例外訊息，不會外洩。後台列表尤其需要——列表依 Organizer 過濾，單筆損毀會讓整個 Organizer 的訂單列表無法開啟。理由：這些都有 FK 約束（`OrderConfiguration`、`OrderItemConfiguration`、`EventSeatConfiguration`，皆 `Restrict`）且系統沒有刪除端點，查不到代表資料損毀，靜默降級會掩蓋問題。

唯一的合法 null：`EventSeatId` 為 null（純計數項目）→ 座位標示為 null；`TicketTypeId` 為 null（`ticket-type-requires-seat` 之前的舊訂單，不回填）→ 票種名稱為 null。前端分別顯示「—」。

### 決策 3：座位標示回傳分區與號碼兩個欄位，由前端組字串

回傳 `SeatZoneCode`、`SeatNumber` 兩個欄位（皆可為 null，兩者同時為 null 或同時有值），不在後端組成 `"A-12"` 字串，保留前端排版彈性。「同時為 null 或同時有值」只由 `EventSeatId` 是否為 null 決定，不存在「座位範本存在但只缺其中一欄」的情形：`Seat.ZoneCode`／`SeatNumber` 為非 nullable 型別、資料表欄位 `IsRequired`（`SeatConfiguration`），且唯一建立入口 `SeatMap.AddSeat` 拒絕空白值（`Seat` 建構子為 `internal`）。因此 Handler 不另寫部分缺失的判斷分支；此不變式以 Domain 測試固定（tasks 1.5），座位範本整筆查不到則依決策 2 回 500。`SeatNumber` 與既有 `EventSeatDto` 同名；分區刻意加上 `Seat` 前綴（`EventSeatDto` 為 `ZoneCode`），避免與同一筆項目的票種 `ZoneCode` 混淆。票種名稱欄位命名為 `TicketTypeName`，值取自 `TicketType.ZoneCode`（系統目前沒有獨立的票種名稱欄位，以此作為票種顯示名稱）。已知限制：座位項目的票種 `ZoneCode` 必然等於座位分區（`OrderService` 下單時強制檢查），畫面上會出現「票種 A／座位 A-12」的重複資訊，本次接受；日後若票種新增真正的名稱欄位，`TicketTypeName` 改取該欄位即可，欄位名稱不需變更。

## 安全確認（CLAUDE.md 安全強制規則）

本 change 觸發「資料庫讀寫」「身份驗證／授權」「前端渲染使用者輸入」三項條件，逐條回答如下：

**輸入驗證**
- 本次不新增任何外部輸入：新增的全是回應欄位，沿用既有端點與路由參數。
- 沒有任何值拼接進 SQL 或 shell 指令。

**資料庫**
- 所有新增查詢皆經 EF Core LINQ：`GetByIdsAsync` 與 `Members` 批次查詢使用 `Where(ids.Contains(x.Id))`，由 EF Core 產生參數化查詢（PostgreSQL 為 `= ANY(@p)` 陣列參數），不使用 raw SQL／ADO.NET。
- N+1：買家明細查詢次數固定（決策 1）；買家列表活動名稱、後台列表買家名稱、明細的座位範本皆為對不重複 Id 的單次查詢；不使用逐筆 `FirstOrDefaultAsync` 迴圈。空清單時不發出查詢，直接回傳空結果。三個列表／明細端點的「查詢次數不隨筆數成長」皆以整合測試計算實際送到資料庫的查詢數驗證——單元測試只能數 Fake repository 被呼叫幾次，repository 實作內部若逐筆查詢仍會通過。

**權限**
- 買家列表／明細：`OrdersController` 類別層級 `[Authorize]`；列表以 Token 內 `MemberId` 作為 `BuyerId` 過濾（Repository 查詢條件）；明細在 `GetMyOrderDetailHandler` 最前面做存在與本人檢查，**先於**任何顯示資訊查詢，非本人回 403、不存在回 404，不觸發顯示資訊查詢。顯示資訊只由該訂單自己的 `EventId`／項目 Id 查得，不可能帶出其他買家或其他 Organizer 的資料。
- 後台列表：沿用既有 `RequireOrganizerContext` Authorization Policy 與 `GetByOrganizerIdAsync` 的資料庫端租戶過濾（`ORD-LIST-001`），買家名稱只針對過濾後的訂單查詢；只投影 `Id`、`DisplayName`，不讀取、不回傳 Email 等其他欄位。

**前端**
- `EventTitle`（Organizer 輸入）、`DisplayName`（會員自行設定）、`TicketTypeName`、座位標示皆為使用者可控字串，一律以 Vue 文字插值（`{{ }}`）或 Element Plus 表格 `prop` 渲染（皆自動跳脫），MUST NOT 使用 `v-html`／`innerHTML`。其中 `DisplayName` 由會員自行設定且會被任何 Organizer 看到，以前端測試固定此行為（tasks 6.4：含 HTML 標籤的名稱以純文字顯示、不產生元素）。
- 查詢呼叫沿用 `api/orders.ts`／`api/admin.ts` → `authorizedRequest`（統一注入 Authorization Header 並處理 401 換發）。

## Risks / Trade-offs

- [決策 2 讓單筆損毀訂單導致整個「我的訂單」列表 500] → 與既有 `ORD-DETAIL-004` 取捨一致；資料有 FK 且無刪除端點，實務上不應發生，發生時需要被發現而非被掩蓋。
- [後台列表的 `DisplayName` 可被會員自行修改，不具唯一性] → 本次僅作辨識輔助，明細頁仍有訂單 Id；需要精確辨識時另開提案。
- [本 change 的 delta 以 `order-pending-actions` 歸檔後的主 spec 為基準] → 若兩者順序顛倒，MODIFIED Requirement 會覆蓋掉對方的變更；實作前確認 `order-pending-actions` 已歸檔。

## Migration Plan

無資料庫 schema 變更、無資料遷移。DTO 只新增欄位，前後端同時部署；舊前端忽略新欄位不受影響。回滾即還原程式碼。
