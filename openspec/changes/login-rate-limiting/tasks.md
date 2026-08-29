## 1. Application 層

- [x] 1.1 新增 `LoginRateLimitingOptions`（`src/ProjectC.Application/Common/`），結構比照既有 `RateLimitingOptions` 既定寫法：`SectionName`、`[Range(1, int.MaxValue)]` DataAnnotations、安全預設值（`PermitLimit = 5`、`WindowSeconds = 60`），不需要 `ValidateOnStart()`（見 design.md 決策 2）

## 2. WebApi 層

- [x] 2.1 新增 `LoginRateLimiterPartitioning`（`src/ProjectC.WebApi/`，public static class），內含 `GetPartitionKey(HttpContext httpContext)` 方法，回傳 `httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown"`——**MUST** 抽成獨立可單元測試的方法，不得沿用既有 `CreateMemberPartition` 那種內嵌於 `Program.cs` 的 local function 寫法（見 design.md 決策 3「可測試性考量」）
- [x] 2.2 `Program.cs` 新增 `LoginRateLimitingOptions` 的註冊，**逐行比照既有 `RateLimitingOptions` 的三行寫法**（`Program.cs` 現行 L122-126，在 `builder.Build()` 之前、與既有 `AddRateLimiter` 呼叫同一個註冊區塊）：
  ```csharp
  builder.Services
      .AddOptions<LoginRateLimitingOptions>()
      .Bind(builder.Configuration.GetSection(LoginRateLimitingOptions.SectionName))
      .ValidateDataAnnotations();
  builder.Services.AddSingleton(sp => sp.GetRequiredService<IOptions<LoginRateLimitingOptions>>().Value);
  ```
  第三行的 `AddSingleton` 是關鍵：它讓 `LoginRateLimitingOptions` 可以直接被 `RequestServices.GetRequiredService<LoginRateLimitingOptions>()` 取得（不用再包一層 `IOptions<T>`），2.3 的 partition 函式依賴這個註冊
- [x] 2.3 `Program.cs` 的 `AddRateLimiter` 內新增 `"login"` policy，**逐行比照既有 `CreateMemberPartition` 的既定寫法**（`Program.cs` 現行 L218-219、L245-256）：`rateLimiterOptions.AddPolicy("login", httpContext => CreateIpPartition(httpContext))`；`CreateIpPartition` 是與 `CreateMemberPartition` 同層級的 local static function，內容為：
  ```csharp
  static RateLimitPartition<string> CreateIpPartition(HttpContext httpContext)
  {
      var options = httpContext.RequestServices.GetRequiredService<LoginRateLimitingOptions>();
      var partitionKey = LoginRateLimiterPartitioning.GetPartitionKey(httpContext);

      return RateLimitPartition.GetFixedWindowLimiter(partitionKey, _ => new FixedWindowRateLimiterOptions
      {
          PermitLimit = options.PermitLimit,
          Window = TimeSpan.FromSeconds(options.WindowSeconds),
          QueueLimit = 0,
      });
  }
  ```
  **讀取時機是 lazy**：`options` 是在每次請求進入、`AddPolicy` 註冊的 partition 函式被呼叫時才透過 `httpContext.RequestServices` 解析，不是在 `AddPolicy` 註冊當下（app 啟動時）就 eager 讀取一次——與既有 `CreateMemberPartition` 完全相同的模式，不需要擔心 2.2 的 `AddOptions` 與 2.3 的註冊順序互相依賴（`AddRateLimiter` 只是註冊 policy 定義，不會在註冊當下就去解析 `LoginRateLimitingOptions`）
- [x] 2.3.1 `app.Build()` 之後（`Program.cs` 現行 L266 `app.Services.GetRequiredService<RateLimitingOptions>();` 同一個位置，緊接著再加一行），強制解析一次 unwrap 後的 `LoginRateLimitingOptions`：`app.Services.GetRequiredService<LoginRateLimitingOptions>();`（**MUST** 解析 unwrap 後的型別本身，而非只取得 `IOptions<LoginRateLimitingOptions>` wrapper，理由與既有 `RateLimitingOptions` 那行相同——觸發 2.2 第三行 `AddSingleton` 工廠內部的 `.Value` 存取，讓 DataAnnotations 驗證確實在啟動階段執行，不延遲到第一個進入端點的 HTTP 請求）
- [x] 2.4 `AuthController.Login` action 加上 `[EnableRateLimiting("login")]`

## 3. 前端

