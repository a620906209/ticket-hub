# Tasks

> 對應 spec：`list-pagination`（新）、`buyer-order-query`（MODIFIED）、`order-administration`（MODIFIED）、`buyer-web-ui`（MODIFIED）、`admin-web-ui`（ADDED）。
> 既有 Scenario 覆蓋：MODIFIED 時原樣複製、語意未變的 Scenario——ORD-LIST-002/003/005/007（Organizer 範圍、資料不一致）、BOQ-LIST-002、BW-MYORDER-DETAIL-001/002、TICKET-ID-001、QR-001、NOTFOUND-001、FORBIDDEN-001、INVALID-ID-001、DISPLAY-001/002——由 3.1／4.1／6.2 更新後的既有測試覆蓋（改為斷言分頁 envelope）；ORD-LIST-006／008（後台排序）改由 2.1 的 Repository 整合測試覆蓋，原 `GetOrdersHandlerTests` 的 `HandleAsync_SortsByCreatedAtDescending`、`HandleAsync_WhenCreatedAtTies_SortsByIdAscendingRegardlessOfRepositoryOrder` 因排序移到資料庫端而刪除（3.1），各 task 的 RED 證據須逐一列出受影響的既有測試名稱；LP-VALID-004（`page=abc`／`pageSize=abc`、超出 int 範圍）由 4.1 的 HTTP 層測試覆蓋。
> 所有後端命令在容器內執行：`docker compose exec -T api dotnet test <專案> --filter "..."`；前端：`docker compose exec -T web npm run test -- <檔案>`。
> 每個 task 的「完成紀錄」於實作時依 CLAUDE.md「證據貼原文」填寫（RED：命令、失敗測試名、Expected／Actual 原文、需在修改正式程式碼前；GREEN：命令與摘要行；重構後重跑或 N/A）。核心列表查詢的 task（2.1、3.1）另預留「還原驗證」紀錄。

## 1. 共用分頁型別與驗證（Application／Domain）

- [x] 1.1 在 `tests/ProjectC.Application.Tests/Common/` 新增 `PageRequestValidatorTests` 與 `PagedResultDtoTests`：先寫測試。涵蓋 LP-VALID-001/002/003/005/006/007（`pageSize` 101 失敗、100 通過；`page` 10001 與 `int.MaxValue` 失敗、10000 通過；`page` 0／-1、`pageSize` 0 失敗；兩欄位同時越界時 `errors` 同時含 `page` 與 `pageSize`）、LP-PARAM-001（`PageRequest.DefaultPage == 1`、`DefaultPageSize == 20`、`MaxPageSize == 100`、`MaxPage == 10000`）、`PagedResultDto` 保存 `Items/Page/PageSize/TotalCount`。預期 RED：以反射或最小空殼使測試可編譯後，驗證器對越界值回報「通過」或型別缺漏的斷言失敗（不得以編譯失敗當 RED；先新增空殼 `PageRequest`／`PageRequestValidator`／`PagedResultDto` 使其可編譯，再執行）。再實作 `Application/Common/Paging/PageRequest`、`PageRequestValidator`、`PagedResultDto<T>` 與 Domain `OrderPage` 使 GREEN。
  - RED 紀錄：先建立空殼（`PageRequest` 常數皆為 0、`PageRequestValidator` 無規則、`PagedResultDto`、`OrderPage`）使測試可編譯，於正式程式碼實作前執行 `docker compose exec -T api dotnet test tests/ProjectC.Application.Tests --filter "FullyQualifiedName~PageRequestValidatorTests|FullyQualifiedName~PagedResultDtoTests"`，輸出原文節錄：
    - `Failed ...PageRequestValidatorTests.Constants_AreTheDocumentedPagingConvention` — `Expected PageRequest.DefaultPage to be 1, but found 0 (difference of -1).`
    - `Failed ...Validate_WhenPageSizeExceedsMax_FailsOnPageSize` — `Expected result.IsValid to be False, but found True.`
    - `Failed ...Validate_WhenPageExceedsMax_FailsOnPage(page: 10001)` 與 `(page: 2147483647)` — `Expected result.IsValid to be False, but found True.`
    - `Failed ...Validate_WhenBelowMinimum_FailsOnThatField(page: 0, pageSize: 20, expectedField: "Page")`、`(page: -1 ...)`、`(page: 1, pageSize: 0, expectedField: "PageSize")` — 皆 `Expected result.IsValid to be False, but found True.`
    - 摘要行：`Failed!  - Failed:     8, Passed:     4, Skipped:     0, Total:    12, Duration: 148 ms - ProjectC.Application.Tests.dll (net10.0)`（失敗原因為驗證器未攔截越界值／常數為空殼，非編譯或環境錯誤）
  - GREEN 紀錄：實作 `PageRequest` 常數（1/20/100/10000）與 `PageRequestValidator`（`InclusiveBetween`）後同一命令：`Passed!  - Failed:     0, Passed:    12, Skipped:     0, Total:    12, Duration: 76 ms - ProjectC.Application.Tests.dll (net10.0)`
  - 重構後重跑：N/A（無重構）

## 2. Repository 分頁查詢（Domain／Infrastructure）

