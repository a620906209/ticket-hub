# Design

## Context

- 兩個目標端點目前都是「Repository 整表 `ToList` → Handler 在記憶體組 DTO（含批次查活動名稱／買家名稱）→ Controller `Ok(list)`」。後台列表的排序（`CreatedAtUtc` 新到舊、`Id` 升冪）在 Handler 記憶體內做；買家列表沒有排序。
- `IOrderRepository` 屬 Domain，其實作在 Infrastructure（見 CLAUDE.md 架構強制規則）；`Order` 不屬於 `IApplicationDbContext` 範圍，Handler 必須透過 Repository 取資料。
- Application 層已有 FluentValidation 慣例（`*RequestValidator`＋`ToValidationError()`）與 `Result<T>`；WebApi 對 400 `Validation` 輸出逐欄位 `errors`（`api-conventions.md` §2）。模型繫結失敗（例如 `page=abc`）由 `InvalidModelStateResponseFactory` 以同格式輸出。
- `Orders` 表目前沒有 `BuyerId`／`CreatedAtUtc` 相關索引（`OrderConfiguration` 無 `HasIndex`）。
- 前端 Element Plus 以 `unplugin-vue-components` 按需引入，`el-pagination` 由 resolver 自動解析。

## Goals / Non-Goals

**Goals:**
- 兩個訂單端點的資料庫查詢量與回應大小有上界（`pageSize ≤ 100`）。
- 分頁慣例可被 `/api/admin/events` 等後續端點原樣重用（共用型別與驗證放 Application/Common）。

**Non-Goals:**
- 不新增資料庫索引或 migration（見風險）。
- 不重構 Handler 既有的「查不到買家／活動就 500」邏輯，只把檢查範圍縮到當頁。
- 不處理 `/api/admin/events`、`/api/events`、其他列表端點。

## Decisions

### 決策 1：offset 分頁，`page` 從 1 起，不用 cursor
訂單列表是後台／會員頁翻頁，需要「共 N 筆」與跳頁，cursor 無法提供。代價是深頁 `OFFSET` 成本隨頁碼成長，由 `pageSize ≤ 100` 與資料量（單一會員或單一 Organizer 的訂單）限制；超出此假設時再另案評估。

### 決策 2：越界回 400，超過最後一頁回 200＋空 `items`
靜默截斷 `pageSize` 會讓呼叫端以為拿到全部資料，故越界一律 400。頁碼超過最後一頁不是錯誤（例如帶入過期或手動輸入的頁碼），回空 `items` 並帶實際 `totalCount`，前端靠 `totalCount` 回到最後一頁。

### 決策 3：驗證放 Application，參數綁定放 Controller
- Controller 以 `[FromQuery] int page = 1, [FromQuery] int pageSize = 20` 綁定（預設值引用 Application 常數 `PageRequest.DefaultPage`／`DefaultPageSize`，避免兩處寫死）。非整數由模型繫結失敗路徑回 400（key 為 `page`／`pageSize`）。
- Application 新增 `PageRequest(int Page, int PageSize)` 與 `PageRequestValidator`（`1 ≤ Page ≤ 10000`、`1 ≤ PageSize ≤ 100`，兩欄位同時違規時一次回報兩個欄位），Handler 以 `ToValidationError()` 轉為 `Result.Failure`。上限 100 為 `PageRequest.MaxPageSize`、`MaxPage` 為 10000，後續端點共用。`Page ≤ 10000` 且 `PageSize ≤ 100` 時 `(Page-1)*PageSize ≤ 999,900`，遠小於 `int.MaxValue`，`Skip` 計算不會溢位；這同時限制深頁 OFFSET 成本。
- 替代方案：在 Controller 以 `[Range]` 屬性驗證。不採用——本專案請求驗證一律在 Application 層（FluentValidation），Handler 也可能被 Controller 以外的呼叫端使用。

### 決策 4：回應型別與 Repository 回傳型別
- Application/Common 新增 `PagedResultDto<T>(IReadOnlyList<T> Items, int Page, int PageSize, int TotalCount)`；JSON 為 camelCase `items/page/pageSize/totalCount`。
- Domain 新增 `OrderPage(IReadOnlyList<Order> Items, int TotalCount)`（Repository 回傳型別，Domain 不依賴 Application）。
- `IOrderRepository` 新增 `GetPageByBuyerIdAsync(buyerId, skip, take, ct)` 與 `GetPageByOrganizerIdAsync(organizerId, skip, take, ct)`，並**移除**舊的 `GetByBuyerIdAsync`／`GetByOrganizerIdAsync`（兩者唯一正式程式碼呼叫端就是這兩個 Handler；留著會讓日後有人無意間又接回無上限查詢）。測試替身（`FakeOrderRepository`、`OrganizerScopingFaultInjectionWebApplicationFactory`、`OrganizerScopingFaultInjectionWebApplicationFactory` 內的 `UnresolvableOrganizerOrderRepository`）與 `OrderRepositoryOrganizerScopingTests` 同步改用新方法。
- 介面歸類：Repository 屬有業務語意的介面，放 Domain（CLAUDE.md 判準）。

