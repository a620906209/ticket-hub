## Context

- 後台訂單列表 `GET /api/admin/orders` 由 `GetOrdersHandler` 處理：`IOrderRepository.GetByOrganizerIdAsync`（已 `Include(o => o.Items)`、DB 端依 Organizer 過濾、未排序）＋ `IMemberDisplayNameReader` 批次取買家名稱；回傳 `OrderSummaryDto(Id, EventId, BuyerId, BuyerDisplayName, Status, HeldUntilUtc)`。
- 前端 `AdminOrderListPage.vue` 只有狀態、持有到期時間（非 Pending 時 `<span v-if>` 不渲染，欄位留白）、買家、操作四欄。
- `Order` 沒有建立時間欄位。`CreateOrderHandler` 是唯一建單處（`new Order(` 在 `src` 只出現一次），`HeldUntilUtc = now + HoldDuration`，`HoldDuration` 自 e35b8b0（第一版）起即為 `static readonly TimeSpan.FromMinutes(10)`，從未改過（`git log -S HoldDuration` 與 `git log -S FromMinutes` 對 `CreateOrderHandler.cs` 都只有 e35b8b0 一筆）。k6 腳本（`loadtest/*.js`）只透過 API 建單，不直接寫入 `Orders`。
- 買家端 `GetMyOrdersHandler` 已用 `IEventRepository.GetByIdsAsync` 批次取活動名稱，活動查不到時丟 `InvalidOperationException` 轉 500（order-display-enrichment design.md 決策 2）。

## Goals / Non-Goals

**Goals:**
- 列表 API 每筆訂單提供活動名稱、張數、總金額、建立時間，並依建立時間由新到舊排序。
- 列表頁顯示訂單編號短碼、活動、買家、張數、總金額、狀態、建立時間、持有到期時間（非 Pending 顯示「—」）。
- 查詢次數不隨訂單、活動或買家數量成長。

**Non-Goals:**
- 不改後台訂單詳情頁、買家端「我的訂單」與其 DTO。
- 不做分頁、篩選、搜尋、欄位點選排序。
- 不把 `ConfirmOrderHandler`、`TicketIssuedNotificationContentFactory` 既有的金額／張數加總改用新 Domain 方法（見決策 4）。

## Decisions

### 決策 1：建立時間存成 `Order.CreatedAtUtc`，不以 `HeldUntilUtc − 10 分鐘` 在查詢時推導

- 選擇：`Order` 新增 `public DateTime CreatedAtUtc { get; }`，公開建構子新增 `createdAtUtc` 參數（放在 `heldUntilUtc` 前），EF 物化用的 private 建構子同步新增；`CreateOrderHandler` 傳入同一個 `now`。
- 建構子驗證：`createdAtUtc` 為 `default` 時丟 `ArgumentException`（比照 `buyerId == Guid.Empty`）。不驗證 `heldUntilUtc > createdAtUtc`——那是 `CreateOrderHandler` 的持有時間規則，不是訂單本身的不變式，且會讓既有測試以任意時間建構訂單的寫法全部失效。
- 替代方案：查詢時推導。不需 migration，但把「持有時間永遠 10 分鐘」寫死成讀取端的假設，日後若持有時間改成可設定，所有歷史建立時間都會算錯。

### 決策 2：migration 精確回填既有訂單，欄位 NOT NULL

