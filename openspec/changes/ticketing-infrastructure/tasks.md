## 1. 共用基礎（Domain 介面、Application UnitOfWork）

- [x] 1.1 在 `ProjectC.Domain` 定義 Repository 介面：`IVenueRepository`、`ISeatMapRepository`、`IEventRepository`、`IEventSeatRepository`、`ITicketTypeRepository`、`IOrderRepository`（`Seat`、`OrderItem` 依附各自聚合根存取，不單獨開介面），只涵蓋依 ID 查詢、新增這類最小夠用的操作
- [x] 1.2 `IEventSeatRepository` 新增 `GetForUpdateAsync(IReadOnlyList<Guid> eventSeatIds, CancellationToken ct)`，介面 XML 文件註明：(a) 回傳的實體在目前交易內已被資料庫鎖定，(b) 涵蓋 `Hold`/`ConfirmSold`/`ReleaseHold` 三個操作，凡是會呼叫這三個方法之一都必須先取得鎖，(c) MUST 在已開啟的交易內呼叫，否則拋出例外，(d) 傳入空清單拋 `ArgumentException`，(e) 部分 ID 找不到對應列時只回傳實際存在的實體，不補空值也不拋例外（對應 design.md 決策 1、2）
- [x] 1.3 在 `ProjectC.Application.Common.Interfaces` 定義 `IUnitOfWork`/`IUnitOfWorkTransaction`（`BeginTransactionAsync` 回傳一個實作 `IAsyncDisposable` 的 transaction handle，handle 上有 `CommitAsync`/`RollbackAsync`），介面文件明確寫出 design.md 決策 4 的五條生命週期規則（重複 Begin 拋例外、Commit 自動呼叫 SaveChanges、Dispose 未 Commit 視為放棄並自動 Rollback、對已結束的 handle 再次操作拋例外、不提供交易之外的獨立 `SaveChangesAsync`）

## 2. Infrastructure：EF Core 持久化

- [x] 2.1 在 `ProjectC.Infrastructure.Persistence.Configurations` 新增八個 Entity 的 EF Core Configuration（`Venue`、`SeatMap`、`Seat`、`Event`、`EventSeat`、`TicketType`、`Order`、`OrderItem`），反映 Domain 既有的唯一性約束（座位樣板分區代碼+編號唯一、EventSeat 依 EventId+SeatId 唯一）為資料庫層級的 Unique Index。依 design.md 決策 5：
  - `Order`、`TicketType` 各新增一個只吃純量參數的 `private` 建構子供 EF 物化使用（`Order` 不接受 `items` 集合參數，避免跟公開建構子產生歧義）
  - backing field mapping 涵蓋 `EventSeat` 的 `_heldByOrderId`/`_heldUntilUtc`/`_soldByOrderId`、`Order` 的 `_items`、`SeatMap` 的 `_seats`
  - `OrderItem` 與 `Order` 的一對多關聯用 shadow FK（`OrderId`），不修改 `OrderItem` 公開介面

  **[2026-08-16 驗收修正]** 初版只設定了純量欄位（`.Property(...)`），沒有幫 `SeatMaps.VenueId`、`Events.VenueId`、`Events.SeatMapId`、`EventSeats.EventId`、`EventSeats.SeatId`、`TicketTypes.EventId`、`Orders.EventId`、`OrderItems.EventSeatId` 這八個跨聚合的參照建立真正的資料庫 FK 約束（Domain 這些關聯本身就沒有 navigation property，只有純量 Guid），導致可以插入指向不存在活動/座位/座位圖的資料。已在對應 Configuration 補上 `HasOne<T>().WithMany().HasForeignKey(...)`（不需要 navigation 也能建立 FK），全部設為 `DeleteBehavior.Restrict`（跨聚合參照不能 cascade delete；`Seats.SeatMapId`、`OrderItems.OrderId` 這兩個原本就是聚合內部的擁有關係，維持 `Cascade` 不變）。migration 已重新產生並套用。
