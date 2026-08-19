# 售票系統（Ticketing System）Project Scope

> **文件性質**：專案初期宏觀需求盤點（`docs/project-scope.md`），純範疇說明文件，非可執行任務清單。
> **定位**：確立「要做什麼、先做什麼、不做什麼」；個別功能後續透過 `/openspec-propose` 展開為 `tasks.md`。
> **主軸情境**：情境 B（高併發防黃牛），情境 A（多租戶 SaaS）僅保留架構擴充性。
> **本文件僅產生一次**，若後續需求變動請直接編輯本檔案，不重跑此流程。

---

## 1. 產品定位與範疇

**核心商業目標（優先順序）**
1. 抗高併發與庫存一致性（不超賣、開賣不當機）
2. 防黃牛 / 防機器人搶票（Rate limiting、排隊機制）
3. 售票效率（主辦方自助上架、買家流暢結帳）

**核心角色**

| 角色 | 關鍵需求 |
|---|---|
| 買家 | 排隊狀態透明、限時保留鎖定、選票不卡頓 |
| 主辦方 | 建立活動/票種、設定開賣時間、查看銷售報表 |
| 平台管理員 | 簡化版異常訂單監控 |

**系統定位**：對外設計為多租戶（Organizer 為獨立 Entity），實作範圍僅需支援單一主辦方即可跑通。

**商業模式**：買家每張票服務費示意，僅作系統設計依據，非真實金流，付款走 Mock Gateway。

**既有系統整合**：已有**會員系統**，售票系統需共用登入/會員資料（帳號 ID、Email、顯示名稱）。

---

## 2. 功能地圖與優先級（MoSCoW）

**Must（本次範疇核心，Phase 1）**
- 活動 / 票種建立與上架
- 座位（或票種）選擇與鎖定（**悲觀鎖，資料庫交易鎖 + 固定順序取鎖避免死鎖**，套用於 `Seat`）——**已於既有 `seat-reservation` spec 完成實作，決策維持悲觀鎖現況，不改為樂觀鎖**
- 訂單建立與結帳流程（Mock 金流，`IPaymentGateway` 介面 + 假實作展示 DIP）——既有 `ticket-purchase` 確認訂單端點目前為「不接受付款資訊、呼叫即成功」的簡化版，**決策讓既有實作對齊本規劃**，待開 OpenSpec 提案補上 `IPaymentGateway` 抽象化
- 電子票券產出（QR Code，內容為 HMAC 簽章過的 Ticket ID，防偽造）
- 核銷 API（`PATCH /tickets/{id}/redeem`，需處理併發核銷防重複、狀態機驗證）
- 與既有會員系統整合登入
- 前端 RWD（手機瀏覽器支援）

**Should（提升體驗，Phase 2，非上線阻塞項）**
- 主辦方銷售報表（即時或準即時，僅支援活動進行中/結束後查詢，不含歷史趨勢分析）
- Rate limiting / 基礎排隊機制（與「API 防搶票機器人」共用同一套機制）
- Email 通知（`INotificationService` 介面，票券產出後通知買家）
- 登入 Rate limiting（防暴力破解）
- 監控（Serilog + Seq，本地容器化，對應 CLAUDE.md 結構化 log 規範）**［待確認］**

**Could（技術深化，Phase 3，視剩餘時間精力擴充，暫不預排優先順序）**
- Redis 分散式鎖 / Queue 排隊室（座位鎖定機制進階版）
- 多租戶主辦方管理介面（審核、切換）
- 實名制驗證（姓名、身分證末四碼或手機號）
- 動態驗證碼（CAPTCHA）
- 現場核銷掃碼前端頁面
- 快取層（Redis 等，一般查詢 API 用）

**Won't（明確排除，避免範疇擴散）**
- 多元金流與自動分潤結算
- 客服爭議處理流程
- 全站主辦方治理（下架、黑名單）
- 第三方金流 sandbox（綠界/藍新）串接
- 簡訊通知
- 第三方憑證/簽章服務（QR Code 走本地 HMAC 簽章，非第三方簽章）
- 電子發票 / 正式法定電子憑證
- 銷售歷史趨勢分析（BI 範疇）

---

## 3. 資料與實體

**核心實體階層**

```
Organizer → Event → TicketType（含 RequiresSeat 布林欄位）
                          ↓（RequiresSeat = true 時才存在）
                        Seat
Order → OrderItem → Ticket（電子票券，核銷用）
```

- 指定座位與純計數票種兩種模式皆支援，透過 `TicketType.RequiresSeat` 開關切換
- 鎖定邏輯採**悲觀鎖**（既有 `seat-reservation` 實作：資料庫交易層鎖定，多座位鎖定依固定順序取鎖避免死鎖），套用在 `Seat` 上；`TicketType.AvailableQuantity` 純計數模式的鎖定機制待該功能實作時另行決定是否共用同一套

