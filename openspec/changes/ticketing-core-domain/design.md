## Context

目前 `ProjectC.Domain` / `ProjectC.Application` 皆為空專案，尚未有任何 Entity。此階段只處理 Domain + Application 層的邏輯，不涉及 EF Core 持久化、Controller、付款。座位鎖定機制是整個售票流程中最容易出錯的部分（超賣、逾時未釋放、庫存跨活動誤共用、補償釋放釋放到別人的鎖），因此設計重點放在「庫存歸屬是否正確、狀態機是否完整、不變條件是否在正確的層級被強制執行、跨 Entity 的協調邏輯是否驗證了完整的前置條件」。

本文件已歷經兩輪修正：第一輪修正了庫存歸屬（改用 `EventSeat`）與訂單逾時判斷（改為單一到期時間）；第二輪修正補償釋放的歸屬範圍、逾時暫扣被覆寫的語意、`ConfirmOrderHandler` 的驗證完整性、`Expired` 是否落地寫入等問題。以下 Decisions 已依此重新設計。

## Goals / Non-Goals

**Goals:**
- 定義 `Event`／`Venue`／`SeatMap`／`Seat`（座位樣板）／`EventSeat`（活動專屬座位庫存，共 8 個 Entity）／`TicketType`／`Order`／`OrderItem` 的 Domain 規則與狀態機
- 座位「暫扣（Hold）→ 逾時釋放 / 售出」的不變條件可被單元測試驗證，且不依賴真實時鐘、資料庫
- 明確劃分「Domain 能自行判斷的規則」與「需要查詢多個 Entity 的 Application 協調邏輯」
- 明確劃分「Domain 拋領域例外」與「Application 對外回傳 `Result<T>`」的邊界，避免實作時各自發明
- Application 層的補償/釋放操作 MUST 限定在自己持有的座位範圍內，不得誤動其他訂單的鎖定

**Non-Goals:**
- EF Core 持久化、Migration、資料庫層級的並行控制（樂觀鎖／悲觀鎖）——本階段只決定「方向」，不實作
- 背景排程（實際定期釋放逾時座位的 Job）——本階段先以「讀取時判讀」代替，不主動寫回逾時清空
- Controller / API 介面、付款串接、主辦方後台權限
- 座位圖的視覺化佈局資料（僅需分區代碼＋座位編號，不含座標）
- 買家身分（`Order` 不含 `BuyerId` 或任何使用者關聯）
- 暫扣續命（Extend Hold，例如付款頁「幫我多留 5 分鐘」）
- 多幣別 / `Money` Value Object（票價先用 `decimal`）

## Decisions

**1. 庫存歸屬：`EventSeat` 是每場活動專屬的可售座位庫存，鎖定/售出狀態掛在 `EventSeat` 上，不掛在共用的 `Seat` 樣板上**
- `Seat` 只描述場地座位圖的物理樣板（分區代碼、座位編號），可被多場活動的座位圖重複使用
- 建立活動並指定使用某個 `SeatMap` 時，系統為該座位圖中每個 `Seat` 建立一筆對應的 `EventSeat`（鍵為 `EventId` + `SeatId`），鎖定/售出狀態、暫扣欄位都存在 `EventSeat` 上
- 理由：同一場地座位圖若被多場活動使用，若狀態掛在 `Seat` 本身，會造成庫存跨活動共用（A 場賣掉的位子 B 場也不能賣），這是售票系統不可接受的錯誤

**2. `EventSeat` 唯一性與聚合邊界：獨立 Entity，鍵為 `EventId` + `SeatId`，不是 `Event` 的子集合**
- 同一活動內，每個座位樣板 MUST 最多只對應一筆 `EventSeat`；此唯一性由「建立活動時依座位圖逐一建立 `EventSeat`」的建構流程本身保證，不是事後去重
- `EventSeat` 是獨立於 `Event` 聚合的 Entity，鎖定/釋放/售出操作直接對單筆 `EventSeat` 進行，不透過 `Event` 載入整份座位集合再操作；`Event` 只在建立當下扮演「產生一批 `EventSeat`」的工廠角色，之後兩者運作上互不依賴
- 理由：若 `EventSeat` 被塞進 `Event` 聚合（例如 `Event.Seats` 集合），鎖 2 個座位就得載入整場活動的座位圖，這在座位數量大的場館下是不必要的效能與並行瓶頸；本階段沒有 DB 也感受不到，但先在設計上定調，避免之後接 Infrastructure 時把它做成聚合子集合

