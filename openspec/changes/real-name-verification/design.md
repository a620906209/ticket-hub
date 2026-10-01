## Context

- `Member` 目前只有 Email、DisplayName、密碼雜湊、角色、啟用狀態，沒有任何個資欄位。會員資料由 Application 層經 `IApplicationDbContext.Members` 直接存取（Entity 數量少時的既有簡化做法，見 CLAUDE.md「何時可簡化」），沒有 Member Repository。
- `Event` 的屬性除 `IsQueueModeEnabled` 外皆為建構時指定、之後不可變（getter-only）；系統沒有編輯活動的端點。`MaxTicketsPerOrder` 是同類型的活動層級設定。`OrderService.PlaceOrderAsync` 不論活動是否設定限購，都會在開交易前以 `GetByIdAsync` 讀取一次活動（`OrderService.cs:161`）；目前這次讀取只用於限購檢查，讀到 null 時不中斷流程，也不是存在性檢查。開交易後再以 `GetForUpdateAsync` 重新讀取並鎖定活動，讀不到才回 NotFound；後者是交易內活動狀態（例如 `IsQueueModeEnabled`）判斷的權威資料來源（`OrderService.cs:174-199`）。實名設定的讀取時點見決策 3。
- 核銷是單一 `PATCH /api/admin/tickets/{id}/redeem`：驗簽（可選）→ 鎖定 Ticket → Organizer 歸屬核對（不符視同不存在）→ 狀態檢查 → `Redeem`。掃碼頁掃到即送出，沒有確認步驟。
- `ResultExtensions` 已有「以 `ErrorType` 名稱作為 ProblemDetails `Title`，前端據此判斷特定語意」的既定慣例（`QueueAdmissionRequired`、`InvalidTicketSignature`、`CaptchaInvalid`）。
- `GET /api/events` 由 `query-caching` 以 JSON 序列化存入 Redis（cache-aside），建立活動時明確失效，另有 TTL 安全網。
- `observability` 能力已要求遮蔽規則涵蓋 Serilog 結構化屬性，而不只是渲染後的訊息文字；請求摘要日誌不含 body。

## Goals / Non-Goals

**Goals:**
- 會員一次性登記實名，登記後不可變更，防止「改實名」繞過實名轉賣。
- 主辦方可在建立活動時指定需實名；未登記實名的買家無法對該活動下單或排隊。
- 需實名活動的核銷，後端強制先確認持票人；舊客戶端或手動輸入路徑都無法略過。
- 完整身分證末四碼只在核銷查詢持票人時出現；其他回應一律遮蔽，日誌一律不記錄。

**Non-Goals:**
- 逐張票填寫持票人實名、同一實名限購、實名更正（客服流程）、外部身分驗證、欄位層級加密（見決策 7）。
- 後台訂單列表／明細顯示買家實名。
- 修改既有活動的「需實名」設定（系統本來就沒有編輯活動功能）。

## Decisions

### 決策 1：實名資料作為 `Member` 的兩個可為 null 欄位，不另建 Entity 或 Value Object

`Member` 新增 `RealName`（string?，最長 50）與 `NationalIdLast4`（string?，固定 4 位數字），兩者皆 `private set`，只能透過 `RegisterRealName(realName, nationalIdLast4)` 設定。

- **一致性約束**：資料庫加 check constraint：兩欄同時為 null 或同時有值。`HasRegisteredRealName` 以 `RealName != null` 推導，不另存 bool，避免兩個來源不一致。
- **Domain 規則**：已登記時再呼叫 `RegisterRealName` 回傳失敗。這是單一 Entity 自己能判斷的規則，依 CLAUDE.md 放 Domain。
- **格式驗證位置**：
  - API 輸入格式由 Application 的 FluentValidation 驗證：姓名去除前後空白後 1–50 字、不得含控制字元；末四碼必須符合 `^[0-9]{4}$`。
  - Domain 方法再做防禦性檢查（null／空白）。
- **替代方案：獨立的 `MemberIdentity` Entity**。只有兩個欄位、一對一且不可變，拆表只增加 join，沒有收益。
- **替代方案：Value Object**。CLAUDE.md 規定「同一驗證邏輯在多處重複」才導入；目前只有登記一處會建立這組值，暫不導入。

### 決策 2：一次性登記以條件式 `ExecuteUpdateAsync` 保證並發安全，不依賴先讀後寫

如果用「讀出 Member → 檢查未登記 → 設定 → SaveChanges」，兩個並發的首次登記請求（例如不同分頁送出不同姓名）都會讀到「未登記」，最後寫入者覆蓋前者，兩邊都回 200，違反「只能登記一次」。

