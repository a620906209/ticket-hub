## Context

- 目標來自 docs/project-scope.md §5（500 併發搶 50 張、0% 超賣、P95 < 500ms），執行方式來自 §8 ②（compose 內跑 k6、產出腳本與報告、不改產品程式碼）。
- 現況限制：
  - 註冊（`RegisterMemberRequest`）與登入（`LoginHandler`）都需要驗證碼，k6 無法走真實登入取得 500 個 token；登入另有 per-IP 限流（`CreateIpPartition`）。
  - 下單／付款為 per-member 限流（`RateLimiting` 20 次 / 60 秒），每位買家只下 1 單，不受影響。
  - 排隊模式的入隊需驗證碼，本次不納入。
  - 售完回應：數量票庫存不足、座位已被持有皆為 409（`CreateOrderHandler`）。
  - 銷售報表 `GetEventSalesReportHandler` 只統計 Paid 訂單（`GetPaidItemSalesByEventIdAsync`），Pending 不計入。
  - 付款閘道為 `MockPaymentGateway`（`AlwaysSucceed`），寄信為 `MockEmailNotificationService`，壓測不會打到外部服務。
  - 產品的 DI 註冊全部寫在 `Program.cs`，Infrastructure 沒有可重用的 `AddInfrastructure()` 擴充方法。
  - access token 預設 30 分鐘（`Jwt:AccessTokenExpirationMinutes`）。

## Goals / Non-Goals

**Goals:**

- 可重現地驗證兩個 scenario（數量票、座位票）的 0% 超賣與下單 P95。
- 全程在 compose 內執行，不改 `src/` 任何產品程式碼。
- 結果報告的數字可追溯到實際的 k6 summary JSON。

**Non-Goals:**

- 排隊模式、登入／註冊流程、驗證碼端點的壓測。
- 正式環境容量規劃或雲端部署壓測（本機 Docker Desktop／WSL2 數據僅供參考）。
- 為壓測修改產品限流或加入驗證碼繞過開關。
- 壓測後自動清除測試資料。

## Decisions

### 決策 1：以獨立 seeder 在 process 內簽發 token，而非產品加驗證碼繞過

- **做法**：新增 `tools/ProjectC.LoadTest.Seeder`（console，reference `ProjectC.Infrastructure`）。
  - 自行在 seeder 的 `ServiceCollection` 註冊 `ApplicationDbContext`（Npgsql，讀 `ConnectionStrings:DefaultConnection`）、`BCryptPasswordHasher`、`JwtTokenService`（綁定 `Jwt` 設定）、`DevelopmentDataSeeder`，以及它建構子需要的 `IDateTimeProvider`（Singleton）與 `ILogger<T>`（`AddLogging`）。
  - DbContext 與 seeder 為 Scoped，在單一 scope 內執行。
  - 程式分兩層：進入點（`Program`）負責參數與環境檢查；`LoadTestSeedRunner` 負責寫 DB、簽發 token、寫檔。runner 由參數接收買家數、輸出路徑、Admin 隨機密碼與 `TextWriter`，讓整合測試能以 Testcontainers DB 直接呼叫並擷取輸出（見決策 9）。
  - 標準輸出的契約：`Program` 把 `Console.Out` 交給 runner 當 `TextWriter`，成功路徑上 `Program` 自己不再寫任何內容，所以成功時的標準輸出等於 runner 的輸出。Admin 隨機密碼只在 `Program` 內產生後傳給 runner，`Program` 不輸出它。整合測試驗證 runner 輸出；tasks 2.7 實跑時再擷取完整 stdout／stderr，確認不含 token 檔中的任何 token（隨機密碼無從得知，由整合測試涵蓋）。
- **替代方案**：
  - 產品加 Development 限定的驗證碼繞過：違反「不改產品程式碼」，也在產品裡留下攻擊面。
  - k6 從 Redis 讀驗證碼答案：耦合 captcha 內部儲存格式，且仍受登入 per-IP 限流阻擋。
- **安全邊界（只支援本機 compose，目標一律寫死）**：
  - seeder 進入點依序檢查，任一不符就以非 0 exit code 結束，且不建立 DbContext、不寫檔：
    1. `ASPNETCORE_ENVIRONMENT == Development`；
    2. 連線字串以 `NpgsqlConnectionStringBuilder` 解析後 `Host == "db"`（compose service name），擋下連線字串被改指向其他資料庫的情況；
    3. `Jwt:Issuer`、`Jwt:Audience`、`Jwt:SigningKey` 都有值，且 `SigningKey` 至少 32 個字元（與產品 `JwtOptions.SigningKey` 的 `[MinLength(32)]` 相同規則），否則不簽發 token，避免簽出弱 token 或無法被 api 驗證的 token；
    4. 參數驗證（見下方「安全確認」）。
  - 設定來源：以 `ConfigurationBuilder().AddEnvironmentVariables()` 明確讀取環境變數（compose 以 `ConnectionStrings__*`、`Jwt__*`、`ASPNETCORE_ENVIRONMENT` 提供），不依賴 Host builder 的預設行為。
  - 隨機密碼一律用 `RandomNumberGenerator.GetBytes(32)` 轉 Base64 產生，不用 `Random`／`Random.Shared`（本專案 captcha 曾因 `Random.Shared` 出過問題）。
  - 簽發用的 signing key 與 api 相同、來自同一組 compose 環境變數，不新增任何金鑰。
  - k6 的 API 位址寫死為腳本常數 `http://api:8080`，不讀 `__ENV`，因此無法用 `-e` 指向其他環境。

### 安全確認（CLAUDE.md 安全強制規則）

**輸入驗證**
- seeder 只接受一個參數 `--buyers`，在進入點（`Program`，DI 建立之前）驗證：必須是整數且在 1–1000 之間。
  - 非整數、超出範圍、未知參數：stderr 印出錯誤，exit code 2，不連 DB、不寫檔。
  - 預設 500。