**會員資料整合**
- 共用既有會員系統：帳號 ID、Email、顯示名稱
- 售票系統自行存放：收件資訊（取票方式/寄送地址）
- 實名制欄位（姓名、身分證末四碼或手機號）列為 Could，不進 Must 範疇

**生命週期狀態**

| 實體 | 狀態機 |
|---|---|
| Order | `Pending`（待付款）→ `Paid`（已付款，規劃目標命名）→ `Cancelled`（已取消，含逾時未付款自動轉）→ `Refunded`（已退款，若做）——**既有實作目前為 `Confirmed`（無 `Paid`）且無 `Refunded`，決策讓既有實作對齊本規劃，待 `IPaymentGateway` 提案時一併調整命名** |
| Ticket | `Issued`（已發放）→ `Redeemed`（已核銷）→ `Voided`（作廢，對應訂單取消） |
| Seat | `Available` → `Locked` → `Sold`；`Locked` 逾時須自動釋放回 `Available`（與 Order 狀態連動，具體實作方式待 OpenSpec 提案階段決定） |

**資料量級**
- 上限透過設定檔控制（`IOptions<EventCapacityOptions>`，對應 DI Singleton），不寫死於 Domain 層
- 預設值以單場 **2000 座位 / 20 票種**作為效能驗證基準，用途是定義負載測試情境，非強制驗證規則
- 此量級不需分區/分片（sharding），單一 PostgreSQL table + 適當複合索引（如 `SeatId + EventId`）即可支撐
- Domain 層是否加入建立 Event 時的容量上限驗證規則：**［待確認，OpenSpec 提案階段決定］**

**資料保留與歷史查詢**
- 訂單資料不做自動清除，僅邏輯狀態變更（不刪除）
- 銷售報表僅支援活動進行中/結束後的即時查詢，歷史趨勢分析列入 Won't

---

## 4. 外部整合

| 項目 | 決策 |
|---|---|
| 金流 | 完全自建 Mock Gateway（假成功/假失敗開關），設計為 `IPaymentGateway` 介面 + 假實作展示依賴反轉；第三方 sandbox（綠界/藍新）列入 Won't |
| 通知 | 站內查看為 Must；Email 通知（`INotificationService` 介面）為 Should；簡訊列入 Won't |
| 電子票券 | 本地產生 QR Code（QRCoder 套件），內容為 HMAC 簽章過的 Ticket ID 防偽造，不接第三方憑證/簽章服務，列為 Must |
| 核銷 | 本次範疇僅做 Ticket 狀態切換 API（`PATCH /tickets/{id}/redeem`），不含現場掃碼 App/頁面（掃碼前端頁面列入 Could） |
| 部署 | 暫定本機 Docker Compose 展示，視情況加雲端環境（Azure App Service / Render 免費層）供履歷連結 **［待確認］** |
| 監控 | 暫定 Serilog + Seq（本地容器化） **［待確認］** |

---

## 5. 非功能需求

**效能與可用性**
- 不設正式 uptime SLA（如 99.9%），以「開賣尖峰不當機、不超賣」作為可用性驗證標準
- 技術驗證指標：模擬 **500 併發**搶購同一場次 **50 張票**，**0% 超賣**，**P95 回應時間 < 500ms**（k6/Locust 實測）
- 一般（非搶購尖峰）情境下，同時在線使用者量級暫定 **100–300 人**；此量級下一般查詢 API（活動列表、票種查詢）不需額外快取層，EF Core + PostgreSQL index 即可支撐；快取層（Redis 等）列入 Could

**安全**
- 除 CLAUDE.md 既有安全強制規則外：
  - 登入 Rate limiting 防暴力破解：Should
  - API 防搶票機器人：與 Should 的「基礎排隊機制」共用同一套，不另建防爬蟲系統
  - 進階行為驗證（CAPTCHA）：Could

**前端支援範圍**
- 支援 RWD（手機瀏覽器）：Must
- 瀏覽器版本下限：Chrome / Safari / Edge 最近 2 個大版本，不相容過舊瀏覽器

**合規/法規**
- 電子發票、正式法定電子憑證：Won't
- 本專案不涉及電子發票/法定憑證，僅示範訂單與票券生命週期

---

## 6. 技術脈絡對應

- 技術棧、分層規範、DI 生命週期等執行細節一律以 `CLAUDE.md` 為準，本文件不重複規範
- 座位鎖定機制的 Must/Could 分階段對應，於個別功能走 OpenSpec 提案時再展開技術設計

