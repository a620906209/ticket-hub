## Context

`POST /api/auth/login`（`AuthController.Login`）目前沒有任何請求頻率限制，攻擊者可以無限制地對這個端點嘗試帳密組合。`rate-limiting-queue` 已在 `Program.cs` 建立 `Microsoft.AspNetCore.RateLimiting`（Fixed Window）的既定模式：`AddRateLimiter` 註冊多個獨立命名的 policy、共用一個 `OnRejected` callback 統一輸出 `ProblemDetails`（429 + `Retry-After`）、`RateLimitingOptions` 承載可設定的 `PermitLimit`／`WindowSeconds`。既有的 `place-order`／`confirm-order` 兩個 policy 都是以 `httpContext.User.GetMemberId()`（JWT Claims）作為分區鍵。

登入端點的根本差異：呼叫當下使用者**尚未通過驗證**，`httpContext.User` 沒有任何 Claims，無法沿用「以已登入會員 Id 分區」的既定做法。這是本次設計要解決的核心問題。

## Deployment Prerequisite（強制條件，非選配）

本次以來源 IP 分區的限流機制，正確性依賴 `HttpContext.Connection.RemoteIpAddress` 確實反映真實用戶端位址。這個前提**只在應用程式直接接收用戶端連線（無反向代理／CDN）的部署拓樸下成立**——這也是本專案目前唯一的部署拓樸（Docker Compose，`api` 服務直接對外）。

- **本次支援條件**：僅支援應用程式直接接收用戶端連線的部署方式。
- **禁止事項**：禁止在未設定 `ForwardedHeadersOptions`（且限定 `KnownProxies`／`KnownNetworks` 為實際可信任的 proxy 位址，不得信任任意來源自帶的 `X-Forwarded-For`）的情況下，將本機制部署在反向代理或 CDN 之後——否則所有請求的 `RemoteIpAddress` 會變成 proxy 自身 IP，等同所有使用者共用同一個分區、限流機制對真實攻擊者實質失效，且任何使用者的異常行為都可能誤傷同一 proxy 後的所有其他使用者。
- **未來部署拓樸變更時**：若導入反向代理或 CDN，必須先開一個變更明確設定 `ForwardedHeadersOptions`（可信任 proxy 清單）並重新驗證 LRL-004（不同來源 IP 各自獨立）在該拓樸下仍然成立，才能視為本機制在新拓樸下依然有效；在完成之前，視為本機制暫時停用等級的防護（不得假設它仍在生效）。

## Goals / Non-Goals

**Goals:**
- 對 `POST /api/auth/login` 套用速率限制，讓自動化腳本無法無限制地對這個端點高頻發送請求
- 沿用既有 429 `ProblemDetails` + `Retry-After` 回應格式與 `OnRejected` 機制，不新增第二套錯誤格式
- 沿用既有的設定驗證慣例（DataAnnotations、安全預設值）

**Non-Goals:**
- **不做帳號鎖定機制**（account lockout：連續失敗 N 次後鎖定該帳號一段時間）——這需要在 `Member` Domain Entity 增加狀態欄位與對應的解鎖流程，屬於與速率限制不同的防護手段，複雜度明顯超出「基礎」等級，留待未來評估
- **不做 CAPTCHA**——`docs/project-scope.md` 已明確列為 Phase 3 Could，本次不處理
- **不做以 Email 分區的限流**——見決策 1，屬於技術複雜度與既定慣例不符的取捨，留在 Open Questions
- **不擴及其他 Auth 端點**（`register`／`refresh`／`logout`／`password-reset/*`）——本次範疇限定為登入端點，其餘端點若未來評估仍有濫用風險，留待後續變更處理

## Decisions