- 不提供 `--output`：token 檔路徑寫死為 `/src/loadtest/.output/tokens.json`，沒有路徑穿越或覆寫任意檔案的入口。
- 不拼接任何 SQL 或 shell 指令。
- k6 腳本不輸出 token：`fail()`、`check` 名稱、`console.log` 與 setup 失敗訊息只印 HTTP 狀態碼、端點路徑與回應 body 的錯誤摘要，不印 request header（含 Authorization）或 token 檔內容。
- k6 腳本不接受外部輸入改變目標。`__ENV` 只有三個，都不影響 API 位址：
  - 故障注入開關 `LT_FAULT`（決策 9），只能讓門檻更嚴或讓輸入變壞，不能放寬門檻；
  - 組態標籤 `LT_API_BUILD`（決策 10），只接受 `debug` 或 `release`，只影響 summary 檔名，其他值或未設定時 `fail()`；
  - 執行序號 `LT_RUN`（決策 10），只接受 `1`、`2`、`3`，只影響 summary 檔名；非故障注入的執行未設定或為其他值時 `fail()`。
- 彙整腳本 `loadtest/aggregate.js`（決策 11）只讀 `/output` 下固定檔名的 summary JSON，不發任何 HTTP 請求、不讀 token 檔、不讀 `__ENV`。

**資料庫**
- 全部透過 EF Core LINQ（參數化），不用 raw SQL。
- N+1：既有 email 以單一 `Where(m => emails.Contains(m.Email))` 查詢一次取得；新會員一次 `SaveChangesAsync`；Admin 的 Organizer 以單一查詢取得。
- 寫 DB 前先檢查 pending migration：有未套用的 migration 時以非 0 exit code 結束，並提示執行 `docker compose exec api dotnet ef database update`。原因是 `DevelopmentDataSeeder` 遇到 pending migration 只記警告就 return，若不先擋，後續找不到 Admin 會變成不明確的失敗。
- `SeedAsync` 後查不到該 Admin 擁有的 Approved Organizer 時，同樣以非 0 exit code 結束並說明原因。

**權限**
- 誰能執行 seeder：能對本機 compose 執行 `docker compose exec api` 的人。這等同已能讀取 `.env` 的 signing key，seeder 沒有擴大權限。
- token 檔：
  - 只含本機 signing key 簽發的 token，30 分鐘後失效；
  - 每次執行覆寫，不累積；
  - 位於 gitignore 目錄，不進版控；k6 以唯讀方式讀取；
  - 不需自動刪除（過期即無效），`loadtest/README.md` 註明可手動刪除。
  - seeder 寫檔前先 `Directory.CreateDirectory`：`.output` 被 gitignore，新 clone 時不存在。
- 壓測 Admin 帳號會以未知的隨機密碼留在開發 DB，並擁有 Approved Organizer。持有 signing key 本來就能偽造任何 token，所以這不增加風險；README 註明這是 Development 專用帳號。
- 產品端點的授權不變，k6 沒有繞過任何一層檢查（以下依既有程式碼核對）：
  - 買家端點 `POST /api/orders`、`POST /api/orders/{id}/confirm`：`OrdersController` 類別層級 `[Authorize]`，JWT 驗證失敗（簽章、Issuer、Audience、過期）由認證 middleware 回 401。
  - Admin 端點（`/api/admin/venues`、`/api/admin/events`、`/api/admin/events/{id}/ticket-types`、`/api/admin/events/{id}/sales-report`）：掛 `RequireOrganizerContext` Authorization Policy。`RequireOrganizerContextHandler` 只以 `TryGetOrganizerId` 判定：OrganizerId claim 不存在、空字串、無法解析或為 `Guid.Empty` 時不 Succeed，ASP.NET Core 回 403。**這個 policy 不檢查 Role**，所以 Admin token 能通過的唯一條件是帶合法 OrganizerId claim。
  - 第二層（Controller）：`AdminEventsController` 的建立活動、建立票種、sales-report 在 policy 之後再呼叫 `TryGetOrganizerId`，失敗時 `Forbid()`（403，fail-closed）。`AdminVenuesController` 只有 policy，沒有第二層（場館不屬於 Organizer scope）。
  - 第三層（Handler）：`CreateTicketTypeHandler`、`GetEventSalesReportHandler` 檢查 `event.OrganizerId == organizerId`，不符時回 404。因此 k6 只能操作「這個 Admin token 的 Organizer」建立的活動。
  - Admin token 的 OrganizerId 來源見決策 2：只取壓測 Admin 擁有、狀態為 Approved 的那個 Organizer，不是任意 Guid。
  - 未授權觸發：故障注入 `bad-admin-token`（期望 setup 收到 401 而中止）與 `bad-buyer-token`（期望下單全部 401）實際驗證上述拒絕路徑。

**前端**：不涉及。

### 決策 2：壓測 Admin 重用 `DevelopmentDataSeeder`

- **做法**：以 `Options.Create(new DevelopmentSeedOptions { AdminEmail = "loadtest-admin@loadtest.invalid", AdminPassword = <每次以 RandomNumberGenerator 隨機產生、不輸出>, OrganizerName = "LoadTest Organizer" })` 建構 `DevelopmentDataSeeder` 並呼叫 `SeedAsync`。
  - 既有邏輯已冪等，負責建立 Admin 與 Approved Organizer。