- [x] 2.2 `ApplicationDbContext`（具體類別）新增八個 `DbSet<T>`；**不**加進 `IApplicationDbContext` 介面（design.md 決策 3，避免繞過 Repository）
- [x] 2.3 產生 Migration：`docker compose exec api dotnet ef migrations add AddTicketingPersistence`，確認 Up/Down 皆正確
- [x] 2.4 實作六個 Repository（`ProjectC.Infrastructure.Persistence.Repositories`），一般查詢/寫入走 EF Core 標準 API；`IOrderRepository.GetByIdAsync` MUST `Include(o => o.Items)`，`ISeatMapRepository.GetByIdAsync` MUST `Include(m => m.Seats)`（design.md 決策 5，Domain 邏輯假設這兩個集合已完整載入）；所有寫入方法只把物件加進 change tracker，MUST NOT 自行呼叫 `SaveChangesAsync`（design.md 決策 4）
- [x] 2.5 實作 `IEventSeatRepository.GetForUpdateAsync`：傳入的 `eventSeatIds` 為空時拋 `ArgumentException`，非空則先去重，方法開頭檢查 `Database.CurrentTransaction is not null`（沒有進行中交易時拋 `InvalidOperationException`），接著用單一 `FromSqlInterpolated` 查詢（`SELECT * FROM "EventSeats" WHERE "Id" = ANY(@ids) ORDER BY "Id" FOR UPDATE`）一次鎖定所有列，不逐筆迴圈查詢，回傳實際找到的實體（可能少於傳入的 ID 數量）；此處繞過 EF Core 標準查詢是因為 EF Core 無對應悲觀鎖 API，符合 CLAUDE.md 允許例外的情況，需在程式碼註解說明理由；Npgsql 的陣列參數繫結需以測試驗證實際可行，若字串插值無法可靠推斷 `uuid[]` 型別，改用明確的 `NpgsqlParameter`
- [x] 2.6 實作 `IUnitOfWork`/`IUnitOfWorkTransaction`（包裝 `ApplicationDbContext.Database.BeginTransactionAsync`），落實 design.md 決策 4 的五條生命週期規則
- [x] 2.7 於 `Program.cs` 註冊上述 Repository 與 `IUnitOfWork` 至 DI（Scoped，理由：綁定同一個 `DbContext` 生命週期）

## 3. 測試

- [x] 3.1 建立 `ProjectC.Infrastructure.Tests` 測試專案，並加入 `ProjectC.slnx` 的 `/tests/` 資料夾：reference `ProjectC.Infrastructure`，套件（`Testcontainers.PostgreSql`、`xunit`、`xunit.runner.visualstudio`、`FluentAssertions`、`Microsoft.NET.Test.Sdk`、`coverlet.collector`）已在 `Directory.Packages.props` 集中管理版本，不需另外指定版本號
- [x] 3.2 `ProjectC.Infrastructure.Tests`（Testcontainers 整合測試）：驗證 EF Core Configuration 的唯一性約束確實反映到資料庫（重複座位樣板/重複 EventSeat 寫入時違反 Unique Index）；順便驗證資料庫裡的 `Order.Status` 不會出現 `Expired` 這個值（既有 Domain 不變條件的迴歸防護，design.md 決策 5）
- [x] 3.3 整合測試：驗證 `GetForUpdateAsync` 在兩個並發交易同時鎖定同一座位時，第二個交易會等待第一個交易提交/回滾後才能取得列，證明悲觀鎖確實生效（對應 spec `seat-reservation`「兩筆交易同時嘗試鎖定同一座位」與 design.md 決策 1）；測試需設定明確的等待逾時（例如 Postgres `lock_timeout` 或測試層級的 cancellation），不要依賴預設 command timeout，避免鎖定行為若有誤時整個測試無限期卡住
- [x] 3.4 整合測試：驗證交易 A 執行 `ConfirmSold` 期間，交易 B 對同一座位嘗試 `Hold`/`ReleaseHold` MUST 等待（對應 spec「一筆交易確認售出座位時，另一筆交易同時嘗試修改同一座位」），同樣需設定明確逾時
- [x] 3.5 整合測試：驗證 `GetForUpdateAsync` 對重疊但不完全相同的座位集合交叉鎖定時不會死鎖，鎖定順序由資料庫 `ORDER BY "Id"` 保證（對應 spec「多筆座位交叉鎖定不會造成死鎖」與 design.md 決策 1），同樣需設定明確逾時

  **[2026-08-16 驗收修正]** 初版這個測試讓 A 完整鎖完才讓 B 開始，屬於循序競爭，沒有真正驗證「兩邊幾乎同時發起、輸入 ID 順序刻意相反」這種更貼近死鎖風險的情境。已另外補上 `GetForUpdateAsync_ConcurrentOverlappingRequestsStartedSimultaneouslyInOppositeOrder_NeitherDeadlocksNorHangs`：兩筆交易中間不互相 `await`、幾乎同時發起，A 用 `[X, Y]`、B 用 `[Y, X]`（刻意反向），驗證鎖定順序由資料庫 `ORDER BY` 保證、不受呼叫端輸入順序影響。原本的測試保留，兩者驗證的情境不同。
