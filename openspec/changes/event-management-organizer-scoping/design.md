## Context

`event-management` 能力的所有後台管理端點（建立 Venue、SeatMap、Event、TicketType，以及 Admin 專用活動列表查詢）都是「角色為 Admin 即可呼叫」，沒有「這個活動屬於哪個主辦方」的邊界。`Event.CreatedByMemberId` 只記錄操作者，不是租戶歸屬。

`organizer-management` 變更已交付 `Organizer` 實體、申請／審核流程、`Member`-`Organizer` 多對多關聯，以及「目前操作中主辦方」的切換機制（含 `RequireOrganizerContext` Policy 的定義）。本次是**第一個實際套用**這個機制的變更：把 `event-management` 既有後台管理端點的授權規則從「Admin 角色」改為「已切換至一個 Approved Organizer」。

## Goals / Non-Goals

**Goals:**
- 後台活動／票種管理端點權限邊界從「全域 Admin」收斂為「該 Organizer 的成員」
- 既有 Event 資料在遷移後仍可正常查詢與管理，不遺失資料
- 既有全域 Admin 帳號在部署後仍能操作後台（透過回填自動成為轉入用 Organizer 的成員）

**Non-Goals:**
- 不改變 `Venue`／`SeatMap` 的歸屬模型，維持平台共用資源，不歸屬特定 Organizer
- 不處理 `order-administration`／`ticket-redemption`／`sales-report`／`purchase-queue` 的授權收斂——分別是後續變更 `order-report-redemption-organizer-scoping`、`purchase-queue-organizer-scoping` 的範圍
- 不為 `Venue`／`SeatMap` 建立配額或流量限制（任何已切換至 Approved Organizer 的成員皆可無限制建立）；這是延續既有「平台共用資源」模型的刻意簡化（YAGNI），資源濫用風險目前視為可接受，若平台治理需求變化（例如需要限制單一 Organizer 的建立量），留給後續另開變更評估

## Decisions

### 1. 既有全域 `Admin` 角色語意收斂為平台治理（本次範圍：event-management）
`Admin` 角色在活動管理這個範圍內，從「可直接建立/管理所有活動」改為「僅能審核 Organizer 申請」（由 `organizer-management` 變更提供）。**BREAKING**：既有 `event-management`（含建立票種）後台端點不再接受單純的 Admin 角色呼叫，Admin 帳號要操作這些端點，必須先加入某個 Organizer 並切換過去（與一般 Member 相同流程）。**範圍澄清**：本次「收斂」僅涵蓋 `event-management` 端點；既有 `AdminMembersController`（啟用／停用會員帳號）屬於純平台層級操作、與任何 Organizer 或活動無關，不受本次影響，仍維持既有的純 `MemberRole.Admin` 授權，不在本次變更範圍內。

**替代方案**：讓 Admin 角色維持「萬用鑰匙」，可以繞過 Organizer 邊界直接管理所有活動。
**選擇收斂的理由**：維持萬用鑰匙會讓多租戶邊界形同虛設，不符合本次「落實多租戶隔離」的目的。

### 2. `Venue`／`SeatMap` 不歸屬 Organizer
現實中場地本就可能被多個主辦方輪流租用，維持平台共用資源池，只有 `Event`／`TicketType` 歸屬特定 Organizer。

**理由**：避免不必要的資料模型擴張（Rule 2 Simplicity First）；也避免既有 `Venue`/`SeatMap` 相關 spec、測試需要大改。