做法：Domain 新增 `IMemberRealNameRepository`，Infrastructure 實作。

- `TryRegisterAsync(memberId, realName, nationalIdLast4, ct)` → bool：以 EF Core `ExecuteUpdateAsync` 執行，條件為 `m.Id == memberId && m.RealName == null`，一次寫入兩欄，回傳影響筆數是否為 1。
- `GetAsync(memberId, ct)` → `MemberRealName?`（姓名＋末四碼的唯讀 record，未登記為 null）：`AsNoTracking`，只投影兩欄。供下單閘門、排隊閘門、查詢持票人使用。
- Handler 流程：以 `AsNoTracking` 載入 Member（不存在回 NotFound）→ 對這份**未追蹤**的副本呼叫 `RegisterRealName`（已登記回 Conflict，快速路徑；成功時副本上的值即本次要寫入的值）→ `TryRegisterAsync`（false 表示期間被其他請求搶先登記，回 Conflict）→ 以副本組成回應 DTO。
- **為何必須用未追蹤的副本**（design-hardener 指出）：若對追蹤中的 Member 呼叫會修改狀態的 `RegisterRealName`，同一個 scoped DbContext 之後只要有任何 `SaveChanges`（例如 UnitOfWork 或日後新增的程式碼），就會對 Member 送出**無條件**的 UPDATE，繞過 `RealName == null` 條件。在 `TryRegisterAsync` 輸掉競爭時，記憶體中的實體持有輸家的值，存檔就會覆蓋贏家。用 `AsNoTracking` 載入，可以讓 Domain 規則照常執行並提供 DTO 的值，又不會留下 change tracker 的髒狀態。整合測試鎖定：登記請求結束時，該 DbContext 的 ChangeTracker 中沒有 Modified 狀態的 Member（tasks 5.1）。

`ExecuteUpdateAsync` 是 EF Core 參數化的單一 `UPDATE ... WHERE`，不是原生 SQL，符合 CLAUDE.md。

- **為何要包成介面**：Application 測試的 `FakeApplicationDbContext` 以 MockQueryable 模擬 `DbSet`，不支援 `ExecuteUpdateAsync`。直接在 Handler 呼叫會讓 Handler 無法做單元測試。比照上一個 change 的 `IMemberDisplayNameReader`，把這個需要真實資料庫語意的操作放到 Infrastructure，Application 測試用 Fake，並發語意由 Infrastructure 整合測試（Testcontainers）驗證。
- **與 Domain 方法的關係**：`Member.RegisterRealName` 仍保留，作為「只能登記一次、兩欄須同時有值」規則的單一定義與單元測試對象。Handler 用它做快速路徑檢查；條件式更新才是並發保證。由於落地不經過 Entity 的 setter，Infrastructure 實作的條件中「`RealName == null`」與 Domain 規則必須一致，由整合測試鎖定。
- **替代方案：`xmin` 樂觀並發 token**。需要為 `Member` 全表啟用並發 token，會影響既有的 `UpdateMyProfile`、啟用／停用等寫入路徑（它們目前沒有處理 `DbUpdateConcurrencyException`），影響範圍超出本次。
- **替代方案：`SELECT ... FOR UPDATE`**。需要明確交易與鎖，條件式更新以單一語句就能達成，較簡單。

### 決策 3：活動「需實名」為建構時指定的不可變屬性；下單／排隊閘門的讀取時點

- **欄位與 API**：`Event` 新增 `IsRealNameRequired`（建構參數，預設 false），沒有 setter 或變更方法。建立活動 API 的 request 新增可選欄位 `IsRealNameRequired`，未提供時視為 false，既有客戶端不受影響。

**現況事實（2026-10-01 核對原始碼）**
- `OrderService.PlaceOrderAsync` 讀取活動兩次：
  1. 交易外以 `GetByIdAsync` 讀 `orderEvent`（可能為 null），只用於限購檢查（`OrderService.cs:161-172`）。
  2. 交易內以 `GetForUpdateAsync` 重讀並鎖定 `lockedEvent`，為 null 時回 NotFound；之後用 `lockedEvent.IsQueueModeEnabled` 判斷排隊資格（`OrderService.cs:174-199`）。這是既有的線性化時點，因為 `IsQueueModeEnabled` 可變。
- `JoinPurchaseQueueHandler` 同樣在交易外讀一次（驗證碼 → 活動存在 → 熱門搶購模式），交易內再以 `GetForUpdateAsync` 重讀（`JoinPurchaseQueueHandler.cs:49-79`）。
- 系統沒有刪除活動的路徑，也沒有刪除會員或清除會員欄位的路徑（`Member` 只有 `Deactivate`）。
- `Event.IsRealNameRequired` 目前**不存在**，是本 change 新增的屬性。

