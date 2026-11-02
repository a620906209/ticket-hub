## Purpose

定義 api 對部署平台提供的存活與就緒探測端點，包含判定條件、存取限制、回應內容、日誌等級與轉址行為，讓平台能據此決定重啟或暫停導流，且不暴露內部資訊。

## ADDED Requirements

### Requirement: 存活探測不依賴外部服務
`GET /health/live` SHALL 在行程能處理請求時回傳 HTTP 200，且 MUST NOT 檢查資料庫、Redis、Seq 或任何其他外部服務。外部服務故障時，此端點的結果 MUST NOT 改變，避免平台因相依故障而重啟健康的行程。

#### Scenario: HC-LIVE-HEALTHY 相依正常時回報存活
- **WHEN** 資料庫與 Redis 皆正常，呼叫端發出 `GET /health/live`
- **THEN** 系統回傳 HTTP 200

#### Scenario: HC-LIVE-DB-DOWN 資料庫無法連線時仍回報存活
- **WHEN** 資料庫無法連線，呼叫端發出 `GET /health/live`
- **THEN** 系統回傳 HTTP 200

#### Scenario: HC-LIVE-REDIS-DOWN Redis 無法連線時仍回報存活
- **WHEN** Redis 無法連線，呼叫端發出 `GET /health/live`
- **THEN** 系統回傳 HTTP 200

#### Scenario: HC-LIVE-SEQ-DOWN Seq 無法連線時仍回報存活
- **WHEN** Seq 無法連線，呼叫端發出 `GET /health/live`
- **THEN** 系統回傳 HTTP 200

#### Scenario: HC-LIVE-NO-CHECKS 存活探測不執行任何相依檢查
- **WHEN** 呼叫端發出 `GET /health/live`
- **THEN** 系統回傳 HTTP 200，且沒有任何已註冊的健康檢查被執行

### Requirement: 就緒探測只依資料庫判定
`GET /health/ready` SHALL 只以資料庫連線能力判定就緒：2 秒內確認可連線時回傳 HTTP 200；連線失敗或超過 2 秒未完成時回傳 HTTP 503。2 秒上限不包含資料庫主機名稱解析的時間；名稱解析失敗時仍 MUST 回傳 HTTP 503。Redis 與 Seq 的狀態 MUST NOT 影響此端點的結果。

#### Scenario: HC-READY-HEALTHY 資料庫可連線時回報就緒
- **WHEN** 資料庫可連線，呼叫端發出 `GET /health/ready`
- **THEN** 系統回傳 HTTP 200

#### Scenario: HC-READY-DB-DOWN 資料庫連線失敗時回報未就緒
- **WHEN** 資料庫拒絕連線，呼叫端發出 `GET /health/ready`
- **THEN** 系統回傳 HTTP 503

#### Scenario: HC-READY-DB-MISSING 目標資料庫不存在時回報未就緒
- **WHEN** 資料庫伺服器可連線但目標資料庫不存在，呼叫端發出 `GET /health/ready`
- **THEN** 系統回傳 HTTP 503

#### Scenario: HC-READY-DNS-FAILURE 資料庫主機名稱無法解析時回報未就緒
- **WHEN** 資料庫主機名稱無法解析，呼叫端發出 `GET /health/ready`
- **THEN** 系統回傳 HTTP 503

#### Scenario: HC-READY-TIMEOUT 資料庫無回應時於逾時後回報未就緒
- **WHEN** 資料庫端點接受 TCP 連線但不回應，呼叫端發出 `GET /health/ready`
- **THEN** 系統回傳 HTTP 503，且從呼叫端送出請求到收到完整回應的時間不少於 1.5 秒、不超過 4 秒

#### Scenario: HC-READY-REDIS-DOWN Redis 無法連線時仍回報就緒
- **WHEN** 資料庫可連線但 Redis 無法連線，呼叫端發出 `GET /health/ready`
- **THEN** 系統回傳 HTTP 200