- [x] 3.1 `web/src/pages/buyer/LoginPage.vue` 的 `handleSubmit` catch block 新增 429 判斷：`if (error instanceof ApiError && error.status === 429) { errorMessage.value = '登入嘗試過於頻繁，請稍後再試'; return }`，比照 `EventDetailPage.vue` 對下單 429 的既有處理模式（見 design.md 決策 5），其餘錯誤情況維持現有 `toErrorMessage(...)` 邏輯不變

## 4. 後端測試

- [x] 4.1 單元測試：`LoginRateLimiterPartitioning.GetPartitionKey`——相同 `RemoteIpAddress` 回傳相同分區鍵、不同 `RemoteIpAddress` 回傳不同分區鍵（直接建構 `DefaultHttpContext` 並設定 `Connection.RemoteIpAddress`，不透過 `WebApplicationFactory`，對應 LRL-004）、`RemoteIpAddress` 為 `null` 時回傳 `"unknown"` 且不拋出例外。**測試限制**：這裡只驗證分區鍵推導邏輯本身（相同輸入→相同鍵、不同輸入→不同鍵），不是「兩個來源 IP 在 ASP.NET Core `RateLimiter` middleware 實際執行時各自維護獨立計數」的端到端驗證——`WebApplicationFactory`／`TestServer` 的 in-memory transport 對所有請求回報固定 `RemoteIpAddress`，無法透過建立多個 `HttpClient` 模擬不同來源 IP 來做端到端驗證（見 design.md 決策 3），此為可接受的測試取捨，非完整覆蓋
- [x] 4.2 整合測試：`api-rate-limiting` 能力 — 登入端點限流（LRL-001~003、005~006、010）。核心原則：任何共用同一個 factory 實例（`IClassFixture`）的測試方法之間，登入限流額度都 MUST 是寬鬆值，緊額度（`PermitLimit = 3`／`WindowSeconds = 2`）只能用在「每個測試方法各自獨立 factory 實例」的情境，避免測試方法互相消耗彼此額度（見 design.md 決策 6 問題 A／B）：
  - [x] 4.2.0 **修改 base class `CustomWebApplicationFactory`**（`tests/ProjectC.WebApi.Tests/TestSupport/CustomWebApplicationFactory.cs`）與新增共用 helper，依序完成兩件事（見 design.md 決策 6 點 1、3，兩邊程式碼逐字一致，照抄即可）：
    1. 新增 `internal static class TestHostConfiguration`（`tests/ProjectC.WebApi.Tests/TestSupport/TestHostConfiguration.cs`），內含 `public static void ApplyCommonTestConfiguration(IWebHostBuilder builder, string connectionString)`：把 `CustomWebApplicationFactory.ConfigureWebHost` 目前內聯的共用邏輯搬進來（`UseEnvironment("Testing")`、Jwt／Auth／TicketSigning 的 `AddInMemoryCollection` 覆寫、三個 `RemoveAll`（`DbContextOptions<ApplicationDbContext>`／`IDbContextOptionsConfiguration<ApplicationDbContext>`／`ApplicationDbContext`）+ `AddDbContext(connectionString)`）——**不**包含 `LoginRateLimiting`（那不是共用邏輯，見下一步）
    2. `CustomWebApplicationFactory.ConfigureWebHost` 改寫為：先呼叫 `TestHostConfiguration.ApplyCommonTestConfiguration(builder, _connectionString)`（取代原本的內聯程式碼，`_connectionString` 來源不變，仍是自己 `InitializeAsync` 啟動的容器），**接著另外呼叫一次** `builder.ConfigureAppConfiguration(...)` 加入寬鬆的 `LoginRateLimiting:PermitLimit = 1000`、`LoginRateLimiting:WindowSeconds = 60`（完整程式碼見 design.md 決策 6 點 1 的程式碼區塊）——這是解決「`AuthControllerTests` 等既有測試被 production 登入限流（`PermitLimit = 5`／`WindowSeconds = 60`）誤傷」的唯一位置，所有繼承 `CustomWebApplicationFactory` 的測試類別自動套用，不需個別子類別各自覆寫；**`RateLimitedWebApplicationFactory` 不需要任何修改**（繼承 base class 自動取得這個寬鬆值）
  - [x] 4.2.1 於 `OrdersRateLimitingTests` 新增一個測試方法（LRL-006，繼續使用既有 `IClassFixture<RateLimitedWebApplicationFactory>`，登入額度繼承自 4.2.0 的寬鬆預設值，故不受互相污染問題影響）：`CreateAuthenticatedMemberClientAsync()` 建立的同一個 `HttpClient` 先對 `place-order` 送出恰好 `PermitLimit` 次請求（`place-order` 額度到達上限邊界但仍允許，比照既有 `PlaceOrder_WithExactlyThePermitLimitRequests_...` 測試手法逐次斷言皆非 429，不需要再多送一次觸發 429），再對 `POST /api/auth/login` 送出請求，驗證不受 429 影響（意圖：證明兩個 policy 分區鍵語意不同、計數互不影響，而非登入額度本身寬鬆才通過）
  - [x] 4.2.2 新增 `LoginRateLimitTestDatabaseFixture`（`tests/ProjectC.WebApi.Tests/TestSupport/`，`IAsyncLifetime`，公開介面 `public string ConnectionString { get; private set; }`）：只負責啟動/停止一個共用的 Testcontainers Postgres 容器並跑 migration，`InitializeAsync` 內容器 `StartAsync` 與 migration 都完成後才對 `ConnectionString` 賦值，容器啟動邏輯搬移自 `CustomWebApplicationFactory.InitializeAsync` 既有的容器啟動程式碼（**不含任何限流器狀態**，純粹是資料庫連線的共用資源，`ConfigureWebHost` 相關邏輯不放在這裡）
  - [x] 4.2.3 新增 `LoginRateLimitedWebApplicationFactory`（`tests/ProjectC.WebApi.Tests/TestSupport/`）：完整寫法見 design.md 決策 6 點 3 的程式碼區塊——直接繼承 `WebApplicationFactory<Program>`（**不**繼承 `CustomWebApplicationFactory`，也不實作 `IAsyncLifetime`——xUnit 只對 `IClassFixture<T>`／`ICollectionFixture<T>` 建構注入的物件自動呼叫 `IAsyncLifetime`，這個類別是測試方法內手動 `new` 出來的，`IAsyncLifetime` 不會被呼叫），建構子簽名 `LoginRateLimitedWebApplicationFactory(string connectionString)`，公開常數 `LoginPermitLimit = 3`、`LoginWindowSeconds = 2`；`ConfigureWebHost` 內先呼叫 `TestHostConfiguration.ApplyCommonTestConfiguration(builder, connectionString)`（4.2.0 新增的共用方法），再用自己的 `ConfigureAppConfiguration` 疊加 `LoginRateLimiting:PermitLimit = LoginPermitLimit`、`LoginRateLimiting:WindowSeconds = LoginWindowSeconds`；`RateLimiting`（下單）不覆寫、維持 production 預設值。**注意**：這裡的 `LoginRateLimiting = 3` 不是「覆蓋」4.2.0 設定給 `CustomWebApplicationFactory` 的 `1000`——`LoginRateLimitedWebApplicationFactory` 不繼承 `CustomWebApplicationFactory`，兩者是各自獨立設定自己的值，不存在疊加順序問題；4.2.4 的測試方法第一次跑通後應額外確認耗盡額度確實回傳 429（而不是全部意外通過），作為「這個 factory 真的套用了緊額度 3、不是誤用了寬鬆值 1000」的間接驗證
  - [x] 4.2.4 新增 `LoginRateLimitingTests` 測試類別（`IClassFixture<LoginRateLimitTestDatabaseFixture>`——只取得共用的 `ConnectionString`，**不** `IClassFixture<LoginRateLimitedWebApplicationFactory>`）：每個測試方法內 `using var factory = new LoginRateLimitedWebApplicationFactory(_databaseFixture.ConnectionString); using var client = factory.CreateClient();`，各自獨立的 TestServer／限流器狀態。對 `POST /api/auth/login` 送出不存在的假帳密請求（只關心是否被限流中介軟體擋下，不需要真的登入成功），拆成三個測試方法（命名比照 `MethodName_Scenario_ExpectedResult` 慣例，比照既有 `OrdersRateLimitingTests` 方法命名風格）：`Login_WithRequestsUnderTheLimit_AllSucceedWithoutBeingRateLimited`（LRL-001）送出 `PermitLimit - 1` 次，逐次驗證皆非 429；`Login_WithExactlyThePermitLimitRequests_AllSucceedWithoutBeingRateLimited`（LRL-002）送出恰好 `PermitLimit` 次，逐次驗證皆非 429；`Login_AfterTheWindowResets_RequestsAreAllowedAgain`（LRL-005）先送滿 `PermitLimit` 次觸發 429（**MUST** 先斷言最後一次請求確實收到 429，附清楚的 assertion message，例如「時間窗重置前置條件：額度應已耗盡」，確認耗盡後才開始計時，不要在請求送出前就搶跑 `Task.Delay`），再 `Task.Delay(WindowSeconds + 1 秒)`，之後再送一次並斷言恢復非 429（同樣附清楚的 assertion message，比照既有 `PlaceOrder_AfterTheWindowResets_RequestsAreAllowedAgain` 的斷言訊息風格）
  - [x] 4.2.5 LRL-003（`LoginRateLimitingTests`，同樣各自獨立 factory 實例）：先用 `AuthTestHelper.RegisterAsync` 建立一個有效帳號並記住其 email／password（呼叫 `/api/auth/register`，不受 `login` policy 影響，不消耗登入限流額度）；送出 `PermitLimit` 次錯誤密碼的登入請求耗盡額度，第 `PermitLimit + 1` 次改用正確密碼送出，驗證仍回傳 429 且回應 body 不包含 `accessToken`／`refreshToken` 欄位——驗證的是可觀察的 HTTP 結果，不透過 mock 驗證 `LoginHandler` 內部未被呼叫（見 design.md 決策 6 第 5 點、spec.md Requirement 措辭）
  - [x] 4.2.6 LRL-010（`LoginRateLimitingTests`，同樣各自獨立 factory 實例）：耗盡登入額度後驗證拒絕回應的完整格式，比照既有 `PlaceOrder_WhenRateLimited_ReturnsProblemDetailsWithRetryAfterHeader` 手法——HTTP 狀態碼 429、包含 `Retry-After` 標頭、body 為 `ProblemDetails` 且 `status = 429`、`title = "TooManyRequests"`（與既有 `OnRejected` callback 寫死的值一致，非僅檢查欄位存在）、`traceId` 存在
