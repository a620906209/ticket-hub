## Context

目前系統只有 `MemberRole.Member` / `MemberRole.Admin` 兩種全域角色，`Event.CreatedByMemberId` 只記錄操作者，不是租戶歸屬。`docs/project-scope.md` 把系統定位為「Organizer 為獨立 Entity 的多租戶架構」，但從未真正實作。

本次是落地這個定位的**第一步**：新增 `Organizer` 實體、申請／審核／停權流程、`Member`-`Organizer` 多對多關聯，以及「目前操作中主辦方」的切換機制。本次刻意不觸碰任何既有 capability 的授權規則——把既有後台管理端點的授權規則從「Admin 角色」改為「已切換至一個 Approved Organizer」，是後續依賴本次的變更（`event-management-organizer-scoping` 及其之後的變更）的範圍。

JWT 目前的簽發模式（見 `JwtTokenService.GenerateAccessToken`）是把角色直接烘進 Access Token claim，短生命週期（`AccessTokenExpirationMinutes`），角色變更後需等下次登入或換發才會反映。本次沿用同一套模式處理「目前操作中的 Organizer」。

## Goals / Non-Goals

**Goals:**
- 讓一般已登入 Member 可以自助申請成立 Organizer
- 讓平台管理員（既有 `Admin` 角色）審核 Organizer 申請（Approve / Reject / Suspend）
- 讓屬於多個 Organizer 的 Member 可以切換「目前操作中」的 Organizer
- 這個切換機制本身要能被後續變更（`event-management-organizer-scoping` 等）直接拿來當作既有後台端點的授權依據，不需要重新設計

**Non-Goals:**
- 不做 Organizer 內部的精細角色分工（例如 Owner／Staff 權限差異）；本次僅有單一 `Owner` 角色
- 不做平台管理員「跨 Organizer 稽核所有活動」的畫面或 API
- 一般會員自助申請建立的 Organizer，一律只有申請人一位 `Owner`；本次不提供「邀請」或「新增既有 Organizer 成員」的功能，`OrganizerMember` 雖是多對多的資料模型（供未來擴充），但在本次交付範圍內每個 Organizer 實際上只會有單一成員
- 停權（`Suspend`）本次僅提供後端端點（`PATCH /api/admin/organizers/{id}/suspend`），**不提供對應的前端操作介面**；平台管理員本次僅能透過直接呼叫 API 觸發停權，`admin-web-ui` 的審核清單頁只涵蓋核准／駁回。停權操作介面留待後續變更視需求另行提案。
- Organizer 狀態機本次刻意只提供 `Pending → Approved／Rejected`、`Approved → Suspended` 三種轉換；`Suspended`／`Rejected` 皆為終態，**不提供「解除停權」或「重新開放已駁回申請」的路徑**——這是刻意的範圍限定（YAGNI），不是遺漏，需要這類能力時另開提案評估。
- 不做 Organizer 停權後對其既有活動／訂單的連動處理；停權**不刪除、不變更**該 Organizer 名下既有資料庫資料。停權本身立即阻擋兩個時機點（見「平台管理員可以停權已核准的 Organizer」spec Requirement）：該 Organizer 成員之後嘗試切換操作情境進入該 Organizer；以及該成員下一次以 Refresh Token 換發 Access Token 時。至於**既有其他能力**要如何感知並強制這個狀態變化（例如某個已核發的 Access Token 是否可能在過期前仍通過某個其他能力的授權檢查），屬於採用該 claim 做為授權依據的能力自己的責任與範圍，不由本次的 organizer-management 能力保證——這個議題在 `event-management-organizer-scoping`（第一個實際採用 `OrganizerId` claim 做授權依據的變更）中有明確定義與測試
- 不限制同一 Member 可同時持有的 `Pending` 申請數量，也不對申請端點設送出頻率限制；這是已登入、低成本（單筆 DB insert）操作，本次刻意不加速率限制（YAGNI），待實際觀測到濫用再視需要補上
- 平台管理員待審核清單（`GET /api/admin/organizers?status=Pending`）本次不做分頁；待審核量體目前規模小，本次刻意不加分頁（YAGNI），待清單成長到有感延遲再視需要補上
- 換發 Access Token 時因 Organizer 被停權或成員資格不存在（`ORG-REFRESH-003`／`004`）而清空 `OrganizerId` claim 屬於 fail-closed 的安全行為，本次前端不提供「已離開該主辦方操作情境」的提示 UX，使用者下次操作會自然因缺少 claim 被既有授權檢查擋下；提示 UX 留待後續變更視需要補強
- 平台管理員審核（核准／駁回）與停權操作不設樂觀併發保護（`Organizer` entity 不像 `RefreshToken` 有 `xmin` 併發權杖檢查）；若兩位 Admin 同時對同一筆 Organizer 分別呼叫，可能發生 lost update（後寫入者覆蓋先寫入者，兩邊皆收到成功回應但最終狀態只反映其中一方）。這是刻意的風險接受（YAGNI）：平台管理員數量少、操作頻率低，衝突機率與影響皆可忽略，待實際觀測到問題再視需要補上併發保護