- 步驟：`AddColumn CreatedAtUtc`（nullable）→ `migrationBuilder.Sql("UPDATE \"Orders\" SET \"CreatedAtUtc\" = \"HeldUntilUtc\" - INTERVAL '10 minutes'")` → `AlterColumn` 為 NOT NULL。
- 回填值精確的依據見 Context（唯一建單處、持有時間從未改過）。因此前端不需要「建立時間未記錄」的 `—` 分支，與活動列表「本次功能上線前建立的活動顯示 —」不同。
- `migrationBuilder.Sql` 是 CLAUDE.md「禁止原生 SQL」的明確例外，比照 `AddTicketTypeRequiresSeat`、`AddEventOrganizerId` 的回填：EF Core migration API 沒有「以其他欄位運算更新」的操作；內容是固定字串、只在 migration 執行、不接受外部輸入。
- Down：直接 `DropColumn`。在持有時間固定 10 分鐘的前提下（決策 1），所有值都可由 `HeldUntilUtc` 重新推導，回滾後再 Up 得到相同結果，不需要 event-sales-window 那種中止檢查。
- 鎖表：Npgsql 以單一交易執行整個 migration，`ALTER TABLE` 取得的 ACCESS EXCLUSIVE 鎖會持續到回填結束，期間 `Orders` 讀寫都會被擋；部分失敗時整體回滾。目前資料量小（開發與 k6 測試資料等級），接受短暫鎖表，不拆批次回填。

### 決策 3：活動名稱以 `IEventRepository.GetByIdsAsync` 單次批次查詢，查不到大聲失敗

- 比照 `GetMyOrdersHandler`：取不重複 `EventId` 一次查回；任一訂單的活動查不到 → `InvalidOperationException`（訊息帶 OrderId／EventId，只進 log）→ 全域例外處理轉 500。`Orders.EventId` 有 FK 且系統沒有刪除活動的端點，查不到只代表資料損毀。
- 替代方案：在 `GetByOrganizerIdAsync` 內 join 出標題。可少一次查詢，但要改 repository 回傳型別（目前回傳 `Order` entity），而且會和買家端的做法分歧。
- 查詢次數：固定為 Orders 1 次、Members 1 次、Events 1 次，不隨筆數成長。

### 決策 4：張數、總金額由 Domain 方法計算

- `Order.GetTicketCount()` = Σ `Quantity`；`Order.GetTotalAmount()` = Σ `Quantity × UnitPrice`。這是單一 Entity 自己能判斷的規則，依 CLAUDE.md 放 Domain。
- `ConfirmOrderHandler:69` 與 `TicketIssuedNotificationContentFactory:30` 已各自寫了相同加總。本 change 不改這兩處（Rule 3 外科式修改），在 Task 完成回報中標為重複邏輯，留給後續清理。

### 決策 5：排序依 `CreatedAtUtc` 由新到舊，相同時再依 `Id` 升冪

- 使用者在提案階段選「依持有到期時間新到舊」。因為 `HeldUntilUtc = CreatedAtUtc + 固定 10 分鐘`，兩者排序結果完全相同；改用 `CreatedAtUtc` 是因為「新到舊」本來就是指建立時間，日後持有時間若改變也不會讓順序錯亂。
- 在 handler 記憶體內排序：資料本來就全部載入（無分頁），不需要改 repository。`Id`（升冪）為次要鍵，確保同一時間建立的訂單順序固定。

### 決策 6：訂單編號短碼由前端截取

- 前端顯示 `id.slice(0, 8)`，比照 `MyOrganizersPage.vue`；後端不新增欄位。「查看明細」連結仍使用完整 Id。短碼只用於辨識，不保證唯一，所以不提供以短碼查詢的功能。

### 決策 7：金額與時間格式沿用既有工具

- 總金額用 `utils/currency.ts` 的 `formatCurrency`（NT$）；建立時間、持有到期時間沿用頁面既有的 `new Date(...).toLocaleString()`。
- 持有到期時間：`status === 'Pending'` 顯示時間，否則顯示「—」。Pending 但已過 `HeldUntilUtc` 的訂單後端會回 `Expired`（`GetStatus(now)`），所以也會顯示「—」。

## Risks / Trade-offs