#### Scenario: HC-READY-SEQ-DOWN Seq 無法連線時仍回報就緒
- **WHEN** 資料庫可連線但 Seq 無法連線，呼叫端發出 `GET /health/ready`
- **THEN** 系統回傳 HTTP 200

#### Scenario: HC-READY-ONLY-DATABASE 就緒探測只執行資料庫檢查
- **WHEN** 呼叫端發出 `GET /health/ready`
- **THEN** 系統註冊的健康檢查只有資料庫檢查，該檢查被執行一次，其他未標記為就緒用途的健康檢查不被執行

### Requirement: 探測端點匿名存取且不受請求頻率限制
`GET /health/live` 與 `GET /health/ready` SHALL 不需要任何身分驗證，且 MUST NOT 套用任何請求頻率限制，不論限制為全域或端點專屬、額度高低，因為部署平台的探針不帶憑證並會定期重複呼叫。

#### Scenario: HC-ANONYMOUS 未帶權杖可存取
- **WHEN** 資料庫可連線，呼叫端未帶 Authorization 標頭，分別呼叫兩個探測端點
- **THEN** 兩者皆回傳 HTTP 200

#### Scenario: HC-NO-RATE-LIMIT 限流額度用盡時探測端點仍不受限
- **WHEN** 資料庫可連線，系統設有適用於所有端點的全域限流，且其額度已用盡到非探測端點回傳 HTTP 429，呼叫端再對兩個探測端點各連續發出 50 次請求
- **THEN** 所有探測回應皆為 HTTP 200，沒有任何 HTTP 429

#### Scenario: HC-NO-ENDPOINT-RATE-LIMIT 其他端點專屬限流額度用盡時探測端點仍不受限
- **WHEN** 資料庫可連線，系統未設全域限流，登入端點的專屬限流額度已用盡到該端點回傳 HTTP 429，呼叫端再對兩個探測端點各連續發出 50 次請求
- **THEN** 所有探測回應皆為 HTTP 200，沒有任何 HTTP 429

#### Scenario: HC-NO-RATE-LIMIT-POLICY 探測端點不綁定任何端點專屬限流 policy
- **WHEN** 檢視兩個探測端點的路由設定
- **THEN** 兩者皆明確停用限流，且未綁定任何具名限流 policy

### Requirement: 探測回應不暴露內部資訊
探測端點回報探測結果時，回應內容 SHALL 只有純文字 `Healthy` 或 `Unhealthy`。探測處理發生未預期例外時，系統 SHALL 回傳 HTTP 500 的標準錯誤回應。任何回應皆 MUST NOT 包含相依服務名稱、連線資訊或例外訊息，因為這兩個端點不需驗證即可存取。

#### Scenario: HC-BODY-HEALTHY 成功回應只有狀態文字
- **WHEN** 探測結果為成功
- **THEN** 回應內容完全等於 `Healthy`

#### Scenario: HC-BODY-UNHEALTHY 失敗回應不含內部細節
- **WHEN** 資料庫無法連線，呼叫端發出 `GET /health/ready`
- **THEN** 回應內容完全等於 `Unhealthy`

#### Scenario: HC-BODY-EXCEPTION 例外回應為標準錯誤格式且不含例外訊息
- **WHEN** 探測處理發生未預期例外，呼叫端呼叫探測端點
- **THEN** 系統回傳 HTTP 500，回應內容為 ProblemDetails JSON，只有 `status`、`title`、`traceId` 三個欄位，其 `status` 為 500、`title` 為 `An unexpected error occurred.`、`traceId` 不為空，且回應內容不含例外訊息、例外型別、堆疊、相依服務名稱或連線資訊