- [x] 3.9 整合測試：驗證跨聚合的外鍵約束確實擋得住非法參照（`ForeignKeyConstraintsTests`）——涵蓋全部 8 條新增 FK（`SeatMaps.VenueId`、`Events.VenueId`/`SeatMapId`、`EventSeats.EventId`/`SeatId`、`TicketTypes.EventId`、`Orders.EventId`、`OrderItems.EventSeatId`）皆有對應測試驗證非法插入會違反約束（對應 task 2.1 的驗收修正）。**[2026-08-16 二次驗收修正]** 初版只覆蓋 4 條代表性 FK，已補齊剩下 4 條，達到跟 task 2.1 的「八條新增 FK」完全對齊

  另外修正 `GetForUpdateAsync_ConcurrentOverlappingRequestsStartedSimultaneouslyInOppositeOrder_NeitherDeadlocksNorHangs`（task 3.5 補的那個測試）：原本用 `loserTask.IsCompleted.Should().BeFalse(...)` 做即時同步檢查，這種寫法理論上可能因為 task 排程時機巧合而 flaky；改成跟其他並發測試一致的等待式驗證（`Task.WhenAny(loserTask, Task.Delay(500ms))`），實際等一小段時間才斷言，更穩定。

  **[2026-08-16 自查修正]** 主動比對既有 codebase 的命名慣例，發現這次新增的 14 個檔案（Domain 6 個 Repository 介面、Infrastructure 6 個 Repository 實作、`UnitOfWork.cs`、`IUnitOfWork.cs`）全部把 `CancellationToken` 參數命名為 `ct`，但既有 Handler／Controller 一律用全名 `cancellationToken`（`grep` 現有程式碼確認），屬於未經確認就引入的縮寫，違反 CLAUDE.md「禁止使用縮寫」與「遵循既有命名慣例」。已全部改回 `cancellationToken`。
- [x] 3.6 整合測試：驗證未開啟交易時呼叫 `GetForUpdateAsync` 會立即拋出例外，不會誤導呼叫端以為已取得鎖（對應 spec「未在交易內嘗試取得座位鎖」與 design.md 決策 2）
- [x] 3.7 整合測試：驗證 `IUnitOfWork` 的交易生命週期規則（重複 `BeginTransactionAsync` 拋例外、`CommitAsync` 確實落地資料、未呼叫 Commit/Rollback 就 Dispose 會自動回滾、對已結束的 transaction handle 再次呼叫拋例外、沒有交易時無法寫入資料）（對應 design.md 決策 4）
- [x] 3.8 整合測試：驗證各 Repository 的基本 CRUD 往返，皆包在 `BeginTransactionAsync`/`CommitAsync` 內執行（寫入後依 ID 讀回，逐欄位比對一致，包含 `EventSeat` 的私有欄位、`OrderItem` 的 shadow FK、`Order.Items` 與 `SeatMap.Seats` 這兩個子集合的內容）

## 4. 收尾檢查

- [x] 4.1 確認 `ProjectC.Domain.csproj` 未新增任何 `<ProjectReference>`
- [x] 4.2 確認既有三個 Handler（`CreateOrderHandler`/`ConfirmOrderHandler`/`CancelOrderHandler`）簽章與行為未被改動
- [x] 4.3 確認 EF materialization 新增的 `private` 建構子與 backing field mapping 沒有開放任何新的 public setter（design.md 決策 5 的硬性限制）
- [x] 4.4 確認 `IApplicationDbContext` 介面沒有被加入任何售票相關的 `DbSet`（design.md 決策 3）
- [x] 4.5 執行全部測試（`docker compose exec api dotnet test`），確認通過

  **[2026-08-16 驗收修正]** 額外修正兩處文件措辭：design.md 決策 5 原本描述「即使加 private 無參建構子，EF 還是可能選錯」，實際查證 EF Core 規則是「navigation/collection 參數從一開始就不可能被 constructor binding 選中」，不是「選錯」的問題，已改成準確描述；`IUnitOfWork.cs` 的 XML 文件原本寫「任何寫入都必須包在交易裡」，容易被誤讀成會員系統也要改，已明確加註「只有售票 Repository 的寫入透過這個介面，會員系統維持既有的 `IApplicationDbContext.SaveChangesAsync`」。
- [x] 4.6 比對 tasks 完成狀況與 `seat-reservation`（新增部分）spec 的全部 4 個 Scenario，確認皆有對應測試
- [x] 4.7 主動告知：Repository 介面的形狀是否需要在下一個 change 開始規劃時重新檢視（design.md 已標注這是可接受的已知風險，非阻擋項目）