- [x] 2.1 在 `tests/ProjectC.Infrastructure.Tests/Orders/` 新增（或擴充 `OrderRepositoryOrganizerScopingTests`）Testcontainers 整合測試，測 `GetPageByBuyerIdAsync` 與 `GetPageByOrganizerIdAsync`：先寫測試。涵蓋 BOQ-LIST-PAGE-001／ORD-PAGE-001（`skip`／`take` 切頁、`TotalCount` 僅計入該買家或該 Organizer 名下活動的訂單、不含他人訂單）、BOQ-LIST-ORDER-001 與後台 ORD-LIST-006（兩個方法各自：`CreatedAtUtc` 各不相同且寫入順序打亂，結果新到舊——`GetPageByOrganizerIdAsync` 若誤用升冪必須失敗）、BOQ-LIST-ORDER-002／ORD-PAGE-002／ORD-LIST-008／LP-ORDER-001（建立時間相同時依 `Id` 升冪、逐頁合併後每筆恰好一次；ORD-LIST-008 在真實資料庫無法重現「兩次查詢回傳順序不同」，以此 tie 測試對應）、LP-RESP-003／004（skip 超過總數回空 `Items` 但 `TotalCount` 為實際總數；無資料回空與 0）、`Items` 已載入 `Items`（訂單明細）集合。預期 RED：先在 `IOrderRepository`／`OrderRepository` 加入方法簽章並以 `throw new NotImplementedException()` 或回空結果使其可編譯，測試因 `NotImplementedException`／Expected 筆數不符而失敗。再實作：`CountAsync` 加 `OrderByDescending(CreatedAtUtc).ThenBy(Id).Skip().Take().Include(Items)`，後台版沿用 `Events.Any(OrganizerId)` 範圍過濾；`AsNoTracking`。
  - RED 紀錄：在 `IOrderRepository`／`OrderRepository` 加入兩個方法（先回 `new OrderPage([], 0)`）後、實作查詢前執行 `docker compose exec -T api dotnet test tests/ProjectC.Infrastructure.Tests --filter "FullyQualifiedName~OrderRepositoryPagingTests"`，輸出原文節錄：
    - `GetPageByBuyerIdAsync_WhenInsertedOutOfOrder_ReturnsNewestFirst` — `Expected page.Items.Select(o => o.Id) to be equal to {{a2dad8b4-...}, ...}, but found empty collection.`
    - `GetPageByOrganizerIdAsync_WhenInsertedOutOfOrder_ReturnsNewestFirst` — 同上 `but found empty collection.`
    - `GetPageByBuyerIdAsync_WhenCreatedAtTies_OrdersByIdAscendingAndPagesWithoutOverlap`、`GetPageByOrganizerIdAsync_WhenCreatedAtTies_...` — `Expected collected to be equal to {...}, but found empty collection.`
    - `GetPageByBuyerIdAsync_WhenOtherBuyersHaveOrders_PagesAndCountsOnlyCallerOrders` — `Expected page.TotalCount to be 5, but found 0 (difference of -5).`
    - `GetPageByOrganizerIdAsync_WhenOtherOrganizerHasOrders_PagesAndCountsOnlyCallerOrganizerOrders` — `Expected page.TotalCount to be 5, but found 0 (difference of -5).`
    - `GetPageAsync_WhenSkipBeyondTotalOrNoData_ReturnsEmptyItemsWithActualTotal` — `Expected buyerBeyond.TotalCount to be 3, but found 0 (difference of -3).`
    - `GetPageAsync_LoadsOrderItemsForEachOrderOnThePage` — `Expected buyerPage.Items to contain 3 item(s), but found 0: {empty}.`
    - 摘要行：`Failed!  - Failed:     8, Passed:     0, Skipped:     0, Total:     8, Duration: 790 ms - ProjectC.Infrastructure.Tests.dll (net10.0)`
  - GREEN 紀錄：實作 `GetPageAsync`（`CountAsync` ＋ `OrderByDescending(CreatedAtUtc).ThenBy(Id).Skip().Take().Include(Items)`、`AsNoTracking`）後同一命令：`Passed!  - Failed:     0, Passed:     8, Skipped:     0, Total:     8, Duration: 863 ms - ProjectC.Infrastructure.Tests.dll (net10.0)`
  - 還原驗證紀錄：`restore` 不適用——`IOrderRepository`／`OrderRepository` 的新方法簽章被 stash 後測試專案無法編譯（非有效 RED，腳本會判為環境或編譯錯誤）。改用 `bash scripts/verify-test-evidence.sh mutate --file src/ProjectC.Infrastructure/Persistence/Repositories/OrderRepository.cs --from <原> --to <新> -- docker compose exec -T api dotnet test tests/ProjectC.Infrastructure.Tests --filter FullyQualifiedName~OrderRepositoryPagingTests`，一次一個改動，每次腳本結束後檔案已還原（`git diff --stat` 僅剩 25 行新增）：
    - `.OrderByDescending(o => o.CreatedAtUtc)` → `.OrderBy(o => o.CreatedAtUtc)`（原文輸出）：
      ```text
        Failed ProjectC.Infrastructure.Tests.Orders.OrderRepositoryPagingTests.GetPageByBuyerIdAsync_WhenInsertedOutOfOrder_ReturnsNewestFirst [661 ms]
        Error Message:
         Expected page.Items.Select(o => o.Id) to be equal to {{176bcaa4-0db7-4a62-8481-bd45a1201ef6}, {4c26eebe-e28c-4892-bccd-37bacdae4d6b}, {2881c4d9-1860-4bfa-9ab3-01eee9997209}, {e56fbd96-6c81-4faa-8963-279ba1a6d0d0}, {d6696c09-af41-4a55-8797-671ecad8e174}}, but {{d6696c09-af41-4a55-8797-671ecad8e174}, {e56fbd96-6c81-4faa-8963-279ba1a6d0d0}, {2881c4d9-1860-4bfa-9ab3-01eee9997209}, {4c26eebe-e28c-4892-bccd-37bacdae4d6b}, {176bcaa4-0db7-4a62-8481-bd45a1201ef6}} differs at index 0.
        Failed ProjectC.Infrastructure.Tests.Orders.OrderRepositoryPagingTests.GetPageByOrganizerIdAsync_WhenOtherOrganizerHasOrders_PagesAndCountsOnlyCallerOrganizerOrders [24 ms]
        Error Message:
         Expected page.Items.Select(o => o.Id) to be equal to {{54501f0f-4173-4682-9db9-27b3141fe432}, {948130a1-3241-45d5-8aac-8d3521e8b492}}, but {{54501f0f-4173-4682-9db9-27b3141fe432}, {d4e6c57a-ee09-45c9-9ca1-98b5a0b750bc}} differs at index 1.
        Failed ProjectC.Infrastructure.Tests.Orders.OrderRepositoryPagingTests.GetPageByBuyerIdAsync_WhenOtherBuyersHaveOrders_PagesAndCountsOnlyCallerOrders [21 ms]
        Error Message:
         Expected page.Items.Select(o => o.Id) to be equal to {{095ad0ea-1169-42a9-bc91-f0e87b61a9cb}, {d73af722-abe0-4df8-902c-7364eaa08bb7}}, but {{095ad0ea-1169-42a9-bc91-f0e87b61a9cb}, {0c3ebbd8-dc6e-4f92-9839-f3c79b444f3d}} differs at index 1.
        Failed ProjectC.Infrastructure.Tests.Orders.OrderRepositoryPagingTests.GetPageByOrganizerIdAsync_WhenInsertedOutOfOrder_ReturnsNewestFirst [34 ms]
        Error Message:
         Expected page.Items.Select(o => o.Id) to be equal to {{a1f74974-e516-4215-bc00-ecb8b4df0e75}, {40d1d96b-a993-4284-ae85-85952fe9bcf2}, {02325b17-6caa-42c0-9fb1-9332dd15cb84}, {3e762ca6-56b6-4c08-aae8-88fabe6ae53a}, {8331616c-168c-4aee-befe-0572376faf2e}}, but {{8331616c-168c-4aee-befe-0572376faf2e}, {3e762ca6-56b6-4c08-aae8-88fabe6ae53a}, {02325b17-6caa-42c0-9fb1-9332dd15cb84}, {40d1d96b-a993-4284-ae85-85952fe9bcf2}, {a1f74974-e516-4215-bc00-ecb8b4df0e75}} differs at index 0.
      Failed!  - Failed:     4, Passed:     4, Skipped:     0, Total:     8, Duration: 869 ms - ProjectC.Infrastructure.Tests.dll (net10.0)
      結果：測試如預期失敗
      ```
    - `.ThenBy(o => o.Id)` → `.ThenByDescending(o => o.Id)`（原文輸出）：
      ```text
        Failed ProjectC.Infrastructure.Tests.Orders.OrderRepositoryPagingTests.GetPageByOrganizerIdAsync_WhenCreatedAtTies_OrdersByIdAscendingAndPagesWithoutOverlap [81 ms]
        Error Message:
         Expected collected to be equal to {{043d956c-b1cb-43cc-b918-8c2810e0a878}, {90ae7e0b-9d0c-490b-b5e9-7f04e022965b}, {a4c2ecc4-0a86-41e7-91ce-fdf8af878485}, {cbe0a96e-fb0c-40d1-904e-1e850fec1007}, {e896d6de-4957-4084-ba37-1bb5adf677f2}}, but {{e896d6de-4957-4084-ba37-1bb5adf677f2}, {cbe0a96e-fb0c-40d1-904e-1e850fec1007}, {a4c2ecc4-0a86-41e7-91ce-fdf8af878485}, {90ae7e0b-9d0c-490b-b5e9-7f04e022965b}, {043d956c-b1cb-43cc-b918-8c2810e0a878}} differs at index 0.
        Failed ProjectC.Infrastructure.Tests.Orders.OrderRepositoryPagingTests.GetPageByBuyerIdAsync_WhenCreatedAtTies_OrdersByIdAscendingAndPagesWithoutOverlap [31 ms]
        Error Message:
         Expected collected to be equal to {{5bcd00c9-cc23-4d7a-8083-0791c49e8a8b}, {75fedaa6-7687-45a2-bd02-9fa1824fba1e}, {a878cfcf-2868-41b0-acb3-6b017106d996}, {c15ab190-4b06-4ded-b96d-be918dfd91ae}, {ff1382a4-c11a-4dec-ba45-07bf91a7d9e5}}, but {{ff1382a4-c11a-4dec-ba45-07bf91a7d9e5}, {c15ab190-4b06-4ded-b96d-be918dfd91ae}, {a878cfcf-2868-41b0-acb3-6b017106d996}, {75fedaa6-7687-45a2-bd02-9fa1824fba1e}, {5bcd00c9-cc23-4d7a-8083-0791c49e8a8b}} differs at index 0.
      Failed!  - Failed:     2, Passed:     6, Skipped:     0, Total:     8, Duration: 852 ms - ProjectC.Infrastructure.Tests.dll (net10.0)
      結果：測試如預期失敗
      ```
    - `.Skip(skip)` → `.Skip(0)`（原文輸出）：
      ```text
        Failed ProjectC.Infrastructure.Tests.Orders.OrderRepositoryPagingTests.GetPageByOrganizerIdAsync_WhenCreatedAtTies_OrdersByIdAscendingAndPagesWithoutOverlap [68 ms]
        Error Message:
         Expected collected to be equal to {{021191a9-524d-4888-be7a-f73f03a7ee6f}, {2048c975-195b-47c0-b165-341bdf2d8b32}, {5f96f121-25d8-4087-b3e8-7ba0e0dee9d4}, {72d7e2ac-774f-431c-9353-352d70e00708}, {c6c0f50d-9758-459a-8efe-872f7cae7161}}, but {{021191a9-524d-4888-be7a-f73f03a7ee6f}, {2048c975-195b-47c0-b165-341bdf2d8b32}, {021191a9-524d-4888-be7a-f73f03a7ee6f}, {2048c975-195b-47c0-b165-341bdf2d8b32}, {021191a9-524d-4888-be7a-f73f03a7ee6f}, {2048c975-195b-47c0-b165-341bdf2d8b32}} contains 1 item(s) too many.
        Failed ProjectC.Infrastructure.Tests.Orders.OrderRepositoryPagingTests.GetPageAsync_WhenSkipBeyondTotalOrNoData_ReturnsEmptyItemsWithActualTotal [27 ms]
        Error Message:
         Expected buyerBeyond.Items to be empty, but found at least one item {
        Failed ProjectC.Infrastructure.Tests.Orders.OrderRepositoryPagingTests.GetPageByOrganizerIdAsync_WhenOtherOrganizerHasOrders_PagesAndCountsOnlyCallerOrganizerOrders [25 ms]
        Error Message:
         Expected page.Items.Select(o => o.Id) to be equal to {{39564697-daf7-4996-9bce-c45bddfa12d9}, {9b820edf-bdf6-4ca2-b489-b1b6291979e8}}, but {{1310ea65-872f-4829-85a6-4bc08a782471}, {14adb134-c0d0-4291-be86-0ae912a9fdb8}} differs at index 0.
        Failed ProjectC.Infrastructure.Tests.Orders.OrderRepositoryPagingTests.GetPageByBuyerIdAsync_WhenCreatedAtTies_OrdersByIdAscendingAndPagesWithoutOverlap [17 ms]
        Error Message:
         Expected collected to be equal to {{127b486f-18bc-4fec-986b-538eb7617b58}, {67756b8f-31c9-49b4-87a6-3fa730292355}, {88caa7de-587f-4d75-8906-969b700a2e32}, {9e352111-af94-433c-a63f-48c05b7b48ea}, {c81d3f64-157c-4d1b-ba30-e33c44642a1e}}, but {{127b486f-18bc-4fec-986b-538eb7617b58}, {67756b8f-31c9-49b4-87a6-3fa730292355}, {127b486f-18bc-4fec-986b-538eb7617b58}, {67756b8f-31c9-49b4-87a6-3fa730292355}, {127b486f-18bc-4fec-986b-538eb7617b58}, {67756b8f-31c9-49b4-87a6-3fa730292355}} contains 1 item(s) too many.
        Failed ProjectC.Infrastructure.Tests.Orders.OrderRepositoryPagingTests.GetPageByBuyerIdAsync_WhenOtherBuyersHaveOrders_PagesAndCountsOnlyCallerOrders [17 ms]
        Error Message:
         Expected page.Items.Select(o => o.Id) to be equal to {{4fd54295-4f30-4b81-984a-9873d3569e3b}, {9da6781b-6c71-47b6-84b8-7bb0561b471a}}, but {{4d86ce59-f81b-4678-841c-97994cdeb7ae}, {035cba85-7891-48f8-a6f6-926338607e69}} differs at index 0.
      Failed!  - Failed:     5, Passed:     3, Skipped:     0, Total:     8, Duration: 864 ms - ProjectC.Infrastructure.Tests.dll (net10.0)
      結果：測試如預期失敗
      ```
    - `e.OrganizerId == organizerId` → `e.OrganizerId != Guid.Empty`（原文輸出）：
      ```text
        Failed ProjectC.Infrastructure.Tests.Orders.OrderRepositoryPagingTests.GetPageByOrganizerIdAsync_WhenCreatedAtTies_OrdersByIdAscendingAndPagesWithoutOverlap [80 ms]
        Error Message:
         Expected collected to be equal to {{2890790a-7ba0-4efb-8aea-9d508f56a14d}, {67653331-e84f-43a2-a462-4db9f8f725d4}, {ceb1e5fe-6da9-4bd5-8f26-4acb7d457871}, {d66b740e-0946-4b6c-9b03-5ee2d461390f}, {e807b533-3896-4f8f-a42a-05a9aec47a0b}}, but {{69d34864-9daf-42c8-82f9-3999113a4936}, {13e5ec2f-7a80-46ea-809d-6959a82d27e1}, {33572ebb-e0b2-44ef-923d-b0762e746baf}, {e08ec27d-a66c-4817-ad8a-6dc3604b00d4}, {2890790a-7ba0-4efb-8aea-9d508f56a14d}, {67653331-e84f-43a2-a462-4db9f8f725d4}} contains 1 item(s) too many.
        Failed ProjectC.Infrastructure.Tests.Orders.OrderRepositoryPagingTests.GetPageAsync_LoadsOrderItemsForEachOrderOnThePage [21 ms]
        Error Message:
         System.Collections.Generic.KeyNotFoundException : The given key '69d34864-9daf-42c8-82f9-3999113a4936' was not present in the dictionary.
        Failed ProjectC.Infrastructure.Tests.Orders.OrderRepositoryPagingTests.GetPageAsync_WhenSkipBeyondTotalOrNoData_ReturnsEmptyItemsWithActualTotal [22 ms]
        Error Message:
         Expected organizerBeyond.TotalCount to be 3, but found 16 (difference of 13).
        Failed ProjectC.Infrastructure.Tests.Orders.OrderRepositoryPagingTests.GetPageByOrganizerIdAsync_WhenOtherOrganizerHasOrders_PagesAndCountsOnlyCallerOrganizerOrders [22 ms]
        Error Message:
         Expected page.TotalCount to be 5, but found 25 (difference of 20).
        Failed ProjectC.Infrastructure.Tests.Orders.OrderRepositoryPagingTests.GetPageByOrganizerIdAsync_WhenInsertedOutOfOrder_ReturnsNewestFirst [32 ms]
        Error Message:
         Expected page.Items.Select(o => o.Id) to be equal to {{7ad934ff-e3eb-4f9d-8e6f-bfd0bc7568a3}, {68c7a404-6110-434b-9000-f62122e50d29}, {33a7acd1-273a-47c4-b3c9-af89cd03555f}, {9972776d-aa39-4520-a14e-385458b5ed51}, {92898ea5-c12c-4971-8f17-0f313ca8dc39}}, but {{69d34864-9daf-42c8-82f9-3999113a4936}, {71e6b703-30c9-4513-baf7-87bc5794fbf8}, {7ad934ff-e3eb-4f9d-8e6f-bfd0bc7568a3}, {a75d262c-508e-4846-9196-01b35e9b0e97}, {13e5ec2f-7a80-46ea-809d-6959a82d27e1}, {2f112764-9205-4e91-8e6d-3a1c7d4f3cd4}, {68791cb8-65b8-4c0e-9879-548c8d6dc326}, {68c7a404-6110-434b-9000-f62122e50d29}, {6bb878e6-16a0-47a8-99e0-87b6b91534b8}, {0c7fcc61-8792-4d25-8d03-a7b33e3306af}} contains 5 item(s) too many.
      Failed!  - Failed:     5, Passed:     3, Skipped:     0, Total:     8, Duration: 919 ms - ProjectC.Infrastructure.Tests.dll (net10.0)
      結果：測試如預期失敗
      ```
  - 重構後重跑：N/A（無重構）