- [`Order` 建構子多一個參數，約 28 處呼叫端要改] → 都是機械式補參數；以全量 build 與測試確認沒有遺漏。
- [測試中有 raw SQL `INSERT INTO "Orders"` 沒有 `CreatedAtUtc`，NOT NULL 後會失敗：`ForeignKeyConstraintsTests`（136、149、164 行）、`GetOrderByIdLegacyDataTests`（37 行）、`GetPaidItemSalesByEventIdAsyncTests`（114 行），共 3 檔 5 處] → tasks 3.3 明列，並以 grep 確認沒有遺漏。
- [列表欄位變多，窄螢幕會出現水平捲動] → `el-table` 本身就支援水平捲動；後台列表不在 R1 窄螢幕不溢出的驗收範圍內（那條 Requirement 只管導覽列）。
- [新版程式在 migration 前啟動會因欄位不存在而失敗] → 依既有流程，先 migrate 再部署新版（event-sales-window 的經驗）。
- [反方向：migrate 完成後、新版上線前，仍在跑的舊版 `CreateOrderHandler` INSERT 不含 `CreatedAtUtc`，因 NOT NULL 且無 DB default 而失敗；`Program.cs` 沒有自動 migrate，兩步之間必有空窗] → Migration Plan 明訂先停 api，再以一次性容器 migrate，最後啟動新版；`api` 是唯一寫入 `Orders` 的程序（`web` 只經 Vite proxy 轉送到 `api:8080`），停止後建單請求一律失敗（502），不會部分成功。單人開發、無正式流量，不採「先 nullable、下一個 change 再收緊」的兩階段做法。
- [`api` 容器的 `ENTRYPOINT` 是 `dotnet watch ... run`，停止後無法 `exec`；`docker compose run --rm api dotnet ef ...` 只會把參數接在 `dotnet watch` 後面，不會執行 ef] → 一次性容器須加 `--entrypoint dotnet`（已實測 `docker compose run --rm --no-deps --entrypoint dotnet api ef --version` 輸出 10.0.12）。`run` 預設不發布 service port，所以一次性容器不會接到外部流量。
- [Npgsql 寫入 `timestamp with time zone` 時，`DateTime.Kind` 不是 Utc 會丟例外；`createdAtUtc == default` 的驗證擋不住 Kind 錯誤] → 測試補參數時一律用 Utc（`DateTime.UtcNow` 或 `DateTimeKind.Utc`），見 tasks 1.3。回填 SQL 是 timestamptz 的絕對時間運算，沒有時區問題。

## 安全確認

- 外部輸入：沒有新增端點或輸入參數，只擴充既有 `GET /api/admin/orders` 的回應欄位。
- 資料庫：全部經 EF Core 查詢；回填 SQL 為 migration 內固定字串（決策 2）。N+1：固定 Orders、Members、Events 各 1 次（決策 3，ORD-LIST-004 驗證）。
- 權限：沿用既有「已切換至 Approved Organizer」授權與 DB 端 Organizer 過濾，不新增權限規則；新增欄位（活動名稱、金額、張數、建立時間）都屬於該 Organizer 自己的資料，不含買家個資。
- 前端：新欄位以 Vue 文字插值渲染，不使用 `v-html`；API 呼叫沿用既有攔截器帶 Auth Header。

## Migration Plan

`SQL "<query>"` 代表 `docker compose exec db sh -c 'psql -U "$POSTGRES_USER" -d "$POSTGRES_DB" -c "<query>"'`（單引號讓變數在 db 容器內展開，host shell 不一定有 `.env` 的變數）。前一版 migration 為 `20261003115627_AddEventSalesWindow`。一次性 SDK 容器的完整指令如下（PR 與操作紀錄一律寫完整指令，不縮寫）：

- 套用：`docker compose run --rm --no-deps --entrypoint dotnet api ef database update --project src/ProjectC.Infrastructure --startup-project src/ProjectC.WebApi`
- 回滾：`docker compose run --rm --no-deps --entrypoint dotnet api ef database update 20261003115627_AddEventSalesWindow --project src/ProjectC.Infrastructure --startup-project src/ProjectC.WebApi`

**產生 migration（開發時）**：`docker compose exec api dotnet ef migrations add AddOrderCreatedAt`，手動在中間插入回填 SQL，並把欄位改成先 nullable、回填後 NOT NULL。

