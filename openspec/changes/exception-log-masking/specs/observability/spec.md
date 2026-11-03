# Spec Delta

## ADDED Requirements

### Requirement: 日誌中的例外內容須遮蔽敏感資訊
系統 SHALL 在日誌輸出前，遮蔽例外文字（`Message`、`InnerException` 鏈、`AggregateException` 的內層例外）中符合敏感格式的值，使 Console、Seq 等所有輸出目的地都不含明文敏感值。此規則 SHALL 對所有日誌來源一致適用：HTTP 請求處理、背景服務、基礎設施元件、框架自行記錄的例外，以及應用程式啟動階段的失敗日誌。

#### Scenario: OBS-EXMASK-001 HTTP 未預期例外的日誌不含明文密碼
- **WHEN** 某端點處理請求時拋出例外，其訊息含 `Password=s3cret-pw`
- **THEN** 該請求期間產生的每一筆帶例外的日誌（含框架自行記錄的那些），在 Console 與 Seq 的輸出中都 MUST NOT 包含 `s3cret-pw`，且 `Password=` 之後的值為 `***`

#### Scenario: OBS-EXMASK-002 背景服務的例外日誌同樣遮蔽
- **WHEN** 背景服務單一輪次中捕捉到訊息含 `Password=s3cret-pw` 的例外並記錄錯誤日誌
- **THEN** 該筆日誌的例外文字 MUST NOT 包含 `s3cret-pw`

#### Scenario: OBS-EXMASK-003 內部例外鏈中的敏感值被遮蔽
- **WHEN** 例外的 `InnerException`（任一層）訊息含 `Password=s3cret-pw`
- **THEN** 日誌輸出的例外文字 MUST NOT 包含 `s3cret-pw`

#### Scenario: OBS-EXMASK-004 AggregateException 內層例外被遮蔽
- **WHEN** `AggregateException` 的某個內層例外訊息含 `Password=s3cret-pw`
- **THEN** 日誌輸出的例外文字 MUST NOT 包含 `s3cret-pw`

#### Scenario: OBS-EXMASK-015 啟動階段失敗日誌同樣遮蔽
- **WHEN** 應用程式啟動階段拋出訊息含 `Password=s3cret-pw` 的例外，並以「Application terminated unexpectedly」記錄致命錯誤
- **THEN** 該筆日誌的例外文字 MUST NOT 包含 `s3cret-pw`

### Requirement: 例外遮蔽涵蓋指定的敏感格式
系統 SHALL 遮蔽下列格式中的值，將值換成 `***` 並保留 key 與其餘文字：連線字串的 `Password`／`Pwd` 後接 `=` 或 `:`；`Bearer` token 與 JWT（`eyJ` 開頭的三段式字串）；`scheme://user:password@host` 中的密碼；`ApiKey`、`Secret`、`Token` 的 `key=value` 或 `key: value`。key 比對 SHALL 不分大小寫。

#### Scenario: OBS-EXMASK-005 連線字串密碼被遮蔽且 key 保留
- **WHEN** 例外訊息含 `Pool=true;Pwd=s3cret-pw;Port=5432`
- **THEN** 輸出含 `Pwd=***`，且不含 `s3cret-pw`，`Pool=true` 與 `Port=5432` 原樣保留

#### Scenario: OBS-EXMASK-006 Bearer token 與 JWT 被遮蔽
- **WHEN** 例外訊息含 `Authorization: Bearer abc.def.ghi` 或獨立出現的 `eyJhbGciOi.eyJzdWIiOi.c2lnbmF0dXJl`
- **THEN** 輸出不含該 token 字串

#### Scenario: OBS-EXMASK-007 URL 內嵌的密碼被遮蔽
- **WHEN** 例外訊息含 `redis://user:s3cret-pw@cache:6379`
- **THEN** 輸出含 `redis://user:***@cache:6379`，且不含 `s3cret-pw`

#### Scenario: OBS-EXMASK-008 ApiKey、Secret、Token 的值被遮蔽且大小寫不敏感
- **WHEN** 例外訊息含 `apikey=k-123`、`SECRET: k-456` 或 `Token=k-789`
- **THEN** 輸出不含 `k-123`、`k-456`、`k-789`

#### Scenario: OBS-EXMASK-009 不含敏感格式的例外文字不被改動
- **WHEN** 例外訊息不含任何上述格式（例如 `Sequence contains no elements`），且例外文字長度不超過 64 KB
- **THEN** 日誌輸出的例外文字與遮蔽前完全相同

### Requirement: 遮蔽後保留排錯所需的例外資訊
系統 SHALL 在遮蔽後的日誌中保留原始例外的型別名稱、堆疊追蹤與內部例外鏈的層次結構，只替換命中敏感格式的值。例外文字超過 64 KB 時，系統 SHALL 僅保證保留文字開頭 32 KB 內的型別與訊息，以及結尾 32 KB 內的堆疊；位於中間而被省略的內部例外型別、訊息，以及被省略的內部例外鏈層數與順序，皆不在保證範圍內。本規格中的「KB」一律以 UTF-16 字元數計（1 KB ＝ 1,024 個 `string.Length` 單位），不是位元組數。

#### Scenario: OBS-EXMASK-010 遮蔽後仍可辨識例外型別與堆疊
- **WHEN** 一個 `InvalidOperationException` 的訊息含 `Password=s3cret-pw`，且該例外是從某方法拋出
- **THEN** 日誌輸出的例外文字含 `InvalidOperationException`、該方法在堆疊中的位置，且不含 `s3cret-pw`