- **runner 流程與契約**（`SeedAsync` 回傳 `Task`、不回報成功與否，所以結果一律由 runner 事後查詢判定）：
  1. 檢查 pending migration，有的話以非 0 結束並提示 migration 指令。原因：`SeedAsync` 遇到 pending migration 只記 warning 就 return，runner 若不先擋，會在後續查詢才以不明確的方式失敗。
  2. 呼叫 `SeedAsync`。
  3. 以 email 查詢壓測 Admin（`Members.SingleOrDefaultAsync(m => m.Email == ...)`）。查不到時以非 0 結束。
  4. 檢查 `Role == MemberRole.Admin`。不是時以非 0 結束並說明原因，且不寫 token 檔。原因：`SeedAsync` 遇到同 email 但非 Admin 的既有帳號只記 warning、不改角色，卻仍會替它建立 Organizer。policy 不看 Role，這種帳號的 token 仍能通過，但那代表壓測資料被人為改動過，fail loud 比靜默沿用安全。
  5. 以單一查詢（`OrganizerMembers` join `Organizers`）找出該 Admin 為 Owner、`Status == Approved` 且 `Name == "LoadTest Organizer"` 的 Organizer。結果不是恰好 1 筆時以非 0 結束（0 筆表示 `SeedAsync` 沒有完成；多筆表示資料被人為改動，無法判斷該用哪一個）。
  6. 以 `JwtTokenService.GenerateAccessToken(admin, organizerId)` 簽發 admin token；買家以 `GenerateAccessToken(buyer)`（`organizerId` 為 null，不帶 OrganizerId claim）簽發。
  7. 所有檢查都通過後才寫 token 檔；任一步失敗都不寫檔，舊的 token 檔維持原狀（會在 30 分鐘內自然失效）。
- **理由**：不重寫「建 Admin + Approved Organizer」的邏輯。
  - 已存在時密碼不會被覆寫，這沒有影響，因為不會用密碼登入。
- **替代方案**：使用 .env 的 `DEV_SEED_ADMIN_*` 帳號。目前 .env 未設定，而且把壓測與展示帳號混用會互相干擾。

### 決策 3：買家會員批次建立，共用一次 BCrypt 雜湊

- **做法**：
  - email 為 `loadtest-buyer-{0001..N}@loadtest.invalid`。
  - 先一次查出已存在的 email，只對缺少的部分呼叫 `Member.Register`。
  - 所有新會員共用同一個「隨機密碼 → BCrypt 雜湊」結果，最後一次 `SaveChangesAsync`。
- **理由**：
  - BCrypt 刻意設計得很慢，500 次個別雜湊要數分鐘。
  - 這些帳號永遠不以密碼登入，共用雜湊沒有安全意義上的損失。
  - 密碼本身不輸出、不保存。
- `Member.Register` 建立的會員預設 Active、Role = Member。這點於實作時以測試確認；若不是，就改呼叫 `Activate()`。

### 決策 4：token 檔格式與位置

- **位置**：`loadtest/.output/tokens.json`，經由 api 容器的 `/src` bind mount 寫入。
- **k6 掛載（兩個 volume，讀寫分開）**：
  - `./loadtest:/scripts:ro`：腳本唯讀；
  - `./loadtest/.output:/output`：可寫，k6 從 `/output/tokens.json` 讀 token，`handleSummary` 寫 `/output/<scenario>-<LT_API_BUILD>-run<LT_RUN>-summary.json`（決策 10）。
- **格式**：`{ "issuedAtUtc": "...", "adminToken": "...", "buyerTokens": ["..."] }`。
- `loadtest/.output/` 列入 `.gitignore`。
- 報告引用的 summary 另外複製到 `docs/load-test/`。summary 不含 token，可以進版控。
- k6 init context 以 `open()` 讀檔，用 `SharedArray` 共享，避免 500 份複本。

### 決策 5：k6 setup 用真實 admin API 建活動，規模依 §3 基準

- **座位圖**：20 個分區共 2000 席。
  - 目標座位分區 `HOT` 為 50 席。
  - 其餘 19 區合計 1950 席，平均分配，餘數放最後一區。
- **票種**：共 20 個。
  - 座位票 scenario：每個分區 1 個座位票種，目標為 `HOT`。
  - 數量票 scenario：19 個非目標分區的座位票種，加上 1 個 `RequiresSeat=false`、`AvailableQuantity=50`、`ZoneCode = "GA"` 的數量票種，總數同樣為 20。`GA` 不與座位圖任何分區同名；`CreateTicketTypeHandler` 對 `RequiresSeat=false` 不驗證分區，產品也沒有「同活動票種 ZoneCode 唯一」的限制，但仍刻意避開重名，讓報表與列表能直接以 ZoneCode 辨識目標票種。
- **活動命名**：Title 一律以 `[LoadTest] ` 開頭，後接 scenario 與 UTC 時間（例如 `[LoadTest] count 2026-10-05T08:00:00Z`），方便手動辨識與清理。
- **setup 逾時**：兩支腳本設 `setupTimeout: '180s'`（k6 預設 60 秒，建 2000 席座位圖與 20 個票種可能超過）。逾時由 k6 視為 setup 失敗並中止，不會進入下單。
- **前置檢查**：`setup()` 一開始先確認 token 檔的 `buyerTokens` 數量 ≥ 500 且彼此不重複，否則 `fail()`。避免買家索引越界取到 `undefined`、以無效 Bearer 送出請求。
- **販售時間**：`SalesStartAtUtc` 設為現在減 1 分鐘，`StartAtUtc` 設為現在加 1 天，`SalesEndAtUtc` 留空。不呼叫 queue-mode，預設就是非排隊。
- **建立順序**：`POST /api/admin/venues` → `/{venueId}/seat-maps` → `POST /api/admin/events` → `/{eventId}/ticket-types`（逐一）。
  - 座位票的目標 event seat id 從 `GET /api/events/{id}/seats` 篩出 `HOT` 分區取得。
