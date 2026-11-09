# Tasks

> 對應 spec：`event-catalog`（ADDED：EVT-DETAIL-001～008）、`buyer-web-ui`（ADDED：BW-DETAIL-001～005；MODIFIED：三條既有 Requirement 的詳情頁資料來源敘述）。`query-caching` 不修改。
> 所有後端命令在容器內執行：`docker compose exec -T api dotnet test <專案> --filter "..."`；前端：`docker compose exec -T web npm run test -- <檔案>`。
> 每個 task 的「完成紀錄」於實作時依 CLAUDE.md「證據貼原文」填寫（RED：命令、失敗測試名、Expected／Actual 原文，需在修改正式程式碼前；GREEN：命令與摘要行；重構後重跑或 N/A）。核心查詢 task（1.1）另預留「還原驗證」紀錄，使用 `scripts/verify-test-evidence.sh`（mutate／restore 規則見 CLAUDE.md；mutate 後 `touch` 被改檔案再重建）。

## 1. 後端單一活動查詢

- [ ] 1.1 在 `tests/ProjectC.Application.Tests/Events/GetEventById/` 新增 `GetEventByIdHandlerTests`：先寫測試，涵蓋 EVT-DETAIL-001（取得存在活動，欄位與列表同筆一致）、EVT-DETAIL-002（不存在回 `ErrorType.NotFound`）、EVT-DETAIL-004（快取命中不呼叫 Repository）、EVT-DETAIL-005（未命中時 `GetAllAsync` 恰好呼叫一次並寫回 `GetEventsHandler.CacheKey`）、EVT-DETAIL-006（失效後再查得新值）、EVT-DETAIL-008 的回退路徑（以既有 `FakeQueryCache` 表示「未命中」：查 DB 後回傳該活動、不存在仍回 `NotFound`；fake 不得拋 Redis 例外，因 `IQueryCache` 契約為內部吞例外不外拋，真實 Redis 斷線行為由 1.2 的 HTTP 層測試驗證）。預期 RED：先建立 `GetEventByIdHandler` 空殼（簽章 `Task<Result<EventDto>> HandleAsync(Guid, CancellationToken)`，一律回 `NotFound`）使其可編譯，測試因 Expected／Actual 不符失敗（不得以編譯失敗當 RED）。再實作：呼叫 `GetEventsHandler.HandleAsync` 後 `FirstOrDefault(e => e.Id == id)`，找不到回 `Error.NotFound`。並依 hardener 清單先列防禦分支：`CancellationToken` 傳遞、`Guid.Empty` 視為不存在。
  - EVT-DETAIL-004 須斷言快取命中時 Repository 的**任何方法**（含 `GetByIdAsync`、`GetAllAsync`）皆未被呼叫（以 strict mock／所有方法計數），否則無法殺掉「略過 `GetEventsHandler` 改直接 `GetByIdAsync`」的 mutate。
  - 因 RED 空殼一律回 `NotFound`，EVT-DETAIL-002 及 EVT-DETAIL-008 的不存在分支、`Guid.Empty` 案例在 RED 時即通過；RED 紀錄須列出「RED 時預期已通過」的測試名稱，並以 mutate 證明它們能失敗（例如把實作改成一律回傳列表第一筆活動，這些測試必須失敗）。
  - RED 紀錄：（實作時填寫）
  - GREEN 紀錄：（實作時填寫）
  - 還原驗證紀錄（mutate，一次一個改動，例如 `FirstOrDefault` 條件反向、略過 `GetEventsHandler` 改直接 `GetByIdAsync`）：（實作時填寫）
  - 重構後重跑：（實作時填寫或 N/A）

