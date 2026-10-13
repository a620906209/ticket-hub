## AC ↔ 測試對應

| AC | 測試任務 |
| --- | --- |
| AUTH-LOGOUT-001 | 1.1、2.1 |
| AUTH-LOGOUT-002 | 1.1、2.2、2.3、2.6 |
| AUTH-LOGOUT-003 | 1.2、2.4 |
| AUTH-LOGOUT-004 | 1.3、1.4 |
| AUTH-LOGOUT-005 | 3.1、3.2 |
| AUTH-LOGOUT-006 | 2.5 |
| AUTH-LOGOUT-007 | 1.5 |

前端改動（design.md 決策 6）不改變使用者可見行為，由 5.1–5.3 鎖定。

## 1. Application 單元測試（先寫，確認失敗）

- [x] 1.0 `LogoutHandler` 建構子新增 `ILogger<LogoutHandler>`（決策 3 的 Warning log 需要）；既有 `LogoutHandlerTests` 以 `NullLogger<LogoutHandler>.Instance` 建構。
- [x] 1.1 `LogoutHandlerTests`：改為新簽章 `HandleAsync(LogoutRequest, ct)`；`HandleAsync_WithActiveToken_RevokesTokenWithoutMemberContext`——Active Token 屬於任一會員，Handler 不帶 memberId 也能撤銷，狀態變 Revoked。
- [x] 1.2 `HandleAsync_WithUnknownToken_SucceedsWithoutSaving`——回 Success，且未呼叫 `SaveChangesAsync`（Fake 記錄呼叫次數）。
- [x] 1.3 `HandleAsync_WithUsedToken_SucceedsWithoutChangingAnyToken`——Used Token 狀態維持 Used，同會員另一個 Active Token 維持 Active，未呼叫 `SaveChangesAsync`。失敗分支：若實作對 Used 呼叫 `Revoke()` 或觸發全面撤銷，狀態斷言失敗。
- [x] 1.4 `HandleAsync_WithRevokedToken_SucceedsWithoutSaving`——Revoked Token 維持 Revoked，同會員另一個 Active Token 維持 Active，未呼叫 `SaveChangesAsync`。
- [x] 1.5 `HandleAsync_WithActiveButExpiredToken_RevokesToken`——`ExpiresAt` 已過的 Active Token 仍改為 Revoked、呼叫一次 `SaveChangesAsync`。失敗分支：若實作以「未過期」作為撤銷條件，狀態斷言失敗。

## 2. WebApi 整合測試（`AuthControllerTests`）

- [x] 2.1 既有 `Logout_ThenRefreshWithSameToken_Returns401` 保留（AUTH-LOGOUT-001）。
- [x] 2.2 `Logout_WithoutAccessToken_Returns401` 改寫為 `Logout_WithoutAccessToken_RevokesTokenAndReturns204`：登入取得 Token，不帶 Authorization 呼叫登出 → 204；再用同一 Token 換發 → 401。
- [x] 2.3 `Logout_WithInvalidAccessToken_RevokesTokenAndReturns204`：帶簽章無效的 Bearer 字串 → 204；之後換發 → 401。
- [x] 2.4 `Logout_WithUnknownToken_Returns204`。
- [x] 2.5 `Logout_WithoutRefreshTokenField_Returns400`（Theory）：送 `{}` 與 `{"refreshToken":null}` → 皆 400。
- [x] 2.6 `Logout_WithExpiredAccessToken_RevokesTokenAndReturns204`：比照 `EventManagementAuthorizationMatrixTests` 的 private `CreateSignedAccessToken`（不能直接呼叫，抽到 WebApi.Tests 共用的測試支援類別，兩邊共用）的 `CreateSignedAccessToken(..., expiresAtUtc: DateTime.UtcNow.AddMinutes(-5))`，用測試環境簽章金鑰簽發同一會員的過期 JWT 呼叫登出 → 204；之後換發 → 401。同一測試內以同 claim、未過期的 Token 呼叫受保護端點（`/api/members/me`）成功作為對照組，佐證 Token 只差在過期；以過期 Token 呼叫 `/api/members/me` 回 401，佐證它確實會被 JwtBearer 拒絕。