**本 change 完成後的不變量**
- I1：`Event.IsRealNameRequired` 只在建構時指定，之後不可變。
- I2：會員實名只能從「未登記」變成「已登記」（唯一寫入路徑是 `RealName == null` 條件的 `TryRegisterAsync`）。

**權威讀取時點與兩次讀取的互動**
- **建立訂單**：
  - **主要檢查（交易外）**：`orderEvent` 不為 null 時，以 `orderEvent.IsRealNameRequired` 判斷，在限購檢查之前、開交易之前執行。理由：失敗時不必開交易或取得任何鎖，而且錯誤順序（實名先於限購，TP-RN-ORDER-004）固定。依 I1，這個值與交易內重讀的值必然相同。
  - **補位檢查（交易內）**：`orderEvent` 為 null，但交易內 `lockedEvent` 不為 null 時（兩次讀取之間活動才變得可讀；正常情況不可達），在取得 `lockedEvent` 之後、排隊資格檢查與任何座位或庫存鎖定之前，以 `lockedEvent.IsRealNameRequired` 做同樣的實名檢查。失敗時回 `RealNameRequired`，交易回滾。這補上「交易外讀不到就整個跳過實名檢查」的漏洞。
  - **不變量自我檢查（交易內）**：兩者都不為 null 但 `IsRealNameRequired` 不同時，代表 I1 被破壞，丟 `InvalidOperationException`（500，訊息只含活動 Id），交易回滾。不靜默採信任一方。
  - **活動不存在**：兩次都為 null 時，沿用既有的交易內 NotFound；只有交易內為 null 時同樣回 NotFound（既有行為）。不新增錯誤路徑。
  - **會員實名**：在交易外讀取一次（`IMemberRealNameRepository.GetAsync`，不加鎖）。依 I2，「讀到已登記」在交易期間不可能失效；「讀到未登記、提交前剛好完成登記」只會讓這次請求被多擋一次，不會放行不該放行的請求。補位檢查也重用這次讀取的結果；若交易外沒有讀過（因為 `orderEvent` 為 null），就在補位檢查時讀取。
- **加入排隊**：以交易外讀到的活動判斷（該處活動為 null 時已提前回 NotFound，不會跳過），交易內重讀只用於既有的熱門搶購模式線性化。依 I1 不需要在交易內重查實名設定；兩者不同時同樣丟 `InvalidOperationException`。

**未來需要重新評估的條件**
- 新增活動編輯功能，讓 `IsRealNameRequired` 可變更（破壞 I1）：實名檢查必須改成以交易內 `lockedEvent` 為唯一權威，比照 `IsQueueModeEnabled`。
- 新增會員刪除、實名清除或個資刪除功能（破壞 I2）：會員實名的讀取必須移進交易並加鎖，或改成在訂單上保存實名快照。
- 實作時把上述兩點寫進 `OrderService` 與 `JoinPurchaseQueueHandler` 閘門處的程式碼註解。

- **檢查順序**：
  - 建立訂單：放在既有限購檢查之前。兩者都失敗時回 `RealNameRequired`：先引導登記，登記完再看張數錯誤，比反過來合理。
  - 加入排隊：放在驗證碼之後。驗證碼是防機器人的第一道關卡，比照 PQ-JOIN「驗證碼先於任何查詢」的既有規則。
- **錯誤語意**：`ErrorType.RealNameRequired` → 403，`Title = "RealNameRequired"`，比照 `QueueAdmissionRequired`：前端依 Title 而不是泛用 403 來判斷，再導向實名頁。
- **排隊入場後**：購票排隊放行（Admitted）後建立訂單，走的是同一個 `PlaceOrderAsync`，自然經過閘門。排隊閘門只是提早告知，不是唯一把關。

### 決策 4：核銷兩步驟——後端以請求旗標強制確認，查詢持票人為獨立唯讀端點

- **PATCH 請求**：`RedeemTicketRequest` 新增 `IsHolderVerified`（bool?，未提供視為 false）。
- **PATCH 處理順序**：既有的驗簽 → 鎖定 → Organizer 歸屬 → 狀態檢查之後，新增最後一關：
  - 票券所屬活動需實名且 `IsHolderVerified != true` 時，回 `Error.HolderVerificationRequired`（409，`Title = "HolderVerificationRequired"`），不呼叫 `Redeem`。
  - 交易回滾即可，因為沒有任何寫入。
  - 只有在歸屬與狀態都通過後才回這個錯誤，所以不會洩漏其他 Organizer 票券的存在性（RDM-AUTHZ-004／005 的既有保證不變）。