- [ ] 1.2 加入 HTTP 層測試，分兩處（factory 不可混用）：
  - `tests/ProjectC.WebApi.Tests/Events/EventsControllerTests.cs`（既有檔，`CustomWebApplicationFactory`）：EVT-DETAIL-001（`200` 且 body 欄位與同一活動在 `GET /api/events` 一致）、EVT-DETAIL-002（`404` ProblemDetails）、EVT-DETAIL-007（匿名與已登入不同角色回應相同）。
  - 使用 `CachingComponentTestWebApplicationFactory`（含 `CountingEventRepository`，真實 Redis，放在既有 `QueryCaching*Tests` 類別或同目錄新類別）：EVT-DETAIL-003（`/api/events/not-a-guid` 回 `404`，`CountingEventRepository` 計數為 0，且列表快取 key 未被寫入，比照 QC-ACCESS-003 測試手法，不使用不存在的 `IQueryCache` 呼叫計數）、EVT-DETAIL-005（冷快取 `GetAllAsync` 恰一次、列表 key 被寫入、之後 `GET /api/events` 不再查庫）。
  - 使用 `RedisUnavailableWebApplicationFactory`（`QueryCachingFailOpenTests.cs` 既有手法）：EVT-DETAIL-008（存在的活動回 `200`、不存在的活動回 `404`、且有 Redis 連線失敗 Warning log）。
  - EVT-DETAIL-004 的 HTTP 層對應：先預熱列表快取，再呼叫單筆端點，`CountingEventRepository` 計數不增加。`CountingEventRepository` 目前只計 `GetAllAsync`（`GetByIdAsync` 為 pass-through 不計數），本 task 一併擴充為兩個方法分別計數，EVT-DETAIL-003 的「Repository 計數為 0」改為兩者皆為 0。
  - 1.2 的 404 案例（EVT-DETAIL-002、003、008 不存在分支）在 RED 空殼（回 `NotFound()`）下即通過，RED 紀錄列出這些測試，並以 mutate 證明能失敗（例如把路由改為 `{id}` 去掉 `:guid` 約束使 003 失敗、讓 action 一律回 `NotFound()` 使 001 失敗）。
  - EVT-DETAIL-006 的真實失效觸發由既有 QC-EVT-INV-002 涵蓋，本 task 不重複；1.1 以 handler 層驗證。
  - 預期 RED：在 `EventsController` 加入尚未實作的 `GetEventById` action（回 `NotFound()`）後測試因狀態碼／body 不符失敗。再實作：`[HttpGet("{id:guid}")]` 呼叫 `GetEventByIdHandler`，`result.ToActionResult(Ok)`；於 DI 註冊 `GetEventByIdHandler`（lifetime 同 `GetEventsHandler`，理由記入 PR 說明）。
  - RED 紀錄：（實作時填寫）
  - GREEN 紀錄：（實作時填寫）
  - 重構後重跑：（實作時填寫或 N/A）

## 2. 前端詳情頁改用單一活動 API

- [ ] 2.1 在 `web/src/api/events.ts` 新增 `getEventById(eventId)`；於 `EventDetailPage.test.ts` 先改寫測試：全檔 `getEvents` mock 改為 `getEventById`（既有測試名稱列入 RED 紀錄）；新增 BW-DETAIL-001（不呼叫 `getEvents`）、BW-DETAIL-002（`getEventById`、座位、票種三者皆以 `ApiError` 404 拒絕（真實情境）→ 顯示「找不到這個活動」且無錯誤訊息）、BW-DETAIL-003（涵蓋兩個組合：僅 `getEventById` 以 `ApiError` 500 拒絕；以及 `getEventById` 500 且座位／票種 404 → 皆顯示「載入活動資訊失敗」且無「找不到」，以活動請求的結果為判定依據）、BW-DETAIL-004（既有測試 `EventDetailPage.test.ts` 中以 `getEvents toHaveBeenCalledTimes(2)` 驗證重載的案例改為 `getEventById` 兩次、`getEvents` 零次；須各有一個測試涵蓋三條重新載入路徑：下單一般失敗後、下單成功後（`EventDetailPage.vue:759`）、下單成功後離頁導覽失敗而留在本頁時的重新同步（`:420`），每個都斷言 `getEventById` 被再次呼叫且 `getEvents` 零次）、BW-DETAIL-005（`getEventById` 200，座位或票種以 `ApiError` 500 拒絕 → 顯示「載入活動資訊失敗」且無「找不到」，各一個測試）。BW-DETAIL-001 另斷言並行載入：以不 resolve 的 deferred promise 讓 `getEventById`、座位、票種三個呼叫在任一者 resolve 前皆已發出。預期 RED：先加 `getEventById` 空殼（`reject`）使測試可執行，因斷言不符失敗（非編譯／匯入失敗）。再實作 `loadData`：依設計決策 3 以 `Promise.allSettled`（或等效）並行載入，活動 `404` → `event=null`，其他失敗沿用 `toErrorMessage(error, '載入活動資訊失敗')`。同時更新其他掛載 `EventDetailPage` 並 mock `getEvents` 的前端測試：`orderConfirmationFlow.test.ts`（以全套 Vitest 為準；`BuyerLayout.test.ts`、`elementPlusOnDemand.test.ts` 掛載的是列表頁，其 `getEvents` mock 不得更動）。實作時先列出 `grep getEvents web/src` 的所有受影響測試。`loadData` 實作後以 `Promise.allSettled` 判定：活動 `404` → `event=null`；活動非 `404` 失敗，或活動成功但座位／票種任一失敗 → 沿用 `toErrorMessage(error, '載入活動資訊失敗')`，不得出現有活動內容卻無錯誤訊息的情況（BW-DETAIL-005）。注意現行模板在載入失敗時  為 null，頂端 el-alert 與「找不到這個活動」的 el-empty 會同時顯示（BW-FLOW-004 未斷言其不存在）；BW-DETAIL-003／005 要求失敗時不顯示「找不到」，故 `EventDetailPage.vue` 的 el-empty 條件須加上 `!errorMessage`，BW-FLOW-004 等既有載入失敗測試需確認不受影響（屬行為變更，測試於 RED 時應能抓到）。2.1 的 BW-DETAIL-004 測試可參考既有 BW-CONFIRM-015、BW-CONFIRM-020 離頁失敗案例，遷移為斷言 `getEventById` 第 2 次被呼叫；既有 `toHaveBeenCalledTimes(1)` 的 401／SalesNotOpen「不重載」測試須保留語意。
  - RED 紀錄：（實作時填寫）
  - GREEN 紀錄：（實作時填寫）
  - 重構後重跑：（實作時填寫或 N/A）