- **座位票的 50 席邊界**：產品契約是既有 `ticket-purchase` spec 的 TP-ORDER-004——座位分區 MUST 與票種 `ZoneCode` 一致，不一致拒絕建立。已核對實作：檢查位於 `OrderService.PlaceOrderAsync`（`seatTemplate.ZoneCode != ticketType.ZoneCode` 回傳驗證錯誤，呼叫 `CreateOrderHandler` 之前），並有 `OrderServiceTests.PlaceOrderAsync_WhenSeatZoneDoesNotMatchTicketTypeZone_ReturnsValidationError` 涵蓋。`CreateOrderHandler` 不負責 `ZoneCode` 分區驗證，但仍負責非空選擇、所有項目同一活動、座位不重複，以及座位與票種 `EventId` 一致等檢查。因此 `HOT` 票種只能賣出 `HOT` 分區的座位。本 change 不修改這段產品邏輯，也不測試跨分區配對（那是 TP-ORDER-004 既有單元測試的範圍）。setup 的核對是**壓測前置條件**，確保測試資料真的是「50 席搶 500 人」，在交給 VU 之前 MUST 以公開 API 核對（任一不符就 `fail()`）：
  1. `GET /api/events/{id}/ticket-types` 中目標票種的 `ZoneCode == "HOT"`、`RequiresSeat == true`；
  2. `GET /api/events/{id}/seats` 中 `ZoneCode == "HOT"` 的座位恰好 50 個、id 不重複、`Status` 全部為可售；
  3. 交給 VU 的座位池恰好等於第 2 點的 50 個 id（集合相等），VU 只從這個座位池選位。
  - verify 階段另外確認非 `HOT` 分區的座位全部仍為可售（決策 7）。這不是取代產品的分區檢查，而是確認本次壓測除了目標分區之外沒有其他座位狀態變化，讓「`HOT` 非可售席數 = `QuantitySold`」的對帳範圍完整。
- 任一步驟非 2xx 就 `fail()` 中止。
- **理由**：走真實 API 才能涵蓋產品的驗證規則，也不必耦合 Domain 建構細節。
- **替代方案**：由 seeder 直接以 EF 建活動。這會重複產品的建立邏輯，與「不重寫產品邏輯」的方向相反。

### 決策 6：負載模型與 P95 量測範圍

- **執行器**：每個 scenario 是獨立腳本、獨立執行。
  - 使用 `per-vu-iterations` executor，`vus: 500`、`iterations: 1`。
  - VU 以 `exec.scenario.iterationInTest`（scenario 內從 0 起算、不重複的 iteration 序號；每個 VU 只跑 1 次，所以 500 個 VU 對應 0–499）索引買家 token，確保 500 個 VU 對應 500 個不同會員。實作時實測發現 `__VU` 是全測試共用的 VU 編號（加上 verify scenario 的 VU 後不保證從 1 開始連續），原設計的 `__VU - 1` 可能越界，因此改用此值。
- **同時起跑**：k6 沒有原生 barrier。採 `setup()` 回傳 `startAt = now + 10s`，VU 在下單前 `sleep` 到該時間點，讓 500 個請求集中送出。
  - 不使用 ramping，因為目標是「同時搶」而不是逐步加壓。
- **量測範圍**：下單請求加上 tag `name: place-order`，threshold 只對 `http_req_duration{name:place-order}` 設 `p(95)<500`，confirm 與 setup 請求不計入。
- **座位票選位**：每個 VU 從 50 個 seat id 中隨機選 1 個，以真實撞位為目標。
  - 成功數理論上可能少於 50（某席沒人選中的機率約 (49/50)^500 ≈ 4.5×10⁻⁵），所以上限是 ≤ 50。
  - 下限設 ≥ 1，避免「全部失敗」被判通過；其餘回應是否都是 409 由 `place_order_unexpected` 門檻把關（決策 7）。
- **時間安排（保證 verify 在所有 confirm 之後）**：
  - 搶票 scenario 設 `maxDuration: '70s'`（含 10 秒對齊）、`gracefulStop: '0s'`，超過時間的 iteration 會被直接中斷；
  - 每個 VU 完成下單（及成功時的 confirm）後，Counter `buyer_iterations_completed` 加 1，threshold `count==500`。任何 VU 沒在時限內完成就會讓門檻失敗；
  - `verify` 的 `startTime: '85s'`（= 70s + 10s + 5s 緩衝）。由於 `gracefulStop: '0s'`，verify 開始時不會有仍在進行的 confirm。
  - 多等的 10 秒是為了查詢快取：`GET /api/events/{id}/ticket-types` 有 Redis 快取（`QueryCache:TicketTypesTtlSeconds = 10`）。下單／付款後雖會清除快取，但搶票期間的併發讀取可能在最後一次清除之後寫回舊值；搶票在 70s 前結束，舊值最晚在 80s 前過期。座位列表沒有快取，sales-report 也沒有快取。
- **替代方案**：`shared-iterations`。它不保證每個 VU 只跑 1 次，同一個 token 可能下多單，失去「500 名不同買家」的語意。

### 決策 7：超賣驗證——k6 計數 + 付款後銷售報表核對

- **k6 端**：
  - 自訂 Counter `orders_created`（201 時加 1），threshold `count<=50`。數量票另加 `count>=50`（恰好 50）；座位票另加 `count>=1`。
  - 自訂 Counter `place_order_5xx`，threshold `count==0`。
  - 自訂 Counter `place_order_unexpected`：下單回應不是 201 也不是 409 時加 1（含 401／400／404／429 與網路錯誤），threshold `count==0`。這條落實「其餘回應皆為 409」。
- **付款**：下單 201 的 VU 立即呼叫 `POST /api/orders/{id}/confirm`，tag 為 `name: confirm-order`。
  - 理由：銷售報表只統計 Paid。不付款的話，伺服器端唯一可用的 API 證據會是 0。
  - confirm 失敗計入 Counter `confirm_failed`，threshold `count==0`。否則報表與 k6 計數不一致時，會無法分辨是超賣還是付款失敗。
