## Context

`ticketing-core-domain`（已歸檔）只有 Domain + Application 層。三個既有 Handler（`CreateOrderHandler`、`ConfirmOrderHandler`、`CancelOrderHandler`）都是純協調邏輯，不做任何 I/O：呼叫端要先把 `EventSeat`/`TicketType`/`Order` 等物件載入好、以參數傳入，Handler 只在記憶體中操作這些物件、回傳 `Result`，從不負責存檔。這個設計本身是刻意的（方便單元測試、不混入持久化細節），這次先補上「怎麼把這些物件真的存到資料庫、怎麼在真實併發下安全地鎖座位」這一層，不動這三個 Handler 本身。

會員系統（`membership-system`）目前只有 3 個 Entity（`Member`、`RefreshToken`、`PasswordResetToken`），照 CLAUDE.md 門檻直接注入 `IApplicationDbContext`，沒有 Repository 抽象層。這次售票這邊有 8 個 Entity，超過門檻，是這個 codebase 第一次引入 Repository 介面。

這是原本 `ticketing-persistence-api` change 拆分後的第一部分，只做 Infrastructure（持久化 + 並發控制），刻意不含任何 WebApi 端點——原因是悲觀鎖這種機制容易出錯，需要能獨立寫測試驗證（兩個並發交易搶同一座位），不希望跟 Controller/授權/背景服務的程式碼混在一起改，那樣出問題時不容易定位是持久化本身的問題還是上層邏輯的問題。API 層、`Order.BuyerId`、後台管理/買家端/後台訂單查看、背景清理，都留給下一個 change（暫定名稱 `ticketing-api`），建立在這裡驗證過的基礎上。

## Goals / Non-Goals

**Goals:**
- 八個售票 Entity 的 EF Core 持久化（mapping + migration），Domain 不新增任何業務欄位（仍不含 `Order.BuyerId`），但允許為了讓 EF Core 能物化這些 rich domain model 而做最小結構調整（見決策 5）。
- `EventSeat` 的鎖定/售出狀態變更（`Hold`/`ConfirmSold`/`ReleaseHold`）在真實併發下，用資料庫悲觀鎖保證不會有兩筆交易同時修改同一座位的狀態。
- Repository 介面定義在 Domain、實作在 Infrastructure，供下一個 change 的 Application 協調服務使用。
- `IUnitOfWork` 包裝交易邊界，供下一個 change 使用。

**Non-Goals:**
- 任何對外 WebApi 端點（後台管理、買家端、後台訂單查看）——留給下一個 change。
- `Order.BuyerId`／登入串接——這次不新增任何業務欄位，`Order` 仍無買家身份欄位（結構上允許為 EF 物化做最小調整，見決策 5，但不代表 Domain 模型完全不動）。
- Application 層的協調服務（例如把 Repository/UnitOfWork/既有 Handler 串起來的 Orchestrator）——這次只交付 Repository 與 UnitOfWork 本身，串接邏輯留給下一個 change（那邊會需要 `requestingBuyerId` 之類的 API 層概念，放這裡沒有意義）。
- 逾時訂單的背景自動清理——留給下一個 change。

## Decisions

### 1. 座位鎖定用悲觀鎖，單一 SQL 查詢達成，涵蓋所有會修改鎖定/售出欄位的操作
EF Core 的 LINQ API 沒有對應標準 `SELECT ... FOR UPDATE` 的寫法，只能透過 `FromSqlInterpolated` 下 Raw SQL 取得帶鎖的列。這符合 CLAUDE.md「禁止繞過 EF Core 直接寫原生 SQL，除非有明確說明理由」的例外情況——這裡的理由是 EF Core 本身不支援悲觀鎖語法。

新增 `Domain.Events.IEventSeatRepository.GetForUpdateAsync(IReadOnlyList<Guid> eventSeatIds, CancellationToken ct)`。實作方式是**一次 SQL 查詢鎖定所有列**，而非在 .NET 端逐筆迴圈鎖定：

```sql
SELECT * FROM "EventSeats"
WHERE "Id" = ANY(@ids)
ORDER BY "Id"
FOR UPDATE
```