- **查詢方式**：既有 `IOrderRepository.GetOrganizerIdByOrderItemIdAsync` 改為 `GetRedemptionContextByOrderItemIdAsync`，以同一個單一投影查詢回傳 `(OrganizerId, IsRealNameRequired, BuyerId)`，不增加持鎖期間的查詢次數（維持既有 Decision 1「縮短持鎖時間」的理由）。舊方法目前只有 `RedeemTicketHandler` 使用，直接改名取代，不並存兩個方法。
- **查詢持票人端點** `GET /api/admin/tickets/{id}/holder`：
  - 授權與 Organizer scoping 規則和核銷端點相同（不屬於呼叫端 Organizer 視同不存在，回 404）。
  - 回傳 `{ ticketId, ticketStatus, isRealNameRequired, holderRealName, holderNationalIdLast4 }`。非實名活動時兩個持票人欄位為 null，不回傳買家任何資料。
  - 持票人即訂單買家（`Order.BuyerId` 對應的 Member）。
  - 唯讀，不加鎖。
  - 只對 `Issued` 票券回傳持票人資料，其他狀態回 409（RDM-HOLDER-008）；完整授權契約見 ticket-redemption delta spec「已切換 Organizer 的操作人員可查詢票券持票人資料」。
- **掃碼頁流程（被動觸發）**：維持「掃到即送 PATCH（不帶旗標）」。
  1. 收到 `HolderVerificationRequired` 時，呼叫查詢持票人端點並顯示確認面板。
  2. 操作人員按「確認核銷」後，再送一次 PATCH，帶 `IsHolderVerified = true` 與原本的簽章。
  3. 非實名活動完全沒有額外請求，既有 ADMIN-REDEEM-SCAN-* 情境的行為不變。
- **替代方案：掃碼後一律先查持票人**。每張票多一次請求，而且要改寫既有掃碼情境；被動觸發只影響實名活動。
- **替代方案：把持票人資料放進 409 的 ProblemDetails**。錯誤回應會被泛用錯誤處理、日誌或監控當作錯誤內容處理，個資混進錯誤通道，容易在未來被誤記錄；獨立端點讓「唯一會回傳完整末四碼的地方」可被單獨審查與測試。
- **替代方案：查詢端點也要求簽章**。簽章若放 query string，可能被存取紀錄記下（違反 `ticket-issuance` 簽章不得入 log 的規則）；改成 POST body 又讓唯讀查詢語意不清。實際上這個端點只會在 PATCH 已驗簽通過、回 `HolderVerificationRequired` 之後才被掃碼頁呼叫，真正的狀態變更仍由 PATCH 驗簽把關。手動輸入路徑本來就不帶簽章（既有行為）。Ticket Id 是不可猜的 GUID，而且受 Organizer scoping 限制，能查到的只有自家活動的持票人。

### 決策 5：個資遮蔽規則集中定義，回應與日誌分開處理

- **API 回應**：
  - `GET /api/members/me` 回傳 `HasRegisteredRealName`、`RealName`（本人資料，完整姓名）、`NationalIdLast4Masked`（固定格式 `**` + 末兩碼，例如 `**34`）。
  - 完整末四碼只出現在決策 4 的查詢持票人端點。
  - 遮蔽函式放 Application（`RealNameMasking.MaskNationalIdLast4`），單元測試涵蓋。
- **日誌**：
  - 實名登記 Handler 只記錄 `MemberId` 與結果（成功／已登記衝突），MUST NOT 將姓名或末四碼放進訊息樣板或結構化屬性。
  - 查詢持票人與核銷流程同樣只記錄 Ticket Id／Organizer Id。
  - 驗證失敗時 FluentValidation 的錯誤訊息不回顯輸入值（不使用 `{PropertyValue}` 佔位符）。
  - 測試沿用 `RecordingLogger`／Serilog 結構化屬性斷言的既有做法：用一組獨特的姓名與末四碼走完整流程，斷言所有 `LogEvent` 屬性與訊息都不含這些值。
- **錯誤訊息與例外**：
  - `ResultExtensions` 會把 `Error.Message` 原樣放進 ProblemDetails `Detail`，所以本次新增的錯誤訊息只能含 Id，不得含姓名或末四碼。涵蓋 `RealNameRequired`、`HolderVerificationRequired`、登記的 Conflict／NotFound，以及 `InvalidOperationException`。
  - Npgsql 的 `PostgresException` 只在連線字串含 `Include Error Detail` 時，才會把違反約束的整列值（含姓名、末四碼）放進例外訊息；目前 `docker-compose.yml` 沒有設定。禁止在任何環境的連線字串加入此參數，並在 tasks 2.6 以測試斷言約束違反時的例外訊息不含寫入的值。 tasks 5.17 另以設定檔掃描作為自動化的部署前置檢查；這條是部署設定規範，不列為 Acceptance Criteria。
  - EF Core 沒有啟用 `EnableSensitiveDataLogging`，SQL 指令日誌已降噪到 Warning，不會記錄參數值（design-hardener 已核對）。