---

## 7. 里程碑

**目標完成時間**：無明確截止日

**開發階段順序**
- **Phase 1（Must）**：跑通核心流程 end-to-end——建立活動 → 選票/選座 → 下單 → Mock 付款 → 出票 → 核銷 API
- **Phase 2（Should）**：銷售報表、基礎排隊機制、登入 Rate limiting、Email 通知
- **Phase 3（Could）**：視剩餘時間精力擴充，優先順序待 Phase 1、2 完成後再決定

**中期可展示節點**：以 Phase 1 完成作為履歷/面試展示基準線——「建立活動 → 買家下單 → 核銷」主流程可跑通

**後續提案分包方式**：依 Must → Should → Could 順序逐一走 OpenSpec 提案，非按 Domain 切分

---

## 8. 待確認事項彙整

- Domain 層是否在建立 Event 時驗證票種數/座位數超過設定上限（見第 3 節）
- 部署環境是否加雲端平台展示（見第 4 節）
- 監控方案是否採用 Serilog + Seq（見第 4 節）
- Could 項目（Redis 分散式鎖、多租戶管理介面、CAPTCHA 等）的實作優先順序，待 Phase 1、2 完成後再決定（見第 7 節）

**已決策的現況對齊項目（2026-08-19 盤點後）**
- 座位鎖定機制：維持既有 `seat-reservation` 的**悲觀鎖**實作，本文件不再規劃改為樂觀鎖 RowVersion（原「兩套機制衝突」已解決）
- 訂單確認流程與狀態機命名：**決策讓既有實作對齊本規劃**（而非把規劃改成配合現況）——後續需另開 OpenSpec 提案，針對既有 `ticket-purchase` 確認訂單流程補上 `IPaymentGateway` 抽象化，並將 `OrderStatus` 對齊調整為含 `Paid` 命名；是否新增 `Refunded` 待該提案階段決定
- **既有實作調整順序（避免新功能重工）**：① 訂單確認流程 `IPaymentGateway` 化 + `OrderStatus` 命名對齊 → ② `TicketType.RequiresSeat` 開關 → ③ 全新電子票券（Ticket entity）+ 核銷 API。理由：Ticket 出票邏輯會掛在訂單付款成功事件上，且需要知道票種是否綁座位，故先調整地基再蓋新功能，三者各自獨立開 OpenSpec 提案
  - ① **已完成並歸檔**（2026-08-19，`openspec/changes/archive/2026-08-19-order-payment-gateway-alignment`）：`IPaymentGateway`/`MockPaymentGateway` 已實作，`OrderStatus.Confirmed` 已全面改名為 `OrderStatus.Paid`，`ticket-purchase`/`ticket-ordering`/`order-administration` 三份 spec 已同步更新；`Refunded` 狀態未加入（不在本次範疇）。下一步是②`TicketType.RequiresSeat` 開關

---

## 9. 現有進度確認

> 逐項列出清單容易與實際進度脫節，故僅記錄下方 Phase 1 Must 盤點快照（盤點日期見標題），之後仍以 `openspec/specs/`（archived proposal）與 codebase 現況為準，不逐一維護本節。

**Phase 1 Must 盤點快照（2026-08-19）**

| Must 項目 | 狀態 | 對應 spec / 程式碼 |
|---|---|---|
| 活動/票種建立與上架 | ✅ 已完成 | `event-catalog`、`event-management` |
| 座位選擇與鎖定 | ✅ 已完成（悲觀鎖，見第 8 節決策） | `seat-reservation` |
| 訂單建立與結帳流程 | ✅ 已完成，`IPaymentGateway` 抽象化已補上（見第 8 節） | `ticket-ordering`、`ticket-purchase` |
| 電子票券產出（QR Code + HMAC） | ❌ 未做，`Ticket` entity 不存在 | — |
| 核銷 API | ❌ 未做 | — |
| `TicketType.RequiresSeat` 開關 | ❌ 未做，目前僅支援綁座位模式 | `TicketType.cs` |
| 會員系統整合登入 | ✅ 已完成 | `authentication`、`member-management` |
| 前端 RWD | ✅ 已完成；買家「我的訂單」列表/明細仍待補查詢 API（原 `buyer-order-query` 提案已移除，需重新走 `/openspec-propose`） | `buyer-web-ui`、`admin-web-ui` |

- 專案骨架與技術棧設定請參照 `CLAUDE.md`
- 已完成 / 進行中功能請查閱 `openspec/` 下已核准（archived）的 proposal，或直接檢視 codebase 現有實作
- 對現況不確定時，先回報「目前確認到的狀態」，再詢問是否需要更新本文件