原因：
- **一次查詢，不要逐筆 round-trip**：逐筆鎖定在多座位訂單下會有 N 次資料庫往返，且第一個座位持鎖後、其餘座位還沒鎖到，會放大熱門場次的延遲。
- **鎖定順序由資料庫的 `ORDER BY` 保證，不靠 .NET 端排序**：C# 的 `Guid.CompareTo` 與 PostgreSQL `uuid` 型別的排序規則不保證一致；若兩邊分別排序，不同交易可能因排序結果不同而走不同的鎖定順序，達不到「固定順序避免死鎖」的目的。改成資料庫端 `ORDER BY "Id"` 是唯一可信的順序來源，所有交易都走同一條路徑。
- **去重是實作的責任，不是呼叫端的責任**：`GetForUpdateAsync` 內部 MUST 對傳入的 `eventSeatIds` 去重再組 SQL（不要求、也不能信任呼叫端一定會先去重）。
- **輸入為空清單時 MUST 拋出 `ArgumentException`**：呼叫這個方法卻不帶任何 ID 是呼叫端的邏輯錯誤，不是「鎖 0 筆」的合法情境，直接 fail fast。
- **部分 ID 找不到對應列時，只回傳實際存在的實體，不補空值也不拋例外**：`ANY(@ids)` 天生只會回傳存在的列；`GetForUpdateAsync` 回傳的集合可能比傳入的 ID 數量少，比對數量、判斷是否有座位不存在，是呼叫端（下一個 change 的協調服務）的責任，不在這個方法裡處理。
- Npgsql 的陣列參數繫結（`Guid[]`/`List<Guid>` → `uuid[]`）需要在實作時（task 2.5）用整合測試實際驗證可行；若 `FromSqlInterpolated` 的字串插值無法可靠推斷出 `uuid[]` 參數型別，退回明確建立 `NpgsqlParameter` 指定型別，這裡先定調方向，不保證語法細節。

**鎖定範圍涵蓋 `Hold`、`ConfirmSold`、`ReleaseHold` 三個會修改 `EventSeat` 私有欄位（`_heldByOrderId`/`_heldUntilUtc`/`_soldByOrderId`）的操作，不只是「建立訂單時的 Hold」。** 這次雖然還沒有 Orchestrator 去呼叫 `ConfirmOrderHandler`/`CancelOrderHandler`，但介面契約與 XML 文件必須寫清楚：下一個 change 的協調服務只要會呼叫這三個方法中任何一個，都必須先透過 `GetForUpdateAsync` 取得帶鎖的實體，否則悲觀鎖形同虛設（例如 A 交易正在 `ConfirmSold`、B 交易同時未經鎖定就 `Hold` 或 `ReleaseHold` 同一座位，會讀到過期狀態）。

只有「修改座位鎖定/售出狀態」需要這個帶鎖查詢；純瀏覽（查詢可售性）用一般唯讀查詢，不鎖，允許顯示些微過期的可售狀態——實際的「瀏覽端點」本身留給下一個 change。

### 2. `GetForUpdateAsync` 必須在已開啟的交易內呼叫
PostgreSQL 的列鎖只在交易存續期間有效，交易一旦提交或回滾，鎖立刻釋放。如果呼叫端沒有明確開交易，`SELECT ... FOR UPDATE` 這行 SQL 本身就是一筆獨立交易，查詢一返回鎖就沒了——`GetForUpdateAsync` 會變成「看起來有鎖、其實完全沒鎖」的假保護。

Infrastructure 實作 MUST 在方法一開始檢查 `DbContext.Database.CurrentTransaction is not null`；沒有進行中的交易時，直接拋出 `InvalidOperationException`（fail fast），不要讓呼叫端誤以為呼叫這個方法本身就有鎖的效果。對應的整合測試需要覆蓋「未開交易時呼叫 `GetForUpdateAsync`」的情境。

### 3. Repository 介面定義在 Domain，實作在 Infrastructure
依 CLAUDE.md 既定規則。新增：`IVenueRepository`、`ISeatMapRepository`、`IEventRepository`、`IEventSeatRepository`、`ITicketTypeRepository`、`IOrderRepository`（`Seat`、`OrderItem` 依附各自聚合根存取，不單獨開介面）。

這次的 Repository 介面只需要涵蓋「下一個 change 的協調服務會用到」的基本操作（依 ID 查詢、新增、`GetForUpdateAsync`），不預先猜測分頁/篩選等查詢需求——那些等下一個 change 真的要做瀏覽/管理 API 時再依實際需求擴充介面。

**新的八個 `DbSet<T>` 只加在具體類別 `ApplicationDbContext` 上，不加進 `IApplicationDbContext` 介面。** `IApplicationDbContext` 目前只暴露會員系統的三個 `DbSet`；若把售票的 `DbSet` 也加進這個介面，等於又開一條繞過 Repository、直接查售票 Entity 的路，讓這次「Repository 是存取售票資料唯一入口」的設計形同虛設。`ApplicationDbContext` 具體類別本身仍需要這些 `DbSet`（Repository 實作內部要用），只是不透過介面對外暴露。

### 4. `IUnitOfWork` 的交易生命週期契約
定義在 `Application.Common.Interfaces`：

```csharp
public interface IUnitOfWork
{
    Task<IUnitOfWorkTransaction> BeginTransactionAsync(CancellationToken ct);
}

public interface IUnitOfWorkTransaction : IAsyncDisposable
{
    Task CommitAsync(CancellationToken ct);
    Task RollbackAsync(CancellationToken ct);
}
```