#### Scenario: OBS-EXMASK-011 內部例外鏈的型別順序保留
- **WHEN** 例外文字長度不超過 64 KB，例外鏈為外層 `A` 包住內層 `B`，且 `B` 的訊息含 `Password=s3cret-pw`
- **THEN** 日誌輸出的例外文字中 `A` 的型別名稱出現在 `B` 之前，且 `B` 的型別名稱存在

### Requirement: 遮蔽失敗時不得外洩原文也不得丟失日誌
系統 SHALL 在遮蔽處理本身失敗時，仍輸出該筆日誌，並將例外內容換成只含型別名稱的占位文字；MUST NOT 把未遮蔽的例外文字傳給任何輸出目的地，也 MUST NOT 讓遮蔽失敗中斷呼叫端的日誌呼叫。

#### Scenario: OBS-EXMASK-012 遮蔽逾時時改輸出只含型別的占位文字
- **WHEN** 遮蔽處理對某例外文字無法在允許時間內完成
- **THEN** 該筆日誌仍被輸出，其例外文字只含原例外的型別名稱，不含原始訊息與堆疊；並另有一筆固定範本、不含例外文字的 Warning 日誌記錄遮蔽失敗

### Requirement: 回應本文與非例外欄位不受遮蔽影響
遮蔽 SHALL 只作用於日誌的例外內容；未預期例外的 500 回應本文 SHALL 維持只含 `status`、`title`、`traceId`，日誌訊息範本的具名屬性值與渲染訊息 SHALL 維持原樣。

#### Scenario: OBS-EXMASK-013 500 回應本文不變
- **WHEN** 某端點拋出訊息含 `Password=s3cret-pw` 的例外
- **THEN** 回應狀態為 500，本文欄位集合恰為 `status`、`title`、`traceId`

#### Scenario: OBS-EXMASK-014 非例外的結構化屬性不被遮蔽器改動
- **WHEN** 一筆日誌帶有具名屬性 `{OrderId}`，其例外分別為「不含敏感格式」與「訊息含 `Password=s3cret-pw`」兩種情況
- **THEN** 兩種情況下該筆日誌的 `OrderId` 屬性值與渲染訊息都和遮蔽前相同

#### Scenario: OBS-EXMASK-016 框架類別記錄的例外同樣遮蔽
- **WHEN** 以框架類別名稱（例如 `Microsoft.AspNetCore.Hosting.Diagnostics`）為來源記錄一筆錯誤日誌，其例外訊息含 `Password=s3cret-pw`
- **THEN** 該筆日誌在 Console 與 Seq 的輸出中 MUST NOT 包含 `s3cret-pw`

#### Scenario: OBS-EXMASK-017 Password 以冒號分隔時同樣遮蔽
- **WHEN** 例外訊息含 `Password: s3cret-pw`
- **THEN** 輸出含 `Password: ***`，且不含 `s3cret-pw`

#### Scenario: OBS-EXMASK-018 JSON 與前綴 key 的值被遮蔽
- **WHEN** 例外訊息含 `{"password":"s3cret-pw"}`、`access_token=s3cret-pw` 或 `"client_secret": "s3cret-pw"`
- **THEN** 輸出不含 `s3cret-pw`

#### Scenario: OBS-EXMASK-019 含空白的連線字串密碼完整遮蔽
- **WHEN** 例外訊息含 `Pool=true;Password=my s3cret pw;Port=5432`
- **THEN** 輸出不含 `my`、`s3cret`、`pw` 這三段密碼內容，且 `Pool=true`、`Port=5432` 原樣保留

#### Scenario: OBS-EXMASK-020 超長例外文字的省略部分不輸出
- **WHEN** 例外文字長度超過 64 KB，且位於頭段與尾段之間（第 32 KB 之後、距結尾 32 KB 之前）的位置含 `Password=s3cret-pw`
- **THEN** 輸出不含 `s3cret-pw`，且輸出含固定的截斷標記

#### Scenario: OBS-EXMASK-021 JWT 跨越頭段上限時不輸出任何片段
- **WHEN** 例外文字長度超過 64 KB，其中一個 JWT 起於頭段上限（第 32 KB）之前、延伸到其之後
- **THEN** 輸出不含該 JWT 的任何連續 10 個以上字元的片段，且輸出含固定的截斷標記

#### Scenario: OBS-EXMASK-022 含空白密碼被頭段上限切斷時不輸出任何片段
- **WHEN** 例外文字長度超過 64 KB，且 `Password=my s3cret pw` 被頭段上限（第 32 KB）切在中間
- **THEN** 輸出不含 `my`、`s3cret`、`pw` 三段中任何一段緊接在 `Password=` 之後的內容，且輸出含固定的截斷標記

#### Scenario: OBS-EXMASK-023 超長訊息的例外仍保留型別與堆疊
- **WHEN** 例外的訊息超過 64 KB，堆疊追蹤位於訊息之後，且訊息中段含 `Password=s3cret-pw`
- **THEN** 輸出含該例外的型別名稱與堆疊中至少一個 `at ` 開頭的堆疊框架，含固定的截斷標記，且不含 `s3cret-pw`

#### Scenario: OBS-EXMASK-024 含逗號的 URL 密碼被邊界切開時不輸出任何片段
- **WHEN** 例外文字長度超過 64 KB，且 `redis://user:abc,def@cache:6379` 被頭段上限（第 32 KB）切在 `def` 中
- **THEN** 輸出不含 `abc` 緊接在 `redis://user:` 之後的內容，且含固定的截斷標記