**1. 分區鍵僅使用來源 IP 位址（`HttpContext.Connection.RemoteIpAddress`），不採用以 Email 分區**
- **技術限制是主要理由**：ASP.NET Core 的 `AddRateLimiter` policy partitioner（`AddPolicy(name, Func<HttpContext, RateLimitPartition<TKey>>)`）在 pipeline 中執行的時間點早於 MVC model binding——`UseRateLimiter()` 中介軟體攔截請求時，`LoginRequest`（含 `Email`）尚未被解析出來，request body 仍是原始未讀取的 stream。要以 Email 分區，partition callback 內必須手動 `EnableBuffering()` 並同步讀取、反序列化 body（sync-over-async 讀取，例如 `.GetAwaiter().GetResult()`），這在本專案目前的中介軟體慣例裡沒有先例，且引入額外的正確性風險（body 讀取失敗時如何降級、超大 body 的濫用防護）與效能成本（每次請求都要完整讀入 body 才能決定是否限流）
- **即使解決了技術限制，單獨以 Email 分區本身防護力也有限**：攻擊者可以在每次嘗試時送出不同（甚至隨機不存在）的 Email，讓每次請求都落在全新的 bucket，繞過以 Email 為單位的計數。真正有效的組合是「IP 分區 + Email 分區同時套用」（兩者都要通過才放行，比照決策 3 的獨立 policy 模式），而不是「只用 Email 分區取代 IP 分區」——IP-based 是這個組合裡技術上簡單許多、且能獨立生效的一半，Email-based 是需要額外中介軟體工程才能疊加的加強層，故本次先做前者，後者列入 Open Questions
- **已知取捨（見 Risks）**：分散在多個不同 IP 位址、針對單一帳號的目標式攻擊不會被本次機制擋下（因為每個 IP 各自有獨立額度）；本機開發環境（Docker Compose，無反向代理）下 `RemoteIpAddress` 直接反映客戶端位址，符合上方「Deployment Prerequisite」列出的支援條件；若未來部署在反向代理／CDN 之後，須依該節規則另外設定 `ForwardedHeadersOptions` 才能取得真實用戶端 IP

**2. 新增獨立的 `LoginRateLimitingOptions`，不沿用既有 `RateLimitingOptions`**
- `RateLimitingOptions`（`PermitLimit = 20`、`WindowSeconds = 60`）是為下單端點設計的合理呼叫頻率——買家瀏覽/選位/下單過程中確實可能在短時間內送出好幾次請求。登入端點的正常使用頻率遠低於此（一般使用者登入失敗頂多重試兩三次），若沿用同一組數值，20 次／分鐘的額度形同虛設，對暴力破解沒有實質防護意義
- 新增 `LoginRateLimitingOptions`（`src/ProjectC.Application/Common/`），結構完全比照 `RateLimitingOptions` 既定寫法（`SectionName`、`[Range(1, int.MaxValue)]` DataAnnotations、有安全預設值、不需要 `ValidateOnStart`，理由與 `RateLimitingOptions` 相同：設定缺漏時仍要能正常運作，只有「設定但為 0 或負數」才需要被擋下）：
  - `PermitLimit = 5`、`WindowSeconds = 60`——起始猜測值：一般使用者打錯密碼重試兩三次仍在額度內，同時讓自動化腳本的嘗試速率被壓到每分鐘最多 5 次。這是「基礎」等級的起始猜測、不是最終調優值，比照 `RateLimitingOptions`／`PurchaseQueueOptions` 起始值「先求可運作、可依上線後測試結果調整」的既定定位
- 不與 `RateLimitingOptions` 共用同一個類別的第二個理由：兩者的分區鍵語意不同（IP vs 已登入會員 Id），即使數值未來意外設成相同，共用類別會讓設定檔的欄位命名混淆兩種不同的分區策略，不利於之後各自獨立調整
- **命名與型別**：`LoginRateLimitingOptions` 的欄位命名（`PermitLimit`／`WindowSeconds`，整數秒數）比照既有 `RateLimitingOptions` 類別本身的實際 C# 屬性命名，而非比照 `rate-limiting-queue` archived spec 文件prose 中使用的抽象措辭（`Window`／`TimeSpan`）——後者是 spec 文件用語，前者才是實際程式碼型別；本次 spec.md 對登入限流設定的描述直接採用與程式碼一致的 `WindowSeconds`（整數秒數）用語，避免實作者誤以為需要改用 `TimeSpan` 型別