**3. 不獨立成 `Reservation`／`SeatHold` Entity，理由不是「Entity 數量少」**
- `EventSeat` 直接帶 `HeldByOrderId`、`HeldUntilUtc`、`SoldByOrderId` 欄位，不另外拆一個歷史記錄 Entity
- 理由（修正版）：因為同一 `EventSeat` **同時最多只有一筆有效鎖定**，不需要保留鎖定歷史，所以欄位直接放在 `EventSeat` 上就足夠；這與「Entity 數量少於 5 個可簡化」無關（Goals 已列 8 個 Entity），先前引用該條 CLAUDE.md 規則是誤用，予以更正
- 若之後出現「需查詢座位曾被誰搶過幾次」的需求，再抽出獨立的鎖定歷史 Entity

**4. 狀態不是純粹由時間推導，`Sold` 是明確標記；`Held`／`Available` 才由時間推導**
- `EventSeat` 欄位：`SoldByOrderId`（nullable，一旦設定即永久生效，不受時間影響）、`HeldByOrderId`（nullable）、`HeldUntilUtc`（nullable）
- 狀態判斷邏輯（只透過方法存取，禁止外部直接讀欄位判斷可售性）：
  | 狀態 | 判斷條件 |
  |---|---|
  | Sold | `SoldByOrderId != null` |
  | Held | `SoldByOrderId == null && HeldByOrderId != null && now < HeldUntilUtc` |
  | Available | 其餘情況（含 `HeldByOrderId != null` 但 `now >= HeldUntilUtc` 的逾時情況） |
- 對外只暴露三個計算方法：`EventSeat.GetStatus(DateTime now)`、`EventSeat.IsAvailableForHold(DateTime now)`、`EventSeat.IsHeldBy(Guid orderId, DateTime now)`（判斷「未售出 且 `HeldByOrderId == orderId` 且 `now < HeldUntilUtc`」）；不暴露 `HeldByOrderId`／`HeldUntilUtc`／`SoldByOrderId` 等可直接讀取判斷的原始欄位存取捷徑。任何呼叫端（包含 Application 層的 Handler）需要判斷「這個座位現在是不是由某筆訂單合法持有」，一律呼叫 `IsHeldBy`，不得自行比較欄位

**5. `Hold()` 對逾時暫扣採「覆寫」，這是明確寫入而非讀取時寫回**
- `EventSeat.Hold(orderId, heldUntilUtc, now)` 呼叫時，若目前狀態依 `GetStatus(now)` 判斷為 Available（含「有 `HeldByOrderId` 但已逾時」的情況），MUST 直接以新的 `orderId`／`heldUntilUtc` 覆寫原本的 `HeldByOrderId`／`HeldUntilUtc`，無論原本是否為別的訂單留下的過期資料
- 理由：這是呼叫端主動觸發、有明確業務目的（鎖定座位）的寫入操作，與 Risks 中「讀取時判讀、不主動寫回」描述的「背景/被動清除」是不同的事——後者指的是沒有人呼叫 `Hold` 時，系統不會自己跑去清空過期欄位；一旦有人呼叫 `Hold`，欄位當然要被正常寫入

**6. 座位釋放限定持有者：`ReleaseHold(orderId)`，非持有者呼叫視為無操作，不拋例外**
- 方法簽章為 `EventSeat.ReleaseHold(Guid orderId)`：
  - 若目前 `SoldByOrderId != null`（已售出）→ MUST 拋領域例外（售出是不可逆的終態，任何釋放嘗試都是誤用）
  - 若目前 `HeldByOrderId != orderId`（座位不是由這個訂單持有——可能本來就是 Available，也可能已被其他訂單在逾時後重新鎖定）→ 視為無操作（no-op），不改變任何欄位、不拋例外
  - 若目前 `HeldByOrderId == orderId` → 清除 `HeldByOrderId`／`HeldUntilUtc`，轉為 Available
