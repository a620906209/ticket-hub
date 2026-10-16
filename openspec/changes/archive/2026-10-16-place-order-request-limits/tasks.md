# Tasks

> 所有指令都在容器內執行（例如 `docker compose exec api dotnet test`），不在本機執行 dotnet。
>
> 本 change 沒有 AC 測試對應例外：所有 AC 都以 xUnit 涵蓋，整合測試用 Testcontainers，不連開發用的 db 服務。

## AC 與測試對應

| AC | 測試檔／方法 | 任務 |
| --- | --- | --- |
| TP-LIMIT-001 | `WebApi.Tests/Orders/OrdersControllerTests.cs`：`PlaceOrder_WhenTotalQuantityExceedsSystemLimitWithoutEventLimit_Returns400AndKeepsInventory`（活動不設限購、計數票種可售 60、購買 51；斷言 400、`detail` 含總張數訊息、沒有新增 Order、`AvailableQuantity` 仍為 60）；`Application.Tests/Orders/PlaceOrder/PlaceOrderRequestValidatorTests.cs`：`Validate_WhenTotalQuantityIs51_IsInvalidWithTotalMessage` | 1.1、2.1 |
| TP-LIMIT-002 | `OrdersControllerTests`：`PlaceOrder_WhenTotalQuantityEqualsSystemLimit_Returns201`（可售 60、購買 50；斷言 201、`AvailableQuantity` 為 10）；`PlaceOrderRequestValidatorTests`：`Validate_WhenTotalQuantityIs50_IsValid`、`Validate_WhenSelectionCountIs50_IsValid` | 1.1、2.1 |
| TP-LIMIT-003 | `OrdersControllerTests`：`PlaceOrder_WhenSelectionCountExceedsSystemLimit_Returns400WithOnlyCountMessage`（51 筆 `TicketTypeId` 為空 Guid 的項目；斷言 400、`detail` **等於**筆數訊息）；`PlaceOrderRequestValidatorTests`：`Validate_WhenSelectionCountIs51_ReturnsOnlyCountError`（斷言 `Errors` 恰為 1 則且為筆數訊息）；`OrderServiceTests`：`PlaceOrderAsync_WhenSelectionCountExceedsSystemLimit_DoesNotOpenConnection`（斷言 `UnitOfWork.OpenConnectionCallCount` 為 0，守住「借連線前擋下」） | 1.1、2.1 |
| TP-LIMIT-004 | `OrdersControllerTests`：`PlaceOrder_WhenQuantitiesSumBeyondIntMax_Returns400NotServerError`（斷言 400、`detail` 含總張數訊息）；`PlaceOrderRequestValidatorTests`：`Validate_WhenQuantitiesSumBeyondIntMax_IsInvalidWithTotalMessage`（直接呼叫 `Validate`，不得拋例外） | 1.1、2.1 |
| TP-LIMIT-005 | `Application.Tests/Orders/OrderServiceTests.cs`：`PlaceOrderAsync_WhenEventLimitExceedsSystemLimit_RejectsAt51WithoutReserving`（Fake 活動 `MaxTicketsPerOrder` 為 100，購買 51；斷言驗證錯誤含總張數訊息、`GetForUpdateCallCount` 為 0、可售量不變） | 2.1 |
| TP-LIMIT-006 | `OrdersControllerTests`：`PlaceOrder_WhenSelectionsContainNullItem_Returns400NotServerError`（`{"selections":[null]}` 與「一筆合格計數項目加一個 null」兩種本文，以 raw JSON 送出；斷言 400、`detail` 含 null 元素訊息、沒有新增 Order）；`PlaceOrderRequestValidatorTests`：`Validate_WhenSelectionsContainNullItem_IsInvalidWithoutThrowing`（直接呼叫 `Validate`，不得拋例外，`Errors` 含 null 元素訊息） | 1.2、2.1 |
| TP-LIMIT-007 | `OrdersControllerTests`：`PlaceOrder_WhenSelectionsMissingOrNull_Returns400NotServerError`（`{}` 與 `{"selections":null}` 兩種本文，以 raw JSON 送出；斷言 400、沒有新增 Order。不斷言 `detail` 內容，因為可能由模型繫結的必填檢查或 Validator 擋下）；`PlaceOrderRequestValidatorTests`：`Validate_WhenSelectionsIsNull_IsInvalidWithoutThrowing` | 1.2、2.1 |
| EVT-LIMIT-001 | `WebApi.Tests/Admin/AdminEventsControllerTests.cs`：`CreateEvent_WhenMaxTicketsPerOrderExceedsSystemLimit_Returns400AndCreatesNoRows`（沿用 `AssertCreateEventRejectedWithoutRowsAsync` 與 `AssertProblemDetailContainsAsync`）；`Application.Tests/Events/CreateEvent/CreateEventHandlerTests.cs`：`HandleAsync_WhenMaxTicketsPerOrderIs51_ReturnsValidationOnMaxTicketsPerOrder` | 1.1、2.2 |
| EVT-LIMIT-002 | `AdminEventsControllerTests`：`CreateEvent_WhenMaxTicketsPerOrderEqualsSystemLimit_Returns201`（斷言後台列表該活動 `MaxTicketsPerOrder` 為 50）；`CreateEventHandlerTests`：`HandleAsync_WhenMaxTicketsPerOrderIs50_Succeeds` | 1.1、2.2 |
| EVT-LIMIT-003 | `AdminEventsControllerTests`：`QueryEvents_WhenStoredMaxTicketsPerOrderExceedsSystemLimit_ListsStillLoad`（Arrange 直接經 DbContext 寫入 `MaxTicketsPerOrder` 為 100 的活動，模擬規則生效前的資料；斷言公開與後台列表皆 200，回傳值為 100） | 2.2 |

