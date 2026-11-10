## AC 與測試對應

| AC | 測試 task |
|----|-----------|
| CAPTCHA-MODE-001 設定缺漏為 Enforced | 1.1（預設值）、1.3（缺漏時錯誤答案仍 400） |
| CAPTCHA-MODE-002 Enforced 行為不變 | 1.3(B)（login、register、join purchase queue 三流程各一，GETDEL 照常） |
| CAPTCHA-MODE-003 AcceptAny 任意答案通過 | 1.2、1.4 |
| CAPTCHA-MODE-004 產生端點不變 | 1.4 |
| CAPTCHA-MODE-005 欄位驗證與限流不繞過 | 1.4 |
| CAPTCHA-MODE-006 Production + AcceptAny 啟動失敗 | 1.5 |
| CAPTCHA-MODE-007 非 Production 可啟用並記 Warning | 1.6 |
| CAPTCHA-MODE-008 無效值擋下 | 1.5 |
| CAPTCHA-MODE-009 Enforced 不記 Warning | 1.6 |

## 1. 實作（依 TDD：先 RED 再 GREEN，證據逐字記錄於各 task 下）

通則：RED 必須是目標行為不符的斷言失敗，不得是編譯、DI 或環境錯誤，也不得以刻意錯誤的預設值或偽造行為的空殼製造。新型別不存在而使測試無法編譯時，只先建立不含目標行為的最小契約宣告（見 1.1、1.2），測試在該宣告上因行為缺失而失敗。所有命令皆在容器內執行：`docker compose exec api dotnet test tests/<專案> --filter "<名稱>"`。

- [ ] 1.1 `CaptchaOptions` 新增 `VerificationMode` 與列舉 `CaptchaVerificationMode { Enforced = 1, AcceptAny = 2 }`；`CaptchaOptionsTests.Defaults_VerificationModeIsEnforced`。
  - 契約宣告：先只宣告列舉與屬性（`public CaptchaVerificationMode VerificationMode { get; set; }`，不寫初始值），列舉成員以非零值定義，使未設定的 `default` 不等於任何有效模式。
  - 預期 RED：`new CaptchaOptions().VerificationMode` 預期 `Enforced`，實際為 `0`（未定義、未設定預設）；斷言失敗，原因是「缺漏時預設 Enforced」尚未實作。
  - GREEN：屬性加上初始值 `Enforced`；`Application.Tests` 過濾 `CaptchaOptionsTests` 全數通過。
  - RED：
  - GREEN：
- [ ] 1.2 新增 `AcceptAnyCaptchaService`（Infrastructure，建構子注入 `RedisCaptchaService`）；`AcceptAnyCaptchaServiceTests`（`RedisCaptchaService` 以 mock／fake 替身，或以其所依賴的 `IConnectionMultiplexer` mock 建構）：(a) `VerifyAsync` 以任意 token 與答案預期通過，且內部 `RedisCaptchaService` 與 `IConnectionMultiplexer` 皆未被呼叫（「不碰 Redis」只針對 `VerifyAsync`）；(b) `GenerateAsync` 回傳的 `CaptchaChallenge` 與內部服務產生的是同一個結果，且內部 `GenerateAsync` 恰被呼叫一次。
  - 契約宣告：先只建立實作 `ICaptchaService` 的類別與建構子，`VerifyAsync`、`GenerateAsync` 方法本體皆為 `throw new NotImplementedException()`（不偽造任何行為）。
  - 預期 RED：(a) 預期通過、實際拋出 `NotImplementedException`；(b) 預期回傳內部服務的結果、實際拋出 `NotImplementedException`（目標行為尚未實作）。
  - GREEN：`VerifyAsync` 恆回通過；`GenerateAsync` 委派內部服務；`Infrastructure.Tests` 過濾 `AcceptAnyCaptchaServiceTests` 全數通過。
  - RED：
  - GREEN：