## 3. Infrastructure 併發測試（Testcontainers Postgres）

- [x] 3.1 新增 `LogoutConcurrencyTests.HandleAsync_WhenRefreshCommitsFirst_ReturnsConflict`：比照 `OrganizerRefreshSwitchConcurrencyTests` 的 identity resolution 手法，輸家 DbContext 先載入 Active Token，贏家用 `RefreshTokenHandler` 完成換發並提交，再執行輸家的 `LogoutHandler` → 回 `ErrorType.Conflict`，不拋例外；DB 中舊 Token 為 Used、後繼 Token 為 Active（鎖定決策 3 的殘餘風險描述）。Handler 以可捕捉的 `ILogger` 測試替身建構，斷言 Warning 恰好一筆，且格式化訊息與所有結構化屬性值都不含 Token 明文與其雜湊字串。失敗分支：拿掉 catch 時測試因未處理例外失敗；log 帶入 Token 時字串比對失敗。
- [x] 3.2 `HandleAsync_WhenSwitchContextCommitsFirst_ReturnsConflict`：同手法，贏家改為 `SwitchOrganizerContextHandler` 先提交（比照 `OrganizerRefreshSwitchConcurrencyTests` 建構方式）→ 輸家 `LogoutHandler` 回 `ErrorType.Conflict`，Warning 恰好一筆且不含 Token；DB 中該 Token 仍為 Active 且 `OrganizerId` 為切換結果（輸家寫入未覆蓋贏家）。
- [x] 3.3 `AuthControllerTests` 不另測 409：HTTP 對應由既有 `ResultExtensions`（Conflict → 409）負責，該對應已被其他端點的整合測試涵蓋；實作時以 grep 確認至少有一個既有整合測試斷言 409 經由 `ErrorType.Conflict`，若無則補一個以 mock Handler 觸發的 Logout 409 測試。

## 4. 後端實作

- [x] 4.1 `LogoutHandler`：移除 `memberId` 參數與 MemberId 條件；只在 `Status == Active` 時 `Revoke()` + 儲存；捕捉 `DbUpdateConcurrencyException` → Warning log（TokenId、MemberId，不含 Token）+ `Error.Conflict`。
- [x] 4.2 `AuthController.Logout`：`[Authorize]` 改為 `[AllowAnonymous]`，呼叫新簽章。
- [x] 4.3 確認 1.x、2.x、3.x 全部通過；後端全套測試通過。

## 5. 前端

- [x] 5.1 `stores/auth.test.ts`：`logout_WhenLoggedIn_SendsPreClearAccessTokenAfterClearingLocalState` 改為 `logout_WhenLoggedIn_CallsApiWithRefreshTokenOnlyAfterClearingLocalState`（`toHaveBeenCalledWith('refresh-1')`，呼叫當下 store 與 localStorage 已清空）；過期情境測試改名與註解移除「殘餘風險」描述，仍鎖定 API 失敗時完成本地登出、不換發。
- [x] 5.2 新增 `web/src/api/auth.test.ts`（若不存在）：`logout_SendsRequestWithoutAuthorizationHeader`——mock `request`，斷言以 `skipAuth: true` 呼叫 `/auth/logout`，body 只含 `refreshToken`。
- [x] 5.3 `httpClient.test.ts` 移除 accessToken 優先序測試；`httpClient.ts` 移除 `RequestOptions.accessToken`；`api/auth.ts` logout 改 `skipAuth: true`；`stores/auth.ts` 移除 `currentAccessToken` 與 TODO 註解。
- [x] 5.4 前端全套測試與 type-check 通過。

## 6. 收尾

- [x] 6.1 套用 hardener 檢查清單（LogoutHandler）。
- [x] 6.2 strict-reviewer。
- [x] 6.3 確認 openspec validate --strict 通過，回報 spec 同步狀態。