| TP-LIMIT-008 | `OrdersControllerTests`：`PlaceOrder_WhenFiftySeatItemsOmitQuantity_Returns201`（50 個可售座位，raw JSON 每筆只帶 `eventSeatId`／`ticketTypeId`；斷言 201、50 個座位皆為 Held）、`PlaceOrder_WhenFiftyOneSeatItemsOmitQuantity_Returns400WithOnlyCountMessage`（斷言 400、`detail` 等於筆數訊息、座位皆仍可售）；`PlaceOrderRequestValidatorTests`：`Validate_WhenFiftySeatItemsUseDefaultQuantity_IsValid`（以 `new PlaceOrderSelectionRequest(seatId, ticketTypeId)` 建構，不指定 `Quantity`） | 1.1、2.1 |
| TP-LIMIT-009 | `OrdersControllerTests`：`PlaceOrder_WhenFiftySelectionsTotalFiftyOne_Returns400WithTotalMessage`（49 座位項目 + 1 計數項目數量 2；斷言 400、`detail` 含總張數訊息且不含筆數訊息、座位皆仍可售、可售量不變）；`PlaceOrderRequestValidatorTests`：`Validate_WhenFiftySelectionsTotalFiftyOne_ReturnsOnlyTotalError`（`Errors` 恰為 1 則總張數訊息） | 1.1、2.1 |
| TP-LIMIT-010 | `OrdersControllerTests`：`PlaceOrder_WhenSeatItemQuantityIsTwoWithinSystemLimit_Returns400WithSeatQuantityMessage`（斷言 400、`detail` 含 `must have a quantity of exactly 1.`、不含總張數訊息、座位仍可售）；`PlaceOrderRequestValidatorTests`：`Validate_WhenSeatItemQuantityIsTwoWithinLimit_IsValid`（Validator 不負責座位數量規則，守住規則分工） | 1.1、2.1 |
| 「開啟資料庫連線之前拒絕」（TP-LIMIT-001、003、004、006、007、008(b)、009） | `OrderServiceTests`：`PlaceOrderAsync_WhenRequestViolatesSystemLimits_RejectsBeforeOpeningConnection`（`[Theory]`，每類違規一筆資料：總張數 51、筆數 51、兩筆 `int.MaxValue`、含 null 元素、`Selections` 為 null、51 筆預設數量座位、50 筆合計 51；逐筆斷言回傳 `ErrorType.Validation`、`UnitOfWork.OpenConnectionCallCount` 為 0；3.1 加入 `GetByIdsCallCount` 後，同一 Theory 補斷言 `FakeTicketTypeRepository.GetByIdsCallCount` 為 0）。TP-LIMIT-003 原有的 `PlaceOrderAsync_WhenSelectionCountExceedsSystemLimit_DoesNotOpenConnection` 併入此 Theory。TP-LIMIT-005 已斷言 `GetForUpdateCallCount` 為 0；它在 Validator 擋下，同時列入此 Theory（活動限購 100、購買 51） | 2.1、3.1 |

決策 4（票種批次查詢）沒有新的 API 可觀察行為，由下列測試守住（3.1、3.2）：

- `Application.Tests/Orders/OrderServiceTests.cs`：`PlaceOrderAsync_WithMultipleTicketTypes_QueriesTicketTypesOnce`（3 個不同票種；斷言 `GetByIdsCallCount` 為 1）、`PlaceOrderAsync_WhenTwoTicketTypesMissing_ReportsFirstMissingInRequestOrder`（斷言訊息為請求順序中第一個缺漏的 Id）。
- `Infrastructure.Tests`：`TicketTypeRepository.GetByIdsAsync` 回傳實體未被追蹤（`ChangeTracker.Entries<TicketType>()` 為空）、只回傳存在的 Id、空清單回傳空集合且不發出查詢（比照既有 `OrderDisplayLookupRepositoryTests.GetByIdsAsync_WithEmptyList_ReturnsEmptyWithoutQuery` 的驗證方式）。
- 既有 TP-ORDER-003（不存在的票種回 404）測試維持通過。