### 3. 通盤盤點 `event-management` 底下所有以既有 `EventId` 為輸入的端點，補上 `CreateTicketTypeHandler` 的歸屬核對
**現況核對與缺口**：`AdminEventsController` 底下 `CreateTicketType`（`CreateTicketTypeHandler`）是以既有 `eventId` 為輸入、對既有活動執行**寫入**操作的端點。核對原始碼確認：只檢查 `@event is null`（活動是否存在），完全沒有核對該活動的 `OrganizerId` 是否等於呼叫端目前 Organizer。若只把授權屬性從 `AdminOnly` 改為 `RequireOrganizerContext`，卻不補上這個核對，會造成新的 IDOR（Insecure Direct Object Reference）缺口：任何已核准的 Organizer 成員都能透過猜測或枚舉 `eventId`，對其他 Organizer 名下的既有活動新增票種——這是本次授權模型收斂**主動引入**的風險（原本純 Admin-only 時，因為只有一種全域角色、不存在「屬於別人」這個概念，自然不會有這個問題；改成多租戶後，任何操作既有租戶資料的端點都需要重新檢視）。

**修正後設計**：`CreateTicketTypeHandler` 在既有的 `@event is null` 檢查之後，新增「`@event.OrganizerId` 是否等於呼叫端目前 `OrganizerId`」的核對，不一致時回傳與「活動不存在」相同的 `Error.NotFound`，不建立任何票種。

**通盤複查結論**：`event-management` 底下另外兩個以既有資源 Id 為輸入的端點——`AdminVenuesController`（Venue／SeatMap）依 Decision 2 本就是平台共用資源、不歸屬特定 Organizer，不需要個別資源的歸屬核對；後台專用活動列表查詢本身就是依 `OrganizerId` 過濾產生結果，不是以既有 `eventId` 為輸入的單筆操作，不適用同一種核對模式。`order-administration`／`ticket-redemption`／`sales-report`／`purchase-queue` 底下同類的既有 `eventId`／訂單／票券輸入端點，留給後續依賴本次的變更（`order-report-redemption-organizer-scoping`、`purchase-queue-organizer-scoping`）逐一核對，不在本次範圍。

### 4. `RequireOrganizerContext` Policy 驗證 claim 格式但不即時查表——停權的實際生效時機
`organizer-management` 變更已定義 `RequireOrganizerContext` Policy，本次是第一個實際套用它的變更。這個 Policy 驗證 Access Token 是否帶有格式合法（可解析為非 `Guid.Empty` 的 `Guid`）的 `OrganizerId` claim（見「安全確認-輸入驗證」），但**不**對每次請求即時查詢該 Organizer 目前在資料庫中的狀態（比照既有 `[Authorize(Roles = "Admin")]` 的執行層級：Role 變更後，舊 Access Token 在過期前仍持舊 Role，是既有的、可接受的延遲曝險）——這是兩件獨立的事：claim 格式驗證是「這個 claim 本身有沒有壞掉」，不即時查表是「這個 claim 所指的 Organizer 現在還有沒有效」，本節只討論後者。

**具體後果**：`organizer-management` 能力的「停權」操作（見該能力 spec 的「平台管理員可以停權已核准的 Organizer」需求）會立即阻擋該 Organizer 成員之後的「切換」與「換發」，但**停權前已核發、尚未過期的 Access Token**，在其自然到期（`AccessTokenExpirationMinutes`）前仍可能通過本次新增的 `RequireOrganizerContext` Policy、繼續呼叫 `event-management` 的後台端點。這不是本次新引入的漏洞，而是與既有 Role 變更延遲曝險同等級的既知取捨（Rule 2 Simplicity First：不透過每次請求即時查表的方式消除，以維持 Claim 換發模式的效能與架構一致性）；「完全無法透過後台存取或管理資料」的實際生效時間點，因此是「該 Organizer 全部既有 Access Token 過期或被拒絕換發」之後，而非停權指令執行的當下。見 `EVT-AUTHZ-004` Scenario。