- [x] 2.2 更新測試替身以實作新介面方法：`FakeOrderRepository`（在記憶體用同一排序與切頁語意，並記錄收到的 `CancellationToken`）。同時更新 `OrganizerScopingFaultInjectionWebApplicationFactory` 內也實作 `IOrderRepository` 的 `UnresolvableOrganizerOrderRepository`（改包裝新方法）。此 task 無獨立行為，驗證為 `docker compose exec -T api dotnet build` 與 `docker compose exec -T api dotnet build tests/ProjectC.WebApi.Tests` 通過；TDD N/A（測試替身，由 3.1 的測試間接驗證）。完成紀錄：`FakeOrderRepository` 新增 `GetPageByBuyerIdAsync`／`GetPageByOrganizerIdAsync`（同一排序與切頁語意，記錄 token、skip、take、呼叫次數）；`UnresolvableOrganizerOrderRepository` 新增兩個包裝方法。`docker compose exec -T api dotnet build` → `0 Error(s)`；`docker compose exec -T api dotnet build tests/ProjectC.WebApi.Tests` → `0 Error(s)`。

## 3. Handler（Application）

- [x] 3.1 在 `tests/ProjectC.Application.Tests/Orders/GetMyOrders/GetMyOrdersHandlerTests` 與 `GetOrders/GetOrdersHandlerTests` 先改寫／新增測試：`HandleAsync(buyerId 或 organizerId, PageRequest, ct)` 回傳 `Result<PagedResultDto<...>>`。涵蓋 LP-VALID（越界回 `Validation` 且 `FieldErrors` 含 `page`／`pageSize`、Repository 未被呼叫）、LP-RESP-001～004（envelope 欄位、最後一頁不足、超過最後一頁回空 `items` 與實際 `totalCount`、無資料）、BOQ-LIST-PAGE-001／ORD-PAGE-001、BOQ-LIST-TITLE-001（每筆摘要的 `EventTitle` 正確）、BOQ-LIST-TITLE-002／ORD-PAGE-004（資料不一致只在包含該筆的頁面失敗，另一頁正常）、活動名稱／買家名稱只針對當頁 Id 批次查詢（斷言 `GetByIdsAsync` 收到的 Id 只含當頁）、`CancellationToken` 原樣傳遞給 Repository。預期 RED：沿用 1.1、2.2 的空殼使其可編譯；Handler 目前簽章不收 `PageRequest`，先把 Handler 簽章改為接收 `PageRequest` 並回傳舊內容（不驗證、不切頁）使測試編譯，RED 為 `Validation` 未回、`TotalCount`／`Items` 筆數不符、`GetByIdsAsync` 收到全部 Id。再實作：驗證 → Repository 分頁查詢 → 當頁批次查名稱與資料不一致檢查 → 回 `PagedResultDto`（排序已由 Repository 完成，移除 `GetOrdersHandler` 內的記憶體排序，並刪除 `GetOrdersHandlerTests` 中測記憶體排序的兩個測試，排序驗證改由 2.1 負責）。`GetMyOrdersHandlerTests` 內的包裝類別是 `IEventRepository` 的包裝，不需因本 change 修改。
  - RED 紀錄：先把兩個 Handler 改為空殼（新簽章與 `IValidator<PageRequest>` 建構參數，但仍呼叫舊 Repository 方法、不驗證），於正式邏輯實作前執行 `docker compose exec -T api dotnet test tests/ProjectC.Application.Tests --filter "FullyQualifiedName~GetMyOrdersHandlerTests|FullyQualifiedName~GetOrdersHandlerTests"`，輸出原文節錄：
    - 兩個 Handler 的 `HandleAsync_WhenPageRequestOutOfRange_ReturnsValidationErrorWithoutQueryingRepository`（page 0／10001、pageSize 101、兩者同時越界，共 8 例）— `Expected result.IsSuccess to be False, but found True.`
    - `Failed GetMyOrders...HandleAsync_WithSecondPage_ReturnsThatPageWithEnvelopeAndQueriesOnlyPageEventIds` — `Expected page.Items.Select(s => s.Id) to be equal to {{2c41cda8-...}, {86e524c2-...}}, but {{3b5b94f4-...}, ... 共 5 筆} contains 3 item(s) too many.`
    - `Failed GetOrders...HandleAsync_WithSecondPage_ReturnsThatPageWithEnvelopeAndQueriesOnlyPageIds` — `Expected page.Items.Select(o => o.Id) to be equal to {{3392cce2-...}, {02aaf3c6-...}}, but {... 共 5 筆} contains 3 item(s) too many.`
    - `Failed ...HandleAsync_WhenPageBeyondLastPage_ReturnsEmptyItemsWithActualTotal`（兩個 Handler）— `Expected result.Value!.Items to be empty, but found at least one item {`
    - `Failed GetMyOrders...HandleAsync_WithLastPageNotFull_ReturnsRemainingItems` — `Expected page.Items to contain 1 item(s), but found 5:`
    - `Failed ...HandleAsync_WithCallerToken_ForwardsTokenToEveryQuery`（兩個 Handler）— `Expected _orderRepository.LastGetPageByBuyerIdToken to be System.Threading.CancellationToken`（Organizer 版為 `LastGetPageByOrganizerIdToken`）
    - `Failed ...HandleAsync_WhenOrphanOrderOnOtherPage_RequestedPageStillSucceeds`／`...WhenOrphanBuyerOnOtherPage_...`（舊邏輯載入全部而撞到他頁孤兒資料）
    - 摘要行：`Failed!  - Failed:    17, Passed:    14, Skipped:     0, Total:    31, Duration: 161 ms - ProjectC.Application.Tests.dll (net10.0)`（失敗原因為未驗證、未切頁、總數與 Token 轉送不符，非編譯或環境錯誤）
  - 註記：上列 RED 為當時的節錄（GUID 以 `...` 省略）；空殼版 Handler 已不存在而無法重現原 RED，故以下列 `mutate` 的逐字輸出補強每個失敗測試的名稱與訊息。
  - GREEN 紀錄：實作 Handler（驗證 → `GetPageBy…IdAsync(skip=(Page-1)*PageSize, take=PageSize)` → 空頁提早回傳 → 只對當頁批次查詢與檢查 → `PagedResultDto(…, TotalCount)`；移除記憶體排序）後同一命令：`Passed!  - Failed:     0, Passed:    31, Skipped:     0, Total:    31, Duration: 124 ms - ProjectC.Application.Tests.dll (net10.0)`
  - 還原驗證紀錄：`restore` 同 2.1 不適用（Handler 新簽章被 stash 後測試無法編譯）。改用 `bash scripts/verify-test-evidence.sh mutate --file <Handler> --from <原> --to <新> -- docker compose exec -T api dotnet test tests/ProjectC.Application.Tests --filter "FullyQualifiedName~GetMyOrdersHandlerTests|FullyQualifiedName~GetOrdersHandlerTests"`，一次一個改動，每次皆 `結果：測試如預期失敗`：
    - `(pageRequest.Page - 1) * pageRequest.PageSize` → `0`（原文輸出）：
      ```text
        Failed ProjectC.Application.Tests.Orders.GetMyOrders.GetMyOrdersHandlerTests.HandleAsync_WithSecondPage_ReturnsThatPageWithEnvelopeAndQueriesOnlyPageEventIds [67 ms]
        Error Message:
         Expected page.Items.Select(s => s.Id) to be equal to {{a521af90-7736-4cbb-a66c-2849a7f69449}, {eb709417-fc56-4fae-b4ae-d22b7912f8d9}}, but {{917162f1-0203-494e-b8bd-a44cc66291c6}, {b25dfbaf-fd81-43ea-9a7b-d631b7fc369c}} differs at index 0.
        Failed ProjectC.Application.Tests.Orders.GetMyOrders.GetMyOrdersHandlerTests.HandleAsync_WhenOrphanOrderOnOtherPage_RequestedPageStillSucceeds [5 ms]
        Error Message:
         Expected a <System.InvalidOperationException> to be thrown, but no exception was thrown.
        Failed ProjectC.Application.Tests.Orders.GetMyOrders.GetMyOrdersHandlerTests.HandleAsync_WithLastPageNotFull_ReturnsRemainingItems [8 ms]
        Error Message:
         Expected page.Items to contain 1 item(s), but found 2: {
        Failed ProjectC.Application.Tests.Orders.GetMyOrders.GetMyOrdersHandlerTests.HandleAsync_WhenPageBeyondLastPage_ReturnsEmptyItemsWithActualTotal [4 ms]
        Error Message:
         Expected result.Value!.Items to be empty, but found at least one item {
      Failed!  - Failed:     4, Passed:    27, Skipped:     0, Total:    31, Duration: 143 ms - ProjectC.Application.Tests.dll (net10.0)
      結果：測試如預期失敗
      ```
    - `(pageRequest.Page - 1) * pageRequest.PageSize` → `0`（原文輸出）：
      ```text
        Failed ProjectC.Application.Tests.Orders.GetOrders.GetOrdersHandlerTests.HandleAsync_WhenOrphanBuyerOnOtherPage_RequestedPageStillSucceeds [73 ms]
        Error Message:
         Expected a <System.InvalidOperationException> to be thrown, but no exception was thrown.
        Failed ProjectC.Application.Tests.Orders.GetOrders.GetOrdersHandlerTests.HandleAsync_WithSecondPage_ReturnsThatPageWithEnvelopeAndQueriesOnlyPageIds [8 ms]
        Error Message:
         Expected page.Items.Select(o => o.Id) to be equal to {{6f8c034a-6696-4903-90e8-7d71687ed2b9}, {6a9d4ec4-0090-41e2-bb7a-f42bf59d2931}}, but {{a051891f-7333-467f-8fe0-583f7599ba00}, {96d48834-46d0-4a45-bb6b-1cfa982a4dd3}} differs at index 0.
        Failed ProjectC.Application.Tests.Orders.GetOrders.GetOrdersHandlerTests.HandleAsync_WhenPageBeyondLastPage_ReturnsEmptyItemsWithActualTotal [6 ms]
        Error Message:
         Expected result.Value!.Items to be empty, but found at least one item {
      Failed!  - Failed:     3, Passed:    28, Skipped:     0, Total:    31, Duration: 139 ms - ProjectC.Application.Tests.dll (net10.0)
      結果：測試如預期失敗
      ```
    - `(items, pageRequest.Page, pageRequest.PageSize, orderPage.TotalCount)` → `(items, pageRequest.Page, pageRequest.PageSize, items.Count)`（原文輸出）：
      ```text
        Failed ProjectC.Application.Tests.Orders.GetMyOrders.GetMyOrdersHandlerTests.HandleAsync_WithSecondPage_ReturnsThatPageWithEnvelopeAndQueriesOnlyPageEventIds [64 ms]
        Error Message:
         Expected page.TotalCount to be 5, but found 2.
        Failed ProjectC.Application.Tests.Orders.GetMyOrders.GetMyOrdersHandlerTests.HandleAsync_WithLastPageNotFull_ReturnsRemainingItems [3 ms]
        Error Message:
         Expected page.TotalCount to be 5, but found 1.
      Failed!  - Failed:     2, Passed:    29, Skipped:     0, Total:    31, Duration: 137 ms - ProjectC.Application.Tests.dll (net10.0)
      結果：測試如預期失敗
      ```
    - `(items, pageRequest.Page, pageRequest.PageSize, orderPage.TotalCount)` → `(items, pageRequest.Page, pageRequest.PageSize, items.Count)`（原文輸出）：
      ```text
        Failed ProjectC.Application.Tests.Orders.GetOrders.GetOrdersHandlerTests.HandleAsync_WithSecondPage_ReturnsThatPageWithEnvelopeAndQueriesOnlyPageIds [27 ms]
        Error Message:
         Expected page.TotalCount to be 5, but found 2.
      Failed!  - Failed:     1, Passed:    30, Skipped:     0, Total:    31, Duration: 130 ms - ProjectC.Application.Tests.dll (net10.0)
      結果：測試如預期失敗
      ```
    - `if (!validation.IsValid)` → `if (false)`（原文輸出）：
      ```text
        Failed ProjectC.Application.Tests.Orders.GetMyOrders.GetMyOrdersHandlerTests.HandleAsync_WhenPageRequestOutOfRange_ReturnsValidationErrorWithoutQueryingRepository(page: 10001, pageSize: 20, expectedFields: ["Page"]) [27 ms]
        Error Message:
         Expected result.IsSuccess to be False, but found True.
        Failed ProjectC.Application.Tests.Orders.GetMyOrders.GetMyOrdersHandlerTests.HandleAsync_WhenPageRequestOutOfRange_ReturnsValidationErrorWithoutQueryingRepository(page: 1, pageSize: 101, expectedFields: ["PageSize"]) [3 ms]
        Error Message:
         Expected result.IsSuccess to be False, but found True.
        Failed ProjectC.Application.Tests.Orders.GetMyOrders.GetMyOrdersHandlerTests.HandleAsync_WhenPageRequestOutOfRange_ReturnsValidationErrorWithoutQueryingRepository(page: 0, pageSize: 20, expectedFields: ["Page"]) [2 ms]
        Error Message:
         Expected result.IsSuccess to be False, but found True.
        Failed ProjectC.Application.Tests.Orders.GetMyOrders.GetMyOrdersHandlerTests.HandleAsync_WhenPageRequestOutOfRange_ReturnsValidationErrorWithoutQueryingRepository(page: 0, pageSize: 101, expectedFields: ["Page", "PageSize"]) [3 ms]
        Error Message:
         Expected result.IsSuccess to be False, but found True.
      Failed!  - Failed:     4, Passed:    27, Skipped:     0, Total:    31, Duration: 140 ms - ProjectC.Application.Tests.dll (net10.0)
      結果：測試如預期失敗
      ```
    - `if (!validation.IsValid)` → `if (false)`（原文輸出）：
      ```text
        Failed ProjectC.Application.Tests.Orders.GetOrders.GetOrdersHandlerTests.HandleAsync_WhenPageRequestOutOfRange_ReturnsValidationErrorWithoutQueryingRepository(page: 0, pageSize: 20, expectedFields: ["Page"]) [26 ms]
        Error Message:
         Expected result.IsSuccess to be False, but found True.
        Failed ProjectC.Application.Tests.Orders.GetOrders.GetOrdersHandlerTests.HandleAsync_WhenPageRequestOutOfRange_ReturnsValidationErrorWithoutQueryingRepository(page: 1, pageSize: 101, expectedFields: ["PageSize"]) [4 ms]
        Error Message:
         Expected result.IsSuccess to be False, but found True.
        Failed ProjectC.Application.Tests.Orders.GetOrders.GetOrdersHandlerTests.HandleAsync_WhenPageRequestOutOfRange_ReturnsValidationErrorWithoutQueryingRepository(page: 10001, pageSize: 20, expectedFields: ["Page"]) [2 ms]
        Error Message:
         Expected result.IsSuccess to be False, but found True.
        Failed ProjectC.Application.Tests.Orders.GetOrders.GetOrdersHandlerTests.HandleAsync_WhenPageRequestOutOfRange_ReturnsValidationErrorWithoutQueryingRepository(page: 0, pageSize: 101, expectedFields: ["Page", "PageSize"]) [2 ms]
        Error Message:
         Expected result.IsSuccess to be False, but found True.
      Failed!  - Failed:     4, Passed:    27, Skipped:     0, Total:    31, Duration: 125 ms - ProjectC.Application.Tests.dll (net10.0)
      結果：測試如預期失敗
      ```
    - 腳本自動還原檔案，`git diff --stat` 僅剩預期的兩個 Handler 修改。
  - 重構後重跑：N/A（無重構）