- [x] 4.3 整合測試：`appsettings` 驗證行為（比照既有 `RateLimitingOptionsFailFastTests.cs` 手法）——`LoginRateLimitingOptions` 缺漏時採用預設值仍可正常啟動（LRL-007）、設定為 0 或負數時於啟動階段擋下（LRL-008）

## 5. 前端測試

- [x] 5.1 元件測試：`LoginPage.vue` 對 429 的錯誤處理（對應 buyer-web-ui LRL-009），比照既有 `EventDetailPage.test.ts` 對下單 429 情境的測試手法，驗證顯示「登入嘗試過於頻繁，請稍後再試」而非後端原始 `title` 字串

## 6. Spec 同步確認

- [x] 6.1 實作完成後比對 `openspec/changes/login-rate-limiting/specs/` 與最終實作行為是否一致，如有偏差回報並更新 spec
- [x] 6.2 更新 `docs/project-scope.md` Phase 2 Should 清單第 48 行「登入 Rate limiting（防暴力破解）」，比照第 46 行「Rate limiting / 基礎排隊機制」既有寫法，標示為已完成並附上對應 openspec archive 路徑

## 7. AC ↔ Test Traceability

| AC ID | Requirement | Scenario | Test task |
|---|---|---|---|
| LRL-001 | 登入端點的請求頻率限制 | 登入請求次數未超過限制 | 4.2.4 |
| LRL-002 | 登入端點的請求頻率限制 | 恰好第 PermitLimit 次請求仍允許 | 4.2.4 |
| LRL-003 | 登入端點的請求頻率限制 | 第 PermitLimit+1 次請求起拒絕（含帳密正確仍拒絕、不核發 Token） | 4.2.5 |
| LRL-004 | 登入端點的請求頻率限制 | 不同來源 IP 的限流各自獨立 | 4.1（測試限制：僅驗證分區鍵推導，見 4.1 說明） |
| LRL-005 | 登入端點的請求頻率限制 | 時間窗重置後恢復可請求 | 4.2.4 |
| LRL-006 | 登入端點的請求頻率限制 | 登入端點的限流與下單端點的限流互不影響 | 4.2.1 |
| LRL-007 | 登入端點限流設定值須為正數，缺漏時採用明確預設值 | 設定缺漏時採用預設值 | 4.3 |
| LRL-008 | 登入端點限流設定值須為正數，缺漏時採用明確預設值 | 設定值為 0 或負數時擋下 | 4.3 |
| LRL-009 | 買家可透過介面註冊與登入（buyer-web-ui） | 登入因請求頻率限制被拒絕 | 5.1 |
| LRL-010 | 登入端點的請求頻率限制 | 登入限流拒絕回應遵循統一格式 | 4.2.6 |