**運維補充（供部署／運維文件參考，非本次自動化測試範圍）**：正式環境延遲曝險上限即當下設定的 `AccessTokenExpirationMinutes`（見 `organizer-management` 能力 design.md Context，現行預設 30 分鐘）。呼叫「停權」端點後，該 Organizer 成員之後的換發（依 `organizer-management` 能力 `ORG-REFRESH-003`）與切換皆會立即被拒絕、不再帶出該 `OrganizerId` claim，操作人員**不需要**額外撤銷 Refresh Token 才能阻止這點。唯一無法提前失效的是**停權當下已核發、尚未過期的 Access Token 本身**——系統目前沒有機制可以主動撤銷單一已核發的 Access Token，只能等其自然到期；操作人員不可假設「停權」會立即切斷該 Organizer 全部既有請求。

## 安全確認（CLAUDE.md 安全強制規則）

**輸入驗證**
- 建立活動／票種的既有欄位驗證規則不變；新增的 `OrganizerId` 核對邏輯不接受前端輸入，一律取自 Access Token claim 或既有資料庫欄位
- `RequireOrganizerContext` Policy 本身 MUST 驗證 claim 值可解析為合法、非 `Guid.Empty` 的 `Guid`（不只檢查存在性），驗證失敗一律 fail-closed 回傳 403；此驗證邏輯與本次各端點在 Controller 層讀取該 claim 時，統一呼叫 `organizer-management` 能力已定義的共用擴充方法 `TryGetOrganizerId`（見該能力 spec「`RequireOrganizerContext` Authorization Policy MUST 驗證 claim 格式並 fail-closed」需求），不由本次各端點各自 `Guid.Parse`

**資料庫**
- 全數透過 EF Core 參數化查詢
- N+1 查詢風險評估：後台專用活動列表查詢新增的 `OrganizerId` 過濾條件是既有查詢的 `WHERE` 子句延伸，不逐筆額外查詢；`CreateTicketTypeHandler` 的歸屬核對只是在既有的單筆 Event 查詢後，多讀一次已載入物件的既有欄位（`Event.OrganizerId`），不構成 N+1

**權限**
- 「已切換至一個 Approved Organizer」的檢查在 WebApi 層以 `RequireOrganizerContext` Authorization Policy 執行，比照既有 `[Authorize(Roles = "Admin")]` 的執行層級，不下放到 Handler 內個別判斷
- 「這個活動是否屬於呼叫端目前 Organizer」需要查詢資料庫才能判斷，在 Application 層的 Handler 內執行

**前端（Vue/React）**
- 路由守衛調整不涉及使用者輸入渲染，沿用既有前端統一攔截器帶入 Auth Header 的機制

**機敏資訊管理**
- 本次無新增機敏設定

## Migration Plan