### Requirement: 探測請求的日誌等級依結果區分
探測端點回傳 HTTP 200 時，其請求摘要日誌 SHALL 為 Debug 等級，在預設最低等級 Information 下不輸出。回傳非 200 或處理時發生例外時，請求摘要日誌 SHALL 為 Error 等級，且探測處理 MUST NOT 將系統設定的資料庫密碼帶入日誌或例外。資料庫連線失敗時，其日誌 MUST NOT 含該密碼。其他端點的請求摘要日誌等級 MUST NOT 因此改變。

#### Scenario: HC-LOG-SUCCESS-DEBUG 成功探測記錄為 Debug
- **WHEN** 最低日誌等級設為 Debug，呼叫端成功呼叫探測端點
- **THEN** 該請求的摘要日誌等級為 Debug

#### Scenario: HC-LOG-SUCCESS-HIDDEN 預設等級下成功探測不輸出摘要
- **WHEN** 最低日誌等級為預設的 Information，呼叫端成功呼叫探測端點
- **THEN** 日誌中沒有該請求的摘要

#### Scenario: HC-LOG-FAILURE-ERROR 失敗探測記錄為 Error
- **WHEN** 資料庫無法連線，呼叫端發出 `GET /health/ready`
- **THEN** 該請求的摘要日誌等級為 Error

#### Scenario: HC-LOG-FAILURE-EXCEPTION 探測處理發生例外時記錄為 Error
- **WHEN** 探測處理發生未預期例外，呼叫端呼叫探測端點
- **THEN** 該請求的摘要日誌等級為 Error

#### Scenario: HC-LOG-FAILURE-NO-PASSWORD 失敗探測的日誌不含密碼
- **WHEN** 最低日誌等級設為 Debug 且資料庫無法連線，呼叫端發出 `GET /health/ready`
- **THEN** 此期間輸出的所有日誌，其訊息、例外內容與所有結構化屬性皆不含資料庫密碼

#### Scenario: HC-LOG-EXCEPTION-NO-PASSWORD 探測處理不將密碼帶入例外日誌
- **WHEN** 最低日誌等級設為 Debug 且連線字串含資料庫密碼，探測處理發生未預期例外，呼叫端呼叫探測端點
- **THEN** 該請求有記錄此例外的 Error 日誌，且此期間輸出的所有日誌，其訊息、例外內容與所有結構化屬性皆不含系統設定的資料庫密碼

#### Scenario: HC-LOG-OTHER-UNCHANGED 其他端點的日誌等級不變
- **WHEN** 呼叫端成功呼叫非探測端點
- **THEN** 該請求的摘要日誌等級仍為 Information

#### Scenario: HC-LOG-OTHER-ERROR 其他端點的 5xx 仍記錄為 Error
- **WHEN** 資料庫無法連線且活動清單查詢快取未命中，呼叫端呼叫非探測端點 `GET /api/events` 而得到 HTTP 500
- **THEN** 該請求的摘要日誌等級為 Error

### Requirement: 探測端點不被 HTTPS 轉址
即使系統設定了 HTTPS 埠，透過 HTTP 呼叫探測端點時 SHALL 直接回傳探測結果，MUST NOT 回傳轉址回應。其他端點的 HTTPS 轉址行為 MUST NOT 因此改變。

#### Scenario: HC-NO-REDIRECT 設定 HTTPS 埠時探測端點不轉址
- **WHEN** 系統設定了 HTTPS 埠且資料庫可連線，呼叫端透過 HTTP 呼叫兩個探測端點
- **THEN** 兩者皆回傳 HTTP 200

#### Scenario: HC-NO-REDIRECT-CASE-INSENSITIVE 路徑大小寫不同時同樣不轉址
- **WHEN** 系統設定了 HTTPS 埠且資料庫可連線，呼叫端透過 HTTP 呼叫 `GET /Health/Live`
- **THEN** 系統回傳 HTTP 200

#### Scenario: HC-REDIRECT-OTHER-UNCHANGED 其他端點維持轉址
- **WHEN** 系統設定了 HTTPS 埠，呼叫端透過 HTTP 呼叫非探測端點
- **THEN** 系統回傳 3xx 轉址回應