## Decisions

### 1. 「目前操作中的 Organizer」放在 Access Token claim，而非每次請求帶 Header
比照既有 Role claim 的模式：切換 Organizer 時呼叫新端點 `POST /api/organizers/{id}/switch-context`，驗證呼叫者是該 Organizer 的成員且該 Organizer 狀態為 `Approved` 後，換發一組新的 Access Token（帶 `OrganizerId` claim），Refresh Token 不變。

**現況核對（識別是哪一個 session 發起切換）**：核對 `RefreshTokenConfiguration`（`MemberId` 索引未加唯一性約束）與 `LoginHandler.IssueTokensAsync`（每次登入都新增一筆 `RefreshToken`、不撤銷該 Member 既有的有效 Token）後確認：同一個 Member 可以同時持有多筆 `Status = Active` 且未過期的 `RefreshToken`（多裝置／多分頁登入）。切換操作情境要更新的是「發起這次切換的那個 session」對應的 `RefreshToken` 資料列（見 Decision 2），若只憑 Access Token 解出的 `MemberId` 查詢，會查到 0～多筆，無法唯一定位。因此 `switch-context` 端點的請求內容 **MUST** 額外攜帶呼叫者目前持有的 Refresh Token 明文（欄位命名比照既有 `RefreshTokenRequest.RefreshToken`），後端以 `TokenHash` 精確查出**這一筆** `RefreshToken` 資料列，並驗證其 `MemberId` 與 Access Token 解出的呼叫者一致、狀態為 `Active` 且未過期，只更新這一筆的 `OrganizerId`；其餘裝置／分頁各自的 `RefreshToken` 資料列（與其各自的 `OrganizerId`）完全不受影響，各自維持原本的操作情境，直到各自也呼叫切換或各自換發時才重新驗證。這同時是刻意的行為：每個裝置／分頁可以獨立處於不同的 Organizer 操作情境，不會因為在另一台裝置上切換而被靜默改變。此原地更新 **MUST NOT** 觸發 Refresh Token 輪替：不建立新的 `RefreshToken` 資料列，回應也 MUST NOT 包含新的 Refresh Token 明文——呼叫端沿用原本持有的那一組。

**替代方案**：每次請求帶 `X-Organizer-Id` Header，後端在 Middleware/Filter 逐次查 `OrganizerMember` 驗證資格。
**選擇 Claim 換發的理由**：與現有 Role 的授權模式一致（Rule 11：符合既有慣例），未來採用此 claim 的能力（例如 `event-management-organizer-scoping`）的 `[Authorize]` 可以直接用 Policy 讀 claim，不需要額外中介層查表；Access Token 生命週期短，資格變動的延遲曝險與現有 Role 變更的延遲曝險同等級，不是新引入的風險。
**Trade-off**：換發時機需要顯式呼叫「切換」端點，前端才能開始以該 Organizer 的操作情境運作。本次交付範圍內只有一個自動觸發點：申請成功後系統自動嘗試切換一次（見 admin-web-ui spec「使用者可透過介面申請建立主辦方」需求、`AWU-APPLY-002`／`AWU-APPLY-004`，因新申請預設為 `Pending` 這次自動切換預期會被拒絕）。其餘情境（例如申請被核准後、或在「我的主辦方」清單頁切換到另一個已核准的 Organizer）皆為使用者於清單頁手動點選「切換」觸發（見 admin-web-ui spec「使用者可透過介面查看與切換自己所屬的主辦方」需求、`AWU-LIST-002`），本次不提供額外的自動觸發。前端呼叫切換端點時，需要額外傳入目前持有的 Refresh Token（前端既有 `/api/auth/refresh` 流程本來就會持有這個值，不需新增儲存機制）。