- **伺服器端**：
  - `handleSummary` 拿不到 HTTP。因此新增最後階段 scenario `verify`：單一 VU，`startTime` 排在搶票 scenario 之後。
  - `verify` 以 admin token 取 sales-report，找出目標票種的 `QuantitySold`。
  - 判斷邏輯抽成純函式 `evaluateVerification(input)`（`loadtest/lib/verify.js`），輸入為 sales-report、票種列表、座位列表與 scenario 種類，回傳失敗原因清單。verify scenario 只負責取資料、呼叫它，清單非空時 Counter `oversell_check_failed` 加 1，threshold `count==0`。
  - 抽成純函式是為了能用假資料逐條觸發每個失敗條件（決策 9），不必真的製造超賣。
- **跨 VU 傳遞成功數**：k6 的 metric 在執行期間無法跨 scenario 讀取。因此 verify 不和 k6 計數比較，改為：
  - 驗證 sales-report 的 `QuantitySold` ≤ 50；
  - 數量票另外驗證 `QuantitySold == 50`，並以 `GET /api/events/{id}/ticket-types` 確認目標票種剩餘量為 0；
  - 座位票另外驗證 `GET /api/events/{id}/seats` 中 `HOT` 分區非可售的席數等於 `QuantitySold`，且非 `HOT` 分區沒有任何非可售的座位。
  - 「報表 = k6 成功數」這一條在 `handleSummary` 自動判定：`handleSummary` 拿得到所有 scenario 的彙總 metric。verify scenario 把讀到的 `QuantitySold` 記到 Gauge `verify_quantity_sold`，`handleSummary` 呼叫純函式 `evaluateRunResult(data)`（`loadtest/lib/result.js`）比對 `orders_created` 與 `verify_quantity_sold`，同時檢查所有 threshold 的 `ok`。結果寫進 summary JSON 的 `runVerdict`（`passed` 與失敗原因清單）。
  - 限制：`handleSummary` 不保證能改變 exit code，所以單次執行「通過」的定義是 exit code 為 0 **且** `runVerdict.passed == true`。兩者由決策 11 的彙整腳本與報告一起檢查。
  - `verify_quantity_sold` 沒有值（verify 沒跑到或取資料失敗）時，`evaluateRunResult` 判為失敗，不視為一致。
- **併發程度的佐證**：Trend `place_order_send_offset_ms` 記錄每個 VU 送出下單時距離 `startAt` 的毫秒數。報告用它的最大值減最小值說明 500 個請求是否真的集中送出。不看最大值：k6 只有牆上時鐘（`Date.now()` 與 `exec.instance.currentTestRunDuration` 皆是，沒有 `performance.now()`），WSL2 時鐘約每 29 秒倒退約 1.5 秒，等待期間倒退會讓全部時距一起平移成負值（2026-10-05 實測）。
- **座位票成功數的判讀**：門檻 `count>=1` 只是防呆。500 次隨機選 50 席時，50 席都至少被選中一次的機率約 99.8%（任一席沒被選中的機率約 50 × 0.98^500 ≈ 0.2%），所以全部回應都是 201／409 時，成功數幾乎必然是 50。報告依下列順序歸因，不得跳過：
  1. 產品問題：`oversell_check_failed`、`place_order_5xx`、`place_order_unexpected`、`confirm_failed` 任一非 0，或 `QuantitySold ≠ orders_created`。
  2. 壓測環境未達預期併發：`buyer_iterations_completed < 500`，或 `place_order_send_offset_ms` 最大值減最小值超過 1000ms（`evaluateSendSpread`：寫進 `runVerdict`，彙整時也重算一次）。該次執行判未通過，報告註明是環境因素。
  3. 以上皆否但座位票成功數 < 50：標「低於預期，需調查」，不直接歸因為隨機結果。
- **不使用 raw SQL**：符合 CLAUDE.md 的禁止規則，全部透過既有 API 驗證。

### 決策 8：compose 整合

- **k6 服務**：
  - `image: grafana/k6:<固定版本>`，`profiles: ["loadtest"]`。
  - `depends_on: api (service_started)`，因為 api 沒有 healthcheck。
  - volumes 見決策 4（腳本唯讀、`.output` 可寫）。
  - 不設 API 位址環境變數（寫死在腳本，見決策 1）。
  - 不對外映射任何 port。
- **k6 image 版本**：和既有 seq 一樣釘固定 tag，compose 註解記錄查證來源與日期；不額外釘 digest，因為本專案沒有 digest 慣例，且只用於本機壓測。k6 授權為 AGPL-3.0，這裡只在本機以工具容器執行、不散布，不受影響。
- **檔案權限**：`grafana/k6` image 以非 root 使用者（uid 12345）執行。實作時實測發現：Docker Desktop／WSL2 的 bind mount 會保留容器內設定的權限，由 seeder（api 容器內的 root）建立的 `.output` 是 0755 root，k6 無法寫入 summary JSON（從主機建立的目錄則可寫）。因此 seeder 寫 token 檔時一併處理輸出目錄權限，由 xUnit 測試涵蓋。原本採目錄 0777、token 檔 0644（使用者 2026-10-05 選定）；審查指出同主機其他使用者可讀到有效 JWT，同日改為最小權限：目錄 0700、token 檔 0600，擁有者都 chown 給 k6 的 12345:12345（seeder 以 root 執行不受影響）；token 檔先以 0600 建立暫存檔再改名覆蓋，不沿用舊檔的 0644。Linux 權限只在 Docker／WSL2 內有效，Windows 主機端由 NTFS ACL 決定：README 要求以 `icacls` 移除 `loadtest/.output` 的繼承、只保留目前使用者／SYSTEM／Administrators（使用者 2026-10-05 選定）。Linux 原生 Docker 未驗證，本 change 只支援本機 Docker Desktop／WSL2，README 記錄此前提。
- **seeder 執行方式**：`docker compose exec api dotnet run --project tools/ProjectC.LoadTest.Seeder -- --buyers 500`。沿用 api 容器的環境變數（DB、Jwt），不新增服務。
  - seeder 建置時會寫入 Domain／Application／Infrastructure 共用的 bin/obj volume，這和既有的 `docker compose exec api dotnet test` 行為相同。
  - 為避免建置影響正在量測的 api，執行順序固定為：seeder 完成 → 確認 api 回應正常（`GET /api/events` 回 200）→ 才執行 k6。壓測進行中不得執行 seeder 或 `dotnet test`。寫進 `loadtest/README.md`。
