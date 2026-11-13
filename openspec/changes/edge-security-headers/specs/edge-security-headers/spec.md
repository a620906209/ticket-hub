# Spec Delta

## Purpose

定義網頁邊界（nginx）對所有回應附加的瀏覽器安全標頭、條件式 HSTS 與文字壓縮，並明定前端與 API 同源、不開放跨來源存取，降低 XSS、點擊劫持與 MIME 嗅探風險。

## ADDED Requirements

### Requirement: 所有回應附帶瀏覽器安全標頭
系統 SHALL 對邊界 nginx 各 location（靜態資源、`index.html`、`/api/` 代理）產生或轉發的每一個回應，含其 4xx／5xx，附加 `Content-Security-Policy`、`X-Content-Type-Options: nosniff`、`X-Frame-Options: DENY`、`Referrer-Policy: strict-origin-when-cross-origin`、`Permissions-Policy: camera=(self), microphone=(), geolocation=()`。標頭 MUST 不因該路徑另有 `Cache-Control` 等標頭設定而遺漏。

#### Scenario: ESH-001 首頁回應帶全部安全標頭
- **WHEN** 請求 `GET /`
- **THEN** 回應帶上述五個標頭，值與本 Requirement 所列完全相同（CSP 值見 ESH-005）

#### Scenario: ESH-002 帶 hash 的靜態資源也帶安全標頭
- **WHEN** 請求 `GET /assets/<檔名>`（命中長期快取的 location）
- **THEN** 回應同時有 `Cache-Control: public, max-age=31536000, immutable` 與上述五個安全標頭

#### Scenario: ESH-003 API 代理回應帶安全標頭
- **WHEN** 請求 `GET /api/...`，不論上游回 200、4xx 或 5xx
- **THEN** 回應帶上述五個安全標頭，且每個標頭只出現一次

#### Scenario: ESH-004 邊界自身產生的錯誤回應帶安全標頭
- **WHEN** 請求 `/assets/` 下不存在的檔案而得到 404
- **THEN** 該 404 回應仍帶上述五個安全標頭

### Requirement: CSP 限縮資源來源且禁止嵌入
`Content-Security-Policy` 的值 SHALL 為：`default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data: blob:; connect-src 'self'; object-src 'none'; base-uri 'self'; form-action 'self'; frame-ancestors 'none'`。`script-src` MUST NOT 含 `'unsafe-inline'`、`'unsafe-eval'` 或任何外部來源；`style-src` 允許 `'unsafe-inline'` 的理由與限制見 design.md。

#### Scenario: ESH-005 CSP 值逐字相符
- **WHEN** 請求任一路徑
- **THEN** `Content-Security-Policy` 標頭值與本 Requirement 所列字串逐字相同

#### Scenario: ESH-006 買家、後台與核銷頁在 CSP 下正常運作
- **WHEN** 在真實瀏覽器經邊界 nginx 載入買家端頁面、後台頁面與核銷掃描頁並完成其主要操作
- **THEN** 瀏覽器 console 沒有任何 CSP 違規訊息，頁面功能（含核銷頁相機啟用）正常

### Requirement: 僅在請求經 HTTPS 抵達時送 HSTS
系統 SHALL 僅在入站請求帶 `X-Forwarded-Proto: https`（TLS 由前方代理終結）時，於回應附加 `Strict-Transport-Security: max-age=15552000`，不含 `includeSubDomains` 與 `preload`；其他情況 MUST NOT 送出該標頭。API 應用程式本身 MUST NOT 送出 `Strict-Transport-Security`，避免標頭重複。

#### Scenario: ESH-007 經 HTTPS 抵達的請求帶 HSTS
- **WHEN** 請求帶 `X-Forwarded-Proto: https`，路徑為靜態資源或 `/api/...`
- **THEN** 回應帶 `Strict-Transport-Security: max-age=15552000`，且只出現一次

#### Scenario: ESH-008 未經 HTTPS 的請求不帶 HSTS
- **WHEN** 請求不帶 `X-Forwarded-Proto`，或其值為 `http`
- **THEN** 回應沒有 `Strict-Transport-Security` 標頭

#### Scenario: ESH-009 API 直連不自行送 HSTS
- **WHEN** 直接請求 API（非 Development 環境，且帶 `X-Forwarded-Proto: https` 自受信任來源）
- **THEN** API 回應沒有 `Strict-Transport-Security` 標頭

### Requirement: 文字類回應啟用 gzip
系統 SHALL 對 `Accept-Encoding` 含 `gzip` 的請求，壓縮回應本文長度達 1024 位元組以上的 JavaScript、CSS、SVG、JSON 與 HTML 回應，並附 `Vary: Accept-Encoding`；其他內容類型與較小的回應 MUST NOT 被壓縮。

#### Scenario: ESH-010 大型文字資源被壓縮
- **WHEN** 請求 `Accept-Encoding: gzip` 分別取得大於 1024 位元組的 JavaScript 資源與 CSS 資源
- **THEN** 兩者回應皆帶 `Content-Encoding: gzip` 與 `Vary: Accept-Encoding`，解壓後內容與原檔相同

#### Scenario: ESH-011 小型回應不壓縮
- **WHEN** 請求 `Accept-Encoding: gzip` 取得小於 1024 位元組的文字回應
- **THEN** 回應沒有 `Content-Encoding`

#### Scenario: ESH-012 未宣告 gzip 的請求不壓縮
- **WHEN** 請求不帶 `Accept-Encoding`
- **THEN** 回應沒有 `Content-Encoding`

### Requirement: 前端與 API 同源，不開放跨來源存取
系統 SHALL 維持前端與 API 皆由同一個邊界來源提供，API MUST NOT 對任何來源送出 `Access-Control-Allow-*` 標頭，也不處理 CORS 預檢請求。

#### Scenario: ESH-013 跨來源請求得不到 CORS 許可標頭
- **WHEN** API 收到帶 `Origin: https://evil.example` 的請求，或對同一路徑的 `OPTIONS` 預檢請求（帶 `Access-Control-Request-Method`）
- **THEN** 回應沒有任何 `Access-Control-Allow-*` 標頭
