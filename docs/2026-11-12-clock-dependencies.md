# 時鐘依賴與 Logout 匿名決策（2026-11-12）

> 來源：`docs/review/TODO.md` 的 H-11 與 M-16。此文件日後併入 runbook（H-10）。

## 1. 系統依賴時鐘的位置

| 位置 | 依賴方式 | 時鐘偏移的症狀 |
|---|---|---|
| JWT 驗證（`AuthenticationServiceCollectionExtensions.cs`，`ClockSkew = 30s`） | API 以節點時鐘判斷 access token 是否過期 | 偏移超過 30 秒：剛簽發的 token 被判過期（401），或已過期 token 仍被接受 |
| 訂單保留期限 `HeldUntilUtc` | 建單時以 API 節點時鐘寫入，過期清理與確認訂單時以時鐘比對 | 多副本時鐘不一致：訂單提早被清理，或逾期仍可確認 |
| Redis TTL（驗證碼、查詢快取、分散式鎖、排隊名單） | TTL 由 Redis 節點時鐘計算 | Redis 與 API 節點偏移：鎖過早釋放或殘留，驗證碼提早失效 |
| 限流視窗 | API 節點時鐘 | 計數視窗不穩定，偶發 429 |

## 2. 運維要求

- 所有節點（K8s worker、VPS）啟用 NTP 同步（chrony / systemd-timesyncd）。
- 告警門檻：節點時鐘偏移 > 1 秒（node_exporter 的 `node_timex_offset_seconds`；metrics 導入前由人工巡檢 `chronyc tracking`）。
- 已知環境問題：WSL2 時鐘曾間歇快 2–3% 並每約 30 秒倒退約 1 秒，只影響本機開發與測試（時間型測試已改成容忍此情況），不代表正式環境。

## 3. Logout 維持匿名（M-16 決策）

`POST /api/auth/logout` 維持 `[AllowAnonymous]`，只憑 refresh token 撤銷。

- **理由**：前端登出時 access token 常已過期；若要求有效 access token，使用者會登不出去（見 `logout-revoke-by-refresh-token` design.md 決策 1）。
- **騷擾向量評估**：撤銷他人 session 需持有對方的 refresh token（高熵不透明 token，資料庫僅存雜湊）。能拿到它的攻擊者本來就能直接使用該 session，額外騷擾能力有限。
- **重新評估條件**：若改為 refresh token 放 HttpOnly cookie（TODO M-2），登出端點需一併設計 CSRF 對策，屆時重新檢視。
