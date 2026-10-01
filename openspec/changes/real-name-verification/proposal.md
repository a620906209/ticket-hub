## Why

`docs/project-scope.md` 第 2 節 Could 清單中，尚未實作的功能項目是「實名制驗證（姓名、身分證末四碼或手機號）」。目前任何人拿到 QR Code 就能入場，主辦方無法限制票券轉賣。本次讓主辦方可以把個別活動設成「需實名」：買家先登記實名，現場核銷時比對證件後才放行。這也是專案第一次在資料庫存放身分證相關個資，用來落實 CLAUDE.md 對個資遮蔽與 log 不記敏感資訊的要求。

## What Changes

- **會員實名登記**：會員可登記一次實名資料（真實姓名＋身分證末四碼），登記後本人不可修改或清除。
  - 用途是防止「買完票後把實名改成接手者」繞過實名轉賣。
  - 填錯的更正屬客服流程，依 `docs/project-scope.md` Won't 清單不在範疇內。
- **個人資料查詢**：查詢自己的會員資料時多回傳是否已登記實名。已登記時回傳姓名，身分證末四碼只回傳遮蔽後的值，不回傳完整四碼。
- **活動層級「需實名」開關**：建立活動時可選擇是否需實名，預設否。
  - 建立後不可變更，比照既有 `MaxTicketsPerOrder`，系統沒有編輯活動端點。
  - 公開活動列表 API 與後台活動列表 API 都回傳此設定；畫面上的標示只在買家活動詳情頁與後台活動列表頁（買家活動列表頁不顯示，避免列表資訊過載）。
- **下單與排隊閘門**：需實名的活動，買家未登記實名時：
  - 建立訂單 MUST 被拒絕，回應可辨識的錯誤類型 `RealNameRequired`，讓前端引導買家去登記。
  - 加入購票排隊同樣被拒絕，避免排了隊才發現不能下單。
  - 建立訂單是最終把關點。
- **核銷改為兩步驟（僅需實名活動）**：
  - 需實名活動的票券，核銷請求必須明確帶上「已確認持票人身分」，否則回應可辨識的錯誤類型 `HolderVerificationRequired`，票券狀態不變。
  - 新增唯讀的「查詢持票人」端點，回傳持票人姓名與完整身分證末四碼，供現場比對證件。整個系統只有這個端點會回傳完整末四碼。
  - 非實名活動的核銷行為完全不變。
- **後台核銷頁**：掃碼或手動輸入後，若收到 `HolderVerificationRequired`，改為顯示持票人姓名與末四碼，操作人員按「確認核銷」才送出。比對不符時可以放棄，票券維持未核銷。
- **後台建立活動表單**：新增「需實名」勾選。
- **買家端**：
  - 新增「實名資料」頁面，可查看與登記。登記前顯示「登記後無法修改」的確認對話框。
  - 活動頁標示「本活動需實名」。未登記實名者在活動頁看到提示與登記入口，下單或排隊收到 `RealNameRequired` 時也顯示同樣的引導。
- **個資保護**：真實姓名與身分證末四碼 MUST NOT 出現在任何日誌的訊息文字或結構化屬性中，比照 `observability` 能力的既有規則。
- **本次不做**：
  - 同一實名跨帳號／跨訂單限購。
  - 逐張票填寫持票人實名。實名綁會員帳號，一筆訂單的所有票券持票人都是買家本人。
  - 實名資料更正。
  - 對接外部身分驗證服務：只驗證格式，不驗證真偽。

## Capabilities

### New Capabilities
- `real-name-verification`：會員實名資料的登記（一次性、不可修改）、格式驗證、遮蔽規則與日誌禁止記錄規則。

### Modified Capabilities
- `member-management`：「查詢自己的會員資料」回傳內容新增實名登記狀態（姓名、遮蔽後末四碼）；「更新自己的會員資料」明確不得變更實名欄位。
- `event-management`：建立活動可指定「需實名」，建立後不可變更；後台活動列表回傳此設定。
- `ticket-purchase`：買家活動列表回傳「需實名」設定；需實名活動建立訂單前須已登記實名。
- `purchase-queue`：需實名活動加入排隊前須已登記實名。
- `ticket-redemption`：需實名活動核銷須帶持票人已確認旗標；新增查詢持票人端點（同 Organizer scoping 規則）。
- `admin-web-ui`：建立活動表單「需實名」勾選；核銷頁兩步驟確認流程。
- `buyer-web-ui`：實名資料頁、活動頁實名標示與未登記引導、下單／排隊收到 `RealNameRequired` 的處理。

## Impact

- **Domain**：
  - `Member` 新增 `RealName`、`NationalIdLast4`（`private set`）與一次性登記方法。
  - `Event` 新增 `IsRealNameRequired`，只在建構時指定。
  - 新增 `IMemberRealNameRepository`（一次性條件式登記、讀取實名）。
  - `IOrderRepository` 的核銷歸屬查詢擴充為同時回傳活動是否需實名與買家 Id。
- **Application**：
  - 新增實名登記 Handler 與驗證器。
  - `GetMyProfile`、`CreateEvent`、`GetEvents`／`GetAdminEvents` DTO 擴充。
  - `OrderService` 建立訂單、加入排隊 Handler 加入實名閘門。
  - `RedeemTicketHandler` 加入持票人確認閘門。
  - 新增查詢持票人 Handler。
  - 新增 `ErrorType.RealNameRequired`、`ErrorType.HolderVerificationRequired`。
- **Infrastructure**：EF 設定與 migration：`Members` 新增兩個可為 null 的欄位，加「同時為 null 或同時有值」約束；`Events` 新增 bool 欄位，預設 false。
- **WebApi**：
  - `PUT /api/members/me/real-name`（新）。
  - `GET /api/admin/tickets/{id}/holder`（新）。
  - `PATCH /api/admin/tickets/{id}/redeem` request body 新增可選欄位。
  - `ResultExtensions` 對應兩個新的 `ErrorType`。
- **快取**：`GET /api/events` 已有 Redis 快取（`query-caching`）。回應新增欄位後，舊格式的快取項目會被讀成 false，列表標示可能在快取 TTL 內落後資料庫。下單、排隊、核銷三個後端判斷一律讀資料庫，不受快取影響。見 design.md 決策 6「部署切換期間的保證範圍」。
- **前端**：
  - 新增實名資料頁與 API 呼叫。
  - 修改 `EventDetailPage`、`EventCreatePage`、`EventListPage`（後台）、`RedemptionScannerPage`。
  - 會員選單新增入口。
- **測試**：Domain／Application 單元測試、WebApi 整合測試（含 log 結構化屬性斷言、Organizer scoping、並發一次性登記）、前端元件測試。
- **文件**：`docs/project-scope.md` 第 2 節 Could 項目與第 8 節剩餘項目在歸檔時更新。