## 0. 前置依賴

- [x] 0.1 確認前置 change `request-body-size-limits` 已 archive 並 merge 到 master（design「前置依賴」的完成條件）。驗證：`openspec/changes/archive/` 下有該 change，且 master 的 git log 含其 merge commit。未完成時不得開始 1.1。

## 1. 前置確認與重現

- [x] 1.1 先寫 AC 表中 WebApi 層的 6 個測試（TP-LIMIT-001～004、EVT-LIMIT-001～002），在修正前執行。驗證：預期 TP-LIMIT-001 為 201（或 409／其他非 400，記錄實際值）、TP-LIMIT-002 為 201（修正前就會通過，屬邊界保護）、TP-LIMIT-003 為 400 但 `detail` 含逐項訊息（斷言失敗）、TP-LIMIT-004 為 400 或 500（記錄實際值）、EVT-LIMIT-001 為 201、EVT-LIMIT-002 為 201。同時寫 TP-LIMIT-008～010 的 WebApi 測試並在修正前執行：預期 008(a) 為 201（邊界保護）、008(b) 為 201、009 為 201、010 為 400 且含座位數量訊息（邊界保護）。實際結果與預期不同時停下回報，因為 proposal 的問題描述可能不成立。
  - 完成紀錄（2026-10-16）：TP-LIMIT-001 201、002 201、003 400 但 `detail` 不符、**004 為 409**（與預期 400／500 不同：`OrderService` 既有 `long` 加總不溢位，活動未設限購時走到交易前庫存檢查回「庫存不足」409；問題描述「沒有系統上限、請求會借連線查詢」仍成立，使用者確認記錄後繼續，spec 與斷言不改）、008(a) 201、008(b) 201、009 201、010 400 含座位數量訊息、EVT-LIMIT-001 201、EVT-LIMIT-002 201。
- [x] 1.2 先寫 AC 表中 TP-LIMIT-006 的 WebApi 測試，在修正前執行，記錄兩種本文的實際狀態碼。驗證：預期為 500（Validator 的重複檢查直接存取元素屬性，見 design 決策 2）。若實際為 400（例如模型繫結已拒絕 null 元素），停下回報，因為 design 決策 2 規則 2 的理由不成立，需重新評估是否保留該規則。同時寫 TP-LIMIT-007 的 WebApi 測試並在修正前執行，記錄兩種本文的實際狀態碼與 `detail`（可能已由模型繫結回 400，屬邊界保護；若為 500 則確認 design 決策 2 `When` 段落的描述）。
  - 完成紀錄（2026-10-16）：TP-LIMIT-006 兩種本文皆 500（符合預期，規則 2 保留）；TP-LIMIT-007 `{}` 與 `{"selections":null}` 皆 400（模型繫結隱含必填擋下，邊界保護）。

## 2. Application：Validator

- [x] 2.1 新增 `PlaceOrderLimits`（design 決策 1），依 design 決策 2 修改 `PlaceOrderRequestValidator`，並新增 AC 表所列 `PlaceOrderRequestValidatorTests` 與 `OrderServiceTests`（TP-LIMIT-005）測試。另加 `Validate_WhenNegativeQuantityKeepsSumUnderLimit_IsInvalid`（`[100, -60]` 加總 40 仍須無效）與 `Validate_WhenSelectionsIsNull_IsInvalidWithoutThrowing`（`Selections` 為 null 時回傳無效、不拋 `NullReferenceException`；現行程式碼的重複檢查會拋，見 design 決策 2 規則 1～4 的 `When` 條件段落）。另加兩個規則互斥的組合測試：`Validate_WhenSelectionCountIs51WithNullItem_ReturnsOnlyCountError`（`Errors` 恰為 1 則筆數訊息）、`Validate_WhenNullItemAndQuantityIsIntMax_ReturnsOnlyNullItemError`（`Errors` 恰為 1 則 null 元素訊息，不拋例外、不含總張數訊息）。驗證：Application 測試全部通過；1.1 的 TP-LIMIT-001／003／004／008(b)／009 與 1.2 的 TP-LIMIT-006／007 轉為通過，TP-LIMIT-002／008(a)／010 維持通過。
- [x] 2.2 依 design 決策 3 修改 `CreateEventRequestValidator`，新增 AC 表所列 `CreateEventHandlerTests` 與 EVT-LIMIT-003 測試。驗證：Application 測試全部通過；1.1 的 EVT-LIMIT-001 轉為通過，EVT-LIMIT-002／003 通過。

