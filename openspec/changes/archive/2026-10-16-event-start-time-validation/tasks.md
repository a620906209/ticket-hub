# Tasks

> 所有指令都在容器內執行（例如 `docker compose exec api dotnet test`），不在本機執行 dotnet。
>
> 本 change 沒有 AC 測試對應例外：所有 AC 都以 xUnit 涵蓋，整合測試用 Testcontainers，不連開發用的 db 服務。
>
> 1.1 是**實作前置閘門**，只驗證開發 DB，確保加守衛後本機開發不會壞。它不取代 5.1 的部署前閘門：每個實際部署目標環境都要在部署當下各自執行 5.1。

## AC 與測試對應

| AC | 測試檔／方法 | 任務 |
| --- | --- | --- |
| EVT-CREATE-006 | `WebApi.Tests/Admin/AdminEventsControllerTests.cs`：`CreateEvent_WhenStartHasNoUtcDesignator_Returns400AndPublicListStillLoads`（Theory：不提供販售欄位／提供帶 `Z` 的合法販售欄位；斷言 400、ProblemDetails `detail` 含 `StartAtUtc must be a UTC time (with 'Z' designator).`、沒有新增 Event 與 EventSeat、之後 `GET /api/events` 為 200）；`Application.Tests/Events/CreateEvent/CreateEventHandlerTests.cs`：`HandleAsync_WhenStartKindIsUnspecified_ReturnsValidationOnStartAtUtc` | 1.2、2.1 |
| EVT-CREATE-007 | `AdminEventsControllerTests`：`CreateEvent_WhenStartHasOffset_Returns400AndCreatesNoRows`（Theory：`+08:00`／`+00:00`；斷言同 006 的 UTC 訊息）；`CreateEventHandlerTests`：`HandleAsync_WhenStartKindIsLocal_ReturnsValidationOnStartAtUtc` | 1.2、2.1 |
| EVT-CREATE-008 | `AdminEventsControllerTests`：`CreateEvent_WhenStartIsMaxValue_Returns400AndPublicListStillLoads`（`9999-12-31T23:59:59.9999999Z`；斷言 `detail` 含 `StartAtUtc must not be the maximum date.`）；`CreateEventHandlerTests`：`HandleAsync_WhenStartIsMaxValue_ReturnsValidationOnStartAtUtc` | 1.2、2.1 |
| EVT-CREATE-009 | `AdminEventsControllerTests`：`QueryEvents_WhenStoredStartIsUtc_AllQueryEndpointsReturn200`（Arrange 以帶 `Z` 的時間經 API 建立活動，只是寫入資料的手段；驗證重點是讀取路徑。Act 之後，查公開列表、`GET /api/events/{id}/seats`、後台列表都是 200；兩個列表回傳的 `StartAtUtc` 與建立時相同且以 `Z` 結尾） | 3.3 |

Domain 守衛（design 決策 2）不屬於 API 可觀察行為，由 `Domain.Tests/Events/EventTests.cs` 驗證（3.1）。

## 1. 前置確認與重現

- [x] 1.1 在開發 DB 執行 design 決策 3 步驟 1 的完整指令（逐字照抄，不自行改寫），記錄輸出。驗證：輸出為單一數字 `0`。指令失敗或沒有輸出時停下回報。不為 0 時，先停下回報使用者，由使用者決定用決策 3 步驟 3 的哪一種修復方式；修復後完成步驟 4 的 SQL 重查，結果為 0 才進入 3.1。
  - 完成紀錄（2026-10-15 15:28 UTC，本機開發 DB）：逐字執行步驟 1 指令，輸出 `0`，exit code 0。
- [x] 1.2 先寫 EVT-CREATE-006～008 的 API 整合測試（AC 表所列 3 個方法）。斷言除了既有的 `AssertCreateEventRejectedWithoutRowsAsync`（只檢查 400 與 Event／EventSeat 筆數），另外把回應讀成 `ProblemDetails`，斷言 `detail` 含 AC 表所列的完整訊息（比照既有 `problem!.Detail.Should().Contain(...)` 寫法）。斷言完整句子，不能只比對 `StartAtUtc` 字樣，因為提供販售欄位時 `detail` 可能另含整微秒規則的 `StartAtUtc` 訊息，會造成假通過；006 與 008 再斷言之後 `GET /api/events` 為 200（只是附帶健全檢查：修正前不會失敗，主要判別依據是 400 與 `detail` 訊息）。在修正前執行。驗證：3 個測試都失敗，從失敗訊息記錄實際狀態碼，預期 006／007 為 500、008 為 201。若狀態碼不同，停下回報，因為 proposal 的問題描述可能不成立。
  - 完成紀錄：修正前 5 個案例全部失敗，狀態碼與預期一致：006（不帶／帶販售欄位）皆 500、007（`+08:00`／`+00:00`）皆 500、008 為 201。`AssertCreateEventRejectedWithoutRowsAsync` 改為回傳 `HttpResponseMessage`，供讀取 `ProblemDetails`；既有呼叫端不受影響。