- **回應快取**：查詢個人資料、實名登記、查詢持票人三個端點以 action 層級的 `[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]` 設定 `Cache-Control: no-store`。這是 result filter，會套用到 action 產生的所有結果（含 404、409、400），所以不必逐一判斷哪個狀態碼含個資。401／403 由授權 middleware 在進入 action 前產生；JSON 格式錯誤的 400 由 `[ApiController]` 的 `ModelStateInvalidFilter` 短路產生；500 由全域例外處理產生。這三類都不經過這個 filter，也不含實名欄位，不在要求內。格式驗證（RNV-FORMAT-*）在 Handler 內以 FluentValidation 執行（比照既有 `LoginHandler`），它的 400 是 action 結果，會帶 no-store。核銷頁可能是多人共用的現場裝置，所以必須設定（RNV-NOSTORE-001～005）。
- **替代方案：Serilog destructuring policy 全域遮蔽**。這類 policy 只在物件被解構時生效，擋不住直接把字串當參數傳入；靠呼叫端紀律加測試斷言較直接，與既有 Email 遮蔽做法一致。

### 決策 6：既有 `GET /api/events` 快取不需要清除或改 key

- 部署前已寫入 Redis 的 `EventDto` JSON 沒有 `isRealNameRequired`，`System.Text.Json` 會把它反序列化為 false。
- 這些快取項目只可能是部署前建立的活動，而 migration 會把既有活動一律回填為 false，所以快取值與資料庫一致。
- 部署後建立的需實名活動，會因建立活動時的既有快取失效機制而重新載入。

結論：不需要變更快取 key 版本，也不需要手動清除。這個推論以一個整合測試鎖定：把不含新欄位的舊格式 JSON 直接寫入快取，查詢結果應為 `isRealNameRequired = false`。

**部署切換期間的保證範圍**（design-hardener 指出，依使用者審查收斂）：
- **保證**：已建立且設定為需實名的活動，不會因快取或部署切換，被後端的下單、排隊、核銷判斷視為不需實名。這三個閘門一律讀資料庫欄位（`IEventRepository` 與核銷歸屬投影），完全不讀 `query-cache:events:list`。
- **不保證**：公開活動列表 API 回傳的 `isRealNameRequired`（以及前端據此顯示的「本活動需實名」標示）可能在快取 TTL（`QueryCache:EventListTtlSeconds`，預設 30 秒）內落後資料庫，與既有 `IsQueueModeEnabled` 的快取陳舊性相同性質。部署切換期間命中舊格式快取時，該欄位一律為 false。
- 單一 api 服務與「只有新版能建立需實名活動」的推論，可以降低列表標示出錯的機率，但不作為保證的依據。


### 決策 7：不做欄位層級加密

只存姓名與身分證「末四碼」，不存完整身分證字號。資料庫存取已受 compose 網路隔離與帳密保護；欄位加密需要金鑰管理（本機沒有 Key Vault），會增加部署與測試複雜度。依 CLAUDE.md「Simplicity First」，本次不做；若未來改存完整身分證字號或手機號，須另開提案評估。

### 決策 8：前端

- **實名資料頁**（`/me/real-name`，需登入）：
  - 未登記時顯示表單，送出前以確認對話框強調「登記後無法自行修改」，按鈕文字為「確認登記」／「返回修改」。
  - 已登記時唯讀顯示姓名與遮蔽末四碼。
  - 收到 409 時（例如另一分頁已登記），重新查詢個人資料並顯示已登記狀態。
- **入口**：買家端會員選單新增「實名資料」。
- **活動頁**：活動需實名時顯示「本活動需實名」標示。
  - 已登入且未登記者，顯示提示與「前往登記」連結。
  - 下單或排隊仍可送出，以後端為準；收到 `RealNameRequired` 時顯示同樣的引導。
  - 前端不自行擋下送出：避免個人資料查詢失敗或過期時誤擋已登記者。