- [ ] 1.3 `AddCaptcha` 以工廠依模式選實作。兩組測試：
  - (A) 選擇行為（RED 來源）：`CaptchaServiceSelectionTests`，以 `ServiceCollection` 加 `AddCaptcha` 後建立 scope 解析 `ICaptchaService`：`VerificationMode=AcceptAny` 預期為 `AcceptAnyCaptchaService`（其 `GenerateAsync` 的委派行為由 1.2(b) 與 1.4(b) 驗證）；`Enforced` 與設定缺漏預期為 `RedisCaptchaService`。
  - (B) Enforced 回歸保護：整合測試（Testing 環境，真實 Redis 容器）針對 **login、register、join purchase queue 三個流程各一**，設定缺漏與顯式 `Enforced` 時，以不存在的 token 或錯誤答案呼叫，預期 400 `CaptchaInvalid`，且 Redis 對應 key 已被 `GETDEL` 刪除（先寫入 token、用錯誤答案呼叫、事後該 key 不存在為斷言）。
  - 預期 RED（只針對 A）：factory 尚未實作時 `AddCaptcha` 固定註冊 `RedisCaptchaService`，`AcceptAny` 案例預期型別 `AcceptAnyCaptchaService`、實際為 `RedisCaptchaService`（斷言失敗）。
  - B 的 RED：N/A——這些是既有 Enforced 行為的回歸保護，在 factory 實作前即通過，不存在「目標行為缺失」的失敗；改以額外 mutation 驗證其敏感度（不取代 A 的 RED）：暫時把預設實作換成 `AcceptAnyCaptchaService`（`bash scripts/verify-test-evidence.sh mutate`，一次一個改動），預期三個流程各自因「預期 400 `CaptchaInvalid`、實際非 400」失敗，隨後還原並重跑通過，命令與輸出記錄於下。
  - GREEN：工廠依模式選擇且預設 `Enforced`；A、B 全數通過（`WebApi.Tests` 過濾 `CaptchaServiceSelection`、`CaptchaVerificationMode` 相關測試）。
  - RED（A）：
  - mutation（B）：
  - GREEN：
- [ ] 1.4 整合測試（`AcceptAny`，非 Production）：(a) login、register、join purchase queue 以未曾產生的 token 與任意非空答案通過核對。三個流程的其餘欄位 MUST 使 Validator 通過（Handler 先跑 Validator 才呼叫 `VerifyAsync`，Validator 失敗時回 `Validation`，不含 `CaptchaInvalid` 但也未證明核對通過），並斷言驗證碼核對之後的成功結果，不得只斷言「不是 `CaptchaInvalid`」：login 以正確帳密回 200；register 以未使用的有效 Email、顯示名稱與合規密碼註冊成功，且 DB 可查到該會員；join purchase queue 以有效會員、已開啟佇列模式的有效活動／票種送出，成功建立排隊項目且 DB 可查到。fixture 沿用既有測試資料（`tests/ProjectC.WebApi.Tests/Captcha/PurchaseQueueCaptchaTests.cs`、`TestSupport/RealNameTestData.cs` 等已能讓 Validator 通過的請求建構），不另造。成功斷言 MUST 取自「同一次 HTTP 請求」的回應（成功狀態碼，且回應不是 `Validation` 或 `CaptchaInvalid`），再以 DB 查證其副作用；不得以事前 setup 資料存在代替。同一組請求在 `Enforced` 下以同樣的錯誤 token 呼叫會回 400 `CaptchaInvalid`，作為對照以證明差異來自模式；(b) `GET /api/captcha` 仍回新 token 與 base64 圖片，Redis 寫入帶 TTL 的雜湊，其剩餘 TTL 大於 0 且不超過 `Captcha:TtlSeconds`；(c) token 空白或超過 64 字元、答案空白或超過 16 字元仍回 400 `Validation`（含逐欄位 `errors`）；(d) 同一來源 IP 連續請求 `GET /api/captcha`，前 N 次（N 為 `CaptchaRateLimiting:PermitLimit`）回 200，第 N+1 次回 429（以測試設定縮小額度與視窗）。
  - 預期 RED（只針對 (a)）：在 1.3 完成後、`AcceptAnyCaptchaService` 尚未被工廠選用前，(a) 預期不回 `CaptchaInvalid`、實際回 400 `CaptchaInvalid`。
  - (b)(c)(d) 的 RED：N/A——它們保護的是既有行為，在 `AcceptAny` 接線前即通過，沒有「目標行為缺失」的失敗；但須先於 `AcceptAny` 接線前加入並執行，記錄基線通過的命令與輸出（證明測試對既有行為有效），GREEN 接線後重跑仍通過。
  - 否證驗證（額外 mutation，`bash scripts/verify-test-evidence.sh mutate`，一次一個改動，還原後重跑）：(b) 讓 `GetCaptchaHandler` 不寫 Redis 雜湊 → 預期 (b) 因「Redis 無對應 key」失敗；(c) 暫時移除 token 長度上限規則 → 預期 (c) 因「預期 400 `Validation`、實際非 400」失敗；(d) 暫時關閉 `CaptchaRateLimiting` → 預期 (d) 因「預期 429、實際 200」失敗。各項命令與失敗輸出記錄於下。
  - GREEN：`AcceptAny` 接線後 (a)–(d) 全數通過；`WebApi.Tests` 過濾 `CaptchaAcceptAny` 相關測試通過。
  - RED（a）：
  - 基線（b）（c）（d）：
  - mutation（b）（c）（d）：
  - GREEN：
