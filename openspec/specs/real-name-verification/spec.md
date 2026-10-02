# real-name-verification Specification

## Purpose
TBD - created by archiving change real-name-verification. Update Purpose after archive.
## Requirements
### Requirement: 已登入會員可登記一次實名資料，登記後不可修改
系統 SHALL 提供已登入會員登記自己實名資料的端點 `PUT /api/members/me/real-name`。實名資料由真實姓名（`RealName`）與身分證末四碼（`NationalIdLast4`）組成；登記的會員身份 MUST 完全取自呼叫者的 JWT Claims，不接受請求內容指定其他會員。

- **只能登記一次**：每位會員只能成功登記一次。已登記的會員再次呼叫，MUST 回傳 409 Conflict，不得變更已登記的任何值（即使新值與舊值相同也一樣回 409）。系統 MUST NOT 提供任何讓會員本人修改或清除實名資料的途徑。
- **並發登記**：同一會員的兩個首次登記請求並發送出時，MUST 恰好一個成功、另一個回傳 409；最終儲存的值 MUST 是成功那次請求的值，兩欄不得混合兩個請求的值。
- **一致性**：兩欄 MUST 同時為空（未登記）或同時有值（已登記），資料庫層 MUST 以約束保證。
- **成功回應**：成功時回傳登記後的個人資料，格式同「查詢自己的會員資料」，末四碼依遮蔽規則回傳。
- **帳號不存在**：呼叫者帳號已不存在時，MUST 回傳 404。

#### Scenario: RNV-REGISTER-001 首次登記成功
- **WHEN** 尚未登記實名的已登入會員送出合法的姓名「王小明」與末四碼「1234」
- **THEN** 系統儲存實名資料，回傳 `hasRegisteredRealName = true`、`realName = "王小明"`、`nationalIdLast4Masked = "**34"`，回應中不出現完整末四碼「1234」

#### Scenario: RNV-REGISTER-002 已登記後再次登記被拒
- **WHEN** 已登記實名的會員再次呼叫登記端點，送出不同的姓名與末四碼
- **THEN** 系統回傳 409，資料庫中的實名資料維持原值

#### Scenario: RNV-REGISTER-003 已登記後送出相同值仍被拒
- **WHEN** 已登記實名的會員再次呼叫登記端點，送出與已登記完全相同的姓名與末四碼
- **THEN** 系統回傳 409，不視為成功

#### Scenario: RNV-REGISTER-004 並發首次登記只有一個成功
- **WHEN** 尚未登記實名的會員同時送出兩個登記請求，分別為（「王小明」,「1234」）與（「李大華」,「5678」）
- **THEN** 恰好一個請求回傳 200、另一個回傳 409；資料庫中的姓名與末四碼 MUST 同屬回傳 200 的那個請求，不得出現「王小明＋5678」這類混合值

#### Scenario: RNV-REGISTER-005 未登入呼叫被拒
- **WHEN** 未攜帶有效 Access Token 的請求呼叫登記端點
- **THEN** 系統回傳 401

#### Scenario: RNV-REGISTER-006 會員資料不存在
- **WHEN** Access Token 有效，但其會員 Id 在資料庫中已不存在
- **THEN** 系統回傳 404，不建立任何資料

### Requirement: 實名資料格式驗證
系統 SHALL 在 Application 層驗證登記請求，驗證失敗時 MUST 回傳 400，不寫入任何資料：
- `RealName`：去除前後空白後長度 MUST 為 1 至 50 字元（以 Unicode 字元計，擴充 B 區罕用字等 UTF-16 佔兩個單位的字算 1 字，與資料庫 `varchar(50)` 的字元計數一致），MUST NOT 含控制字元（Cc）或不可見的格式字元（Cf，例如零寬空格 U+200B、方向覆寫 U+202E、BOM U+FEFF），也 MUST NOT 含歸類為字母／符號卻顯示為空白的字元（U+115F、U+1160、U+2800、U+3164、U+FFA0），且 MUST 至少包含一個字母類別（Unicode L*）的字元。儲存值為去除前後空白後的結果。
- `NationalIdLast4`：MUST 恰為 4 個半形數字（`0`–`9`）。全形數字、英文字母、空白、少於或多於 4 碼皆 MUST 拒絕。

驗證錯誤回應 MUST NOT 回顯使用者輸入的姓名或末四碼原值。系統只驗證格式，不驗證姓名與身分證是否真實或相符。

#### Scenario: RNV-FORMAT-001 姓名空白被拒
- **WHEN** 會員送出的姓名為空字串或只有空白
- **THEN** 系統回傳 400，不寫入資料

#### Scenario: RNV-FORMAT-002 姓名超過 50 字被拒
- **WHEN** 會員送出去除前後空白後長度為 51 字的姓名
- **THEN** 系統回傳 400，不寫入資料