- **登記後返回**：從活動頁進入實名頁時，帶 `redirect` 參數，登記成功後返回原活動頁。沿用既有登入頁 redirect 的做法：`LoginPage.vue` 直接把 query 字串交給 `router.push`，沒有白名單；vue-router 會把它當成站內路徑解析，不會導到外部網址。實名頁另外要求 redirect 必須以 `/` 開頭且不以 `//` 開頭，不符合時導回首頁（防 protocol-relative URL）。
- **後台建立活動表單**：新增「需實名」勾選，說明文字註明「建立後不可變更」。
- **核銷頁**：
  - 收到 `HolderVerificationRequired` 時暫停掃描，顯示確認面板：姓名、末四碼以大字顯示，旁邊有「確認核銷」「放棄」兩個按鈕。
  - 放棄後回到可掃描狀態，該票維持 Issued。
  - 確認核銷的結果沿用既有結果橫幅（成功／已核銷過／系統錯誤）。
  - 面板顯示期間，比照 ADMIN-REDEEM-SCAN-DEDUPE 忽略相機重複偵測。
  - 查詢持票人失敗時顯示可重試的系統錯誤，不得自動核銷。
- **XSS**：姓名一律以文字插值渲染，禁止 `v-html`；測試以含 HTML 標籤的姓名驗證。

**UI 細節**（規劃時套用 `emil-design-eng`，依使用頻率決定動畫與互動強度）：

| 元件 | 決定 | 理由 |
| --- | --- | --- |
| 核銷確認面板 | 不加進場位移或縮放動畫，直接顯示（至多 150ms 以內的 opacity 淡入） | 入場高峰一場活動會出現數百次，動畫只會拖慢節奏 |
| 核銷確認面板 | 不套用既有結果橫幅的「停留後自動恢復掃描」計時；只有按下「確認核銷」或「放棄」才離開 | 比對證件是人工判斷，自動消失會讓操作人員來不及比對，或誤以為已處理 |
| 核銷確認面板 | 送出確認後按鈕立即進入處理中並停用，直到結果回來 | 防止重複點擊送出兩次 PATCH；第二次會得到「已核銷過」，誤導操作人員 |
| 核銷確認面板 | 「確認核銷」「放棄」以文字區分，按鈕觸控面積足夠，不只靠顏色辨識；面板也顯示「持票人即訂購會員」說明 | 現場光線差、手持操作；同行者入場情境見 Risks |
| 實名登記確認對話框 | 對話框重新列出使用者輸入的姓名與末四碼，請使用者再看一次；開啟時預設焦點在「返回修改」 | 登記後無法修改，誤按 Enter 不應直接送出不可逆操作 |
| 活動頁「本活動需實名」 | 靜態標籤，不加動畫；以文字標示，不只用圖示或顏色 | 資訊性標示，不需要吸引注意的動態效果 |
| 後台「需實名」勾選 | 勾選框旁附說明文字「建立後不可變更；買家需先登記實名才能購票，入場時需核對證件」 | 讓主辦方在不可逆設定前知道後果 |

## 安全確認問題（CLAUDE.md 安全強制規則）

**輸入驗證**
- 外部輸入與驗證層：
  - 登記請求的姓名／末四碼：Application 層 FluentValidation（RNV-FORMAT-001～006）。
  - 建立活動的 `IsRealNameRequired`、核銷的 `isHolderVerified`：皆為 `bool?`，由框架反序列化把關，型別不符（例如字串 `"yes"`、數字）時在模型繫結階段回 400，不進入 Handler。核銷的規則比照既有 TICKET-REDEEM-SIG-TYPE-MISMATCH，見 RDM-RN-009。
  - 路徑 Id 由路由約束 `{id:guid}` 把關（RDM-HOLDER 路徑格式規則）。
- 有沒有拼接進 SQL 或 shell：沒有。所有查詢都是 EF Core LINQ 或 `ExecuteUpdateAsync`，參數化；沒有 shell 呼叫。

**資料庫**
- 參數化：EF Core（決策 2 的 `ExecuteUpdateAsync` 同樣參數化）。沒有原生 SQL、沒有 Dapper。
- N+1 風險：每條新路徑的查詢次數固定，與資料筆數無關，沒有迴圈內查詢。

| 路徑 | 新增查詢 |
| --- | --- |
| 實名登記 | 載入 Member 1 次 ＋ 條件式 `UPDATE` 1 次 |
| 查詢個人資料 | 沿用既有 1 次 Member 查詢，多投影兩欄，不增加查詢 |
| 建立訂單／加入排隊閘門 | 僅需實名活動多 1 次 `GetAsync`（以主鍵查詢，只投影兩欄）；不需實名活動 0 次（TP-RN-ORDER-003、PQ-RN-JOIN-004 斷言呼叫次數為 0） |
| 核銷 | 歸屬投影改為同時取三欄，仍是同一個單一查詢，查詢次數不變 |
| 查詢持票人 | Ticket 1 次 ＋ 歸屬投影 1 次 ＋（僅需實名時）`GetAsync` 1 次 |
| 公開／後台活動列表 | 既有投影多取一欄，不增加查詢 |