## 3. 負載測試核對

- [ ] 3.1 `loadtest/lib/admin-api.js` 的 `assertEventOnSale` 改為 `getOk(\`/api/events/${eventId}\`)`，移除 `Array.isArray`／`find`，保留販售中判斷與既有失敗訊息格式；同步 `loadtest/README.md` 若有提到列表核對。保留既有的 `withTags({}, 'setup')`。`assertEventOnSale` 另有 `loadtest/fixtures/purchase-flow-ux-event.js:73` 呼叫端，一併納入驗證。TDD N/A：k6 腳本無自動化單元測試（`loadtest/tests` 沒有涉及 `assertEventOnSale` 或 `/api/events` 的檢查，design 決策 4）；替代驗證（有可判定的通過／失敗斷言）：
    1. 先依 loadtest README 跑 seeder（`--buyers 2`），再以 `bash loadtest/fixtures/run-purchase-flow-ux-event.sh` 實際跑 purchase-flow-ux fixture（需其設定的 `PFUX_FIXTURE_LOCK_TOKEN`，不可直接呼叫 k6），執行到 `assertEventOnSale` 通過為止，exit code 為 0。
    2. 以 `scripts/verify-test-evidence.sh mutate` 暫時把傳給 `assertEventOnSale` 的 id 換成 `00000000-0000-0000-0000-000000000000`（fixture 的 eventId 來自剛建立的活動、無 env 可替換），執行同一路徑，必須 fail 且訊息含 `GET /api/events/<實際 id> -> 404`（`describeFailure` 格式）（失敗訊息由原本的「not found in GET /api/events」改為 `describeFailure` 格式，語意不變、格式改變）。
    3. 驗證會寫入 dev DB（建立活動、票種、訂單；fixture 保留上限 RETENTION_LIMIT=6），完成後依 loadtest README「清理」段落清理，並以 restore 還原腳本。
    4. 以 curl 對 `/api/events/{id}` 核對回應欄位含 `salesStartAtUtc`、`salesEndAtUtc`、`isQueueModeEnabled`、`startAtUtc`。
  - 完成紀錄：（實作時填寫）

## 4. 防禦性最終檢查與審查

- [ ] 4.1 hardener 最終檢查：對 `GetEventByIdHandler`、`EventsController.GetEventById` 套用 `.claude/skills/hardener/SKILL.md` 清單（參數驗證、Entity 存在性、`CancellationToken` 傳遞、例外邊界、日誌遮蔽）；若有補強依 TDD 先補 RED 測試。
  - 完成紀錄：（實作時填寫）

- [ ] 4.2 全套測試：`docker compose exec -T api dotnet test`（各測試專案）與 `docker compose exec -T web npm run test`，逐專案貼摘要行（含 Passed／Failed／Skipped 數）；任何 skipped 需列出原因。
  - 完成紀錄：（實作時填寫）

- [ ] 4.3 審查迴圈：依序 `strict-reviewer` 與全新的對抗式 subagent，修正至兩者皆無 blocking；再以 herdr Claude Opus 做交叉審查。每輪結果與 commit 記於此。
  - 完成紀錄：（實作時填寫）

## 5. 前端真實瀏覽器驗證

- [ ] 5.1 以 claude-in-chrome 實際操作：開啟存在活動的詳情頁（網路面板確認呼叫 `/api/events/{id}` 而非 `/api/events`，活動資訊與座位正常）；開啟不存在但格式合法的活動 id 確認顯示「找不到這個活動」；以瀏覽器重新載入頁面，以及製造一次下單失敗（例如座位已被搶先鎖定）後的重新載入，確認再次呼叫單筆端點而非列表。數字與狀態以頁面文字或 DB 核對，不只依截圖。
  - 完成紀錄：（實作時填寫）

## 6. 規格同步檢查

- [ ] 6.1 歸檔前確認：`event-catalog`、`buyer-web-ui` delta 與實作一致；`docs/api-conventions.md`、`docs/api-conventions-todo.md` 本 change 不需更新（Change 2 處理）；`openspec validate public-event-detail-endpoint` 通過。
  - 完成紀錄：（實作時填寫）