1. 新增 EF Core migration：`Events` 新增 `OrganizerId`（先允許為 null 以利資料回填，回填後在同一支或後續 migration 內加上 `NOT NULL` 約束與外鍵）
2. 資料回填腳本（以 EF Core migration 的 `Up()` 方法內的原生 SQL 實作，隨步驟 1 同一支或緊接的下一支 migration 執行；依賴 `organizer-management` 變更已存在的 `Organizer`／`OrganizerMember` 資料表）：

   **單次執行的可靠性來源**：本專案的 migration 一律透過 `docker compose exec api dotnet ef database update` 執行，由 EF Core 內建的 `__EFMigrationsHistory` 資料表保證同一支 migration 不會被同一次 `database update` 重複套用；本專案目前是單一 instance、無真實併發部署流程（同上「部署窗口風險」的既有前提），因此**不假設**兩個部署程序同時對同一個資料庫執行本 migration。以下三個步驟改用資料庫層級的衝突處理（`ON CONFLICT`／`WHERE` 條件），是防禦性作法（涵蓋「migration 因故中途失敗、人工重跑」這類單一執行緒重試的情境），不是為了支援真正併發部署而設計，即使真的意外併發執行，這三步驟本身在 Postgres 層級也不會產生重複資料或損毀狀態：

   - **轉入用 Organizer 識別方式**：使用寫死在 migration 程式碼中的**固定 GUID**（例如 `11111111-1111-1111-1111-111111111111`，僅供本次遷移內部識別使用，與一般 `Organizer.Id` 的 `Guid.NewGuid()`生成方式無關，不會與自助申請產生的 Organizer 碰撞），**不依賴 `Name` 比對**（`organizer-management` spec 明確不保證 `Organizer.Name` 全系統唯一，若真的已有使用者自助申請了一個同樣命名為「既有資料轉入」的 Organizer，僅依 `Name` 查詢會誤判為同一筆）。以 `INSERT ... ON CONFLICT (Id) DO NOTHING` 建立這筆固定 Id、`Name = "既有資料轉入"`、`Status = Approved` 的 Organizer，重跑時該筆已存在即略過，不產生第二筆、也不依賴應用層的「先查詢再建立」（避免查詢與寫入之間的競態窗口）
   - 找出**所有**既有 `MemberRole.Admin` 角色的 Member（不僅限於曾建立過活動者），各自以 `INSERT ... ON CONFLICT (OrganizerId, MemberId) DO NOTHING` 建立一筆 `OrganizerMember`（`Role = Owner`）掛在上述固定 Id 的轉入用 Organizer 下——涵蓋範圍刻意大於「曾建立過活動的 `CreatedByMemberId`」，避免從未建立過活動、但仍需要操作後台的既有 Admin 帳號在部署後被鎖在後台之外；此 `ON CONFLICT` 依賴 `organizer-management` 變更已在 `OrganizerMemberConfiguration` 建立的 `(OrganizerId, MemberId)` 複合唯一索引，由資料庫保證重跑或（意外）併發執行皆不會重複插入
   - `UPDATE Events SET OrganizerId = <固定 Id> WHERE OrganizerId IS NULL`：`WHERE OrganizerId IS NULL` 條件本身保證此更新對已回填過的既有列是不可變的 no-op，重跑或併發執行皆安全，不需要額外的判斷邏輯

   **部分失敗後重試的一致性**：上述三步驟各自獨立冪等（`Organizer` 依固定 Id 衝突忽略、`OrganizerMember` 依複合唯一索引衝突忽略、`Event` 依 `IS NULL` 條件天然冪等），若腳本執行到一半失敗，重新執行整支回填腳本會自動略過已完成的部分、只補完尚未完成的部分，不需要額外的斷點續傳邏輯或人工判斷「上次跑到哪」
3. 部署順序：先跑 migration（含回填）→ 再部署新版後端（此時後台端點授權規則已改變）。因為回填涵蓋「所有既有 Admin」而非僅事件建立者，正式環境部署後既有 Admin 帳號皆已是轉入用 Organizer 的成員，僅需各自呼叫一次切換操作情境即可恢復操作，不需要額外的手動介入步驟。**部署程序 MUST 維持單一 migration runner**（由單一 API container 執行 `dotnet ef database update`）——本節上述的資料庫層冪等防護是應對「單一執行緒中途失敗後重跑」的防禦性設計，不是為了支援兩個部署程序同時對同一個資料庫執行 `database update`；部署流程不得讓多個 API instance 同時觸發 migration
4. **部署窗口風險**：`Events.OrganizerId` 分兩階段套用（先 nullable 回填，後續 migration 才加 `NOT NULL`）——若回填（步驟 2）完成後、新版後端尚未部署上線這段窗口內，舊版後端仍持續受理 `CreateEvent` 請求，會建立 `OrganizerId` 為 null 的新 Event；等到後續加 `NOT NULL` 約束的 migration 執行時，若存在這類窗口期殘留的 null 列，該次 migration 會失敗。本專案目前是單一 instance、無真實併發流量部署，此風險視為可接受的低機率情境。
   **檢查責任 MUST 內建於 migration 本身，不得只是仰賴部署人員手動查詢、也不得只依賴 Postgres 因 NULL 值而讓 `ALTER TABLE ... SET NOT NULL` 底層碰巧失敗**（那樣的錯誤訊息不可辨識、也無法保證在加約束前就先擋下——PostgreSQL 對已存在的欄位加 `NOT NULL` 約束時確實會全表掃描檢查，但這是資料庫的內部行為，不是本次設計要求的明確 preflight）。加 `NOT NULL` 約束的這支 migration（步驟見 3.3）的 `Up()` 方法 MUST 在執行 `ALTER TABLE` 之前，以明確、可辨識的檢查主動偵測殘留 null 列，偵測到時 MUST 主動中止（例如 Postgres 原生 SQL 的 `DO $$ BEGIN IF EXISTS (SELECT 1 FROM "Events" WHERE "OrganizerId" IS NULL) THEN RAISE EXCEPTION '...'; END IF; END $$;`，或等效的、訊息可辨識「這是 OrganizerId 殘留 null 檢查失敗」而非泛用資料庫錯誤的實作），確認無殘留列後才繼續套用 `NOT NULL` 約束與外鍵。EF Core 預設會把單支 migration 的整個 `Up()` 包在同一個資料庫交易內執行，此明確中止 MUST 讓整支 migration（含約束、外鍵）完全不套用，不得產生部分套用的 schema 變更