**權限**
- `PUT /api/members/me/real-name`：
  - 授權：`MembersController` 類別層級的 `[Authorize]`（middleware／policy 層），未登入回 401（RNV-REGISTER-005）。
  - 身分：會員 Id 只在 Controller 以既有 `User.GetMemberId()` 取自 JWT claims（與 `GET/PUT /me` 相同），request body 沒有會員 Id 欄位，無從指定他人。
  - 任何已登入會員都能登記自己的實名，不需額外角色。
- `GET /api/admin/tickets/{id}/holder`：
  - 授權：`AdminTicketsController` 類別層級的 `[Authorize(Policy = RequireOrganizerContext)]`，未帶 `OrganizerId` claim 回 403、未登入回 401（RDM-HOLDER-004／005）。
  - 資料範圍：Handler 內的 Organizer 歸屬核對（RDM-HOLDER-003）。
  - 兩層都必須通過。
- `PATCH /api/admin/tickets/{id}/redeem` 新增的確認閘門：在既有 policy 與 Handler 歸屬核對都通過之後才執行，不改變既有權限層級（RDM-RN-005）。
- 有沒有可能被未授權使用者觸發：
  - 登記：只能寫自己的帳號。
  - 查詢持票人：其他 Organizer 回 404，不洩漏存在性。
  - 下單／排隊閘門：只會額外拒絕，不會放行原本被拒的請求。

**前端**
- 使用者輸入是否直接渲染進 DOM：姓名在實名頁、核銷確認面板一律以文字插值渲染，禁止 `v-html`（BW-RN-PAGE-010、AWU-REDEEM-RN-009）。
- redirect 參數另做檢查（BW-RN-PAGE-008）。
- API 呼叫的 Auth Header：新增的 `registerRealName`、`getTicketHolder`，以及擴充的 `redeemTicket`，一律經 `web/src/api/httpClient.ts` 的 `authorizedRequest`（與既有 queue/orders/events/admin 等 api 檔案相同）。它內部走 `request()` 統一注入 `Authorization: Bearer`（`httpClient.test.ts` 已驗證），並在收到 401 時換發重試一次；不可直接呼叫 `request()`，否則會漏掉換發（該檔註解明文規定），不在元件內自行組 header 或呼叫 `fetch`。

**機敏資訊**
- 沒有新增金鑰或密碼。
- 實名資料的日誌規則見決策 5。
- 保留與刪除：本次不提供刪除或匯出實名資料的途徑。實名資料隨會員資料保留：會員被停用（`Deactivate`）時不清除，停用前已售出的需實名票券仍可查詢持票人與核銷。這是本 change 的保證，見 real-name-verification delta spec「會員停用不清除實名資料…」（RNV-RETAIN-001～003）。資料庫備份依既有備份範圍一併包含。若未來需要個資刪除（例如帳號註銷），須另開提案；而且刪除會破壞決策 3「實名只增不減」的前提，屆時必須一併重新評估下單閘門的檢查位置。

## Risks / Trade-offs

- **[一筆訂單多張票，持票人都是買家本人]** 買家幫朋友買的票，朋友單獨入場時比對不符。→ 這是「實名綁會員帳號」決策的已知後果，proposal 已列為不做（逐張實名）。核銷面板顯示「持票人即訂購會員」說明，讓現場人員知道同行者應與買家一起入場；主辦方可搭配既有 `MaxTicketsPerOrder` 限制張數。
- **[實名只驗格式不驗真偽]** 可以填假名。→ 現場比對證件才是真正的把關點；登記的值不可修改，假名無法在現場通過比對。
- **[登記錯誤無法自行更正]** → UI 以確認對話框降低誤填機率；更正屬客服流程，不在範疇內，已在 proposal 標明。
- **[查詢持票人端點不要求簽章]** 拿到 Ticket Id 的自家 Organizer 成員可以查到持票人姓名與末四碼。→ 這類成員本來就能看到自家活動的訂單；後台訂單列表與明細 UI 都不顯示 Ticket Id（已核對 `AdminOrderDetailPage.vue`）。風險可接受，決策 4 已說明。
- **[核銷多一次往返]** 實名活動每張票需要 PATCH（409）→ GET → PATCH 三次請求。→ 入場比對本來就需要人工看證件，網路往返相對可忽略；非實名活動不受影響。
- **[查詢持票人端點的殘餘風險]**（design-hardener 指出，已接受的取捨）：
  - 端點對持有該 Organizer `OrganizerId` claim 的成員開放（目前只有 Owner 角色）。只限 `Issued` 票券：核銷後即不可讀（RDM-HOLDER-008）。沒有時間限制，因為系統沒有活動結束時間欄位，`Issued` 票券在活動開始後仍可讀，以支援遲到入場（RDM-HOLDER-009）。
  - RDM-AUTHZ-006 的「停權後 Access Token 在過期前仍有效」延遲視窗，現在不只是能核銷，也能讀到買家姓名與末四碼；上限同為 `AccessTokenExpirationMinutes`。
  - 緩解：每次查詢（含被拒）記錄不含個資的稽核日誌（RDM-HOLDER-011）。本次不加 rate limit。
  - 稽核日誌的保存與存取：寫入既有 Serilog → Seq，保存期限依 Seq 的保存設定（本機開發環境，未另訂）。目前 `docker-compose.yml` 的 Seq 查詢介面綁定所有網卡、不需帳密（`"${SEQ_HOST_PORT:-8081}:80"`、`SEQ_FIRSTRUN_NOAUTHENTICATION`），這是 observability 能力既有的「本機開發展示、非公開部署」決策，本 change 不修改。日誌只含識別碼，本身不構成新的個資來源；但它能把 Member Id 與「某時刻在某活動入場」連結起來，在任何共用或正式環境部署前，必須先限制 Seq 的存取（至少綁定 localhost 或加上驗證），保存期限也一併訂定；這是部署前置條件，留待部署提案處理（`docs/project-scope.md` 第 8 節目前只有「部署環境是否加雲端平台展示」一條，歸檔時補上此前置條件，見 tasks 8.5）。