#### Scenario: RNV-FORMAT-003 姓名前後空白被去除後儲存
- **WHEN** 會員送出姓名「  王小明  」與合法末四碼
- **THEN** 系統成功登記，儲存與回傳的姓名為「王小明」

#### Scenario: RNV-FORMAT-004 姓名含控制字元被拒
- **WHEN** 會員送出含換行字元（含 U+2028 分行符號、U+2029 分段符號）或其他控制字元的姓名
- **THEN** 系統回傳 400，不寫入資料

#### Scenario: RNV-FORMAT-005 末四碼格式不合法被拒
- **WHEN** 會員送出的末四碼為「123」、「12345」、「12a4」、「１２３４」（全形）或「12 4」其中之一
- **THEN** 系統回傳 400，不寫入資料

#### Scenario: RNV-FORMAT-006 驗證錯誤不回顯輸入值
- **WHEN** 會員送出姓名合法、末四碼為「98x7」的請求而驗證失敗
- **THEN** 400 回應內容 MUST NOT 包含字串「98x7」

#### Scenario: RNV-FORMAT-007 姓名含不可見格式字元被拒
- **WHEN** 會員送出只有零寬空格（U+200B）的姓名，或含方向覆寫字元（U+202E）、BOM（U+FEFF）的姓名
- **THEN** 系統回傳 400，不寫入資料

#### Scenario: RNV-FORMAT-008 姓名長度以 Unicode 字元計
- **WHEN** 會員送出 50 個擴充 B 區罕用字（UTF-16 共 100 個單位）的姓名與合法末四碼
- **THEN** 系統成功登記，儲存的姓名與送出值完全相同；51 個同類字元則回傳 400

#### Scenario: RNV-FORMAT-009 姓名含顯示為空白的字母或符號被拒
- **WHEN** 會員送出只有韓文填充字（U+3164）的姓名，或含空白點字（U+2800）、半形韓文填充字（U+FFA0）、韓文首尾填充字（U+115F、U+1160）的姓名
- **THEN** 系統回傳 400，不寫入資料

#### Scenario: RNV-FORMAT-010 姓名不含任何字母被拒
- **WHEN** 會員送出只由組合字元（U+034F）、異體字選擇符（U+FE0F）、蒙古文自由變體選擇符（U+180B）、數字或標點組成的姓名
- **THEN** 系統回傳 400，不寫入資料；含至少一個字母（例如中文字、英文字母、擴充 B 區罕用字）的姓名不受此規則影響

### Requirement: 會員停用不清除實名資料，已售出的需實名票券仍可查詢持票人與核銷
會員被管理員停用（`POST /api/admin/members/{id}/deactivate`）時，系統 MUST NOT 清除或修改該會員的實名資料。停用前已售出的需實名活動票券，查詢持票人仍 SHALL 回傳該會員的姓名與末四碼，帶確認旗標的核銷仍 SHALL 成功。理由：停用只阻擋該會員登入與操作，不應讓已付款的票券在入場時無法比對。重新啟用後實名資料維持原值。本次不提供刪除實名資料的途徑（見 design.md 安全確認問題「機敏資訊」）。

#### Scenario: RNV-RETAIN-001 停用與重新啟用不改變實名資料
- **WHEN** 已登記實名（「王小明」、「1234」）的會員被管理員停用，之後又被重新啟用
- **THEN** 停用後與重新啟用後，資料庫中該會員的姓名與末四碼都維持「王小明」、「1234」

#### Scenario: RNV-RETAIN-002 買家停用後仍可查詢其需實名票券的持票人
- **WHEN** 已登記實名的買家購買需實名活動票券並付款出票後被管理員停用，該活動的操作人員查詢這張 `Issued` 票券的持票人
- **THEN** 系統回傳 200，含該買家的姓名與末四碼

#### Scenario: RNV-RETAIN-003 買家停用後帶確認旗標的核銷仍成功
- **WHEN** 同上條件，操作人員對該票券送出帶 `isHolderVerified = true` 的核銷請求
- **THEN** 系統核銷成功，票券轉為 `Redeemed`

