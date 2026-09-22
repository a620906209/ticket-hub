## 1. Domain

- [ ] 1.1 `Event` entity 建構子新增必填 `OrganizerId` 參數與對應屬性，驗證非 `Guid.Empty`

## 2. Application

- [ ] 2.1 既有建立 Event 的 Handler 改為從呼叫端 Organizer context 取得 `OrganizerId` 並傳入 `Event` 建構子，不接受請求內容指定
- [ ] 2.1a `CreateTicketTypeHandler`（design.md Decision 3）在既有 `@event is null` 檢查之後，新增核對 `@event.OrganizerId` 是否等於呼叫端目前 `OrganizerId`，不一致時回傳與「活動不存在」相同的 `Error.NotFound`，不建立任何票種
- [ ] 2.2 既有 Admin 專用活動列表查詢 Handler 新增依 `OrganizerId` 過濾條件，只回傳呼叫端目前 Organizer 名下的活動

## 3. Infrastructure

- [ ] 3.1 新增 EF Core migration：`Events` 新增可為 null 的 `OrganizerId` 欄位（先不加 `NOT NULL`／外鍵約束）
- [ ] 3.2 資料回填腳本（EF Core migration `Up()` 內原生 SQL）：以寫死的固定 GUID（見 design.md Migration Plan）搭配 `INSERT ... ON CONFLICT (Id) DO NOTHING` 建立轉入用 `Organizer`（`Name = "既有資料轉入"`、`Status = Approved`），不依賴 `Name` 查詢比對（`organizer-management` 不保證 `Name` 唯一），依賴 `organizer-management` 變更已存在的 `Organizer` 資料表
- [ ] 3.2a 資料回填腳本：為**所有**既有 `MemberRole.Admin` 角色的 Member（不限於曾建立過活動者）以 `INSERT ... ON CONFLICT (OrganizerId, MemberId) DO NOTHING` 建立對應 `OrganizerMember`（`Owner`），掛在 3.2 建立的固定 Id 轉入用 Organizer 下；此 `ON CONFLICT` 依賴 `organizer-management` 變更 `OrganizerMemberConfiguration` 已建立的 `(OrganizerId, MemberId)` 複合唯一索引，由資料庫保證重跑不重複插入
- [ ] 3.2b 資料回填腳本：以 `UPDATE Events SET OrganizerId = <固定 Id> WHERE OrganizerId IS NULL` 回填既有 `Event`，`WHERE` 條件本身保證重跑時已回填過的列不受影響
- [ ] 3.3 新增後續 migration：`Up()` 方法 MUST 先以明確、可辨識的原生 SQL 檢查（例如 `DO $$ BEGIN IF EXISTS (SELECT 1 FROM "Events" WHERE "OrganizerId" IS NULL) THEN RAISE EXCEPTION '...'; END IF; END $$;`，見 design.md Migration Plan 步驟 4）偵測是否仍有 `OrganizerId IS NULL` 的殘留列，偵測到時 MUST 主動拋出例外中止（不得依賴 Postgres 對 `NOT NULL` 約束本身的底層檢查碰巧失敗，訊息須可辨識為本次的殘留檢查，而非泛用資料庫錯誤）；確認無殘留列後，同一支 migration 才繼續套用 `Events.OrganizerId` 的 `NOT NULL` 約束與外鍵（確認 3.2～3.2b 回填已完成後才可加上）。此檢查與約束套用在同一個 migration 交易內，中止時整支 migration（含約束、外鍵）完全不套用，不產生部分套用的 schema 變更

## 4. WebApi