**3. 新增獨立命名的 `login` policy，套用方式與註冊時機比照 `place-order`／`confirm-order` 的既定寫法**
- `Program.cs` 的 `AddRateLimiter` 呼叫內新增 `rateLimiterOptions.AddPolicy("login", httpContext => CreateIpPartition(httpContext))`，`CreateIpPartition` 讀取 `httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown"` 作為分區鍵字串（`RemoteIpAddress` 理論上不會是 `null`，但型別上是 nullable，需要 fallback 避免 `NullReferenceException`；`"unknown"` 這個 fallback bucket 的存在本身可接受——若真的觸發代表底層連線資訊有異常，不影響正常請求）
- `AuthController.Login` action 加上 `[EnableRateLimiting("login")]`，比照既有 controller action 套用 policy 的既定寫法
- `LoginRateLimitingOptions` 註冊方式比照 `RateLimitingOptions`：`services.AddOptions<LoginRateLimitingOptions>().Bind(section).ValidateDataAnnotations()`（不鏈 `ValidateOnStart()`），並在 `app.Build()` 之後同樣強制解析一次 unwrap 後的 `LoginRateLimitingOptions`（而非只取得 `IOptions<LoginRateLimitingOptions>` wrapper），觸發 DataAnnotations 驗證於啟動階段執行（比照 `RateLimitingOptions` 既定寫法與其背後的理由，見 rate-limiting-queue design.md 決策 1）。`Program.cs` 執行順序：`AddOptions<LoginRateLimitingOptions>` 註冊 → `AddRateLimiter` 內的 `"login"` policy 註冊（讀取同一個 options）→ `app.Build()` → 強制解析 `LoginRateLimitingOptions` 觸發驗證（見 tasks.md 2.2～2.3.1）
- **可測試性考量（與既有 `CreateMemberPartition` 的關鍵差異）**：既有 `CreateMemberPartition` 是 `Program.cs` 內的 local function，之所以不需要獨立單元測試，是因為 `RL-005`（不同會員各自獨立）可以直接用兩個不同登入身份的 `HttpClient` 走完整 HTTP pipeline驗證——分區鍵（會員 Id）天然隨每個已驗證的 client 不同，不需要碰觸底層連線資訊。IP 分區沒有這個天然變因：`WebApplicationFactory`／`TestServer` 的 in-memory transport 對所有請求回報的 `Connection.RemoteIpAddress` 是固定值，無法透過建立多個 `HttpClient` 來模擬不同來源 IP。因此 `CreateIpPartition` 的分區鍵推導邏輯 MUST 抽成一個獨立、可直接建構 `DefaultHttpContext` 呼叫的 public static 方法（例如 `LoginRateLimiterPartitioning.GetPartitionKey(HttpContext)`，置於 `ProjectC.WebApi`），不能沿用 `CreateMemberPartition` 那種內嵌 local function 的寫法——LRL-004（不同來源 IP 各自獨立）改在單元測試層級，直接建構兩個 `Connection.RemoteIpAddress` 不同的 `DefaultHttpContext` 驗證回傳的分區鍵不同；LRL-001~003／005／010／007／008 則利用「同一個 `WebApplicationFactory` 建立的所有請求天然共用同一個模擬 IP」這個特性，直接end-to-end 驗證「同一分區（即測試環境下的所有請求）的限流行為」；但這個 factory 不能是既有 `RateLimitedWebApplicationFactory` 本身，須用獨立的 fixture 隔離，避免與既有下單限流測試互相污染，細節見決策 6

**4. 429 回應格式完全沿用既有 `OnRejected` callback，不另外寫一套**
- `AddRateLimiter` 是應用程式層級只註冊一次，`rateLimiterOptions.OnRejected` 是全域唯一的一個 callback，新增的 `login` policy 與既有的 `place-order`／`confirm-order` 共用同一個 `OnRejected`，回應格式（`ProblemDetails`、`Status = 429`、`Title = "TooManyRequests"`、`traceId` extension、`Retry-After` 標頭）自然完全一致，不需要新增任何程式碼

**5. 前端登入頁對 429 的處理，比照 `EventDetailPage.vue` 對下單 429 的既定模式**
- `ApiError.message` 目前的邏輯是 `problem?.detail ?? problem?.title ?? 通用訊息`——後端 429 回應只設定 `title = "TooManyRequests"`、不設定 `detail`，若不特別處理，`LoginPage.vue` 會直接顯示英文的 `"TooManyRequests"` 字串給使用者，不符合 CLAUDE.md「所有回應統一繁體中文」的要求
- 不修改後端 `Title`（維持它作為前端可穩定判斷的機器可讀字串，比照 `EventDetailPage.vue` 對 `QueueAdmissionRequired`／`TooManyRequests` 的既定處理原則：前端依 `status`／`title` 分流，不依賴 `Title` 的文字內容本身作為使用者可讀訊息）
- `LoginPage.vue` 的 `handleSubmit` catch block 新增：
  ```ts
  if (error instanceof ApiError && error.status === 429) {
    errorMessage.value = '登入嘗試過於頻繁，請稍後再試'
    return
  }
  ```
  其餘錯誤情況維持現有 `toErrorMessage(error, '登入失敗，請確認帳號密碼是否正確')` 邏輯不變