## 4. Controller 與 WebApi 整合行為

- [x] 4.1 在 `tests/ProjectC.WebApi.Tests/Orders/OrdersControllerTests` 與 `Admin/AdminOrdersControllerTests` 先改寫／新增測試：涵蓋 LP-PARAM-001／002（未帶參數用預設值、只帶一個）、LP-VALID-001～007 的 HTTP 層（含 `page=2147483647` 回 400 而非 500；400、`title` 為 `Validation`、`errors` 鍵為小寫 `page`／`pageSize`、`page=abc` 走模型繫結失敗路徑同格式）、LP-RESP-001～004、BOQ-LIST-001／002／ORD-LIST-001 的 envelope 形狀（`items`／`page`／`pageSize`／`totalCount`）與範圍不外洩（買家看不到他人訂單、Organizer A 看不到 B）、ORD-PAGE-002、既有 401／403 行為不變。同步調整 `OrganizerScopingDataCorruptionTests`（ORD-PAGE-004）與 `OrganizerScopingFaultInjectionWebApplicationFactory`（工廠本體，替身類別已於 2.2 處理），以及 `OrderDisplayQueryCountTests` 的列表測試：ORD-LIST-004（後台，當頁 1 筆 vs 3 筆，每張表查詢次數相同）與 BOQ-LIST-TITLE-003（買家，同樣比較 1 筆 vs 3 筆不同活動，`Events` 表查詢次數相同且為 1 次、`Orders` 表為固定次數；若逐筆查活動則此測試必須失敗）。既有測試的絕對次數錨點會改變（後台現為 `Orders`＝1、`Events`＝2，加入帶 `Events.Any` 範圍過濾的 COUNT 查詢後預期變為 `Orders`＝2、`Events`＝3；買家 `Orders` 1→2），須先重新實測並寫入新的絕對值，不可只保留「兩次相等」的比較，否則斷言可空洞通過。預期 RED：Controller 尚未接收分頁參數、仍回裸陣列，`items`／`totalCount` 欄位不存在、400 未回。再實作 `OrdersController.GetMyOrders`、`AdminOrdersController.GetOrders`（`[FromQuery] int page = PageRequest.DefaultPage, [FromQuery] int pageSize = PageRequest.DefaultPageSize` → `result.ToActionResult(Ok)`）。
  - RED 紀錄：先改寫／新增測試（新增 `AdminOrdersPagingTests`、`MyOrdersPagingTests`、`TestSupport/PaginationTestSupport`；調整 `AdminOrdersControllerTests`、`OrdersControllerTests`、`OrderDisplayQueryCountTests`、`OrganizerScopingDataCorruptionTests`），Controller 先放僅能編譯的空殼（以預設 `PageRequest` 呼叫 Handler、不綁定 `page`／`pageSize`）。於實作 `[FromQuery]` 綁定前執行 `docker compose exec -T api dotnet test tests/ProjectC.WebApi.Tests --no-build --filter "FullyQualifiedName~AdminOrdersPagingTests|FullyQualifiedName~MyOrdersPagingTests|FullyQualifiedName~OrganizerScopingDataCorruptionTests|FullyQualifiedName~AdminOrdersControllerTests|FullyQualifiedName~OrderDisplayQueryCountTests|FullyQualifiedName~OrdersControllerTests"`，輸出原文節錄：
    - 兩個端點的 `GetOrders／GetMyOrders_WithInvalidPagingParameters_Returns400ValidationWithFieldErrors`（`?page=0`、`-1`、`10001`、`2147483647`、`99999999999`、`abc`、`?pageSize=0`、`101`、`abc`、`?page=0&pageSize=101`，共 20 例）— `Expected response.StatusCode to be HttpStatusCode.BadRequest {value: 400}, but found HttpStatusCode.OK {value: 200}.`
    - `Failed ...AdminOrdersPagingTests.GetOrders_WithOnlyOneParameter_UsesDefaultForTheOther`、`...MyOrdersPagingTests.GetMyOrders_WithOnlyOneParameter_UsesDefaultForTheOther` — `Expected pageSizeOnly.PageSize to be 5, but found 20 (difference of 15).`
    - `Failed ...AdminOrdersPagingTests.GetOrders_WithPageSizeTwoOverThreeOrders_SplitsIntoPagesAndKeepsTotalCount`、`...MyOrdersPagingTests.GetMyOrders_WithPageSizeTwoOverThreeOrders_SplitsIntoPagesAndKeepsTotalCount` — `Expected first!.Items to contain 2 item(s), but found 3: {`
    - `Failed ...OrganizerScopingDataCorruptionTests.GetAdminOrders_WhenOrphanOrderIsOnAnotherPage_OnlyThatPageReturns500`、`...GetMyOrders_WhenOrphanOrderIsOnAnotherPage_OnlyThatPageReturns500` — `Expected root[0] to equal HttpStatusCode.OK {value: 200} by value, but found HttpStatusCode.InternalServerError {value: 500}.`
    - 摘要行：`Failed!  - Failed:    26, Passed:    70, Skipped:     0, Total:    96, Duration: 33 s - ProjectC.WebApi.Tests.dll (net10.0)`（失敗原因為 Controller 未綁定分頁參數、無法回 400／切頁，非編譯或環境錯誤）。
    - 誠實註記：`OrderDisplayQueryCountTests` 的新絕對值（後台 `Orders`＝2、`Events`＝3；買家 `Orders`＝2）在空殼階段即通過，因為 Handler（3.1）已改用新 Repository 方法，查詢次數與 Controller 是否綁定參數無關；這兩個錨點的有效性由 2.1 的 Repository 變異驗證（COUNT＋當頁各一次）與其「1 筆 vs 3 筆相等」比較共同保證，不屬於本 task 的 RED。
  - GREEN 紀錄：`OrdersController.GetMyOrders`、`AdminOrdersController.GetOrders` 加入 `[FromQuery] int page = PageRequest.DefaultPage, [FromQuery] int pageSize = PageRequest.DefaultPageSize` 並以 `result.ToActionResult(Ok)` 回應後同一命令（去掉 `--no-build`）：`Passed!  - Failed:     0, Passed:    96, Skipped:     0, Total:    96, Duration: 29 s - ProjectC.WebApi.Tests.dll (net10.0)`。（中途一次執行出現 `Expected root[0] to be 3, but found 2.`，原因是前一輪 `mutate` 還原後 Handler 檔案 mtime 較舊、增量建置沿用變異版本的 dll；`touch` 兩個 Handler 後重建即通過。）
  - 重構後重跑：N/A（無重構）