- [ ] 4.1 套用 `organizer-management` 變更已定義的 `RequireOrganizerContext` Policy 至 `AdminVenuesController`（Venue／SeatMap 建立與查詢），取代原本的 `[Authorize(Policy = AuthorizationPolicies.AdminOnly)]`
- [ ] 4.2 套用 `RequireOrganizerContext` Policy 至 `AdminEventsController` 的建立 Event／TicketType、Admin 專用活動列表查詢端點，取代原本的授權屬性（`AdminEventsController` 上其餘端點——銷售報表查詢、開關熱門搶購模式——維持原本的 `AdminOnly` 授權，屬於後續依賴本次的變更範圍，本次不動）。這些端點在 Controller 層組出傳給 Application 層 Handler 的 Command／Query 物件時，讀取呼叫端目前 `OrganizerId` MUST 統一呼叫 `organizer-management` 能力已定義的 `ClaimsPrincipalExtensions.TryGetOrganizerId`（見該能力 tasks.md 4.3），不得由各端點各自呼叫 `Guid.Parse` 另外實作一套；`RequireOrganizerContext` Policy 已保證能走到這裡時 claim 必為合法非空 Guid，此處呼叫 `TryGetOrganizerId` 只是共用同一套既有邏輯，不需要重複處理解析失敗的分支

## 5. 前端（admin-web-ui）

- [ ] 5.1 後台路由守衛調整：活動、場館頁面檢查 Access Token 是否帶 `OrganizerId` claim，未帶者導向「選擇主辦方」頁面；訂單、核銷頁面本次不修改，維持既有「角色為 Admin」規則

## 6. 既有測試盤點與更新

- [ ] 6.1 盤點所有假設「Admin 角色即可呼叫後台管理端點」的既有測試（`event-management`、`admin-web-ui` 活動／場館相關），更新其前置條件為「已切換至一個 Approved Organizer」（沿用 `organizer-management` 變更已新增的 `AuthTestHelper` 輔助方法）
- [ ] 6.2 更新本機開發／Docker Compose 的 seed data，補上一組已核准 Organizer 供展示（正式環境部署已由 3.2～3.2b 的回填涵蓋所有既有 Admin，不需額外手動步驟；此處僅為開發便利性）

## 7. 測試 — event-management（授權規則與活動建立變更）

測試類型：整合測試（xUnit + `WebApplicationFactory` + Testcontainers Postgres），因為驗證的是 `[Authorize]` Policy、EF Core 查詢過濾與端點間的實際串接，非單純 Handler 內部邏輯；7.9～7.12a（遷移回填、冪等性、完整性約束、端到端部署）需要實際執行 EF Core migration。被測主體：`AdminVenuesController`／`AdminEventsController`（含 `CreateTicketTypeHandler`、後台專用活動列表查詢 Handler）；7.9～7.12a 被測主體另含遷移腳本本身（3.2～3.3）。

