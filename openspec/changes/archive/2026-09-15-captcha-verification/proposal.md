## Why

現有防搶票機制（rate limiting、排隊機制、Redis 分散式鎖）皆針對「已知請求量」做流量管制，但無法區分請求是真人操作還是自動化腳本；機器人仍可在限流門檻內大量註冊帳號、登入、或搶佔排隊名額，稀釋真人買家的機會。導入驗證碼可在這些高風險端點補上「真人驗證」這一層，與既有 rate limiting 互補（前者擋量、後者擋機器人），呼應本專案「防黃牛/防機器人搶票」的核心目標。

## What Changes

- 新增自建圖形驗證碼能力：伺服器產生驗證碼圖片與對應答案，答案以短時效、一次性 token 形式暫存（Redis，依活動人數量級不需另建資料庫實體），呼叫端提交 token 與使用者輸入的驗證碼文字進行核對
- 驗證碼圖片與 token 皆有明確時效（逾時視為失效，須重新取得）
- token 為一次性：驗證成功或驗證失敗皆立即失效，不可重複使用同一 token 再次嘗試
- 註冊會員帳號端點（`member-management`）新增驗證碼檢查：未提供或驗證碼錯誤 MUST 拒絕註冊
- 登入端點（`authentication`）新增驗證碼檢查：未提供或驗證碼錯誤 MUST 拒絕登入，此檢查與既有登入 rate limiting（`api-rate-limiting`）各自獨立生效，不互相取代
- 加入購票排隊端點（`purchase-queue`）新增驗證碼檢查：未提供或驗證碼錯誤 MUST 拒絕加入排隊
- 買家前端（`buyer-web-ui`）在註冊頁、登入頁、加入排隊操作新增驗證碼圖片顯示、刷新、輸入欄位，並將 token 與使用者輸入一併送出
- Redis 無法連線時採 fail-closed（拒絕該次操作，回傳明確技術性錯誤），不沿用既有查詢快取／分散式鎖的 fail-open 慣例——驗證碼是防機器人的唯一防線，fail-open 形同該次故障期間完全撤除防護（詳見 design.md 決策 5）

## Capabilities

### New Capabilities
- `captcha-verification`：驗證碼產生（圖片、答案）、暫存（含時效與一次性）、核對三項核心能力，並定義供其他能力呼叫的介面

### Modified Capabilities
- `member-management`：「使用者可以註冊會員帳號」Requirement 新增驗證碼檢查前提
- `authentication`：「會員可以使用 Email 與密碼登入」Requirement 新增驗證碼檢查前提
- `purchase-queue`：「買家可加入活動的購票排隊」Requirement 新增驗證碼檢查前提
- `buyer-web-ui`：「買家可透過介面註冊與登入」與加入排隊相關 Requirement 新增驗證碼元件顯示與送出邏輯

## API 摘要（快速參考，詳細行為以各能力 spec 為準，發生落差時以 spec 為準）

### `GET /api/captcha`（匿名可存取）
- 成功：`200`，Content-Type `application/json`，body `{ "token": string（GUID）, "imageBase64": string（PNG bytes，不含 `data:` 前綴，前端自行組成 `data:image/png;base64,...` 使用）}`，不含答案明文
- `429 Too Many Requests`：同一來源 IP 超過新增的 `captcha` policy 限流門檻（獨立於既有 `login` policy，不共用計數，見 design.md 決策 6），含 `Retry-After`（CAPTCHA-RATE-001）
- `5xx`：Redis 無法連線時，由既有全域 `IExceptionHandler` 轉換的技術性錯誤格式，不回傳 `token` 或圖片（CAPTCHA-FAIL-001）

### 登入／註冊／加入排隊三個既有端點新增欄位
- Request body 新增 `captchaToken`（string，上限 64 字元）、`captchaAnswer`（string，上限 16 字元）兩個必填欄位
- 缺漏或超過長度上限：`400 Validation` 錯誤，不呼叫驗證邏輯（CAPTCHA-*-002、CAPTCHA-INPUT-001／002）
- 驗證碼文字錯誤：`400 Validation` 錯誤（CAPTCHA-AUTH-001／CAPTCHA-REG-001／CAPTCHA-QUEUE-001）
- Redis 無法連線：`5xx` 技術性錯誤，MUST 與上述 `400` 情境明確可區分，不得外觀相同（CAPTCHA-FAIL-002）

## Impact

- 新增 Infrastructure 元件：驗證碼圖片產生（需評估套件或手繪，設計階段決定）、Redis 暫存讀寫（可能沿用既有 Redis 連線設定，見 `query-caching`／`purchase-queue-leader-election` 既有基礎設施）
- 新增 API 端點：取得驗證碼（`GET /api/captcha`，回傳圖片與 token）
- 修改既有端點：註冊、登入、加入排隊三個既有端點的 request DTO 新增驗證碼欄位、Handler 新增驗證步驟
- 前端：註冊頁、登入頁、活動詳情頁（加入排隊操作）新增驗證碼 UI 元件
- 不影響資料庫 schema（驗證碼狀態存於 Redis，非 PostgreSQL）
