## Why

登出端點目前標記 `[Authorize]`，Handler 以「Token 雜湊 + 目前登入者 MemberId」查詢要撤銷的 Refresh Token。前端登出採「本地狀態清空優先」（ticketing-web-ui design.md 決策 5），API 呼叫是 best-effort。結果是：

- Access Token 已過期（預設 15 分鐘）時，登出 API 回 401，前端吞掉錯誤並清空本地 Refresh Token，**伺服器端 Refresh Token 未被撤銷**，在有效期內（14 天）仍可被拿去換發新 Access Token。
- 0739403 hotfix 只修了「清空後才呼叫 API 導致不帶 Authorization」的問題，過期情境仍是已知殘餘風險（`web/src/stores/auth.ts` 的 TODO(2026-10-13)）。
- 登出與換發／切換操作情境併發寫入同一筆 `RefreshToken` 時，`DbUpdateConcurrencyException` 未被捕捉，回 500。

Refresh Token 本身就是持有者憑證（bearer credential）：持有它的人本來就能拿它換發 Access Token，因此「憑 Refresh Token 本身撤銷它」不需要額外的身分驗證（比照 RFC 7009 Token Revocation 的精神）。

## What Changes

- **BREAKING（API 行為）登出端點改為匿名可呼叫**：拿掉 `[Authorize]`，不再要求 Access Token；只憑請求內的 Refresh Token 明文雜湊比對撤銷。不帶或帶過期 Access Token 都能完成撤銷。
- **撤銷條件不再比對 MemberId**：Handler 簽章移除 `memberId` 參數。
- **一律回 204（衝突除外）**：Token 不存在、已使用（Used）、已撤銷（Revoked）都回 204，不揭露 Token 是否存在；只有 Active 的 Token 會被改為 Revoked，其他狀態不寫入。
- **併發衝突明確失敗**：撤銷時遇到樂觀併發衝突，捕捉後回 409，不再是 500。
- **前端移除 hotfix 的 access token 傳遞**：`authApi.logout` 改為 `skipAuth: true`、只帶 Refresh Token；移除 `RequestOptions.accessToken` 與相關測試、store 的 `currentAccessToken` 暫存與 TODO 註解。
- **不包含**：登出時撤銷該會員所有 Refresh Token（全裝置登出）、登出端點的 rate limit（理由見 design.md 決策 4）、前端「換發進行中同時登出」的競態（既有行為，見 design.md 非目標）。

## Capabilities

### New Capabilities

（無）

### Modified Capabilities

- `authentication`：重新命名並修改「已登入使用者可以登出」→「持有 Refresh Token 者可以登出並撤銷該 Token」，改為不要求 Access Token、補上不存在／非 Active／併發衝突／缺少欄位的情境。

## Impact

- 後端：`src/ProjectC.WebApi/Controllers/AuthController.cs`（logout action 屬性與呼叫）、`src/ProjectC.Application/Authentication/Logout/LogoutHandler.cs`。
- 前端：`web/src/api/auth.ts`、`web/src/api/httpClient.ts`、`web/src/stores/auth.ts`。
- 測試：`LogoutHandlerTests.cs`、`AuthControllerTests.cs`（`Logout_WithoutAccessToken_Returns401` 改寫）、新增 Postgres 併發測試、`web/src/stores/auth.test.ts`、`web/src/api/httpClient.test.ts`（移除 accessToken 優先序測試）。
- 不需 migration；不影響 `admin-web-ui` AWU-MOBILE-NAV-006 與買家端登出的 UI 行為（仍是本地先清空、導向 `/login`）。