- **[`isHolderVerified` 是流程防呆，不是授權控制]** 它防的是「誤用」：舊客戶端或手動輸入時忘了比對。它不防持有 Organizer 權限的內部人員直接送 `true` 跳過比對；「防轉賣」最終依靠現場人員紀律。系統目前不記錄是誰確認的（不存 `confirmedBy`／`confirmedAt`）；若未來需要追責，須另開提案。

## Migration Plan

契約見 real-name-verification delta spec「部署切換與 migration 回滾不得讓實名資料或實名活動設定靜默失效」（RNV-ROLLBACK-001～003；部署順序見下方檢查清單）。

1. **EF migration**：`Members` 新增 `RealName varchar(50) NULL`、`NationalIdLast4 char(4) NULL`，加 check constraint `(RealName IS NULL) = (NationalIdLast4 IS NULL)`；`Events` 新增 `IsRealNameRequired boolean NOT NULL DEFAULT false`。可為 null 欄位與帶預設值的 bool 欄位在 PostgreSQL 11 以上只改 metadata，不需回填、不長時間鎖表。
2. **部署檢查清單**（營運規範，非自動化驗收情境）：
   1. `docker compose exec api dotnet ef database update` 套用 migration。舊容器此時仍在服務；它不認得新欄位，寫入的快取不含 `isRealNameRequired`。
   2. `docker compose up -d --build api` 重建（單一服務、沒有多副本，舊容器先停）。
   3. 刪除 `query-cache:events:list`，縮短列表標示可能落後的時間：清除完成後的第一次查詢會從資料庫重新載入。步驟 2、3 之間仍可能命中舊格式快取，此時列表的 `isRealNameRequired` 為 false（TP-BROWSE-RN-002），只影響 UI 標示，不影響後端閘門。
   4. 確認：`redis-cli EXISTS query-cache:events:list` 為 0 之後，呼叫一次公開活動列表，每筆都含 `isRealNameRequired`。
   - 不以多個 api 實例滾動部署本次變更；未來若改為多實例部署，須另行評估快取 key 版本化。
   - 後端閘門以資料庫為準（見決策 6「部署切換期間的保證範圍」），列表標示的短暫落後屬於既有快取陳舊性的同類取捨，不另設監控或告警。

3. **Down 的前置檢查**：以 `migrationBuilder.Sql` 執行一段 PostgreSQL `DO` 區塊，存在已登記實名的會員或需實名活動時 `RAISE EXCEPTION`，之後才刪除約束與欄位。
   - 這是 CLAUDE.md「禁止原生 SQL」的明確例外，理由：只在 migration 內執行、內容是固定字串、不接受任何外部輸入；EF Core migration API 沒有「依資料狀態中止」的功能。
   - 由 RNV-ROLLBACK-001～003 的 Testcontainers 測試驗證。
4. **已有資料時的回滾**：依 spec 優先只回滾程式碼；必須移除 schema 時，人工備份後清除資料再執行 Down，重新升版時以備份還原後才對外服務。

## Open Questions

（無；分岔決策已於提案前與使用者確認：實名綁會員帳號、活動層級開關、核銷兩步驟、登記後不可修改、核銷頁完整顯示末四碼、本次不做限購。）