- 理由：先前版本的 `Release()` 無條件釋放，會讓 `CreateOrderHandler` 的補償邏輯、`CancelOrderHandler` 的釋放邏輯有機會釋放到「已被其他訂單合法搶走」的座位（例如：訂單 A 逾時後座位被訂單 B 重新鎖定，此時 A 才執行取消，若無條件釋放會把 B 的鎖定也清掉）。用 `orderId` 限定持有者、非持有者靜默略過，是唯一能同時滿足「補償正確」與「取消/逾時釋放不誤傷他人」的做法。不拋例外是因為「座位已被別人拿走」對呼叫端（Cancel/Expire 流程）而言是正常、可預期的競態結果，不是呼叫端的錯誤用法，不需要用例外中斷流程

**7. 時間傳遞：Domain 方法直接接收 `DateTime now` 參數，`IDateTimeProvider` 只存在於 Application**
- `IDateTimeProvider` 介面定義在 `Domain`（符合 CLAUDE.md「跨層介面定義於 Domain」），但 **`EventSeat`／`Order` 等 Entity 的方法一律只接收 `DateTime now` 參數，Entity 內部不持有、不注入 `IDateTimeProvider`**，維持純函式、方便測試
- Application 層的 Handler 注入 `IDateTimeProvider`，取得目前時間後再傳入 Domain 方法呼叫

**8. 訂單只有一個到期時間，不逐座位判斷逾時**
- `Order` 建立時記錄單一 `HeldUntilUtc`（等於建立當下由 `CreateOrderHandler` 決定的暫扣政策時長，例如 10 分鐘，套用到這筆訂單的所有 `EventSeat`）
- 判斷訂單是否逾時只比較 `now` 與 `Order.HeldUntilUtc` 這一個時間點，不逐一檢查訂單內每個 `EventSeat` 的暫扣狀態
- 理由：先前版本「確認訂單看任一座位逾時、查詢訂單看全部座位逾時」是兩套矛盾的規則，且逐座位判斷會出現「一顆過期、一顆還 Held」的中間態；讓 `Order` 自己持有唯一的到期時間可以避免這個問題。建立訂單時，每個 `EventSeat.Hold()` 呼叫都傳入與 `Order.HeldUntilUtc` 相同的到期時間，保持一致

**9. `Order` 的 `Expired` 是查詢時推導，不落地寫入 `Status` 欄位**
- `Order` 內部持久化的 `Status` 欄位只會被明確設為三種值之一：`Pending`／`Confirmed`／`Cancelled`，**永遠不會有程式碼把 `Status` 直接寫成 `Expired`**
- `Order.GetStatus(DateTime now)` 是計算方法：若內部 `Status == Pending` 且 `now >= HeldUntilUtc`，回傳 `Expired`（供查詢/顯示使用）；其餘情況原樣回傳內部 `Status`
- 理由：與 Decision 4（`EventSeat` 的狀態透過 `GetStatus` 計算、不落地存成欄位）以及 Risks 中「讀取時判讀、不主動寫回」的原則一致——「逾時」永遠是觀察者在某個時間點的推導結果，不是系統主動幫你改的持久狀態。若之後某個操作要把一筆已逾時的 `Pending` 訂單「處理掉」，它的落地結果是 `Cancelled`（見 Decision 10），而不是新增一個會被寫入的 `Expired` 狀態

**10. 沒有獨立的「逾時釋放」Handler，`CancelOrderHandler` 同時處理主動取消與逾時清理**
- `CancelOrderHandler` 接受任何內部 `Status == Pending` 的訂單，不論呼叫當下 `Order.GetStatus(now)` 是 `Pending` 還是 `Expired`，一律：對訂單內每個 `OrderItem` 對應的 `EventSeat` 呼叫 `ReleaseHold(order.Id)`（依 Decision 6，只釋放仍歸屬此訂單的座位），並將訂單 `Status` 設為 `Cancelled`
- 已 `Confirmed` 的訂單 MUST 被拒絕（不論是否逾時，`Confirmed` 是終態之一，逾時語意只對 `Pending` 有意義）
- 理由：使用者主動取消、與系統/使用者發現訂單已逾時想清掉它，兩者最終要做的事完全一樣（釋放仍持有的座位、把訂單標記為終態），沒有必要另開一個 `ExpireOrderHandler` 重複這段邏輯；呼叫端要不要在 UI 上區分「你取消了」跟「你逾時了」，屬於後續 WebApi/前端的呈現邏輯，不影響這裡的 Domain/Application 行為