## 2. Application：Validator

- [x] 2.1 在 `CreateEventRequestValidator` 為 `StartAtUtc` 加上 Kind 必須為 UTC（`.When(x => x.StartAtUtc != default)`）與 `NotEqual(DateTime.MaxValue)` 兩條規則，並在 `CreateEventHandlerTests` 新增 AC 表所列 3 個測試，另加 `HandleAsync_WhenStartIsMissing_ReturnsOnlyRequiredErrorOnStartAtUtc`（斷言 `StartAtUtc` 只有一則錯誤，沒有多疊 UTC 錯誤）。驗證：
  - Application 測試全部通過，1.2 的 3 個 API 測試轉為通過。
  - 反向驗證：移除 Kind 規則時 006／007 的測試失敗；移除 MaxValue 規則時 008 的測試失敗；拿掉 `.When` 時 `WhenStartIsMissing` 失敗。驗證後還原。
  - 完成紀錄：Application 489 tests 通過；1.2 的 5 個 API 案例轉為通過。反向驗證已執行並還原：移除 Kind 規則時 Application 2 個 Kind 測試與 API 4 個 006／007 案例失敗；移除 MaxValue 規則時 Application 與 API 的 008 測試各 1 個失敗；拿掉 `.When` 時 `WhenStartIsMissing` 失敗（API 測試不受影響，符合預期）。

## 3. Domain 守衛與既有資料

- [x] 3.1 在 `Event` 建構子加上 `startAtUtc.Kind != DateTimeKind.Utc` 時丟 `ArgumentException`，並在 `EventTests` 新增 `Constructor_WhenStartKindIsUnspecified_ThrowsArgumentException`、`Constructor_WhenStartKindIsLocal_ThrowsArgumentException`。驗證：Domain 測試通過；移除守衛時這 2 個測試失敗，驗證後還原。
  - 完成紀錄：Domain 151 tests 通過；兩個測試另斷言 `WithParameterName("startAtUtc")`，避免被其他參數的例外假通過。移除守衛時 2 個測試失敗，已還原。
- [x] 3.2 執行全部後端測試（`docker compose exec api dotnet test`），把因守衛而失敗、用了非 UTC 時間的測試 fixture 改成 UTC。只改時間的 Kind，不改測試意圖與斷言。驗證：全部測試通過，沒有 skipped；完成紀錄列出修改的檔案數。若失敗原因不是 fixture 的 Kind，停下回報。
  - 完成紀錄（2026-10-15；2026-10-16 使用者確認 WSL2 TTL flake 不影響本項，同意勾選）：修改 fixture 檔數 0，沒有任何測試因 Kind 守衛失敗。Domain 151、Application 489、LoadTest.Seeder 38、WebApi 494 全部通過，無 skipped。Infrastructure 231 中有 1 個失敗，且兩次完整執行失敗的測試不同：`QueryCacheTtlSafetyNetTests.GetEventsHandler_WithinToleranceAroundTtl_HitsBeforeAndMissesAfter`、`GetTicketTypesHandler_WhenInvalidationHappensWhileQueryInFlight_...`（`到期前 key 應仍存在`）。單獨執行該類別 3 次皆通過；`git stash` 回到乾淨 HEAD 完整執行 2 次，同樣這兩個測試各失敗一次。即 `docs/project-scope.md` §8 已記錄的 WSL2 時鐘環境問題，與本 change 無關，依政策未修改、未重試／跳過。
- [x] 3.3 新增 EVT-CREATE-009 整合測試（見 AC 表）。驗證：測試通過；反向驗證時，暫時在 `EventConfiguration` 為 `StartAtUtc` 加上只影響讀取的轉換 `HasConversion(v => v, v => DateTime.SpecifyKind(v, DateTimeKind.Unspecified))`，模擬讀回時 Kind 不是 UTC（寫入不受影響）。此時測試必須在查詢階段失敗，不是在建立階段失敗。驗證後還原。註：此反向驗證證明的是「測試會抓到讀回 Kind 非 UTC」（不論是守衛丟例外或 JSON 缺 `Z`），不是證明守衛本身；「守衛不誤傷一般 UTC 資料」由測試正向通過證明。
  - 完成紀錄：測試通過。`StartAtUtc` 以 `JsonDocument` 讀原始字串比對（反序列化成 `DateTime` 會吞掉缺 `Z` 的差異）。反向驗證已執行並還原：加上讀取轉換後，建立回 201，失敗點在公開列表查詢（500，`ReadStartAtUtcJsonAsync` 的狀態碼斷言），屬查詢階段。

## 4. 文件與審查