- [ ] 1.5 `CaptchaOptionsValidator`（`IValidateOptions<CaptchaOptions>`，WebApi）；啟動測試（比照 `RateLimitingOptionsFailFastTests`）：(a) Production + `AcceptAny` 啟動拋 `OptionsValidationException`，訊息含 Production；(b) 環境名稱 `production`（小寫）同樣失敗；(c) `Captcha:VerificationMode=99` 啟動失敗；(d) 拼錯名稱（`acceptany1`）啟動失敗（斷言一般例外，不是 `OptionsValidationException`：ConfigurationBinder 在無驗證器時本就會拋例外，故 (d) 不作為 RED 依據，只作為回歸保護）；(e) Production + `Enforced` 正常啟動（防止驗證器誤擋）。
  - 預期 RED：驗證器未註冊時 (a)(b) 預期拋 `OptionsValidationException`、實際正常啟動；(c) 預期啟動失敗、實際以預設值啟動。
  - GREEN：註冊驗證器後 (a)–(e) 全數通過；`WebApi.Tests` 過濾 `CaptchaOptionsFailFast` 相關測試通過。
  - RED：
  - GREEN：
- [ ] 1.6 `ResolveEagerlyValidatedOptions` 於 `AcceptAny` 記一筆 Warning；測試以記憶體 logger provider 擷取：Development 與 Testing + `AcceptAny` 正常啟動且恰有一筆 Warning（訊息含「驗證碼核對已停用」）；`Enforced` 與缺漏時無該 Warning。
  - 預期 RED：尚未記錄時，`AcceptAny` 案例預期恰 1 筆 Warning、實際 0 筆。
  - GREEN：記錄後全數通過，`Enforced`／缺漏案例仍為 0 筆。
  - RED：
  - GREEN：
- [ ] 1.7 重構後重跑 1.1–1.6 相關測試（或標 N/A 並說明）。

## 2. 同步與收尾

- [ ] 2.1 更新 `tests/ProjectC.WebApi.Tests/Startup/ServiceRegistrationSnapshot.baseline.txt`（`ICaptchaService` 註冊、新增驗證器）；快照測試通過。
- [ ] 2.2 更新 `docs/todo/2026-11-10-deployment-env-vars.md`（`Captcha__VerificationMode`：僅限非 Production、部署預設不設）；執行 `bash scripts/check-doc-paths.sh`。
- [ ] 2.3 將 `docs/todo/2026-11-10-captcha-test-switch-todo.md` 前兩項標為完成（第三項 K8s 3.3 重跑另做）。
- [ ] 2.4 hardener 最終檢查：本 change 只動設定驗證、DI 註冊與無 I/O 的 `AcceptAnyCaptchaService`，預期 N/A；實作完成後確認並記錄理由。
- [ ] 2.5 全套測試（各測試專案）逐字貼摘要行。
- [ ] 2.6 `graphify update .`。