### Requirement: 身分證末四碼遮蔽規則
除「核銷時查詢持票人」端點（見 `ticket-redemption` 能力）外，系統所有 API 回應 MUST NOT 回傳完整的身分證末四碼，只能回傳遮蔽值。`Cache-Control: no-store` 的適用範圍：查詢個人資料（`GET /api/members/me`）、更新個人資料（`PUT /api/members/me`，回應同樣含姓名與遮蔽末四碼）、實名登記（`PUT /api/members/me/real-name`）、查詢持票人（`GET /api/admin/tickets/{id}/holder`）四個端點，由端點本身（Controller action）產生的所有回應都 MUST 帶 `Cache-Control: no-store`，不論狀態碼（200、400、404、409）、也不論回應是否實際含個資，這樣不必逐一判斷哪個狀態碼含個資。由驗證／授權 middleware 在進入端點之前產生的 401、403，`[ApiController]` 自動模型驗證在進入端點之前產生的 400（例如 JSON 格式錯誤），以及由全域例外處理產生的 500，不在此要求內：這些回應由框架產生、不含任何實名欄位（RNV-ERROR-001、RDM-HOLDER-004／005、RDM-HOLDER-007 另有斷言）。所有錯誤回應（ProblemDetails）MUST NOT 包含姓名或末四碼（不論完整或遮蔽）。遮蔽值格式固定為兩個星號加上末兩碼（例如末四碼「1234」遮蔽為「**34」）。真實姓名對會員本人以完整值回傳。

#### Scenario: RNV-MASK-001 遮蔽格式
- **WHEN** 系統遮蔽末四碼「0912」
- **THEN** 結果為「**12」

#### Scenario: RNV-MASK-002 個人資料查詢不含完整末四碼
- **WHEN** 已登記末四碼「1234」的會員查詢自己的會員資料
- **THEN** 回應 JSON 的任何欄位都不包含字串「1234」

#### Scenario: RNV-NOSTORE-001 已登記會員查詢個人資料的回應帶 no-store
- **WHEN** 已登記實名的會員呼叫 `GET /api/members/me`，回應 200 且含姓名
- **THEN** 回應標頭 `Cache-Control` 含 `no-store`

#### Scenario: RNV-NOSTORE-002 未登記會員查詢個人資料的回應同樣帶 no-store
- **WHEN** 尚未登記實名的會員呼叫 `GET /api/members/me`，回應 200、實名欄位為 null
- **THEN** 回應標頭 `Cache-Control` 含 `no-store`

#### Scenario: RNV-NOSTORE-003 實名登記端點的回應帶 no-store
- **WHEN** 會員呼叫實名登記端點，分別得到 200（登記成功）、409（已登記）、400（JSON 合法但姓名或末四碼不符格式，由 Application 層驗證產生）
- **THEN** 三種回應的標頭 `Cache-Control` 都含 `no-store`

#### Scenario: RNV-NOSTORE-004 查詢持票人成功回應帶 no-store
- **WHEN** 操作人員查詢自家需實名活動 `Issued` 票券的持票人，回應 200 且含姓名與末四碼
- **THEN** 回應標頭 `Cache-Control` 含 `no-store`

#### Scenario: RNV-NOSTORE-005 查詢持票人由端點產生的錯誤回應同樣帶 no-store
- **WHEN** 操作人員查詢其他 Organizer 的票券得到 404，或查詢自家 `Redeemed` 票券得到 409
- **THEN** 兩種回應的標頭 `Cache-Control` 都含 `no-store`

#### Scenario: RNV-NOSTORE-006 更新個人資料的回應帶 no-store
- **WHEN** 已登記實名的會員呼叫 `PUT /api/members/me` 更新顯示名稱，回應 200 且含姓名與遮蔽末四碼
- **THEN** 回應標頭 `Cache-Control` 含 `no-store`

#### Scenario: RNV-ERROR-001 錯誤回應（ProblemDetails）不含實名資料
- **WHEN** 已登記實名的會員再次登記收到 409，或需實名活動的下單、排隊、核銷收到 `RealNameRequired`／`HolderVerificationRequired`
- **THEN** ProblemDetails 的所有欄位（含 `detail`）都不包含該會員的姓名或末四碼，也不包含本次請求送出的姓名或末四碼

### Requirement: 實名資料不得出現在日誌
系統 MUST NOT 將會員的真實姓名或身分證末四碼（不論完整或遮蔽）寫入任何日誌，包括渲染後的訊息文字與 Serilog 結構化屬性（比照 `observability` 能力「既有能力定義的敏感資訊遮蔽規則在結構化日誌下持續適用」的檢查方式）。涵蓋範圍：實名登記、查詢個人資料、建立訂單與加入排隊的實名閘門、查詢持票人與核銷流程產生的日誌，含成功、驗證失敗、衝突與例外路徑。日誌如需識別對象，只能使用會員 Id、Ticket Id 等識別碼。

#### Scenario: RNV-LOG-001 登記與查詢個人資料流程的日誌不含實名資料
- **WHEN** 會員以一組獨特的姓名與末四碼完成登記，接著再次登記收到 409，再送出一次格式錯誤的請求收到 400，最後呼叫一次查詢個人資料（`GET /api/members/me`，回應含該姓名）
- **THEN** 這四次請求期間產生的所有 `LogEvent`，其訊息文字與所有結構化屬性皆不包含該姓名或該末四碼