**11. `ConfirmOrderHandler` 必須在變更任何座位前，完整驗證訂單與座位的歸屬一致性**
- 執行任何 `EventSeat.ConfirmSold` 之前，MUST 依序驗證：
  1. `Order.Status == Pending`（非 `Confirmed`/`Cancelled`）
  2. `now < Order.HeldUntilUtc`（訂單本身未逾時，依 Decision 8/9）
  3. 對訂單內每一筆 `OrderItem`，呼叫 `EventSeat.IsHeldBy(Order.Id, now)` 確認該座位目前確實由本訂單合法持有（未售出、鎖定訂單編號相符、未逾時）——**不得**直接比較 `EventSeat.HeldByOrderId` 等原始欄位，這樣會違反 Decision 4「狀態只能透過計算方法存取」的規則
  4. 每一筆 `OrderItem.EventSeatId` 都必須能成功解析到一筆存在的 `EventSeat`（防呆用，避免資料輸入錯置指向不存在的座位）；由於本階段沒有第二份外部座位清單可比對，這裡不檢查「多餘」，只檢查「每筆都解析得到」
- 只有全部驗證通過，才對每筆 `EventSeat` 呼叫 `ConfirmSold(Order.Id, now)`、並將 `Order.Status` 設為 `Confirmed`；任一驗證失敗，MUST 不變更任何 `EventSeat` 或 `Order` 狀態，回傳失敗的 `Result`
- 理由：先前版本只描述「確認訂單→座位轉 Sold」，沒有規定 Handler 要先核對「這些座位真的還是這筆訂單鎖定的」；沒有這層檢查，理論上可能出現輸入資料錯置導致「確認 A 訂單卻把 B 訂單持有的座位標記售出」的錯誤。這一步驗證屬於跨 Entity 的一致性檢查，只能放在 Application 層，`EventSeat.ConfirmSold` 本身仍保留自己的單筆守衛（見 seat-reservation spec）作為第二層防護

**12. 業務失敗回傳契約：Domain 拋領域例外守衛不變條件；Application 邊界一律轉譯為 `Result<T>`**
- `EventSeat`／`Order` 的守衛方法（例如「座位已被鎖定」「Sold 座位不可釋放」「已確認的訂單不可取消」）在違反不變條件時拋出具名的領域例外
- `CreateOrderHandler` / `ConfirmOrderHandler` / `CancelOrderHandler` 對外一律回傳 `Result<T>`（成功值或錯誤碼/訊息），內部攔截 Domain 拋出的領域例外並轉譯為失敗的 `Result`，不讓例外穿透到呼叫端做流程控制
- 理由：符合 CLAUDE.md「可預期的業務失敗優先用 Result 型別，而非以例外控制流程」。這條界線純粹是**分層**（Domain 用例外守衛自己的不變條件；Application 是對外的邊界，一律轉成 Result），不是依「這是不是程式錯誤」去分類——「座位已被鎖定」在 Domain 方法的呼叫合約上就是違反前置條件（呼叫前本該檢查），但站在 Application/呼叫端的角度它是完全可預期的業務結果，兩種描述並不衝突，先前版本用「程式錯誤 vs 業務失敗」來解釋兩層的差異是多餘且會讓人誤解的說法，予以移除

**13. Application 協調邏輯使用 `Handler` 命名，不用 `Service`**
- 依 CLAUDE.md 架構骨架「Application：Use case 邏輯（Handler）」，將座位鎖定＋訂單協調拆成三個 use case Handler：`CreateOrderHandler`、`ConfirmOrderHandler`、`CancelOrderHandler`（涵蓋 Decision 10 的逾時清理）
- `EventSeat` 自己只負責單一座位的狀態轉換規則與不變條件（例如：已售出的座位不能再被鎖定或釋放）