### 2. Refresh 時是否保留 `OrganizerId`——不解析舊 Access Token，改為持久化在 `RefreshToken` 上
**現況核對**：既有 `/api/auth/refresh` 端點（`RefreshTokenHandler`）完全不接收、也不解析舊 Access Token，只憑 Refresh Token 本體查出對應的 `RefreshToken` 資料列（`Id`／`MemberId`／`TokenHash`／`Status`／`ExpiresAt`／`PreviousTokenId`）與 `Member`；`RefreshToken` entity 目前沒有任何欄位可以得知「換發前 Access Token 帶的是哪個 `OrganizerId`」。原設計「讀取舊 Token 的 `OrganizerId` claim」在現有架構下沒有資料來源，不可行。

**修正後設計**：`RefreshToken` entity 新增可為 null 的 `OrganizerId` 欄位。
- 切換操作情境端點（`POST /api/organizers/{id}/switch-context`）的請求內容 MUST 帶呼叫者目前持有的 Refresh Token 明文（見 Decision 1）。成功時，除了換發新 Access Token，**額外**把該筆（依 `TokenHash` 精確定位、且確認屬於呼叫者本人、狀態為 `Active`）`RefreshToken` 資料列的 `OrganizerId` 欄位更新為切換目標（原地更新，不觸發 Token 輪替、不影響 Refresh Token 本身的值、不影響呼叫者其他裝置/分頁各自的 `RefreshToken` 資料列）
- `RefreshTokenHandler` 換發時，讀取 `existingToken.OrganizerId`：若非 null，重新查詢該 `OrganizerId` 對應的 `OrganizerMember`／`Organizer` 是否仍存在、呼叫者仍是成員、且該 Organizer 狀態仍為 `Approved`——驗證通過則新簽發的 Access Token 帶相同 `OrganizerId` claim，且新產生的 `RefreshToken` 資料列（`RefreshToken.Issue`）延續同一個 `OrganizerId`；驗證未通過則新 Access Token 不帶該 claim，新 `RefreshToken` 資料列的 `OrganizerId` 設為 null。因為 `OrganizerId` 是掛在個別 `RefreshToken` 資料列上（而非 Member 層級的全域狀態），這個換發驗證天然只影響「正在換發的這一筆」，不會誤動到同一 Member 其他裝置/分頁各自的 Refresh Token 與其各自的 `OrganizerId`
- 驗證未通過涵蓋兩種情況：(a) Organizer 狀態變為非 `Approved`（本次交付範圍內，狀態機只允許 `Approved → Suspended` 這一種離開 `Approved` 的轉換，故此分支實務上等同「已被停權」）；(b) 呼叫者不再是該 Organizer 的成員——**本次交付範圍內沒有任何功能會移除既有 `OrganizerMember` 記錄**，這個分支目前無法透過任何既有端點觸發，屬於為未來擴充預先寫好的防禦邏輯，仍需以測試手法模擬驗證其本身正確

**替代方案**：讓前端在 refresh request body 額外帶目前的 Access Token，後端解析其 claim。
**選擇持久化在 `RefreshToken` 的理由**：不需要修改既有 `RefreshTokenRequest` 的公開契約，也不依賴「Access Token 內容可信」這個較弱的假設；`RefreshToken` 資料列本來就是後端持久化、可信的來源，且既有 Token 輪替（`PreviousTokenId` 鏈）機制已經是每次換發都會建立新資料列，天然適合搭配延續／清除 `OrganizerId` 的邏輯。