- **新增 named volume**：seeder 與其測試專案的 bin/obj，延續 compose 既有的 Windows I/O 效能慣例。
- **slnx**：加入 seeder 與測試專案，讓 `dotnet build` 和 strict-reviewer 涵蓋它們。

### 決策 9：測試策略

- **seeder 測試專案**：`tests/ProjectC.LoadTest.Seeder.Tests`，xUnit。
  - 單元測試（只測進入點的檢查，不碰 DB）：
    - 環境不是 Development、連線字串 Host 不是 `db`、`--buyers` 非法時，回傳非 0 且不建立 DbContext；
    - token 檔序列化格式。
  - 整合測試：用 Testcontainers 啟動 PostgreSQL，直接呼叫 `LoadTestSeedRunner`（繞過進入點的 Host 檢查，因為測試容器的 Host 不是 `db`）。
    - 首次執行建立 N 個會員與 1 個 Admin + Approved Organizer；
    - 重跑不會重複建立；
    - 簽發的買家 token 能被產品的 JWT 驗證參數接受（以與 `Program.cs` 相同設定建立的 `TokenValidationParameters`），Admin token 帶 OrganizerId claim；
    - 以 `StringWriter` 擷取完整成功路徑的輸出，斷言不含 token 檔中的任何 token，也不含測試傳入的 Admin 密碼。
    - 有 pending migration（未套用 migration 的空 DB）時回傳非 0。
- **k6 腳本（核准的測試例外，使用者於 2026-10-05 同意）**：
  - 例外內容：k6 腳本不以 xUnit 單元／整合測試涵蓋。
  - 理由：k6 腳本是 JavaScript，在 k6 runtime 執行，xUnit 無法載入；為此把 k6 包進 .NET 測試只會增加不必要的複雜度。
  - 適用範圍：只限 `loadtest/` 下的 k6 腳本，以及 compose 設定與執行流程類的 Scenario（LT-COMPOSE-*、LT-RELEASE-001、LT-SETUP-001、LT-RUN-*、LT-GATE-001、LT-REPEAT-*、LT-REPORT-*），這些以指令或實際執行驗證；seeder 仍完整走 xUnit。
  - 替代驗證（都可重複執行、不必修改檔案）：
    1. `loadtest/tests/verify.test.js`：用 k6 執行的自我測試，以假資料呼叫 `evaluateVerification`，逐條觸發每個失敗條件（`QuantitySold > 50`、數量票 `QuantitySold ≠ 50`、數量票剩餘量 ≠ 0、座位票非可售席數 ≠ `QuantitySold`、座位票非 `HOT` 分區有非可售座位），再加一組全部正常的資料；用 `check` 斷言，threshold `checks: rate==1`。
    2. `loadtest/tests/report.test.js`：用 k6 執行的自我測試，以假的 summary 資料呼叫 `evaluateRunResult` 與 `aggregateRuns`／`renderReportTables`（決策 11）：一致且全部通過、`QuantitySold ≠ orders_created`、某 threshold `ok == false`、缺 `verify_quantity_sold`、3 次全過、3 次中 1 次失敗、缺檔；並斷言產出表格的數字等於輸入的數字。
    3. 故障注入開關 `LT_FAULT`（`-e LT_FAULT=<值>`），只能讓情況變壞：
       - `p95`：把 P95 門檻改為 `p(95)<1`；
       - `quantity51`：數量票目標票種 `AvailableQuantity` 改為 51，使 k6 端與伺服器端都應偵測到超賣；
       - `bad-admin-token`：setup 使用竄改過的 admin token。
       - `bad-buyer-token`：下單使用竄改過的買家 token（預期全部 401）；
       - `setup-timeout`：把 `setupTimeout` 改為 `'1ms'`，觸發 setup 逾時（原訂 `'1s'`；2026-10-05 實作時實測 API 熱機後 setup 在 1 秒內完成、未逾時而繼續下單，故改為 1ms；k6 2.3.0 對 1ms 確實強制逾時，exit code 100）。
    4. 搶票類故障注入（`p95`、`quantity51`、`bad-buyer-token`）執行後，檢查 summary JSON 中「預期失敗的那幾個 threshold」`ok == false`，而不只看 exit code。
    5. setup 中止類的驗證（`bad-admin-token`、`setup-timeout`、token 不足）不依賴 summary JSON（setup 失敗時不保證產生）。證據為：k6 exit code 非 0、stdout 含 setup 失敗訊息，且 stdout 的 `iterations` 為 0 或未出現。依 k6 語意，setup 失敗時不會執行任何 VU 的 default／scenario 函式，因此不會送出下單請求。
  - 實際執行壓測本身也是驗收（5.4、5.5）。

### 決策 10：Release 組態對照與重複執行

- **為什麼**：
  - api 平時以 `dotnet watch` 的 Debug 組態執行，JIT 最佳化較少，P95 不代表正式部署的表現；只有 Debug 數據，結論的說服力不足。
  - 本機 Docker Desktop／WSL2 有共用資源與時鐘問題，單次執行的數字波動可能很大。
