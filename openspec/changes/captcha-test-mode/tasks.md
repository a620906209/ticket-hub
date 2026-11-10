## 1. AC 與測試對應

| AC | 測試 task |
|----|-----------|
| CAPTCHA-MODE-001 設定缺漏為 Enforced | 1.1（預設值）、1.3（缺漏時錯誤答案仍 400） |
| CAPTCHA-MODE-002 Enforced 行為不變 | 1.3（Enforced 的 GETDEL 照常） |
| CAPTCHA-MODE-003 AcceptAny 任意答案通過 | 1.2、1.4 |
| CAPTCHA-MODE-004 產生端點不變 | 1.4 |
| CAPTCHA-MODE-005 欄位驗證與限流不繞過 | 1.4 |
| CAPTCHA-MODE-006 Production + AcceptAny 啟動失敗 | 1.5 |
| CAPTCHA-MODE-007 非 Production 可啟用並記 Warning | 1.6 |
| CAPTCHA-MODE-008 無效值擋下 | 1.5 |
| CAPTCHA-MODE-009 Enforced 不記 Warning | 1.6 |

## 2. 實作（依 TDD：先 RED 再 GREEN，證據逐字記錄於各 task 下）

- [ ] 1.1 `CaptchaOptions` 新增 `VerificationMode` 與列舉（預設 `Enforced`）；`CaptchaOptionsTests` 先寫預設值測試。
  - RED：（命令、失敗測試與 Expected／Actual 原文）
  - GREEN：
- [ ] 1.2 新增 `AcceptAnyCaptchaService`（Infrastructure）；單元測試：任意 token／答案皆通過、不呼叫 Redis。
  - RED：
  - GREEN：
- [ ] 1.3 `AddCaptcha` 以工廠依模式選實作；整合測試：缺漏／`Enforced` 時錯誤答案登入回 400 `CaptchaInvalid` 且 GETDEL 照常。
  - RED：
  - GREEN：
- [ ] 1.4 整合測試（`AcceptAny`）：登入、註冊、加入購買佇列以未曾產生的 token 與任意答案通過核對；`GET /api/captcha` 仍回新 token 與圖片並寫入 Redis；空白／過長欄位仍 400 `Validation`；產生端點超限仍 429。
  - RED：
  - GREEN：
- [ ] 1.5 `CaptchaOptionsValidator`（`IValidateOptions<CaptchaOptions>`，WebApi）；啟動測試（比照 `RateLimitingOptionsFailFastTests`）：Production + `AcceptAny` 拋 `OptionsValidationException` 且訊息指出 Production；`99` 與拼錯名稱啟動失敗；環境名稱不分大小寫。
  - RED：
  - GREEN：
- [ ] 1.6 `ResolveEagerlyValidatedOptions` 於 `AcceptAny` 記 Warning；測試：Development／Testing + `AcceptAny` 正常啟動且有 Warning，`Enforced`／缺漏無該 Warning。
  - RED：
  - GREEN：
- [ ] 1.7 重構後重跑相關測試（或標 N/A 並說明）。

## 3. 同步與收尾

- [ ] 2.1 更新 `tests/ProjectC.WebApi.Tests/Startup/ServiceRegistrationSnapshot.baseline.txt`（`ICaptchaService` 註冊、新增驗證器）；快照測試通過。
- [ ] 2.2 更新 `docs/todo/2026-11-10-deployment-env-vars.md`（`Captcha__VerificationMode`：僅限非 Production、部署預設不設）；執行 `bash scripts/check-doc-paths.sh`。
- [ ] 2.3 將 `docs/todo/2026-11-10-captcha-test-switch-todo.md` 前兩項標為完成（第三項 K8s 3.3 重跑另做）。
- [ ] 2.4 hardener 最終檢查：本 change 只動設定驗證、DI 註冊與無 I/O 的 `AcceptAnyCaptchaService`，預期 N/A；實作完成後確認並記錄理由。
- [ ] 2.5 全套測試（各測試專案）逐字貼摘要行。
- [ ] 2.6 `graphify update .`。
