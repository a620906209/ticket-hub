## 1. AC 與測試對應

| AC | 測試 task |
|----|-----------|
| CAPTCHA-MODE-001 設定缺漏為 Enforced | 1.1（預設值）、1.3（缺漏時錯誤答案仍 400） |
| CAPTCHA-MODE-002 Enforced 行為不變 | 1.3（login、register、join purchase queue 三流程各一，GETDEL 照常） |
| CAPTCHA-MODE-003 AcceptAny 任意答案通過 | 1.2、1.4 |
| CAPTCHA-MODE-004 產生端點不變 | 1.4 |
| CAPTCHA-MODE-005 欄位驗證與限流不繞過 | 1.4 |
| CAPTCHA-MODE-006 Production + AcceptAny 啟動失敗 | 1.5 |
| CAPTCHA-MODE-007 非 Production 可啟用並記 Warning | 1.6 |
| CAPTCHA-MODE-008 無效值擋下 | 1.5 |
| CAPTCHA-MODE-009 Enforced 不記 Warning | 1.6 |

## 2. 實作（依 TDD：先 RED 再 GREEN，證據逐字記錄於各 task 下）

通則：RED 必須是目標行為不符的斷言失敗，不得是編譯、DI 或環境錯誤。為避免新增 `VerificationMode`／列舉前測試只產生編譯錯誤，1.1 先只加入列舉與屬性的最小骨架（預設值先設為 `AcceptAny`，使預設值測試能以斷言失敗作為 RED，GREEN 再改為 `Enforced`）；後續 task 的測試以該骨架編譯。所有命令皆在容器內執行：`docker compose exec api dotnet test tests/<專案> --filter "<名稱>"`。

- [ ] 1.1 `CaptchaOptions` 新增 `VerificationMode` 與列舉 `CaptchaVerificationMode { Enforced, AcceptAny }`；`CaptchaOptionsTests.Defaults_VerificationModeIsEnforced`。
  - 預期 RED：`new CaptchaOptions().VerificationMode` 預期 `Enforced`，實際為骨架預設的 `AcceptAny`（斷言失敗）。
  - GREEN：把預設改為 `Enforced`；`Application.Tests` 過濾 `CaptchaOptionsTests` 全數通過。
  - RED：
  - GREEN：
- [ ] 1.2 新增 `AcceptAnyCaptchaService`（Infrastructure）；`AcceptAnyCaptchaServiceTests`：以任意 token（含空字串以外的未簽發值）與任意答案呼叫 `VerifyAsync`，且 `IConnectionMultiplexer` mock 上無任何呼叫。
  - 預期 RED：先建立 `VerifyAsync` 回傳「驗證失敗」的空殼，斷言預期通過、實際失敗；Redis mock 斷言 `DidNotReceive` 於 GREEN 實作前亦成立，故另以「呼叫次數為 0」確認不碰 Redis 的測試在 GREEN 才新增並通過。
  - GREEN：`VerifyAsync` 恆回通過；`Infrastructure.Tests` 過濾 `AcceptAnyCaptchaServiceTests` 全數通過。
  - RED：
  - GREEN：
- [ ] 1.3 `AddCaptcha` 以工廠依模式選實作；整合測試（Testing 環境，真實 Redis 容器）針對 **login、register、join purchase queue 三個流程各一**：設定缺漏與顯式 `Enforced` 時，以不存在的 token 或錯誤答案呼叫，預期 400 `CaptchaInvalid`，且 Redis 中對應 key 若存在則已被 `GETDEL` 刪除（以先寫入 token 再用錯誤答案、事後該 key 不存在為斷言）。
  - 預期 RED：工廠尚未接線時三流程仍使用 `RedisCaptchaService`，故測試在顯式 `Enforced` 下應已通過；為證明測試能失敗，RED 以暫時把 `AcceptAnyCaptchaService` 註冊為預設實作（`mutate`，一次一個改動）執行，預期三個流程各自因「預期 400 `CaptchaInvalid`、實際 200／非 400」失敗，隨後還原。
  - GREEN：工廠依模式選擇且預設 `Enforced`；`WebApi.Tests` 過濾 `CaptchaVerificationMode` 相關測試全數通過。
  - RED：
  - GREEN：
- [ ] 1.4 整合測試（`AcceptAny`，非 Production）：(a) login、register、join purchase queue 以未曾產生的 token 與任意非空答案通過核對（login 以正確帳密回 200，register 與 queue 不回 `CaptchaInvalid`）；(b) `GET /api/captcha` 仍回新 token 與 base64 圖片，Redis 寫入帶 TTL 的雜湊；(c) token 空白或超過 64 字元、答案空白或超過 16 字元仍回 400 `Validation`（含逐欄位 `errors`）；(d) 超過 `CaptchaRateLimiting` 額度仍回 429。
  - 預期 RED：在 1.3 完成後、`AcceptAnyCaptchaService` 接線前，(a) 預期不回 `CaptchaInvalid`、實際回 400 `CaptchaInvalid`；(b)(c)(d) 為既有行為，另以「`AcceptAny` 下仍 400／429」的斷言在 GREEN 後確認未被繞過。
  - GREEN：`AcceptAny` 接線後 (a)–(d) 全數通過；`WebApi.Tests` 過濾 `CaptchaAcceptAny` 相關測試通過。
  - RED：
  - GREEN：
- [ ] 1.5 `CaptchaOptionsValidator`（`IValidateOptions<CaptchaOptions>`，WebApi）；啟動測試（比照 `RateLimitingOptionsFailFastTests`）：(a) Production + `AcceptAny` 啟動拋 `OptionsValidationException`，訊息含 Production；(b) 環境名稱 `production`（小寫）同樣失敗；(c) `Captcha:VerificationMode=99` 啟動失敗；(d) 拼錯名稱（`acceptany1`）啟動失敗；(e) Production + `Enforced` 正常啟動（防止驗證器誤擋）。
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

## 3. 同步與收尾

- [ ] 2.1 更新 `tests/ProjectC.WebApi.Tests/Startup/ServiceRegistrationSnapshot.baseline.txt`（`ICaptchaService` 註冊、新增驗證器）；快照測試通過。
- [ ] 2.2 更新 `docs/todo/2026-11-10-deployment-env-vars.md`（`Captcha__VerificationMode`：僅限非 Production、部署預設不設）；執行 `bash scripts/check-doc-paths.sh`。
- [ ] 2.3 將 `docs/todo/2026-11-10-captcha-test-switch-todo.md` 前兩項標為完成（第三項 K8s 3.3 重跑另做）。
- [ ] 2.4 hardener 最終檢查：本 change 只動設定驗證、DI 註冊與無 I/O 的 `AcceptAnyCaptchaService`，預期 N/A；實作完成後確認並記錄理由。
- [ ] 2.5 全套測試（各測試專案）逐字貼摘要行。
- [ ] 2.6 `graphify update .`。