- **Release 組態的切換方式**：
  - 新增 `docker-compose.loadtest-release.yml`，只覆寫 api 的 `entrypoint` 為 `dotnet run --project src/ProjectC.WebApi/ProjectC.WebApi.csproj -c Release --no-launch-profile --urls http://0.0.0.0:8080`。
  - `entrypoint` 以 list 形式撰寫（`["dotnet", "run", ...]`），不經 shell 切字串。
  - 切換：`docker compose -f docker-compose.yml -f docker-compose.loadtest-release.yml up -d --no-deps api`。`--no-deps` 讓 db、redis 等相依服務不被重建；不帶本機的 `docker-compose.override.yml` 也不影響 api，因為 k6 走 compose 內部網路，不需要對外 port；api 的 8080 映射來自 `docker-compose.yml`，切換後仍存在。若 pgadmin 正在執行，Compose 會印 orphan 警告，無害（README 註明）。
  - 還原：`docker compose up -d --no-deps api`，api 回到 `dotnet watch`。
  - 就緒判斷：Release 首次啟動需完整編譯，時間不固定，所以切換、還原後都以輪詢判斷就緒（從主機執行 `curl -fsS http://localhost:<主機 port>/api/events`（主機 port 以 `docker compose port api 8080` 取得；預設 8080，`.env` 的 `API_HOST_PORT` 可改） 回 200），不用固定等待時間。
  - 組態確認：讀 api 容器 PID 1 的指令列（`docker compose exec api sh -c "tr '\\0' ' ' < /proc/1/cmdline"`）。不用 `ps`，因為 SDK image 不保證有安裝；只看 PID 1，因為 `dotnet run` 本身帶 `-c Release`，它啟動的子程序不帶。
  - service name 仍是 `api`，k6 位址維持寫死的 `http://api:8080`，不需要新增目標。
  - k6 一律以 `docker compose --profile loadtest run --rm --no-deps k6 ...` 執行：k6 有 `depends_on: api`，不帶 `--no-deps` 時 Compose 會依預設 compose 檔比對 api 設定，發現與 Release 覆寫不同就把 api 重建回 `dotnet watch`。2026-10-05 實作時第一次 Release 批次因此全部打在 Debug 上（結果移到 `loadtest/.output/invalid-release-attempt/`、整批重跑，報告註明）；同時改為每一次執行前都確認 PID 1 指令列，而非每批一次。
  - `ASPNETCORE_ENVIRONMENT` 仍是 Development（seeder 與開發設定需要），報告註明「Release 組態 + Development 環境」，不是完整的正式環境設定。
  - Release 的建置產出寫到既有 named volume 下的 `bin/Release`、`obj/Release`，不與 Debug 產出衝突。
- **替代方案**：
  - 新增獨立的 `api-release` 服務：k6 要多一個目標位址，破壞「位址寫死」的安全邊界，且兩個 api 同時連同一個 DB／Redis 會互相干擾（例如兩個 leader election 參與者）。
  - 在 Dockerfile 加 Release 階段：會改動開發用 image，超出範圍。
- **重複執行**：
  - 每個 scenario 在 Debug 與 Release 各執行 3 次，共 12 次；每次都由 setup 建立新的活動，彼此不共用庫存。
  - 每一次執行都必須通過全部門檻（決策 7），任一次失敗該組態的該 scenario 即判未通過。
  - §5 目標的最終判定以 Release 為準；Debug 列為對照，不影響判定。
  - 報告列出每次的 P95／P99，並給出 3 次的中位數、最小值、最大值。
- **執行批次與 token 有效期**：
  - **每一次**執行前都重跑 seeder，而不是每批一次：一批 6 次執行，每次 setup 最多 180 秒、verify 從 85 秒開始，加上間隔，整批可能接近或超過 30 分鐘。單次執行最長約 5 分鐘，遠低於 30 分鐘，token 不會在執行中途過期。
  - 因此任何失敗（包含 401）都計入該次結果，不得以「token 過期」為由排除或重跑；若確實發生 401，代表重現步驟沒有照做，報告須如實記錄。
  - 每次執行之間至少間隔 60 秒，避免同一買家連續兩次執行落在同一個 per-member 限流視窗（20 次／60 秒；單次執行每位買家最多 2 個請求，實際不會觸發，但間隔讓結果不受上一次殘留負載影響）。
- **summary 檔名**：`/output/<scenario>-<LT_API_BUILD>-run<LT_RUN>-summary.json`，同一次執行的 summary、`runVerdict` 與 verify 結果（`verify_quantity_sold`、失敗原因）都在這一個檔案裡，不需要另外關聯 verify 輸出。檔案內另記 `runInfo`（scenario、`LT_API_BUILD`、`LT_RUN`、開始的 UTC 時間）。
  - `LT_RUN` 只接受 `1`、`2`、`3`。init 階段以 `open()` 試讀目標檔名，**檔案已存在時 setup `fail()`**：不允許覆寫既有結果，落實「失敗不得以重跑取代」。
  - 故障注入執行（設定 `LT_FAULT`）不需要 `LT_RUN`，檔名改為 `/output/<scenario>-fault-<LT_FAULT>-summary.json`，與正式結果分開；`LT_API_BUILD` 非法或未設定時，`<LT_API_BUILD>` 用 `invalid`；`LT_RUN` 非法或未設定時，`run<LT_RUN>` 用 `run-invalid`；`handleSummary` 不得拋例外（實測拋例外時 k6 仍 exit 0 且不寫檔），出錯時改寫一份 `runVerdict.passed == false` 並列出錯誤的 summary。這兩種 invalid／run-invalid 檔名都不會與 12 個正式檔名衝突，且都含 `-run`，會被 tasks 5.3 的整理步驟移走；故障注入檔名不含 `-run`、會留在 `/output`，但不在彙整腳本讀取的固定檔名內，不影響結果。

### 決策 11：報告資料表由彙整腳本產生