**理由（維持原決策的安全考量）**：避免資格被撤銷後，使用者仍能用舊的有效 Refresh Token 換出帶著已失效 Organizer 權限的新 Access Token。

### 3. Organizer 成員關聯只有單一 `Owner` 角色，不分 Owner/Staff
`OrganizerMember { OrganizerId, MemberId, Role }`，`Role` enum 目前只有 `Owner` 一個值。

**理由**：YAGNI——目前沒有「同一 Organizer 底下需要限制部分成員只能做部分操作」的具體需求；等真的出現這類需求時再擴充 `Role` enum 與對應授權規則，不提前設計。

### 4. 切換操作情境端點刻意不採用「一律視同找不到」

後續依賴本次的變更（`event-management-organizer-scoping` 等）對訂單、票券、活動、銷售報表都要求「不屬於呼叫端目前 Organizer 時一律視同找不到（404），不回傳 403」，理由是這些是需要保護的租戶業務資料。但「會員可以切換目前操作中的 Organizer」需求刻意採用不同的區分：切換到非自己所屬的 Organizer 回傳 403、不存在回傳 404、狀態非 `Approved` 回傳狀態衝突（409）——三種狀態碼分開，不合併成統一的 404。

**這是刻意的取捨**：
- Organizer 本身的存在性與審核狀態，不是需要對其他 Member 保密的機敏業務資料；讓申請人知道「我的申請被駁回」「還在待審核」本來就是這個功能要對外呈現的核心語意，不能用統一的找不到含糊帶過
- Organizer Id 是 128 bit GUID，沒有可枚舉的序列空間，不存在像「訂單序號」那樣容易被猜測/枚舉進而刺探的風險
- 若真的對「非自己所屬的 Organizer」也採用 404，會讓合法情境（例如使用者不小心用錯瀏覽器分頁、或前端狀態過期導致嘗試切換到一個自己其實已被移除的 Organizer）難以與「純粹打錯 Id」區分，降低前端能呈現的錯誤訊息品質，而這裡沒有對應的租戶邊界保密需求需要犧牲這個可用性

## 邊界情況：切換與換發併發競爭同一筆 Refresh Token

若切換操作情境的請求與 `/api/auth/refresh` 的換發請求，恰好併發作用在同一筆 `RefreshToken` 資料列上（例如切換請求讀到 `Status = Active` 之後、`SaveChanges` 之前，該筆已被併發的 refresh 請求呼叫 `MarkAsUsed()` 標記為 `Used` 並產生新資料列），兩者對同一筆資料列的寫入會觸發既有 `RefreshTokenConfiguration` 已設定的 Postgres `xmin` 樂觀併發權杖檢查，其中一方的 `SaveChanges` 會拋出 `DbUpdateConcurrencyException`。

**明確定義的契約**：先完成的一方正常成功；後完成的一方捕捉 `DbUpdateConcurrencyException` 後 **一律回傳 401**（不分方向、不新增 409 分類），比照既有 `RefreshTokenHandler` 對「另一個併發請求已先一步消費了這個 Token」的既定回應（`RefreshTokenHandler.cs:68-72`），不覆寫對方已寫入的結果，不造成資料錯亂；使用者只需重新呼叫一次。`SwitchOrganizerContextHandler` 需依同一模式新增對稱的 `DbUpdateConcurrencyException` 捕捉。

這不是重新設計鎖定機制，只是驗證既有機制在「切換」與「換發」這個新組合下確實如預期運作，已補上對應的服務層整合測試（見 tasks.md）。

## 安全確認（CLAUDE.md 安全強制規則）

**輸入驗證**
- 外部輸入（申請 Organizer 的 `Name`；核准／駁回／停權端點路徑中的 Organizer Id；切換端點的 Refresh Token 明文）在 Application 層以 FluentValidation／DataAnnotations 驗證：`Name` 必填、去除頭尾空白後長度 1～100 字元；Organizer Id 為路由參數，由 ASP.NET Core 模型繫結驗證為合法 `Guid`
- 不存在任何拼接 SQL 或 shell 指令的情境；所有資料存取皆透過 EF Core LINQ

