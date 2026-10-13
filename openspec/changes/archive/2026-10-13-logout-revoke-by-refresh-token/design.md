## Context

- `AuthController.Logout` 有 `[Authorize]`，呼叫 `LogoutHandler.HandleAsync(User.GetMemberId(), request, ct)`。
- `LogoutHandler` 用 `TokenHash == hash && MemberId == memberId` 查詢，找到就 `Revoke()` + `SaveChangesAsync`，一律回 `Result.Success()`；沒有捕捉 `DbUpdateConcurrencyException`。
- `RefreshToken` 有 xmin 樂觀併發權杖；`Revoke()` 為冪等；`TokenHash` 有唯一索引。
- `/api/auth/refresh` 匿名、沒有 rate limit，非 Active Token 視為重複使用 → 撤銷該會員全部 Token；併發衝突回 401。
- 前端 `stores/auth.ts` 的 logout 先清空本地狀態再呼叫 API，API 失敗吞掉（best-effort）。

## Goals / Non-Goals

**Goals**
- Access Token 過期或不存在時，登出仍能撤銷伺服器端 Refresh Token。
- 登出不揭露 Token 是否存在；併發衝突不再回 500。

**Non-Goals**
- 全裝置登出（撤銷該會員所有 Token）。
- 前端「refreshSession 進行中同時登出」的競態：換發回應晚於登出到達時，store 會把新 Refresh Token 寫回 localStorage。這是既有行為，與本次後端問題無關，不在本 change 處理。

## Decisions

### 決策 1：憑 Refresh Token 本身撤銷，拿掉 `[Authorize]` 與 MemberId 條件

Refresh Token 是 256-bit 隨機不透明 Token，持有者本來就能用它換發 Access Token，等同已證明對該登入工作階段的控制權。要求同時持有有效 Access Token 並不增加安全性，反而讓過期情境無法撤銷。

- 替代方案 A：保留 `[Authorize]`，前端在 401 時先換發再登出——前端已經清空 Refresh Token，且換發會輪替出新 Token，流程更長、失敗點更多。不採用。
- 替代方案 B：同時支援「有 Access Token 時比對 MemberId」——沒有安全上的好處，只增加分支。不採用。

### 決策 2：只撤銷 Active Token；不存在／非 Active 一律 204

- Active → `Revoke()` + 儲存 → 204。
- Used／Revoked／查無 → 不寫入 → 204。回應相同，不提供「Token 是否存在」的探測管道。
- 不對 Used Token 觸發全面撤銷：重複使用偵測屬於換發端點的職責；登出呼叫者是在「放棄」Token，不是「使用」Token。之後若有人拿這個 Used Token 去換發，換發端點仍會照既有規則偵測。
- 只對 Active 寫入，也縮小與換發／切換併發衝突的範圍。

### 決策 3：併發衝突回 409

撤銷時 SaveChanges 拋出 `DbUpdateConcurrencyException`（換發或切換操作情境先一步提交了同一列），捕捉後：
- 記錄 Warning（只記 TokenId 與 MemberId，不記 Token 明文或雜湊）；
- 回 `Error.Conflict`（409），訊息為「登出時 Token 狀態已變更」。不寫「請重試」：瀏覽器前端在呼叫前已清空本地 Refresh Token，實際上無法重試。

- 替代方案 A：視為成功回 204——對方可能是換發（舊 Token 已變 Used、新 Token 為 Active），此時登出實際上沒有撤銷到任何可用的 Token，回成功等於靜默失敗，違反 CLAUDE.md「禁止靜默失敗」。
- 替代方案 B：衝突後重新查詢並撤銷後繼 Token（`PreviousTokenId`）——`IApplicationDbContext` 沒有 Reload／ChangeTracker 存取，需要擴充介面；觸發條件是「同一個用戶端同時登出又換發」，屬罕見情境，不值得這個複雜度。不採用。
- 殘餘風險：衝突發生時伺服器端仍可能留有 Active Token。前端登出為 best-effort，不會重試；這與決策前的 500 行為相同或更好（至少有明確狀態碼與 log）。

### 決策 4：登出端點不加 rate limit

與 `/api/auth/refresh` 一致：Token 為 256-bit 隨機值，無法暴力猜測；查詢走唯一索引；不存在時不寫入 DB。新增 rate limit 會讓共用 NAT 的正常使用者登出失敗。接受此風險。

### 決策 5：不新增 Validator