**6. 登入限流整合測試的 Fixture 隔離策略——兩層問題都要處理**
- **問題 A（既有測試被 production 登入限流誤傷）**：一旦 `Program.cs` 加上全域的 `"login"` policy，**所有**使用 `CustomWebApplicationFactory`（或其子類別）的既有整合測試都會受影響，不只 `OrdersRateLimitingTests`。`AuthControllerTests`（`IClassFixture<CustomWebApplicationFactory>`，直接使用 base class、未覆寫 `LoginRateLimiting`）單一測試類別內就有十幾個測試方法呼叫登入 API（`Login_WithCorrectCredentials`／`Login_WithWrongPassword`／`Login_WithDeactivatedAccount`(2 次)／`Refresh_*`／`Logout_*`（各自透過 `RegisterAndLoginAsync` 呼叫 1 次）／`PasswordReset_RequestThenConfirmWithValidToken`(2 次) 等），累計呼叫次數遠超過 production 預設的 `PermitLimit = 5`／`WindowSeconds = 60`，且同一個 `IClassFixture` 實例讓這些測試方法共用同一個模擬來源 IP、同一個限流器狀態，一旦在 60 秒內執行完（xUnit 同類別測試方法預設循序執行，正常情況下遠快於 60 秒），後面的測試方法會收到非預期的 429，而不是原本預期的 200／401／403
- **問題 B（新的 `LoginRateLimitingTests` 類別內部仍會互相污染）**：若 `LoginRateLimitingTests` 用 `IClassFixture<LoginRateLimitedWebApplicationFactory>` 讓 LRL-001／002／003／005／010 這幾個測試方法共用同一個 factory 實例，就是共用同一個 TestServer、同一個模擬 IP、同一個 `PermitLimit = 3`／`WindowSeconds = 2` 限流器狀態——測試方法之間會互相消耗彼此的額度，出現測試順序依賴（例如 LRL-002 一開始就被前一個測試耗盡的額度擋下、LRL-005 的時間窗起點被前一個測試影響）。這與問題 A 本質相同：只要多個測試方法共用同一個 factory 實例，就無法避免互相污染
- **決定**：
  1. **修改 base class `CustomWebApplicationFactory` 本身**（不是子類別），加入寬鬆的 `LoginRateLimiting:PermitLimit = 1000`、`LoginRateLimiting:WindowSeconds = 60` 覆寫值（時間窗沿用與 production 預設相同的 60 秒量級，只放大額度，不縮短視窗，避免視窗長度本身成為另一個變因）。**這個覆寫值不放進點 3 新增的共用方法 `TestHostConfiguration.ApplyCommonTestConfiguration`**（那個方法只負責 Jwt／Auth／TicketSigning／DbContext 這幾項兩個 factory 都要用到的共用設定；`LoginRateLimiting` 在兩個 factory 是不同的值、彼此也不繼承，不算共用邏輯，不該放進共用方法），而是在 `CustomWebApplicationFactory.ConfigureWebHost` 內，緊接著呼叫完 `ApplyCommonTestConfiguration` 之後，**另外自己呼叫一次** `builder.ConfigureAppConfiguration(...)` 加入這組值：
     ```csharp
     protected override void ConfigureWebHost(IWebHostBuilder builder)
     {
         TestHostConfiguration.ApplyCommonTestConfiguration(builder, _connectionString);

         builder.ConfigureAppConfiguration((_, configBuilder) =>
         {
             configBuilder.AddInMemoryCollection(new Dictionary<string, string?>
             {
                 ["LoginRateLimiting:PermitLimit"] = "1000",
                 ["LoginRateLimiting:WindowSeconds"] = "60",
             });
         });
     }
     ```
     這是唯一能同時解決問題 A 的做法——`AuthControllerTests`、`OrdersRateLimitingTests`／`RateLimitedWebApplicationFactory`，以及任何現在或未來繼承 `CustomWebApplicationFactory` 的測試類別，都自動取得一個不會被自己的正常測試流程意外觸發的登入限流額度，不需要每個子類別各自複製一份覆寫；**`RateLimitedWebApplicationFactory` 不需要修改**（繼承 base class 即自動取得這個寬鬆值，先前草稿要求它自己額外覆寫的部分予以撤銷）。**`LoginRateLimitedWebApplicationFactory`（點 3）不繼承 `CustomWebApplicationFactory`**，所以它自己的 `LoginRateLimiting:PermitLimit = 3` 不是在「覆蓋」這裡的 1000——兩者是互不相關的兩個類別，各自只設定一次自己的值，不存在誰蓋過誰的疊加順序問題（這點回應第三輪／人類問到的「覆寫順序」疑慮：疑慮的前提是誤以為兩者有繼承關係，實際設計沒有）
  2. **不對登入限流本身的測試使用 `IClassFixture` 共用 factory**：`LoginRateLimitingTests` 內每個測試方法（LRL-001／002／003／005／010）各自 `new` 一個全新的 `LoginRateLimitedWebApplicationFactory` 實例（`using var factory = new LoginRateLimitedWebApplicationFactory(connectionString); using var client = factory.CreateClient();`），確保每個測試方法擁有獨立的 TestServer、獨立的 DI container、獨立的 in-memory rate limiter 狀態，不受其他測試方法執行順序或殘留額度影響——這是唯一能徹底排除問題 B 的做法（比照 spec-reviewer 建議的「每個測試建立獨立的 factory / host」）
  3. **容器啟動成本不能跟著每個測試方法重複付，且 `LoginRateLimitedWebApplicationFactory` 不繼承 `CustomWebApplicationFactory`**：`CustomWebApplicationFactory` 目前透過 `IAsyncLifetime` 自行啟動/停止一個 Testcontainers Postgres 容器（`InitializeAsync`／`DisposeAsync`），若每個測試方法都各自啟動一個全新容器，測試會明顯變慢（容器啟動通常數秒起跳）；`LoginRateLimitedWebApplicationFactory` 又是在測試方法內用一般建構子 `new` 出來、xUnit **不會**對它自動呼叫 `IAsyncLifetime`（xUnit 只對 `IClassFixture<T>`／`ICollectionFixture<T>` 建構注入的物件自動呼叫 `InitializeAsync`／`DisposeAsync`，手動 `new` 的物件不會），所以它本來就不能靠 `IAsyncLifetime` 取得連線字串，必須在建構子當下就已經知道連線字串。因此把「容器生命週期」與「WebApplicationFactory／TestServer 生命週期」完全拆開，兩個類別之間用**組合**而非繼承共用設定邏輯（比照 CLAUDE.md「優先組合而非繼承」）：
     - 新增 `LoginRateLimitTestDatabaseFixture`（`IAsyncLifetime`，`tests/ProjectC.WebApi.Tests/TestSupport/`）：只負責啟動/停止一個共用的 Testcontainers Postgres 容器並跑 migration，公開介面為 `public string ConnectionString { get; private set; }`（`InitializeAsync` 內容器 `StartAsync` 完成、migration 跑完後賦值；`DisposeAsync` 停止容器），容器啟動邏輯搬移自 `CustomWebApplicationFactory.InitializeAsync` 既有的容器啟動程式碼；`LoginRateLimitingTests` 透過 `IClassFixture<LoginRateLimitTestDatabaseFixture>` 取得（容器啟動成本在整個測試類別只付一次，容器本身不含任何限流器狀態，不影響測試方法之間的隔離）
     - 新增共用的 internal static helper `TestHostConfiguration.ApplyCommonTestConfiguration(IWebHostBuilder builder, string connectionString)`（`tests/ProjectC.WebApi.Tests/TestSupport/TestHostConfiguration.cs`）：把 `CustomWebApplicationFactory.ConfigureWebHost` 目前內聯的共用邏輯（現行 L77-105）逐字搬過來，完整程式碼：
       ```csharp
       internal static class TestHostConfiguration
       {
           public static void ApplyCommonTestConfiguration(IWebHostBuilder builder, string connectionString)
           {
               builder.UseEnvironment("Testing");

               builder.ConfigureAppConfiguration((_, configBuilder) =>
               {
                   configBuilder.AddInMemoryCollection(new Dictionary<string, string?>
                   {
                       ["Jwt:Issuer"] = "ProjectC.Tests",
                       ["Jwt:Audience"] = "ProjectC.Tests.Client",
                       ["Jwt:SigningKey"] = "integration-test-signing-key-not-for-prod-32+",
                       ["Jwt:AccessTokenExpirationMinutes"] = "30",
                       ["Auth:RefreshTokenExpirationDays"] = "14",
                       ["Auth:PasswordResetTokenExpirationMinutes"] = "15",
                       ["TicketSigning:SigningKey"] = "integration-test-ticket-signing-key-not-for-prod-32+",
                   });
               });

               builder.ConfigureServices(services =>
               {
                   services.RemoveAll<DbContextOptions<ApplicationDbContext>>();
                   services.RemoveAll<IDbContextOptionsConfiguration<ApplicationDbContext>>();
                   services.RemoveAll<ApplicationDbContext>();

                   services.AddDbContext<ApplicationDbContext>(options => options.UseNpgsql(connectionString));
               });
           }
       }
       ```
       讓 `CustomWebApplicationFactory` 與 `LoginRateLimitedWebApplicationFactory` 都呼叫這個共用方法，而不是讓 `LoginRateLimitedWebApplicationFactory` 去繼承 `CustomWebApplicationFactory`（繼承會連帶繼承它「建構子直接建立一個 `PostgreSqlBuilder(...).Build()` 容器物件」的既有行為，這正是本次要避免的）
     - `LoginRateLimitedWebApplicationFactory` 直接繼承 `WebApplicationFactory<Program>`（不繼承 `CustomWebApplicationFactory`，也不實作 `IAsyncLifetime`），完整寫法：
       ```csharp
       public class LoginRateLimitedWebApplicationFactory : WebApplicationFactory<Program>
       {
           public const int LoginPermitLimit = 3;
           public const int LoginWindowSeconds = 2;

           private readonly string _connectionString;

           public LoginRateLimitedWebApplicationFactory(string connectionString)
           {
               _connectionString = connectionString;
           }

           protected override void ConfigureWebHost(IWebHostBuilder builder)
           {
               TestHostConfiguration.ApplyCommonTestConfiguration(builder, _connectionString);

               builder.ConfigureAppConfiguration((_, configBuilder) =>
               {
                   configBuilder.AddInMemoryCollection(new Dictionary<string, string?>
                   {
                       ["LoginRateLimiting:PermitLimit"] = LoginPermitLimit.ToString(),
                       ["LoginRateLimiting:WindowSeconds"] = LoginWindowSeconds.ToString(),
                   });
               });
           }
       }
       ```
       `RateLimiting`（下單）不覆寫、維持 production 預設值；每個測試方法各自 `new`／`Dispose` 一個實例，重複建立/釋放的成本只是 ASP.NET Core TestServer 啟動（毫秒級），不是容器啟動（秒級），不會拖慢測試
     - `CustomWebApplicationFactory.ConfigureWebHost` 同步改為呼叫同一個 `TestHostConfiguration.ApplyCommonTestConfiguration(builder, _connectionString)`（`_connectionString` 來源不變，仍是自己 `InitializeAsync` 啟動的容器），移除原本內聯重複的設定程式碼，避免兩個類別各自維護一份相同的 Jwt／TicketSigning／DbContext 覆寫邏輯
  4. LRL-006（登入限流與下單限流互不影響）驗證的是「下單額度打滿不影響登入」，需要下單緊額度 + 登入寬鬆額度的組合，因此沿用**既有** `RateLimitedWebApplicationFactory`（`OrdersRateLimitingTests` 新增一個測試方法，見 tasks.md 4.2.1），不使用 `LoginRateLimitedWebApplicationFactory`；此測試方法仍受 `IClassFixture` 共用 factory 影響，但因為登入額度是繼承自 base class 的寬鬆值（1000／60 秒），不會出現問題 A／B 那種緊額度互相消耗的情況
  5. LRL-003（超額請求即使帳密正確也一律拒絕、不核發 Token）不透過 mock 驗證，而是先用 `AuthTestHelper.RegisterAsync`（呼叫 `/api/auth/register`，不受 `login` policy 影響）建立一個有效帳號並記住其 email／password；接著送出 `PermitLimit` 次錯誤密碼的登入請求耗盡額度，第 `PermitLimit + 1` 次改用正確密碼送出，驗證仍回傳 429 且回應內容不含任何 Token 欄位——這證明的是「超額請求的可觀察 HTTP 結果」（429、無 Token），而不是「`LoginHandler` 內部確實沒被呼叫」；後者是 ASP.NET Core `RateLimiter` middleware 短路管線的框架保證（`[EnableRateLimiting]` 攔截發生在 controller action 執行與 model binding 之前——rate limiting middleware 仍需要 endpoint routing 先解析出 endpoint metadata（`[EnableRateLimiting]` 屬性本身就掛在 endpoint 上）才知道要套用哪個 policy，因此精確地說是「routing 決定 endpoint 之後、model binding／action 執行之前」短路，而非發生在 routing 本身之前），與既有 `place-order`／`confirm-order` policy 依賴的是同一個框架保證、既有測試也同樣不透過 mock 驗證這件事——本次比照既有慣例，不額外引入 mock／spy 來驗證框架已經保證的行為，見下方 spec.md Requirement 措辭調整