**資料庫**
- 全數透過 EF Core 參數化查詢，不使用 raw ADO.NET query
- N+1 查詢風險評估：「我的 Organizer 清單」「待審核清單」資料量級小，採單次 `Include`／join 查詢，不需要 `AsSplitQuery`

**權限**
- 「呼叫者是否為目標 Organizer 的成員」「目標 Organizer 是否為 `Approved`」等需要查詢資料庫才能判斷的規則，在 Application 層的 Handler 內執行
- 審核（核准／駁回／查詢待審清單）與停權端點的 `MemberRole.Admin` 角色檢查在 Controller 層執行，比照既有 `AdminEventsController`／`AdminOrdersController`／`AdminMembersController` 等既有 Admin 端點慣例，採 `[Authorize(Policy = AuthorizationPolicies.AdminOnly)]`（該 Policy 於 `Program.cs` 定義為 `policy.RequireRole("Admin")`），不使用 `Roles=` 直接寫法，也不進入 Handler 才判斷
- `OrganizerController` 的一般會員端點（申請、我的清單、切換操作情境，僅需「已登入」不需 Admin）比照既有 `TicketsController`／`OrdersController`／`MembersController` 慣例，在類別層級加 `[Authorize]`；本專案未設定全域 Fallback Authorization Policy，未標註 `[Authorize]` 的 Action 預設允許匿名存取，故此屬性為必要防線，不可遺漏
- 這些操作皆有可能被未授權使用者觸發的風險，已逐條列出對應的拒絕情境（403／404／409），見 `specs/organizer-management/spec.md`

**前端（Vue/React）**
- 新增畫面（申請表單、我的主辦方清單、審核清單頁）僅顯示後端回傳的 Organizer 名稱／狀態等文字內容，一律透過 Vue 預設的文字插值（`{{ }}`）渲染，不使用 `v-html`
- 新增端點呼叫沿用既有前端統一攔截器帶入 Auth Header 的機制，不另外處理

**機敏資訊管理**
- 本次無新增密碼、API Key 等機敏設定，`OrganizerId` claim 與既有 Role claim 同樣是非機敏的授權識別碼，記錄 log 時可比照既有慣例正常記錄，不需遮蔽

## Migration Plan

1. 新增 EF Core migration：建立 `Organizers`、`OrganizerMembers` 資料表；`RefreshTokens` 新增可為 null 的 `OrganizerId` 欄位與外鍵（刪除行為 `NoAction`／`Restrict`，不因 Organizer 被刪除而級聯刪除 Refresh Token）
2. **本次不觸碰 `Events` 資料表、不執行任何既有資料回填**——這是後續變更 `event-management-organizer-scoping` 的範圍（該變更需要先確認本次的 `Organizers`／`OrganizerMembers` 資料表已存在，才能執行既有 Admin 帳號的回填）
3. 開發/測試環境不需要任何既有 seed data 調整（本次沒有修改任何既有端點的授權前置條件）
4. Rollback：本次新增的資料表與欄位皆為獨立、無其他既有資料依賴（`RefreshTokens.OrganizerId` 為 nullable，還原不影響既有 Refresh Token 資料），可直接降版，不需要額外資料處理步驟

## Risks / Trade-offs

- [Access Token 換發時機的使用者體驗] → 本次僅在「申請成功」這一節點自動觸發一次切換嘗試（預期因 `Pending` 被拒絕）；核准通過後或切換到其他已核准 Organizer，皆需使用者在「我的主辦方」清單頁手動點選「切換」，本次不做進一步自動化
- [本次上線後、`event-management-organizer-scoping` 上線前的過渡期，自助申請的非 Admin 主辦方切換後仍無法進入一般後台] → 已在 proposal.md「Dependency」段落明確記錄，建議兩次部署間隔盡量短

## Open Questions

- 是否要在平台管理員審核清單加上「Organizer 申請時填寫的簡短說明／聯絡方式」欄位，方便審核判斷？本次先以最小欄位（僅 `Name`）實作，若審核時發現資訊不足，可在後續提案擴充
