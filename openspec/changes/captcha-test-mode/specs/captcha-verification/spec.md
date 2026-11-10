## ADDED Requirements

### Requirement: 驗證碼驗證模式設定，預設強制驗證，接受任何答案的測試模式不得用於 Production
系統 SHALL 提供設定鍵 `Captcha:VerificationMode`，允許的值為 `Enforced` 與 `AcceptAny`；設定缺漏時 MUST 視為 `Enforced`，不得因缺漏而降低驗證強度。`Enforced` 下，本能力其餘 Requirement 描述的產生、儲存、一次性核對與 fail-closed 行為完全不變。`AcceptAny` 下，登入、註冊與加入購買佇列所做的驗證碼核對 MUST 一律視為通過（不論 token 是否存在、是否過期、答案是否正確），且不得因此讀取或刪除 Redis；除此之外的行為（`GET /api/captcha` 的產生與回應格式、請求欄位的必填與長度驗證、驗證碼產生端點的限流、登入帳密檢查、`Enforced` 以外的所有業務規則）MUST 維持不變。`AcceptAny` 與本能力「驗證碼核對為一次性」及「fail-closed」兩項 Requirement 衝突時，以本 Requirement 為準，且衝突只限於核對本身。

系統 MUST 在啟動階段擋下以下設定，不得繼續啟動：環境為 Production 且模式為 `AcceptAny`（拋出 `OptionsValidationException`）；`VerificationMode` 的值不是已定義的模式（拼字錯誤，或不對應任何模式的數值）。模式名稱與環境名稱的比對皆不分大小寫。`AcceptAny` 生效時，系統 MUST 在啟動時記錄一筆 Warning 等級日誌，說明驗證碼核對已停用；`Enforced` 不得記錄此日誌。

#### Scenario: CAPTCHA-MODE-001 設定缺漏時採用 Enforced
- **WHEN** `Captcha` 設定未提供 `VerificationMode`
- **THEN** 系統以 `Enforced` 運作，使用錯誤的驗證碼答案登入回傳 400 `CaptchaInvalid`，與本 change 之前的行為相同

#### Scenario: CAPTCHA-MODE-002 明確設為 Enforced 時行為不變
- **WHEN** `Captcha:VerificationMode` 設為 `Enforced`，使用不存在的 token 或錯誤答案呼叫登入、註冊或加入購買佇列
- **THEN** 回傳 400 `CaptchaInvalid`，且 Redis 的一次性核對（`GETDEL`）照常執行

#### Scenario: CAPTCHA-MODE-003 AcceptAny 下任意 token 與答案的核對皆通過
- **WHEN** 非 Production 環境且 `VerificationMode` 為 `AcceptAny`，以未曾產生過的 token 與任意非空答案呼叫登入、註冊或加入購買佇列，其餘欄位皆有效
- **THEN** 驗證碼核對視為通過，請求依後續業務規則處理（例如登入以帳密判定成功或失敗），不回傳 `CaptchaInvalid`

#### Scenario: CAPTCHA-MODE-004 AcceptAny 下產生端點行為不變
- **WHEN** `VerificationMode` 為 `AcceptAny`，呼叫 `GET /api/captcha`
- **THEN** 仍回傳新的 token 與 base64 PNG 圖片，格式與 `Enforced` 相同，Redis 仍寫入帶 TTL 的雜湊

#### Scenario: CAPTCHA-MODE-005 AcceptAny 不繞過請求欄位驗證與限流
- **WHEN** `VerificationMode` 為 `AcceptAny`，登入請求的驗證碼 token 或答案為空白或超過長度上限，或超過 `GET /api/captcha` 的限流額度
- **THEN** 請求欄位驗證仍回傳 400 `Validation`（含逐欄位 `errors`），限流仍回傳 429；`AcceptAny` 只略過核對本身

#### Scenario: CAPTCHA-MODE-006 Production 設為 AcceptAny 時啟動失敗
- **WHEN** 環境為 Production，且 `Captcha:VerificationMode` 為 `AcceptAny`
- **THEN** 系統在設定驗證階段拋出 `OptionsValidationException`，不得繼續啟動，訊息指出 Production 不得使用 `AcceptAny`

#### Scenario: CAPTCHA-MODE-007 非 Production 環境可啟用 AcceptAny
- **WHEN** 環境為 Development 或 Testing，且 `VerificationMode` 為 `AcceptAny`
- **THEN** 系統正常啟動，並記錄一筆 Warning 等級日誌說明驗證碼核對已停用

#### Scenario: CAPTCHA-MODE-008 VerificationMode 值無效時擋下
- **WHEN** `Captcha:VerificationMode` 為未定義的名稱（例如 `acceptany1`）或不對應任何模式的數值（例如 `99`）
- **THEN** 系統在啟動階段擋下並拋出例外，不得以預設值靜默取代

#### Scenario: CAPTCHA-MODE-009 Enforced 啟動時不記錄停用 Warning
- **WHEN** `VerificationMode` 為 `Enforced` 或缺漏
- **THEN** 啟動日誌不含「驗證碼核對已停用」的 Warning