### 決策 5：兩段查詢（COUNT＋當頁），排序與切頁在資料庫端
Repository 先 `CountAsync` 再以 `OrderByDescending(CreatedAtUtc).ThenBy(Id).Skip(skip).Take(take).Include(Items)` 取當頁。兩次查詢之間資料可能變動，`totalCount` 與 `items` 可能差一筆，列表情境可接受（不為此開交易）。後台列表保留既有的 `_dbContext.Events.Any(e => e.Id == o.EventId && e.OrganizerId == organizerId)` 範圍過濾，count 與 page 兩個查詢使用同一個過濾條件。Handler 拿到當頁後，沿用既有的批次查詢取活動名稱與買家名稱（只針對當頁的 Id），並以當頁資料做「查不到就 500」檢查。`Include(Items)` 搭配 `Skip/Take`：EF Core 會對訂單主表先切頁再 join 明細，不產生 N+1；實作時以查詢計數測試（`OrderDisplayQueryCountTests`）確認每張表查詢次數不隨筆數成長。

### 決策 6：買家列表排序比照後台
`CreatedAtUtc` 新到舊、`Id` 升冪。`Id` 升冪而非降冪，是為了與後台列表既有排序（`order-administration` ORD-LIST-008）一致，兩者共用同一種排序語意。

### 決策 7：前端換頁行為
- API 層 `getMyOrders(page)`、`getAdminOrders(page)` 回傳 `PagedResult<T>`；`pageSize` 不由前端傳（用後端預設 20），前端以回應的 `pageSize` 供 `el-pagination`。
- 頁面持有 `currentPage`（元件內 `ref`，不寫入網址）。`el-pagination` 以 `layout="total, prev, pager, next"` 呈現，只在 `totalCount > pageSize` 時顯示。
- 任何一次載入若回應 `items` 為空且 `totalCount > 0`，只重試一次，改請求最後一頁 `ceil(totalCount / pageSize)`，並同步分頁元件的頁碼；目前專案沒有刪除訂單的路徑，這是防禦行為，以 Vitest mock 驗證。
- 換頁請求失敗（500、429、斷網）時，頁碼退回上一個成功載入的頁，列表保留該頁資料並顯示錯誤提示，避免分頁元件與表格內容不一致。
- 快速連續換頁時以遞增的請求序號丟棄過期回應，避免舊回應覆蓋新頁面。

### 決策 8：`api.generated.ts` 重新產生
`web/src/types/api.generated.ts` 由 `openapi-typescript` 從執行中的 API 產生（`npm run generate:api-types`）。後端完成後在 `web` 容器重新產生，並檢查 diff 只包含兩個端點的查詢參數與回應型別。

## 安全確認（CLAUDE.md 強制問題）

- **輸入驗證**：`page`／`pageSize` 先經模型繫結（非整數、超出 int 範圍 → 400），再經 `PageRequestValidator`（Application 層，FluentValidation）驗範圍；未拼接進 SQL 或 shell。
- **資料庫**：全程 EF Core LINQ（參數化），`Skip/Take` 值為驗證後的整數；無 raw SQL。N+1：活動名稱與買家名稱以 `WHERE Id IN (...)` 單次批次查詢，Orders 為 COUNT＋當頁各一次，由 `OrderDisplayQueryCountTests` 驗證；`Include(Items)` 搭配 `Skip/Take` 由 EF 對主表先切頁。
- **權限**：買家端點為 `[Authorize]`，範圍條件為 `BuyerId == 呼叫者 MemberId`（取自 JWT claim，非請求參數）；後台端點為 `RequireOrganizerContext` Policy，範圍條件為 `Event.OrganizerId == claim 的 OrganizerId`，皆在 Repository 查詢條件內，`totalCount` 與 `items` 使用同一條件，因此不會透過總筆數洩漏他人訂單數。未授權者無法觸發（401／403 行為不變）。`pageSize ≤ 100` 與 `page ≤ 10000` 使端點不能被當作整表匯出。
- **前端**：訂單欄位以 Vue 文字插值顯示，不使用 `v-html`；分頁請求經既有 `authorizedRequest`（`httpClient.ts`）帶 Authorization Header，頁碼為元件內的整數，查詢字串只由該整數組成。

## Risks / Trade-offs

- **[`Orders` 無相關索引，深頁或大量訂單時 `ORDER BY CreatedAtUtc DESC` 需排序]** → 目前資料量下可接受，且 `pageSize` 上限與買家／Organizer 範圍已限制工作集；若負載測試或實際資料量顯示需要，另開 change 加 `(BuyerId, CreatedAtUtc)` 索引與 migration。本 change 不碰 schema。
- **[破壞性回應變更]** → 前後端同 change 切換；已確認負載測試腳本（`loadtest/`）不呼叫這兩個 GET 列表端點；實作時以 `rg` 再核對一次其他呼叫端。
- **[`totalCount` 與 `items` 非同一快照]** → 見決策 5，列表情境可接受。
- **[資料不一致只在被請求的頁面才會 500]** → 比整表載入時「任何一筆壞資料都讓整個列表失敗」更寬鬆；這是分頁的自然結果，已寫入 spec（ORD-PAGE-004、BOQ-LIST-TITLE-002）。

## Migration Plan

無資料庫變更。部署時 API 與 Web 必須同版本上線（回應結構不相容）；本專案前後端同一個 compose 部署，無需相容期。回滾即還原整個 change。