`LogoutRequest(string RefreshToken)` 與 `RefreshTokenRequest` 一致不設 FluentValidation。缺少 `refreshToken` 欄位或為 `null` 時由 `[ApiController]` 的 nullable 參考型別模型驗證回 400；空字串雜湊後查無資料 → 204。

### 決策 6：前端改為 `skipAuth: true` 並移除 hotfix 的 access token 傳遞

`authApi.logout(refreshToken)` 比照 `refresh()` 使用 `skipAuth: true`；`RequestOptions.accessToken` 只有登出使用，一併移除（含 httpClient 的優先序測試）。store 的 logout 維持「本地狀態清空優先、API best-effort」。

## 安全確認

**輸入驗證**
- 外部輸入只有 body 的 `refreshToken` 字串。驗證在 WebApi 層：`[ApiController]` 的 nullable 模型驗證，缺欄位或 null 回 400（決策 5）。不另設 FluentValidation，與 `/refresh` 一致。
- 字串只拿去 SHA-256 雜湊，不拼接進 SQL 或 shell 指令。

**資料庫**
- 查詢使用 EF Core LINQ（`FirstOrDefaultAsync(t => t.TokenHash == hash)`），由 Npgsql 參數化，走 `TokenHash` 唯一索引。
- 單筆查詢、不載入導覽屬性，沒有 N+1 風險。
- 寫入只有 Active → Revoked 一筆，受 xmin 樂觀併發保護（決策 3）。

**權限**
- 端點標記 `[AllowAnonymous]`，不做角色或會員權限檢查。授權依據是「持有 Refresh Token 明文」這件事本身，在 Application 層（Handler）以雜湊比對執行。
- 任何人都能呼叫這個端點，但只有知道某個 Token 明文的人才能撤銷它。這個人本來就能用該 Token 換發、使用那個工作階段，撤銷不會讓他取得更多權限。
- 不知道明文的呼叫者只會得到與成功相同的 204，無法探測 Token 是否存在，也無法影響任何資料（決策 2）。

**前端**
- 登出 API 改為 `skipAuth: true`，刻意不帶 Authorization header：後端不再需要它，而且呼叫時 store 已清空。
- 這次改動不渲染任何使用者輸入到 DOM，沒有新增 XSS 面。
- Refresh Token 仍存 localStorage（既有設計，不在本 change 範圍）。

**Log**
- 衝突時的 Warning 只記 TokenId 與 MemberId，不記 Token 明文或雜湊（AUTH-LOGOUT-005，3.1 測試鎖定）。

## Risks / Trade-offs

- 任何持有 Refresh Token 的人都能撤銷它 → 持有者本來就能用它換發並使用該工作階段，撤銷不會提升權限，只會讓 Token 失效。
- 決策 3 的殘餘風險，見上。
- 登出一個 Used Token 時，不會撤銷它的後繼 Active Token。情境：前端換發成功、伺服器已輪替，但新 Token 沒存到本地（例如換發回應遺失），之後用舊 Token 登出 → 204，後繼 Token 仍為 Active，直到自然過期或有人拿舊 Token 換發觸發重複使用偵測。觸發條件罕見，接受此風險。
- 登出後若另一個分頁或舊用戶端仍拿同一個（已撤銷的）Token 呼叫換發，會觸發換發端點的重複使用偵測，撤銷該會員所有 Token。這是 `[Authorize]` 時期就存在的既有行為；改為匿名登出後，Token 外洩時攻擊者更容易觸發，但結果只是讓受害者全裝置重新登入，不會取得權限。接受此風險。
- 後續追蹤：上述兩條「後繼 Active Token 未撤銷」的殘餘風險，日後若做「全裝置登出」或「撤銷 `PreviousTokenId` 鏈」的 change 一併處理；若日後為 `/refresh` 加 rate limit，`/logout` 一併評估。
- 實作時 MUST NOT 對 `LogoutRequest` 做結構化解構日誌（如 `{@Request}`），避免 Token 明文進 log。
- Active 但 `ExpiresAt` 已過的 Token 仍會被改為 Revoked 並回 204。結果與不撤銷相同（換發端點本來就會拒絕過期 Token），只多一次寫入，不另外分支。

## Migration Plan

無 DB 變更。前後端同一個 commit 部署；舊前端（帶 Authorization header）呼叫新後端仍正常運作（`[AllowAnonymous]` 端點忽略驗證結果）。
