## Why

核銷 API（`PATCH /api/admin/tickets/{id}/redeem`）已於 `ticket-issuance-and-redemption` 完成並上線，但目前僅能透過 API 工具直接呼叫，現場工作人員沒有可用介面。`docs/project-scope.md` 第 51-57 行將「現場核銷掃碼前端頁面」列為 Phase 3（Could）項目；Phase 1（Must）與 Phase 2（Should）已全數完成，這是補齊「建立活動 → 買家下單 → 出票 → 核銷」端到端可視化 demo 最後一段缺口的下一步。

## What Changes

- 新增 Admin 後台頁面：使用裝置相機（含 iOS Safari）掃描票券 QR Code，導覽選單補上入口
- 掃描到 QR 內容後，解析出 Ticket ID 與簽章（QR 內容格式為 `{TicketId:D}.{Base64Url(HMAC簽章)}`，見 `ticket-issuance-and-redemption` design.md 決策 3），連同簽章一併呼叫核銷端點完成核銷
- 核銷端點（`PATCH /api/admin/tickets/{id}/redeem`）新增可選 `signature` 欄位：有提供時後端呼叫既有 `ITicketSigningService.TryVerify` 驗證未被竄改，驗證失敗回傳 400 且不變更 Ticket 狀態；未提供時行為與現況完全相同（**BREAKING 無**——純新增可選欄位，既有無 body 呼叫端向下相容）
- 依核銷端點回應（204 成功／409 已核銷過或狀態衝突／404 查無此票／400 簽章無效或格式不合法／其他錯誤）顯示對應且可分辨的結果訊息（不僅依賴顏色），並可連續掃描下一張，不需重新整理頁面
- 提供手動輸入 Ticket ID 的備援輸入方式，供相機不支援、權限不可用或 QR code 毀損時使用；手動輸入路徑不附帶簽章，沿用既有「Admin 角色即信任邊界」模型（design.md 決策 2 明確記錄此信任邊界，非本次新增的攻擊面）
- 前端 API 型別／service 層補上核銷端點（`web/src/types/api.generated.ts` 目前未涵蓋此端點，比照既有 `web/src/api/admin.ts` 慣例手動補上）；`web/src/api/httpClient.ts` 補上 `PATCH` method 型別

## Capabilities

### New Capabilities
（無）

### Modified Capabilities
- `admin-web-ui`: 新增「Admin 可透過介面掃描 QR Code 核銷票券」與「掃描期間與相機不可用時皆可切換到手動輸入 Ticket ID 完成核銷」需求，涵蓋掃描、核銷結果顯示、連續掃描、手動輸入備援、導覽入口
- `ticket-redemption`: 核銷端點新增可選的 QR 簽章驗證——見 design.md 決策 2
- `ticket-issuance`: 既有「每張票券可依 Ticket ID 按需產生 QR Code 內容以 HMAC 簽章防偽」需求文字補上精確的 payload 格式契約（分隔符、Ticket ID 格式、簽章編碼），不改變既有行為，僅把先前只存在於已封存 `ticket-issuance-and-redemption` design.md 的格式細節同步進權威 spec，供本次前端 parser／後端驗證雙方對齊

## Impact

- **前端**：新增 `web/src/pages/admin` 下的核銷掃碼頁面元件、路由與導覽入口（限 Admin 角色，比照既有 `admin-web-ui` 路由守衛規則）、`web/src/api/admin.ts` 補上核銷 API 呼叫、`httpClient.ts` 補上 `PATCH` method 型別
- **相依套件**：新增 npm 依賴 `jsqr`（跨瀏覽器 canvas-based QR 解碼，見 design.md 決策 1——原生 `BarcodeDetector` 因 Safari 不支援而不採用）
- **後端**：`RedeemTicketHandler`（Application 層）新增可選簽章驗證分支，注入既有 `ITicketSigningService`；Controller/Request 新增可選 `signature` 欄位；無 DB schema 異動，向下相容既有呼叫端
- **不受影響**：`ticket-issuance`（QR 內容產生與簽章邏輯本身不變，只是新增了消費者）