明確定義以下行為，避免呼叫端忘記 rollback/dispose 造成交易懸而未決：
- **`BeginTransactionAsync` 重複呼叫**：若目前這個 `DbContext`/`IUnitOfWork` 實例已有進行中的交易，MUST 拋出 `InvalidOperationException`（這也自然對應 EF Core `DbContext` 本身一次只能有一個進行中交易的限制）。
- **`CommitAsync` 會自動呼叫 `SaveChangesAsync`**：呼叫端不需要（也不應該）在呼叫 `CommitAsync` 前另外呼叫 `SaveChangesAsync`；`CommitAsync` 內部依序執行 `SaveChangesAsync` → 資料庫交易的 `CommitAsync`，兩者視為同一個原子操作。
- **未呼叫 `CommitAsync`/`RollbackAsync` 就 `DisposeAsync`**：視為呼叫端放棄這筆交易，`DisposeAsync` MUST 自動回滾，不留下懸而未決的交易（安全預設值）。
- **對已經 Commit 或 Rollback 過的 transaction handle 再次呼叫 `CommitAsync`/`RollbackAsync`**：MUST 拋出 `InvalidOperationException`（避免誤用同一個 handle 操作兩次）。
- **這個介面刻意不提供獨立於交易之外的 `SaveChangesAsync`**：唯一能把資料寫進資料庫的路徑是 `BeginTransactionAsync` → ... → `CommitAsync`。Repository 的寫入方法（例如 `IVenueRepository.Add`）只把物件加進 EF Core 的 change tracker，MUST NOT 自己呼叫 `SaveChangesAsync`——即使是「不需要悲觀鎖的單純新增」（例如建立一個 `Venue`）也一樣要包在交易裡才會真的落地。這是刻意的簡化：只有一條寫入路徑，不會有人在 Repository 裡偷塞 `SaveChanges` 導致行為不一致；EF Core 本身每次 `SaveChangesAsync` 也都會用隱含交易包住，所以這不是額外的效能負擔，只是把它明確化成一個顯式契約。

`OrderService`（下一個 change 才會新增的協調服務）的標準用法會是 `await using var tx = await unitOfWork.BeginTransactionAsync(ct); ... await tx.CommitAsync(ct);`——失敗路徑不呼叫 `CommitAsync`，交由 `DisposeAsync` 自動回滾即可，不需要每個失敗分支都手動呼叫 `RollbackAsync`。這次的 CRUD 往返測試（task 3.8）也要照這個路徑寫，不能繞過交易直接測 Repository。

### 5. EF Core materialization：允許為既有 Domain Entity 做最小結構調整
現有 Domain Entity 是為了「方便測試、封裝良好」設計的，不是為了 EF Core 物化設計的。逐一檢查八個 Entity 後，真正需要調整的只有三處：

- **`Order`**：公開建構子是 `Order(Guid id, Guid eventId, DateTime heldUntilUtc, IEnumerable<OrderItem> items)`，`items` 參數對應到 `Items` navigation。EF Core 的 constructor binding 規則是「一個參數只能對應到純量欄位／shadow 屬性，不能對應到 navigation（collection 或關聯實體）」——所以這個公開建構子從一開始就不是 EF 物化的候選建構子，不需要额外處理歧義。真正的問題是：這個建構子完全沒有其他可用的建構子可以物化，需要**新增一個 EF 專用的 `private` 建構子**，且只吃純量參數（不吃 `items`）——`_items` 完全透過 backing field mapping 處理，不經過任何建構子參數。
- **`TicketType`**（`internal` 建構子）：需要一個完整的 `SeatMap` 物件來驗證 zone 是否存在，EF Core 物化時不會有這個物件可傳，constructor binding 在這裡完全用不上。
- **`OrderItem`** 沒有 `OrderId` 屬性（只有 `EventSeatId`），`Order`／`OrderItem` 的一對多關聯需要額外的 FK；`Order._items` 與 `SeatMap._seats` 這兩個集合都要 backing field mapping，不能只做 `EventSeat` 的三個純量私有欄位。

`Seat`、`Venue`、`Event`、`EventSeat` 的既有建構子只吃純量參數（`Guid`/`string`/`DateTime`），且不會跟其他建構子產生歧義，EF Core constructor binding 可以直接使用，不需要額外調整。