5. 開發/測試環境的 seed data／既有 integration test 固定資料，需要同步補上對應的 `Organizer`／`OrganizerMember` 資料與「切換 Organizer」流程，否則既有假設「Admin 角色即可呼叫」的測試會全部失敗
6. Rollback：若需回滾，還原 migration 前先確認沒有新建立的 Organizer 資料依賴（例如新申請的 Organizer 下已有 Event），必要時先手動處理資料再降版；這是部署當下由人工核對的運維步驟，本次不強制要求對應的自動化測試

## 既有文件用詞落差（非本次修改範圍）

`admin-web-ui` 既有的「Admin 可透過介面管理場館與座位圖」「Admin 可透過介面管理活動與票種」兩條 Requirement（本次未提出 MODIFIED）文字仍以「Admin」稱呼操作者。實際上，依本次 RENAMED／MODIFIED 後的路由守衛規則，這兩個頁面的實際可操作者已放寬為「任何已切換至 Approved Organizer 的使用者」，不限 `Admin` 角色。這是既有文件的既有措辭與本次新授權模型範圍的用詞落差，不影響路由守衛 Requirement 本身的正確性；本次刻意不展開這兩條大篇幅既有 Requirement 的 MODIFIED（避免無謂重複既有大量 Scenario 內容），實際行為以路由守衛 Requirement 與 `event-management` 的授權規則為準。

## Risks / Trade-offs

- [既有整合測試大量依賴「Admin 角色即可呼叫後台端點」的假設] → 明確列在 Impact 段落，實作階段需要先盤點並更新這些測試的前置條件（改為先切換 Organizer），而非事後才發現大量測試失敗
- [Access Token 換發時機的使用者體驗] → `organizer-management` 變更僅在「申請成功」這一節點自動觸發一次切換嘗試；核准通過後或切換到其他已核准 Organizer，使用者需在「我的主辦方」清單頁手動點選「切換」才能取得帶 `OrganizerId` claim 的 Access Token（見該能力 design.md Decision 1 Trade-off），本次不額外處理
- [資料回填的單一轉入 Organizer 把所有既有活動歸在同一個主辦方名下，可能不符合展示情境] → 屬於已知、可接受的簡化；文件與 Demo 腳本可事後手動把資料拆分到多個 Organizer 展示多租戶效果，不影響正確性
- [平台 Admin 角色收斂後，Admin 帳號需要加入 Organizer 才能操作活動管理] → 已透過 Migration Plan 第 2 點解決：回填涵蓋所有既有 Admin 角色 Member，部署後既有 Admin 帳號皆自動是轉入用 Organizer 的成員，只需呼叫一次切換即可