**套用（runbook，tasks 3.5 以開發 DB 演練）**：
1. 只讀盤點：`SQL` 查詢 `__EFMigrationsHistory` 最新一筆（應為 `AddEventSalesWindow`）、`Orders` 筆數、`HeldUntilUtc` 的 min／max（`HeldUntilUtc` 本身是 NOT NULL，盤點是為了確認沒有明顯異常值，例如早於系統上線日）。
2. 確認沒有其他寫入者：`docker compose ps -a` 中只有 `api` 會寫入 `Orders`，沒有 loadtest／seeder 容器在跑；沒有其他 API replica（本專案 compose 只有一個 `api`，無 CI seeder 或外部 worker）；pgAdmin 等外部 client 先斷線。
3. 停止寫入：`docker compose stop api`。`api` 內的背景服務（排隊入場推進等）也一併停止。驗證：經 `web` 或直連 8080 呼叫任一 API 都失敗；`Orders` 筆數與步驟 1 相同；`SQL "SELECT pid, usename, application_name, client_addr, state FROM pg_stat_activity WHERE datname = current_database() AND pid <> pg_backend_pid()"` 沒有任何其他連線。
4. 備份：`docker compose exec db sh -c 'pg_dump -U "$POSTGRES_USER" -d "$POSTGRES_DB" -t "\"Orders\""' > orders-backup.sql` 輸出到本機檔案（回滾失敗時的最後手段，不進版控）。
5. 套用：在上面「套用」指令前加 `time`，記錄耗時（即鎖表時間的上限）。
6. 驗證 schema 與回填：`__EFMigrationsHistory` 最新一筆為 `AddOrderCreatedAt`；`information_schema.columns` 中 `CreatedAtUtc` 為 `timestamp with time zone`、`is_nullable = 'NO'`；`CreatedAtUtc IS NULL` 筆數為 0；`CreatedAtUtc <> "HeldUntilUtc" - INTERVAL '10 minutes'` 筆數為 0；`Orders` 筆數與步驟 1 相同。任一項不符就停在這裡，不啟動新版，改走回滾。
7. 啟動新版：`docker compose start api`，等 `GET /api/events` 回 200。
8. Smoke test：買家建立一筆訂單回 201；DB 中該筆 `HeldUntilUtc - CreatedAtUtc = 10 分鐘`，且 `CreatedAtUtc` 落在呼叫前後的時間內；主辦方 `GET /api/admin/orders` 回 200，第一筆就是這筆新訂單，`createdAtUtc` 以 `Z` 結尾（UTC）。
9. 恢復服務：`web` 不需另外處理，api 啟動後 proxy 即恢復。

**回滾**：
- 前提：只在 `api` 停止時執行（步驟 3 的方式）。新版已寫入訂單也可以回滾，因為 `Down` 只會丟掉 `CreatedAtUtc`，而這個值在持有時間固定 10 分鐘時可由 `HeldUntilUtc` 精確重新推導，回滾後再 Up 會得到相同的值；其他欄位不受影響。前提失效的情況：持有時間已改為可變，此時必須先備份 `Id, CreatedAtUtc`，否則不得回滾。
- 步驟：停止 api → 以**新版**原始碼（只有新版含這個 migration 的 `Down`）執行上面的「回滾」指令 → 驗證 `__EFMigrationsHistory` 不含 `AddOrderCreatedAt`、`information_schema.columns` 不再有 `CreatedAtUtc`、`Orders` 筆數不變 → api 仍停止時才 `git checkout` 舊版（api 執行中切換會讓 `dotnet watch` 混用新舊原始碼，見 event-sales-window tasks 6.9）→ `docker compose start api` → 以舊版建立一筆訂單回 201。
- 回滾後重新上線：照「套用」步驟重跑。新版寫過的訂單與舊版回滾期間建立的訂單，回填值都必須等於 `HeldUntilUtc − 10 分鐘`；新版寫過的訂單值也必須與回滾前記錄的值相同。

## Open Questions

（無）