決策：
- **`Order` 新增一個 EF 專用的 `private` 建構子**：`private Order(Guid id, Guid eventId, DateTime heldUntilUtc, OrderStatus status)`，四個參數都對應到既有屬性（`Id`/`EventId`/`HeldUntilUtc`/`Status`），**不接受 `items` 集合**——`_items` 完全透過 backing field mapping 處理，不經過任何建構子參數。這是唯一一個 EF Core 能用來物化 `Order` 的建構子（公開建構子因為含有 navigation 參數，從一開始就不是候選）。
- **`TicketType` 新增一個只吃純量參數的 `private` 建構子**：不含 `SeatMap` 參數，`ZoneCode`/`Price` 等改用 backing field 或屬性直接 mapping（不需要走驗證邏輯，因為從資料庫讀回來的資料已經通過當初寫入時的驗證）。
- **backing field mapping 涵蓋範圍**：`EventSeat` 的 `_heldByOrderId`/`_heldUntilUtc`/`_soldByOrderId`、`Order` 的 `_items`、`SeatMap` 的 `_seats`。`OrderItem` 與 `Order` 的關聯用 **shadow FK 屬性**（`OrderId`，只存在於 EF Core 的 Model 層，不出現在 `OrderItem` 類別本身）處理，不修改 `OrderItem` 的公開介面。
- **`IOrderRepository.GetByIdAsync` MUST 明確 `Include` `Items`；`ISeatMapRepository.GetByIdAsync` MUST 明確 `Include` `Seats`。** 這不只是避免物化例外，而是 Domain 邏輯本身假設這兩個集合是完整的：`ConfirmOrderHandler`/`CancelOrderHandler` 會遍歷 `order.Items`，`Event.CreateEventSeats`/`CreateTicketType` 會讀 `seatMap.Seats` 判斷 zone 是否存在——沒有 Include 到，會得到「查得到訂單/座位圖，但內容是空的」這種更難察覺的錯誤結果，而不是明顯的例外。
- **`OrderStatus.Expired` 不落庫的既有約束需要在 mapping 層有對應防護**：Domain 本身已保證 `Status` 欄位只會被賦值 Pending/Confirmed/Cancelled 三者之一（`Expired` 純粹是 `GetStatus(now)` 查詢時推導），這裡不需要額外的 EF `HasConversion`，但 task 3.2 的唯一性/約束測試順便驗證「資料庫裡不會出現 `Expired` 這個值」，作為這個既有不變條件的迴歸防護。

**硬性限制：不開放任何新的 public setter。** 所有既有的 `private set`／唯讀屬性維持不變，這個調整只是給 EF Core 一條「怎麼把資料庫的一列組回這個物件」的路，不改變外部程式碼能對這個物件做什麼——符合 CLAUDE.md「Domain Entity 內部狀態一律 private set」的規則。

## Risks / Trade-offs

- **[Risk]** 悲觀鎖在熱門開賣、高併發搶同一批座位時，交易會互相排隊，可能拉高延遲甚至逾時。→ **Mitigation**：這是使用者已知並接受的取捨（相較樂觀鎖換取「絕不衝突」的保證）；先上線觀察，若真的成為瓶頸，之後可評估更細的鎖定粒度或改用樂觀鎖 + 佇列重試。
- **[Risk]** 多座位鎖定若鎖定順序不一致會造成資料庫死鎖。→ **Mitigation**：`GetForUpdateAsync` 改為單一 SQL 查詢、由資料庫的 `ORDER BY "Id"` 保證所有交易走同一條鎖定順序（不靠 .NET 端排序），並在測試中驗證交叉鎖定情境不會死鎖。
- **[Risk]** 這次交付的 Repository 介面形狀（方法簽章）可能不完全符合下一個 change 實際串接時的需求，屆時需要回頭調整介面。→ **Mitigation**：介面刻意先寫最小夠用的形狀，調整介面本身的成本遠低於調整持久化/鎖定機制本身，可接受。
- **[Risk]** 為 EF materialization 新增的 `private` 建構子（`Order`/`TicketType` 為純量參數版本）與 backing field mapping（決策 5）容易寫錯（漏掉某個私有欄位、shadow FK 命名不一致、忘記 `Include` 子集合），導致物化出來的物件狀態跟資料庫實際內容不一致，卻不一定會在編譯期或明顯的執行期錯誤中曝光。→ **Mitigation**：task 3.8 的 CRUD 往返測試（寫入後依 ID 讀回、逐欄位比對，含子集合內容）直接覆蓋這個風險，比只測「能不能存」更嚴格。

## Migration Plan

- 新增 EF Core Migration：`docker compose exec api dotnet ef migrations add AddTicketingPersistence`，全新資料表，不涉及既有資料轉換。
- Rollback：`docker compose exec api dotnet ef database update <上一個 migration>`（新表未被任何既有功能依賴，可安全回退）。

## Open Questions

（無——這次範圍已收斂到純 Infrastructure，主要的架構決策都已定案；下一個 change 開始規劃時才需要重新確認 Admin 授權、背景清理等問題）
