# Design

## Context

- `GET /api/admin/events` 目前是「`IEventRepository.GetByOrganizerIdAsync` 整表 `ToList`（無排序）→ `GetAdminEventsHandler` 對全部活動批次查建立者名稱與座位 → Controller `Ok(list)`」。唯一呼叫端是此 Handler（測試替身另外轉發該方法）。
- 分頁慣例、共用型別（`PageRequest`／`PageRequestValidator`／`PagedResultDto<T>`，`Application/Common/Paging`）與前端換頁行為已由 `order-list-pagination` 建立並歸檔，見其 design.md 決策 1–3、7；本 change 只引用，不重述。
- `Event.CreatedAtUtc` 是 nullable（建立者／建立時間欄位上線前的舊活動為 null）；`Orders.CreatedAtUtc` 則否。Postgres 的 `ORDER BY ... DESC` 預設把 NULL 排最前面。
- 前端 `EventListPage.vue` 的 `events` 同時供列表表格與「建立票種」表單的活動下拉選單使用；建立票種成功後只刷新該活動的票種清單，不重載活動列表；建立活動成功後導回列表頁（重新掛載，從第 1 頁開始）。
- `loadtest/fixtures/purchase-flow-ux-event.js` 的 `countRetainedEvents` 以 `GET /api/admin/events` 取得陣列並依標題前綴計數。

## Goals / Non-Goals

**Goals:**
- 此端點的資料庫查詢量與回應大小有上界（`pageSize ≤ 100`）；座位與建立者資料只針對當頁活動查詢。
- 前端行為與兩個訂單列表頁一致，使用者不需重新學習。

**Non-Goals:**
- 不新增索引或 migration（見風險）。
- 不抽共用的前端分頁 composable、不重構兩個訂單列表頁（見風險）。
- 不改變「建立票種」表單的互動（活動下拉選單限制見決策 5）。
- 不處理公開 `GET /api/events` 及其快取。

## Decisions

### 決策 1：Repository 回傳 `EventPage`，移除舊的整表方法
- Domain 新增 `EventPage(IReadOnlyList<Event> Items, int TotalCount)`；`IEventRepository` 新增 `GetPageByOrganizerIdAsync(organizerId, skip, take, ct)` 並**移除** `GetByOrganizerIdAsync`（理由同 `order-list-pagination` 決策 4：留著會讓日後有人無意間又接回無上限查詢）。實作 `IEventRepository` 的測試 wrapper／fake 同步改為轉發新方法。
- 介面歸類：Repository 屬有業務語意的介面，放 Domain（CLAUDE.md 判準）。

### 決策 2：排序為「建立時間新到舊、null 最後、Id 升冪」
`OrderBy(e => e.CreatedAtUtc == null).ThenByDescending(e => e.CreatedAtUtc).ThenBy(e => e.Id)`。布林 `false < true`，所以有建立時間者在前、null 在後；在 PostgreSQL 上等同 `NULLS LAST`，不依賴資料庫的 NULL 預設排序。`Id` 升冪與兩個訂單列表一致。替代方案：把 null 視為最舊以 `COALESCE` 填入最小時間——不採用，會在 SQL 與 LINQ 間引入魔術值。實作時以 Testcontainers 整合測試確認 EF Core 實際翻譯出的排序（含 null 在最後）。

### 決策 3：兩段查詢（COUNT＋當頁），範圍過濾只寫一次
`Where(e => e.OrganizerId == organizerId)` 組成的範圍 query 同時餵給 `CountAsync` 與取頁，`totalCount` 因此不洩漏他人活動數。兩次查詢之間資料可能變動，`totalCount` 與 `items` 可能差一筆，列表情境可接受（理由同 `order-list-pagination` 決策 5，不為此開交易）。`AsNoTracking`。

### 決策 4：Handler 先驗證分頁，再只對當頁查附屬資料
`GetAdminEventsHandler.HandleAsync(organizerId, PageRequest, ct)` 回傳 `Result<PagedResultDto<AdminEventSummaryDto>>`：`IValidator<PageRequest>` 驗證失敗回 `Validation`（不查 Repository）→ `GetPageByOrganizerIdAsync` → 以當頁的 `memberIds`、`eventIds` 批次查建立者顯示名稱與座位（沿用既有的 `GetByEventIdsAsync` 與 `Members` 查詢）→ 組 DTO。當頁為空時兩個批次查詢都不會有 Id（`GetByEventIdsAsync` 對空集合直接回空），Members 查詢以 `memberIds` 為空時跳過（本次新增的行為；現行 Handler 無此跳過）。查詢次數：Events 2 次（COUNT＋當頁）、Members ≤ 1 次、EventSeats 1 次，不隨活動數成長。