- [x] 4.2 移除 `IOrderRepository.GetByBuyerIdAsync`／`GetByOrganizerIdAsync` 與其實作、測試替身中的對應成員。驗證：`rg "GetByBuyerIdAsync|GetByOrganizerIdAsync" src tests --glob '!**/obj/**'` 在訂單 Repository 相關檔案無結果（`IEventRepository.GetByOrganizerIdAsync` 為活動的，不得誤刪），`dotnet build` 通過。TDD N/A（刪除無呼叫端的程式碼，由編譯與既有測試驗證）。
  - 完成紀錄：已移除 `IOrderRepository`／`OrderRepository`／`FakeOrderRepository`（含 `LastGetByOrganizerIdToken`／`LastGetByBuyerIdToken`）／`UnresolvableOrganizerOrderRepository` 的舊成員；`OrderRepositoryOrganizerScopingTests` 的 ORD-LIST-001 兩個測試改用 `GetPageByOrganizerIdAsync(skip: 0, take: 100)` 並更名。
    - `rg -n "GetByBuyerIdAsync|GetByOrganizerIdAsync" src tests -g '*.cs' --glob '!**/obj/**'` 剩餘結果全部是 `IEventRepository.GetByOrganizerIdAsync` 及其包裝（`EventRepository`、`GetAdminEventsHandler`、各測試的 `IEventRepository` 轉接類別），訂單 Repository 相關檔案無結果。
    - `docker compose exec -T api dotnet build tests/ProjectC.{Application,Infrastructure,WebApi}.Tests`：三者皆 `Build succeeded.`
    - `dotnet test tests/ProjectC.Infrastructure.Tests --no-build --filter "FullyQualifiedName~Orders"`：`Passed!  - Failed:     0, Passed:    61, Skipped:     0, Total:    61, Duration: 2 s - ProjectC.Infrastructure.Tests.dll (net10.0)`
    - `dotnet test tests/ProjectC.Application.Tests --no-build`：`Passed!  - Failed:     0, Passed:   591, Skipped:     0, Total:   591, Duration: 1 s - ProjectC.Application.Tests.dll (net10.0)`
- [x] 4.3 確認沒有意外的 schema 變更：`docker compose exec -T api dotnet ef migrations has-pending-model-changes --project src/ProjectC.Infrastructure --startup-project src/ProjectC.WebApi` 應回報無待處理變更（本 change 不新增 migration）。TDD N/A（確認性檢查）。完成紀錄：`docker compose exec -T api dotnet ef migrations has-pending-model-changes --project src/ProjectC.Infrastructure --startup-project src/ProjectC.WebApi` 輸出最後一行：`No changes have been made to the model since the last migration.`（結束碼 0；輸出前段的 `HostAbortedException` 是 EF 工具取得 Host 後中止啟動的正常現象，非錯誤；`git status` 無任何 migration 檔變更）。

## 5. 文件

- [x] 5.1 更新 `docs/api-conventions.md`：§3「列表分頁」列改為本慣例（參數、預設、上限、越界 400、回應結構、確定性排序，並標註適用端點與 `list-pagination` 能力）；§4 加入新增分頁端點的更新時機；記錄 `venues`、`organizers/mine`、`admin/organizers` 目前不做分頁的理由（`venues` 為少量固定資料；`organizers/mine` 受限於單一會員的 Organizer 數；`admin/organizers` 為全平台主辦方清單，會隨申請增加，目前規模可接受並列為觀察項目，規模成長時另案分頁），`/api/admin/events`、`/api/events` 標為後續 change。`docs/api-conventions-todo.md` 第 6 項更新進度（本 change 範圍已完成的項目打勾，剩餘項目保留）。驗證：`rg "未發現統一的分頁" docs` 無結果；TDD N/A（純文件）。
  - 驗證（TDD N/A，純文件）：`rg "未發現統一的分頁" docs` 輸出為空（exit=1，無符合）；`git diff --stat docs`：`docs/api-conventions-todo.md | 8 +++++---`、`docs/api-conventions.md | 5 +++--`、`2 files changed, 8 insertions(+), 5 deletions(-)`。

## 6. 前端