#### Scenario: RNV-LOG-002 核銷與查詢持票人流程的日誌不含實名資料
- **WHEN** 操作人員對需實名活動的票券依序執行：核銷（未帶確認旗標，被拒）→ 查詢持票人 → 核銷（帶確認旗標，成功）
- **THEN** 這三次請求期間產生的所有 `LogEvent`，其訊息文字與所有結構化屬性皆不包含持票人的姓名或末四碼

#### Scenario: RNV-LOG-003 下單與排隊實名閘門的日誌不含實名資料
- **WHEN** 已登記實名的買家（使用獨特的姓名與末四碼）對需實名活動下單成功、加入排隊成功；另一位未登記的買家對同一活動下單與加入排隊被 `RealNameRequired` 擋下
- **THEN** 這些請求期間產生的所有 `LogEvent`，其訊息文字與所有結構化屬性皆不包含該姓名或末四碼，也沒有名稱含 `RealName`、`NationalId` 的結構化屬性

### Requirement: 部署切換與 migration 回滾不得讓實名資料或實名活動設定靜默失效
系統 SHALL 確保部署切換期間的快取讀取，以及 migration 回滾，都不會讓已登記的實名資料或需實名活動的設定靜默失效；違反前置條件時 MUST 明確失敗。

- **部署切換期間的保證範圍**（系統行為）：
  - 下單、加入排隊、核銷三個後端判斷 MUST 以資料庫中的 `IsRealNameRequired` 為準，MUST NOT 讀取公開活動列表快取。所以已建立且設定為需實名的活動，不會因快取或部署切換而被這三個判斷視為不需實名。
  - 公開活動列表 API 的 `isRealNameRequired` 可能在快取 TTL 內落後資料庫；命中不含此欄位的舊格式快取項目時 MUST 解讀為 false、不得失敗（TP-BROWSE-RN-002）。這只影響列表與活動頁的標示。
  - 部署順序與「不以多實例滾動部署」屬於營運規範，列在 design.md Migration Plan 的部署檢查清單，不是本 Requirement 的驗收情境。
- **migration Down 的前置條件**：Down MUST 在執行時先檢查資料庫；有任何會員已登記實名，或有任何活動 `IsRealNameRequired = true` 時，Down MUST 失敗並中止，不刪除任何欄位或資料。只有兩者皆不存在時（此時刪除不會遺失任何實名資料，也不會讓任何已售票券失去實名要求），Down 才可執行。
- **必須回滾但已有資料時**：優先只回滾程式碼、保留 schema。新增欄位舊版不讀不寫，保留是安全的。若真的必須移除 schema，須以人工程序先備份 `Members.RealName`、`Members.NationalIdLast4`、`Events.IsRealNameRequired`，清除後再執行 Down；重新升版時以備份還原這三個欄位後，才可重新對外服務。此人工程序不在本次自動化範圍內。
- **回滾後重新升版**：Down 成功執行後再次套用 Up，所有會員皆為未登記、所有活動皆為不需實名。由於 Down 的前置條件保證執行前就沒有這些資料，這個結果與 Down 之前一致，沒有已售票券被錯誤視為非實名。

#### Scenario: RNV-ROLLBACK-001 已有會員登記實名時 Down 失敗
- **WHEN** 資料庫中有一位會員已登記實名，執行本次 migration 的 Down
- **THEN** Down 失敗並回報原因，`Members` 的兩個實名欄位、`Events.IsRealNameRequired` 欄位與其資料都仍存在

#### Scenario: RNV-ROLLBACK-002 已有需實名活動時 Down 失敗
- **WHEN** 資料庫中沒有任何會員登記實名，但有一個 `IsRealNameRequired = true` 的活動，執行本次 migration 的 Down
- **THEN** Down 失敗並回報原因，所有欄位與資料都仍存在

#### Scenario: RNV-ROLLBACK-003 沒有實名資料時 Down 成功，重新升版行為一致
- **WHEN** 資料庫中沒有任何會員登記實名，也沒有需實名活動，執行 Down 之後再執行 Up
- **THEN** Down 成功移除欄位與約束；Up 後所有會員為未登記、所有活動 `IsRealNameRequired = false`，與 Down 之前的資料狀態一致

#### Scenario: RNV-CACHE-001 列表快取顯示不需實名時，後端閘門仍以資料庫為準
- **WHEN** 資料庫中活動 `IsRealNameRequired = true`，但公開活動列表快取中該活動的項目為舊格式或 `isRealNameRequired = false`（以測試直接寫入快取模擬），未登記實名的會員對該活動下單，另一次加入排隊（加入排隊的情境中活動已開啟熱門搶購模式）
- **THEN** 兩者都回傳 403、`Title = "RealNameRequired"`；公開活動列表此時仍回傳快取中的 false