## Risks / Trade-offs

- [Risk] 僅以 IP 分區，無法防禦「分散在多個不同 IP 位址、針對單一帳號」的目標式暴力破解（例如殭屍網路輪流從不同 IP 各嘗試少量密碼組合，每個 IP 都在額度內）→ Mitigation：這是「基礎」等級刻意接受的限制（見決策 1），比照既有 `api-rate-limiting` 對「大量帳號分散請求」風險的既定取捨（`docs/project-scope.md` 已將 CAPTCHA 等進階行為驗證列為 Could）；本次先擋下最常見、最低成本的自動化腳本單源高頻攻擊
- [Risk] 多個真實使用者共用同一對外 IP（例如公司網路、行動網路 NAT、學校/公共 WiFi）時，其中一人多次登入失敗可能連帶讓同 IP 的其他人也被暫時限流 → Mitigation：`WindowSeconds = 60` 秒的短時間窗與明確的 `Retry-After` 提示，將誤傷影響控制在極短時間內；`PermitLimit = 5` 已考慮一般使用者打錯密碼的合理重試次數，多人共用 IP 情境下這個數字若上線後發現太嚴格，屬於可透過設定值調整、不需改程式碼的範疇
- [Risk] `LoginRateLimitingOptions` 的 `PermitLimit = 5`／`WindowSeconds = 60` 是起始猜測值，過嚴會影響真實使用者體驗、過鬆則防護意義不大 → Mitigation：與 `RateLimitingOptions`／`PurchaseQueueOptions` 相同定位，可依上線後觀察調整，不影響本次程式碼或 spec
- [Risk] 若部署拓樸加入反向代理或 CDN 且未正確設定 `ForwardedHeadersOptions`，會導致所有請求的 `RemoteIpAddress` 變成反向代理自身的 IP（等同所有使用者共用同一個分區、限流機制實質失效）→ Mitigation：見上方「Deployment Prerequisite」——已列為強制條件而非僅供參考的風險紀錄，本次部署拓樸（Docker Compose 直接對外）符合前提