- [ ] 7.1 [EVT-AUTHZ-001] 已切換至 Approved Organizer 的使用者逐一成功呼叫 spec.md Requirement 列出的全部 8 個端點，每個端點各自獨立斷言：(1) 建立 Venue 成功、回傳識別碼；(2) 建立 SeatMap 成功、回傳識別碼；(3) 查詢場地列表成功、回傳結果；(4) 查詢場地明細成功、回傳結果；(5) 查詢座位圖明細成功、回傳結果；(6) 建立 Event 成功、回傳識別碼；(7) 建立 TicketType 成功、回傳識別碼；(8) 後台專用活動列表查詢成功、回傳結果
- [ ] 7.2 [EVT-AUTHZ-002] 已登入但未帶 `OrganizerId` claim（含單純 `Admin` 角色未切換）逐一呼叫上述全部 8 個端點，每個端點各自獨立斷言：(a) 回傳 403；(b) 寫入端點（建立 Venue／SeatMap／Event／TicketType）額外斷言目標資源未被建立（比照既有 `SetQueueMode_AsNonAdminMember_Returns403AndDoesNotChangeState` 的「事後查詢確認狀態未變」模式）
- [ ] 7.3 [EVT-AUTHZ-003] 未帶 Token 逐一呼叫上述全部 8 個端點，每個端點各自獨立斷言：(a) 回傳 401；(b) 寫入端點額外斷言目標資源未被建立，比照 7.2 的驗證模式
- [ ] 7.3a [EVT-AUTHZ-004] 整合測試：核發一組帶 `OrganizerId` claim 的 Access Token 後，將該 Organizer 停權（呼叫 `organizer-management` 能力的停權端點），直接以該（未過期、未重新換發）Access Token 呼叫本能力任一後台管理端點，斷言請求仍被受理——驗證這是已知、有界（`AccessTokenExpirationMinutes`）的延遲視窗，而非 Policy 忘記檢查。**「停權後換發／切換即被拒絕」這兩條負向路徑不在本 task 重複測試**：已由 `organizer-management` change 的 tasks.md 7.24 [ORG-REFRESH-003]（換發時 claim 被清空）與 7.11 [ORG-SUSPEND-001]（停權後無法再切換進入）完整覆蓋，本 task 與該兩項測試合起來才是 `EVT-AUTHZ-004` 完整的正負向驗收
- [ ] 7.3b [EVT-AUTHZ-005] 整合測試：直接建構一組 `exp` 已在過去的 Access Token（不透過真實等待，避免測試變慢或不穩定；比照既有 `AuthTestHelper` 的 Token 產生方式，覆寫過期時間），以此 Token 呼叫**建立 Event** 端點（`POST /api/admin/events`，帶一個測試專用、可辨識的標題字串），斷言：(1) 回傳 401，此 401 MUST 由既有 JWT Bearer authentication 中介層產生（未帶過期 Token 時同一支測試改用有效 Token 呼叫同一端點須能成功，佐證差異只在於 Token 過期，而非請求格式本身有誤）；(2) 呼叫後改用一個獨立的、已核准 Organizer 身份的用戶端查詢活動列表，斷言查無任何 `Title` 等於該測試專用標題字串的 Event——證明 `CreateEventHandler` 未被執行、資料庫未被寫入。此驗證方式比照既有 `AdminEventsControllerTests.SetQueueMode_AsNonAdminMember_Returns403AndDoesNotChangeState`「事後查詢確認狀態未變」的既定測試模式，不新增額外的執行計數探針或 DB Interceptor 等本專案既有測試慣例中未使用過的機制
- [ ] 7.4 [EVT-CREATE-002] 建立活動成功時記錄的 `OrganizerId` 為呼叫端 Access Token 中的值，不接受請求內容覆寫
- [ ] 7.5 [EVT-LIST-001] 後台專用活動列表查詢僅回傳呼叫端目前 Organizer 名下的活動，不含其他 Organizer 的活動
- [ ] 7.6 [EVT-LIST-002][EVT-LIST-003][EVT-LIST-004] 後台專用活動列表查詢的建立者／建立時間／售票狀況統計（含過期 Held 視為 Available、零座位活動）於新授權模式下重新執行，確認行為與既有 spec 一致
- [ ] 7.6a [EVT-LIST-005] 既有公開活動列表查詢端點確認仍不回傳建立者／建立時間／售票狀況統計／`OrganizerId`
- [ ] 7.7 [EVT-CREATE-001][EVT-CREATE-003][EVT-CREATE-004][EVT-CREATE-005][EVT-TICKET-001][EVT-TICKET-002][EVT-TICKET-003][EVT-TICKET-005][EVT-TICKET-006][EVT-TICKET-007][EVT-TICKET-008] 既有建立活動／票種相關的驗證規則（缺必要欄位、場地座位圖不存在或不對應、票價無效、`RequiresSeat` 分流規則等）於新授權模式下重新執行，確認行為與既有 spec 一致
- [ ] 7.7a [EVT-TICKET-004]（design.md Decision 3）建立票種時，活動屬於其他 Organizer，回傳與「活動不存在」相同的 404，不建立任何票種，不回傳 403
- [ ] 7.8 [EVT-VENUE-001][EVT-VENUE-002][EVT-VENUE-003] 既有建立場地／座位圖相關驗證規則於新授權模式下重新執行：座位圖內座位樣板重複、建立座位圖時場地不存在，確認行為與既有 spec 一致
- [ ] 7.8a [EVT-VENUE-QUERY-001]～[EVT-VENUE-QUERY-009] 既有查詢場地／座位圖相關規則於新授權模式下重新執行：查詢場地列表（含依名稱排序、同名場地依 Id 排序）、查詢場地明細（含多張座位圖、座位圖無座位）、查詢不存在的場地明細、查詢座位圖明細、查詢不屬於指定場地的座位圖明細、座位圖目前沒有任何座位，確認行為與既有 spec 一致
- [ ] 7.9 [EVT-MIGRATE-001] 整合測試：對含既有 `MemberRole.Admin` Member（含從未建立過活動者）的資料庫執行 3.2～3.2a 的回填遷移，斷言每一位既有 Admin 皆各自擁有一筆掛在轉入用 Organizer 下、`Role = Owner` 的 `OrganizerMember`
- [ ] 7.10 [EVT-MIGRATE-002] 整合測試：對含既有 `Event` 資料的資料庫執行 3.2～3.2b 的回填遷移，斷言所有既有 `Event.OrganizerId` 皆等於轉入用 Organizer 的 Id
- [ ] 7.11 [EVT-MIGRATE-003] 整合測試：在資料庫刻意留下至少一筆 `OrganizerId IS NULL` 的 `Event`（模擬部署窗口期殘留資料）後執行 3.3 的 `NOT NULL` 約束遷移，斷言：(1) 遷移中止失敗，錯誤訊息可辨識為本次的殘留 null 檢查（而非泛用的資料庫層 `NOT NULL` 違反錯誤，驗證真的是 3.3 內建的明確 preflight 擋下、不是碰巧被資料庫擋下）；(2) 事後查詢資料庫 schema，確認 `Events.OrganizerId` 的 `NOT NULL` 約束與外鍵皆未套用，不存在部分套用的中間狀態
- [ ] 7.12 [EVT-MIGRATE-004] 端到端整合測試（Testcontainers Postgres，實際套用 EF Core migration）：對含既有 Admin 與既有 Event 的資料庫依序套用 3.2～3.3 的遷移；接著依序呼叫：(1) 該既有 Admin 登入端點，取得 Access Token **與 Refresh Token**；(2) `organizer-management` 能力的切換操作情境端點，**請求 body MUST 帶入步驟 (1) 取得的 Refresh Token 明文**（比照該能力 spec「會員可以切換目前操作中的 Organizer」需求的必要欄位），切換到回填產生的「既有資料轉入」Organizer；(3) 斷言步驟 (2) 換發的新 Access Token 帶有該轉入用 Organizer 的 `OrganizerId` claim；(4) 以步驟 (3) 的新 Access Token 呼叫本能力的後台專用活動列表查詢端點，斷言成功查得回填的既有 Event
- [ ] 7.12a [EVT-MIGRATE-005] 整合測試：對已執行過一次 3.2～3.2b 回填的資料庫，完整重新執行整支回填腳本（3.2～3.2b），斷言：(1) 轉入用 `Organizer` 資料表中僅有一筆固定 GUID 的記錄，未新增第二筆；(2) 每一位既有 Admin 角色 Member 仍各自只有一筆掛在該 Organizer 下的 `OrganizerMember`，未重複插入；(3) 所有既有 `Event.OrganizerId` 與第一次回填後的值完全相同，未被改寫

## 8. 測試 — admin-web-ui（路由守衛，活動／場館頁面）

測試類型：元件測試（Vitest + Vue Test Utils，路由守衛邏輯）。被測主體：路由守衛（router guard）。

- [ ] 8.1 [AWU-GUARD-001] 未登入的使用者開啟一般後台路由，導向登入頁
- [ ] 8.2 [AWU-GUARD-002] 已登入但未切換 Organizer 的使用者開啟活動或場館頁面，導向「選擇主辦方」頁面
- [ ] 8.3 [AWU-GUARD-003] 已切換至 Approved Organizer 後可進入活動或場館頁面
- [ ] 8.4 [AWU-GUARD-004][AWU-GUARD-005] 非 Admin 角色開啟 `/admin/organizers` 導向買家端首頁；Admin 角色（不論是否已切換 Organizer）可進入
- [ ] 8.5 [AWU-GUARD-006] 已切換 Organizer 但角色非 Admin 的使用者開啟訂單或核銷頁面，仍依既有規則導向買家端首頁（本次不受影響，回歸測試）