**14. 先產生 `OrderId`，再逐一鎖定座位，最後才組成 `Order`；補償釋放限定 `orderId`**
- `CreateOrderHandler` 流程：`orderId = Guid.NewGuid()` → 決定 `heldUntilUtc`（now + 暫扣時長政策）→ 依序對所選 `EventSeat` 呼叫 `Hold(orderId, heldUntilUtc, now)` → 全部成功才建立 `Order` 聚合＋`OrderItem`（快照票價，`OrderItem.EventSeatId` 指向對應的 `EventSeat`，不是 `Seat`）→ 任一 `Hold` 失敗，對本次已成功 `Hold` 的 `EventSeat` 呼叫 `ReleaseHold(orderId)`（依 Decision 6 限定持有者）復原，不建立 `Order`
- 理由：`EventSeat.Hold` 需要 `OrderId` 才能記錄鎖定歸屬，但 `Order` 要等座位全部鎖定成功才能建立，因此 `OrderId` 必須先產生；補償釋放限定 `orderId` 是因為即使理論上同一次建立流程內 `orderId` 皆相同、不會誤釋放，方法簽章仍要求 `orderId`，避免其他呼叫路徑（例如日後的重試邏輯）誤用成無條件釋放
- 本階段「原子性」的範圍：僅為 **Handler 內的操作順序與失敗復原**（記憶體內、單一執行緒的循序呼叫），**不是資料庫交易**；多執行緒/多請求同時搶同一座位的真實並行安全性，本階段無法驗證，見 Risks 與 Open Questions

**15. `OrderItem` 建立時快照票價**
- `OrderItem` 建立時複製當下 `TicketType.Price` 到自己的 `UnitPrice` 欄位，之後 `TicketType` 改價不影響已建立的 `OrderItem`

**16. 票價先用 `decimal`，不建立 `Money` Value Object**
- 理由：目前無多幣別需求，依 CLAUDE.md「VO 非必要不用，出現重複驗證邏輯才導入」原則，先簡化

## Risks / Trade-offs

- **[風險]** 「讀取時判讀」代表沒有主動流程觸發釋放或清除欄位，`EventSeat`／`Order` 在沒有人呼叫 `GetStatus` 的情況下，原始欄位會一直停留在逾時前的狀態 → **緩解**：所有判斷可售性/訂單狀態的程式碼一律走 `GetStatus(now)` / `IsAvailableForHold(now)`，不得直接讀欄位；`Hold()` 這類明確寫入操作可以覆寫過期資料（Decision 5），但這不等於系統會主動清除；正式的主動釋放/清除機制在後續 Infrastructure change 補上
- **[風險]** 本階段沒有資料庫，無法驗證「兩個並行請求同時鎖定同一 `EventSeat`」的真實並行安全性，Domain 層的不變條件（拋例外）只在單一執行緒的單元測試下成立 → **緩解**：方向先定為 Infrastructure 實作時搭配樂觀鎖（`RowVersion`），搶位衝突視為可預期的業務失敗（回傳 `Result` 失敗，前端可提示重試/換位），不上悲觀鎖；明確記錄在 Open Questions，並於 Infrastructure change 補上對應整合測試（Testcontainers）
- **[風險]** `CreateOrderHandler` 的「原子性」只是記憶體內操作順序保證，不是資料庫交易 → **緩解**：Infrastructure 接線時需將整個鎖定＋建立訂單流程包進資料庫交易，本階段先在 design 中明確承認此限制
- **[風險]** `EventSeat` 若被誤做成 `Event` 聚合的子集合（例如 `Event.Seats`），未來接 Infrastructure 時鎖 1-2 個座位會連帶載入整場活動的座位圖，造成不必要的效能/並行瓶頸 → **緩解**：Decision 2 已明訂 `EventSeat` 為獨立 Entity，操作時以單筆或依需要的少量 `EventSeat` 為單位存取，`Event` 只在建立當下作為工廠使用
- **[風險]** 暫扣時長寫死在 `CreateOrderHandler` 的政策常數中，尚無可設定化機制 → **緩解**：本階段先接受寫死的預設值（如 10 分鐘），需要可設定時再抽出設定物件，不在本階段先做

## Open Questions

- 逾時 `EventSeat` 的主動釋放/清除機制（背景 Job／訊息佇列／排程）要在哪個後續 change 處理？本 change 只保證「讀取時判讀」與「`Hold()` 時覆寫」正確
- 資料庫層級的並行鎖定策略：方向已定為樂觀鎖（`RowVersion`），實際欄位設計與衝突重試邏輯待 Infrastructure change 時決定
- 暫扣續命（Extend Hold）若之後需要，`ReleaseHold → Hold` 兩步中間會有被其他訂單搶位的競態，需要設計一個原子的 `Extend` 操作，本階段不處理
- 票價是否需要多幣別／`Money` Value Object，待有實際需求時再評估