- [x] 6.1 先寫 Vitest：`web/src/api/orders.test.ts`（新增或併入現有）與 `admin.test.ts`：`getMyOrders(page)`／`getAdminOrders(page)` 以 `page` 查詢字串請求並回傳 `PagedResult`。預期 RED：目前函式不收參數、請求網址不含 `page`。再實作 `web/src/types/apiResponses.ts` 的 `PagedResult<T>`、兩個 API 函式。
  - RED 紀錄：`docker compose exec -T web npx vitest run src/api/orders.test.ts src/api/admin.test.ts`，修改正式程式碼前。失敗原因為目前函式不帶 `page` 查詢字串（非編譯／環境錯誤）：
    - `orders.test.ts > [BW-MYORDER-PAGE-001] getMyOrders(2)…`：`AssertionError: expected '/api/orders' to be '/api/orders?page=2'`
    - `orders.test.ts > [BW-MYORDER-LIST-001] getMyOrders() 未指定頁碼時請求第 1 頁`：`expected '/api/orders' to be '/api/orders?page=1'`
    - `admin.test.ts > [AWU-ORDER-PAGE-001] getAdminOrders(3)…`：`expected '/api/admin/orders' to be '/api/admin/orders?page=3'`
    - `admin.test.ts > [AWU-ORDER-LIST-001] getAdminOrders() 未指定頁碼時請求第 1 頁`：`expected '/api/admin/orders' to be '/api/admin/orders?page=1'`
    - 摘要行：`Test Files  2 failed (2)` / `Tests  4 failed | 6 passed (10)`
  - GREEN 紀錄：實作 `PagedResult<T>`、`getMyOrders(page = 1)`、`getAdminOrders(page = 1)` 後同一命令：`✓ src/api/orders.test.ts (2 tests)`、`✓ src/api/admin.test.ts (8 tests)`；`Test Files  2 passed (2)` / `Tests  10 passed (10)`。重構：N/A。（頁面呼叫端尚未改，型別檢查於 6.2／6.3 的 build 驗證。）
- [x] 6.2 先寫 Vitest（`MyOrdersPage.test.ts`、`AdminOrderListPage.test.ts`）：BW-MYORDER-LIST-001（第 1 頁）、BW-MYORDER-PAGE-001／AWU-ORDER-PAGE-001（超過一頁顯示總筆數與頁碼、點第 2 頁以 `page=2` 重新請求並取代列表、載入中狀態）、BW-MYORDER-PAGE-002／AWU-ORDER-PAGE-002（不超過一頁不顯示分頁元件、空清單提示不顯示分頁）、BW-MYORDER-PAGE-003／AWU-ORDER-PAGE-003（重新整理停留在目前頁；空 `items` 且 `totalCount > 0` 時只重試一次改請求最後一頁，並同步分頁元件顯示該頁）、BW-MYORDER-PAGE-004／AWU-ORDER-PAGE-004（換頁請求失敗：頁碼退回前一頁、列表保留前一頁資料並顯示錯誤提示）、BW-MYORDER-PAGE-005／AWU-ORDER-PAGE-005（快速連續換頁時過期回應被丟棄）、既有狀態標籤與持有到期時間顯示不變。列表 API 的 mock 回傳形狀（陣列 → 分頁物件）只需更新這兩個測試檔；`orderConfirmationFlow.test.ts`、`BuyerLayout.test.ts`、`elementPlusOnDemand.test.ts` 不引用 `getMyOrders`／`getAdminOrders`，不需修改（實作時以 `rg "getMyOrders|getAdminOrders" web/src` 再核對）。預期 RED：頁面仍把回應當陣列使用，或不呼叫帶 `page` 的 API／不渲染 `el-pagination`。再實作兩個頁面（`currentPage` 為元件內 `ref`，`el-pagination` 的 `layout="total, prev, pager, next"`，請求序號丟棄過期回應）。
  - RED 紀錄：`docker compose exec -T web npx vitest run src/pages/buyer/MyOrdersPage.test.ts src/pages/admin/AdminOrderListPage.test.ts`，頁面修改前（API mock 已改為分頁物件、新增分頁測試）。失敗原因為頁面仍把回應當陣列／不渲染 `el-pagination`：
    - 每個失敗測試的名稱與首行訊息（逐字，`Tests  24 failed | 2 passed (26)`，AdminOrderListPage 12 筆、MyOrdersPage 12 筆；測試名稱中含 `>` 的部分在擷取時被截斷，以原樣呈現）：
      - AdminOrderListPage.test.ts > [AWU-ORDER-LIST-001] 訂單狀態以中文標籤顯示，Pending／Paid 分別為警示色／成功色 :: AssertionError: expected [] to have a length of 2 but got +0
      - AdminOrderListPage.test.ts > [AWU-ORDER-LIST-001] 買家顯示名稱與活動名稱含 HTML 標籤時原樣以文字顯示，表格內不產生元素 :: AssertionError: expected '訂單管理重新整理' to contain '<img src=x onerror=alert(1)>'
      - AdminOrderListPage.test.ts > [AWU-ORDER-LIST-002] 顯示訂單編號短碼、活動、張數、總金額與建立時間，明細連結使用完整 Id :: AssertionError: expected [] to have a length of 1 but got +0
      - AdminOrderListPage.test.ts > [AWU-ORDER-HOLD-001] 持有到期時間只對 Pending 訂單顯示，其餘狀態顯示「—」 :: AssertionError: expected [] to have a length of 4 but got +0
      - AdminOrderListPage.test.ts > [AWU-ORDER-LIST-003] 依 API 回傳順序顯示，前端不重新排序 :: AssertionError: 表頭「訂單編號」應存在，實際表頭：: expected -1 to be greater than or equal to 0
      - AdminOrderListPage.test.ts > [AWU-ORDER-PAGE-001] 超過一頁時顯示總筆數與頁碼（無每頁筆數選擇器），點第 2 頁以 page=2 取代列表並在載入期間顯示載入中 :: AssertionError: expected last "vi.fn()" call to have been called with [ 1 ]
      - AdminOrderListPage.test.ts > [AWU-ORDER-PAGE-002] 筆數不超過一頁或沒有訂單時不顯示分頁元件 :: AssertionError: expected '' to contain 'Event 1-1'
      - AdminOrderListPage.test.ts > [AWU-ORDER-PAGE-003] 重新整理停留在目前頁，不回到第 1 頁 :: AssertionError: 應有頁碼 2: expected undefined to be defined
      - AdminOrderListPage.test.ts > 0 時只重試一次改請求最後一頁，並同步分頁元件頁碼 :: AssertionError: 應有頁碼 2: expected undefined to be defined
      - AdminOrderListPage.test.ts > [AWU-ORDER-PAGE-003] 最後一頁仍為空時不再重試（只重試一次） :: AssertionError: expected [ undefined ] to deeply equal [ 1, 3 ]
      - AdminOrderListPage.test.ts > [AWU-ORDER-PAGE-004] 換頁請求失敗時頁碼退回前一頁，列表保留前一頁資料並顯示錯誤提示 :: AssertionError: 應有頁碼 2: expected undefined to be defined
      - AdminOrderListPage.test.ts > [AWU-ORDER-PAGE-005] 快速連續換頁時丟棄過期回應：第 3 頁先到、第 2 頁後到，列表顯示第 3 頁 :: AssertionError: 應有頁碼 2: expected undefined to be defined
      - MyOrdersPage.test.ts > [BW-MYORDER-LIST-001 開啟我的訂單列表頁] 顯示中文狀態；Pending 訂單顯示保留時間，終態訂單不顯示保留時間 :: AssertionError: expected [] to deeply equal [ 'Event 1', 'Event 2', …(2) ]
      - MyOrdersPage.test.ts > [BW-STATUS-TAG-001] Pending／Paid／Cancelled 分別以警示色、成功色、中性色標籤顯示中文文字 :: AssertionError: expected [] to deeply equal [ '待付款', '已付款', '已取消' ]
      - MyOrdersPage.test.ts > [BW-MYORDER-DISPLAY-002] 未知狀態原樣顯示，其餘訂單正常顯示 :: AssertionError: expected '我的訂單重新整理目前沒有訂單' to contain 'Refunded'
      - MyOrdersPage.test.ts > 列表頁不提供確認付款或取消訂單操作 :: AssertionError: expected '我的訂單重新整理目前沒有訂單' to contain '待付款'
      - MyOrdersPage.test.ts > [BW-MYORDER-LIST-001] 活動名稱含 HTML 標籤時原樣以文字顯示，表格內不產生元素 :: AssertionError: expected '我的訂單重新整理目前沒有訂單' to contain '<img src=x onerror=alert(1)>'
      - MyOrdersPage.test.ts > [BW-MYORDER-PAGE-001] 超過一頁時顯示總筆數與頁碼（無每頁筆數選擇器），點第 2 頁以 page=2 取代列表並在載入期間顯示載入中 :: AssertionError: expected last "vi.fn()" call to have been called with [ 1 ]
      - MyOrdersPage.test.ts > [BW-MYORDER-PAGE-002] 筆數不超過一頁或沒有訂單時不顯示分頁元件 :: AssertionError: expected '' to contain 'Event 1-1'
      - MyOrdersPage.test.ts > [BW-MYORDER-PAGE-003] 重新整理停留在目前頁，不回到第 1 頁 :: AssertionError: 應有頁碼 2: expected undefined to be defined
      - MyOrdersPage.test.ts > 0 時只重試一次改請求最後一頁，並同步分頁元件頁碼 :: AssertionError: 應有頁碼 2: expected undefined to be defined
      - MyOrdersPage.test.ts > [BW-MYORDER-PAGE-003] 最後一頁仍為空時不再重試（只重試一次） :: AssertionError: expected [ undefined ] to deeply equal [ 1, 3 ]
      - MyOrdersPage.test.ts > [BW-MYORDER-PAGE-004] 換頁請求失敗時頁碼退回前一頁，列表保留前一頁資料並顯示錯誤提示 :: AssertionError: 應有頁碼 2: expected undefined to be defined
      - MyOrdersPage.test.ts > [BW-MYORDER-PAGE-005] 快速連續換頁時丟棄過期回應：第 3 頁先到、第 2 頁後到，列表顯示第 3 頁 :: AssertionError: 應有頁碼 2: expected undefined to be defined
    - 摘要行：`Test Files  2 failed (2)` / `Tests  24 failed | 2 passed (26)`（通過的 2 項為「沒有訂單」「API 失敗」兩個與形狀無關的既有測試）。
  - GREEN 紀錄：實作兩頁的 `loadPage`（`currentPage`／`loadedPage` 元件內 ref、請求序號丟棄過期回應、空頁且 `totalCount > 0` 只重試一次改請求最後一頁、失敗時頁碼退回 `loadedPage`）與 `el-pagination layout="total, prev, pager, next"`（`totalCount > pageSize` 才顯示）後同一命令：`Test Files  2 passed (2)` / `Tests  26 passed (26)`。
    - 還原驗證（暫改正式程式碼）：將 `MyOrdersPage.vue` 的 `requestSequence !== latestRequestSequence` 全部改為 `false` → `FAIL … [BW-MYORDER-PAGE-005] …`：`AssertionError: expected 'Event 2-1已付款|Event 2-2已付款' to contain 'Event 3-1'`，`Tests  1 failed | 13 passed (14)`；還原後 `Tests  14 passed (14)`。
  - 重構後重跑：N/A（無重構）。`rg "getMyOrders|getAdminOrders" web/src` 僅出現在 `api/`、兩個頁面與其測試，其他頁面測試不需修改。
