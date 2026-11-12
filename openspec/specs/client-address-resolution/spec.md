# client-address-resolution Specification

## Purpose
定義 API 部署在反向代理之後時如何還原真實用戶端位址與通訊協定，使依來源 IP 分區的功能（限流等）能區分不同用戶端，同時不讓不受信任的來源偽造位址。

## Requirements

### Requirement: 只還原受信任代理送來的 X-Forwarded-For 與 X-Forwarded-Proto
系統 SHALL 僅在直接連線來源位於 `ForwardedHeaders:TrustedNetworks` 所列網段時，才依 `X-Forwarded-For` 還原用戶端位址（`HttpContext.Connection.RemoteIpAddress`）、依 `X-Forwarded-Proto` 還原 `HttpContext.Request.Scheme`；只還原一層（`ForwardLimit = 1`），不處理其他 `X-Forwarded-*` 標頭。來源不在信任網段時，這兩個標頭 MUST 被忽略，`RemoteIpAddress` 維持實際連線位址。還原 MUST 發生在 HTTPS 轉址與限流之前，使其後的元件看到還原後的值。

#### Scenario: CAR-001 受信任來源的 X-Forwarded-For 被還原
- **WHEN** 直接連線來源位於信任網段，請求帶 `X-Forwarded-For: 203.0.113.7`
- **THEN** 後續元件看到的 `RemoteIpAddress` 為 `203.0.113.7`

#### Scenario: CAR-002 非信任來源的 X-Forwarded-For 被忽略
- **WHEN** 直接連線來源不在信任網段，請求帶 `X-Forwarded-For: 203.0.113.7`
- **THEN** `RemoteIpAddress` 維持實際連線位址，標頭不影響任何依來源 IP 的行為

#### Scenario: CAR-003 只還原一層
- **WHEN** 受信任來源送來 `X-Forwarded-For: 198.51.100.9, 203.0.113.7`
- **THEN** `RemoteIpAddress` 為最後一個（最靠近代理）的 `203.0.113.7`，不採用用戶端自行附加在前面的值

#### Scenario: CAR-004 未帶標頭時行為不變
- **WHEN** 請求不帶 `X-Forwarded-For`（例如開發環境的 Vite proxy 或直連）
- **THEN** `RemoteIpAddress` 為實際連線位址，行為與未啟用還原時相同

#### Scenario: CAR-009 受信任來源的 X-Forwarded-For 無法解析時維持實際連線位址
- **WHEN** 受信任來源送來 `X-Forwarded-For` 的最後一個值無法解析為 IP 位址（例如 `garbage`、空字串、超長字串、`203.0.113.7, garbage`）
- **THEN** 請求不得因此失敗（不回 5xx、不拋未處理例外），`RemoteIpAddress` 維持實際連線位址，請求落入與未帶標頭相同的限流分區

#### Scenario: CAR-013 還原早於 HTTPS 轉址
- **WHEN** 已設定 HTTPS 轉址的連接埠，受信任來源帶 `X-Forwarded-Proto: https` 請求非探針路徑（例如 `GET /api/events`）
- **THEN** 請求不被轉址（非 3xx）；對照：同一設定下無標頭的請求，或非信任來源帶同標頭的請求，仍被轉址至 HTTPS

#### Scenario: CAR-011 最後一個值合法時依框架行為還原
- **WHEN** 受信任來源送來 `X-Forwarded-For: garbage, 203.0.113.7`（前面值非法、最後值合法），或最後值為 `ip:port` 形式（`203.0.113.7:8080`）
- **THEN** `RemoteIpAddress` 依 ASP.NET Core ForwardedHeaders 的既有解析行為還原（預期為 `203.0.113.7`）；實作時對照已安裝套件實測確認，若實際行為不同，以實測為準並更新本 Scenario

#### Scenario: CAR-012 X-Forwarded-Host 不改寫 Host
- **WHEN** 受信任來源帶 `X-Forwarded-Host: evil.example`
- **THEN** `Request.Host` 維持原值

#### Scenario: CAR-010 受信任來源的 X-Forwarded-Proto 還原 Scheme，非信任來源則忽略
- **WHEN** 受信任來源帶 `X-Forwarded-Proto: https`，或非信任來源帶 `X-Forwarded-Proto: https`
- **THEN** 前者 `Request.Scheme` 為 `https`；後者 `Scheme` 維持實際連線的 scheme

### Requirement: 信任網段由設定提供，預設為私有網段與 loopback，設定錯誤時啟動失敗
系統 SHALL 以 `ForwardedHeaders:TrustedNetworks`（CIDR 字串陣列）設定可信任的代理網段；設定缺漏時預設為 `10.0.0.0/8`、`172.16.0.0/12`、`192.168.0.0/16`、`127.0.0.0/8` 與 `::1/128`。任一項不是合法 CIDR 時，應用程式 MUST 在啟動時失敗並指出錯誤項目，不得略過該項繼續啟動；已明確設定但為空陣列（或含空字串項目）視為誤設定，同樣 MUST 啟動失敗；「設定缺漏」指該設定鍵完全不存在，與「明確設定為空」以設定鍵是否存在區分。

#### Scenario: CAR-005 設定缺漏時採用預設網段
- **WHEN** 設定未提供 `ForwardedHeaders:TrustedNetworks`
- **THEN** 系統以預設網段運作，來自 `10.x`、`172.16–31.x`、`192.168.x`、loopback 的連線視為受信任

#### Scenario: CAR-006 自訂網段取代預設
- **WHEN** 設定 `ForwardedHeaders:TrustedNetworks` 為 `["10.1.0.0/16"]`，來源為 `192.168.1.5` 的請求帶 `X-Forwarded-For`
- **THEN** 該標頭被忽略（預設網段不再生效）

#### Scenario: CAR-007 非法 CIDR 或空陣列時啟動失敗
- **WHEN** `TrustedNetworks` 含非法字串（例如 `"not-a-cidr"`）、含空字串項目，或明確設定為空陣列
- **THEN** 應用程式啟動失敗，錯誤訊息指出 `ForwardedHeaders:TrustedNetworks`

### Requirement: 轉送標頭只影響來源位址與通訊協定，不得影響探針
還原後的 `RemoteIpAddress`／`Scheme` SHALL 不改變健康檢查探針路徑（`/health/live`、`/health/ready`）既有的「不受 HTTPS 轉址影響」行為；`Host` 標頭不被轉送標頭改寫。

#### Scenario: CAR-008 帶 X-Forwarded-Proto: http 的探針不被轉址
- **WHEN** 受信任來源以 `X-Forwarded-Proto: http` 請求 `/health/live`
- **THEN** 回應為探針結果（不是 HTTPS 轉址的 3xx）