- **為什麼**：報告的數字若由人手抄，LT-REPORT-001（數字等於 summary JSON）只能靠肉眼比對。改由腳本從 summary JSON 產生資料表，報告直接貼上，比對就變成可重複的 `diff`。
- **做法**：
  - 純函式 `aggregateRuns(runs)` 與 `renderReportTables(aggregate)` 放在 `loadtest/lib/aggregate.js`：每組（組態 × scenario）列出 3 次的 P95／P99、成功數、409 數、5xx 數、`QuantitySold`、`place_order_send_offset_ms` 最大值減最小值、`runVerdict`；計算 P95／P99 的中位數、最小值、最大值；3 次 `runVerdict.passed` 都為 true 才標「通過」，否則標「未通過」並列出第幾次、哪些失敗原因；缺檔視為該次未通過。
  - **缺欄位不得預設為 0**：`evaluateRunResult` 與 `aggregateRuns` 讀不到預期的 metric 或欄位（例如 `http_req_duration{name:place-order}` 的 `p(95)`、`p(99)`，`orders_created` 的 `count`，threshold 的 `ok`）時，該次判為未通過，失敗原因寫明缺少哪個欄位；表格中該格顯示「缺少」，不顯示 0，也不參與中位數等統計。原因：自我測試的假資料是依我們對 k6 summary 結構的理解手寫的，若真實輸出的 key 寫法不同，預設為 0 會讓錯誤靜默通過；改成明確失敗，第一次彙整真實結果時就會暴露。
    - 從未遞增的自訂 Counter 可能不會出現在 summary 中（例如沒有任何 5xx 時的 `place_order_5xx`）。為了不必在彙整端猜「沒出現是 0 還是缺漏」，兩支壓測腳本在 `setup()` 開頭對全部 7 個自訂 Counter（`orders_created`、`place_order_conflict`、`place_order_5xx`、`place_order_unexpected`、`confirm_failed`、`buyer_iterations_completed`、`oversell_check_failed`）各呼叫一次 `add(0)`，保證它們都會出現在 summary；彙整端因此可以一律把「沒出現」當成缺少。
    - Gauge `verify_quantity_sold` 與 Trend `place_order_send_offset_ms` **刻意不補 0**：Gauge 必須保持「verify 沒跑到就缺少」，才能判為未通過（決策 7）；Trend 補 0 會污染統計值。
  - 檔案讀取函式 `loadRunSummaries(baseDir)` 也放在 `loadtest/lib/aggregate.js`：只讀白名單 `{count-ticket,seat-ticket}-{debug,release}-run{1,2,3}-summary.json` 展開後的 12 個檔名（`count-ticket-debug-run1-summary.json` … `seat-ticket-release-run3-summary.json`），不用 glob，所以 `precheck/`、故障注入與 `invalid` 檔案不會被納入。檔案不存在時記為缺檔，不拋例外。
  - 執行腳本 `loadtest/aggregate.js`：在 init 階段呼叫 `loadRunSummaries('/output')`，再呼叫上述函式，由 `handleSummary` 寫出 `/output/report-tables.md`。不發 HTTP 請求。
  - **檔案流程測試**：`loadtest/tests/aggregate-flow.test.js` 走與 `aggregate.js` 相同的 `loadRunSummaries` → `aggregateRuns` → `renderReportTables` → `handleSummary` 寫檔流程，只把 `baseDir` 換成唯讀的 fixture 目錄 `/scripts/tests/fixtures/aggregate/`，輸出寫到 `/output/aggregate-flow-report-tables.md`。這樣不需要新增 `__ENV`，也不會碰到正式結果。fixture 是依 k6 summary 結構手寫的；它與真實結構是否一致，由 tasks 6.1 對真實結果執行彙整時「不得出現任何『缺少』」來確認。
  - P99 需要在兩支壓測腳本的 options 設定 `summaryTrendStats` 包含 `p(99)`。409 數由 Counter `place_order_conflict` 提供。
- **替代方案**：寫一個 .NET 彙整工具並用 xUnit 測試。會多一個專案，且要重寫 k6 summary 的資料結構；k6 腳本已能 `open()` JSON，留在同一個 runtime 較簡單。

## Risks / Trade-offs

- [本機 WSL2 時鐘偶發偏快、倒退（見 memory）] → 對策：`SalesStartAtUtc` 提前 1 分鐘、`startAt` 預留 10 秒，並在報告註明時間相關的限制。
- [token 30 分鐘過期] → 對策：每一次執行前都重跑 seeder（決策 10）；setup 遇到 401 會依 spec 中止。
- [本機資源讓 P95 失真（api、db、redis、k6 共用同一台機器）] → 對策：報告記錄主機規格與 Docker 資源配額，並在判讀中標註限制。
- [dev DB 累積測試資料（每次執行新增 2000 席活動）] → 對策：明確列為 Non-Goal，活動 Title 以 `[LoadTest] ` 開頭方便辨識，報告附上手動清理建議。這可能讓活動列表快取與管理介面變雜，屬於可接受的取捨。
- [「報表 = k6 成功數」無法用 threshold 表達] → 對策：`handleSummary` 以 `evaluateRunResult` 自動判定並寫入 `runVerdict`；單次通過須 exit code 0 且 `runVerdict.passed`（決策 7）。
- [api 平時以 `dotnet watch`（Debug）執行] → 對策：加 Release 組態對照（決策 10），§5 目標以 Release 結果判定。
- [`LT_API_BUILD` 標籤可能與實際組態不符（k6 無法從 API 得知組態）] → 對策：每一次執行前讀 api 容器 PID 1 的指令列確認組態，k6 以 `--no-deps` 執行避免 api 被重建（決策 10），並把結果記入報告。

## Migration Plan

- 純新增工具與設定，不涉及 DB migration。
- 回復方式：刪除 `tools/`、`loadtest/` 與 compose 的 k6 服務即可。
- 壓測資料可以留在 dev DB，不影響產品。

## Open Questions

（無）