## 3. 票種批次查詢

- [x] 3.1 依 design 決策 4，`ITicketTypeRepository.GetByIdAsync` 改為 `GetByIdsAsync`，更新 `TicketTypeRepository`（保留 no-tracking 與註解理由）與 `OrderService`；同步更新 7 個測試實作，每個都移除 `GetByIdAsync`、改實作 `GetByIdsAsync`，批次版行為比照原本單筆版：
  - `FakeTicketTypeRepository`（Application）：回傳 `Data` 中 Id 在輸入清單內的項目，加 `GetByIdsCallCount`（每次呼叫 +1，不論清單長度）。
  - `AlwaysThrowingTicketTypeRepository`（Infrastructure `QueryCacheTtlSafetyNetTests`）：拋與原本相同的 `InvalidOperationException("MUST NOT query the database on a cache hit.")`。
  - `AlwaysEmptyTicketTypeRepository`（Infrastructure `QueryCacheTicketTypeInvalidationOrderingTests`）：回傳空清單（對應原本回傳 null）。
  - `CountingTicketTypeRepository`、`SlowTicketTypeRepository`（Infrastructure `QueryCacheTtlSafetyNetTests`）、`CountingTicketTypeRepository`（WebApi `TestSupport`）、`MissingTicketTypeRepository`（WebApi `OrganizerScopingFaultInjectionWebApplicationFactory`）：直接轉呼叫 `inner.GetByIdsAsync`，不計數、不過濾、不延遲。這 4 個原本對 `GetByIdAsync` 就是純轉呼叫，計數、延遲、缺漏模擬都只作用在 `GetByEventIdAsync`，改版後維持不變。

  另更新 `RepositoryCrudRoundTripTests` 的呼叫。新增決策 4 段落所列 Application 測試。驗證：4 個測試專案編譯通過（介面移除 `GetByIdAsync` 後，漏改的實作與呼叫端會編譯失敗）；Application 測試全部通過。
  - 完成紀錄（2026-10-16）：`ProjectC.slnx` 建置通過（無新增警告；`PlaceOrderCoreAsync` 的 CA1502 為既有警告，分支數未增加）；Application 527 項全部通過。
- [x] 3.2 新增決策 4 段落所列 Infrastructure 測試。驗證：把 `AsNoTracking()` 暫時拿掉時 no-tracking 測試失敗，恢復後通過（記錄於完成紀錄）。
  - 完成紀錄（2026-10-16）：新增 `Infrastructure.Tests/Tickets/TicketTypeRepositoryTests.cs` 3 項測試。暫時拿掉 `AsNoTracking()` 後 `GetByIdsAsync_WhenTicketTypesExist_ReturnsUntrackedEntities` 失敗（其餘 2 項通過），恢復後 3 項全過。

## 4. 文件與審查

- [x] 4.1 執行 4 個測試專案完整測試。驗證：全部通過；若只有已知的 WSL2 時鐘 flake（`QueryCacheTtlSafetyNetTests`）失敗，單獨重跑該測試通過並記錄。
  - 完成紀錄（2026-10-16）：`dotnet test ProjectC.slnx` 全部通過，無 flake：Domain 151、Application 527、LoadTest.Seeder 38、Infrastructure 234、WebApi 541。
- [x] 4.2 更新 `docs/project-scope.md` §8 待辦 ②：標註 2026-10-16 重新啟動並由本 change 處理。驗證：§8 不再把「下單選購筆數無上限、票種逐筆查詢」列為未處理。
- [x] 4.3 套用 `.claude/skills/hardener/SKILL.md` 檢查清單於 `OrderService` 與兩個 Validator 的變更，再呼叫 strict-reviewer 與 adversarial 審查直到同一輪皆 PASS。
  - 完成紀錄（2026-10-16）：hardener 檢查清單無需修改（輸入驗證在 `PlaceOrderRequestValidator`、借連線前執行；批次查詢 Id 數受 50 筆上限約束、無 N+1；存在性查詢不加鎖，鎖定仍由 `GetForUpdateAsync` 負責；`CancellationToken` 有傳遞；無新增 log 點）。第 1 輪 strict-reviewer PASS（建議更新 `OrderService` 限購加總處「validator 沒有上限」的過時註解，已改寫，僅註解）、adversarial PASS（non-blocking：buyer-web-ui／admin-web-ui 主 spec 未提系統上限 50，屬 design Non-Goals「前端不改」的文字落差）。