### 決策 5（含已選活動清除規則）：票種表單的活動下拉選單只列當頁活動
下拉選單沿用 `events`，因此分頁後只列當頁。不為此另外載入「全部活動」（那會繞過分頁，違反 `list-pagination`「不得提供取得全部資料的途徑」）。這是使用者可見的限制：活動超過一頁時，較舊活動需先換到該頁才能從表單選到。接受理由：活動列表依建立時間新到舊排序，剛建立、最需要補票種的活動在第 1 頁；改為在活動列展開區新增票種可消除此限制，但會改動票種表單互動與既有 spec／測試，列為後續 change。換頁失敗時 `events` 保持前一頁，下拉選單與列表一致（AWU-EVENT-PAGE-007）。換頁成功後，若 `ticketTypeForm.eventId` 不在新的 `events` 中則清除（AWU-EVENT-PAGE-008）：否則 Element Plus 找不到對應 option 時會直接顯示 Id 字串，且送出會對畫面上看不到的活動建立票種。此行為由 Opus 規格審查指出，原設計未定義。

### 決策 6：前端換頁行為沿用訂單列表頁，`EventListPage` 自行持有一份
`getAdminEvents(page)` 回傳 `PagedResult<AdminEventSummary>`（沿用既有 `PagedResult<T>` 型別）；頁面持有 `currentPage`／`loadedPage`／`pageSize`／`totalCount` 與遞增請求序號，行為（過期回應丟棄、空頁且 `totalCount > 0` 重試一次最後一頁、失敗退回上一個成功頁）與 `order-list-pagination` 決策 7 相同。場館名稱查詢維持獨立、只在掛載時查一次，不受換頁影響。
- 替代方案：抽 `usePagedList` composable 讓三頁共用。不採用——會同時改動已上線並歸檔的兩個頁面，擴大審查範圍；此重複記錄於 proposal，另開 change。
- 建立票種成功後不重載活動列表（既有行為，票種不影響列表欄位），不新增此行為。

### 決策 7：負載測試 fixture 逐頁讀取
`countRetainedEvents` 改為 `page=1,2,…`、`pageSize=100` 逐頁讀取直到 `items` 為空或已讀滿 `totalCount`，累計標題前綴相符的筆數；回應不是含 `items` 陣列的物件時維持原本的 `fail(...)`。`page` 上限 10000 × 100 遠大於保留上限，不需另外處理。

### 決策 8：`api.generated.ts` 重新產生
後端完成後在 `web` 容器執行 `npm run generate:api-types`，檢查 diff 只包含此端點的 `page`／`pageSize` 查詢參數（與 `order-list-pagination` 決策 8 相同）。

## 安全確認（CLAUDE.md 強制問題）

- **輸入驗證**：`page`／`pageSize` 先經模型繫結（非整數、超出 int 範圍 → 400），再經既有 `PageRequestValidator`（Application 層，FluentValidation）；未拼接進 SQL 或 shell。
- **資料庫**：全程 EF Core LINQ（參數化），`Skip/Take` 為驗證後的整數；無 raw SQL。N+1：建立者名稱與座位皆以 `WHERE ... IN (...)` 單次批次查詢（只針對當頁 Id），由查詢次數測試驗證（頁內 1 筆 vs 多筆查詢次數相同）。
- **權限**：端點維持 `RequireOrganizerContext` Policy；`organizerId` 取自 JWT claim（`TryGetOrganizerId` 失敗時 `Forbid()` 的 fail-closed 分支不變），範圍條件在 Repository 查詢內，`totalCount` 與 `items` 共用同一條件；未授權者無法觸發（401／403 行為不變）。`pageSize ≤ 100`、`page ≤ 10000` 使端點不能被當成整表匯出。
- **前端**：活動欄位以 Vue 文字插值顯示，不使用 `v-html`；請求經既有 `authorizedRequest` 帶 Authorization Header，查詢字串只由整數頁碼組成。

## Risks / Trade-offs

- **[`Events` 表沒有 `(OrganizerId, CreatedAtUtc)` 索引，排序需掃描該 Organizer 的活動]** → 單一 Organizer 的活動數有限，工作集小；若實際資料量顯示需要再另案加索引與 migration。本 change 不碰 schema。
- **[破壞性回應變更]** → 前端與負載測試 fixture 同一個 change 切換；實作時以 `rg "admin/events" web loadtest scripts docs` 再核對一次沒有遺漏的呼叫端。
- **[`totalCount` 與 `items` 非同一快照]** → 見決策 3。
- **[三頁重複的 `loadPage` 邏輯]** → 見決策 6，記錄為技術債，後續 change 處理。
- **[票種表單下拉選單限制]** → 見決策 5。
- **[空頁重試（AWU-EVENT-PAGE-003）的觸發來源]** 活動列表頁沒有「重新整理」按鈕，Event 也無刪除功能，因此只有競態（例如另一分頁的操作改變總數、或停留頁在其他情境失效）才會觸發；與訂單列表不同。保留此分支是為了與 `list-pagination`「超過最後一頁回 200 空 items」的伺服器行為一致時，前端不顯示空白頁。

## Migration Plan

無資料庫變更。API 與 Web 必須同版本上線（回應結構不相容），本專案前後端同一個 compose 部署。回滾即還原整個 change。