- [x] 6.3 後端啟動後在 `web` 容器執行 `npm run generate:api-types` 重新產生 `web/src/types/api.generated.ts`，檢查 diff 只包含兩個端點的 `page`／`pageSize` 查詢參數（回應型別在 OpenAPI 中為 `content?: never`，分頁回應結構由 `web/src/types/apiResponses.ts` 手寫的 `PagedResult<T>` 描述）；`docker compose exec -T web npm run build`（含型別檢查）通過。完成紀錄：執行 `docker compose restart api`（原本執行中的 api 還是舊建置，`/openapi/v1.json` 的 `/api/orders` 無查詢參數）後，OpenAPI 兩個列表端點皆有 `['page', 'pageSize']`；`docker compose exec -T web npm run generate:api-types` → `🚀 http://api:8080/openapi/v1.json → src/types/api.generated.ts [161.7ms]`；`git diff --stat`：`web/src/types/api.generated.ts | 14 ++++++++++----`（`10 insertions(+), 4 deletions(-)`）。diff 內容：`GET /api/orders` 與 `GET /api/admin/orders` 的 `query?: never` 改為 `{ page?: number | string; pageSize?: number | string }`（符合預期）；另有一處與本 change 無關的既有漂移：`SeatRequest.zoneCode`／`seatNumber` 由 `string` 變 `null | string`（上次重新產生後後端 DTO 已變，自 60b3bf0 起未更新；型別放寬，不影響編譯）。`docker compose exec -T web npm run build`（`vue-tsc -b && vite build`）→ `✓ built in 1.79s`，無型別錯誤。

## 7. 最終檢查與驗證