- [x] 4.1 更新 `docs/project-scope.md` §8 的 R4 與「`StartAtUtc` 極端值」兩條：標示已由本 change 處理，並註明「部署前須執行 design 決策 3 的資料閘門（檢查 → 不為 0 禁止部署 → 修復 → 驗證）」，附上步驟 1 指令。驗證：兩條都已標示，且指令與 design 決策 3 逐字一致。
  - 完成紀錄：兩條標題改為「已處理」並註明本 change，下方新增共用的「部署須知」子項附步驟 1 指令；以 shell 字串比對確認與 design.md 逐字相同。
- [x] 4.2 程式碼變更全部完成後，套用 `.claude/skills/hardener/SKILL.md` 檢查清單一次（Validator 屬 Application 層）。驗證：在完成紀錄寫下檢查結果；若因此改了程式碼，記錄改了什麼。
  - 完成紀錄：套用前必答——輸入驗證：`CreateEventRequestValidator`（FluentValidation），由 `CreateEventHandler` 在建立實體前呼叫；權限：沿用建立活動端點既有授權（Approved Organizer，`OrganizerId` 取自 JWT），本次未改；N+1：未新增查詢；悲觀鎖：否，只是單筆建立的輸入驗證。檢查清單：1️⃣ 參數驗證——新規則放在 Validator，單一 Entity 不變量（Kind）放 Domain，MaxValue 依 design 決策 2 只在 Validator，符合分層表；2️⃣ Entity 存在性、3️⃣ 狀態機、5️⃣ 並發——不適用，本次未新增查詢或狀態轉移；4️⃣ `CancellationToken`——新規則為同步純比較，無 I/O；6️⃣ 例外邊界——Domain 守衛沿用既有 `ArgumentException` 慣例，Validator 先擋成 400；7️⃣ 日誌——未新增 log，`detail` 只含規則訊息不含輸入值。結論：無遺漏，未修改程式碼。
- [x] 4.3 strict-reviewer 加上新開的對抗式 general-purpose subagent 審查，重複到同一輪兩者都 PASS；之後若有任何修改，再跑一輪。驗證：完成紀錄寫下每一輪的結論與修正。
  - 第 1 輪：strict-reviewer PASS（無 blocking）；對抗式 subagent PASS，另有 2 個非阻擋發現，皆已修正。A：`StartAtUtc must be a UTC time…` 是 `SalesStartAtUtc must be a UTC time…` 的子字串，若 `SalesStartAtUtc` 也非 UTC，`Contain` 會假通過。修正：`AssertProblemDetailContainsAsync` 在 `detail` 與預期訊息前各補一個空白，比對訊息邊界。實證：移除 `StartAtUtc` Kind 規則，並讓 006 帶販售欄位的案例送出非 UTC 的開賣時間；舊斷言下該案例通過（假通過），新斷言下失敗。已還原。B：`docs/project-scope.md`「`StartAtUtc` 極端值」內文仍寫「讀回不會丟例外」與「建議合併為小 change」，與本 change 後的行為矛盾。修正：改標為變更前實測，並說明本 change 後既有 `infinity` 資料會讓讀取 500。
  - 第 2 輪（2026-10-16）：strict-reviewer PASS、對抗式 subagent PASS，同一輪兩者皆通過，未再修改。對抗式另實測接近最大／最小值（`…59.9999998Z`、`0001-01-01T00:00:00.0000001Z` 等）寫入後讀回仍為有限 UTC 值，只擋剛好等於 MaxValue 已足夠。非阻擋觀察未處理：006／008 的「之後公開列表 200」可能命中查詢快取（tasks 1.2 已定位為附帶健全檢查，讀取路徑由 009 負責）；`ReadStartAtUtcJsonAsync` 內含狀態碼斷言。

## 5. 部署前資料閘門（每個部署目標環境）

- [ ] 5.1 每個實際部署目標環境，在部署含本 change 的版本之前，各自執行 design 決策 3 的四個步驟。目前唯一的目標環境是本機 Docker Compose 開發環境；之後若新增 staging／production 等環境，每個環境都要各自執行，開發環境的結果不能替代。驗證：
  - 每個環境都留下紀錄：環境名稱、執行時間、步驟 1 的指令輸出、是否做過修復。
  - 任一環境的結果不為 0，該環境禁止部署，停下回報使用者，由使用者決定修復方式。
  - 修復後在同一環境重跑步驟 1，結果必須為 0；部署後在同一環境驗證 `GET /api/events`、後台 `GET /api/admin/events`、`GET /api/events/{id}/seats` 三個端點都回 200（有修復過的活動，用該活動驗證）。
  - 本項在 merge 後、部署時執行，不擋 archive；完成紀錄註明執行的環境與時間。

## Workflow follow-up

- 使用者指示後才 commit、archive、merge。