## Migration Plan

- 純 middleware／設定變更，無需資料庫 migration
- 部署順序：套用即生效，`appsettings` 需提供 `LoginRateLimitingOptions` 區段（或依賴其安全預設值運作，比照 `RateLimitingOptions` 既定行為）
- Rollback：移除 `AuthController.Login` 上的 `[EnableRateLimiting("login")]` attribute 即可停用，不影響其他既有功能；`LoginRateLimitingOptions` 設定區段可保留不動（未被引用時無副作用）

## Open Questions

- 是否需要疊加以 Email 分區的第二層限流（見決策 1 的技術限制說明）——留待未來若觀察到分散 IP 針對單一帳號的攻擊模式時，再評估是否值得投入「buffer + 同步讀取 request body」的中介軟體工程複雜度，或改用其他方案（例如在 `LoginHandler` 內部維護一個以 Email 為鍵的失敗次數計數器，屬於與本次 ASP.NET Core RateLimiting middleware 不同的另一種機制，需要另外設計）
- 帳號鎖定機制（Non-Goals 已列）與本次的速率限制是互補而非互斥的兩種手段，若未來評估需要更強的防護，可以疊加而非取代本次機制
- `register`／`password-reset/request` 等端點是否也需要速率限制（防止大量假註冊或密碼重設信件濫發）——本次不處理，留待後續變更評估