- [x] 7.1 hardener 最終檢查：程式碼全部完成後，對 `GetMyOrdersHandler`、`GetOrdersHandler`、`OrderRepository` 新方法與兩個 Controller 套用 `.claude/skills/hardener/SKILL.md` 檢查清單（參數驗證、`CancellationToken` 傳遞、例外邊界等），發現缺口依 TDD 補 RED 測試再修改，結果與證據記錄於此。完成紀錄（未新增防禦分支，無程式碼修改，故無 RED／GREEN）：套用前必答——輸入驗證：`PageRequestValidator`（Handler 入口以 `IValidator<PageRequest>` 驗證，越界回 `Validation`；模型繫結失敗由 MVC 回同格式 400，皆有 WebApi 測試）；權限：兩個 Controller 的既有 `[Authorize]`／Policy 與 organizer 範圍未變（`GetPageByOrganizerIdAsync` 保留 `Events.Any` 範圍，`OrganizerScopingTests` 覆蓋）；N+1：每頁仍為「1 次 Orders（含 Items）＋1 次 Count＋批次查 Events／Members」，`OrderDisplayQueryCountTests` 錨點通過；悲觀鎖：否（唯讀查詢）。Handler 檢查：參數驗證、`CancellationToken` 傳入 Validator 與 Repository、缺活動／缺買家仍大聲失敗；Repository 檢查：回傳契約為 `OrderPage`（非 `Result`）、`CancellationToken` 傳到 `CountAsync`／`ToListAsync`、無吞例外。已評估但接受：Count 與取頁為兩次查詢、未包交易，併發新增訂單時 `totalCount` 與當頁內容可能差一筆——僅為展示用數字，且「空頁且 `totalCount > 0` 回退最後一頁」的前端規則已涵蓋越界情形；Repository 對負數 `skip`／`take` 不另加守衛（Skill 對 Repository 不要求參數驗證，唯一呼叫端 Handler 已驗證範圍）。
- [x] 7.2 全套測試：`docker compose exec -T api dotnet test` 逐專案（Domain、Application、Infrastructure、WebApi、LoadTest.Seeder）與 `docker compose exec -T web npm run test`，每個測試專案的摘要行逐字貼入。`rg -n "api/orders|api/admin/orders" loadtest` 預期只命中 POST `/api/orders` 與訂單 confirm（`baseline.js`、`lib/load-test.js`、`fixtures/purchase-flow-ux-event.js`），逐筆確認沒有任何一筆是 GET 列表呼叫。完成紀錄（逐字摘要行）：
    - `Passed!  - Failed:     0, Passed:   153, Skipped:     0, Total:   153, Duration: 169 ms - ProjectC.Domain.Tests.dll (net10.0)`
    - `Passed!  - Failed:     0, Passed:   591, Skipped:     0, Total:   591, Duration: 1 s - ProjectC.Application.Tests.dll (net10.0)`
    - `Passed!  - Failed:     0, Passed:   274, Skipped:     0, Total:   274, Duration: 52 s - ProjectC.Infrastructure.Tests.dll (net10.0)`
    - `Passed!  - Failed:     0, Passed:    38, Skipped:     0, Total:    38, Duration: 7 s - ProjectC.LoadTest.Seeder.Tests.dll (net10.0)`
    - WebApi 第一次：`Failed ProjectC.WebApi.Tests.Startup.ServiceRegistrationSnapshotTests.ServiceRegistrations_MatchCommittedBaseline [49 ms]`／`Failed!  - Failed:     1, Passed:   753, Skipped:     0, Total:   754`，訊息：`DI 圖與基準檔不一致。缺少的項目：；多出的項目：Scoped | FluentValidation.IValidator`1[ProjectC.Application.Common.Paging.PageRequest] | ProjectC.Application.Common.Paging.PageRequestValidator || Scoped | ProjectC.Application.Common.Paging.PageRequestValidator | …`。原因：本 change 新增 `PageRequestValidator` 的 DI 註冊，屬預期變動；於 `ServiceRegistrationSnapshot.baseline.txt`（已排序）補上這兩行後單測 `Passed!  - Failed:     0, Passed:     1, Skipped:     0, Total:     1`。
    - WebApi 修正後全套：`Passed!  - Failed:     0, Passed:   754, Skipped:     0, Total:   754, Duration: 3 m 16 s - ProjectC.WebApi.Tests.dll (net10.0)`
    - 前端 `npm run test`：第一次（與 dotnet 測試同時執行）`Test Files  2 failed | 46 passed (48)` / `Tests  2 failed | 689 passed (691)`，失敗為與本 change 無關的 `EventCreatePage.test.ts > [AWU-EVENT-SALES-002] …` 與 `EventDetailPage.test.ts > 未登入嘗試調整計數購買數量…`；單獨重跑這兩檔 `Test Files  2 passed (2)` / `Tests  187 passed (187)`（既有負載 flake）。無並行負載下整套重跑：`Test Files  48 passed (48)` / `Tests  691 passed (691)`。
    - `rg -n "api/orders|api/admin/orders" loadtest` 命中 5 筆：`baseline.js:40` POST `/api/orders`、`check-measure-logging.test.sh:59` 日誌字串 `HTTP POST /api/orders`、`purchase-flow-ux-event.js:62` POST、`:115` POST confirm、`lib/load-test.js:155` POST confirm、`:168` POST `/api/orders`；無任何 GET 列表呼叫。
- [x] 7.3 真實瀏覽器驗證（claude-in-chrome）：以種子資料建立超過 20 筆訂單的買家與 Organizer，操作「我的訂單」與後台訂單列表的換頁、重新整理停留頁（「最後一頁 fallback」與「換頁失敗退回前一頁」以 Vitest 的 mock 回應驗證，真實瀏覽器不手動刪資料，因專案目前沒有刪除訂單的路徑）；總筆數、頁碼與每頁內容以頁面文字與資料庫查詢（`docker compose exec db psql` 於容器內）核對，不只依截圖判讀。完成紀錄（2026-11-08，https://localhost:5173，種子帳號 PG Buyer / PG Organizer，密碼為隨機值未記錄）：
  - 買家「我的訂單」：28 筆訂單（以頁面內 fetch 下單；pageSize=20 於第 21 筆起觸發 429 限流，等待後補下）。頁面文字 `Total 28`、頁碼 1／2；點第 2 頁顯示 8 筆，8 筆訂單 ID 與 DB 查詢（`order by CreatedAtUtc desc, Id asc offset 20`）逐筆相同、順序一致；按「重新整理」後仍停在第 2 頁、內容不變。
  - 後台訂單列表（切換至主辦方 17ed779e，DB 該主辦方訂單 46 筆）：頁面 `Total 46`、頁碼 1／2／3；第 2 頁 20 筆、第 3 頁 6 筆（20+20+6=46）；第 3 頁 6 筆 ID（85acaa03、c22179fa、33f7f036、8413313c、062e4e90、ee096961）與 DB `offset 40` 逐筆相同；於第 3 頁按「重新整理」後仍停在第 3 頁、仍 6 筆。
  - 最後一頁 fallback、換頁失敗退回依本任務說明以 Vitest 驗證（見 6.x 紀錄）。
- [x] 7.4 審查迴圈：依序執行 `strict-reviewer` 與全新的對抗式 general-purpose subagent，修正後重跑直到兩者皆無 blocking；每輪結果與修正記錄於此。完成紀錄：
  - 第 1 輪 strict-reviewer：FAIL，1 個 blocking。B-001：`MyOrdersPage.vue` 的 `<el-empty v-else-if>` 被插入的 `<el-pagination v-if>` 截斷，買家只有一頁（1–20 筆）訂單時表格與「目前沒有訂單」同時顯示；7.3 用 28 筆訂單所以未觸發。已確認屬實（AdminOrderListPage 無此樣式）。
    - RED（修改 .vue 前，`docker compose exec -T web npx vitest run src/pages/buyer/MyOrdersPage.test.ts`）：`× [BW-MYORDER-PAGE-002] 筆數不超過一頁或沒有訂單時不顯示分頁元件`，`AssertionError: expected '我的訂單重新整理活動狀態保留時間操作Event 1-1已付款Event 1…' not to contain '目前沒有訂單'`，Received 結尾為「Event 1-2已付款目前沒有訂單」；`Tests 1 failed | 14 passed (15)`。此失敗即為「沒有這次修改時測試會失敗」的證據（測試先於修正執行，失敗原因為目標行為不符）。
    - 還原驗證（補，2026-11-09；`bash scripts/verify-test-evidence.sh mutate --file web/src/pages/buyer/MyOrdersPage.vue --from '<el-empty v-if="orders.length === 0 && !loading && !errorMessage"' --to '<el-empty v-else-if="!loading && !errorMessage"' -- docker compose exec -T web npx vitest run src/pages/buyer/MyOrdersPage.test.ts`）：把修正還原為舊結構後同一測試失敗，原文：
      ```text
      × [BW-MYORDER-PAGE-002] 筆數不超過一頁或沒有訂單時不顯示分頁元件
      AssertionError: expected '我的訂單重新整理活動狀態保留時間操作Event 1-1已付款Event 1…' not to contain '目前沒有訂單'
      Received: "我的訂單重新整理活動狀態保留時間操作Event 1-1已付款Event 1-2已付款目前沒有訂單"
       ❯ src/pages/buyer/MyOrdersPage.test.ts:212:42
      Test Files  1 failed (1)
      Tests  1 failed | 13 passed (14)
      ```
      腳本只偵測 dotnet 的 `Failed!`，對 vitest 輸出印出 `結果：沒有測試失敗摘要，視為環境或編譯錯誤`（結束碼 2）；上列 vitest 失敗原文才是證據。檔案已由腳本還原（`git status` 乾淨）。
    - 修正：`<el-empty v-if="orders.length === 0 && !loading && !errorMessage">`（不再掛在 pagination 的條件鏈上）；單頁案例新增 `not.toContain('目前沒有訂單')` 斷言，既有「沒有訂單時顯示空清單提示」仍覆蓋空狀態。
    - GREEN：`MyOrdersPage.test.ts` `Tests 14 passed (14)`；全套 `Test Files 48 passed (48)`、`Tests 691 passed (691)`；`npm run build` `✓ built in 1.61s`。
  - 第 1 輪對抗式 general-purpose subagent（修正 B-001 之後、全新 context）：無 blocking。容器內測試 WebApi 篩選 88/88、Infrastructure 篩選 76/76、web 691/691。non-blocking：(1) 稱 `?page=` / `?pageSize=` 空值綁成預設值回 200、LP-VALID-004 未涵蓋空值——實測不成立：補整合測試時 `GET /api/orders?page=&pageSize=` 回 `400 (Bad Request)`（非可為 null 的 int 綁定失敗）；已新增 spec LP-VALID-008（空值回 400 `Validation`，errors 含對應欄位）並把 `?page=`、`?pageSize=` 加入兩個端點共用的 `InvalidPagingQueries`，`--filter PagingTests` `Passed!  - Failed:     0, Passed:    30`；(2) 兩個列表頁 `loadPage` 約 40 行重複；(3) totalCount 與 items 非同一快照（design 決策 5 已載明）；(4) Orders 表無 BuyerId／CreatedAtUtc 索引（design 風險章節已載明）。
  - 第 2 輪 strict-reviewer（B-001 修正後重審）：PASS，無 blocking。回歸核對 B-001 通過；容器內 Domain 153、Application 591、LoadTest.Seeder 38、Infrastructure 274、WebApi 754 皆 Failed: 0，web `Test Files 48 passed (48)`、`Tests 691 passed (691)`。至此兩者皆無 blocking。
- [x] 7.5 交叉審查（Claude Code Opus，bc363e6）FAIL 後的補強（2026-11-09）。B1／B2：還原驗證與逐字 RED／mutate 證據已補入 2.1、3.1、6.1、6.2、7.4 第 1 輪；B3：下列程式與測試變動後重跑審查迴圈（見下）。新增測試：
  - LP-VALID-002／007 的 HTTP 正向測試（`GetOrders_WithUpperBoundPagingParameters_Returns200`、`GetMyOrders_WithUpperBoundPagingParameters_Returns200`）。正式程式碼本來就正確，故以 `mutate` 取得「沒有此行為時會失敗」的證據（`PageRequestValidator.cs`）：
    - `InclusiveBetween(1, PageRequest.MaxPage)` → `InclusiveBetween(1, PageRequest.MaxPage - 1)`：`Failed!  - Failed:     2, Passed:     0, Skipped:     0, Total:     2, Duration: 2 s - ProjectC.WebApi.Tests.dll (net10.0)`；兩個測試皆 `System.Net.Http.HttpRequestException : Response status code does not indicate success: 400 (Bad Request).`；`結果：測試如預期失敗`
    - `InclusiveBetween(1, PageRequest.MaxPageSize)` → `InclusiveBetween(1, PageRequest.MaxPageSize - 1)`：同上 `Failed:     2, Passed:     0, Total:     2`、同樣 400 訊息；`結果：測試如預期失敗`
    - GREEN：`Passed!  - Failed:     0, Passed:     2, Skipped:     0, Total:     2, Duration: 2 s - ProjectC.WebApi.Tests.dll (net10.0)`
  - 兩個列表頁新增 `[BW-MYORDER-PAGE-005]`／`[AWU-ORDER-PAGE-005]` 同名編號的「過期請求的失敗回應被丟棄」測試（`catch` 內的過期序號守衛原本沒有測試）。`mutate`：`} catch (error) {` → `} catch (error) { latestRequestSequence = requestSequence;`（等同拿掉 `catch` 內的守衛）：
    - `× [BW-MYORDER-PAGE-005] 過期請求的失敗回應被丟棄：…`，`AssertionError: expected '我的訂單重新整理過期的失敗活動狀態保留時間操作Event 3-1已付款To…' not to contain '過期的失敗'`，`Tests  1 failed | 14 passed (15)`
    - `× [AWU-ORDER-PAGE-005] 過期請求的失敗回應被丟棄：…`，`AssertionError: expected '訂單管理重新整理過期的失敗訂單編號活動買家張數總金額狀態建立時間持有到期時…' not to contain '過期的失敗'`，`Tests  1 failed | 12 passed (13)`
    - 腳本對 vitest 輸出印 `結果：沒有測試失敗摘要，視為環境或編譯錯誤`（只認 dotnet `Failed!`），上列 vitest 失敗原文才是證據。GREEN：兩檔 `Test Files  2 passed (2)` / `Tests  28 passed (28)`。
  - 審查迴圈第 3 輪 strict-reviewer（7.5 補強後）：PASS，無 blocking。其容器內全套摘要行（逐字）：
    - `Passed!  - Failed:     0, Passed:   153, Skipped:     0, Total:   153 - ProjectC.Domain.Tests.dll`
    - `Passed!  - Failed:     0, Passed:   591, Skipped:     0, Total:   591 - ProjectC.Application.Tests.dll`
    - `Passed!  - Failed:     0, Passed:   274, Skipped:     0, Total:   274 - ProjectC.Infrastructure.Tests.dll`
    - `Passed!  - Failed:     0, Passed:   760, Skipped:     0, Total:   760 - ProjectC.WebApi.Tests.dll`
    - `Passed!  - Failed:     0, Passed:    38, Skipped:     0, Total:    38 - ProjectC.LoadTest.Seeder.Tests.dll`
    - web：`Test Files 48 passed (48)`、`Tests 693 passed (693)`
    - warnings（non-blocking）：4.1 RED 與 1.1 為節錄；`mutate` 腳本不支援 vitest 失敗偵測；`loadPage` 兩頁重複。
  - 注意：`mutate` 還原後，容器內增量建置可能沿用變異版 dll（曾造成 WebApi 分頁測試假失敗，totalCount 多出其他組織訂單）；還原後須 `touch` 被變異的檔案再測。
  - 審查迴圈第 3 輪對抗式 general-purpose subagent（全新 context，只給 change 名稱與目標）：無 blocking。實測：vitest 6 檔 54 個測試全過；WebApi `PagingTests` 32/32；touch 後 `AdminOrdersPagingTests`＋`OrganizerScopingDataCorruptionTests` 27/27。自行 mutate 兩項皆被測試擋下：`AdminOrdersController` `new PageRequest(page, pageSize)` → `new PageRequest(1, pageSize)`（8 個 WebApi 分頁測試失敗）、`MyOrdersPage.vue` 拿掉 `catch` 內過期守衛（`[BW-MYORDER-PAGE-005]` 失敗，其餘 14 過）。其 WebApi 全套第一次 9 failed／751 passed 為殘留變異 dll（見上方注意），touch 後恢復。至此 strict-reviewer 與對抗式審查皆無 blocking。
  - non-blocking：`FakeOrderRepository` 的 Guid 排序（.NET）與 Postgres uuid bytewise 排序不同，目前 Handler 測試不依賴；`?page=1&page=2` 重複參數未測；`loadPage` 兩頁重複。

## Workflow follow-up

- 審查通過且使用者明確同意後才 `/opsx:archive`（含主 spec 同步）並 merge 回 master；任何步驟不自動 commit／push。
- 後續 change：`admin-event-list-pagination`（`GET /api/admin/events`，引用本 change 的 `list-pagination`）、公開 `GET /api/events` 的分頁與快取設計。
